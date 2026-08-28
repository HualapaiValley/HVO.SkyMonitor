using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Distribution;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class DurableCaptureDistributionPerformanceTests
{
    private const int W2Width = 3096;
    private const int W2Height = 2080;
    private const int W2Stride = W2Width * 2;
    private const int W2PayloadBytes = 12_879_360;
    private const int WarmupCount = 5;
    private const int MeasuredCount = 30;
    private const int W3MetadataCount = 10_000;
    private const int W3LegacyContextCount = 1_000;
    private const int W3PayloadCount = 100;
    private const long W3PayloadBytes = 1_287_936_000;
    private const int VirtualSkySeed = 2025;
    private const string ExpectedProfileSha256 = "227FB3C0484AB5BBAFC4CA3674EA0D68B473315EBDD63F547CB2851D0A00F499";
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions LaneContextOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task W2W3MAndBlockedLane_DurableLaneEvidence()
    {
        var repositoryRoot = GetRepositoryRoot();
        var git = await ReadGitEvidenceAsync(repositoryRoot).ConfigureAwait(false);
        var run = ReadEvidenceRun(git);
        ValidateRunSequence(run);
        Assert.IsTrue(System.Runtime.GCSettings.IsServerGC, "Issue #257 evidence requires Server GC.");
        Assert.AreEqual(
            "0",
            Environment.GetEnvironmentVariable("DOTNET_GCDynamicAdaptationMode"),
            "Issue #257 allocation deltas require DATAS-disabled Server GC.");
        var gcDynamicAdaptationMode = ReadGcDynamicAdaptationMode();
        Assert.AreEqual(0L, gcDynamicAdaptationMode, "The runtime must authenticate DATAS-disabled Server GC.");
        var revision = run.Revision;
        var outputDirectory = run.OutputDirectory;
        var workRoot = Path.Combine(outputDirectory, "performance-work");
        Directory.CreateDirectory(outputDirectory);
        Assert.IsFalse(Directory.Exists(workRoot), "A prior partial durable-lane run must not be overwritten.");
        Directory.CreateDirectory(workRoot);

        using var runtimeSignals = new RuntimeSignalCollector();
        try
        {
            var profileBytes = await File.ReadAllBytesAsync(CanonicalProfilePath).ConfigureAwait(false);
            var profileSha256 = Convert.ToHexString(SHA256.HashData(profileBytes));
            Assert.AreEqual(ExpectedProfileSha256, profileSha256, "The canonical W2 profile changed.");
            var configuration = await LoadCanonicalConfigurationAsync().ConfigureAwait(false);
            var input = await RenderCanonicalW2InputAsync(configuration).ConfigureAwait(false);
            Assert.AreEqual(W2PayloadBytes, input.Payload.Length);

            var w2 = await MeasureW2Async(
                Path.Combine(workRoot, "w2"), input, configuration, runtimeSignals).ConfigureAwait(false);
            var w3m = await MeasureW3MetadataAsync(
                Path.Combine(workRoot, "w3m"), input, configuration).ConfigureAwait(false);
            LiveScenarioMeasurement unblocked;
            LiveScenarioMeasurement blocked;
            if (run.Order == "UB")
            {
                unblocked = await MeasureLiveScenarioAsync(
                    Path.Combine(workRoot, "w3p-unblocked"), input, configuration, blocked: false, runtimeSignals).ConfigureAwait(false);
                blocked = await MeasureLiveScenarioAsync(
                    Path.Combine(workRoot, "w3p-blocked"), input, configuration, blocked: true, runtimeSignals).ConfigureAwait(false);
            }
            else
            {
                blocked = await MeasureLiveScenarioAsync(
                    Path.Combine(workRoot, "w3p-blocked"), input, configuration, blocked: true, runtimeSignals).ConfigureAwait(false);
                unblocked = await MeasureLiveScenarioAsync(
                    Path.Combine(workRoot, "w3p-unblocked"), input, configuration, blocked: false, runtimeSignals).ConfigureAwait(false);
            }

            runtimeSignals.RecordObservableInstruments();
            var runtimeSnapshot = runtimeSignals.Snapshot();
            ValidateRuntimeSignals(runtimeSnapshot);
            Assert.IsFalse(runtimeSnapshot.EventIds.Contains(2059), "A lane service required forced shutdown.");
            Assert.IsFalse(runtimeSnapshot.EventIds.Any(static eventId => eventId is >= 2060 and <= 2062),
                "A claim, handler, or lease failure occurred during issue #257 evidence.");
            Assert.IsTrue(runtimeSnapshot.EventIds.Contains(2058), "Graceful lane drain was not observed.");

            var evidence = new
            {
                Issue = 257,
                Trial = run.Trial,
                Revision = revision,
                Provenance = new
                {
                    Candidate = git,
                    TestAssembly = ReadAssemblyIdentity(typeof(DurableCaptureDistributionPerformanceTests).Assembly, revision),
                    ProductionAssembly = ReadAssemblyIdentity(typeof(SqliteCaptureLaneStore).Assembly, revision)
                },
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    RuntimeVersion = Environment.Version.ToString(),
                    Sdk = ReadPinnedSdkVersion(repositoryRoot),
                    Configuration = BuildConfiguration,
                    Environment.ProcessorCount,
                    ProcessorModel = ReadProcessorModel(),
                    TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    StorageFormat = new DriveInfo(Path.GetPathRoot(outputDirectory)!).DriveFormat,
                    ServerGc = System.Runtime.GCSettings.IsServerGC,
                    GcDynamicAdaptationMode = gcDynamicAdaptationMode,
                    SqliteVersion = await ReadSqliteVersionAsync().ConfigureAwait(false),
                    ExecutionCommand = $"DOTNET_gcServer=1 DOTNET_GCDynamicAdaptationMode=0 HVO_ISSUE_257_EVIDENCE=1 HVO_EVIDENCE_REVISION={revision} HVO_EVIDENCE_TRIAL={run.Trial} HVO_ISSUE_257_ORDER={run.Order} HVO_ISSUE_257_OUTPUT_ROOT={run.OutputRoot} dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --filter FullyQualifiedName~DurableCaptureDistributionPerformanceTests.W2W3MAndBlockedLane_DurableLaneEvidence"
                },
                Workload = new
                {
                    Ids = new[] { "W2", "W3M", "W3P-blocked-lane" },
                    Profile = "src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json",
                    ProfileSha256 = profileSha256,
                    FixtureTimeUtc = CanonicalTime,
                    VirtualSkySeed,
                    DeterministicCatalogObjects = 300,
                    Width = W2Width,
                    Height = W2Height,
                    StrideBytes = W2Stride,
                    Format = CameraPixelFormat.BayerRggb16.ToString(),
                    PayloadBytes = W2PayloadBytes,
                    WarmupCount,
                    MeasuredCount,
                    W3MetadataCount,
                    W3PayloadCount,
                    W3PayloadBytes,
                    Concurrency = 1,
                    PollIntervalMilliseconds = 100
                },
                Baseline = new
                {
                    Comparison = "N/A",
                    Reason = "Issue #95 used a different canonical profile identity and raw-ingress schema; its historical values are not regression gates.",
                    Disposition = "This run establishes an absolute current-profile baseline for later equivalent comparisons."
                },
                Budgets = new
                {
                    MinimumThroughputPerSecond = 1.0,
                    BlockedLatencyCausalDisposition = "N/A: scenario-position effect confounds blocked-vs-unblocked latency; paired deltas are diagnostic only.",
                    MaximumRssMedianGrowthBytes = 64L * 1024 * 1024,
                    MinimumRequiredDrainCapturesPerSecond = 1.0,
                    MinimumOptionalRecoveryCapturesPerSecond = 1.0
                },
                W2 = w2,
                W3M = w3m,
                BlockedComparison = new
                {
                    ExecutionOrder = run.Order == "UB"
                        ? new[] { "unblocked", "blocked" }
                        : new[] { "blocked", "unblocked" },
                    Unblocked = unblocked,
                    Blocked = blocked,
                    AcceptP95PairedDeltaMilliseconds = blocked.AcceptP95Milliseconds - unblocked.AcceptP95Milliseconds,
                    AcceptP95PairedDeltaPercent = PercentChange(unblocked.AcceptP95Milliseconds, blocked.AcceptP95Milliseconds),
                    StandardAckP95PairedDeltaMilliseconds = blocked.StandardAckP95Milliseconds - unblocked.StandardAckP95Milliseconds,
                    StandardAckP95PairedDeltaPercent = PercentChange(
                        unblocked.StandardAckP95Milliseconds, blocked.StandardAckP95Milliseconds),
                    AggregationPolicy = "Diagnostic pair; final disposition requires all five order-balanced per-trial p95 pairs."
                },
                Correctness = new
                {
                    PayloadSha256 = PayloadChecksum.ComputeSha256(input.Payload),
                    PersistedPayloadCopyCount = 0,
                    StandardEvidenceReloadBytes = W3PayloadBytes * 2,
                    MaximumConcurrentStandardEvidencePayloadsPerScenario = 1,
                    PayloadFilesPerCapture = 1,
                    W2ReferenceLaneRowsPerCapture = 3,
                    W3MReferenceLaneRows = 30_000,
                    W3PRawBytes = W3PayloadBytes,
                    LaneWorkContainsPayloadOrBlob = false,
                    Result = "Checksums, manifest lineage, unique identities, canonical initialization, reference-only fan-out, durable completion, optional isolation, restart discovery, and graceful drain were asserted."
                }
            };
            await WriteNewJsonAsync(
                Path.Combine(outputDirectory, "capture-lanes-performance.json"), evidence).ConfigureAwait(false);

            var runtimeEvidence = new
            {
                Issue = 257,
                Trial = run.Trial,
                Revision = revision,
                ExpectedLogEventRanges = new[]
                {
                    new { Minimum = 2050, Maximum = 2059 },
                    new { Minimum = 2060, Maximum = 2063 }
                },
                ObservedLogEvents = runtimeSnapshot.EventIds
                    .Where(static eventId => eventId is >= 2050 and <= 2063)
                    .Select(eventId => new { EventId = eventId, Name = runtimeSnapshot.EventNames[eventId] })
                    .ToArray(),
                Metrics = new
                {
                    Meter = CaptureLaneTelemetry.MeterName,
                    PublishedInstruments = runtimeSnapshot.PublishedInstruments,
                    ObservedInstruments = runtimeSnapshot.MetricSampleCounts,
                    TagKeys = runtimeSnapshot.MetricTagKeys,
                    Cardinality = "Only bounded lane, required, result, outcome, and reason dimensions were observed; no IDs or paths are tags."
                },
                Activities = new
                {
                    Source = CaptureLaneTelemetry.ActivitySourceName,
                    Names = runtimeSnapshot.ActivityNames,
                    TagKeys = runtimeSnapshot.ActivityTagKeys
                },
                LaneStateSnapshots = new
                {
                    UnblockedBeforeStop = unblocked.BeforeReleaseSnapshot,
                    UnblockedFinal = unblocked.FinalSnapshot,
                    BlockedBeforeRelease = blocked.BeforeReleaseSnapshot,
                    BlockedFinal = blocked.FinalSnapshot
                },
                FinalDurableState = new
                {
                    W3MRawRows = w3m.RawRows,
                    W3MLaneRows = w3m.LaneWorkRows,
                    W3MContextRows = w3m.ContextRows,
                    UnblockedCompletedRows = unblocked.FinalCompletedLaneRows,
                    BlockedCompletedRows = blocked.FinalCompletedLaneRows,
                    BlockedPendingRows = blocked.FinalPendingLaneRows,
                    BlockedQuarantinedRows = blocked.FinalQuarantinedLaneRows,
                    ForcedShutdownObserved = runtimeSnapshot.EventIds.Contains(2059)
                }
            };
            await WriteNewJsonAsync(
                Path.Combine(outputDirectory, "runtime-signals.json"), runtimeEvidence).ConfigureAwait(false);
            await WriteNewJsonAsync(
                Path.Combine(outputDirectory, "capture-lanes-manifest.json"),
                new
                {
                    Issue = 257,
                    Trial = run.Trial,
                    Revision = revision,
                    Artifacts = new[]
                    {
                        ReadFileIdentity(Path.Combine(outputDirectory, "capture-lanes-performance.json")),
                        ReadFileIdentity(Path.Combine(outputDirectory, "runtime-signals.json"))
                    }
                }).ConfigureAwait(false);
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
        CameraModuleConfig configuration,
        RuntimeSignalCollector runtimeSignals)
    {
        using var fixture = CreateIngressFixture(root, runtimeSignals);
        var receipts = new List<RawCaptureReceipt>(WarmupCount + MeasuredCount);
        for (var index = 0; index < WarmupCount; index++)
        {
            receipts.Add((await fixture.Ingress.AcceptAsync(
                configuration,
                CreateSubmission(index, input),
                CancellationToken.None).ConfigureAwait(false))!);
        }

        var samples = new double[MeasuredCount];
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var rssBefore = Environment.WorkingSet;
        var measuredStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < MeasuredCount; index++)
        {
            var started = Stopwatch.GetTimestamp();
            receipts.Add((await fixture.Ingress.AcceptAsync(
                configuration,
                CreateSubmission(index + WarmupCount, input),
                CancellationToken.None).ConfigureAwait(false))!);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var measuredDuration = Stopwatch.GetElapsedTime(measuredStarted);
        var cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        Assert.IsGreaterThanOrEqualTo(0L, allocatedBytes);
        var orderedSamples = samples.ToArray();
        Array.Sort(samples);
        var median = Percentile(samples, 0.50);
        var p95 = Percentile(samples, 0.95);
        var throughput = MeasuredCount / measuredDuration.TotalSeconds;
        Assert.IsGreaterThanOrEqualTo(1d, throughput);

        var databasePath = DatabasePath(root);
        var correctness = await ValidateW2EvidenceAsync(root, receipts, input, configuration).ConfigureAwait(false);
        var checkpoint = await ReadWalCheckpointAsync(root).ConfigureAwait(false);
        Assert.AreEqual(0L, checkpoint.Busy);
        Assert.AreEqual(0L, fixture.RawTelemetry.TransactionFailureCount);
        Assert.AreEqual(0L, fixture.RawTelemetry.CheckpointFailureCount);
        runtimeSignals.RecordObservableInstruments();
        return new W2Measurement(
            TotalAcceptedCaptures: WarmupCount + MeasuredCount,
            MedianMilliseconds: median,
            P95Milliseconds: p95,
            MinimumMilliseconds: samples[0],
            MaximumMilliseconds: samples[^1],
            OrderedSamplesMilliseconds: orderedSamples,
            ThroughputPerSecond: throughput,
            CpuMilliseconds: cpuMilliseconds,
            AllocatedBytes: allocatedBytes,
            AllocatedBytesPerCapture: allocatedBytes / MeasuredCount,
            RssBeforeBytes: rssBefore,
            RssAfterBytes: Environment.WorkingSet,
            DatabaseBytes: new FileInfo(databasePath).Length,
            WalBytes: FileBytes(string.Concat(databasePath, "-wal")),
            PayloadFiles: correctness.PayloadFiles,
            PayloadBytesOnDisk: correctness.PayloadBytes,
            LaneWorkRows: correctness.LaneWorkRows,
            LaneContextRows: correctness.ContextRows,
            ObservedTransactions: fixture.RawTelemetry.TransactionCount,
            TransactionFailures: fixture.RawTelemetry.TransactionFailureCount,
            ObservedCheckpoints: fixture.RawTelemetry.CheckpointCount,
            CheckpointFailures: fixture.RawTelemetry.CheckpointFailureCount,
            FileFlushes: fixture.RawTelemetry.FileFlushCount,
            DirectorySyncs: fixture.RawTelemetry.DirectorySyncCount,
            WalCheckpointBusy: checkpoint.Busy,
            WalCheckpointLogFrames: checkpoint.LogFrames,
            WalCheckpointedFrames: checkpoint.CheckpointedFrames,
            ReferenceLaneRowsPerCapture: correctness.LaneWorkRows / (WarmupCount + MeasuredCount),
            UniqueCaptureIds: correctness.UniqueCaptureIds,
            UniqueArtifactIds: correctness.UniqueArtifactIds,
            UniqueSequences: correctness.UniqueSequences,
            RepresentativeManifestValidated: true,
            RepresentativeSceneLineageValidated: true,
            PersistedPayloadCopyCount: 0,
            LaneWorkContainsPayloadOrBlob: false);
    }

    private static async Task<W2Correctness> ValidateW2EvidenceAsync(
        string root,
        IReadOnlyList<RawCaptureReceipt> receipts,
        W2Input input,
        CameraModuleConfig configuration)
    {
        var expectedCount = WarmupCount + MeasuredCount;
        var expectedChecksum = PayloadChecksum.ComputeSha256(input.Payload);
        Assert.HasCount(expectedCount, receipts);
        var journal = new SqliteRawCaptureJournal(DatabasePath(root), busyTimeoutSeconds: 5);
        var entries = await journal.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(expectedCount, entries);
        var captureIds = new HashSet<Guid>();
        var artifactIds = new HashSet<Guid>();
        var sequences = new HashSet<long>();
        var payloadPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            Assert.AreEqual(expectedChecksum, entry.PayloadSha256);
            Assert.AreEqual(W2PayloadBytes, entry.PayloadLength);
            Assert.IsTrue(captureIds.Add(entry.CaptureId));
            Assert.IsTrue(artifactIds.Add(entry.ArtifactId));
            Assert.IsTrue(sequences.Add(entry.CaptureSequence));
            Assert.IsTrue(payloadPaths.Add(entry.PayloadRelativePath));
        }

        foreach (var entry in entries)
        {
            var payloadPath = Path.Combine(root, entry.PayloadRelativePath);
            var sidecarPath = Path.Combine(root, entry.SidecarRelativePath);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            CollectionAssert.AreEqual(entry.ManifestJson, sidecar);
            var parsed = CaptureContractJson.ParseManifest(sidecar);
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            var manifest = parsed.Document!.Manifest!;
            Assert.IsNotNull(manifest.Scene);
            Assert.AreEqual(entry.DescriptorSha256, CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor));
            Assert.AreEqual(entry.ManifestSha256, CaptureContractJson.ComputeManifestSha256(manifest));
            Assert.AreEqual(configuration.AgentId, manifest.Descriptor.Capture.AgentId);
            Assert.AreEqual(configuration.Rig.ProfileVersion, manifest.Descriptor.Profiles.Rig.Version);
            Assert.AreEqual(configuration.Rig.Sensor.SensorRecipeVersion, manifest.Descriptor.Profiles.Sensor.Version);
            Assert.AreEqual(W2Width, manifest.Descriptor.Layout.Width);
            Assert.AreEqual(W2Height, manifest.Descriptor.Layout.Height);
            Assert.AreEqual(W2Stride, manifest.Descriptor.Layout.StrideBytes);
            Assert.AreEqual(CameraPixelFormat.BayerRggb16, manifest.Descriptor.Layout.PixelFormat);
            Assert.AreEqual(expectedChecksum, manifest.Descriptor.Artifact.ChecksumSha256);
            using var payload = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            Assert.AreEqual(
                expectedChecksum,
                await PayloadChecksum.ComputeSha256Async(payload, CancellationToken.None).ConfigureAwait(false));
        }

        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        var rawRows = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false);
        var laneRows = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false);
        var contextRows = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false);
        Assert.AreEqual(expectedCount, rawRows);
        Assert.AreEqual(expectedCount * 3L, laneRows);
        Assert.AreEqual(expectedCount, contextRows);
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures r WHERE (SELECT COUNT(*) FROM capture_lane_work w WHERE w.raw_capture_row_id = r.raw_capture_row_id) != 3;").ConfigureAwait(false));
        await AssertReferenceOnlyLaneWorkAsync(connection).ConfigureAwait(false);

        var payloadFiles = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories).ToArray();
        Assert.HasCount(expectedCount, payloadFiles);
        Assert.AreEqual(expectedCount * (long)W2PayloadBytes, payloadFiles.Sum(static path => new FileInfo(path).Length));
        return new W2Correctness(
            payloadFiles.Length,
            payloadFiles.Sum(static path => new FileInfo(path).Length),
            laneRows,
            contextRows,
            captureIds.Count,
            artifactIds.Count,
            sequences.Count);
    }

    private static async Task ValidateLivePayloadEvidenceAsync(
        string root,
        W2Input input,
        CameraModuleConfig configuration)
    {
        var expectedChecksum = PayloadChecksum.ComputeSha256(input.Payload);
        var journal = new SqliteRawCaptureJournal(DatabasePath(root), busyTimeoutSeconds: 5);
        var entries = await journal.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.HasCount(W3PayloadCount, entries);
        foreach (var entry in entries)
        {
            Assert.AreEqual(W2PayloadBytes, entry.PayloadLength);
            Assert.AreEqual(expectedChecksum, entry.PayloadSha256);
            var payloadPath = Path.Combine(root, entry.PayloadRelativePath);
            var sidecarPath = Path.Combine(root, entry.SidecarRelativePath);
            var sidecar = await File.ReadAllBytesAsync(sidecarPath).ConfigureAwait(false);
            CollectionAssert.AreEqual(entry.ManifestJson, sidecar);
            var parsed = CaptureContractJson.ParseManifest(sidecar);
            Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
            var manifest = parsed.Document!.Manifest!;
            Assert.AreEqual(configuration.AgentId, manifest.Descriptor.Capture.AgentId);
            Assert.AreEqual(entry.CaptureId, manifest.Descriptor.Capture.CaptureId);
            Assert.AreEqual(entry.ArtifactId, manifest.Descriptor.Artifact.ArtifactId);
            Assert.AreEqual(entry.CaptureSequence, manifest.Descriptor.Capture.CaptureSequence);
            Assert.AreEqual(entry.DescriptorSha256, CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor));
            Assert.AreEqual(entry.ManifestSha256, CaptureContractJson.ComputeManifestSha256(manifest));
            Assert.AreEqual(configuration.Rig.ProfileVersion, manifest.Descriptor.Profiles.Rig.Version);
            Assert.AreEqual(configuration.Rig.Sensor.SensorRecipeVersion, manifest.Descriptor.Profiles.Sensor.Version);
            Assert.AreEqual(W2Width, manifest.Descriptor.Layout.Width);
            Assert.AreEqual(W2Height, manifest.Descriptor.Layout.Height);
            Assert.AreEqual(W2Stride, manifest.Descriptor.Layout.StrideBytes);
            Assert.AreEqual(CameraPixelFormat.BayerRggb16, manifest.Descriptor.Layout.PixelFormat);
            Assert.AreEqual(expectedChecksum, manifest.Descriptor.Artifact.ChecksumSha256);
            var stream = new FileStream(
                payloadPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                Assert.AreEqual(
                    expectedChecksum,
                    await PayloadChecksum.ComputeSha256Async(stream, CancellationToken.None).ConfigureAwait(false));
            }
        }
    }

    private static async Task<W3MetadataMeasurement> MeasureW3MetadataAsync(
        string root,
        W2Input input,
        CameraModuleConfig configuration)
    {
        Directory.CreateDirectory(Path.Combine(root, "journal"));
        var databasePath = DatabasePath(root);
        var distribution = CreateDistributionOptions();
        var hostOptions = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressSqliteBusyTimeoutSeconds = 5,
            CaptureDistribution = distribution
        });
        var policy = new CaptureLanePolicy(hostOptions);
        var transactionCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var journal = new SqliteRawCaptureJournal(
            databasePath,
            busyTimeoutSeconds: 5,
            transactionRecorder: (operation, success) =>
            {
                if (success)
                {
                    transactionCounts.AddOrUpdate(operation, 1, static (_, count) => count + 1);
                }
            },
            distributionOptions: distribution,
            laneFaultInjector: new NullCaptureLaneFaultInjector());
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var rssBefore = Environment.WorkingSet;
        var initializationStarted = Stopwatch.GetTimestamp();
        await journal.InitializeAsync(policy.Definitions, CancellationToken.None).ConfigureAwait(false);
        var initializationDuration = Stopwatch.GetElapsedTime(initializationStarted);
        var initializationCpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var initializationAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        var rssAfterInitialization = Environment.WorkingSet;
        Assert.IsGreaterThanOrEqualTo(0L, initializationAllocatedBytes);
        var databaseBytesBefore = FileBytes(databasePath);
        var walBytesBefore = FileBytes(string.Concat(databasePath, "-wal"));
        var insertion = await PopulateCanonicalDatabaseAsync(
            databasePath, input, configuration, policy.Definitions).ConfigureAwait(false);
        var databaseBytesAfter = FileBytes(databasePath);
        var walBytesAfter = FileBytes(string.Concat(databasePath, "-wal"));
        SqliteConnection.ClearAllPools();

        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        var rawRows = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false);
        var laneRows = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false);
        var contextRows = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_lane_contexts;").ConfigureAwait(false);
        var version = await ScalarLongAsync(connection, "PRAGMA user_version;").ConfigureAwait(false);
        var integrity = await ScalarStringAsync(connection, "PRAGMA integrity_check;").ConfigureAwait(false);
        Assert.AreEqual(W3MetadataCount, rawRows);
        Assert.AreEqual(W3MetadataCount * 3L, laneRows);
        Assert.AreEqual(W3LegacyContextCount, contextRows);
        Assert.AreEqual((long)SqliteRawCaptureJournal.CurrentSchemaVersion, version);
        Assert.AreEqual("ok", integrity);
        Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures r WHERE (SELECT COUNT(*) FROM capture_lane_work w WHERE w.raw_capture_row_id = r.raw_capture_row_id) != 3;").ConfigureAwait(false));
        await AssertReferenceOnlyLaneWorkAsync(connection).ConfigureAwait(false);
        await AssertAllLaneContextsRedactedAsync(connection, W3LegacyContextCount).ConfigureAwait(false);

        var queryPlan = await ReadStringsAsync(connection, "EXPLAIN QUERY PLAN SELECT w.work_id FROM capture_lane_work w JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id LEFT JOIN capture_lane_contexts c ON c.raw_capture_row_id = r.raw_capture_row_id WHERE w.lane_name = 'standard' AND w.state NOT IN ('completed', 'abandoned') ORDER BY w.agent_id, w.capture_sequence LIMIT 1;").ConfigureAwait(false);
        var queryPlanUsesIndex = queryPlan.Any(static detail =>
            detail.Contains("ix_capture_lane_work_ordered", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(queryPlanUsesIndex);
        var syntheticTraversalMilliseconds = await MeasureSyntheticIndexedTraversalAsync(connection).ConfigureAwait(false);
        var paginationStarted = Stopwatch.GetTimestamp();
        var pagedRows = await ReadAllLaneWorkPagesAsync(connection, "standard").ConfigureAwait(false);
        var paginationMilliseconds = Stopwatch.GetElapsedTime(paginationStarted).TotalMilliseconds;
        Assert.AreEqual(W3MetadataCount, pagedRows);

        await connection.CloseAsync().ConfigureAwait(false);
        var restartSamples = new double[5];
        var restartDiscoveredRows = new long[5];
        var restartCpuMilliseconds = 0d;
        var restartAllocatedBytes = 0L;
        var restartRssBefore = 0L;
        var restartRssAfter = 0L;
        for (var trial = 0; trial < restartSamples.Length; trial++)
        {
            var restartCpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var restartAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            var trialRssBefore = Environment.WorkingSet;
            var started = Stopwatch.GetTimestamp();
            var restarted = new SqliteRawCaptureJournal(
                databasePath,
                busyTimeoutSeconds: 5,
                distributionOptions: distribution,
                laneFaultInjector: new NullCaptureLaneFaultInjector());
            var store = new SqliteCaptureLaneStore(
                root,
                5,
                distribution,
                policy,
                TimeProvider.System,
                new NullCaptureLaneFaultInjector());
            await restarted.InitializeAsync(policy.Definitions, CancellationToken.None).ConfigureAwait(false);
            await store.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
            var backlogs = await store.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false);
            restartSamples[trial] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            restartCpuMilliseconds += (Process.GetCurrentProcess().TotalProcessorTime - restartCpuBefore).TotalMilliseconds;
            restartAllocatedBytes += GC.GetTotalAllocatedBytes(precise: false) - restartAllocatedBefore;
            restartRssBefore = trial == 0 ? trialRssBefore : restartRssBefore;
            restartRssAfter = Environment.WorkingSet;
            restartDiscoveredRows[trial] = backlogs.Sum(static backlog => backlog.PendingCount);
            Assert.AreEqual(W3MetadataCount * 3L, restartDiscoveredRows[trial]);
        }
        Assert.IsGreaterThanOrEqualTo(0L, restartAllocatedBytes);
        Array.Sort(restartSamples);
        var persistedPayloadCopies = Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count();

        return new W3MetadataMeasurement(
            RawRows: rawRows,
            LaneWorkRows: laneRows,
            ReferenceRowsPerCapture: laneRows / rawRows,
            ContextRows: contextRows,
            CanonicalInsertionMilliseconds: insertion.DurationMilliseconds,
            CanonicalInsertionRecordsPerSecond: W3MetadataCount / (insertion.DurationMilliseconds / 1000d),
            CanonicalInsertionTransactions: 1,
            CanonicalInsertionStatements: insertion.StatementCount,
            InitializationMilliseconds: initializationDuration.TotalMilliseconds,
            CanonicalInsertionRawRecordsPerSecond: W3MetadataCount / (insertion.DurationMilliseconds / 1000d),
            CanonicalInsertionReferenceRowsPerSecond: laneRows / (insertion.DurationMilliseconds / 1000d),
            CanonicalLaneStatements: W3MetadataCount * policy.Definitions.Count(static lane => lane.Enabled),
            CanonicalLaneRows: laneRows,
            ObservedProductionTransactions: transactionCounts.Values.Sum(),
            InitializationCpuMilliseconds: initializationCpuMilliseconds,
            InitializationAllocatedBytes: initializationAllocatedBytes,
            RssBeforeInitializationBytes: rssBefore,
            RssAfterInitializationBytes: rssAfterInitialization,
            CanonicalInsertionCpuMilliseconds: insertion.CpuMilliseconds,
            CanonicalInsertionAllocatedBytes: insertion.AllocatedBytes,
            RssBeforeCanonicalInsertionBytes: insertion.RssBefore,
            RssAfterCanonicalInsertionBytes: insertion.RssAfter,
            DatabaseBytesAfterInitialization: databaseBytesBefore,
            WalBytesAfterInitialization: walBytesBefore,
            DatabaseBytesAfterCanonicalInsertion: databaseBytesAfter,
            WalBytesAfterCanonicalInsertion: walBytesAfter,
            QueryPlan: queryPlan,
            QueryPlanUsesIndex: queryPlanUsesIndex,
            SyntheticIndexedTraversalRows: W3MetadataCount,
            SyntheticIndexedTraversalMilliseconds: syntheticTraversalMilliseconds,
            SyntheticIndexedTraversalRowsPerSecond: W3MetadataCount / (syntheticTraversalMilliseconds / 1000d),
            SyntheticIndexedTraversalLabel: "Direct test-owned SQL traversal; not production SqliteCaptureLaneStore.ClaimAsync latency.",
            PaginationPageSize: 257,
            PaginationRows: pagedRows,
            PaginationMilliseconds: paginationMilliseconds,
            UserVersion: version,
            IntegrityCheck: integrity,
            RestartTrialMilliseconds: restartSamples,
            RestartDiscoveryMedianMilliseconds: Percentile(restartSamples, 0.50),
            RestartDiscoveryMinimumMilliseconds: restartSamples[0],
            RestartDiscoveryMaximumMilliseconds: restartSamples[^1],
            RestartDiscoveredRowsPerTrial: restartDiscoveredRows,
            RestartCpuMilliseconds: restartCpuMilliseconds,
            RestartAllocatedBytes: restartAllocatedBytes,
            RssBeforeRestartBytes: restartRssBefore,
            RssAfterRestartBytes: restartRssAfter,
            PayloadFiles: persistedPayloadCopies,
            PersistedPayloadCopyCount: persistedPayloadCopies);
    }

    private static async Task<CanonicalInsertionMeasurement> PopulateCanonicalDatabaseAsync(
        string databasePath,
        W2Input input,
        CameraModuleConfig configuration,
        IReadOnlyList<CaptureLaneDefinition> laneDefinitions)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        var migrationManifestTemplate = ReconstructableCaptureContractTests.CreateManifest(
            CameraPixelFormat.Mono8,
            2,
            2,
            2,
            [1, 2, 3, 4]);

        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var rssBefore = Environment.WorkingSet;
        var started = Stopwatch.GetTimestamp();
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        using var sequence = connection.CreateCommand();
        sequence.Transaction = transaction;
        sequence.CommandText = "INSERT INTO raw_capture_sequences(agent_id, last_sequence) VALUES ('agent-95-w3m', $last);";
        sequence.Parameters.AddWithValue("$last", W3MetadataCount);
        await sequence.ExecuteNonQueryAsync().ConfigureAwait(false);

        using var assignment = connection.CreateCommand();
        assignment.Transaction = transaction;
        assignment.CommandText = "INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence) VALUES ($capture, $artifact, 'agent-95-w3m', $sequence);";
        var assignmentCapture = assignment.Parameters.Add("$capture", SqliteType.Text);
        var assignmentArtifact = assignment.Parameters.Add("$artifact", SqliteType.Text);
        var assignmentSequence = assignment.Parameters.Add("$sequence", SqliteType.Integer);

        using var capture = connection.CreateCommand();
        capture.Transaction = transaction;
        capture.CommandText = """
            INSERT INTO raw_captures(
                capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                manifest_sha256, payload_sha256, payload_length, payload_relative_path,
                sidecar_relative_path, manifest_json, exposure_started_unix_ms,
                durable_ingress_unix_ms, committed_unix_ms, state, retention_hold)
            VALUES ($capture, $artifact, 'agent-95-w3m', $sequence, $descriptor,
                $manifest, $payload, $payload_length, $payload_path, $sidecar_path,
                $manifest_json, $time, $time, $time, 'committed', 1);
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
        using var context = connection.CreateCommand();
        context.Transaction = transaction;
        context.CommandText = """
            INSERT INTO capture_lane_contexts(
                raw_capture_row_id, context_json, context_sha256, context_source)
            VALUES ($raw, $json, $sha, 'capture');
            """;
        var contextRaw = context.Parameters.Add("$raw", SqliteType.Integer);
        var contextJson = context.Parameters.Add("$json", SqliteType.Blob);
        var contextSha = context.Parameters.Add("$sha", SqliteType.Text);
        using var laneWork = connection.CreateCommand();
        laneWork.Transaction = transaction;
        laneWork.CommandText = """
            INSERT INTO capture_lane_work(
                raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered, state,
                attempt_count, available_unix_ms, created_unix_ms, updated_unix_ms)
            VALUES ($raw, $lane, 'agent-95-w3m', $sequence, $required, $ordered, 'pending',
                0, $time, $time, $time);
            """;
        var laneRaw = laneWork.Parameters.Add("$raw", SqliteType.Integer);
        var laneName = laneWork.Parameters.Add("$lane", SqliteType.Text);
        var laneSequence = laneWork.Parameters.Add("$sequence", SqliteType.Integer);
        var laneRequired = laneWork.Parameters.Add("$required", SqliteType.Integer);
        var laneOrdered = laneWork.Parameters.Add("$ordered", SqliteType.Integer);
        var laneTime = laneWork.Parameters.Add("$time", SqliteType.Integer);
        var payloadSha256 = PayloadChecksum.ComputeSha256([1, 2, 3, 4]);
        for (var index = 0; index < W3MetadataCount; index++)
        {
            var captureId = (index + 1).ToString("X32", System.Globalization.CultureInfo.InvariantCulture);
            var artifactId = (index + W3MetadataCount + 1).ToString("X32", System.Globalization.CultureInfo.InvariantCulture);
            assignmentCapture.Value = captureId;
            assignmentArtifact.Value = artifactId;
            assignmentSequence.Value = index + 1;
            await assignment.ExecuteNonQueryAsync().ConfigureAwait(false);

            parameters.Capture.Value = captureId;
            parameters.Artifact.Value = artifactId;
            parameters.Sequence.Value = index + 1;
            var timestamp = CanonicalTime.AddMilliseconds(index);
            var migrationManifest = migrationManifestTemplate with
            {
                RelativeArtifactPath = $"metadata/{index:D5}.bin",
                Descriptor = migrationManifestTemplate.Descriptor with
                {
                    Capture = migrationManifestTemplate.Descriptor.Capture with
                    {
                        AgentId = "agent-95-w3m",
                        CaptureSequence = index + 1,
                        CaptureId = Guid.Parse(captureId)
                    },
                    Timing = new CaptureTimingDescriptor(
                        timestamp,
                        timestamp,
                        timestamp,
                        timestamp,
                        timestamp),
                    Artifact = migrationManifestTemplate.Descriptor.Artifact with
                    {
                        ArtifactId = Guid.Parse(artifactId),
                        CreatedUtc = timestamp
                    }
                }
            };
            parameters.Descriptor.Value = CaptureContractJson.ComputeDescriptorSha256(migrationManifest.Descriptor);
            parameters.Manifest.Value = CaptureContractJson.ComputeManifestSha256(migrationManifest);
            parameters.Payload.Value = payloadSha256;
            parameters.PayloadLength.Value = 4;
            parameters.PayloadPath.Value = $"metadata/{index:D5}.bin";
            parameters.SidecarPath.Value = $"metadata/{index:D5}.json";
            parameters.ManifestJson.Value = CaptureContractJson.Serialize(migrationManifest);
            parameters.Time.Value = timestamp.ToUnixTimeMilliseconds();
            await capture.ExecuteNonQueryAsync().ConfigureAwait(false);
            foreach (var lane in laneDefinitions.Where(static lane => lane.Enabled))
            {
                laneRaw.Value = index + 1;
                laneName.Value = lane.Name;
                laneSequence.Value = index + 1;
                laneRequired.Value = lane.Required ? 1 : 0;
                laneOrdered.Value = lane.Ordered ? 1 : 0;
                laneTime.Value = timestamp.ToUnixTimeMilliseconds();
                await laneWork.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            if (index < W3LegacyContextCount)
            {
                var submission = CreateSubmission(index, input);
                var lightweight = submission with
                {
                    Result = submission.Result with { Frame = null, Artifacts = null }
                };
                var json = JsonSerializer.SerializeToUtf8Bytes(
                    new CaptureLaneEnvelope(configuration, lightweight),
                    LaneContextOptions);
                var sha256 = Convert.ToHexString(SHA256.HashData(json));
                var redacted = CaptureLaneEnvelopeSerializer.Redact(json, sha256);
                contextRaw.Value = index + 1;
                contextJson.Value = redacted.Json;
                contextSha.Value = redacted.Sha256;
                await context.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync().ConfigureAwait(false);
        var durationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var cpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore;
        var rssAfter = Environment.WorkingSet;
        Assert.IsGreaterThanOrEqualTo(0L, allocatedBytes);
        return new CanonicalInsertionMeasurement(
            durationMilliseconds,
            1 + W3MetadataCount * (2 + laneDefinitions.Count(static lane => lane.Enabled)) + W3LegacyContextCount,
            cpuMilliseconds,
            allocatedBytes,
            rssBefore,
            rssAfter);
    }

    private static async Task<LiveScenarioMeasurement> MeasureLiveScenarioAsync(
        string root,
        W2Input input,
        CameraModuleConfig canonicalConfiguration,
        bool blocked,
        RuntimeSignalCollector runtimeSignals)
    {
        Assert.IsFalse(Directory.Exists(root));
        Directory.CreateDirectory(root);
        var configuration = canonicalConfiguration with { AgentId = blocked ? "agent-257-blocked" : "agent-257-unblocked" };
        using var laneDurations = new LaneDurationCollector();
        using var fixture = CreateIngressFixture(root, runtimeSignals);
        using var standard = new StandardCaptureLaneHandler(
            new EmptyPipelineFactory(),
            new EvidenceLogger<StandardCaptureLaneHandler>(runtimeSignals),
            fixture.Ingress);
        using var outbox = new SqliteArtifactOutbox();
        var upload = new UploadCaptureLaneHandler(outbox, fixture.Options);
        var secondary = new GatedSecondaryLaneHandler(blocked);
        using var service = new CaptureDistributionService(
            new ConfigurationAccessor(configuration),
            fixture.Ingress,
            fixture.Ingress,
            fixture.Policy,
            [standard, upload, secondary],
            standard,
            fixture.Options,
            TimeProvider.System,
            new NullCaptureLaneFaultInjector(),
            fixture.LaneTelemetry,
            fixture.LaneState,
            new EvidenceLogger<CaptureDistributionService>(runtimeSignals));

        var serviceStarted = false;
        var serviceStopped = false;
        try
        {
            await service.StartAsync(CancellationToken.None).ConfigureAwait(false);
            serviceStarted = true;
            var acceptSamples = new double[W3PayloadCount];
            var rssSamples = new List<long>(W3PayloadCount / 10);
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var rssBefore = Environment.WorkingSet;
            var burstStarted = Stopwatch.GetTimestamp();
            for (var index = 0; index < W3PayloadCount; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var receipt = await fixture.Ingress.AcceptAsync(
                    configuration,
                    CreateSubmission(index, input),
                    CancellationToken.None).ConfigureAwait(false);
                acceptSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Assert.IsNotNull(receipt);
                Assert.AreEqual(RawIngressOutcome.Committed, receipt.Outcome);
                service.NotifyCommittedCapture();
                if ((index + 1) % 10 == 0)
                {
                    rssSamples.Add(Environment.WorkingSet);
                }
            }
            var burstEnded = Stopwatch.GetTimestamp();
            var burstDuration = Stopwatch.GetElapsedTime(burstStarted, burstEnded);
            if (blocked)
            {
                await secondary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                Assert.IsLessThanOrEqualTo(burstEnded, secondary.EnteredTimestamp);
            }
            var commitCpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var orderedAcceptSamples = acceptSamples.ToArray();
            Array.Sort(acceptSamples);

            await WaitForCountAsync(
                root,
                "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name IN ('standard', 'upload') AND state = 'completed';",
                W3PayloadCount * 2L,
                TimeSpan.FromMinutes(10)).ConfigureAwait(false);
            var requiredDrainDuration = Stopwatch.GetElapsedTime(burstStarted);
            var requiredDrainRate = W3PayloadCount / requiredDrainDuration.TotalSeconds;
            Assert.IsGreaterThanOrEqualTo(1d, requiredDrainRate);
            var standardCompletionLatencies = await ReadDoubleValuesAsync(
                root,
                "SELECT CAST(w.completed_unix_ms - r.committed_unix_ms AS REAL) FROM capture_lane_work w JOIN raw_captures r ON r.raw_capture_row_id = w.raw_capture_row_id WHERE w.lane_name = 'standard' AND w.state = 'completed' ORDER BY r.capture_sequence;").ConfigureAwait(false);
            Assert.HasCount(W3PayloadCount, standardCompletionLatencies);
            Assert.IsTrue(standardCompletionLatencies.All(static value => value >= 0));
            standardCompletionLatencies.Sort();
            var allStandardClaimDurations = laneDurations.ReadMilliseconds("camera_agent.lanes.claim.duration", "standard");
            var allStandardAckDurations = laneDurations.ReadMilliseconds("camera_agent.lanes.ack.duration", "standard");
            Assert.HasCount(W3PayloadCount, allStandardClaimDurations);
            Assert.HasCount(W3PayloadCount, allStandardAckDurations);
            var orderedStandardClaimDurations = allStandardClaimDurations
                .Skip(WarmupCount)
                .Take(MeasuredCount)
                .ToArray();
            var orderedStandardAckDurations = allStandardAckDurations
                .Skip(WarmupCount)
                .Take(MeasuredCount)
                .ToArray();
            Assert.HasCount(MeasuredCount, orderedStandardClaimDurations);
            Assert.HasCount(MeasuredCount, orderedStandardAckDurations);
            var standardClaimDurations = orderedStandardClaimDurations.Order().ToArray();
            var standardAckDurations = orderedStandardAckDurations.Order().ToArray();

            var rawRows = await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false);
            var rawBytes = await ScalarLongAtRootAsync(root, "SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures;").ConfigureAwait(false);
            var laneRows = await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false);
            var requiredCompleted = await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM capture_lane_work WHERE required = 1 AND state = 'completed';").ConfigureAwait(false);
            Assert.AreEqual(W3PayloadCount, rawRows);
            Assert.AreEqual(W3PayloadBytes, rawBytes);
            Assert.AreEqual(W3PayloadCount * 3L, laneRows);
            Assert.AreEqual(W3PayloadCount * 2L, requiredCompleted);
            using (var connection = await OpenDatabaseAsync(root).ConfigureAwait(false))
            {
                await AssertReferenceOnlyLaneWorkAsync(connection).ConfigureAwait(false);
                Assert.AreEqual(0L, await ScalarLongAsync(connection, "SELECT COUNT(*) FROM raw_captures r WHERE (SELECT COUNT(*) FROM capture_lane_work w WHERE w.raw_capture_row_id = r.raw_capture_row_id) != 3;").ConfigureAwait(false));
            }

            var optionalBeforeRelease = await ScalarLongAtRootAsync(
                root,
                "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state IN ('pending', 'leased');").ConfigureAwait(false);
            if (blocked)
            {
                Assert.AreEqual(W3PayloadCount, optionalBeforeRelease);
                Assert.IsTrue(secondary.Entered.Task.IsCompleted);
            }
            else
            {
                await WaitForCountAsync(
                    root,
                    "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'completed';",
                    W3PayloadCount,
                    TimeSpan.FromMinutes(2)).ConfigureAwait(false);
                optionalBeforeRelease = 0;
            }

            var beforeReleaseSnapshot = fixture.LaneState.Snapshot;
            runtimeSignals.RecordObservableInstruments();
            var restartSamples = blocked
                ? await MeasureOptionalRestartDiscoveryAsync(root, fixture, optionalBeforeRelease).ConfigureAwait(false)
                : [];

            var optionalRecoveryStarted = Stopwatch.GetTimestamp();
            secondary.Release();
            await WaitForCountAsync(
                root,
                "SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'secondary' AND state = 'completed';",
                W3PayloadCount,
                TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            var optionalRecoveryDuration = Stopwatch.GetElapsedTime(optionalRecoveryStarted);
            var optionalRecoveryRate = blocked
                ? W3PayloadCount / optionalRecoveryDuration.TotalSeconds
                : 0;
            if (blocked)
            {
                Assert.IsGreaterThanOrEqualTo(1d, optionalRecoveryRate);
            }

            await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            serviceStopped = true;
            var finalCompleted = await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM capture_lane_work WHERE state = 'completed';").ConfigureAwait(false);
            var finalPending = await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM capture_lane_work WHERE state IN ('pending', 'leased', 'retry_wait');").ConfigureAwait(false);
            var finalQuarantined = await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM capture_lane_work WHERE state = 'quarantined';").ConfigureAwait(false);
            var finalRetentionHeld = await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false);
            Assert.AreEqual(W3PayloadCount * 3L, finalCompleted);
            Assert.AreEqual(0L, finalPending);
            Assert.AreEqual(0L, finalQuarantined);
            Assert.AreEqual(0L, finalRetentionHeld);

            var payloadFiles = Directory.EnumerateFiles(Path.Combine(root, "frames"), "*.bin", SearchOption.AllDirectories).ToArray();
            var payloadBytesOnDisk = payloadFiles.Sum(static path => new FileInfo(path).Length);
            Assert.HasCount(W3PayloadCount, payloadFiles);
            Assert.AreEqual(W3PayloadBytes, payloadBytesOnDisk);
            await ValidateLivePayloadEvidenceAsync(root, input, configuration).ConfigureAwait(false);
            Assert.HasCount(
                W3PayloadCount,
                await outbox.GetRetentionHoldsAsync(root, CancellationToken.None).ConfigureAwait(false));
            var firstHalfRssMedian = Median(rssSamples.Take(rssSamples.Count / 2));
            var finalHalfRssMedian = Median(rssSamples.Skip(rssSamples.Count / 2));
            Assert.IsLessThanOrEqualTo(firstHalfRssMedian + 64L * 1024 * 1024, finalHalfRssMedian);
            runtimeSignals.RecordObservableInstruments();

            Array.Sort(restartSamples);
            var databasePath = DatabasePath(root);
            return new LiveScenarioMeasurement(
                Name: blocked ? "blocked" : "unblocked",
                Captures: W3PayloadCount,
                RawBytes: rawBytes,
                AcceptMedianMilliseconds: Percentile(acceptSamples, 0.50),
                AcceptP95Milliseconds: Percentile(acceptSamples, 0.95),
                AcceptMinimumMilliseconds: acceptSamples[0],
                AcceptMaximumMilliseconds: acceptSamples[^1],
                OrderedAcceptSamplesMilliseconds: orderedAcceptSamples,
                BurstMilliseconds: burstDuration.TotalMilliseconds,
                BurstCapturesPerSecond: W3PayloadCount / burstDuration.TotalSeconds,
                CommitCpuMilliseconds: commitCpuMilliseconds,
                CommitAllocationDisposition: "N/A: process-wide allocation counters are not reliable across concurrent lane-worker windows; W2 and W3M retain bounded allocation evidence.",
                RssBeforeBytes: rssBefore,
                RssSamplesBytes: rssSamples,
                FirstHalfRssMedianBytes: firstHalfRssMedian,
                FinalHalfRssMedianBytes: finalHalfRssMedian,
                RssMedianGrowthBytes: finalHalfRssMedian - firstHalfRssMedian,
                RequiredDrainMilliseconds: requiredDrainDuration.TotalMilliseconds,
                RequiredDrainCapturesPerSecond: requiredDrainRate,
                ProductionClaimWarmupSamples: WarmupCount,
                ProductionClaimMeasuredSamples: MeasuredCount,
                ProductionClaimTotalObservedSamples: allStandardClaimDurations.Length,
                StandardClaimMedianMilliseconds: Percentile(standardClaimDurations, 0.50),
                StandardClaimP95Milliseconds: Percentile(standardClaimDurations, 0.95),
                StandardClaimMaximumMilliseconds: standardClaimDurations[^1],
                OrderedProductionClaimSamplesMilliseconds: orderedStandardClaimDurations,
                StandardAckMedianMilliseconds: Percentile(standardAckDurations, 0.50),
                StandardAckP95Milliseconds: Percentile(standardAckDurations, 0.95),
                OrderedStandardAckSamplesMilliseconds: orderedStandardAckDurations,
                StandardCompletionEndToEndMedianMilliseconds: Percentile(standardCompletionLatencies, 0.50),
                StandardCompletionEndToEndP95Milliseconds: Percentile(standardCompletionLatencies, 0.95),
                OptionalPendingOrLeasedBeforeRelease: optionalBeforeRelease,
                OptionalRecoveryMilliseconds: blocked ? optionalRecoveryDuration.TotalMilliseconds : 0,
                OptionalRecoveryCapturesPerSecond: optionalRecoveryRate,
                RestartTrialMilliseconds: restartSamples,
                RestartDiscoveryMedianMilliseconds: restartSamples.Length == 0 ? 0 : Percentile(restartSamples, 0.50),
                RestartDiscoveryMinimumMilliseconds: restartSamples.Length == 0 ? 0 : restartSamples[0],
                RestartDiscoveryMaximumMilliseconds: restartSamples.Length == 0 ? 0 : restartSamples[^1],
                DatabaseBytes: FileBytes(databasePath),
                WalBytes: FileBytes(string.Concat(databasePath, "-wal")),
                PayloadFiles: payloadFiles.Length,
                PayloadBytesOnDisk: payloadBytesOnDisk,
                PersistedPayloadCopyCount: 0,
                StandardEvidenceReloadBytes: rawBytes,
                MaximumConcurrentStandardEvidencePayloads: 1,
                LaneWorkRows: laneRows,
                LaneWorkContainsPayloadOrBlob: false,
                RequiredCompletedRowsBeforeOptionalRelease: requiredCompleted,
                FinalCompletedLaneRows: finalCompleted,
                FinalPendingLaneRows: finalPending,
                FinalQuarantinedLaneRows: finalQuarantined,
                FinalRetentionHeldRows: finalRetentionHeld,
                BeforeReleaseSnapshot: beforeReleaseSnapshot,
                FinalSnapshot: fixture.LaneState.Snapshot,
                GracefulStop: true);
        }
        finally
        {
            secondary.Release();
            if (serviceStarted && !serviceStopped)
            {
                await service.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            }
        }
    }

    private static async Task<double[]> MeasureOptionalRestartDiscoveryAsync(
        string root,
        IngressFixture fixture,
        long expectedOptionalBacklog)
    {
        var samples = new double[5];
        for (var trial = 0; trial < samples.Length; trial++)
        {
            var journal = new SqliteRawCaptureJournal(
                DatabasePath(root),
                busyTimeoutSeconds: 5,
                distributionOptions: fixture.Options.Value.CaptureDistribution,
                laneFaultInjector: new NullCaptureLaneFaultInjector());
            var started = Stopwatch.GetTimestamp();
            await journal.InitializeAsync(fixture.Policy.Definitions, CancellationToken.None).ConfigureAwait(false);
            var backlogs = await fixture.Ingress.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false);
            samples[trial] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var optional = backlogs.Single(static backlog => backlog.Lane == "secondary");
            Assert.AreEqual(expectedOptionalBacklog, optional.PendingCount);
            Assert.AreEqual(0L, backlogs.Where(static backlog => backlog.Required).Sum(static backlog => backlog.PendingCount));
            Assert.AreEqual(W3PayloadCount, await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM raw_captures;").ConfigureAwait(false));
            Assert.AreEqual(W3PayloadCount * 3L, await ScalarLongAtRootAsync(root, "SELECT COUNT(*) FROM capture_lane_work;").ConfigureAwait(false));
        }
        return samples;
    }

    private static IngressFixture CreateIngressFixture(string root, RuntimeSignalCollector runtimeSignals)
    {
        var distribution = CreateDistributionOptions();
        var options = Options.Create(new CameraAgentHostOptions
        {
            RawIngressRoot = root,
            RawIngressReserveBytes = 0,
            RawIngressSqliteBusyTimeoutSeconds = 5,
            CaptureDistribution = distribution
        });
        var rawState = new RawIngressState(TimeProvider.System);
        var rawTelemetry = new RawIngressTelemetry(rawState);
        var policy = new CaptureLanePolicy(options);
        var laneState = new CaptureLaneState(TimeProvider.System, options);
        var laneTelemetry = new CaptureLaneTelemetry(laneState);
        var ingress = new RawCaptureIngress(
            options,
            new UnlimitedCapacityProvider(),
            rawState,
            TimeProvider.System,
            rawTelemetry,
            new EvidenceLogger<RawCaptureIngress>(runtimeSignals),
            new NullRawIngressFaultInjector(),
            policy,
            new NullCaptureLaneFaultInjector(),
            laneState,
            laneTelemetry);
        return new IngressFixture(ingress, rawTelemetry, policy, laneState, laneTelemetry, options);
    }

    private static CaptureDistributionOptions CreateDistributionOptions()
        => new()
        {
            UploadEnabled = true,
            PollIntervalMilliseconds = 100,
            ShutdownDrainSeconds = 60,
            RequiredMaximumPendingCount = 1_000_000,
            RequiredMaximumPendingBytes = 1_000_000_000_000,
            RequiredMaximumOldestAgeMinutes = 525_600,
            OptionalMaximumPendingCount = 1_000_000,
            OptionalMaximumPendingBytes = 1_000_000_000_000,
            OptionalMaximumOldestAgeMinutes = 525_600,
            SecondaryLanes = [new SecondaryCaptureLaneOptions { Name = "secondary", Enabled = true }]
        };

    private static async Task<CameraModuleConfig> LoadCanonicalConfigurationAsync()
    {
        var loader = new FileCameraAgentConfigurationLoader(
            Options.Create(new CameraAgentHostOptions
            {
                ConfigFilePath = CanonicalProfilePath,
                RawIngressRoot = "performance-only",
                AgentId = "agent-95-performance",
                CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled },
                Observatory = new ObservatoryLocation(35.5599378, -113.9119818, 520, "America/Phoenix")
            }),
            new EvidenceLogger<FileCameraAgentConfigurationLoader>(null));
        return await loader.LoadAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<W2Input> RenderCanonicalW2InputAsync(CameraModuleConfig configuration)
    {
        var rightAscension = AstronomyTime.LocalMeanSiderealDegrees(CanonicalTime, -113.9119818) / 15d;
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
                CanonicalTime,
                TimeSpan.FromSeconds(25),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(20), 150, null, null)),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(result.Frame);
        Assert.AreEqual(W2Width, result.Frame.Width);
        Assert.AreEqual(W2Height, result.Frame.Height);
        Assert.AreEqual(W2Stride, result.Frame.StrideBytes);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, result.Frame.PixelFormat);
        Assert.AreEqual(W2PayloadBytes, result.Frame.PixelData.Length);
        Assert.IsNotNull(result.Frame.Metadata.Scene);
        return new W2Input(result.Frame.PixelData.ToArray(), result.Frame.Metadata);
    }

    private static CaptureLoopSubmission CreateSubmission(int index, W2Input input)
    {
        var timestamp = CanonicalTime.AddSeconds(index);
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

    private static async Task AssertReferenceOnlyLaneWorkAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(capture_lane_work);";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var columns = new List<(string Name, string Type)>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            columns.Add((reader.GetString(1), reader.GetString(2)));
        }
        Assert.IsFalse(columns.Any(static column =>
            column.Name.Contains("payload", StringComparison.OrdinalIgnoreCase) ||
            column.Name.Contains("manifest", StringComparison.OrdinalIgnoreCase) ||
            column.Name.Contains("context", StringComparison.OrdinalIgnoreCase) ||
            column.Type.Contains("BLOB", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(columns.Any(static column => column.Name == "raw_capture_row_id"));
    }

    private static async Task AssertAllLaneContextsRedactedAsync(
        SqliteConnection connection,
        int expectedCount)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT context_json, context_sha256 FROM capture_lane_contexts ORDER BY raw_capture_row_id;";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var envelope = CaptureLaneEnvelopeSerializer.Deserialize(
                (byte[])reader.GetValue(0),
                reader.GetString(1));
            Assert.IsTrue(envelope.Configuration.DeploymentLocationRedacted);
            Assert.IsNull(envelope.Configuration.DeploymentLocation);
            Assert.AreEqual(new ObservatoryLocation(0, 0, 0, "UTC"), envelope.Configuration.Observatory);
            count++;
        }
        Assert.AreEqual(expectedCount, count);
    }

    private static async Task<int> ReadAllLaneWorkPagesAsync(SqliteConnection connection, string lane)
    {
        const int pageSize = 257;
        long after = 0;
        var count = 0;
        var identities = new HashSet<long>();
        while (true)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT work_id FROM capture_lane_work WHERE lane_name = $lane AND work_id > $after ORDER BY work_id LIMIT $limit;";
            command.Parameters.AddWithValue("$lane", lane);
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$limit", pageSize);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            var pageCount = 0;
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                after = reader.GetInt64(0);
                Assert.IsTrue(identities.Add(after));
                pageCount++;
                count++;
            }
            if (pageCount < pageSize)
            {
                return count;
            }
        }
    }

    private static async Task<double> MeasureSyntheticIndexedTraversalAsync(SqliteConnection connection)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText = """
            SELECT work_id
            FROM capture_lane_work
            WHERE lane_name = 'standard' AND state NOT IN ('completed', 'abandoned')
            ORDER BY agent_id, capture_sequence
            LIMIT 1;
            """;
        using var complete = connection.CreateCommand();
        complete.Transaction = transaction;
        complete.CommandText = "UPDATE capture_lane_work SET state = 'completed' WHERE work_id = $work;";
        var work = complete.Parameters.Add("$work", SqliteType.Integer);
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < W3MetadataCount; index++)
        {
            work.Value = Convert.ToInt64(
                await claim.ExecuteScalarAsync().ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.AreEqual(1, await complete.ExecuteNonQueryAsync().ConfigureAwait(false));
        }
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        await transaction.RollbackAsync().ConfigureAwait(false);
        Assert.IsGreaterThanOrEqualTo(1d, W3MetadataCount / (duration / 1000d));
        return duration;
    }

    private static async Task WaitForCountAsync(string root, string sql, long expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await ScalarLongAtRootAsync(root, sql).ConfigureAwait(false) == expected)
            {
                return;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        Assert.Fail($"Timed out waiting for durable row count {expected}.");
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only test-owned constant SQL is passed to this helper.")]
    private static async Task<List<double>> ReadDoubleValuesAsync(string root, string sql)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var values = new List<double>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            values.Add(reader.GetDouble(0));
        }
        return values;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only test-owned constant SQL is passed to this helper.")]
    private static async Task<IReadOnlyList<string>> ReadStringsAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var values = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            values.Add(reader.GetString(3));
        }
        return values;
    }

    private static async Task<long> ScalarLongAtRootAsync(string root, string sql)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        return await ScalarLongAsync(connection, sql).ConfigureAwait(false);
    }

    private static async Task<WalCheckpoint> ReadWalCheckpointAsync(string root)
    {
        using var connection = await OpenDatabaseAsync(root).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new WalCheckpoint(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={DatabasePath(root)};Default Timeout=5");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only test-owned constant SQL is passed to this helper.")]
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only test-owned constant SQL is passed to this helper.")]
    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void ValidateRuntimeSignals(RuntimeSignalSnapshot snapshot)
    {
        var expectedMetrics = new HashSet<string>(
        [
            "camera_agent.lanes.work.created",
            "camera_agent.lanes.claims",
            "camera_agent.lanes.completed",
            "camera_agent.lanes.retries",
            "camera_agent.lanes.quarantined",
            "camera_agent.lanes.abandoned",
            "camera_agent.lanes.wakeups",
            "camera_agent.lanes.claim.duration",
            "camera_agent.lanes.processing.duration",
            "camera_agent.lanes.ack.duration",
            "camera_agent.lanes.sqlite.lock_wait.duration",
            "camera_agent.lanes.accepting",
            "camera_agent.lanes.pending",
            "camera_agent.lanes.pending.bytes",
            "camera_agent.lanes.oldest.age",
            "camera_agent.lanes.leased",
            "camera_agent.lanes.pressure"
        ], StringComparer.Ordinal);
        var expectedActivities = new HashSet<string>(
        [
            "capture-lanes.initialize",
            "capture-lanes.claim",
            "capture-lanes.process",
            "capture-lanes.ack"
        ], StringComparer.Ordinal);
        var expectedTagKeys = new HashSet<string>(["lane", "required", "result", "outcome", "reason"], StringComparer.Ordinal);
        Assert.IsTrue(snapshot.PublishedInstruments.All(expectedMetrics.Contains));
        Assert.IsTrue(snapshot.MetricSampleCounts.Keys.All(expectedMetrics.Contains));
        Assert.IsTrue(snapshot.ActivityNames.All(expectedActivities.Contains));
        Assert.IsTrue(snapshot.MetricTagKeys.All(expectedTagKeys.Contains));
        Assert.IsTrue(snapshot.ActivityTagKeys.All(static key => key is "lane" or "result" or "outcome"));
        Assert.IsFalse(snapshot.MetricTagKeys.Any(static key => key.Contains("id", StringComparison.OrdinalIgnoreCase) || key.Contains("path", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(snapshot.ActivityTagKeys.Any(static key => key.Contains("id", StringComparison.OrdinalIgnoreCase) || key.Contains("path", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(snapshot.MetricSampleCounts.Count > 0);
        Assert.IsTrue(snapshot.ActivityNames.Count > 0);
    }

    private static string StableSha256(string prefix, int index)
        => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes($"{prefix}-{index:D5}")));

    private static double PercentChange(double baseline, double candidate)
        => baseline == 0 ? 0 : (candidate - baseline) * 100d / baseline;

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(percentile * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static long Median(IEnumerable<long> values)
    {
        var sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
    }

    private static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static string DatabasePath(string root) => Path.Combine(root, "journal", "raw-ingress.db");

    private static async Task<string> ReadSqliteVersionAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static long ReadGcDynamicAdaptationMode()
    {
        var configuration = GC.GetConfigurationVariables();
        if (!configuration.TryGetValue("GCDynamicAdaptationMode", out var value))
        {
            throw new InvalidOperationException("The runtime did not report GCDynamicAdaptationMode.");
        }
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadProcessorModel()
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (!File.Exists(cpuInfo))
        {
            return RuntimeInformation.ProcessArchitecture.ToString();
        }
        var line = File.ReadLines(cpuInfo).FirstOrDefault(static value =>
            value.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
        return line is null
            ? RuntimeInformation.ProcessArchitecture.ToString()
            : line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    private static string ReadPinnedSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json does not contain an SDK version.");
    }

    private static async Task<GitEvidence> ReadGitEvidenceAsync(string repositoryRoot)
    {
        var head = (await RunGitAsync(repositoryRoot, "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var branch = (await RunGitAsync(repositoryRoot, "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
        var status = await RunGitAsync(
            repositoryRoot, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        var diff = await RunGitAsync(repositoryRoot, "diff", "--binary", "HEAD", "--").ConfigureAwait(false);
        var untracked = await RunGitAsync(
            repositoryRoot, "ls-files", "--others", "--exclude-standard", "-z").ConfigureAwait(false);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes(diff));
        fingerprint.AppendData(Encoding.UTF8.GetBytes(untracked));
        foreach (var relativePath in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            fingerprint.AppendData(await File.ReadAllBytesAsync(
                Path.Combine(repositoryRoot, relativePath)).ConfigureAwait(false));
        }
        return new GitEvidence(
            head,
            branch,
            !string.IsNullOrWhiteSpace(status),
            Convert.ToHexString(fingerprint.GetHashAndReset()));
    }

    private static EvidenceRun ReadEvidenceRun(GitEvidence git)
    {
        if (!string.Equals(BuildConfiguration, "Release", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Issue #257 evidence requires a Release build.");
        }
        if (!string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_257_EVIDENCE"), "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Set HVO_ISSUE_257_EVIDENCE=1 for a claimable run.");
        }

        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        if (revision is null || revision.Length != 40 || !revision.All(Uri.IsHexDigit) ||
            !string.Equals(revision, git.Head, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HVO_EVIDENCE_REVISION must equal the exact 40-hex candidate HEAD.");
        }
        if (git.Dirty)
        {
            throw new InvalidOperationException("Issue #257 evidence requires a clean working tree.");
        }
        if (!int.TryParse(
                Environment.GetEnvironmentVariable("HVO_EVIDENCE_TRIAL"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var trial) || trial is < 1 or > 5)
        {
            throw new InvalidOperationException("HVO_EVIDENCE_TRIAL must be an integer from 1 through 5.");
        }

        var order = Environment.GetEnvironmentVariable("HVO_ISSUE_257_ORDER");
        if (order is not ("UB" or "BU"))
        {
            throw new InvalidOperationException("HVO_ISSUE_257_ORDER must be UB or BU.");
        }
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE_257_OUTPUT_ROOT");
        if (string.IsNullOrWhiteSpace(outputRoot) || !Path.IsPathFullyQualified(outputRoot))
        {
            throw new InvalidOperationException("HVO_ISSUE_257_OUTPUT_ROOT must be an absolute path.");
        }
        outputRoot = Path.GetFullPath(outputRoot);
        if (!string.Equals(
                Path.GetFileName(outputRoot.TrimEnd(Path.DirectorySeparatorChar)),
                revision,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("HVO_ISSUE_257_OUTPUT_ROOT must end with the candidate HEAD.");
        }
        var expectedOrder = trial % 2 == 1 ? "UB" : "BU";
        if (!string.Equals(order, expectedOrder, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Issue #257 trial {trial} requires durable scenario order {expectedOrder}.");
        }
        return new EvidenceRun(revision, trial, order, outputRoot, Path.Combine(outputRoot, $"trial-{trial:D2}"));
    }

    private static void ValidateRunSequence(EvidenceRun run)
    {
        var lanePath = Path.Combine(run.OutputDirectory, "capture-lanes-performance.json");
        var laneManifestPath = Path.Combine(run.OutputDirectory, "capture-lanes-manifest.json");
        var runtimePath = Path.Combine(run.OutputDirectory, "runtime-signals.json");
        var workRoot = Path.Combine(run.OutputDirectory, "performance-work");
        if (File.Exists(lanePath) || File.Exists(laneManifestPath) || File.Exists(runtimePath) || Directory.Exists(workRoot))
        {
            throw new InvalidOperationException("The durable-lane output for this trial already exists.");
        }

        var controlPath = Path.Combine(run.OutputDirectory, "capture-control-performance.json");
        var controlManifestPath = Path.Combine(run.OutputDirectory, "capture-control-manifest.json");
        if (run.Trial % 2 == 0)
        {
            if (Directory.Exists(run.OutputDirectory) && Directory.EnumerateFileSystemEntries(run.OutputDirectory).Any())
            {
                throw new InvalidOperationException("Even issue #257 trials must start with durable-lane evidence in an empty trial directory.");
            }
        }
        else if (!File.Exists(controlPath) || !File.Exists(controlManifestPath))
        {
            throw new InvalidOperationException("Odd issue #257 trials require completed capture-control evidence first.");
        }
    }

    private static FileIdentity ReadAssemblyIdentity(System.Reflection.Assembly assembly, string revision)
    {
        var informationalVersion = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), inherit: false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
            .Single()
            .InformationalVersion;
        if (!informationalVersion.EndsWith($"+{revision}", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Assembly {assembly.GetName().Name} was not built from candidate HEAD {revision}.");
        }
        var file = ReadFileIdentity(assembly.Location);
        return file with { InformationalVersion = informationalVersion };
    }

    private static FileIdentity ReadFileIdentity(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new FileIdentity(Path.GetFileName(path), bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task WriteNewJsonAsync<T>(string path, T value)
    {
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, value, EvidenceOptions).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> RunGitAsync(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start git for performance evidence attribution.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Git evidence attribution failed: {error}");
        }
        return output;
    }

    private static string CanonicalProfilePath => Path.Combine(
        GetRepositoryRoot(),
        "src",
        "HVO.SkyMonitor.CameraAgent",
        "virtual-asi178mc.full.json");

    private static DateTimeOffset CanonicalTime { get; } = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

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

    private sealed class EmptyPipelineFactory : ICaptureProcessingPipelineFactory
    {
        public CaptureProcessingGraph CreateGraph(CameraModuleConfig config) => new([]);

        public CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config)
            => throw new NotSupportedException();
    }

    private sealed class ConfigurationAccessor(CameraModuleConfig configuration) : ICameraAgentConfigurationAccessor
    {
        public bool IsConfigured => true;

        public void SetConfiguration(CameraModuleConfig config) => throw new NotSupportedException();

        public ValueTask<CameraModuleConfig> WaitForConfigurationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(configuration);
    }

    private sealed class GatedSecondaryLaneHandler(bool blocked) : ICaptureLaneHandler
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _enteredTimestamp;

        public string Lane => "secondary";

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long EnteredTimestamp => Interlocked.Read(ref _enteredTimestamp);

        public async ValueTask<CaptureLaneHandlerResult> HandleAsync(
            CaptureLaneHandlerContext context,
            CancellationToken cancellationToken)
        {
            Interlocked.CompareExchange(ref _enteredTimestamp, Stopwatch.GetTimestamp(), 0);
            Entered.TrySetResult();
            if (blocked)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            return CaptureLaneHandlerResult.Success;
        }

        public void Release() => _release.TrySetResult();
    }

    private sealed class IngressFixture(
        RawCaptureIngress ingress,
        RawIngressTelemetry rawTelemetry,
        CaptureLanePolicy policy,
        CaptureLaneState laneState,
        CaptureLaneTelemetry laneTelemetry,
        IOptions<CameraAgentHostOptions> options) : IDisposable
    {
        public RawCaptureIngress Ingress { get; } = ingress;

        public RawIngressTelemetry RawTelemetry { get; } = rawTelemetry;

        public CaptureLanePolicy Policy { get; } = policy;

        public CaptureLaneState LaneState { get; } = laneState;

        public CaptureLaneTelemetry LaneTelemetry { get; } = laneTelemetry;

        public IOptions<CameraAgentHostOptions> Options { get; } = options;

        public void Dispose()
        {
            Ingress.Dispose();
            RawTelemetry.Dispose();
            LaneTelemetry.Dispose();
            SqliteConnection.ClearAllPools();
        }
    }

    private sealed class EvidenceLogger<T>(RuntimeSignalCollector? collector) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            collector?.RecordEvent(eventId);
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed class RuntimeSignalCollector : IDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _publishedInstruments = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _metricSamples = new(StringComparer.Ordinal);
        private readonly HashSet<string> _metricTagKeys = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activityNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activityTagKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _eventNames = [];
        private readonly MeterListener _meterListener;
        private readonly ActivityListener _activityListener;

        public RuntimeSignalCollector()
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name != CaptureLaneTelemetry.MeterName)
                    {
                        return;
                    }
                    lock (_gate)
                    {
                        _publishedInstruments.Add(instrument.Name);
                    }
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) => RecordMetric(instrument.Name, tags));
            _meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) => RecordMetric(instrument.Name, tags));
            _meterListener.Start();

            _activityListener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == CaptureLaneTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _activityNames.Add(activity.OperationName);
                        foreach (var tag in activity.TagObjects)
                        {
                            _activityTagKeys.Add(tag.Key);
                        }
                    }
                }
            };
            ActivitySource.AddActivityListener(_activityListener);
        }

        public void RecordObservableInstruments() => _meterListener.RecordObservableInstruments();

        public void RecordEvent(EventId eventId)
        {
            if (eventId.Id == 0)
            {
                return;
            }
            lock (_gate)
            {
                _eventNames[eventId.Id] = eventId.Name ?? string.Empty;
            }
        }

        public RuntimeSignalSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new RuntimeSignalSnapshot(
                    _publishedInstruments.Order(StringComparer.Ordinal).ToArray(),
                    _metricSamples.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToDictionary(),
                    _metricTagKeys.Order(StringComparer.Ordinal).ToArray(),
                    _activityNames.Order(StringComparer.Ordinal).ToArray(),
                    _activityTagKeys.Order(StringComparer.Ordinal).ToArray(),
                    _eventNames.Keys.Order().ToArray(),
                    new Dictionary<int, string>(_eventNames));
            }
        }

        private void RecordMetric(string name, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_gate)
            {
                _metricSamples.TryGetValue(name, out var count);
                _metricSamples[name] = count + 1;
                foreach (var tag in tags)
                {
                    _metricTagKeys.Add(tag.Key);
                }
            }
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }
    }

    private sealed class LaneDurationCollector : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<(string Instrument, string Lane, double Milliseconds)> _samples = [];
        private readonly MeterListener _listener;

        public LaneDurationCollector()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CaptureLaneTelemetry.MeterName &&
                        instrument.Name is "camera_agent.lanes.claim.duration" or "camera_agent.lanes.ack.duration")
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
            {
                string? lane = null;
                string? result = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "lane")
                    {
                        lane = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                    else if (tag.Key == "result")
                    {
                        result = Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture);
                    }
                }
                if (lane is not null &&
                    (instrument.Name != "camera_agent.lanes.claim.duration" || result == "claimed"))
                {
                    lock (_gate)
                    {
                        _samples.Add((instrument.Name, lane, measurement * 1000));
                    }
                }
            });
            _listener.Start();
        }

        public double[] ReadMilliseconds(string instrument, string lane)
        {
            lock (_gate)
            {
                return _samples
                    .Where(sample => sample.Instrument == instrument && sample.Lane == lane)
                    .Select(static sample => sample.Milliseconds)
                    .ToArray();
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed record RuntimeSignalSnapshot(
        IReadOnlyList<string> PublishedInstruments,
        IReadOnlyDictionary<string, long> MetricSampleCounts,
        IReadOnlyList<string> MetricTagKeys,
        IReadOnlyList<string> ActivityNames,
        IReadOnlyList<string> ActivityTagKeys,
        IReadOnlyList<int> EventIds,
        IReadOnlyDictionary<int, string> EventNames);

    private sealed record W2Input(byte[] Payload, FrameMetadata Metadata);

    private sealed record GitEvidence(
        string Head,
        string Branch,
        bool Dirty,
        string WorkingTreeDiffSha256);

    private sealed record EvidenceRun(string Revision, int Trial, string Order, string OutputRoot, string OutputDirectory);

    private sealed record FileIdentity(string Name, long Bytes, string Sha256, string? InformationalVersion = null);

    private sealed record W2Correctness(
        int PayloadFiles,
        long PayloadBytes,
        long LaneWorkRows,
        long ContextRows,
        int UniqueCaptureIds,
        int UniqueArtifactIds,
        int UniqueSequences);

    private sealed record W2Measurement(
        int TotalAcceptedCaptures,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        IReadOnlyList<double> OrderedSamplesMilliseconds,
        double ThroughputPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        long AllocatedBytesPerCapture,
        long RssBeforeBytes,
        long RssAfterBytes,
        long DatabaseBytes,
        long WalBytes,
        int PayloadFiles,
        long PayloadBytesOnDisk,
        long LaneWorkRows,
        long LaneContextRows,
        long ObservedTransactions,
        long TransactionFailures,
        long ObservedCheckpoints,
        long CheckpointFailures,
        long FileFlushes,
        long DirectorySyncs,
        long WalCheckpointBusy,
        long WalCheckpointLogFrames,
        long WalCheckpointedFrames,
        long ReferenceLaneRowsPerCapture,
        int UniqueCaptureIds,
        int UniqueArtifactIds,
        int UniqueSequences,
        bool RepresentativeManifestValidated,
        bool RepresentativeSceneLineageValidated,
        int PersistedPayloadCopyCount,
        bool LaneWorkContainsPayloadOrBlob);

    private sealed record WalCheckpoint(long Busy, long LogFrames, long CheckpointedFrames);

    private sealed record CanonicalInsertionMeasurement(
        double DurationMilliseconds,
        int StatementCount,
        double CpuMilliseconds,
        long AllocatedBytes,
        long RssBefore,
        long RssAfter);

    private sealed record W3MetadataMeasurement(
        long RawRows,
        long LaneWorkRows,
        long ReferenceRowsPerCapture,
        long ContextRows,
        double CanonicalInsertionMilliseconds,
        double CanonicalInsertionRecordsPerSecond,
        int CanonicalInsertionTransactions,
        int CanonicalInsertionStatements,
        double InitializationMilliseconds,
        double CanonicalInsertionRawRecordsPerSecond,
        double CanonicalInsertionReferenceRowsPerSecond,
        int CanonicalLaneStatements,
        long CanonicalLaneRows,
        int ObservedProductionTransactions,
        double InitializationCpuMilliseconds,
        long InitializationAllocatedBytes,
        long RssBeforeInitializationBytes,
        long RssAfterInitializationBytes,
        double CanonicalInsertionCpuMilliseconds,
        long CanonicalInsertionAllocatedBytes,
        long RssBeforeCanonicalInsertionBytes,
        long RssAfterCanonicalInsertionBytes,
        long DatabaseBytesAfterInitialization,
        long WalBytesAfterInitialization,
        long DatabaseBytesAfterCanonicalInsertion,
        long WalBytesAfterCanonicalInsertion,
        IReadOnlyList<string> QueryPlan,
        bool QueryPlanUsesIndex,
        int SyntheticIndexedTraversalRows,
        double SyntheticIndexedTraversalMilliseconds,
        double SyntheticIndexedTraversalRowsPerSecond,
        string SyntheticIndexedTraversalLabel,
        int PaginationPageSize,
        int PaginationRows,
        double PaginationMilliseconds,
        long UserVersion,
        string IntegrityCheck,
        IReadOnlyList<double> RestartTrialMilliseconds,
        double RestartDiscoveryMedianMilliseconds,
        double RestartDiscoveryMinimumMilliseconds,
        double RestartDiscoveryMaximumMilliseconds,
        IReadOnlyList<long> RestartDiscoveredRowsPerTrial,
        double RestartCpuMilliseconds,
        long RestartAllocatedBytes,
        long RssBeforeRestartBytes,
        long RssAfterRestartBytes,
        int PayloadFiles,
        int PersistedPayloadCopyCount);

    private sealed record LiveScenarioMeasurement(
        string Name,
        int Captures,
        long RawBytes,
        double AcceptMedianMilliseconds,
        double AcceptP95Milliseconds,
        double AcceptMinimumMilliseconds,
        double AcceptMaximumMilliseconds,
        IReadOnlyList<double> OrderedAcceptSamplesMilliseconds,
        double BurstMilliseconds,
        double BurstCapturesPerSecond,
        double CommitCpuMilliseconds,
        string CommitAllocationDisposition,
        long RssBeforeBytes,
        IReadOnlyList<long> RssSamplesBytes,
        long FirstHalfRssMedianBytes,
        long FinalHalfRssMedianBytes,
        long RssMedianGrowthBytes,
        double RequiredDrainMilliseconds,
        double RequiredDrainCapturesPerSecond,
        int ProductionClaimWarmupSamples,
        int ProductionClaimMeasuredSamples,
        int ProductionClaimTotalObservedSamples,
        double StandardClaimMedianMilliseconds,
        double StandardClaimP95Milliseconds,
        double StandardClaimMaximumMilliseconds,
        IReadOnlyList<double> OrderedProductionClaimSamplesMilliseconds,
        double StandardAckMedianMilliseconds,
        double StandardAckP95Milliseconds,
        IReadOnlyList<double> OrderedStandardAckSamplesMilliseconds,
        double StandardCompletionEndToEndMedianMilliseconds,
        double StandardCompletionEndToEndP95Milliseconds,
        long OptionalPendingOrLeasedBeforeRelease,
        double OptionalRecoveryMilliseconds,
        double OptionalRecoveryCapturesPerSecond,
        IReadOnlyList<double> RestartTrialMilliseconds,
        double RestartDiscoveryMedianMilliseconds,
        double RestartDiscoveryMinimumMilliseconds,
        double RestartDiscoveryMaximumMilliseconds,
        long DatabaseBytes,
        long WalBytes,
        int PayloadFiles,
        long PayloadBytesOnDisk,
        int PersistedPayloadCopyCount,
        long StandardEvidenceReloadBytes,
        int MaximumConcurrentStandardEvidencePayloads,
        long LaneWorkRows,
        bool LaneWorkContainsPayloadOrBlob,
        long RequiredCompletedRowsBeforeOptionalRelease,
        long FinalCompletedLaneRows,
        long FinalPendingLaneRows,
        long FinalQuarantinedLaneRows,
        long FinalRetentionHeldRows,
        CaptureLaneSnapshot BeforeReleaseSnapshot,
        CaptureLaneSnapshot FinalSnapshot,
        bool GracefulStop);

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
}
