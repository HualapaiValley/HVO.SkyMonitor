using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class ProcessingGraphExecutionPerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasuredCount = 30;
    private const int ReplayCount = 5;
    private const int GraphNodeCount = 3;
    private static readonly DateTimeOffset FixtureUtc = new(2026, 8, 31, 1, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly Workload[] Workloads =
    [
        new("W1", 1936, 1216, CameraPixelFormat.Mono16, 4_708_352),
        new("W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 12_879_360),
        new("W6", 3552, 3552, CameraPixelFormat.BayerRggb16, 25_233_408)
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task W1W2W6LiveExecutionAndReplayEvidence()
    {
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE423_424_EVIDENCE_ROOT")
            ?? Path.Combine(GetRepositoryRoot(), "TestResults", "issues-423-424", "working-tree");
        var workRoot = Path.Combine(outputRoot, "performance-work");
        Directory.CreateDirectory(outputRoot);
        if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
        Directory.CreateDirectory(workRoot);
        var measurements = new List<WorkloadMeasurement>(Workloads.Length);
        try
        {
            foreach (var workload in Workloads)
            {
                var payload = CreatePayload(workload.PayloadBytes);
                var baseline = await MeasureLiveAsync(
                    Path.Combine(workRoot, $"{workload.Id}-baseline"), workload, payload, durableExecutions: false)
                    .ConfigureAwait(false);
                var candidate = await MeasureLiveAsync(
                    Path.Combine(workRoot, $"{workload.Id}-candidate"), workload, payload, durableExecutions: true)
                    .ConfigureAwait(false);
                measurements.Add(new(
                    workload,
                    baseline.Live,
                    candidate.Live,
                    candidate.Replay!,
                    PercentChange(baseline.Live.MedianMilliseconds, candidate.Live.MedianMilliseconds),
                    PercentChange(baseline.Live.P95Milliseconds, candidate.Live.P95Milliseconds)));
            }

            var evidence = new
            {
                SchemaVersion = "issues-423-424-processing-execution-performance-v1",
                Revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "candidate-working-tree",
                DirtyState = "The harness records candidate working-tree evidence before the first push.",
                RecordedUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    Configuration = "Release",
                    ProcessorCount = Environment.ProcessorCount,
                    ServerGc = System.Runtime.GCSettings.IsServerGC,
                    Storage = "local filesystem and SQLite WAL",
                    Topology = "in-process CameraAgent service-provider harness with central integration and upload disabled"
                },
                Method = new
                {
                    WarmupCount,
                    MeasuredCount,
                    ReplayCount,
                    Concurrency = 1,
                    Scope = "Supplemental durable-checkpoint isolation at canonical W1/W2/W6 frame dimensions; this is not the standalone production-graph W6 campaign.",
                    Baseline = "Feature-isolation baseline using the same candidate binary with ProcessingGraphOperationsCoordinator removed; an exact base-revision campaign remains separate evidence.",
                    Candidate = "Durable live execution enabled through normal CameraAgent dependency injection and the same three-node graph.",
                    Replay = "Five sequential replay operations use an exact retained artifact, active immutable revision, local replay worker, and three full-payload scans. The first operation includes deliberate live preemption; median/min/max are reported without inferring p95.",
                    Sample = "Raw acceptance through the real ordered standard-lane handler and acknowledgement; replay submission through terminal graph execution.",
                    RegressionMethod = "Report absolute feature-off/feature-on values and percentage changes without a universal pass threshold; canonical base-revision and standalone-host evidence determine merge disposition."
                },
                Measurements = measurements,
                Correctness = new
                {
                    Checks = new[]
                    {
                        "raw payload byte length and SHA-256",
                        "three-node graph completion and full payload scan count",
                        "one durable live execution per accepted candidate capture",
                        "zero unfinished standard-lane and replay backlog after drain",
                        "terminal completed replay identity for the exact archived artifact",
                        "live acknowledgement while a full-resolution replay execution is durably running"
                    },
                    Result = "The harness fails on raw checksum, graph-node count, payload-scan count, execution-count, terminal-state, or final-backlog divergence."
                }
            };
            var outputPath = Path.Combine(outputRoot, "processing-execution-performance.json");
            await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceOptions))
                .ConfigureAwait(false);
            TestContext.WriteLine($"Issues #423/#424 performance evidence: {outputPath}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(workRoot)) Directory.Delete(workRoot, recursive: true);
        }
    }

    private static async Task<PathMeasurement> MeasureLiveAsync(
        string root,
        Workload workload,
        byte[] payload,
        bool durableExecutions)
    {
        Directory.CreateDirectory(root);
        var configuration = CreateConfiguration(workload);
        using var provider = CreateProvider(root, durableExecutions);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        ProcessingGraphOperationsCoordinator? operations = null;
        if (durableExecutions)
        {
            operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            _ = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        }
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(
            static handler => handler.Lane == "standard");
        var probe = provider.GetRequiredService<PerformanceProbeObservation>();
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");
        RawCaptureReceipt? lastReceipt = null;
        for (var index = 0; index < WarmupCount; index++)
        {
            lastReceipt = await AcceptAndAcknowledgeAsync(
                ingress, laneStore, laneHandler, standard, configuration, workload, payload, index, operations)
                .ConfigureAwait(false);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetBefore = process.WorkingSet64;
        var maximumWorkingSet = workingSetBefore;
        var lohBefore = GetLohSize();
        var samples = new double[MeasuredCount];
        for (var index = 0; index < MeasuredCount; index++)
        {
            var started = Stopwatch.GetTimestamp();
            lastReceipt = await AcceptAndAcknowledgeAsync(
                ingress, laneStore, laneHandler, standard, configuration, workload, payload, WarmupCount + index,
                operations)
                .ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            process.Refresh();
            maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
        }
        process.Refresh();
        var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var workingSetAfter = process.WorkingSet64;
        var lohAfter = GetLohSize();
        Array.Sort(samples);
        Assert.IsNotNull(lastReceipt);
        Assert.AreEqual(workload.PayloadBytes, new FileInfo(lastReceipt.StoredFrame.AbsolutePath).Length);
        using (var stored = File.OpenRead(lastReceipt.StoredFrame.AbsolutePath))
        {
            Assert.AreEqual(
                Convert.ToHexString(SHA256.HashData(payload)),
                Convert.ToHexString(await SHA256.HashDataAsync(stored).ConfigureAwait(false)));
        }

        var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
        var state = await ReadStateAsync(databasePath, durableExecutions).ConfigureAwait(false);
        Assert.AreEqual(0L, state.LiveBacklog);
        Assert.AreEqual(durableExecutions ? (long)(WarmupCount + MeasuredCount) : 0L, state.LiveExecutions);
        Assert.AreEqual((long)(WarmupCount + MeasuredCount) * GraphNodeCount, state.CompletedGraphNodes);
        Assert.AreEqual(
            (long)(WarmupCount + MeasuredCount) * GraphNodeCount * workload.PayloadBytes,
            probe.PayloadBytesScanned);
        var live = new LiveMeasurement(
            samples[MeasuredCount / 2],
            Percentile(samples, 0.95),
            samples[0],
            samples[^1],
            MeasuredCount / (samples.Sum() / 1000d),
            cpuMilliseconds,
            allocatedBytes,
            allocatedBytes / (double)MeasuredCount,
            workingSetBefore,
            maximumWorkingSet,
            workingSetAfter,
            lohBefore,
            lohAfter,
            (long)MeasuredCount * workload.PayloadBytes,
            new FileInfo(databasePath).Length,
            File.Exists($"{databasePath}-wal") ? new FileInfo($"{databasePath}-wal").Length : 0,
            state.LiveExecutions,
            state.LiveBacklog,
            state.CompletedGraphNodes,
            probe.PayloadBytesScanned);
        ReplayMeasurement? replay = null;
        if (durableExecutions)
        {
            replay = await MeasureReplayAsync(
                provider, operations!, configuration, ingress, laneStore, standard, workload, payload,
                WarmupCount + MeasuredCount, lastReceipt).ConfigureAwait(false);
        }
        return new(live, replay);
    }

    private static async Task<ReplayMeasurement> MeasureReplayAsync(
        ServiceProvider provider,
        ProcessingGraphOperationsCoordinator operations,
        CameraModuleConfig configuration,
        IRawCaptureIngress ingress,
        ICaptureLaneStore laneStore,
        CaptureLaneDefinition standard,
        Workload workload,
        byte[] payload,
        int sequence,
        RawCaptureReceipt source)
    {
        var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var worker = provider.GetRequiredService<ProcessingReplayWorker>();
        var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(
            static handler => handler.Lane == "standard");
        var probe = provider.GetRequiredService<PerformanceProbeObservation>();
        var scannedBefore = probe.PayloadBytesScanned;
        var samples = new double[ReplayCount];
        var liveWhileReplayMilliseconds = 0d;
        var replayWasRunning = false;
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < ReplayCount; index++)
            {
                if (index == 0) probe.BlockNextReplayUntilLivePreemption();
                var started = Stopwatch.GetTimestamp();
                var replay = await operations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(
                        source.Manifest.Descriptor.Capture.CaptureId,
                        registry.ActiveRevisionId,
                        source.Manifest.Descriptor.Artifact.ArtifactId,
                        TriggerReference: $"performance-{workload.Id}-{index}"),
                    $"performance-{workload.Id}-{index}",
                    "performance-harness",
                    CancellationToken.None).ConfigureAwait(false);
                if (index == 0)
                {
                    replayWasRunning = await WaitForRunningAsync(operations, replay.Execution.ExecutionId)
                        .ConfigureAwait(false);
                    Assert.IsTrue(replayWasRunning);
                    using (var replayStarted = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        await probe.WaitForReplayBlockAsync(replayStarted.Token).ConfigureAwait(false);
                    var liveStarted = Stopwatch.GetTimestamp();
                    _ = await AcceptAndAcknowledgeAsync(
                        ingress, laneStore, laneHandler, standard, configuration, workload, payload, sequence++,
                        operations)
                        .ConfigureAwait(false);
                    liveWhileReplayMilliseconds = Stopwatch.GetElapsedTime(liveStarted).TotalMilliseconds;
                }
                var terminal = await WaitForTerminalAsync(operations, replay.Execution.ExecutionId).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, terminal.Status);
                samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        Array.Sort(samples);
        var state = await ReadStateAsync(
            Path.Combine(provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CameraAgentHostOptions>>()
                .Value.RawIngressRoot, "journal", "raw-ingress.db"),
            durableExecutions: true).ConfigureAwait(false);
        Assert.AreEqual(0L, state.ReplayBacklog);
        var minimumReplayBytesScanned = (long)ReplayCount * GraphNodeCount * workload.PayloadBytes;
        Assert.IsGreaterThanOrEqualTo(minimumReplayBytesScanned, probe.PayloadBytesScanned - scannedBefore);
        return new(
            samples[ReplayCount / 2],
            samples[0],
            samples[^1],
            ReplayCount / (samples.Sum() / 1000d),
            minimumReplayBytesScanned,
            state.ReplayBacklog,
            replayWasRunning,
            replayWasRunning ? liveWhileReplayMilliseconds : null);
    }

    private static async Task<bool> WaitForRunningAsync(
        ProcessingGraphOperationsCoordinator operations,
        Guid executionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var execution = await operations.ReadExecutionAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            if (execution?.Status == ProcessingGraphExecutionStatus.Running) return true;
            if (execution?.Status is ProcessingGraphExecutionStatus.Completed or ProcessingGraphExecutionStatus.Failed)
                return false;
            await Task.Delay(1).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task<ProcessingGraphExecutionState> WaitForTerminalAsync(
        ProcessingGraphOperationsCoordinator operations,
        Guid executionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        ProcessingGraphExecutionState? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await operations.ReadExecutionAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            if (last?.Status is ProcessingGraphExecutionStatus.Completed or ProcessingGraphExecutionStatus.Failed or
                ProcessingGraphExecutionStatus.Cancelled or ProcessingGraphExecutionStatus.Expired)
                return last;
            await Task.Delay(5).ConfigureAwait(false);
        }
        var detail = await operations.ReadExecutionDetailAsync(executionId, CancellationToken.None).ConfigureAwait(false);
        var nodes = detail is null
            ? "missing"
            : string.Join(",", detail.Nodes.Select(static node =>
                $"{node.NodeId}:{node.Status}:attempts={node.AttemptCount}"));
        throw new TimeoutException(
            $"The replay execution did not reach a terminal state (status={last?.Status}, reason={last?.FailureReason}, nodes={nodes}).");
    }

    private static async Task<RawCaptureReceipt> AcceptAndAcknowledgeAsync(
        IRawCaptureIngress ingress,
        ICaptureLaneStore laneStore,
        ICaptureLaneHandler laneHandler,
        CaptureLaneDefinition standard,
        CameraModuleConfig configuration,
        Workload workload,
        byte[] payload,
        int index,
        ProcessingGraphOperationsCoordinator? operations)
    {
        var receipt = await ingress.AcceptAsync(
            configuration, CreateSubmission(workload, payload, index), CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var lease = await laneStore.ClaimAsync(
            standard, "performance-live", configuration, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await laneHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        operations?.NotifyLiveWorkChanged();
        return receipt;
    }

    private static ServiceProvider CreateProvider(string root, bool durableExecutions)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["CameraAgent:RawIngressRoot"] = root,
                ["CameraAgent:RawIngressReserveBytes"] = "0",
                ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
                ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1",
                ["CameraAgent:ProcessingGraphs:ReplayMaximumPendingBytes"] = (2L * 1024 * 1024 * 1024).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            }).Build());
        services.AddSingleton<PerformanceProbeObservation>();
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "ExecutionPerformanceProbe",
            typeof(PerformanceProbeStep),
            typeof(PerformanceProbeOptions),
            AutoInclude: false));
        if (!durableExecutions)
        {
            services.RemoveAll<ProcessingGraphOperationsCoordinator>();
        }
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig CreateConfiguration(Workload workload)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile(
                    $"{workload.Id}-sensor",
                    workload.Width,
                    workload.Height,
                    1,
                    workload.PixelFormat == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                    workload.PixelFormat),
                new OpticsProfile("performance", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            new CapturePipelineConfig(
                [
                    CreateProbeNode("decode", FrameArtifactRole.Preview, ["$raw"], workload.PayloadBytes),
                    CreateProbeNode("analyze", FrameArtifactRole.AnnotatedPreview, ["decode"], workload.PayloadBytes),
                    CreateProbeNode("checkpoint", FrameArtifactRole.Metadata, ["analyze"], workload.PayloadBytes)
                ], CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            $"performance-{workload.Id}");

    private static CaptureProcessingStepConfig CreateProbeNode(
        string id,
        FrameArtifactRole outputRole,
        IReadOnlyList<string> dependencies,
        int expectedPayloadBytes)
        => new(
            "ExecutionPerformanceProbe",
            id,
            Options: JsonSerializer.SerializeToElement(new
            {
                expectedPayloadBytes,
                outputRole = (int)outputRole
            }),
            DependsOn: dependencies);

    private static CaptureLoopSubmission CreateSubmission(Workload workload, byte[] payload, int index)
    {
        var startedUtc = FixtureUtc.AddSeconds(index * 2L);
        var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
        var frame = new CameraFrame(
            startedUtc,
            workload.Width,
            workload.Height,
            workload.PixelFormat,
            payload,
            new FrameMetadata(TimeSpan.FromSeconds(1), 1, 10, workload.Id));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, TimeSpan.FromSeconds(2), CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc, startedUtc.AddSeconds(1), startedUtc.AddSeconds(1.1))
            },
            startedUtc,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2));
    }

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 31 + 17);
        return payload;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query is selected from two fixed internal statements and contains no external input.")]
    private static async Task<DurableState> ReadStateAsync(string databasePath, bool durableExecutions)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = durableExecutions
            ? """
              SELECT
                  (SELECT COUNT(*) FROM processing_executions WHERE execution_class = 'Live'),
                  (SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state != 'completed'),
                  (SELECT COUNT(*) FROM processing_replay_work WHERE state IN ('Pending', 'Leased', 'RetryWait')),
                  (SELECT COUNT(*) FROM processing_execution_nodes node
                   JOIN processing_executions execution ON execution.execution_id = node.execution_id
                   WHERE execution.execution_class = 'Live' AND node.status = 'Completed');
              """
            : """
              SELECT 0,
                  (SELECT COUNT(*) FROM capture_lane_work WHERE lane_name = 'standard' AND state != 'completed'),
                  0,
                  (SELECT COUNT(*) FROM processing_nodes WHERE status = 'Completed');
              """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];

    private static double PercentChange(double baseline, double candidate)
        => (candidate - baseline) * 100d / baseline;

    private static long GetLohSize()
    {
        var generations = GC.GetGCMemoryInfo().GenerationInfo;
        return generations.Length > 3 ? generations[3].SizeAfterBytes : 0;
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HVO.SkyMonitor.v9.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat PixelFormat,
        int PayloadBytes);

    private sealed record PathMeasurement(LiveMeasurement Live, ReplayMeasurement? Replay);

    private sealed record WorkloadMeasurement(
        Workload Workload,
        LiveMeasurement Baseline,
        LiveMeasurement Candidate,
        ReplayMeasurement Replay,
        double LiveMedianChangePercent,
        double LiveP95ChangePercent);

    private sealed record LiveMeasurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double CapturesPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        double AllocatedBytesPerCapture,
        long WorkingSetBeforeBytes,
        long MaximumWorkingSetBytes,
        long WorkingSetAfterBytes,
        long LohBeforeBytes,
        long LohAfterBytes,
        long PayloadBytesWritten,
        long DatabaseBytes,
        long WalBytes,
        long LiveExecutionCount,
        long LiveBacklogCount,
        long CompletedGraphNodeCount,
        long PayloadBytesScanned);

    private sealed record ReplayMeasurement(
        double MedianMilliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double ReplaysPerSecond,
        long MinimumPayloadBytesRead,
        long ReplayBacklogCount,
        bool ReplayWasRunningAtLiveStart,
        double? LiveLatencyWhileReplayRunningMilliseconds);

    private sealed record DurableState(
        long LiveExecutions,
        long LiveBacklog,
        long ReplayBacklog,
        long CompletedGraphNodes);

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Dependency injection creates this test observation.")]
    private sealed class PerformanceProbeObservation
    {
        private TaskCompletionSource<bool> _replayBlockEntered = CreateReplayBlockSignal();
        private long _payloadBytesScanned;
        private int _blockNextReplay;

        public long PayloadBytesScanned => Interlocked.Read(ref _payloadBytesScanned);

        public void Record(int payloadBytes) => Interlocked.Add(ref _payloadBytesScanned, payloadBytes);

        public void BlockNextReplayUntilLivePreemption()
        {
            Volatile.Write(ref _replayBlockEntered, CreateReplayBlockSignal());
            Interlocked.Exchange(ref _blockNextReplay, 1);
        }

        public Task<bool> WaitForReplayBlockAsync(CancellationToken cancellationToken) =>
            Volatile.Read(ref _replayBlockEntered).Task.WaitAsync(cancellationToken);

        public bool ConsumeReplayBlock()
        {
            if (Interlocked.Exchange(ref _blockNextReplay, 0) != 1) return false;
            Volatile.Read(ref _replayBlockEntered).TrySetResult(true);
            return true;
        }

        private static TaskCompletionSource<bool> CreateReplayBlockSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The processing pipeline factory deserializes this test options type.")]
    private sealed class PerformanceProbeOptions
    {
        public int ExpectedPayloadBytes { get; init; }

        public FrameArtifactRole OutputRole { get; init; }
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The processing pipeline factory creates this test step through ActivatorUtilities.")]
    private sealed class PerformanceProbeStep(
        CaptureProcessingStepMetadata metadata,
        PerformanceProbeOptions options,
        PerformanceProbeObservation observation)
        : ConfigurableCaptureProcessingStep<PerformanceProbeOptions>(metadata, options), ICaptureProcessingGraphStep
    {
        private static readonly IReadOnlySet<FrameArtifactRole> AcceptedRoles =
            new HashSet<FrameArtifactRole>(Enum.GetValues<FrameArtifactRole>());
        private static long _lastChecksum;

        public bool Enabled => true;

        public string RecipeName => "execution-performance-probe";

        public FrameArtifactRole OutputRole => Options.OutputRole;

        public string OutputVariant => Metadata.Id;

        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles => AcceptedRoles;

        public override ValueTask ProcessAsync(
            CaptureProcessingContext context,
            CancellationToken cancellationToken)
        {
            var frame = context.Frame
                ?? throw new InvalidDataException("The performance graph did not reconstruct its raw payload.");
            var payload = frame.PixelData.Span;
            if (payload.Length != Options.ExpectedPayloadBytes)
                throw new InvalidDataException("The performance graph reconstructed an unexpected payload length.");
            long checksum = 17;
            for (var index = 0; index < payload.Length; index++)
            {
                if ((index & 0xFFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                checksum = unchecked(checksum * 31 + payload[index]);
            }
            Interlocked.Exchange(ref _lastChecksum, checksum);
            observation.Record(payload.Length);
            context.AddProcessingOutcome(ProcessingOutcome.Produced());
            return context.ProcessingExecution?.ExecutionClass == ProcessingGraphExecutionClass.Replay &&
                   observation.ConsumeReplayBlock()
                ? new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
                : ValueTask.CompletedTask;
        }
    }
}
