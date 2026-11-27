using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed partial class VirtualSkyPipelineTests
{
    private static readonly JsonSerializerOptions EvidenceSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static CameraAgentIntegrationFixture Fixture => AssemblyHooks.Fixture;

    [TestMethod]
    public async Task StandaloneForcedTimeoutIncludesBoundedRuntimeStateAndSuccessDoesNotCollect()
    {
        using var scope = Fixture.CreateCameraAgentScope();
        var diagnostics = new VirtualSkyOutageDiagnostics(scope.ServiceProvider, Fixture.StorageRoot)
        {
            Stage = "forced timeout proof",
            CheckpointUtc = DateTimeOffset.UtcNow
        };
        var telemetry = scope.ServiceProvider.GetRequiredService<ICaptureTelemetryProvider>();
        await WaitUntilAsync(() => telemetry.Latest is { FrameStored: true }, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        var error = await Assert.ThrowsAsync<AssertFailedException>(() => WaitUntilAsync(
            () => false, TimeSpan.Zero, describeTimeout: diagnostics.Describe)).ConfigureAwait(false);
        foreach (var field in new[] { "checkpoint:", "capture loop/metering/admission:", "capture telemetry:", "fleet capture:", "ingress queues/bytes/oldest age:", "durable processing/publication:", "journal capture sequence/publication/metering:" })
        {
            StringAssert.Contains(error.Message, field, StringComparison.Ordinal);
        }
        StringAssert.Contains(error.Message, "CaptureSequence", StringComparison.Ordinal);
        Assert.IsFalse(error.Message.Contains("unavailable(", StringComparison.Ordinal), error.Message);
        Assert.IsTrue(error.Message.Length < VirtualSkyOutageDiagnostics.OutputLimit + 1024);
        Console.WriteLine(error.Message);
        await WaitUntilAsync(() => true, TimeSpan.Zero,
            describeTimeout: () => throw new InvalidOperationException("Success must not collect diagnostics.")).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("fleet capture")]
    [DataRow("capture telemetry")]
    [DataRow("durable processing/publication")]
    public async Task StandaloneHeldProductLockDoesNotDelayOriginalTimeout(string section)
    {
        using var scope = Fixture.CreateCameraAgentScope();
        var services = scope.ServiceProvider;
        object state = section switch
        {
            "fleet capture" => services.GetRequiredService<FleetRuntimeState>(),
            "capture telemetry" => services.GetRequiredService<ICaptureTelemetryProvider>(),
            _ => services.GetRequiredService<CaptureProcessingState>()
        };
        using var snapshotReturned = new ManualResetEventSlim();
        var diagnostics = new VirtualSkyOutageDiagnostics(services, Fixture.StorageRoot, target =>
        {
            if (ReferenceEquals(target, state)) snapshotReturned.Set();
        });
        var gate = state.GetType().GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(state);
        Assert.IsNotNull(gate, "The regression must hold the real product snapshot gate.");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait();
            }
        })
        { IsBackground = true };
        Task? assertion = null;
        holder.Start();
        try
        {
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(5)), "The product gate was not acquired.");
            var elapsed = Stopwatch.StartNew();
            assertion = Task.Run(() => WaitUntilAsync(() => false, TimeSpan.Zero, describeTimeout: diagnostics.Describe));
            // The collector has two seconds; allow scheduling slack without releasing the gate to unblock it.
            var error = await Assert.ThrowsAsync<AssertFailedException>(
                () => assertion.WaitAsync(TimeSpan.FromSeconds(2.75))).ConfigureAwait(false);
            Assert.IsFalse(release.IsSet, "The assertion must surface while the gate is still held.");
            Assert.IsTrue(holder.IsAlive);
            Console.WriteLine($"held snapshot={section}; getterReturned={snapshotReturned.IsSet}; assertionElapsedMs={elapsed.Elapsed.TotalMilliseconds:F0}\n{error.Message}");
            Assert.IsTrue(snapshotReturned.IsSet, "The snapshot getter must return before collection completes; no worker may remain blocked on the held gate.");
            StringAssert.Contains(error.Message, "Timed out waiting for the configured VirtualSky pipeline", StringComparison.Ordinal);
            StringAssert.Contains(error.Message, section + ": unavailable(timeout)", StringComparison.Ordinal);
        }
        finally
        {
            release.Set();
            Assert.IsTrue(holder.Join(TimeSpan.FromSeconds(5)), "The product gate holder did not stop.");
            Assert.IsTrue(snapshotReturned.Wait(TimeSpan.FromSeconds(5)), "Drain the snapshot getter even on the red path before disposing its completion signal.");
            if (assertion is not null)
            {
                // Drain the real helper after release, including on the red path, before disposing the fixture scope.
                try { await assertion.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (AssertFailedException) { }
            }
        }
    }

    private static async Task WriteRuntimeEvidenceAsync(
        RawIngressSnapshot ingress,
        CaptureProcessingSnapshot processing,
        int storedArtifactCount,
        int uploadUnfinishedAtDrainCheckpoint,
        IEnumerable<string> instruments,
        IEnumerable<string> activities,
        IReadOnlyDictionary<string, double> measurements)
    {
        var files = Directory.EnumerateFiles(Fixture.StorageRoot, "*", SearchOption.AllDirectories)
            .Where(static path => !Path.GetFileName(path).Contains(".tmp", StringComparison.Ordinal))
            .Select(TrySnapshotFile)
            .OfType<RuntimeFileSnapshot>()
            .ToArray();
        var evidence = new
        {
            SchemaVersion = "issue-61-configured-pipeline-runtime-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            StoredArtifactCount = storedArtifactCount,
            UploadUnfinishedAtDrainCheckpoint = uploadUnfinishedAtDrainCheckpoint,
            FilesystemSnapshot = new
            {
                FileCount = files.Length,
                TotalBytes = files.Sum(static file => file.Length),
                SqliteFileCount = files.Count(static file => file.Extension is ".db" or ".db-shm" or ".db-wal"),
                SqliteBytes = files.Where(static file => file.Extension is ".db" or ".db-shm" or ".db-wal")
                    .Sum(static file => file.Length)
            },
            OperationCounts = new
            {
                RawIngressCommits = measurements.GetValueOrDefault("camera_agent.ingress.committed"),
                RawIngressCommittedBytes = measurements.GetValueOrDefault("camera_agent.ingress.committed.bytes"),
                RawIngressSqliteTransactions = measurements.GetValueOrDefault("camera_agent.ingress.sqlite.transactions"),
                ProcessingOutputs = measurements.GetValueOrDefault("camera_agent.processing.outputs"),
                ProcessingOutputBytes = measurements.GetValueOrDefault("camera_agent.processing.output.bytes")
            },
            RawIngress = ingress,
            CaptureProcessing = processing,
            WarningPolicy = "All errors and all VirtualSky/environmental warnings are forbidden; known unrelated TestServer/fixture warnings are dispositioned outside this feature gate.",
            Instruments = instruments.Order(StringComparer.Ordinal).ToArray(),
            Activities = activities.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            Measurements = measurements.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal)
        };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            root = root.Parent;
        }
        var outputDirectory = Path.Combine(
            root?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found."),
            "TestResults", "issue-61", "runtime");
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "configured-pipeline.json"),
            JsonSerializer.Serialize(evidence, EvidenceSerializerOptions))
            .ConfigureAwait(false);
    }

    private static RuntimeFileSnapshot? TrySnapshotFile(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return new RuntimeFileSnapshot(file.Extension, file.Length);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record RuntimeFileSnapshot(string Extension, long Length);

    private static ushort ReadMono16(ReadOnlySpan<byte> pixels, int strideBytes, int x, int y)
        => System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(pixels[(y * strideBytes + x * 2)..]);

    private static void AssertLineageReachesRaw(
        ReconstructionDescriptor descriptor,
        IReadOnlyCollection<(StoredFrameReference Stored, ArtifactManifestParseResult Parsed)> manifests)
    {
        var byArtifactId = manifests.ToDictionary(
            static item => item.Parsed.Document!.Manifest!.Descriptor.Artifact.ArtifactId,
            static item => item.Parsed.Document!.Manifest!.Descriptor);
        var pending = new Queue<Guid>(descriptor.Artifact.SourceArtifactIds);
        var visited = new HashSet<Guid>();
        while (pending.TryDequeue(out var artifactId))
        {
            if (!visited.Add(artifactId) || !byArtifactId.TryGetValue(artifactId, out var source))
            {
                continue;
            }
            if (source.Artifact.Role == FrameArtifactRole.Raw)
            {
                return;
            }
            foreach (var parent in source.Artifact.SourceArtifactIds)
            {
                pending.Enqueue(parent);
            }
        }
        Assert.Fail("The configured preview lineage did not reach a durable raw artifact.");
    }

    /// <summary>
    /// Waits for a condition inside a stopwatch-measured budget. On timeout the failure names the
    /// exact condition and source line that did not become true, so the separate waits in this test
    /// are distinguishable instead of sharing one generic message.
    /// </summary>
    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        [CallerArgumentExpression(nameof(condition))] string? conditionExpression = null,
        [CallerLineNumber] int conditionLineNumber = 0,
        Func<string>? describeTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw CreateWaitTimeout(stopwatch.Elapsed, timeout, conditionLineNumber, conditionExpression, describeTimeout);
            }
            await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls until <paramref name="observe"/> yields an observation and returns that exact value, so
    /// callers assert on the state that satisfied the wait rather than on a later reread. The
    /// capture loop runs for the whole test, so a reread can already describe a different capture.
    /// </summary>
    private static async Task<T> WaitForObservationAsync<T>(
        Func<T?> observe,
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        [CallerArgumentExpression(nameof(observe))] string? observeExpression = null,
        [CallerLineNumber] int observeLineNumber = 0)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(observe);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            if (observe() is { } observed)
            {
                return observed;
            }
            if (stopwatch.Elapsed >= timeout)
            {
                throw CreateWaitTimeout(stopwatch.Elapsed, timeout, observeLineNumber, observeExpression);
            }
            await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private static AssertFailedException CreateWaitTimeout(
        TimeSpan elapsed,
        TimeSpan timeout,
        int lineNumber,
        string? expression,
        Func<string>? describeTimeout = null)
        => new(
            FormattableString.Invariant(
                $"Timed out waiting for the configured VirtualSky pipeline after ") +
            FormattableString.Invariant(
                $"{elapsed.TotalSeconds:F3} s of {timeout.TotalSeconds:F3} s ") +
            FormattableString.Invariant(
                $"at line {lineNumber}: {expression ?? "<unknown condition>"}")
            + (describeTimeout is null ? string.Empty : Environment.NewLine + describeTimeout()));

    /// One atomic view of the capture prefix at or below <paramref name="Watermark"/>, plus evidence of work
    /// beyond it. Every field comes from a single statement, so they all describe the same database state
    /// rather than a sequence of states a live producer moved through between reads.
    /// </summary>
    private sealed record CapturePrefixSnapshot(
        long Watermark,
        long InMotionLaneWork,
        long TerminalLaneWork,
        long TerminalCaptures,
        long RetryableNodes,
        long TerminalNodes,
        long SkippedNodes,
        long RetentionHolds,
        long RetriedLaneWork,
        long CommittedAtOrBelowWatermark,
        long AssignmentsBeyondWatermark,
        long CapturesBeyondWatermark,
        long UnmaterialisedBelowWatermark,
        long UnmaterialisedAtWatermark)
    {
        // Every member of this predicate has to be a state the prefix can still leave. A member that can enter a
        // state nothing exits makes the fence permanently unsatisfiable against a fixed watermark, which is the
        // same unsatisfiable-wait class the issue exists to remove. Walking each member against the schema:
        //   in motion, can settle: lane work in pending/leased/retry_wait; a capture reserved at the watermark
        //     whose row is mid-publication; nodes in RetryableFailure, whose rows are upserted on re-processing.
        //   terminal, cannot settle, so gated separately below and asserted rather than waited on: lane work in
        //     quarantined or abandoned, which only reach completed from leased; captures in quarantined or
        //     missing_evidence, which leave only through the repair path the raw ingress reconciler drives; nodes in TerminalFailure; and a
        //     reservation below the watermark with no capture row, which nothing ever deletes.
        //   legitimate and excluded entirely, for reasons that are not "never cleared": retention_hold is a
        //     derived flag, recomputed as 1 while any of required lane work is unfinished, a transient candidate
        //     source hold is unreleased, transient capture work is pending, or an unreleased row exists in
        //     processing_execution_input_pins. It is cleared by the same recomputation, including on the lane-work
        //     completion commit. It is excluded because a windowed execution for a capture beyond the watermark
        //     pins the earlier captures it consumes as inputs, so a prefix capture stays held until post-watermark
        //     work releases its pin. Gating on it would therefore re-impose the global idle demand. Also excluded:
        //     nodes in Skipped, a normal terminal outcome, and lane rows past their first attempt.
        // A terminal member ends the wait immediately. Without this a node stranded in RetryableFailure by
        // quarantined lane work would look like work in motion, burn the whole budget and report the wrong
        // member, while the terminal assertion that names the real defect never runs.
        internal bool PrefixHasTerminalDefect =>
            TerminalLaneWork != 0 ||
            TerminalCaptures != 0 ||
            TerminalNodes != 0 ||
            UnmaterialisedBelowWatermark != 0;

        internal bool PrefixInMotion =>
            !PrefixHasTerminalDefect &&
            (InMotionLaneWork != 0 ||
             UnmaterialisedAtWatermark != 0 ||
             RetryableNodes != 0);

        internal bool PrefixSettled =>
            PrefixHasTerminalDefect ||
            (!PrefixInMotion && CommittedAtOrBelowWatermark > 0);

        internal bool ProducerStillLive => AssignmentsBeyondWatermark > 0 || CapturesBeyondWatermark > 0;

        internal bool Satisfied => PrefixSettled && ProducerStillLive;

        internal string Describe()
        {
            var unsettled = InMotionLaneWork != 0 ? $"{InMotionLaneWork} lane work row(s) still pending, leased or waiting to retry"
                : UnmaterialisedAtWatermark != 0 ? "the capture at the watermark has no committed row yet"
                : RetryableNodes != 0 ? $"{RetryableNodes} processing node(s) in RetryableFailure"
                : CommittedAtOrBelowWatermark == 0 ? "no committed capture at or below the watermark"
                : !ProducerStillLive ? "no assignment or capture beyond the watermark, so the producer is not proven live"
                : "nothing";
            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"watermark={Watermark} unsettled={unsettled} committedAtOrBelow={CommittedAtOrBelowWatermark} "
                    + $"assignmentsBeyond={AssignmentsBeyondWatermark} capturesBeyond={CapturesBeyondWatermark} "
                    + $"terminalLaneWork={TerminalLaneWork} terminalCaptures={TerminalCaptures} terminalNodes={TerminalNodes} "
                    + $"unmaterialisedBelowWatermark={UnmaterialisedBelowWatermark} retriedThenSettled={RetriedLaneWork} "
                    + $"skippedNodes={SkippedNodes} retentionHolds={RetentionHolds}");
        }
    }

    /// <summary>
    /// Waits until every capture assignment at or below the watermark is durably settled and at least one
    /// assignment or capture exists beyond it. The old predicate demanded a global zero, which a producer that
    /// never stops is under no obligation to reach; this one bounds the claim to a prefix the agent has already
    /// finished with, while still requiring proof that it kept working.
    /// </summary>
    private static async Task<CapturePrefixSnapshot> WaitForSettledCapturePrefixAsync(TimeSpan timeout)
    {
        var watermark = ReadCaptureWatermark();
        Assert.IsGreaterThan(0L, watermark, "No capture sequence had been assigned when the fence was taken.");
        var deadline = DateTimeOffset.UtcNow + timeout;
        CapturePrefixSnapshot snapshot;
        while (true)
        {
            snapshot = ReadCapturePrefixSnapshot(watermark);
            if (snapshot.Satisfied)
            {
                // PrefixSettled short-circuits on PrefixHasTerminalDefect, so "satisfied" carries two
                // unrelated meanings: the prefix finished cleanly, or it reached a state nothing exits and
                // waiting longer is pointless. Printing "settled" for both makes the caller's terminal
                // assertion read as a contradiction of the line above it rather than as its consequence.
                Console.WriteLine(snapshot.PrefixHasTerminalDefect
                    ? "capture prefix has a terminal defect; waiting stopped because nothing exits that state: "
                        + snapshot.Describe()
                    : "capture prefix settled: " + snapshot.Describe());
                return snapshot;
            }
            if (DateTimeOffset.UtcNow >= deadline)
            {
                Assert.Fail(string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"The capture prefix did not settle within {timeout.TotalSeconds:F0}s. Last snapshot: {snapshot.Describe()}"));
            }
            Console.WriteLine(snapshot.Describe());
            await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
        }
    }

    private static long ReadCaptureWatermark()
    {
        using var connection = OpenJournalReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(last_sequence), 0) FROM raw_capture_sequences;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CapturePrefixSnapshot ReadCapturePrefixSnapshot(long watermark)
    {
        using var connection = OpenJournalReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM capture_lane_work
                   WHERE capture_sequence <= $watermark AND state IN ('pending', 'leased', 'retry_wait')),
                (SELECT COUNT(*) FROM capture_lane_work
                   WHERE capture_sequence <= $watermark AND state IN ('quarantined', 'abandoned')),
                (SELECT COUNT(*) FROM raw_captures
                   WHERE capture_sequence <= $watermark AND state <> 'committed'),
                (SELECT COUNT(*) FROM processing_nodes node
                   JOIN raw_captures capture ON capture.capture_id = node.capture_id
                   WHERE capture.capture_sequence <= $watermark AND node.status = 'RetryableFailure'),
                (SELECT COUNT(*) FROM processing_nodes node
                   JOIN raw_captures capture ON capture.capture_id = node.capture_id
                   WHERE capture.capture_sequence <= $watermark AND node.status = 'TerminalFailure'),
                (SELECT COUNT(*) FROM processing_nodes node
                   JOIN raw_captures capture ON capture.capture_id = node.capture_id
                   WHERE capture.capture_sequence <= $watermark AND node.status = 'Skipped'),
                (SELECT COUNT(*) FROM raw_captures
                   WHERE capture_sequence <= $watermark AND retention_hold = 1),
                (SELECT COUNT(*) FROM capture_lane_work
                   WHERE capture_sequence <= $watermark AND attempt_count > 1),
                (SELECT COUNT(*) FROM raw_captures
                   WHERE capture_sequence <= $watermark AND state = 'committed'),
                (SELECT COUNT(*) FROM raw_capture_assignments WHERE capture_sequence > $watermark),
                (SELECT COUNT(*) FROM raw_captures WHERE capture_sequence > $watermark),
                (SELECT COUNT(*) FROM raw_capture_assignments a
                   WHERE a.capture_sequence < $watermark
                     AND NOT EXISTS (SELECT 1 FROM raw_captures c
                                     WHERE c.capture_id = a.capture_id AND c.state = 'committed')),
                (SELECT COUNT(*) FROM raw_capture_assignments a
                   WHERE a.capture_sequence = $watermark
                     AND NOT EXISTS (SELECT 1 FROM raw_captures c WHERE c.capture_id = a.capture_id));
            """;
        command.Parameters.AddWithValue("$watermark", watermark);
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read(), "The capture prefix snapshot returned no row.");
        return new CapturePrefixSnapshot(
            watermark,
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12));
    }

    /// <summary>
    /// Asserts that raw ingress is admitting work, tolerating the one degradation that is a property of a
    /// live producer rather than of the pipeline under test.
    /// </summary>
    /// <remarks>
    /// <see cref="RawIngressState"/> collapses two independent things into <c>Availability</c>: the base
    /// health of ingress, and an overlay that reports <c>Degraded</c> with reason
    /// <c>projected-scene-backlog</c> whenever the staging reconciler still has entries it has not inspected.
    /// That backlog is the <c>remaining</c> counter of a pass bounded to
    /// <see cref="ProjectedSceneStagingOptions.MaximumReconciliationEntries"/>, so it is non-zero whenever the
    /// producer stages scenes faster than one pass sweeps them. That is throughput on a host that never stops, not evidence that anything is wrong, and an
    /// absolute equality gate here fails under load for a reason that says nothing about the property being
    /// tested.
    /// The overlay is applied only when the base availability is already <c>Accepting</c>
    /// (see <c>RawIngressState.PublishSnapshot</c>), so the reason string discriminates exactly: observing
    /// <c>projected-scene-backlog</c> is itself proof that the base was healthy. Every genuine degradation
    /// keeps its own reason and still fails here -- <c>disk-pressure</c>, <c>index-projection-failed</c>,
    /// <c>reconciliation-findings</c>, <c>capacity-exhausted</c>, <c>capacity-probe-failed</c>. Disk pressure
    /// is deliberately still gated: it is a condition under which this pipeline's evidence is suspect, which
    /// backlog is not.
    /// </remarks>
    private static void AssertIngressAdmitting(
        RawIngressSnapshot snapshot,
        string stage,
        int reconciliationPassBound,
        string? storageRoot = null)
    {
        // Recorded on every run, degraded or not. The open question behind this gate is how close a normal
        // run comes to the pass bound, and nobody finds that out from a test that only speaks when it fails.
        // The bound is read from the configured options rather than written here: a literal would keep
        // printing 512 after someone retuned the option, which is the failure this measurement exists to
        // avoid making elsewhere.
        Console.WriteLine(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"raw ingress {stage}: availability={snapshot.Availability} reason={snapshot.Reason} "
                + $"stagedProjectedScenes={CountStagedProjectedScenes(storageRoot ?? Fixture.StorageRoot)} "
                + $"reconciliationPassBound={reconciliationPassBound}"));

        if (snapshot.Availability == RawIngressAvailability.Degraded &&
            string.Equals(snapshot.Reason, "projected-scene-backlog", StringComparison.Ordinal))
        {
            return;
        }

        Assert.AreEqual(
            RawIngressAvailability.Accepting,
            snapshot.Availability,
            string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Raw ingress {stage} is not admitting: availability={snapshot.Availability} reason={snapshot.Reason}"));
    }

    /// <summary>
    /// Counts entries in the projected-scene staging directory, which is what the bounded reconciliation pass
    /// walks and therefore what the backlog counter is derived from.
    /// </summary>
    private static int CountStagedProjectedScenes(string storageRoot)
    {
        var root = Path.Combine(storageRoot, "staging", "projected-scenes");
        return Directory.Exists(root)
            ? Directory.EnumerateFileSystemEntries(root, "*", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    private static SqliteConnection OpenJournalReadConnection()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(Fixture.StorageRoot, "journal", "raw-ingress.db")}");
        connection.Open();
        return connection;
    }

    private static OutboxCheckpoint ReadPendingOutboxCheckpoint()
    {
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT idempotency_key, artifact_id, payload_sha256, payload_length
            FROM artifact_outbox_records
            WHERE status = 'pending' AND manifest_kind = 'v2'
            ORDER BY next_attempt_unix_ms, created_unix_ms, idempotency_key
            LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read(), "A pending outbox record was not available for the upload checkpoint.");
        return new OutboxCheckpoint(
            reader.GetString(0),
            Guid.ParseExact(reader.GetString(1), "N"),
            reader.GetString(2),
            reader.GetInt64(3));
    }

    private static bool HasMatchingAcknowledgement(OutboxCheckpoint checkpoint)
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        if (!File.Exists(path))
        {
            return false;
        }
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status, acknowledgement
            FROM artifact_outbox_records
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", checkpoint.IdempotencyKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != "acknowledged" || reader.IsDBNull(1))
        {
            return false;
        }

        try
        {
            var acknowledgement = JsonSerializer.Deserialize<ArtifactUploadAcknowledgement>((byte[])reader[1]);
            acknowledgement?.Validate();
            return acknowledgement is not null &&
                string.Equals(acknowledgement.IdempotencyKey, checkpoint.IdempotencyKey, StringComparison.OrdinalIgnoreCase) &&
                acknowledgement.ArtifactId == checkpoint.ArtifactId &&
                string.Equals(acknowledgement.ChecksumSha256, checkpoint.ChecksumSha256, StringComparison.OrdinalIgnoreCase) &&
                acknowledgement.ByteLength == checkpoint.ByteLength &&
                acknowledgement.AcceptedManifestSchemaVersion == ArtifactManifestV2.CurrentSchemaVersion;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static int CountUnfinishedOutboxRecords()
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        if (!File.Exists(path))
        {
            return 0;
        }
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_outbox_records WHERE status <> 'acknowledged';";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool HasNoEnvironmentalOutboxRecord(Guid observationId)
    {
        var path = Path.Combine(Fixture.StorageRoot, ".environment", "environmental-observation-outbox.db");
        if (!File.Exists(path))
        {
            return false;
        }
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM environmental_observation_outbox WHERE observation_id = $observationId;";
        command.Parameters.AddWithValue("$observationId", observationId.ToString("D"));
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static MeterListener CreateMeterListener(
        ConcurrentDictionary<string, byte> instrumentNames,
        ConcurrentDictionary<string, double> measurements,
        ConcurrentBag<string> tagValues)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name is
                    CaptureTelemetryMetricsRecorder.MeterName or
                    CaptureControlTelemetry.MeterName or
                    RawIngressTelemetry.MeterName or
                    CaptureProcessingTelemetry.MeterName or
                    "HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery")
                {
                    instrumentNames.TryAdd(instrument.Name, 0);
                    current.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            measurements.AddOrUpdate(instrument.Name, measurement, (_, total) => total + measurement);
            CaptureTagValues(tags, tagValues);
        });
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            measurements.AddOrUpdate(instrument.Name, measurement, (_, total) => total + measurement);
            CaptureTagValues(tags, tagValues);
        });
        listener.Start();
        return listener;
    }

    private static ActivityListener CreateActivityListener(
        ConcurrentBag<string> activityNames,
        ConcurrentBag<string> tagValues)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name is
                CaptureControlTelemetry.ActivitySourceName or
                RawIngressTelemetry.ActivitySourceName or
                CaptureProcessingTelemetry.ActivitySourceName or
                "HVO.SkyMonitor.CameraAgent.EnvironmentalDelivery",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                activityNames.Add(activity.DisplayName);
                foreach (var tag in activity.TagObjects)
                {
                    if (tag.Value is not null)
                    {
                        tagValues.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
                    }
                }
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static void CaptureTagValues(
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        ConcurrentBag<string> values)
    {
        foreach (var tag in tags)
        {
            if (tag.Value is not null)
            {
                values.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
            }
        }
    }

    private static string ReadOutboxState(string idempotencyKey)
    {
        using var connection = OpenOutboxReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT status || ':' || COALESCE(last_reason, '') || ':ack=' ||
                   CASE WHEN acknowledgement IS NULL THEN 'missing' ELSE 'present' END
            FROM artifact_outbox_records
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        var selected = Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "no-record";
        command.Parameters.Clear();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN status = 'pending' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'leased' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'retry' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'acknowledged' THEN 1 ELSE 0 END),
                SUM(CASE WHEN status = 'quarantined' THEN 1 ELSE 0 END)
            FROM artifact_outbox_records;
            """;
        using var reader = command.ExecuteReader();
        reader.Read();
        return $"selected={selected}; counts=pending:{reader.GetInt64(0)},leased:{reader.GetInt64(1)},retry:{reader.GetInt64(2)},acknowledged:{reader.GetInt64(3)},quarantined:{reader.GetInt64(4)}";
    }

    private static SqliteConnection OpenOutboxReadConnection()
    {
        var path = Path.Combine(Fixture.StorageRoot, "outbox", "artifact-outbox.db");
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed record OutboxCheckpoint(
        string IdempotencyKey,
        Guid ArtifactId,
        string ChecksumSha256,
        long ByteLength);
}
