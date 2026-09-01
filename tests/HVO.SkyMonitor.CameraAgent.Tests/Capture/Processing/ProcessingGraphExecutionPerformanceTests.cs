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
    private const int ReplayWarmupCount = 5;
    private const int ReplayMeasuredCount = 30;
    private const int SimultaneousReplayCount = 240;
    private const int SimultaneousLiveCount = 30;
    private const int GraphNodeCount = 4;
    private const int PayloadScanNodeCount = 3;
    private const double LiveLatencyBudgetMilliseconds = 2_000;
    private const double SimultaneousLiveCadenceMilliseconds = 100;
    private const double CadenceStartToleranceMilliseconds = 10;
    private static readonly TimeSpan DurableStateSamplingInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan ResourceSamplingInterval = TimeSpan.FromMilliseconds(10);
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
        var repositoryRoot = GetRepositoryRoot();
        var revision = new RevisionEvidence(
            RequireEvidenceValue("HVO_EVIDENCE_BASE_REVISION"),
            RequireEvidenceValue("HVO_EVIDENCE_REVISION"),
            RequireEvidenceValue("HVO_EVIDENCE_BRANCH"),
            RequireEvidenceValue("HVO_EVIDENCE_DIRTY_STATE"));
        VerifyRevisionEvidence(repositoryRoot, revision);
        var storage = RequireEvidenceValue("HVO_EVIDENCE_STORAGE");
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
                var replay = candidate.Replay
                    ?? throw new InvalidOperationException("The durable candidate did not produce replay evidence.");
                measurements.Add(new(
                    workload,
                    baseline.Live,
                    candidate.Live,
                    replay,
                    PercentChange(baseline.Live.MedianMilliseconds, candidate.Live.MedianMilliseconds),
                    PercentChange(baseline.Live.P95Milliseconds, candidate.Live.P95Milliseconds),
                    PercentChange(candidate.Live.MedianMilliseconds, replay.SimultaneousLive.MedianMilliseconds),
                    PercentChange(candidate.Live.P95Milliseconds, replay.SimultaneousLive.P95Milliseconds),
                    PercentChange(
                        candidate.Live.CapturesPerSecond,
                        replay.SimultaneousLive.ServiceCapturesPerSecond)));
            }

            var evidence = new
            {
                SchemaVersion = "issues-423-424-processing-execution-performance-v2",
                Revision = revision,
                RecordedUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    Configuration = "Release",
                    ProcessorCount = Environment.ProcessorCount,
                    ProcessorModel = ReadLinuxValue("/proc/cpuinfo", "model name"),
                    TotalMemoryBytes = ReadLinuxMemoryBytes(),
                    ServerGc = System.Runtime.GCSettings.IsServerGC,
                    Storage = new
                    {
                        DeclaredType = storage,
                        FileSystem = ReadFileSystemType(outputRoot)
                    },
                    SqliteVersion = ReadSqliteVersion(),
                    SqliteProviderVersion = typeof(SqliteConnection).Assembly.GetName().Version?.ToString(),
                    Topology = "in-process CameraAgent service-provider harness with central integration and upload disabled"
                },
                Provenance = new
                {
                    HarnessSha256 = ComputeFileSha256(Path.Combine(
                        repositoryRoot,
                        "tests/HVO.SkyMonitor.CameraAgent.Tests/Capture/Processing/ProcessingGraphExecutionPerformanceTests.cs")),
                    TestAssemblySha256 = ComputeFileSha256(typeof(ProcessingGraphExecutionPerformanceTests).Assembly.Location),
                    ReproductionCommandTemplate = "HVO_EVIDENCE_BASE_REVISION=<base> HVO_EVIDENCE_REVISION=<candidate> HVO_EVIDENCE_BRANCH=<branch> HVO_EVIDENCE_DIRTY_STATE=clean HVO_EVIDENCE_STORAGE=<storage> HVO_ISSUE423_424_EVIDENCE_ROOT=<output> dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --filter FullyQualifiedName~ProcessingGraphExecutionPerformanceTests.W1W2W6LiveExecutionAndReplayEvidence"
                },
                Method = new
                {
                    WarmupCount,
                    MeasuredCount,
                    ReplayWarmupCount,
                    ReplayMeasuredCount,
                    SimultaneousReplayCount,
                    SimultaneousLiveCount,
                    Concurrency = 1,
                    LiveLatencyBudgetMilliseconds,
                    SimultaneousLiveCadenceMilliseconds,
                    CadenceStartToleranceMilliseconds,
                    DurableStateSamplingIntervalMilliseconds = DurableStateSamplingInterval.TotalMilliseconds,
                    ResourceSamplingIntervalMilliseconds = ResourceSamplingInterval.TotalMilliseconds,
                    Scope = "Supplemental durable-checkpoint isolation at canonical W1/W2/W6 frame dimensions; this is not the standalone production-graph W6 campaign.",
                    Baseline = "Feature-isolation baseline using the same candidate binary with ProcessingGraphOperationsCoordinator removed. The code baseline commit is provenance, not a second executable comparator, because the harness and execution contracts do not exist there.",
                    Candidate = "Durable live execution enabled through normal CameraAgent dependency injection and the same four-node graph, consisting of one descriptor-only timing barrier and three full-payload probes.",
                    Replay = "After five warm-ups, 30 replay operations are durably queued behind a controlled worker block and drained without live work for isolated resource and I/O measurements. A separate 8:1 backlog of 240 queued replays drains while 30 live captures arrive on a paced 100 ms accelerated schedule; the first replay is deliberately preempted by live acceptance.",
                    Sample = "Raw acceptance through the real ordered standard-lane handler and acknowledgement; replay submission through terminal graph execution.",
                    Counters = "Process CPU, managed allocation and retained LOH after full collections, 10 ms process working-set samples, Linux /proc/self/io logical/physical bytes and syscall counts, SQLite database/WAL sizes, and 25 ms durable queue count/bytes/oldest-age samples. Submission I/O combines source resolution and frozen-input validation; execution I/O is measured separately. Observer poll counts are reported.",
                    RegressionMethod = "Report absolute feature-off/feature-on live values, percentage changes, absolute replay baselines, and simultaneous-live percentage changes without a universal pass threshold; canonical standalone-host evidence determines merge disposition."
                },
                Measurements = measurements,
                Correctness = new
                {
                    Checks = new[]
                    {
                        "raw payload byte length and SHA-256",
                        "four-node graph completion and three full-payload scans",
                        "one durable live execution per accepted candidate capture",
                        "zero unfinished standard-lane and replay backlog after drain",
                        "terminal completed replay identity for the exact archived artifact",
                        "30 live acknowledgements while a full-resolution replay backlog is durably draining",
                        "durable peak replay count/bytes and completed terminal-count convergence"
                    },
                    Result = "The harness fails on raw checksum, graph-node count, payload-scan count, execution-count, terminal-state, peak/final backlog, source-read, or simultaneous-live divergence."
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
            (long)(WarmupCount + MeasuredCount) * PayloadScanNodeCount * workload.PayloadBytes,
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
            probe.PayloadBytesScanned,
            LiveLatencyBudgetMilliseconds,
            samples.Count(sample => sample > LiveLatencyBudgetMilliseconds),
            LiveLatencyBudgetMilliseconds - samples[^1]);
        ReplayMeasurement? replay = null;
        if (durableExecutions)
        {
            replay = await MeasureReplayAsync(
                provider, operations!, configuration, ingress, laneStore, standard, workload, payload,
                WarmupCount + MeasuredCount, lastReceipt, databasePath).ConfigureAwait(false);
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
        RawCaptureReceipt source,
        string databasePath)
    {
        var registry = await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false);
        var worker = provider.GetRequiredService<ProcessingReplayWorker>();
        var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(
            static handler => handler.Lane == "standard");
        var probe = provider.GetRequiredService<PerformanceProbeObservation>();
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            for (var index = 0; index < ReplayWarmupCount; index++)
            {
                var replay = await SubmitReplayAsync(
                    operations, registry.ActiveRevisionId, source, workload.Id, "warmup", index)
                    .ConfigureAwait(false);
                var terminal = await WaitForTerminalAsync(operations, replay.Execution.ExecutionId).ConfigureAwait(false);
                Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, terminal.Status);
            }

            var isolated = await MeasureReplayOnlyAsync(
                operations, registry.ActiveRevisionId, source, workload, probe, databasePath)
                .ConfigureAwait(false);
            var simultaneous = await MeasureSimultaneousLiveAsync(
                operations, registry.ActiveRevisionId, source, workload, payload, probe, databasePath,
                ingress, laneStore, laneHandler, standard, configuration, sequence)
                .ConfigureAwait(false);
            return new(isolated, simultaneous);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<ReplayResourceMeasurement> MeasureReplayOnlyAsync(
        ProcessingGraphOperationsCoordinator operations,
        string revisionId,
        RawCaptureReceipt source,
        Workload workload,
        PerformanceProbeObservation probe,
        string databasePath)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var beforeState = await ReadReplayQueueStateAsync(databasePath).ConfigureAwait(false);
        Assert.AreEqual(0L, beforeState.PendingCount);
        var scannedBefore = probe.PayloadBytesScanned;
        var databaseBytesBefore = GetFileLength(databasePath);
        var walBytesBefore = GetFileLength($"{databasePath}-wal");
        var processIoBefore = ReadProcessIo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetBefore = process.WorkingSet64;
        var lohBefore = GetLohSize();
        using var sampleCancellation = new CancellationTokenSource();
        var peakWorkingSetTask = SamplePeakWorkingSetAsync(process, workingSetBefore, sampleCancellation.Token);
        ReplayBatch? completedBatch = null;
        ReplayDrainObservation? completedDrain = null;
        var measurementStarted = Stopwatch.GetTimestamp();
        var drainMilliseconds = 0d;
        try
        {
            completedBatch = await SubmitBlockedReplayBatchAsync(
                operations, revisionId, source, workload, probe, "isolated", ReplayMeasuredCount, databasePath)
                .ConfigureAwait(false);
            var processIoAfterSubmission = ReadProcessIo();
            var drainStarted = Stopwatch.GetTimestamp();
            probe.ReleaseReplayBlock();
            completedDrain = await WaitForReplayBacklogDrainAsync(databasePath).ConfigureAwait(false);
            drainMilliseconds = Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds;
            completedBatch = completedBatch with { ProcessIoAfterSubmission = processIoAfterSubmission };
        }
        finally
        {
            await sampleCancellation.CancelAsync().ConfigureAwait(false);
        }
        var maximumWorkingSet = await peakWorkingSetTask.ConfigureAwait(false);
        var batch = completedBatch
            ?? throw new InvalidOperationException("The isolated replay batch was not submitted.");
        var drainObservation = completedDrain
            ?? throw new InvalidOperationException("The isolated replay batch did not drain.");
        var totalMilliseconds = Stopwatch.GetElapsedTime(measurementStarted).TotalMilliseconds;
        process.Refresh();
        var cpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var workingSetAfter = process.WorkingSet64;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var lohAfter = GetLohSize();
        var processIoAfter = ReadProcessIo();
        var finalState = drainObservation.FinalState;
        Assert.AreEqual(0L, finalState.PendingCount);
        Assert.AreEqual((long)ReplayMeasuredCount, finalState.CompletedCount - beforeState.CompletedCount);
        Assert.AreEqual(0L, finalState.TerminalFailureCount - beforeState.TerminalFailureCount);
        var durations = await ReadCompletedReplayDurationsAsync(operations, batch.Executions).ConfigureAwait(false);
        Array.Sort(durations);
        var submissionDurations = batch.Executions.Select(static item => item.SubmissionMilliseconds).ToArray();
        Array.Sort(submissionDurations);
        var inMemoryPayloadBytesScanned = probe.PayloadBytesScanned - scannedBefore;
        var expectedPayloadBytesScanned = (long)ReplayMeasuredCount * PayloadScanNodeCount * workload.PayloadBytes;
        Assert.AreEqual(expectedPayloadBytesScanned, inMemoryPayloadBytesScanned);
        var processIo = CalculateProcessIoDelta(processIoBefore, processIoAfter);
        var submissionProcessIo = CalculateProcessIoDelta(processIoBefore, batch.ProcessIoAfterSubmission);
        var executionProcessIo = CalculateProcessIoDelta(batch.ProcessIoAfterSubmission, processIoAfter);
        var expectedSourceResolutionPayloadBytes =
            (long)ReplayMeasuredCount * workload.PayloadBytes;
        var expectedInputFreezeValidationPayloadBytes =
            (long)ReplayMeasuredCount * workload.PayloadBytes;
        var expectedExecutionSourcePayloadBytes = (long)ReplayMeasuredCount * workload.PayloadBytes;
        var minimumSourcePayloadBytesRead =
            expectedSourceResolutionPayloadBytes +
            expectedInputFreezeValidationPayloadBytes +
            expectedExecutionSourcePayloadBytes;
        var minimumFullFrameLohBytesAllocated =
            expectedSourceResolutionPayloadBytes + expectedExecutionSourcePayloadBytes;
        var minimumSubmissionPayloadBytesRead =
            expectedSourceResolutionPayloadBytes + expectedInputFreezeValidationPayloadBytes;
        if (submissionProcessIo is not null)
        {
            Assert.IsGreaterThanOrEqualTo(minimumSubmissionPayloadBytesRead, submissionProcessIo.LogicalReadBytes);
        }
        if (executionProcessIo is not null)
        {
            Assert.IsGreaterThanOrEqualTo(expectedExecutionSourcePayloadBytes, executionProcessIo.LogicalReadBytes);
        }
        if (processIo is not null)
        {
            Assert.IsGreaterThanOrEqualTo(minimumSourcePayloadBytesRead, processIo.LogicalReadBytes);
        }

        var databaseBytesAfter = GetFileLength(databasePath);
        var walBytesAfter = GetFileLength($"{databasePath}-wal");
        return new(
            durations[ReplayMeasuredCount / 2],
            Percentile(durations, 0.95),
            durations[0],
            durations[^1],
            submissionDurations[ReplayMeasuredCount / 2],
            Percentile(submissionDurations, 0.95),
            totalMilliseconds,
            drainMilliseconds,
            ReplayMeasuredCount / (totalMilliseconds / 1000d),
            batch.PeakState.PendingBytes / (totalMilliseconds / 1000d),
            cpuMilliseconds,
            cpuMilliseconds / ReplayMeasuredCount,
            allocatedBytes,
            allocatedBytes / (double)ReplayMeasuredCount,
            workingSetBefore,
            maximumWorkingSet,
            workingSetAfter,
            lohBefore,
            lohAfter,
            expectedSourceResolutionPayloadBytes,
            expectedInputFreezeValidationPayloadBytes,
            expectedExecutionSourcePayloadBytes,
            minimumSourcePayloadBytesRead,
            minimumFullFrameLohBytesAllocated,
            inMemoryPayloadBytesScanned,
            processIo,
            submissionProcessIo,
            executionProcessIo,
            databaseBytesBefore,
            databaseBytesAfter,
            databaseBytesAfter - databaseBytesBefore,
            walBytesBefore,
            walBytesAfter,
            walBytesAfter - walBytesBefore,
            batch.QueueFillMilliseconds,
            batch.PeakState.PendingCount,
            batch.PeakState.PendingBytes,
            drainObservation.MaximumOldestAgeMilliseconds,
            ReplayMeasuredCount / (drainMilliseconds / 1000d),
            batch.PeakState.PendingBytes / (drainMilliseconds / 1000d),
            drainObservation.SampleCount,
            finalState.PendingCount,
            finalState.PendingBytes,
            finalState.CompletedCount - beforeState.CompletedCount,
            finalState.TerminalFailureCount - beforeState.TerminalFailureCount);
    }

    private static async Task<SimultaneousLiveMeasurement> MeasureSimultaneousLiveAsync(
        ProcessingGraphOperationsCoordinator operations,
        string revisionId,
        RawCaptureReceipt source,
        Workload workload,
        byte[] payload,
        PerformanceProbeObservation probe,
        string databasePath,
        IRawCaptureIngress ingress,
        ICaptureLaneStore laneStore,
        ICaptureLaneHandler laneHandler,
        CaptureLaneDefinition standard,
        CameraModuleConfig configuration,
        int sequence)
    {
        var beforeState = await ReadReplayQueueStateAsync(databasePath).ConfigureAwait(false);
        Assert.AreEqual(0L, beforeState.PendingCount);
        var scannedBefore = probe.PayloadBytesScanned;
        var preemptionsBefore = probe.ReplayBlockCancellationCount;
        var batch = await SubmitBlockedReplayBatchAsync(
            operations, revisionId, source, workload, probe, "simultaneous", SimultaneousReplayCount, databasePath)
            .ConfigureAwait(false);
        var replayDrainStarted = Stopwatch.GetTimestamp();
        var replayDrain = WaitForReplayBacklogDrainAsync(databasePath);
        var scheduleStarted = Stopwatch.GetTimestamp();
        var liveSamples = new double[SimultaneousLiveCount];
        var startJitterSamples = new double[SimultaneousLiveCount];
        var liveOperationsStartedBeforeReplayDrain = 0;
        var lastStartMilliseconds = 0d;
        for (var index = 0; index < SimultaneousLiveCount; index++)
        {
            var targetStartMilliseconds = index * SimultaneousLiveCadenceMilliseconds;
            var untilTarget = targetStartMilliseconds - Stopwatch.GetElapsedTime(scheduleStarted).TotalMilliseconds;
            if (untilTarget > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(untilTarget)).ConfigureAwait(false);
            }
            lastStartMilliseconds = Stopwatch.GetElapsedTime(scheduleStarted).TotalMilliseconds;
            startJitterSamples[index] = Math.Max(0, lastStartMilliseconds - targetStartMilliseconds);
            if (!replayDrain.IsCompleted) liveOperationsStartedBeforeReplayDrain++;
            var operationStarted = Stopwatch.GetTimestamp();
            _ = await AcceptAndAcknowledgeAsync(
                ingress, laneStore, laneHandler, standard, configuration, workload, payload, sequence++, operations)
                .ConfigureAwait(false);
            liveSamples[index] = Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds;
        }
        var liveDurationMilliseconds = Stopwatch.GetElapsedTime(scheduleStarted).TotalMilliseconds;
        var replayDrainObservation = await replayDrain.ConfigureAwait(false);
        var replayDrainMilliseconds = Stopwatch.GetElapsedTime(replayDrainStarted).TotalMilliseconds;
        Assert.AreEqual(SimultaneousLiveCount, liveOperationsStartedBeforeReplayDrain);
        var finalReplayState = replayDrainObservation.FinalState;
        Assert.AreEqual(0L, finalReplayState.PendingCount);
        Assert.AreEqual((long)SimultaneousReplayCount, finalReplayState.CompletedCount - beforeState.CompletedCount);
        Assert.AreEqual(0L, finalReplayState.TerminalFailureCount - beforeState.TerminalFailureCount);
        Assert.AreEqual(preemptionsBefore + 1, probe.ReplayBlockCancellationCount);
        var finalState = await ReadStateAsync(databasePath, durableExecutions: true).ConfigureAwait(false);
        Assert.AreEqual((long)(WarmupCount + MeasuredCount + SimultaneousLiveCount), finalState.LiveExecutions);
        Assert.AreEqual(0L, finalState.LiveBacklog);
        Assert.AreEqual(
            (long)(WarmupCount + MeasuredCount + SimultaneousLiveCount) * GraphNodeCount,
            finalState.CompletedGraphNodes);
        var replayDurations = await ReadCompletedReplayDurationsAsync(operations, batch.Executions).ConfigureAwait(false);
        Array.Sort(replayDurations);
        var expectedMinimumScanned =
            (long)(SimultaneousReplayCount + SimultaneousLiveCount) * PayloadScanNodeCount * workload.PayloadBytes;
        Assert.IsGreaterThanOrEqualTo(expectedMinimumScanned, probe.PayloadBytesScanned - scannedBefore);
        Array.Sort(liveSamples);
        Array.Sort(startJitterSamples);
        return new(
            liveSamples[SimultaneousLiveCount / 2],
            Percentile(liveSamples, 0.95),
            liveSamples[0],
            liveSamples[^1],
            liveDurationMilliseconds,
            SimultaneousLiveCount / (liveSamples.Sum() / 1000d),
            SimultaneousLiveCount / (liveDurationMilliseconds / 1000d),
            (SimultaneousLiveCount - 1) / (lastStartMilliseconds / 1000d),
            SimultaneousLiveCadenceMilliseconds,
            CadenceStartToleranceMilliseconds,
            startJitterSamples[SimultaneousLiveCount / 2],
            Percentile(startJitterSamples, 0.95),
            startJitterSamples[^1],
            startJitterSamples.Count(sample => sample > CadenceStartToleranceMilliseconds),
            liveOperationsStartedBeforeReplayDrain,
            batch.PeakState.PendingCount,
            batch.PeakState.PendingBytes,
            replayDrainObservation.MaximumOldestAgeMilliseconds,
            replayDrainMilliseconds,
            SimultaneousReplayCount / (replayDrainMilliseconds / 1000d),
            replayDrainObservation.SampleCount,
            finalReplayState.PendingCount,
            finalReplayState.PendingBytes,
            probe.ReplayBlockCancellationCount == preemptionsBefore + 1);
    }

    private static ValueTask<ProcessingReplaySubmissionResult> SubmitReplayAsync(
        ProcessingGraphOperationsCoordinator operations,
        string revisionId,
        RawCaptureReceipt source,
        string workloadId,
        string phase,
        int index)
        => operations.SubmitReplayAsync(
            new ProcessingReplaySubmission(
                source.Manifest.Descriptor.Capture.CaptureId,
                revisionId,
                source.Manifest.Descriptor.Artifact.ArtifactId,
                TriggerReference: $"performance-{workloadId}-{phase}-{index}"),
            $"performance-{workloadId}-{phase}-{index}",
            "performance-harness",
            CancellationToken.None);

    private static async Task<ReplayBatch> SubmitBlockedReplayBatchAsync(
        ProcessingGraphOperationsCoordinator operations,
        string revisionId,
        RawCaptureReceipt source,
        Workload workload,
        PerformanceProbeObservation probe,
        string phase,
        int count,
        string databasePath)
    {
        var queueFillStarted = Stopwatch.GetTimestamp();
        probe.BlockNextReplay();
        var executions = new List<ReplayBatchItem>(count);
        var firstSubmittedUtc = DateTimeOffset.UtcNow;
        var firstSubmissionStarted = Stopwatch.GetTimestamp();
        var first = await SubmitReplayAsync(operations, revisionId, source, workload.Id, phase, 0)
            .ConfigureAwait(false);
        executions.Add(new(
            first.Execution,
            firstSubmittedUtc,
            Stopwatch.GetElapsedTime(firstSubmissionStarted).TotalMilliseconds));
        using (var replayStarted = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            _ = await probe.WaitForReplayBlockAsync(replayStarted.Token).ConfigureAwait(false);
        for (var index = 1; index < count; index++)
        {
            var submittedUtc = DateTimeOffset.UtcNow;
            var submissionStarted = Stopwatch.GetTimestamp();
            var replay = await SubmitReplayAsync(operations, revisionId, source, workload.Id, phase, index)
                .ConfigureAwait(false);
            executions.Add(new(
                replay.Execution,
                submittedUtc,
                Stopwatch.GetElapsedTime(submissionStarted).TotalMilliseconds));
        }

        var queueFillMilliseconds = Stopwatch.GetElapsedTime(queueFillStarted).TotalMilliseconds;
        var peakState = await ReadReplayQueueStateAsync(databasePath).ConfigureAwait(false);
        Assert.AreEqual((long)count, peakState.PendingCount);
        Assert.AreEqual((long)count * workload.PayloadBytes, peakState.PendingBytes);
        Assert.IsNotNull(peakState.OldestPendingUtc);
        Assert.IsTrue(executions.All(item =>
            item.Execution.CaptureId == source.Manifest.Descriptor.Capture.CaptureId &&
            item.Execution.PrimaryArtifactId == source.Manifest.Descriptor.Artifact.ArtifactId &&
            string.Equals(item.Execution.GraphRevisionId, revisionId, StringComparison.Ordinal)));
        return new(executions, queueFillMilliseconds, peakState);
    }

    private static async Task<ReplayDrainObservation> WaitForReplayBacklogDrainAsync(string databasePath)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        var maximumOldestAgeMilliseconds = 0d;
        var sampleCount = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await ReadReplayQueueStateAsync(databasePath).ConfigureAwait(false);
            sampleCount++;
            if (state.OldestPendingUtc is { } oldest)
            {
                maximumOldestAgeMilliseconds = Math.Max(
                    maximumOldestAgeMilliseconds,
                    (DateTimeOffset.UtcNow - oldest).TotalMilliseconds);
            }
            if (state.PendingCount == 0)
            {
                return new(state, maximumOldestAgeMilliseconds, sampleCount);
            }
            await Task.Delay(DurableStateSamplingInterval).ConfigureAwait(false);
        }
        throw new TimeoutException("The replay backlog did not drain within ten minutes.");
    }

    private static async Task<double[]> ReadCompletedReplayDurationsAsync(
        ProcessingGraphOperationsCoordinator operations,
        IReadOnlyList<ReplayBatchItem> executions)
    {
        var durations = new double[executions.Count];
        for (var index = 0; index < executions.Count; index++)
        {
            var execution = await operations.ReadExecutionAsync(
                executions[index].Execution.ExecutionId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(execution);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, execution.Status, execution.FailureReason);
            Assert.IsNotNull(execution.CompletedUtc);
            durations[index] = (execution.CompletedUtc.Value - executions[index].SubmittedUtc).TotalMilliseconds;
        }
        return durations;
    }

    private static async Task<long> SamplePeakWorkingSetAsync(
        Process process,
        long initialWorkingSet,
        CancellationToken cancellationToken)
    {
        var maximumWorkingSet = initialWorkingSet;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                process.Refresh();
                maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
                await Task.Delay(ResourceSamplingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return maximumWorkingSet;
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
                ["CameraAgent:ProcessingGraphs:ReplayMaximumPendingBytes"] = (8L * 1024 * 1024 * 1024).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
            }).Build());
        services.AddSingleton<PerformanceProbeObservation>();
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "ExecutionPerformanceProbe",
            typeof(PerformanceProbeStep),
            typeof(PerformanceProbeOptions),
            AutoInclude: false));
        services.AddSingleton(new CaptureProcessingStepRegistration(
            "ExecutionPerformanceBarrier",
            typeof(PerformanceReplayBarrierStep),
            typeof(PerformanceReplayBarrierOptions),
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
                    new CaptureProcessingStepConfig(
                        "ExecutionPerformanceBarrier",
                        "barrier",
                        Options: JsonSerializer.SerializeToElement(new { }),
                        DependsOn: ["$raw"]),
                    CreateProbeNode("decode", FrameArtifactRole.Preview, ["barrier"], workload.PayloadBytes),
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

    private static async Task<ReplayQueueState> ReadReplayQueueStateAsync(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN execution.payload_bytes ELSE 0 END),
                MIN(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN work.updated_unix_ms END),
                SUM(CASE WHEN work.state = 'Completed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state IN ('Failed', 'Cancelled', 'Expired') THEN 1 ELSE 0 END)
            FROM processing_replay_work work
            JOIN processing_executions execution ON execution.execution_id = work.execution_id;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return new(
            await reader.IsDBNullAsync(0).ConfigureAwait(false) ? 0 : reader.GetInt64(0),
            await reader.IsDBNullAsync(1).ConfigureAwait(false) ? 0 : reader.GetInt64(1),
            await reader.IsDBNullAsync(2).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            await reader.IsDBNullAsync(3).ConfigureAwait(false) ? 0 : reader.GetInt64(3),
            await reader.IsDBNullAsync(4).ConfigureAwait(false) ? 0 : reader.GetInt64(4));
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

    private static long GetFileLength(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static ProcessIoCounters? ReadProcessIo()
    {
        const string processIoPath = "/proc/self/io";
        if (!OperatingSystem.IsLinux() || !File.Exists(processIoPath)) return null;
        var values = File.ReadLines(processIoPath)
            .Select(static line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(
                static parts => parts[0],
                static parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
        return new(
            values.GetValueOrDefault("rchar"),
            values.GetValueOrDefault("wchar"),
            values.GetValueOrDefault("syscr"),
            values.GetValueOrDefault("syscw"),
            values.GetValueOrDefault("read_bytes"),
            values.GetValueOrDefault("write_bytes"),
            values.GetValueOrDefault("cancelled_write_bytes"));
    }

    private static ProcessIoCounters? CalculateProcessIoDelta(
        ProcessIoCounters? before,
        ProcessIoCounters? after)
        => before is null || after is null
            ? null
            : new(
                after.LogicalReadBytes - before.LogicalReadBytes,
                after.LogicalWriteBytes - before.LogicalWriteBytes,
                after.ReadSystemCalls - before.ReadSystemCalls,
                after.WriteSystemCalls - before.WriteSystemCalls,
                after.PhysicalReadBytes - before.PhysicalReadBytes,
                after.PhysicalWriteBytes - before.PhysicalWriteBytes,
                after.CancelledWriteBytes - before.CancelledWriteBytes);

    private static string RequireEvidenceValue(string name)
        => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The merge-grade performance harness requires {name}.");

    private static void VerifyRevisionEvidence(string repositoryRoot, RevisionEvidence revision)
    {
        if (!string.Equals(revision.DirtyStateDisposition, "clean", StringComparison.Ordinal))
            throw new InvalidOperationException("Merge-grade performance evidence requires a clean worktree disposition.");
        var actualCandidate = RunGit(repositoryRoot, "rev-parse", "HEAD");
        if (!string.Equals(actualCandidate, revision.CandidateCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"HVO_EVIDENCE_REVISION is {revision.CandidateCommit}, but the checked-out commit is {actualCandidate}.");
        }
        var actualBranch = RunGit(repositoryRoot, "branch", "--show-current");
        if (!string.Equals(actualBranch, revision.Branch, StringComparison.Ordinal))
            throw new InvalidOperationException($"HVO_EVIDENCE_BRANCH is {revision.Branch}, but the branch is {actualBranch}.");
        var actualBaseline = RunGit(repositoryRoot, "rev-parse", $"{revision.CodeBaselineCommit}^{{commit}}");
        if (!string.Equals(actualBaseline, revision.CodeBaselineCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("HVO_EVIDENCE_BASE_REVISION must be the full baseline commit SHA.");
        var mergeBase = RunGit(repositoryRoot, "merge-base", revision.CodeBaselineCommit, revision.CandidateCommit);
        if (!string.Equals(mergeBase, revision.CodeBaselineCommit, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The declared baseline is not an ancestor of the candidate commit.");
        var status = RunGit(repositoryRoot, "status", "--porcelain");
        if (status.Length != 0)
            throw new InvalidOperationException($"The worktree is not clean: {status}");
    }

    private static string RunGit(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git could not be started for evidence verification.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {standardError.Trim()}");
        return standardOutput.Trim();
    }

    private static string ReadFileSystemType(string path)
    {
        if (!OperatingSystem.IsLinux())
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveFormat;
        var startInfo = new ProcessStartInfo("stat")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--file-system");
        startInfo.ArgumentList.Add("--format=%T");
        startInfo.ArgumentList.Add(Path.GetFullPath(path));
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("stat could not be started for storage evidence.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"stat failed for evidence storage: {error.Trim()}");
        return output.Trim();
    }

    private static string? ReadLinuxValue(string path, string key)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return null;
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator < 0 || !string.Equals(line[..separator].Trim(), key, StringComparison.Ordinal)) continue;
            return line[(separator + 1)..].Trim();
        }
        return null;
    }

    private static long? ReadLinuxMemoryBytes()
    {
        var value = ReadLinuxValue("/proc/meminfo", "MemTotal");
        if (value is null) return null;
        var fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 0 && long.TryParse(
            fields[0], System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var kibibytes)
            ? kibibytes * 1024
            : null;
    }

    private static string ReadSqliteVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(
            command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
    }

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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

    private sealed record RevisionEvidence(
        string CodeBaselineCommit,
        string CandidateCommit,
        string Branch,
        string DirtyStateDisposition);

    private sealed record PathMeasurement(LiveMeasurement Live, ReplayMeasurement? Replay);

    private sealed record WorkloadMeasurement(
        Workload Workload,
        LiveMeasurement Baseline,
        LiveMeasurement Candidate,
        ReplayMeasurement Replay,
        double LiveMedianChangePercent,
        double LiveP95ChangePercent,
        double SimultaneousLiveMedianChangePercent,
        double SimultaneousLiveP95ChangePercent,
        double SimultaneousLiveThroughputChangePercent);

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
        long PayloadBytesScanned,
        double LatencyBudgetMilliseconds,
        int LatencyBudgetMissCount,
        double MinimumLatencyBudgetHeadroomMilliseconds);

    private sealed record ReplayMeasurement(
        ReplayResourceMeasurement Isolated,
        SimultaneousLiveMeasurement SimultaneousLive);

    private sealed record ReplayResourceMeasurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double SubmissionMedianMilliseconds,
        double SubmissionP95Milliseconds,
        double TotalMeasurementMilliseconds,
        double DrainMilliseconds,
        double ReplaysPerSecond,
        double FullOperationPayloadBytesPerSecond,
        double CpuMilliseconds,
        double CpuMillisecondsPerReplay,
        long AllocatedBytes,
        double AllocatedBytesPerReplay,
        long WorkingSetBeforeBytes,
        long MaximumWorkingSetBytes,
        long WorkingSetAfterBytes,
        long LohRetainedBeforeFullCollectionBytes,
        long LohRetainedAfterFullCollectionBytes,
        long ExpectedSourceResolutionPayloadBytes,
        long ExpectedInputFreezeValidationPayloadBytes,
        long ExpectedExecutionSourcePayloadBytes,
        long MinimumSourcePayloadBytesRead,
        long MinimumFullFrameLohBytesAllocated,
        long InMemoryPayloadBytesScanned,
        ProcessIoCounters? OverallProcessIo,
        ProcessIoCounters? SubmissionProcessIo,
        ProcessIoCounters? ExecutionProcessIo,
        long DatabaseBytesBefore,
        long DatabaseBytesAfter,
        long DatabaseBytesChange,
        long WalBytesBefore,
        long WalBytesAfter,
        long WalBytesChange,
        double QueueFillMilliseconds,
        long PeakBacklogCount,
        long PeakBacklogBytes,
        double MaximumOldestBacklogAgeMilliseconds,
        double BacklogItemsDrainedPerSecond,
        double BacklogBytesDrainedPerSecond,
        int DurableStateSampleCount,
        long FinalBacklogCount,
        long FinalBacklogBytes,
        long CompletedReplayCount,
        long TerminalFailureCount);

    private sealed record SimultaneousLiveMeasurement(
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double TotalLiveDurationMilliseconds,
        double ServiceCapturesPerSecond,
        double EndToEndCampaignCapturesPerSecond,
        double AchievedStartRatePerSecond,
        double ArrivalIntervalMilliseconds,
        double StartToleranceMilliseconds,
        double MedianStartJitterMilliseconds,
        double P95StartJitterMilliseconds,
        double MaximumStartJitterMilliseconds,
        int MissedStartCount,
        int LiveOperationsStartedBeforeReplayDrain,
        long PeakReplayBacklogCount,
        long PeakReplayBacklogBytes,
        double MaximumOldestReplayAgeMilliseconds,
        double ReplayDrainMilliseconds,
        double ReplaysPerSecond,
        int DurableStateSampleCount,
        long FinalReplayBacklogCount,
        long FinalReplayBacklogBytes,
        bool FirstReplayWasPreemptedByLiveAcceptance);

    private sealed record ReplayBatch(
        IReadOnlyList<ReplayBatchItem> Executions,
        double QueueFillMilliseconds,
        ReplayQueueState PeakState,
        ProcessIoCounters? ProcessIoAfterSubmission = null);

    private sealed record ReplayBatchItem(
        ProcessingGraphExecutionState Execution,
        DateTimeOffset SubmittedUtc,
        double SubmissionMilliseconds);

    private sealed record ReplayDrainObservation(
        ReplayQueueState FinalState,
        double MaximumOldestAgeMilliseconds,
        int SampleCount);

    private sealed record ReplayQueueState(
        long PendingCount,
        long PendingBytes,
        DateTimeOffset? OldestPendingUtc,
        long CompletedCount,
        long TerminalFailureCount);

    private sealed record ProcessIoCounters(
        long LogicalReadBytes,
        long LogicalWriteBytes,
        long ReadSystemCalls,
        long WriteSystemCalls,
        long PhysicalReadBytes,
        long PhysicalWriteBytes,
        long CancelledWriteBytes);

    private sealed record DurableState(
        long LiveExecutions,
        long LiveBacklog,
        long ReplayBacklog,
        long CompletedGraphNodes);

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Dependency injection creates this test observation.")]
    private sealed class PerformanceProbeObservation
    {
        private TaskCompletionSource<bool> _replayBlockEntered = CreateReplayBlockSignal();
        private TaskCompletionSource<bool> _replayBlockRelease = CreateReplayBlockSignal();
        private long _payloadBytesScanned;
        private int _replayBlockCancellationCount;
        private int _blockNextReplay;

        public long PayloadBytesScanned => Interlocked.Read(ref _payloadBytesScanned);

        public int ReplayBlockCancellationCount => Volatile.Read(ref _replayBlockCancellationCount);

        public void Record(int payloadBytes) => Interlocked.Add(ref _payloadBytesScanned, payloadBytes);

        public void BlockNextReplay()
        {
            Volatile.Write(ref _replayBlockEntered, CreateReplayBlockSignal());
            Volatile.Write(ref _replayBlockRelease, CreateReplayBlockSignal());
            Interlocked.Exchange(ref _blockNextReplay, 1);
        }

        public Task<bool> WaitForReplayBlockAsync(CancellationToken cancellationToken) =>
            Volatile.Read(ref _replayBlockEntered).Task.WaitAsync(cancellationToken);

        public void ReleaseReplayBlock() => Volatile.Read(ref _replayBlockRelease).TrySetResult(true);

        public async Task WaitForReplayReleaseIfRequestedAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _blockNextReplay, 0) != 1) return;
            Volatile.Read(ref _replayBlockEntered).TrySetResult(true);
            try
            {
                _ = await Volatile.Read(ref _replayBlockRelease).Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Interlocked.Increment(ref _replayBlockCancellationCount);
                throw;
            }
        }

        private static TaskCompletionSource<bool> CreateReplayBlockSignal() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The processing pipeline factory deserializes this test options type.")]
    private sealed class PerformanceReplayBarrierOptions;

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "The processing pipeline factory creates this test step through ActivatorUtilities.")]
    private sealed class PerformanceReplayBarrierStep(
        CaptureProcessingStepMetadata metadata,
        PerformanceReplayBarrierOptions options,
        PerformanceProbeObservation observation)
        : ConfigurableCaptureProcessingStep<PerformanceReplayBarrierOptions>(metadata, options),
          IDescriptorOnlyCaptureProcessingStep,
          ICaptureProcessingGraphStep
    {
        public bool Enabled => true;

        public string RecipeName => "execution-performance-barrier";

        public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;

        public string OutputVariant => Metadata.Id;

        public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
            new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

        public override ValueTask ProcessAsync(
            CaptureProcessingContext context,
            CancellationToken cancellationToken)
            => ((IDescriptorOnlyCaptureProcessingStep)this).ProcessAsync(
                new CaptureDescriptorProcessingContext(context), cancellationToken);

        public async ValueTask ProcessAsync(
            CaptureDescriptorProcessingContext context,
            CancellationToken cancellationToken)
        {
            await observation.WaitForReplayReleaseIfRequestedAsync(cancellationToken).ConfigureAwait(false);
            context.AddProcessingOutcome(ProcessingOutcome.Produced());
        }
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
            return ValueTask.CompletedTask;
        }
    }
}
