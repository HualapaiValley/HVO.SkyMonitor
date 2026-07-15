using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Imaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CaptureControlPerformanceTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private const int WarmupOperations = 5;
    private const int MeasurementCount = 30;
    private const int CandidateOperationsPerMeasurement = 512;
    private const int BaselineOperationsPerMeasurement = 4;
    private const int AllocationOperations = 1_024;
    private const ushort SaturationLevel = 64_224;
    private static readonly DateTimeOffset ScenarioStartUtc = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan CaptureInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan ModuleLogicalDuration = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan MeteringLogicalDuration = TimeSpan.FromMilliseconds(0.25);
    private static readonly TimeSpan ControlLogicalDuration = TimeSpan.FromMilliseconds(0.1);
    private static readonly TimeSpan SetpointLogicalDuration = TimeSpan.FromMilliseconds(0.15);
    private static readonly TimeSpan IngressLogicalDuration = TimeSpan.FromMilliseconds(1);
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    [TestMethod]
    public async Task W1AndW2SparseMeteringWritesCaptureControlEvidence()
    {
        var repositoryRoot = GetRepositoryRoot();
        var revision = GetEvidenceRevision();
        var pinnedSdk = ReadPinnedSdkVersion(repositoryRoot);
        var actualSdk = (await RunProcessAsync(repositoryRoot, "dotnet", "--version").ConfigureAwait(false)).Trim();
        Assert.AreEqual(pinnedSdk, actualSdk, "The performance harness must run with the repository-pinned SDK.");

        var workloads = new[]
        {
            await MeasureWorkloadAsync(
                repositoryRoot,
                "W1",
                "src/HVO.SkyMonitor.CameraAgent/virtual-asi174.full.json",
                "587F6B02DE5D254026DB2FB6D59D1E4FB3185E8577CE23988F57CE7006B10C51",
                1936,
                1216,
                CameraPixelFormat.Mono16).ConfigureAwait(false),
            await MeasureWorkloadAsync(
                repositoryRoot,
                "W2",
                "src/HVO.SkyMonitor.CameraAgent/virtual-asi178mc.full.json",
                "4CDF8496A1F5A05D005CF101A594CEF2053BFF418C554AED14AB1563AA549308",
                3096,
                2080,
                CameraPixelFormat.BayerRggb16).ConfigureAwait(false)
        };
        var git = await ReadGitEvidenceAsync(repositoryRoot).ConfigureAwait(false);
        var command = "DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=<revision> dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/" +
            "HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --filter \"FullyQualifiedName~" +
            "HVO.SkyMonitor.CameraAgent.Tests.Capture.CaptureControlPerformanceTests." +
            "W1AndW2SparseMeteringWritesCaptureControlEvidence\"";
        var durableLaneCommand = "DOTNET_gcServer=1 HVO_EVIDENCE_REVISION=<revision> dotnet test " +
            "tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build " +
            "--configuration Release --filter \"FullyQualifiedName~DurableCaptureDistributionPerformanceTests." +
            "W2W3MAndBlockedLane_DurableLaneEvidence\"";
        var productionTopologyCommand = "dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/" +
            "HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --filter \"FullyQualifiedName~" +
            "DurableCaptureDistributionTests.CameraModuleRunner_DurableLaneOutagesDoNotChangeCaptureCadence\"";
        var w5Command = "DOTNET_gcServer=1 dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/" +
            "HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --filter \"FullyQualifiedName~" +
            "AcceleratedCameraAgentSoakTests.AcceleratedTwentyFourHours_ArtifactsAndBoundedOwnersRemainConsistent\"";
        var evidence = new
        {
            Issue = 58,
            Phase = 6,
            Revision = new
            {
                EvidenceRevision = revision,
                BaselineCommit = "N/A: the production CameraModuleRunner cadence scenario is a net-new acceptance baseline",
                CandidateCommit = git.Head,
                git.Branch,
                git.Dirty,
                DirtyStateDisposition = git.Dirty
                    ? $"Measured working tree; status/diff/untracked fingerprint {git.DirtyDiffSha256}"
                    : "Clean working tree"
            },
            GeneratedUtc = DateTimeOffset.UtcNow,
            ExactCommand = command,
            ExactCommands = new
            {
                Candidate = command,
                DurableSqliteLaneCounterpart = durableLaneCommand,
                RunnerToDurableIngressCounterpart = productionTopologyCommand,
                W5SeparateSoakGate = w5Command,
                FormatVerification = "dotnet format HVO.SkyMonitor.v9.slnx --no-restore --verify-no-changes"
            },
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                PinnedSdk = pinnedSdk,
                ActualSdk = actualSdk,
                Runtime = RuntimeInformation.FrameworkDescription,
                Configuration = BuildConfiguration,
                ServerGc = GCSettings.IsServerGC,
                ProcessorCount = Environment.ProcessorCount,
                Cpu = ReadCpuModel(),
                TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                StorageType = new DriveInfo(Path.GetPathRoot(repositoryRoot)!).DriveFormat,
                ExecutionMode = "Native dotnet test process; no application container",
                ServiceVersions = "N/A: this harness uses no SQL Server, Redis, MinIO, network, or camera SDK service"
            },
            Candidate = git,
            Workload = new
            {
                Ids = new[] { "W1", "W2" },
                FrameCountPerRunnerScenario = WarmupOperations + MeasurementCount,
                MeasuredFrameCountPerRunnerScenario = MeasurementCount,
                Concurrency = 1,
                ArrivalInterval = CaptureInterval,
                InitialAcquisitionBacklog = 0,
                OptionalOutageDuration = "The blocked task remains incomplete for the complete measured scenario",
                FixtureSeed = 580_058,
                FixtureVersion = "Deterministic coordinate-hash linear16-v1",
                Recipe = "CameraModuleRunner host-metered exposure control and simulated synchronous durable-ingress handoff",
                TotalMeasuredPayloadBytes = workloads.Sum(static item =>
                    item.PayloadBytes * MeasurementCount * item.ProductionScenarios.Length)
            },
            Method = new
            {
                Harness = nameof(CaptureControlPerformanceTests),
                Command = command,
                WarmupOperations,
                MeasurementCount,
                CandidateOperationsPerMeasurement,
                BaselineOperationsPerMeasurement,
                AllocationOperations,
                AllocationCounter = "GC.GetAllocatedBytesForCurrentThread",
                RunnerAllocationCounter = "GC.GetTotalAllocatedBytes(precise: true), isolated by Manual category and DoNotParallelize",
                SamplingInterval = "One sample per capture cycle; p95 from 30 independent post-warmup captures",
                PhysicalCounters = "Stopwatch activity/module durations, process TotalProcessorTime, WorkingSet64, and total managed allocations",
                Concurrency = 1,
                Seed = 580_058,
                DeterministicClock = new
                {
                    UtcStart = ScenarioStartUtc,
                    TimestampFrequency = TimeSpan.TicksPerSecond,
                    CaptureInterval,
                    ModuleLogicalDuration,
                    MeteringLogicalDuration,
                    ControlLogicalDuration,
                    SetpointLogicalDuration,
                    IngressLogicalDuration
                },
                Metering = new
                {
                    XStride = 16,
                    YStride = 16,
                    BlackLevel = 0,
                    WhiteLevel = ushort.MaxValue,
                    SaturationLevel,
                    BayerPhotosites = BayerMeteringPhotosites.Green.ToString()
                }
            },
            IO = new
            {
                SimulatedIngressOperationsPerScenario = MeasurementCount,
                SimulatedIngressBytesPerScenario = "payloadBytes * measured captures; same payload owner, no copy",
                FilesystemBytes = "N/A: synchronous handoff is simulated in-memory in this phase-6 cadence harness",
                Sqlite = "N/A in this test. Execute the exact durable-lane and runner-to-durable-ingress counterpart commands for production SQLite/lane evidence.",
                SqlServer = "N/A: no central persistence is invoked",
                Minio = "N/A: central outage is represented by a detached blocked task",
                Network = "N/A: no network transport is invoked",
                TransactionsCheckpointsFsyncBatching = "N/A: owned by the referenced durable-lane counterpart"
            },
            CPU = new
            {
                AlgorithmicComplexity = "Sparse metering is O(ceil(width/16) * ceil(height/16)); legacy comparison is O(width * height)",
                Measurements = "Pure wall/process-CPU samples plus production-runner process CPU and activity wall durations are recorded per workload",
                HotPath = "CameraModuleRunner -> SparseLinear16Meter.Measure -> exposure decision -> setpoint -> ICaptureHostContext.PublishAsync"
            },
            Memory = new
            {
                Managed = "Pure sparse operation current-thread allocation and isolated runner total-allocation deltas are recorded",
                WorkingSet = "RSS before/after each runner scenario is recorded; no cross-machine budget is asserted",
                Loh = "One full-resolution byte[] owner per workload; reused for every scenario and capture",
                PeakTemporaryMemory = "N/A: this harness samples before/after RSS and asserts payload identity rather than estimating process peak",
                RetainedBuffers = "Exactly one scripted full-frame payload owner per workload; detached work never captures the submission",
                QueueWindowSize = "Acquisition backlog 0; blocked optional or central backlog <= 30"
            },
            Latency = new
            {
                Statistics = "Median, p95, and maximum from 30 post-warmup captures",
                Logical = "Requested-vs-actual jitter and critical segments use the controlled monotonic/UTC clock and are equality-checked across scenarios",
                Physical = "Activity/Stopwatch observations are reported without portable pass/fail budgets"
            },
            Throughput = new
            {
                Unit = "captures/second",
                Boundary = "Measured production critical-cycle activity time; whole-run wall throughput is also reported",
                ArrivalRate = 1d / CaptureInterval.TotalSeconds
            },
            Backlog = new
            {
                Acquisition = "Final simulated acquisition backlog must be zero in every scenario",
                Optional = "Standard 0; blocked-optional exactly measured scenario count; bounded by 30",
                Central = "Standard 0; central-outage exactly measured scenario count; bounded by 30",
                OldestAgeDrainRateRecoveryDuration = "N/A for deliberately unreleased detached simulation; release/cleanup convergence to zero is asserted"
            },
            Correctness = new
            {
                Checks = "Payload SHA-256, dimensions/layout, production metering counts/bytes, reference ownership, fake-time schedule/timing equality, disabled metering absence, and backlog convergence",
                PayloadCopyCount = 0,
                Lineage = "Each CaptureLoopSubmission retains the scripted CameraFrame and exact original byte[] owner",
                FailureBehavior = "Blocked optional and central tasks remain detached; synchronous ingress and acquisition continue"
            },
            Result = new
            {
                Baseline = "Legacy full-frame average retained as the nearest algorithm baseline; production runner path is a new absolute baseline",
                Candidate = "See Workloads pure and ProductionScenarios measurements",
                Change = "Sparse-vs-full scan bytes and timing are reported per workload; no universal physical-latency claim",
                NoiseInterpretation = "Deterministic fake-time invariants are pass/fail. Physical latency, CPU, allocation, and RSS are observations from this environment.",
                AcceptedRegressions = "None declared; no physical regression budget is asserted by this acceptance harness",
                ResidualRisk = "Full-resolution timing and the runner-to-real-SQLite topology are split across the referenced gates; neither substitutes for physical camera-SDK validation."
            },
            ProductionDefects = Array.Empty<string>(),
            DurableLaneCounterpart = new
            {
                Test = "DurableCaptureDistributionPerformanceTests.W2W3MAndBlockedLane_DurableLaneEvidence",
                Command = durableLaneCommand,
                Role = "Production SQLite, durable ingress, lane fan-out, blocked-lane, recovery, I/O, and backlog evidence; referenced, not rerun here"
            },
            ProductionTopologyCounterpart = new
            {
                Test = "DurableCaptureDistributionTests.CameraModuleRunner_DurableLaneOutagesDoNotChangeCaptureCadence",
                Command = productionTopologyCommand,
                Role = "Production CameraModuleRunner-to-SQLite/filesystem/lane topology with standard, optional-lane-outage, required-upload-outage, release, and drain evidence; referenced, not rerun here"
            },
            W5 = new
            {
                Command = w5Command,
                ResultRole = "289-capture accelerated virtual day validates sustained state, retention, cadence, artifacts, and bounded owners",
                Gate = "W5 is a separate Soak gate and is not executed by this Manual performance test; it is not a full-resolution throughput claim"
            },
            Workloads = workloads,
            ResultInterpretation = "SparseLinear16Meter reads less than 10% of each canonical payload and allocates " +
                "no managed memory per steady-state operation. The legacy comparison averages every active linear " +
                "sample from the same bytes. Numerical equality is not expected: the candidate rejects saturated " +
                "samples and W2 meters only RGGB green photosites, while the legacy baseline includes saturated " +
                "samples and all Bayer photosites. Latencies are observations, not universal pass/fail budgets."
        };

        var outputDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-58", revision);
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(outputDirectory, "capture-control-performance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    private static async Task<WorkloadEvidence> MeasureWorkloadAsync(
        string repositoryRoot,
        string id,
        string canonicalProfilePath,
        string expectedProfileSha256,
        int width,
        int height,
        CameraPixelFormat pixelFormat)
    {
        var strideBytes = checked(width * 2);
        var profileBytes = await File.ReadAllBytesAsync(
            Path.Combine(repositoryRoot, canonicalProfilePath)).ConfigureAwait(false);
        var profileSha256 = Convert.ToHexString(SHA256.HashData(profileBytes));
        Assert.AreEqual(expectedProfileSha256, profileSha256,
            $"{id} canonical profile content does not match the pinned SHA-256.");
        using (var profile = JsonDocument.Parse(profileBytes))
        {
            var sensor = profile.RootElement.GetProperty("rig").GetProperty("sensor");
            Assert.AreEqual(width, sensor.GetProperty("widthPixels").GetInt32(),
                $"{id} workload width does not match its canonical profile.");
            Assert.AreEqual(height, sensor.GetProperty("heightPixels").GetInt32(),
                $"{id} workload height does not match its canonical profile.");
            Assert.AreEqual(strideBytes, sensor.GetProperty("strideBytes").GetInt32(),
                $"{id} workload stride does not match its canonical profile.");
            Assert.AreEqual(pixelFormat.ToString(), sensor.GetProperty("pixelFormat").GetString(),
                $"{id} workload pixel format does not match its canonical profile.");
        }

        var payload = CreatePayload(width, height, strideBytes, pixelFormat);
        var layout = new ImageLayout(width, height, pixelFormat, strideBytes);
        var options = new SparseMeteringOptions(
            16,
            16,
            blackLevel: 0,
            whiteLevel: ushort.MaxValue,
            saturationLevel: SaturationLevel,
            bayerPhotosites: BayerMeteringPhotosites.Green);

        var candidate = MeasureCandidate(layout, payload, options);
        var baseline = MeasureLegacyBaseline(layout, payload);
        var expectedConsidered = pixelFormat == CameraPixelFormat.Mono16
            ? DivideRoundUp(width, 16) * (long)DivideRoundUp(height, 16)
            : DivideRoundUp(width, 16) * (long)DivideRoundUp(height, 16) * 2;
        var activePixelBytes = checked((long)width * height * 2);

        Assert.AreEqual(expectedConsidered, candidate.ConsideredSamples);
        Assert.AreEqual(candidate.ConsideredSamples, candidate.AcceptedSamples + candidate.SaturatedSamples);
        Assert.AreEqual(candidate.ConsideredSamples * 2, candidate.ScannedBytes);
        Assert.IsGreaterThan(0, candidate.AcceptedSamples);
        Assert.IsGreaterThan(0, candidate.SaturatedSamples);
        Assert.IsTrue(candidate.NormalizedMean is >= 0 and < 1);
        Assert.IsLessThan(activePixelBytes, candidate.ScannedBytes, "Sparse metering inspected a full frame.");
        Assert.IsLessThan(0.1, candidate.ScanRatioToPayload);
        Assert.AreEqual(0, candidate.AllocatedBytesTotal, "Steady-state sparse metering allocated managed memory.");
        Assert.IsTrue(double.IsFinite(candidate.OperationsPerSecond) && candidate.OperationsPerSecond > 0);
        Assert.AreEqual(activePixelBytes, baseline.ScannedBytes);
        Assert.AreEqual((long)width * height, baseline.ConsideredSamples);
        Assert.IsTrue(baseline.NormalizedMean is >= 0 and <= 1);
        Assert.IsTrue(double.IsFinite(baseline.OperationsPerSecond) && baseline.OperationsPerSecond > 0);

        var standard = await MeasureRunnerScenarioAsync(
            id, width, height, pixelFormat, payload, RunnerScenario.Standard, enabled: true).ConfigureAwait(false);
        var blockedOptional = await MeasureRunnerScenarioAsync(
            id, width, height, pixelFormat, payload, RunnerScenario.BlockedOptional, enabled: true).ConfigureAwait(false);
        var centralOutage = await MeasureRunnerScenarioAsync(
            id, width, height, pixelFormat, payload, RunnerScenario.CentralOutage, enabled: true).ConfigureAwait(false);
        var disabled = await MeasureRunnerScenarioAsync(
            id, width, height, pixelFormat, payload, RunnerScenario.Disabled, enabled: false).ConfigureAwait(false);
        AssertEquivalentCriticalTimeline(standard, blockedOptional);
        AssertEquivalentCriticalTimeline(standard, centralOutage);

        foreach (var scenario in new[] { standard, blockedOptional, centralOutage })
        {
            Assert.AreEqual(expectedConsidered, scenario.ProductionMetering.ConsideredSamplesPerCapture);
            Assert.AreEqual(expectedConsidered * 2, scenario.ProductionMetering.ScannedBytesPerCapture);
            Assert.AreEqual(0, scenario.FinalAcquisitionBacklog);
            Assert.IsLessThanOrEqualTo(MeasurementCount, scenario.OutageEndOptionalBacklog);
            Assert.IsLessThanOrEqualTo(MeasurementCount, scenario.OutageEndCentralBacklog);
            Assert.AreEqual(0, scenario.PostReleaseOptionalBacklog);
            Assert.AreEqual(0, scenario.PostReleaseCentralBacklog);
            Assert.AreEqual(0, scenario.PayloadCopyCount);
            Assert.AreEqual(1, scenario.DistinctPayloadOwners);
        }
        Assert.AreEqual(0, standard.OutageEndOptionalBacklog);
        Assert.AreEqual(0, standard.OutageEndCentralBacklog);
        Assert.AreEqual(MeasurementCount, blockedOptional.OutageEndOptionalBacklog);
        Assert.AreEqual(0, blockedOptional.OutageEndCentralBacklog);
        Assert.AreEqual(0, centralOutage.OutageEndOptionalBacklog);
        Assert.AreEqual(MeasurementCount, centralOutage.OutageEndCentralBacklog);
        Assert.AreEqual(0, disabled.FinalAcquisitionBacklog);
        Assert.AreEqual(0, disabled.ProductionMetering.MeteringEvidenceCount);
        Assert.AreEqual(0, disabled.ProductionMetering.ConsideredSamplesPerCapture);
        Assert.AreEqual(0, disabled.ProductionMetering.ScannedBytesPerCapture);
        Assert.AreEqual(0, disabled.MeteringMetricMeasurements);

        return new WorkloadEvidence(
            id,
            canonicalProfilePath,
            profileSha256,
            width,
            height,
            pixelFormat.ToString(),
            strideBytes,
            payload.LongLength,
            activePixelBytes,
            "canonical-profile-shaped deterministic coordinate-hash; not an actual VirtualSky rendered frame; " +
                "seed 580058; little-endian linear 16-bit samples",
            Convert.ToHexString(SHA256.HashData(payload)),
            candidate,
            baseline,
            disabled,
            [standard, blockedOptional, centralOutage],
            candidate.NormalizedMean - baseline.NormalizedMean,
            pixelFormat == CameraPixelFormat.BayerRggb16
                ? "Candidate mean excludes saturated samples and represents sparse RGGB green photosites; baseline mean includes every R, G, and B sample."
                : "Candidate mean excludes saturated samples; baseline mean includes every Mono16 sample.");
    }

    private static CandidateMeasurement MeasureCandidate(
        ImageLayout layout,
        byte[] payload,
        SparseMeteringOptions options)
    {
        SparseMeteringResult result = default;
        for (var index = 0; index < WarmupOperations; index++)
        {
            result = SparseLinear16Meter.Measure(layout, payload, options);
        }

        var wallSamples = new double[MeasurementCount];
        var cpuSamples = new double[MeasurementCount];
        long sink = 0;
        using (var process = Process.GetCurrentProcess())
        {
            for (var sample = 0; sample < MeasurementCount; sample++)
            {
                var cpuStarted = process.TotalProcessorTime;
                var wallStarted = Stopwatch.GetTimestamp();
                for (var operation = 0; operation < CandidateOperationsPerMeasurement; operation++)
                {
                    result = SparseLinear16Meter.Measure(layout, payload, options);
                    sink ^= result.AcceptedSampleCount;
                }
                var wallElapsed = Stopwatch.GetElapsedTime(wallStarted);
                var cpuElapsed = process.TotalProcessorTime - cpuStarted;
                wallSamples[sample] = wallElapsed.TotalMilliseconds / CandidateOperationsPerMeasurement;
                cpuSamples[sample] = cpuElapsed.TotalMilliseconds / CandidateOperationsPerMeasurement;
            }
        }

        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var operation = 0; operation < AllocationOperations; operation++)
        {
            result = SparseLinear16Meter.Measure(layout, payload, options);
            sink ^= result.ConsideredSampleCount;
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        GC.KeepAlive(sink);
        var totalWallMilliseconds = wallSamples.Sum() * CandidateOperationsPerMeasurement;
        Array.Sort(wallSamples);
        Array.Sort(cpuSamples);
        return new CandidateMeasurement(
            nameof(SparseLinear16Meter),
            MeasurementCount,
            CandidateOperationsPerMeasurement,
            Percentile(wallSamples, 0.50),
            Percentile(wallSamples, 0.95),
            Percentile(cpuSamples, 0.50),
            Percentile(cpuSamples, 0.95),
            allocatedBytes,
            AllocationOperations,
            allocatedBytes / (double)AllocationOperations,
            result.ConsideredSampleCount,
            result.AcceptedSampleCount,
            result.SaturatedSampleCount,
            result.ScannedBytes,
            result.ScannedBytes / (double)payload.LongLength,
            result.NormalizedMean,
            MeasurementCount * (double)CandidateOperationsPerMeasurement /
            TimeSpan.FromMilliseconds(totalWallMilliseconds).TotalSeconds);
    }

    private static BaselineMeasurement MeasureLegacyBaseline(ImageLayout layout, byte[] payload)
    {
        LegacyAverageResult result = default;
        for (var index = 0; index < WarmupOperations; index++)
        {
            result = LegacyFullFrameLinearAverage(layout, payload);
        }

        var wallSamples = new double[MeasurementCount];
        var cpuSamples = new double[MeasurementCount];
        double sink = 0;
        using (var process = Process.GetCurrentProcess())
        {
            for (var sample = 0; sample < MeasurementCount; sample++)
            {
                var cpuStarted = process.TotalProcessorTime;
                var wallStarted = Stopwatch.GetTimestamp();
                for (var operation = 0; operation < BaselineOperationsPerMeasurement; operation++)
                {
                    result = LegacyFullFrameLinearAverage(layout, payload);
                    sink += result.NormalizedMean;
                }
                var wallElapsed = Stopwatch.GetElapsedTime(wallStarted);
                var cpuElapsed = process.TotalProcessorTime - cpuStarted;
                wallSamples[sample] = wallElapsed.TotalMilliseconds / BaselineOperationsPerMeasurement;
                cpuSamples[sample] = cpuElapsed.TotalMilliseconds / BaselineOperationsPerMeasurement;
            }
        }

        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var operation = 0; operation < AllocationOperations; operation++)
        {
            result = LegacyFullFrameLinearAverage(layout, payload);
            sink += result.NormalizedMean;
        }
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        GC.KeepAlive(sink);
        var totalWallMilliseconds = wallSamples.Sum() * BaselineOperationsPerMeasurement;
        Array.Sort(wallSamples);
        Array.Sort(cpuSamples);
        return new BaselineMeasurement(
            "LocalLegacyFullFrameLinearAverage",
            "Every active little-endian 16-bit sample; no CFA selection and no saturation rejection",
            MeasurementCount,
            BaselineOperationsPerMeasurement,
            Percentile(wallSamples, 0.50),
            Percentile(wallSamples, 0.95),
            Percentile(cpuSamples, 0.50),
            Percentile(cpuSamples, 0.95),
            allocatedBytes,
            AllocationOperations,
            allocatedBytes / (double)AllocationOperations,
            result.ConsideredSamples,
            result.ScannedBytes,
            result.NormalizedMean,
            MeasurementCount * (double)BaselineOperationsPerMeasurement /
            TimeSpan.FromMilliseconds(totalWallMilliseconds).TotalSeconds);
    }

    private static async Task<RunnerScenarioEvidence> MeasureRunnerScenarioAsync(
        string workloadId,
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        byte[] payload,
        RunnerScenario scenario,
        bool enabled)
    {
        var clock = new DeterministicTimeProvider(ScenarioStartUtc);
        using var cancellation = new CancellationTokenSource();
        var module = new PerformanceCameraModule(clock, width, height, pixelFormat, payload);
        var context = new PerformanceHostContext(
            CreateRunnerConfig(workloadId, width, height, pixelFormat, enabled),
            clock,
            payload,
            cancellation,
            scenario);
        using var activities = new ScenarioActivityCollector(clock);
        using var metrics = new ScenarioMetricCollector();
        using var telemetry = new CaptureControlTelemetry();
        var runner = new CameraModuleRunner(
            module,
            context,
            clock,
            NullLogger.Instance,
            enabled ? new FixedDayEphemeris() : null,
            telemetry);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var rssBefore = process.WorkingSet64;
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var cpuBefore = process.TotalProcessorTime;
        var wallStarted = Stopwatch.GetTimestamp();
        await runner.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var wallDuration = Stopwatch.GetElapsedTime(wallStarted);
        var cpuDuration = process.TotalProcessorTime - cpuBefore;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocationBefore;
        process.Refresh();
        var rssAfter = process.WorkingSet64;

        Assert.AreEqual(WarmupOperations + MeasurementCount, context.Submissions.Count);
        Assert.AreEqual(context.Submissions.Count, module.PhysicalModuleMilliseconds.Count);
        var measuredSubmissions = context.Submissions.Skip(WarmupOperations).ToArray();
        var measuredIngressCompleted = context.IngressCompletedUtc.Skip(WarmupOperations).ToArray();
        var timeline = new CriticalTimelineSample[MeasurementCount];
        for (var index = 0; index < timeline.Length; index++)
        {
            var submission = measuredSubmissions[index];
            var evidence = submission.CycleEvidence;
            Assert.IsNotNull(evidence);
            var acquisition = submission.Result.AcquisitionTiming;
            Assert.IsNotNull(acquisition);
            timeline[index] = new CriticalTimelineSample(
                submission.Request.RequestedStartUtc,
                evidence.ModuleCallStartedUtc,
                evidence.ModuleCallStartedUtc - submission.Request.RequestedStartUtc,
                acquisition.ReadoutCompletedUtc - evidence.ModuleCallStartedUtc,
                evidence.Metering?.CompletedUtc - evidence.Metering?.StartedUtc ?? TimeSpan.Zero,
                evidence.Decision.CompletedUtc - evidence.Decision.StartedUtc,
                evidence.Decision.SetpointAppliedUtc - evidence.Decision.CompletedUtc ?? TimeSpan.Zero,
                measuredIngressCompleted[index] - evidence.IngressHandoffStartedUtc,
                measuredIngressCompleted[index] - evidence.ModuleCallStartedUtc,
                evidence.StartReason);
        }

        var measuredMetering = measuredSubmissions.Select(static item => item.CycleEvidence!.Metering).ToArray();
        Assert.IsTrue(timeline.All(static item => item.StartJitter >= TimeSpan.Zero),
            "MinimumStartInterval started a capture before its requested deadline.");
        if (enabled)
        {
            Assert.IsTrue(context.Submissions.All(static item => item.CycleEvidence!.Metering is not null));
            Assert.IsTrue(measuredMetering.All(static item => item is not null));
            Assert.AreEqual(MeasurementCount, module.Applications - WarmupOperations);
        }
        else
        {
            Assert.IsTrue(context.Submissions.All(static item => item.CycleEvidence!.Metering is null),
                "Disabled ownership must bypass production metering for every runner cycle, including warmup.");
            Assert.IsTrue(measuredMetering.All(static item => item is null),
                "Disabled ownership must bypass production metering for every measured runner cycle.");
            Assert.AreEqual(0, module.Applications);
            Assert.AreEqual(0, metrics.MeteringMeasurementCount,
                "Disabled ownership emitted a production metering metric.");
        }

        var considered = measuredMetering.Where(static item => item is not null)
            .Select(static item => item!.ConsideredSampleCount).ToArray();
        var scanned = measuredMetering.Where(static item => item is not null)
            .Select(static item => item!.ScannedBytes).ToArray();
        var outageEndOptionalBacklog = context.OptionalBacklog;
        var outageEndCentralBacklog = context.CentralBacklog;
        var finalAcquisitionBacklog = context.AcquisitionBacklog;
        await context.ReleaseDetachedAsync().ConfigureAwait(false);
        Assert.AreEqual(0, context.OptionalBacklog);
        Assert.AreEqual(0, context.CentralBacklog);
        var postReleaseOptionalBacklog = context.OptionalBacklog;
        var postReleaseCentralBacklog = context.CentralBacklog;

        var physicalCycles = activities.GetMeasuredMilliseconds("capture-cycle");
        var physicalMetering = enabled
            ? activities.GetMeasuredMilliseconds("capture-meter")
            : Array.Empty<double>();
        var physicalControl = activities.GetMeasuredMilliseconds("capture-control");
        var physicalIngress = activities.GetMeasuredMilliseconds("capture-ingress-handoff");
        var physicalModules = module.PhysicalModuleMilliseconds.Skip(WarmupOperations).ToArray();
        Assert.AreEqual(MeasurementCount, physicalCycles.Length);
        Assert.AreEqual(MeasurementCount, physicalControl.Length);
        Assert.AreEqual(MeasurementCount, physicalIngress.Length);
        Assert.AreEqual(MeasurementCount, physicalModules.Length);
        if (enabled)
        {
            Assert.AreEqual(MeasurementCount, physicalMetering.Length);
        }

        var criticalSeconds = physicalCycles.Sum() / 1_000d;
        var productionMetering = new ProductionMeteringEvidence(
            measuredMetering.Count(static item => item is not null),
            considered.Length == 0 ? 0 : considered.Distinct().Single(),
            considered.Sum(),
            scanned.Length == 0 ? 0 : scanned.Distinct().Single(),
            scanned.Sum(),
            measuredMetering.Where(static item => item is not null)
                .Select(static item => item!.Outcome.ToString()).Distinct(StringComparer.Ordinal).ToArray());
        return new RunnerScenarioEvidence(
            scenario.ToString(),
            enabled ? AutomaticControlOwnership.HostMetered.ToString() : AutomaticControlOwnership.Disabled.ToString(),
            WarmupOperations,
            MeasurementCount,
            HashSchedule(timeline),
            HashCriticalTiming(timeline),
            Summarize(timeline.Select(static item => item.StartJitter.TotalMilliseconds)),
            new CriticalDurationEvidence(
                Summarize(timeline.Select(static item => item.Module.TotalMilliseconds)),
                Summarize(timeline.Select(static item => item.Metering.TotalMilliseconds)),
                Summarize(timeline.Select(static item =>
                    (item.Control + item.Setpoint).TotalMilliseconds)),
                Summarize(timeline.Select(static item => item.Ingress.TotalMilliseconds)),
                Summarize(timeline.Select(static item => item.Total.TotalMilliseconds))),
            new CriticalDurationEvidence(
                Summarize(physicalModules),
                Summarize(physicalMetering),
                Summarize(physicalControl),
                Summarize(physicalIngress),
                Summarize(physicalCycles)),
            MeasurementCount / criticalSeconds,
            (WarmupOperations + MeasurementCount) / wallDuration.TotalSeconds,
            cpuDuration.TotalMilliseconds,
            allocatedBytes,
            allocatedBytes / (double)(WarmupOperations + MeasurementCount),
            rssBefore,
            rssAfter,
            rssAfter - rssBefore,
            productionMetering,
            metrics.MeteringMeasurementCount,
            finalAcquisitionBacklog,
            outageEndOptionalBacklog,
            outageEndCentralBacklog,
            postReleaseOptionalBacklog,
            postReleaseCentralBacklog,
            context.PayloadCopyCount,
            context.DistinctPayloadOwners,
            "The CameraFrame ReadOnlyMemory resolves to the original deterministic byte[] at offset 0 and full length; no handoff copy is made.",
            timeline);
    }

    private static CameraModuleConfig CreateRunnerConfig(
        string workloadId,
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        bool enabled)
        => new(
            new ObservatoryLocation(35.347, -113.878, 0, "UTC"),
            new CameraModuleDescriptor("CaptureControlPerformance"),
            new CameraRigConfig(
                new SensorProfile(
                    workloadId,
                    width,
                    height,
                    1,
                    pixelFormat == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                    pixelFormat,
                    pixelFormat == CameraPixelFormat.Mono16
                        ? SensorResponseMode.Monochrome
                        : SensorResponseMode.BayerRaw,
                    checked(width * 2)),
                new OpticsProfile("EquidistantFisheye", 1, 180, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    CaptureInterval,
                    TimeSpan.FromMilliseconds(1),
                    TimeSpan.FromMilliseconds(1),
                    10,
                    10,
                    new ExposureEnvelope(
                        TimeSpan.FromMilliseconds(1),
                        TimeSpan.FromMilliseconds(8),
                        1,
                        100,
                        new ExposureDefaults(TimeSpan.FromMilliseconds(1), 10),
                        new ExposureDefaults(TimeSpan.FromMilliseconds(1), 10),
                        0.9,
                        Hysteresis: 0.01,
                        AdjustmentFactor: 2,
                        GainStep: 10),
                    CadenceMode: CaptureCadenceMode.MinimumStartInterval),
                new CameraControlPolicy
                {
                    ExposureControl = enabled
                        ? AutomaticControlOwnership.HostMetered
                        : AutomaticControlOwnership.Disabled,
                    GainControl = AutomaticControlOwnership.Disabled,
                    Metering = new CaptureMeteringPolicy
                    {
                        XStride = 16,
                        YStride = 16,
                        UseImageCircle = false,
                        SaturationFraction = 0.98,
                        CfaSelection = CaptureMeteringCfaSelection.Green
                    },
                    SolarRegimes = new CaptureSolarRegimePolicy
                    {
                        DayAltitudeThresholdDegrees = 0,
                        NightAltitudeThresholdDegrees = -12
                    }
                },
                ProfileVersion: "capture-control-performance-v1"));

    private static void AssertEquivalentCriticalTimeline(
        RunnerScenarioEvidence expected,
        RunnerScenarioEvidence actual)
    {
        Assert.AreEqual(expected.DeterministicScheduleSha256, actual.DeterministicScheduleSha256,
            $"{actual.Scenario} changed the deterministic runner start schedule.");
        Assert.AreEqual(expected.DeterministicCriticalTimingSha256, actual.DeterministicCriticalTimingSha256,
            $"{actual.Scenario} changed deterministic acquisition-critical timing.");
        Assert.IsTrue(expected.Timeline.SequenceEqual(actual.Timeline),
            $"{actual.Scenario} changed a deterministic production-runner timeline sample.");
    }

    private static DistributionSummary Summarize(IEnumerable<double> values)
    {
        var samples = values.ToArray();
        if (samples.Length == 0)
        {
            return new DistributionSummary(0, 0, 0, 0);
        }
        Array.Sort(samples);
        return new DistributionSummary(
            samples.Length,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[^1]);
    }

    private static string HashSchedule(IEnumerable<CriticalTimelineSample> timeline)
        => HashText(string.Join('|', timeline.Select(static item => string.Create(
            CultureInfo.InvariantCulture,
            $"{item.RequestedStartUtc:O},{item.ActualStartUtc:O},{item.StartJitter.Ticks},{item.StartReason}"))));

    private static string HashCriticalTiming(IEnumerable<CriticalTimelineSample> timeline)
        => HashText(string.Join('|', timeline.Select(static item => string.Create(
            CultureInfo.InvariantCulture,
            $"{item.Module.Ticks},{item.Metering.Ticks},{item.Control.Ticks},{item.Setpoint.Ticks},{item.Ingress.Ticks},{item.Total.Ticks}"))));

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static LegacyAverageResult LegacyFullFrameLinearAverage(ImageLayout layout, ReadOnlySpan<byte> payload)
    {
        ulong sum = 0;
        for (var y = 0; y < layout.Height; y++)
        {
            var rowOffset = y * layout.StrideBytes;
            for (var x = 0; x < layout.Width; x++)
            {
                var offset = rowOffset + x * 2;
                sum += (ushort)(payload[offset] | payload[offset + 1] << 8);
            }
        }

        var samples = checked((long)layout.Width * layout.Height);
        return new LegacyAverageResult(
            sum / ((double)samples * ushort.MaxValue),
            samples,
            checked(samples * 2));
    }

    private static byte[] CreatePayload(
        int width,
        int height,
        int strideBytes,
        CameraPixelFormat pixelFormat)
    {
        var payload = new byte[checked(strideBytes * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var hash = Mix(unchecked((uint)(580_058 + x * 73_856_093 + y * 19_349_663)));
                ushort value;
                if (pixelFormat == CameraPixelFormat.BayerRggb16)
                {
                    value = (y & 1, x & 1) switch
                    {
                        (0, 0) => (ushort)(8_000 + hash % 12_000),
                        (0, 1) => (ushort)(24_000 + hash % 20_000),
                        (1, 0) => (ushort)(28_000 + hash % 20_000),
                        _ => (ushort)(48_000 + hash % 17_000)
                    };
                }
                else
                {
                    value = (ushort)(512 + hash % 64_500);
                }
                if (hash % 37 == 0)
                {
                    value = 65_000;
                }

                var offset = y * strideBytes + x * 2;
                payload[offset] = (byte)value;
                payload[offset + 1] = (byte)(value >> 8);
            }
        }
        return payload;
    }

    private static uint Mix(uint value)
    {
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ value >> 16;
    }

    private static double Percentile(double[] sortedSamples, double percentile)
        => sortedSamples[(int)Math.Ceiling(percentile * sortedSamples.Length) - 1];

    private static int DivideRoundUp(int value, int divisor) => (value + divisor - 1) / divisor;

    private static string GetEvidenceRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        if (string.IsNullOrWhiteSpace(revision) || Path.GetFileName(revision) != revision ||
            revision is "." or "..")
        {
            throw new InvalidOperationException("HVO_EVIDENCE_REVISION must be a single safe path segment.");
        }
        return revision;
    }

    private static string ReadPinnedSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json does not contain an SDK version.");
    }

    private static string ReadCpuModel()
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (File.Exists(cpuInfo))
        {
            var model = File.ReadLines(cpuInfo).FirstOrDefault(
                static line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
            if (model is not null)
            {
                var separator = model.IndexOf(':', StringComparison.Ordinal);
                if (separator >= 0)
                {
                    return model[(separator + 1)..].Trim();
                }
            }
        }
        return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unknown";
    }

    private static async Task<GitEvidence> ReadGitEvidenceAsync(string repositoryRoot)
    {
        var head = (await RunProcessAsync(repositoryRoot, "git", "rev-parse", "HEAD").ConfigureAwait(false)).Trim();
        var branch = (await RunProcessAsync(
            repositoryRoot, "git", "rev-parse", "--abbrev-ref", "HEAD").ConfigureAwait(false)).Trim();
        var status = await RunProcessAsync(
            repositoryRoot, "git", "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(false);
        var diff = await RunProcessAsync(
            repositoryRoot, "git", "diff", "--binary", "--no-ext-diff", "HEAD", "--").ConfigureAwait(false);
        var untracked = await RunProcessAsync(
            repositoryRoot, "git", "ls-files", "--others", "--exclude-standard", "-z").ConfigureAwait(false);
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        fingerprint.AppendData(Encoding.UTF8.GetBytes(status));
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

    private static async Task<string> RunProcessAsync(
        string workingDirectory,
        string fileName,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {fileName} for performance evidence.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed while collecting performance evidence: {error}");
        }
        return output;
    }

    private static string GetRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private enum RunnerScenario
    {
        Standard,
        BlockedOptional,
        CentralOutage,
        Disabled
    }

    private sealed class PerformanceCameraModule(
        DeterministicTimeProvider clock,
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        byte[] payload) : ICameraModule, ICameraSetpointController
    {
        private readonly FrameMetadata _metadata = new(
            TimeSpan.FromMilliseconds(1),
            10,
            0,
            Extra: new Dictionary<string, string>
            {
                ["blackLevelAdu"] = "0",
                ["whiteLevelAdu"] = ushort.MaxValue.ToString(CultureInfo.InvariantCulture)
            });

        public List<double> PhysicalModuleMilliseconds { get; } = [];
        public int Applications { get; private set; }
        public string Id => "capture-control-performance";
        public string DisplayName => "Capture Control Performance";
        public string ModuleType => "ScriptedPerformance";
        public CameraModuleCapabilities Capabilities => CameraModuleCapabilities.StillFrames;

        public Task InitializeAsync(CameraModuleConfig config, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CaptureResult> CaptureAsync(CaptureRequest request, CancellationToken cancellationToken)
        {
            var physicalStarted = Stopwatch.GetTimestamp();
            var exposureStartedUtc = clock.GetUtcNow();
            clock.Advance(ModuleLogicalDuration);
            var frame = new CameraFrame(
                exposureStartedUtc,
                width,
                height,
                pixelFormat,
                payload,
                _metadata,
                checked(width * 2));
            var result = new CaptureResult(
                frame,
                request.RequestedSetpoint!,
                TimeSpan.Zero,
                CaptureMode.Still,
                false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    exposureStartedUtc,
                    exposureStartedUtc + TimeSpan.FromMilliseconds(1.5),
                    exposureStartedUtc + ModuleLogicalDuration)
            };
            PhysicalModuleMilliseconds.Add(Stopwatch.GetElapsedTime(physicalStarted).TotalMilliseconds);
            return Task.FromResult(result);
        }

        public ValueTask<DateTimeOffset> ApplySetpointAsync(
            CaptureSetpoint setpoint,
            CancellationToken cancellationToken)
        {
            Applications++;
            clock.Advance(SetpointLogicalDuration);
            return ValueTask.FromResult(clock.GetUtcNow());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PerformanceHostContext(
        CameraModuleConfig configuration,
        DeterministicTimeProvider clock,
        byte[] payloadOwner,
        CancellationTokenSource cancellation,
        RunnerScenario scenario) : ICaptureHostContext
    {
        private readonly TaskCompletionSource _optionalRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _centralRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _optionalTasks = [];
        private readonly List<Task> _centralTasks = [];
        private readonly HashSet<byte[]> _payloadOwners = new(ReferenceEqualityComparer.Instance);

        public CameraModuleConfig Configuration => configuration;
        public List<CaptureLoopSubmission> Submissions { get; } = [];
        public List<DateTimeOffset> IngressCompletedUtc { get; } = [];
        public int AcquisitionBacklog { get; private set; }
        public int OptionalBacklog => _optionalTasks.Count(static task => !task.IsCompleted);
        public int CentralBacklog => _centralTasks.Count(static task => !task.IsCompleted);
        public int PayloadCopyCount { get; private set; }
        public int DistinctPayloadOwners => _payloadOwners.Count;

        public async ValueTask PublishAsync(CaptureLoopSubmission submission, CancellationToken cancellationToken)
        {
            AcquisitionBacklog++;
            try
            {
                var frame = submission.Result.Frame;
                Assert.IsNotNull(frame);
                Assert.IsTrue(MemoryMarshal.TryGetArray(frame.PixelData, out var segment));
                if (!ReferenceEquals(segment.Array, payloadOwner) || segment.Offset != 0 ||
                    segment.Count != payloadOwner.Length)
                {
                    PayloadCopyCount++;
                }
                Assert.AreSame(payloadOwner, segment.Array);
                Assert.AreEqual(0, segment.Offset);
                Assert.AreEqual(payloadOwner.Length, segment.Count);
                _payloadOwners.Add(segment.Array!);
                Submissions.Add(submission);

                clock.Advance(IngressLogicalDuration);
                IngressCompletedUtc.Add(clock.GetUtcNow());
                if (Submissions.Count > WarmupOperations)
                {
                    _optionalTasks.Add(scenario == RunnerScenario.BlockedOptional
                        ? WaitForReleaseAsync(_optionalRelease.Task)
                        : Task.CompletedTask);
                    _centralTasks.Add(scenario == RunnerScenario.CentralOutage
                        ? WaitForReleaseAsync(_centralRelease.Task)
                        : Task.CompletedTask);
                }
            }
            finally
            {
                AcquisitionBacklog--;
            }

            if (Submissions.Count == WarmupOperations + MeasurementCount)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        public async Task ReleaseDetachedAsync()
        {
            _optionalRelease.TrySetResult();
            _centralRelease.TrySetResult();
            await Task.WhenAll(_optionalTasks.Concat(_centralTasks)).ConfigureAwait(false);
        }

        private static async Task WaitForReleaseAsync(Task release)
            => await release.ConfigureAwait(false);
    }

    private sealed class ScenarioActivityCollector : IDisposable
    {
        private readonly DeterministicTimeProvider _clock;
        private readonly Dictionary<string, List<double>> _durations = new(StringComparer.Ordinal);
        private readonly ActivityListener _listener;

        public ScenarioActivityCollector(DeterministicTimeProvider clock)
        {
            _clock = clock;
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == CaptureControlTelemetry.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStarted = OnStarted,
                ActivityStopped = OnStopped
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public double[] GetMeasuredMilliseconds(string name)
        {
            lock (_durations)
            {
                Assert.IsTrue(_durations.TryGetValue(name, out var samples), $"No production activity named {name} was observed.");
                Assert.AreEqual(WarmupOperations + MeasurementCount, samples.Count,
                    $"Unexpected production activity count for {name}.");
                return samples.Skip(WarmupOperations).ToArray();
            }
        }

        private void OnStarted(Activity activity)
        {
            if (activity.DisplayName == "capture-meter")
            {
                _clock.Advance(MeteringLogicalDuration);
            }
            else if (activity.DisplayName == "capture-control")
            {
                _clock.Advance(ControlLogicalDuration);
            }
        }

        private void OnStopped(Activity activity)
        {
            lock (_durations)
            {
                if (!_durations.TryGetValue(activity.DisplayName, out var samples))
                {
                    samples = [];
                    _durations.Add(activity.DisplayName, samples);
                }
                samples.Add(activity.Duration.TotalMilliseconds);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class ScenarioMetricCollector : IDisposable
    {
        private readonly MeterListener _listener;
        private int _meteringMeasurementCount;

        public ScenarioMetricCollector()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = static (instrument, listener) =>
                {
                    if (instrument.Meter.Name == CaptureControlTelemetry.MeterName)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => Record(instrument));
            _listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => Record(instrument));
            _listener.Start();
        }

        public int MeteringMeasurementCount => Volatile.Read(ref _meteringMeasurementCount);

        private void Record(Instrument instrument)
        {
            if (instrument.Name.StartsWith("camera_agent.capture_control.metering.", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _meteringMeasurementCount);
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class DeterministicTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private readonly object _sync = new();
        private long _timestamp;
        private long _utcTicks = startUtc.UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return new DateTimeOffset(_utcTicks, TimeSpan.Zero);
            }
        }

        public override long GetTimestamp()
        {
            lock (_sync)
            {
                return _timestamp;
            }
        }

        public void Advance(TimeSpan amount)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(amount, TimeSpan.Zero);
            lock (_sync)
            {
                _utcTicks = checked(_utcTicks + amount.Ticks);
                _timestamp = checked(_timestamp + amount.Ticks);
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);
            var timer = new DeterministicTimer(this, callback, state);
            timer.Schedule(dueTime);
            return timer;
        }

        private sealed class DeterministicTimer(
            DeterministicTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    return false;
                }
                Schedule(dueTime);
                return true;
            }

            public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Schedule(TimeSpan dueTime)
            {
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    return;
                }
                owner.Advance(dueTime);
                ThreadPool.QueueUserWorkItem(static value => ((DeterministicTimer)value!).Fire(), this);
            }

            private void Fire()
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    callback(state);
                }
            }
        }
    }

    private sealed class FixedDayEphemeris : IPlanetEphemeris
    {
        public string ModelVersion => "fixed-day-performance-v1";

        public SolarSystemPosition GetPosition(SolarSystemBody body, DateTimeOffset utc)
        {
            Assert.AreEqual(SolarSystemBody.Sun, body);
            return new SolarSystemPosition(new EquatorialPoint(18.697374558, 0), -26.74);
        }
    }

    private readonly record struct LegacyAverageResult(
        double NormalizedMean,
        long ConsideredSamples,
        long ScannedBytes);

    private sealed record GitEvidence(
        string Head,
        string Branch,
        bool Dirty,
        string DirtyDiffSha256);

    private sealed record WorkloadEvidence(
        string Id,
        string CanonicalProfilePath,
        string CanonicalProfileSha256,
        int Width,
        int Height,
        string PixelFormat,
        int StrideBytes,
        long PayloadBytes,
        long ActivePixelBytes,
        string InputBytes,
        string PayloadSha256,
        CandidateMeasurement Candidate,
        BaselineMeasurement LegacyBaseline,
        RunnerScenarioEvidence DisabledPolicy,
        RunnerScenarioEvidence[] ProductionScenarios,
        double CandidateMinusBaselineNormalizedMean,
        string NumericalInterpretation);

    private sealed record CandidateMeasurement(
        string Implementation,
        int MeasurementCount,
        int OperationsPerMeasurement,
        double WallMedianMilliseconds,
        double WallP95Milliseconds,
        double CpuMedianMilliseconds,
        double CpuP95Milliseconds,
        long AllocatedBytesTotal,
        int AllocationOperations,
        double AllocatedBytesPerOperation,
        long ConsideredSamples,
        long AcceptedSamples,
        long SaturatedSamples,
        long ScannedBytes,
        double ScanRatioToPayload,
        double NormalizedMean,
        double OperationsPerSecond);

    private sealed record BaselineMeasurement(
        string Implementation,
        string Policy,
        int MeasurementCount,
        int OperationsPerMeasurement,
        double WallMedianMilliseconds,
        double WallP95Milliseconds,
        double CpuMedianMilliseconds,
        double CpuP95Milliseconds,
        long AllocatedBytesTotal,
        int AllocationOperations,
        double AllocatedBytesPerOperation,
        long ConsideredSamples,
        long ScannedBytes,
        double NormalizedMean,
        double OperationsPerSecond);

    private sealed record RunnerScenarioEvidence(
        string Scenario,
        string ControlOwnership,
        int WarmupCaptures,
        int MeasuredCaptures,
        string DeterministicScheduleSha256,
        string DeterministicCriticalTimingSha256,
        DistributionSummary RequestedVsActualStartJitterMilliseconds,
        CriticalDurationEvidence LogicalCriticalDurationsMilliseconds,
        CriticalDurationEvidence PhysicalCriticalDurationsMilliseconds,
        double CriticalCycleCapturesPerSecond,
        double WholeRunCapturesPerSecond,
        double ProcessCpuMilliseconds,
        long ManagedAllocatedBytes,
        double ManagedAllocatedBytesPerCapture,
        long RssBeforeBytes,
        long RssAfterBytes,
        long RssDeltaBytes,
        ProductionMeteringEvidence ProductionMetering,
        int MeteringMetricMeasurements,
        int FinalAcquisitionBacklog,
        int OutageEndOptionalBacklog,
        int OutageEndCentralBacklog,
        int PostReleaseOptionalBacklog,
        int PostReleaseCentralBacklog,
        int PayloadCopyCount,
        int DistinctPayloadOwners,
        string PayloadOwnershipInvariant,
        [property: System.Text.Json.Serialization.JsonIgnore] CriticalTimelineSample[] Timeline);

    private sealed record ProductionMeteringEvidence(
        int MeteringEvidenceCount,
        long ConsideredSamplesPerCapture,
        long ConsideredSamplesTotal,
        long ScannedBytesPerCapture,
        long ScannedBytesTotal,
        string[] Outcomes);

    private sealed record CriticalDurationEvidence(
        DistributionSummary Module,
        DistributionSummary SparseMetering,
        DistributionSummary ControlAndSetpoint,
        DistributionSummary SimulatedDurableIngressHandoff,
        DistributionSummary TotalCriticalCycle);

    private sealed record DistributionSummary(
        int SampleCount,
        double Median,
        double P95,
        double Maximum);

    private readonly record struct CriticalTimelineSample(
        DateTimeOffset RequestedStartUtc,
        DateTimeOffset ActualStartUtc,
        TimeSpan StartJitter,
        TimeSpan Module,
        TimeSpan Metering,
        TimeSpan Control,
        TimeSpan Setpoint,
        TimeSpan Ingress,
        TimeSpan Total,
        CaptureStartReason StartReason);
}
