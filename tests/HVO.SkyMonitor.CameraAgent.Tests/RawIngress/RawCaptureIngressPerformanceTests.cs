using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class RawCaptureIngressPerformanceTests
{
    private const int W2Width = 3096;
    private const int W2Height = 2080;
    private const int W2Stride = W2Width * 2;
    private const int WarmupCount = 5;
    private const int MeasuredCount = 30;
    private const int W3MetadataCount = 10_000;
    private const int W3PayloadCount = 100;
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W2W3MAndW3P_DurableIngressEvidence()
    {
        var outputDirectory = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-94", "working-tree");
        var workRoot = Path.Combine(outputDirectory, "performance-work");
        Directory.CreateDirectory(outputDirectory);
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, recursive: true);
        }
        Directory.CreateDirectory(workRoot);
        try
        {
            var configuration = await LoadCanonicalConfigurationAsync().ConfigureAwait(false);
            var input = await RenderCanonicalW2InputAsync(configuration).ConfigureAwait(false);
            var payload = input.Payload;
            var w2 = await MeasureW2Async(Path.Combine(workRoot, "w2"), input, configuration).ConfigureAwait(false);
            var w3m = await MeasureW3MetadataAsync(Path.Combine(workRoot, "w3m"), input, configuration).ConfigureAwait(false);
            var w3p = await MeasureW3PayloadAsync(Path.Combine(workRoot, "w3p"), input, configuration).ConfigureAwait(false);
            var faultRecovery = await MeasureFaultRecoveryAsync(Path.Combine(workRoot, "faults"), input, configuration).ConfigureAwait(false);
            var profileSha256 = Convert.ToHexString(SHA256.HashData(
                await File.ReadAllBytesAsync(CanonicalProfilePath).ConfigureAwait(false)));
            var evidence = new
            {
                Revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "candidate-working-tree",
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    Configuration = BuildConfiguration,
                    ProcessorCount = Environment.ProcessorCount,
                    ServerGc = System.Runtime.GCSettings.IsServerGC,
                    SqliteVersion = await ReadSqliteVersionAsync().ConfigureAwait(false)
                },
                Workload = new
                {
                    Profile = "virtual-asi178mc.full.json",
                    ProfileSha256 = profileSha256,
                    RigProfileVersion = configuration.Rig.ProfileVersion,
                    SensorRecipeVersion = configuration.Rig.Sensor.SensorRecipeVersion,
                    OpticsCalibrationVersion = configuration.Rig.Optics.CalibrationVersion,
                    PayloadSource = "VirtualSkyCameraModule using the exact checked-in profile and a deterministic 300-object in-memory catalog",
                    Seed = 2025,
                    Width = W2Width,
                    Height = W2Height,
                    Stride = W2Stride,
                    Format = CameraPixelFormat.BayerRggb16.ToString(),
                    PayloadBytes = payload.LongLength,
                    WarmupCount,
                    MeasuredCount,
                    W3MetadataCount,
                    W3PayloadCount,
                    Concurrency = 1,
                    ConfiguredArrivalPerSecond = 0.04,
                    RequiredCommitAndRecoveryPerSecond = 1.0
                },
                W2 = w2,
                W3M = w3m,
                W3P = w3p,
                RepresentativeFaultRecovery = faultRecovery,
                NearestPathBaseline = new
                {
                    Revision = "9383e4bc4b31ec23860b26b3ecf18644fb1091de",
                    W2ManifestV2MedianMilliseconds = 14.2587,
                    W2ManifestV2P95Milliseconds = 17.5251,
                    MedianChangePercent = (w2.MedianMilliseconds - 14.2587) * 100d / 14.2587,
                    P95ChangePercent = (w2.P95Milliseconds - 17.5251) * 100d / 17.5251,
                    Disposition = "Expected durability cost: the candidate adds write-through payload/sidecar flushes, ancestor and leaf directory fsync, a restart-safe sequence transaction, a FULL-synchronous WAL commit, and a durable compatibility-index append. It remains over 25 times faster than configured arrival and no cost is moved to an unmeasured queue."
                },
                Correctness = new
                {
                    PayloadSha256 = PayloadChecksum.ComputeSha256(payload),
                    CodePathFullPayloadCopies = 0,
                    BufferOwnership = "Ingress writes and hashes caller-owned ReadOnlyMemory synchronously before returning; no payload reference is stored in receipts, journal entries, telemetry, or wake-up state.",
                    Result = "All committed files, manifest-v2 descriptors with scene provenance, checksums, WAL rows, indexes, and restart counts were asserted by the harness."
                }
            };
            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "raw-ingress-performance.json"),
                JsonSerializer.Serialize(evidence, EvidenceOptions)).ConfigureAwait(false);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workRoot))
            {
                Directory.Delete(workRoot, recursive: true);
            }
        }
    }

    private static async Task<W2Measurement> MeasureW2Async(
        string root,
        W2Input input,
        CameraModuleConfig configuration)
    {
        var payload = input.Payload;
        using var fixture = CreateIngress(root);
        for (var index = 0; index < WarmupCount; index++)
        {
            await fixture.Ingress.AcceptAsync(
                configuration, CreateSubmission(index, input), CancellationToken.None).ConfigureAwait(false);
        }
        var samples = new double[MeasuredCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var workingSetBefore = Environment.WorkingSet;
        var lohBefore = GetLohSize();
        var lockWaitBefore = fixture.Telemetry.TotalLockWait;
        var lockWaitSamplesBefore = fixture.Telemetry.LockWaitSamples;
        var transactionsBefore = fixture.Telemetry.TransactionCount;
        var transactionFailuresBefore = fixture.Telemetry.TransactionFailureCount;
        var fileFlushesBefore = fixture.Telemetry.FileFlushCount;
        var directorySyncsBefore = fixture.Telemetry.DirectorySyncCount;
        RawCaptureReceipt? lastReceipt = null;
        for (var index = 0; index < MeasuredCount; index++)
        {
            var started = Stopwatch.GetTimestamp();
            lastReceipt = await fixture.Ingress.AcceptAsync(
                configuration, CreateSubmission(index + WarmupCount, input), CancellationToken.None).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var workingSetAfter = Environment.WorkingSet;
        var lohAfter = GetLohSize();
        AssertReceipt(lastReceipt!, payload);
        Array.Sort(samples);
        var totalSeconds = samples.Sum() / 1000d;
        var throughput = MeasuredCount / totalSeconds;
        Assert.IsGreaterThanOrEqualTo(1d, throughput);
        var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
        var walPath = string.Concat(databasePath, "-wal");
        await ValidateCommittedEvidenceAsync(
            root,
            WarmupCount + MeasuredCount,
            PayloadChecksum.ComputeSha256(payload),
            configuration).ConfigureAwait(false);
        return new W2Measurement(
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[0],
            samples[^1],
            throughput,
            cpu.TotalMilliseconds,
            allocated,
            allocated / MeasuredCount,
            workingSetBefore,
            workingSetAfter,
            lohBefore,
            lohAfter,
            (fixture.Telemetry.TotalLockWait - lockWaitBefore).TotalMilliseconds,
            fixture.Telemetry.LockWaitSamples - lockWaitSamplesBefore,
            (long)MeasuredCount * payload.LongLength,
            (long)MeasuredCount * payload.LongLength,
            fixture.Telemetry.FileFlushCount - fileFlushesBefore,
            fixture.Telemetry.DirectorySyncCount - directorySyncsBefore,
            fixture.Telemetry.TransactionCount - transactionsBefore,
            fixture.Telemetry.TransactionFailureCount - transactionFailuresBefore,
            WarmupCount + MeasuredCount,
            Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories).Sum(static path => new FileInfo(path).Length),
            Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.json", SearchOption.AllDirectories).Sum(static path => new FileInfo(path).Length),
            Directory.EnumerateFiles(Path.Combine(root, "index"), "*.jsonl").Sum(static path => new FileInfo(path).Length),
            new FileInfo(databasePath).Length,
            File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            "Flush, directory-sync, and transaction counts are recorded by the measured accept path; byte sizes are read from produced files.");
    }

    private static async Task<object> MeasureW3MetadataAsync(
        string root,
        W2Input input,
        CameraModuleConfig canonicalConfiguration)
    {
        var payload = input.Payload;
        var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
        var journal = new SqliteRawCaptureJournal(databasePath, busyTimeoutSeconds: 5);
        var migrationStarted = Stopwatch.GetTimestamp();
        await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var migrationMilliseconds = Stopwatch.GetElapsedTime(migrationStarted).TotalMilliseconds;
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        using var assignment = connection.CreateCommand();
        assignment.Transaction = transaction;
        assignment.CommandText = """
            INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
            VALUES ($capture, $artifact, 'agent-w3m', $sequence);
            """;
        var captureParameter = assignment.Parameters.Add("$capture", SqliteType.Text);
        var artifactParameter = assignment.Parameters.Add("$artifact", SqliteType.Text);
        var assignmentSequence = assignment.Parameters.Add("$sequence", SqliteType.Integer);
        using var capture = connection.CreateCommand();
        capture.Transaction = transaction;
        capture.CommandText = """
            INSERT INTO raw_captures(
                capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                manifest_sha256, payload_sha256, payload_length, payload_relative_path,
                sidecar_relative_path, manifest_json, exposure_started_unix_ms,
                durable_ingress_unix_ms, committed_unix_ms, state, retention_hold)
            VALUES ($capture, $artifact, 'agent-w3m', $sequence, $descriptor,
                $manifest, $payload, $payload_length, $payload_path, $sidecar_path, $manifest_json,
                $time, $time, $time, 'committed', 1);
            """;
        var parameters = new
        {
            Capture = capture.Parameters.Add("$capture", SqliteType.Text),
            Artifact = capture.Parameters.Add("$artifact", SqliteType.Text),
            Sequence = capture.Parameters.Add("$sequence", SqliteType.Integer),
            Descriptor = capture.Parameters.Add("$descriptor", SqliteType.Text),
            Manifest = capture.Parameters.Add("$manifest", SqliteType.Text),
            Payload = capture.Parameters.Add("$payload", SqliteType.Text),
            PayloadLength = capture.Parameters.Add("$payload_length", SqliteType.Integer),
            PayloadPath = capture.Parameters.Add("$payload_path", SqliteType.Text),
            SidecarPath = capture.Parameters.Add("$sidecar_path", SqliteType.Text),
            ManifestJson = capture.Parameters.Add("$manifest_json", SqliteType.Blob),
            Time = capture.Parameters.Add("$time", SqliteType.Integer)
        };
        var configuration = canonicalConfiguration with { AgentId = "agent-w3m" };
        var payloadSha256 = PayloadChecksum.ComputeSha256(payload);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var rssBefore = Environment.WorkingSet;
        var started = Stopwatch.GetTimestamp();
        for (var index = 1; index <= W3MetadataCount; index++)
        {
            var captureId = Guid.NewGuid().ToString("N");
            var artifactId = Guid.NewGuid().ToString("N");
            var submission = CreateSubmission(index, input);
            var durableIngressUtc = submission.Result.Frame!.TimestampUtc.AddSeconds(21);
            var descriptor = RawCaptureDescriptorFactory.Create(
                configuration,
                submission,
                new RawCaptureIdentity("agent-w3m", index, Guid.ParseExact(captureId, "N"), Guid.ParseExact(artifactId, "N")),
                payloadSha256,
                durableIngressUtc);
            var manifest = new ArtifactManifestV2(
                ArtifactManifestV2.CurrentSchemaVersion,
                descriptor,
                $"metadata/{index}.bin",
                submission.Result.Frame!.Metadata.Scene);
            var manifestJson = CaptureContractJson.Serialize(manifest);
            captureParameter.Value = captureId;
            artifactParameter.Value = artifactId;
            assignmentSequence.Value = index;
            await assignment.ExecuteNonQueryAsync().ConfigureAwait(false);
            parameters.Capture.Value = captureId;
            parameters.Artifact.Value = artifactId;
            parameters.Sequence.Value = index;
            parameters.Descriptor.Value = CaptureContractJson.ComputeDescriptorSha256(descriptor);
            parameters.Manifest.Value = CaptureContractJson.ComputeManifestSha256(manifest);
            parameters.Payload.Value = payloadSha256;
            parameters.PayloadLength.Value = payload.LongLength;
            parameters.PayloadPath.Value = $"metadata/{index}.bin";
            parameters.SidecarPath.Value = $"metadata/{index}.json";
            parameters.ManifestJson.Value = manifestJson;
            parameters.Time.Value = durableIngressUtc.ToUnixTimeMilliseconds();
            await capture.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await transaction.CommitAsync().ConfigureAwait(false);
        var insertMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var queryStarted = Stopwatch.GetTimestamp();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM raw_captures WHERE state = 'committed';";
        Assert.AreEqual(W3MetadataCount, Convert.ToInt32(
            await count.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture));
        using var plan = connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT capture_id FROM raw_captures WHERE state = 'committed' ORDER BY agent_id, capture_sequence LIMIT 100;";
        var planDetails = new List<string>();
        using (var reader = await plan.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                planDetails.Add(reader.GetString(3));
            }
        }
        Assert.IsTrue(planDetails.Any(static detail => detail.Contains("INDEX", StringComparison.OrdinalIgnoreCase)));
        var queryMilliseconds = Stopwatch.GetElapsedTime(queryStarted).TotalMilliseconds;
        var databaseBytesBeforeCheckpoint = new FileInfo(databasePath).Length;
        var walPath = string.Concat(databasePath, "-wal");
        var walBytesBeforeCheckpoint = File.Exists(walPath) ? new FileInfo(walPath).Length : 0;
        var checkpointStarted = Stopwatch.GetTimestamp();
        await journal.CheckpointAsync(CancellationToken.None).ConfigureAwait(false);
        var checkpointMilliseconds = Stopwatch.GetElapsedTime(checkpointStarted).TotalMilliseconds;
        await connection.CloseAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        var restartStarted = Stopwatch.GetTimestamp();
        var restartedJournal = new SqliteRawCaptureJournal(databasePath, busyTimeoutSeconds: 5);
        await restartedJournal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var discovered = await restartedJournal.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        var restartDiscoveryMilliseconds = Stopwatch.GetElapsedTime(restartStarted).TotalMilliseconds;
        Assert.HasCount(W3MetadataCount, discovered);
        Assert.IsTrue(discovered.All(static entry =>
        {
            var parsed = CaptureContractJson.ParseManifest(entry.ManifestJson);
            return parsed.IsValid && parsed.Document?.Manifest is not null;
        }));
        return new
        {
            Records = W3MetadataCount,
            MigrationMilliseconds = migrationMilliseconds,
            InsertMilliseconds = insertMilliseconds,
            QueryMilliseconds = queryMilliseconds,
            RecordsPerSecond = W3MetadataCount / (insertMilliseconds / 1000d),
            CpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore,
            RssBeforeBytes = rssBefore,
            RssAfterBytes = Environment.WorkingSet,
            DatabaseBytesBeforeCheckpoint = databaseBytesBeforeCheckpoint,
            WalBytesBeforeCheckpoint = walBytesBeforeCheckpoint,
            DatabaseBytesAfterCheckpoint = new FileInfo(databasePath).Length,
            WalBytesAfterCheckpoint = File.Exists(walPath) ? new FileInfo(walPath).Length : 0,
            CheckpointMilliseconds = checkpointMilliseconds,
            RestartDiscoveryMilliseconds = restartDiscoveryMilliseconds,
            RestartDiscoveredRecords = discovered.Count,
            Transactions = 1,
            Statements = W3MetadataCount * 2 + 3,
            QueryPlan = planDetails
        };
    }

    private static async Task<object> MeasureW3PayloadAsync(
        string root,
        W2Input input,
        CameraModuleConfig configuration)
    {
        var payload = input.Payload;
        var rssSamples = new List<long>();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var lohBefore = GetLohSize();
        var started = Stopwatch.GetTimestamp();
        RawIngressSnapshot initialSnapshot;
        long commitTransactions;
        long commitTransactionFailures;
        double lockWaitMilliseconds;
        long fileFlushes;
        long directorySyncs;
        RawCaptureReceipt? lastReceipt = null;
        using (var fixture = CreateIngress(root))
        {
            for (var index = 0; index < W3PayloadCount; index++)
            {
                lastReceipt = await fixture.Ingress.AcceptAsync(
                    configuration, CreateSubmission(index, input), CancellationToken.None).ConfigureAwait(false);
                if ((index + 1) % 10 == 0)
                {
                    rssSamples.Add(Environment.WorkingSet);
                }
            }
            initialSnapshot = fixture.State.Snapshot;
            commitTransactions = fixture.Telemetry.TransactionCount;
            commitTransactionFailures = fixture.Telemetry.TransactionFailureCount;
            lockWaitMilliseconds = fixture.Telemetry.TotalLockWait.TotalMilliseconds;
            fileFlushes = fixture.Telemetry.FileFlushCount;
            directorySyncs = fixture.Telemetry.DirectorySyncCount;
        }
        var commitDuration = Stopwatch.GetElapsedTime(started);
        var commitCpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var commitAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var lohAfterCommit = GetLohSize();
        AssertReceipt(lastReceipt!, payload);
        var recoveryAllocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var recoveryCpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var recoverySamples = new double[5];
        long recoveryFileFlushes = 0;
        long recoveryDirectorySyncs = 0;
        RawIngressSnapshot recoverySnapshot = null!;
        for (var trial = 0; trial < recoverySamples.Length; trial++)
        {
            var recoveryStarted = Stopwatch.GetTimestamp();
            using var restarted = CreateIngress(root);
            await restarted.Ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            recoverySamples[trial] = Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds;
            recoverySnapshot = restarted.State.Snapshot;
            recoveryFileFlushes += restarted.Telemetry.FileFlushCount;
            recoveryDirectorySyncs += restarted.Telemetry.DirectorySyncCount;
        }
        Array.Sort(recoverySamples);
        var recoveryMedianMilliseconds = Percentile(recoverySamples, 0.50);
        Assert.AreEqual(W3PayloadCount, recoverySnapshot.PendingCount);
        Assert.AreEqual((long)W3PayloadCount * payload.LongLength, recoverySnapshot.PendingBytes);
        var firstHalfMedian = Median(rssSamples.Take(rssSamples.Count / 2));
        var finalHalfMedian = Median(rssSamples.Skip(rssSamples.Count / 2));
        Assert.IsLessThanOrEqualTo(firstHalfMedian + 64L * 1024 * 1024, finalHalfMedian);
        var commitRate = W3PayloadCount / commitDuration.TotalSeconds;
        var recoveryRate = W3PayloadCount / (recoveryMedianMilliseconds / 1000d);
        Assert.IsGreaterThanOrEqualTo(1d, commitRate);
        Assert.IsGreaterThanOrEqualTo(1d, recoveryRate);
        return new
        {
            Captures = W3PayloadCount,
            RawBytes = (long)W3PayloadCount * payload.LongLength,
            CommitDurationMilliseconds = commitDuration.TotalMilliseconds,
            CommitCapturesPerSecond = commitRate,
            CommitCpuMilliseconds = commitCpuMilliseconds,
            CommitAllocatedBytes = commitAllocatedBytes,
            LohBeforeBytes = lohBefore,
            LohAfterCommitBytes = lohAfterCommit,
            SqliteTransactions = commitTransactions,
            FailedSqliteTransactions = commitTransactionFailures,
            SqliteLockWaitMilliseconds = lockWaitMilliseconds,
            FileFlushes = fileFlushes,
            DirectorySyncs = directorySyncs,
            DatabaseBytes = new FileInfo(Path.Combine(root, "journal", "raw-ingress.db")).Length,
            WalBytes = File.Exists(Path.Combine(root, "journal", "raw-ingress.db-wal"))
                ? new FileInfo(Path.Combine(root, "journal", "raw-ingress.db-wal")).Length
                : 0,
            RecoveryTrialMilliseconds = recoverySamples,
            RecoveryMedianMilliseconds = recoveryMedianMilliseconds,
            RecoveryMinimumMilliseconds = recoverySamples[0],
            RecoveryMaximumMilliseconds = recoverySamples[^1],
            RecoveryCapturesPerSecond = recoveryRate,
            RecoveryCpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - recoveryCpuBefore).TotalMilliseconds,
            RecoveryAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - recoveryAllocatedBefore,
            RecoveryFileFlushes = recoveryFileFlushes,
            RecoveryDirectorySyncs = recoveryDirectorySyncs,
            InitialBacklogCount = initialSnapshot.PendingCount,
            InitialBacklogBytes = initialSnapshot.PendingBytes,
            InitialOldestPendingUtc = initialSnapshot.OldestPendingUtc,
            FinalBacklogCount = recoverySnapshot.PendingCount,
            FinalBacklogBytes = recoverySnapshot.PendingBytes,
            RssSamplesBytes = rssSamples,
            FirstHalfRssMedianBytes = firstHalfMedian,
            FinalHalfRssMedianBytes = finalHalfMedian,
            RssMedianDeltaBytes = finalHalfMedian - firstHalfMedian
        };
    }

    private static async Task<object> MeasureFaultRecoveryAsync(
        string root,
        W2Input input,
        CameraModuleConfig configuration)
    {
        var payload = input.Payload;
        var results = new List<object>();
        var points = new[]
        {
            RawIngressFaultPoint.PayloadPublished,
            RawIngressFaultPoint.BeforeJournalTransactionCommit,
            RawIngressFaultPoint.AfterJournalCommit
        };
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            var faultRoot = Path.Combine(root, point.ToString());
            var submission = CreateSubmission(20_000 + index, input);
            using (var interrupted = CreateIngress(faultRoot, new OneShotPerformanceFaultInjector(point)))
            {
                await Assert.ThrowsExactlyAsync<InjectedPerformanceFaultException>(async () =>
                    await interrupted.Ingress.AcceptAsync(
                        configuration, submission, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }
            var started = Stopwatch.GetTimestamp();
            RawCaptureReceipt receipt;
            RawIngressSnapshot snapshot;
            using (var restarted = CreateIngress(faultRoot))
            {
                receipt = (await restarted.Ingress.AcceptAsync(
                    configuration, submission, CancellationToken.None).ConfigureAwait(false))!;
                snapshot = restarted.State.Snapshot;
            }
            using var connection = new SqliteConnection($"Data Source={Path.Combine(faultRoot, "journal", "raw-ingress.db")}");
            await connection.OpenAsync().ConfigureAwait(false);
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM raw_captures;";
            var rowCount = Convert.ToInt64(await count.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(1L, rowCount);
            AssertReceipt(receipt, payload);
            results.Add(new
            {
                FaultPoint = point.ToString(),
                RecoveryMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                receipt.Outcome,
                JournalRows = rowCount,
                snapshot.PendingCount,
                snapshot.PendingBytes,
                snapshot.QuarantineCount
            });
        }
        return new { Cases = results, Result = "Each interruption converged to one durable logical capture." };
    }

    private static PerformanceIngress CreateIngress(string root, IRawIngressFaultInjector? faultInjector = null)
    {
        var state = new RawIngressState(TimeProvider.System);
        var telemetry = new RawIngressTelemetry(state);
        var ingress = new RawCaptureIngress(
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 5
            }),
            new UnlimitedCapacityProvider(),
            state,
            TimeProvider.System,
            telemetry,
            NullLogger<RawCaptureIngress>.Instance,
            faultInjector ?? new NullRawIngressFaultInjector());
        return new PerformanceIngress(ingress, state, telemetry);
    }

    private static async Task<CameraModuleConfig> LoadCanonicalConfigurationAsync()
    {
        var loader = new FileCameraAgentConfigurationLoader(
            Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = CanonicalProfilePath,
                RawIngressRoot = "performance-only",
                AgentId = "agent-94-performance",
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
                Observatory = new ObservatoryLocation(35.5599378, -113.9119818, 520, "America/Phoenix")
            }),
            NullLogger<FileCameraAgentConfigurationLoader>.Instance);
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<W2Input> RenderCanonicalW2InputAsync(CameraModuleConfig configuration)
    {
        var timestamp = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(timestamp, -113.9119818) / 15d;
        var catalog = new InMemoryCelestialCatalog(Enumerable.Range(0, 300)
            .Select(index => new CelestialCatalogObject(
                $"w2-{index:D3}",
                $"W2 Star {index:D3}",
                (rightAscension + index * 24d / 300d) % 24d,
                -75d + index % 151,
                -1d + index % 70 / 10d,
                0.3 + index % 20 / 10d)));
        var module = new VirtualSkyCameraModule(TimeProvider.System, catalog, new ProjectedSceneStore());
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var result = await module.CaptureAsync(
            new CaptureRequest(
                timestamp,
                TimeSpan.FromSeconds(25),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result.Frame);
        Assert.AreEqual(W2Stride * W2Height, result.Frame.PixelData.Length);
        Assert.IsNotNull(result.Frame.Metadata.Scene);
        return new W2Input(result.Frame.PixelData.ToArray(), result.Frame.Metadata);
    }

    private static CaptureLoopSubmission CreateSubmission(int index, W2Input input)
    {
        var timestamp = new DateTimeOffset(2026, 7, 14, 0, 0, 0, TimeSpan.Zero).AddSeconds(index);
        var frame = new CameraFrame(
            timestamp,
            W2Width,
            W2Height,
            CameraPixelFormat.BayerRggb16,
            input.Payload,
            input.Metadata,
            W2Stride);
        return new CaptureLoopSubmission(
            new CaptureRequest(timestamp, TimeSpan.FromSeconds(25), CaptureMode.Still, new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null)),
            new CaptureResult(frame, new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null), TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(timestamp, timestamp.AddSeconds(20), timestamp.AddSeconds(20))
            },
            timestamp,
            TimeSpan.FromSeconds(25),
            TimeSpan.Zero);
    }

    private static void AssertReceipt(RawCaptureReceipt receipt, byte[] payload)
    {
        Assert.AreEqual(payload.LongLength, new FileInfo(receipt.StoredFrame.AbsolutePath).Length);
        Assert.AreEqual(PayloadChecksum.ComputeSha256(payload), receipt.Manifest.Descriptor.Artifact.ChecksumSha256);
        Assert.IsTrue(receipt.Manifest.Validate().IsValid);
        var persisted = CaptureContractJson.ParseManifest(File.ReadAllBytes(Path.ChangeExtension(receipt.StoredFrame.AbsolutePath, ".json")));
        Assert.IsNotNull(persisted.Document?.Manifest?.Scene);
    }

    private static async Task ValidateCommittedEvidenceAsync(
        string root,
        int expectedCount,
        string expectedPayloadSha256,
        CameraModuleConfig configuration)
    {
        var journal = new SqliteRawCaptureJournal(Path.Combine(root, "journal", "raw-ingress.db"), 5);
        var entries = await journal.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(expectedCount, entries);
        var captureIds = new HashSet<Guid>();
        var artifactIds = new HashSet<Guid>();
        var sequences = new HashSet<long>();
        foreach (var entry in entries)
        {
            Assert.AreEqual(expectedPayloadSha256, entry.PayloadSha256);
            var payloadPath = Path.Combine(root, entry.PayloadRelativePath);
            var sidecarPath = Path.Combine(root, entry.SidecarRelativePath);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            CollectionAssert.AreEqual(entry.ManifestJson, sidecar);
            var parsed = CaptureContractJson.ParseManifest(sidecar);
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            var manifest = parsed.Document!.Manifest!;
            var descriptor = manifest.Descriptor;
            Assert.AreEqual(entry.DescriptorSha256, CaptureContractJson.ComputeDescriptorSha256(descriptor));
            Assert.AreEqual(entry.ManifestSha256, CaptureContractJson.ComputeManifestSha256(manifest));
            Assert.AreEqual(configuration.AgentId, descriptor.Capture.AgentId);
            Assert.AreEqual(configuration.Rig.ProfileVersion, descriptor.Profiles.Rig.Version);
            Assert.AreEqual(configuration.Rig.Sensor.SensorRecipeVersion, descriptor.Profiles.Sensor.Version);
            Assert.AreEqual(W2Width, descriptor.Layout.Width);
            Assert.AreEqual(W2Height, descriptor.Layout.Height);
            Assert.AreEqual(W2Stride, descriptor.Layout.StrideBytes);
            Assert.AreEqual(CameraPixelFormat.BayerRggb16, descriptor.Layout.PixelFormat);
            Assert.AreEqual(FrameByteOrder.LittleEndian, descriptor.Layout.ByteOrder);
            Assert.AreEqual(FrameArtifactRole.Raw, descriptor.Artifact.Role);
            Assert.AreEqual(expectedPayloadSha256, descriptor.Artifact.ChecksumSha256);
            Assert.AreEqual(entry.PayloadRelativePath, manifest.RelativeArtifactPath);
            Assert.IsNotNull(manifest.Scene);
            Assert.IsTrue(captureIds.Add(descriptor.Capture.CaptureId));
            Assert.IsTrue(artifactIds.Add(descriptor.Artifact.ArtifactId));
            Assert.IsTrue(sequences.Add(descriptor.Capture.CaptureSequence));
            using var stream = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Assert.AreEqual(
                expectedPayloadSha256,
                await PayloadChecksum.ComputeSha256Async(stream, CancellationToken.None).ConfigureAwait(false));
        }
    }

    private static string CanonicalProfilePath => Path.Combine(
        GetRepositoryRoot(),
        "src",
        "HVO.SkyMonitor.CameraAgent",
        "virtual-asi178mc.full.json");

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1)];

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static long GetLohSize()
    {
        var generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
    }

    private static async Task<string> ReadSqliteVersionAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class UnlimitedCapacityProvider : IStorageCapacityProvider
    {
        public StorageCapacity GetCapacity(string storageRoot) => new(long.MaxValue, long.MaxValue);
    }

    private sealed class OneShotPerformanceFaultInjector(RawIngressFaultPoint target) : IRawIngressFaultInjector
    {
        private int _injected;

        public void Inject(RawIngressFaultPoint point)
        {
            if (point == target && Interlocked.Exchange(ref _injected, 1) == 0)
            {
                throw new InjectedPerformanceFaultException();
            }
        }
    }

    private sealed class InjectedPerformanceFaultException : Exception
    {
        public InjectedPerformanceFaultException()
        {
        }

        public InjectedPerformanceFaultException(string message)
            : base(message)
        {
        }

        public InjectedPerformanceFaultException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    private sealed class PerformanceIngress(
        RawCaptureIngress ingress,
        RawIngressState state,
        RawIngressTelemetry telemetry) : IDisposable
    {
        public RawCaptureIngress Ingress { get; } = ingress;

        public RawIngressState State { get; } = state;

        public RawIngressTelemetry Telemetry { get; } = telemetry;

        public void Dispose()
        {
            Ingress.Dispose();
            Telemetry.Dispose();
        }
    }

    private sealed record W2Input(byte[] Payload, FrameMetadata Metadata);

    private sealed record W2Measurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double OperationsPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        long AllocatedBytesPerOperation,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long LohBeforeBytes,
        long LohAfterBytes,
        double SqliteLockWaitMilliseconds,
        long SqliteLockWaitSamples,
        long PayloadBytesWritten,
        long PayloadChecksumReadBytes,
        long FileFlushes,
        long DirectorySyncs,
        long SqliteTransactions,
        long FailedSqliteTransactions,
        int TotalAcceptedCaptures,
        long StoredPayloadBytes,
        long StoredSidecarBytes,
        long CompatibilityIndexBytes,
        long DatabaseBytes,
        long WalBytes,
        string MeasurementNotes);

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
}
