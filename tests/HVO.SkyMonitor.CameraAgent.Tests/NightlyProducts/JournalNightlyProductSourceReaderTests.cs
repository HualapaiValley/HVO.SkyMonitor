using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;

/// <summary>
/// Producer-side proof of the nightly source contract: real raw ingress and a real standard-lane Preview node commit
/// the outputs, and the journal reader is asked for them by exposure window exactly as the generator asks.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Focused MSTest methods do not require context-free continuations.")]
public sealed class JournalNightlyProductSourceReaderTests
{
    private const int FrameCount = 5;
    private static readonly DateTimeOffset FirstExposureUtc = new(2026, 10, 2, 5, 0, 0, TimeSpan.Zero);

    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-nightly-journal");

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public async Task ReadCandidates_SelectsPublishedPreviewsByTheirOwnExposureClockInOrder()
    {
        using var provider = CreateProvider();
        var receipts = await CaptureAsync(provider);
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();
        var unsupported = new List<string>();

        var window = await reader.ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(1), Exposure(4), 32, unsupported.Add, CancellationToken.None);
        var other = await reader.ReadCandidatesAsync(
            "calibration", Exposure(0), Exposure(FrameCount), 32, unsupported.Add, CancellationToken.None);

        CollectionAssert.AreEqual(
            receipts.Skip(1).Take(3).Select(static receipt => receipt.Manifest.Descriptor.Capture.CaptureId).ToArray(),
            window.Select(static candidate => candidate.CaptureId).ToArray(),
            "The window is [start, end) on the raw exposure clock.");
        CollectionAssert.AreEqual(
            new[] { Exposure(1), Exposure(2), Exposure(3) },
            window.Select(static candidate => candidate.ExposureStartedUtc).ToArray());
        Assert.IsTrue(window.All(static candidate =>
            candidate.MediaType == JournalNightlyProductSourceReader.PackedImageMediaType &&
            candidate.Variant == "default"));
        Assert.HasCount(1, window.Select(static candidate => candidate.RigProfileSha256).Distinct().ToArray());
        Assert.IsEmpty(other);
        Assert.IsEmpty(unsupported);
    }

    [TestMethod]
    public async Task ReadCandidates_RespectsFractionalMillisecondHalfOpenWindowBeforeApplyingBound()
    {
        using var provider = CreateProvider();
        var receipts = await CaptureAsync(provider);
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();

        var candidates = await reader.ReadCandidatesAsync(NightlyProductFixture.NodeId,
            Exposure(0).AddTicks(1), Exposure(2).AddTicks(1), 1, static _ => Assert.Fail("Unsupported source."), CancellationToken.None);

        CollectionAssert.AreEqual(receipts.Skip(1).Take(2).Select(static receipt => receipt.Manifest.Descriptor.Capture.CaptureId).ToArray(),
            candidates.Select(static candidate => candidate.CaptureId).ToArray());
        Assert.HasCount(2, candidates, "Exact selection precedes the maximum + 1 bound.");
    }

    [TestMethod]
    public async Task ReadCandidates_ReturnsOneRowPastTheBoundSoTheCallerCanReject()
    {
        using var provider = CreateProvider();
        await CaptureAsync(provider);
        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();

        var bounded = await provider.GetRequiredService<INightlyProductSourceReader>().ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(0), Exposure(FrameCount), 2, static _ => { }, CancellationToken.None);
        var otherRole = await store.ReadPublishedOutputsByExposureAsync(
            NightlyProductFixture.NodeId, FrameArtifactRole.Calibrated, Exposure(0), Exposure(FrameCount), 32,
            CancellationToken.None);

        Assert.HasCount(3, bounded);
        Assert.IsEmpty(otherRole);
    }

    [TestMethod]
    public async Task ReadCandidates_PrefersTheLatestPublishedOutputAndIgnoresUnpublishedOnes()
    {
        using var provider = CreateProvider();
        var receipts = await CaptureAsync(provider);
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();
        var captureId = receipts[0].Manifest.Descriptor.Capture.CaptureId;
        var original = (await reader.ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(0), Exposure(1), 32, static _ => { }, CancellationToken.None)).Single();
        var newer = new string('E', 64);
        DuplicateOutput(original, newer, Guid.NewGuid());

        var replaced = (await reader.ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(0), Exposure(1), 32, static _ => { }, CancellationToken.None)).Single();
        AttachUnpublished(captureId, newer);
        var restored = (await reader.ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(0), Exposure(1), 32, static _ => { }, CancellationToken.None)).Single();

        Assert.AreEqual(newer, replaced.OutputIdentitySha256, "A capture contributes only its latest output.");
        Assert.AreEqual(captureId, replaced.CaptureId);
        Assert.AreEqual(original.OutputIdentitySha256, restored.OutputIdentitySha256, "An unpublished output is never a source.");
    }

    [TestMethod]
    public async Task ReadCandidates_CountsUnsupportedMediaAndSkipsUnavailableOutputs()
    {
        using var provider = CreateProvider();
        await CaptureAsync(provider);
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();
        var all = await reader.ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(0), Exposure(FrameCount), 32, static _ => { }, CancellationToken.None);
        Execute(
            "UPDATE processing_outputs SET descriptor_json = CAST(replace(CAST(descriptor_json AS TEXT), $from, 'image/jpeg') AS BLOB) WHERE output_identity_sha256 = $output;",
            ("$from", JournalNightlyProductSourceReader.PackedImageMediaType),
            ("$output", all[1].OutputIdentitySha256));
        Execute(
            "UPDATE processing_outputs SET availability_state = 'Missing', availability_reason = 'test', unavailable_unix_ms = 1 WHERE output_identity_sha256 = $output;",
            ("$output", all[2].OutputIdentitySha256));
        var unsupported = new List<string>();

        var remaining = await reader.ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(0), Exposure(FrameCount), 32, unsupported.Add, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { all[0].ArtifactId, all[3].ArtifactId, all[4].ArtifactId },
            remaining.Select(static candidate => candidate.ArtifactId).ToArray());
        CollectionAssert.AreEqual(new[] { NightlyProductContract.ExcludedUnsupportedSourceReasonCode }, unsupported.ToArray());
    }

    [TestMethod]
    public async Task Restore_ReturnsTheSelectedPixelsAndRejectsAnOutputThatBecameUnavailable()
    {
        using var provider = CreateProvider();
        await CaptureAsync(provider);
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();
        var candidates = await reader.ReadCandidatesAsync(
            NightlyProductFixture.NodeId, Exposure(0), Exposure(FrameCount), 32, static _ => { }, CancellationToken.None);

        var artifacts = await reader.RestoreAsync(candidates, CancellationToken.None);
        Execute(
            "UPDATE processing_outputs SET availability_state = 'Quarantined', availability_reason = 'test', unavailable_unix_ms = 1 WHERE output_identity_sha256 = $output;",
            ("$output", candidates[3].OutputIdentitySha256));

        CollectionAssert.AreEqual(
            candidates.Select(static candidate => candidate.ArtifactId).ToArray(),
            artifacts.Select(static artifact => artifact.ArtifactId).ToArray());
        CollectionAssert.AreEqual(
            candidates.Select(static candidate => (DateTimeOffset?)candidate.ExposureStartedUtc).ToArray(),
            artifacts.Select(static artifact => artifact.ObservationStartedUtc).ToArray());
        Assert.IsTrue(artifacts.All(static artifact => artifact.Layout is { Width: 2, Height: 2 }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await reader.RestoreAsync(candidates, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Restore_RejectsOversizedCommittedFilesWithoutAllocatingTheirUntrustedLength(bool sidecar)
    {
        using var provider = CreateProvider();
        await CaptureAsync(provider);
        var reader = provider.GetRequiredService<INightlyProductSourceReader>();
        var selected = (await reader.ReadCandidatesAsync(NightlyProductFixture.NodeId, Exposure(0), Exposure(1), 32,
            static _ => Assert.Fail("Unsupported source."), CancellationToken.None)).Single();
        var output = await provider.GetRequiredService<SqliteCaptureProcessingStore>()
            .ReadOutputByArtifactIdAsync(selected.ArtifactId, CancellationToken.None);
        Assert.IsNotNull(output);
        var path = Path.Combine(_root, sidecar ? output.SidecarRelativePath : output.PayloadRelativePath);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            file.SetLength(256L * 1024 * 1024 + 1);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await reader.RestoreAsync([selected], CancellationToken.None));
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.IsLessThan(1024L * 1024, allocated,
            "A sparse corrupt payload/sidecar must be rejected using its retained length before reading its oversized contents.");
    }

    [TestMethod]
    public async Task WindowQuery_SearchesIndexesRatherThanScanningTheJournal()
    {
        using var provider = CreateProvider();
        await CaptureAsync(provider);
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "journal", "raw-ingress.db")};Pooling=False");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteCaptureProcessingStore.PublishedOutputsByExposureSql;
        command.Parameters.AddWithValue("$node_id", NightlyProductFixture.NodeId);
        command.Parameters.AddWithValue("$role", nameof(FrameArtifactRole.Preview));
        command.Parameters.AddWithValue("$start_unix_ms", Exposure(0).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$end_unix_ms", Exposure(FrameCount).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$maximum_count_plus_one", 33L);
        var plan = new List<string>();
        using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                plan.Add(reader.GetString(3));
            }
        }

        var detail = string.Join(" | ", plan);
        Assert.IsTrue(
            plan.Any(static step => step.StartsWith("SEARCH raw USING INDEX ix_raw_captures_gallery_time", StringComparison.Ordinal) &&
                                    step.Contains("exposure_started_unix_ms>?", StringComparison.Ordinal)),
            detail);
        Assert.IsTrue(
            plan.Any(static step => step.StartsWith(
                "SEARCH output USING INDEX ix_processing_outputs_capture_node (capture_id=? AND node_id=?)",
                StringComparison.Ordinal)),
            detail);
        Assert.IsLessThan(
            plan.FindIndex(static step => step.StartsWith("SEARCH output", StringComparison.Ordinal)),
            plan.FindIndex(static step => step.StartsWith("SEARCH raw", StringComparison.Ordinal)),
            detail);
        Assert.IsFalse(
            plan.Any(static step => step.StartsWith("SCAN raw", StringComparison.Ordinal) ||
                                    step.StartsWith("SCAN output", StringComparison.Ordinal) ||
                                    step.StartsWith("SCAN association", StringComparison.Ordinal)),
            detail);
    }

    private static DateTimeOffset Exposure(int index) => FirstExposureUtc.AddMinutes(index);

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = _root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false"
            }).Build());
        return services.BuildServiceProvider();
    }

    private static async Task<RawCaptureReceipt[]> CaptureAsync(ServiceProvider provider)
    {
        var configuration = Configuration();
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None);
        await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>()
            .EnsureConfiguredBasicAsync(configuration, CancellationToken.None);
        var receipts = new RawCaptureReceipt[FrameCount];
        for (var index = 0; index < FrameCount; index++)
        {
            receipts[index] = (await ingress.AcceptAsync(configuration, Submission(index), CancellationToken.None))!;
            Assert.IsNotNull(receipts[index]);
        }
        Array.Reverse(receipts);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        await laneStore.InitializeLanesAsync(CancellationToken.None);
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static definition => definition.Name == "standard");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "standard");
        for (var index = 0; index < FrameCount; index++)
        {
            var lease = await laneStore.ClaimAsync(standard, "nightly-sources", configuration, CancellationToken.None);
            Assert.IsNotNull(lease);
            var result = await handler.HandleAsync(lease.Context, CancellationToken.None);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
            await laneStore.CompleteAsync(lease, CancellationToken.None);
        }
        return receipts; // Ordered by exposure.
    }

    private static CameraModuleConfig Configuration()
        => new(
            NightlyProductFixture.Observatory,
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile("Test", 2, 2, 1, SensorColorMode.Mono, CameraPixelFormat.Mono8),
                new OpticsProfile("Test", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(
                [new CaptureProcessingStepConfig("Preview", NightlyProductFixture.NodeId, DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "nightly-agent");

    private static CaptureLoopSubmission Submission(int index)
    {
        // Submit newest first so commit order disagrees with exposure order.
        var startedUtc = Exposure(FrameCount - 1 - index);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var frame = new CameraFrame(
            startedUtc,
            2,
            2,
            CameraPixelFormat.Mono8,
            new byte[] { (byte)(index + 1), 2, 3, 4 },
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 10, "test"));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(1), startedUtc.AddSeconds(1.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    /// <summary>
    /// Commits a second, later output for the same capture and node - what a re-run under a new graph revision leaves
    /// behind - with its descriptor rewritten to the new identity so the journal still reads it as consistent.
    /// </summary>
    private void DuplicateOutput(NightlyProductCandidate original, string duplicateIdentity, Guid duplicateArtifactId)
        => Execute(
            """
            CREATE TEMP TABLE duplicate AS SELECT * FROM processing_outputs WHERE output_identity_sha256 = $identity;
            UPDATE duplicate SET output_identity_sha256 = $duplicate, artifact_id = $artifact_n,
                committed_unix_ms = committed_unix_ms + 1,
                descriptor_json = CAST(replace(replace(replace(CAST(descriptor_json AS TEXT),
                    $identity, $duplicate), $original_d, $artifact_d), $original_n, $artifact_n) AS BLOB);
            INSERT INTO processing_outputs SELECT * FROM duplicate;
            DROP TABLE duplicate;
            """,
            ("$identity", original.OutputIdentitySha256),
            ("$duplicate", duplicateIdentity),
            ("$original_d", original.ArtifactId.ToString("D")),
            ("$original_n", original.ArtifactId.ToString("N")),
            ("$artifact_d", duplicateArtifactId.ToString("D")),
            ("$artifact_n", duplicateArtifactId.ToString("N")));

    private void AttachUnpublished(Guid captureId, string outputIdentity)
        => Execute(
            """
            INSERT INTO processing_execution_outputs(
                execution_id, node_id, output_ordinal, output_identity_sha256, published_flag)
            SELECT association.execution_id, association.node_id, MAX(association.output_ordinal) + 1, $output, 0
            FROM processing_execution_outputs association
            JOIN processing_executions execution ON execution.execution_id = association.execution_id
            WHERE execution.capture_id = $capture AND association.node_id = $node
            GROUP BY association.execution_id, association.node_id;
            """,
            ("$capture", captureId.ToString("N")),
            ("$node", NightlyProductFixture.NodeId),
            ("$output", outputIdentity));

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Fixed test SQL with bound parameters.")]
    private void Execute(string sql, params (string Name, string Value)[] parameters)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_root, "journal", "raw-ingress.db")};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        Assert.IsGreaterThan(0, command.ExecuteNonQuery());
    }
}
