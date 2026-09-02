using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Replay;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The test owns asynchronous process and client lifetimes without a synchronization-context dependency.")]
public sealed class LocalReplayRunnerPerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasuredCount = 30;
    private const int SimultaneousCount = 30;
    private const int DurableBacklogCount = 300;
    private const int DurableLiveCount = 30;
    private const double DurableLiveCadenceMilliseconds = 500;
    private static readonly TimeSpan DurableStateSamplingInterval = TimeSpan.FromMilliseconds(25);
    private static readonly DateTimeOffset DurableFixtureUtc = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly byte[] AuthenticationKey = Encoding.UTF8.GetBytes(
        "local-replay-performance-key-0001");
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly Workload[] Workloads =
    [
        new("W1-sized-preview", 1936, 1216, CameraPixelFormat.Mono16),
        new("W2-sized-preview", 3096, 2080, CameraPixelFormat.BayerRggb16),
        new("W6-sized-preview", 3552, 3552, CameraPixelFormat.BayerRggb16)
    ];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task W1W2AndW6SizedPreviewInProcessAndLocalRunnerEvidence()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The self-contained local replay runner is published for Linux.");
        }
        var repositoryRoot = GetRepositoryRoot();
        var revision = new
        {
            Base = RequiredEnvironment("HVO_EVIDENCE_BASE_REVISION"),
            Candidate = RequiredEnvironment("HVO_EVIDENCE_REVISION"),
            Branch = RequiredEnvironment("HVO_EVIDENCE_BRANCH"),
            DirtyState = RequiredEnvironment("HVO_EVIDENCE_DIRTY_STATE")
        };
        var storage = RequiredEnvironment("HVO_EVIDENCE_STORAGE");
        var runnerPath = ResolveRunnerPath(repositoryRoot);
        var outputRoot = Environment.GetEnvironmentVariable("HVO_ISSUE425_EVIDENCE_ROOT")
            ?? Path.Combine(repositoryRoot, "TestResults", "issue-425", "working-tree");
        Directory.CreateDirectory(outputRoot);
        var socketPath = Path.Combine(Path.GetTempPath(), $"hvo-425-{Guid.NewGuid():N}.sock");
        using var runner = StartRunner(runnerPath, socketPath);
        var startup = Stopwatch.StartNew();
        await WaitForSocketAsync(socketPath, runner).ConfigureAwait(false);
        startup.Stop();
        var directRunnerProcessId = runner.Id;
        var directRunnerPriority = TryReadPriority(runner);
        var options = new LocalReplayRunnerOptions
        {
            SocketPath = socketPath,
            PreSharedAuthKey = AuthenticationKey,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            HeartbeatInterval = TimeSpan.FromSeconds(1),
            HeartbeatTimeout = TimeSpan.FromSeconds(10),
            MaxTotalTransferBytes = LocalReplayRunnerOptions.MaximumTransferBytes
        };
        await using var client = new LocalReplayRunnerClient(options);
        var executor = new ProcessingRecipeExecutor();
        var measurements = new List<WorkloadEvidence>();
        try
        {
            foreach (var workload in Workloads)
            {
                var request = CreateRequest(workload);
                var expected = await executor.ExecuteAsync(request).ConfigureAwait(false);
                var cold = await MeasureExternalOnceAsync(client, request, expected).ConfigureAwait(false);
                for (var index = 0; index < WarmupCount; index++)
                {
                    _ = await executor.ExecuteAsync(request).ConfigureAwait(false);
                    _ = await client.ExecuteAsync(CreateJobContext(workload.Id), request).ConfigureAwait(false);
                }

                var inProcess = await MeasureInProcessAsync(executor, request, expected).ConfigureAwait(false);
                var external = await MeasureExternalAsync(client, runner, request, expected).ConfigureAwait(false);
                SimultaneousEvidence? simultaneous = null;
                if (workload.Id == "W6-sized-preview")
                {
                    simultaneous = await MeasureSimultaneousAsync(
                        executor, client, request, expected).ConfigureAwait(false);
                }
                measurements.Add(new(workload, cold, inProcess, external, simultaneous));
            }

            var capabilities = client.LastCapabilities
                ?? throw new InvalidOperationException("The runner did not return capabilities.");
            if (!runner.HasExited)
            {
                runner.Kill(entireProcessTree: true);
                await runner.WaitForExitAsync().ConfigureAwait(false);
            }
            File.Delete(socketPath);
            var durableTopology = await MeasureDurableTopologyAsync(
                repositoryRoot,
                runnerPath,
                outputRoot,
                Workloads.Single(static workload => workload.Id == "W6-sized-preview")).ConfigureAwait(false);
            var evidence = new
            {
                SchemaVersion = "issue-425-local-replay-runner-performance-v2",
                Revision = revision,
                RecordedUtc = DateTimeOffset.UtcNow,
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Framework = RuntimeInformation.FrameworkDescription,
                    Configuration = "Release",
                    ProcessorCount = Environment.ProcessorCount,
                    Storage = storage,
                    RunnerPath = Path.GetRelativePath(repositoryRoot, runnerPath),
                    RunnerProcessId = directRunnerProcessId,
                    RunnerPriority = directRunnerPriority,
                    Topology = "CameraAgent test host plus one long-lived self-contained local runner over an owner-only Unix socket"
                },
                Method = new
                {
                    WarmupCount,
                    MeasuredCount,
                    SimultaneousCount,
                    StartupMilliseconds = startup.Elapsed.TotalMilliseconds,
                    Scope = "Supplemental LocalRunner boundary and durable-queue isolation at W1/W2/W6 frame dimensions using a single built-in Preview recipe; this is not the canonical standalone production-graph W6 campaign.",
                    CanonicalStandaloneW6 = "The full 14-node cameraagent.standalone-w6.json graph, production catalog, calibration/environment prerequisites, rendered outputs, isolation, and recovery remain covered by scripts/test:cameraagent-standalone-211 under docs/planning/performance-validation.md.",
                    Exclusion = "This issue-specific campaign does not claim full-graph W6 latency, throughput, output, or resource evidence and does not replace the canonical standalone W6 gate.",
                    Sample = "One immutable full-resolution artifact per operation; single built-in encoded-preview output; exact output identity/checksum/bytes checked on every operation.",
                    Transfer = "Strict JSON control metadata plus separately framed binary payloads; one required receive allocation per payload at each process boundary; no base64 payloads.",
                    Baseline = $"The exact in-process recipe-executor path retained from base revision {revision.Base}, measured in the candidate binary so both profiles use identical inputs, warmup, runtime, and host conditions.",
                    Candidate = "The same request sent to one long-lived below-normal-priority self-contained runner. The first W1 operation includes cold process/JIT effects; later first-operation values cover workload changes after process warmup.",
                    RegressionMethod = "Report absolute in-process and warm external median/p95/throughput plus percentage changes; combined client-plus-runner CPU and peak working set are compared with in-process; simultaneous W6-sized Preview live median/p95 is compared with the same-run isolated in-process W6-sized Preview sample. Canonical W6 disposition remains separate.",
                    AllocationScope = "In-process and external-client managed allocations are measured. Runner managed allocation is unavailable from the process boundary and is null; runner CPU, working set, process I/O, and exact protocol transfer bytes are measured instead."
                },
                Capabilities = capabilities,
                Measurements = measurements,
                DurableTopology = durableTopology,
                Correctness = new
                {
                    EquivalentOutputIdentity = true,
                    EquivalentChecksum = true,
                    EquivalentBytes = true,
                    LiveDispatchBoundary = "Covered by LocalReplayRunnerTests.Adapter_NeverDispatchesLiveExecutionToConfiguredLocalRunner",
                    DurableFrozenReplay = "Measured through SQLite durable replay work and ProcessingReplayWorker; unit coverage remains ProcessingGraphOperationsTests.SameFrozenReplayMatchesInProcessAndLocalRunnerIdentityAndProvenance"
                }
            };
            var output = Path.Combine(outputRoot, "local-replay-runner-performance.json");
            await File.WriteAllTextAsync(
                output,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
            TestContext.WriteLine($"Issue #425 performance evidence: {output}");
        }
        finally
        {
            if (!runner.HasExited)
            {
                runner.Kill(entireProcessTree: true);
                await runner.WaitForExitAsync().ConfigureAwait(false);
            }
            File.Delete(socketPath);
        }
    }

    private static async Task<PathEvidence> MeasureInProcessAsync(
        ProcessingRecipeExecutor executor,
        ProcessingExecutionRequest request,
        ProcessingOutcome expected)
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var workingSetBefore = process.WorkingSet64;
        var samples = new double[MeasuredCount];
        var maximumWorkingSet = workingSetBefore;
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var outcome = await executor.ExecuteAsync(request).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            VerifyOutcome(expected, outcome);
            process.Refresh();
            maximumWorkingSet = Math.Max(maximumWorkingSet, process.WorkingSet64);
        }
        process.Refresh();
        return Summarize(
            samples,
            process.TotalProcessorTime - cpuBefore,
            GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            workingSetBefore,
            maximumWorkingSet,
            requestPayloadBytes: 0,
            responsePayloadBytes: 0,
            payloadFrames: 0,
            heartbeatCount: 0,
            io: null);
    }

    private static async Task<ExternalEvidence> MeasureExternalAsync(
        LocalReplayRunnerClient client,
        Process runner,
        ProcessingExecutionRequest request,
        ProcessingOutcome expected)
    {
        runner.Refresh();
        var cpuBefore = runner.TotalProcessorTime;
        var workingSetBefore = runner.WorkingSet64;
        var ioBefore = ReadProcessIo(runner.Id);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        using var clientProcess = Process.GetCurrentProcess();
        clientProcess.Refresh();
        var clientCpuBefore = clientProcess.TotalProcessorTime;
        var clientWorkingSetBefore = clientProcess.WorkingSet64;
        var samples = new double[MeasuredCount];
        long requestBytes = 0;
        long responseBytes = 0;
        var payloadFrames = 0;
        var heartbeats = 0;
        using var sampling = new CancellationTokenSource();
        var runnerSampler = SampleWorkingSetAsync(runner, sampling.Token);
        var clientSampler = SampleWorkingSetAsync(clientProcess, sampling.Token);
        try
        {
            for (var index = 0; index < samples.Length; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var outcome = await client.ExecuteAsync(CreateJobContext("measured"), request).ConfigureAwait(false);
                samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                VerifyOutcome(expected, outcome);
                var execution = client.LastExecutionEvidence
                    ?? throw new InvalidOperationException("The runner did not return transfer evidence.");
                requestBytes += execution.RequestMetadataBytes + execution.RequestPayloadBytes;
                responseBytes += execution.ResponseMetadataBytes + execution.ResponsePayloadBytes;
                payloadFrames += execution.RequestPayloadFrames + execution.ResponsePayloadFrames;
                heartbeats += execution.HeartbeatCount;
            }
        }
        finally
        {
            await sampling.CancelAsync().ConfigureAwait(false);
        }
        var maximumWorkingSet = await runnerSampler.ConfigureAwait(false);
        var clientMaximumWorkingSet = await clientSampler.ConfigureAwait(false);
        runner.Refresh();
        var path = Summarize(
            samples,
            runner.TotalProcessorTime - cpuBefore,
            null,
            workingSetBefore,
            maximumWorkingSet,
            requestBytes,
            responseBytes,
            payloadFrames,
            heartbeats,
            ReadProcessIo(runner.Id) - ioBefore);
        clientProcess.Refresh();
        var clientResources = new ProcessResources(
            (clientProcess.TotalProcessorTime - clientCpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            clientWorkingSetBefore,
            clientMaximumWorkingSet);
        return new ExternalEvidence(path, clientResources);
    }

    private static async Task<ColdEvidence> MeasureExternalOnceAsync(
        LocalReplayRunnerClient client,
        ProcessingExecutionRequest request,
        ProcessingOutcome expected)
    {
        var started = Stopwatch.GetTimestamp();
        var outcome = await client.ExecuteAsync(CreateJobContext("cold"), request).ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(started);
        VerifyOutcome(expected, outcome);
        var transfer = client.LastExecutionEvidence
            ?? throw new InvalidOperationException("The runner did not return cold transfer evidence.");
        return new ColdEvidence(
            duration.TotalMilliseconds,
            transfer.DispatchDuration.TotalMilliseconds,
            transfer.RequestMetadataBytes,
            transfer.RequestPayloadBytes,
            transfer.ResponseMetadataBytes,
            transfer.ResponsePayloadBytes);
    }

    private static async Task<SimultaneousEvidence> MeasureSimultaneousAsync(
        ProcessingRecipeExecutor executor,
        LocalReplayRunnerClient client,
        ProcessingExecutionRequest request,
        ProcessingOutcome expected)
    {
        var replay = Task.Run(async () =>
        {
            for (var index = 0; index < SimultaneousCount; index++)
            {
                var outcome = await client.ExecuteAsync(CreateJobContext("simultaneous"), request).ConfigureAwait(false);
                VerifyOutcome(expected, outcome);
            }
        });
        var liveSamples = new double[SimultaneousCount];
        var startDelays = new double[SimultaneousCount];
        var campaign = Stopwatch.StartNew();
        for (var index = 0; index < SimultaneousCount; index++)
        {
            var due = TimeSpan.FromMilliseconds(index * 500d);
            var delay = due - campaign.Elapsed;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            startDelays[index] = Math.Max(0, (campaign.Elapsed - due).TotalMilliseconds);
            var started = Stopwatch.GetTimestamp();
            var outcome = await executor.ExecuteAsync(request).ConfigureAwait(false);
            liveSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            VerifyOutcome(expected, outcome);
        }
        await replay.ConfigureAwait(false);
        return new SimultaneousEvidence(
            Median(liveSamples),
            Percentile95(liveSamples),
            Median(startDelays),
            Percentile95(startDelays),
            campaign.Elapsed.TotalSeconds);
    }

    private static async Task<DurableTopologyEvidence> MeasureDurableTopologyAsync(
        string repositoryRoot,
        string runnerPath,
        string outputRoot,
        Workload workload)
    {
        var root = Path.Combine(outputRoot, "durable-topology-work");
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        var payload = CreateDurablePayload(workload.PayloadBytes);
        try
        {
            var baseline = await MeasureDurableInProcessAsync(
                Path.Combine(root, "in-process"), workload, payload).ConfigureAwait(false);
            var measuredCandidate = await MeasureDurableLocalRunnerAsync(
                Path.Combine(root, "local-runner"), runnerPath, workload, payload).ConfigureAwait(false);
            var candidate = measuredCandidate with
            {
                Profile = measuredCandidate.Profile with
                {
                    MedianLatencyPercentChange = PercentChange(
                        baseline.Profile.MedianMilliseconds,
                        measuredCandidate.Profile.MedianMilliseconds),
                    P95LatencyPercentChange = PercentChange(
                        baseline.Profile.P95Milliseconds,
                        measuredCandidate.Profile.P95Milliseconds),
                    ThroughputPercentChange = PercentChange(
                        baseline.Profile.OperationsPerSecond,
                        measuredCandidate.Profile.OperationsPerSecond)
                }
            };
            Assert.AreEqual(baseline.Profile.Output.OutputIdentitySha256, candidate.Profile.Output.OutputIdentitySha256);
            Assert.AreEqual(baseline.Profile.Output.ChecksumSha256, candidate.Profile.Output.ChecksumSha256);
            Assert.AreEqual(baseline.Profile.Output.ByteLength, candidate.Profile.Output.ByteLength);
            return new(
                new
                {
                    RepositoryRoot = repositoryRoot,
                    Topology = "CameraAgent dependency injection, raw ingress, SQLite replay work, ProcessingReplayWorker, and CameraAgentRecipeExecutionAdapter",
                    Workload = "W6-sized single-Preview workload: 3552x3552 Bayer RGGB 12-bit data in little-endian 16-bit samples; not the canonical standalone W6 graph",
                    LogicalExposureSeconds = 5,
                    DeclaredMinimumStartCadenceSeconds = 10,
                    StressStartCadenceMilliseconds = DurableLiveCadenceMilliseconds,
                    WarmupCount,
                    MeasuredCount,
                    DurableBacklogCount,
                    DurableLiveCount
                },
                baseline,
                candidate,
                new
                {
                    ReplayLatency = $"LocalRunner durable replay median changed {candidate.Profile.MedianLatencyPercentChange:F2}% and p95 changed {candidate.Profile.P95LatencyPercentChange:F2}%; this is accepted for optional background replay because live work remains in process and preempts replay.",
                    ReplayThroughput = $"LocalRunner durable throughput changed {candidate.Profile.ThroughputPercentChange:F2}% while draining {candidate.Backlog.DrainJobsPerSecond:F2} W6-sized single-Preview jobs/s, above the declared 0.10 capture/s cadence.",
                    LiveCadence = $"Under LocalRunner drain, live median changed {candidate.Backlog.LiveMedianPercentChange:F2}% and p95 changed {candidate.Backlog.LiveP95PercentChange:F2}% from the same-host no-replay baseline; p95 remained {candidate.Backlog.LiveP95Milliseconds:F2} ms against the 10,000 ms declared cadence.",
                    Memory = "CameraAgent and runner peak RSS are reported separately and remain inside the generated 1 GiB CameraAgent plus 2 GiB runner deployment limits.",
                    Io = "Durable source validation, SQLite/WAL activity, framed protocol bytes, and runner process I/O are reported explicitly; the runner has no raw/archive mount and receives only authenticated framed payloads."
                },
                new(
                    baseline.Profile.Output.OutputIdentitySha256,
                    baseline.Profile.Output.ChecksumSha256,
                    baseline.Profile.Output.ByteLength,
                    EquivalentIdentity: true,
                    EquivalentChecksum: true,
                    EquivalentBytes: true,
                    OrderedRawLineage: true,
                    OneDurableCompletionPerReplay: true,
                    NoReplayLatestViewReplacement: true,
                    RunnerAbsenceConsumedAttempts: false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<DurableProfileEnvelope> MeasureDurableInProcessAsync(
        string root,
        Workload workload,
        byte[] payload)
    {
        Directory.CreateDirectory(root);
        using var provider = CreateDurableProvider(root, ReplayExecutionProfile.InProcess, socketPath: null);
        var session = await PrepareDurableSessionAsync(provider, workload, payload).ConfigureAwait(false);
        await session.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            var profile = await MeasureDurableProfileAsync(
                "InProcess", root, session, workload, runner: null).ConfigureAwait(false);
            return new(profile, StartupMilliseconds: 0);
        }
        finally
        {
            await session.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<DurableLocalRunnerEnvelope> MeasureDurableLocalRunnerAsync(
        string root,
        string runnerPath,
        Workload workload,
        byte[] payload)
    {
        Directory.CreateDirectory(root);
        var socketPath = Path.Combine(Path.GetTempPath(), $"hvo-425-durable-{Guid.NewGuid():N}.sock");
        using var metrics = new ReplayMetricsCollector();
        using var warmRunner = StartRunner(runnerPath, socketPath);
        var startup = Stopwatch.StartNew();
        await WaitForSocketAsync(socketPath, warmRunner).ConfigureAwait(false);
        startup.Stop();
        using var provider = CreateDurableProvider(root, ReplayExecutionProfile.LocalRunner, socketPath);
        var session = await PrepareDurableSessionAsync(provider, workload, payload).ConfigureAwait(false);
        var liveBaselineSamples = await MeasureDurableLiveCadenceAsync(
            session, workload, payload, drain: null, sequenceOffset: 1).ConfigureAwait(false);
        Array.Sort(liveBaselineSamples.Latencies);
        Array.Sort(liveBaselineSamples.StartJitter);
        var liveBaseline = new DurableLivePathEvidence(
            liveBaselineSamples.Latencies[DurableLiveCount / 2],
            Percentile95(liveBaselineSamples.Latencies),
            liveBaselineSamples.StartJitter[DurableLiveCount / 2],
            Percentile95(liveBaselineSamples.StartJitter));
        await session.Worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        Process? recoveredRunner = null;
        try
        {
            var profile = await MeasureDurableProfileAsync(
                "LocalRunner", root, session, workload, warmRunner).ConfigureAwait(false);
            warmRunner.Kill(entireProcessTree: true);
            await warmRunner.WaitForExitAsync().ConfigureAwait(false);
            File.Delete(socketPath);

            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var beforeBacklog = await ReadDurableReplayStateAsync(databasePath).ConfigureAwait(false);
            var outageStarted = Stopwatch.GetTimestamp();
            for (var index = 0; index < DurableBacklogCount; index++)
            {
                _ = await SubmitDurableReplayAsync(
                    session,
                    $"durable-outage-{index.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
            }
            var unavailable = await WaitForUnavailableBacklogAsync(databasePath).ConfigureAwait(false);
            Assert.AreEqual((long)DurableBacklogCount, unavailable.PendingCount);
            Assert.AreEqual((long)DurableBacklogCount * workload.PayloadBytes, unavailable.PendingBytes);
            Assert.AreEqual(0L, unavailable.LeasedCount);
            Assert.AreEqual(0L, unavailable.AttemptCountSum);
            Assert.IsGreaterThan(0L, unavailable.UnavailableCount);
            var outageQueueMilliseconds = Stopwatch.GetElapsedTime(outageStarted).TotalMilliseconds;
            var initialOldestAgeMilliseconds = unavailable.OldestPendingUtc is { } initialOldest
                ? (DateTimeOffset.UtcNow - initialOldest).TotalMilliseconds
                : 0;

            recoveredRunner = StartRunner(runnerPath, socketPath);
            var recoveryStarted = Stopwatch.GetTimestamp();
            await WaitForSocketAsync(socketPath, recoveredRunner).ConfigureAwait(false);
            var runnerIoBefore = ReadProcessIo(recoveredRunner.Id);
            recoveredRunner.Refresh();
            var runnerCpuBefore = recoveredRunner.TotalProcessorTime;
            using var resourceSampling = new CancellationTokenSource();
            var runnerPeakTask = SampleWorkingSetAsync(recoveredRunner, resourceSampling.Token);
            using var host = Process.GetCurrentProcess();
            host.Refresh();
            var hostCpuBefore = host.TotalProcessorTime;
            var hostIoBefore = ReadProcessIo(host.Id);
            var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
            var hostPeakTask = SampleWorkingSetAsync(host, resourceSampling.Token);
            DurableDrainObservation? drainObservation = null;
            double[] liveLatencies;
            double[] liveStartJitter;
            int liveStartedBeforeDrain;
            var drainStarted = Stopwatch.GetTimestamp();
            try
            {
                var drain = WaitForDurableDrainAsync(databasePath);
                var live = await MeasureDurableLiveCadenceAsync(
                    session, workload, payload, drain, sequenceOffset: DurableLiveCount + 1)
                    .ConfigureAwait(false);
                liveLatencies = live.Latencies;
                liveStartJitter = live.StartJitter;
                liveStartedBeforeDrain = live.StartedBeforeDrain;
                drainObservation = await drain.ConfigureAwait(false);
            }
            finally
            {
                await resourceSampling.CancelAsync().ConfigureAwait(false);
            }
            var runnerPeak = await runnerPeakTask.ConfigureAwait(false);
            var hostPeak = await hostPeakTask.ConfigureAwait(false);
            var drainMilliseconds = Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds;
            var recoveryMilliseconds = Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds;
            var drainState = drainObservation
                ?? throw new InvalidOperationException("The durable LocalRunner backlog did not produce drain evidence.");
            Assert.AreEqual(0L, drainState.Final.PendingCount);
            Assert.AreEqual(0L, drainState.Final.PendingBytes);
            Assert.AreEqual(
                beforeBacklog.CompletedCount + DurableBacklogCount,
                drainState.Final.CompletedCount);
            Assert.AreEqual(beforeBacklog.TerminalFailureCount, drainState.Final.TerminalFailureCount);
            Array.Sort(liveLatencies);
            Array.Sort(liveStartJitter);
            Assert.IsLessThan(10_000d, Percentile95(liveLatencies));
            recoveredRunner.Refresh();
            host.Refresh();
            var output = await ReadDurableOutputEvidenceAsync(
                root,
                session.Source.Manifest.Descriptor.Artifact.ArtifactId,
                WarmupCount + MeasuredCount + DurableBacklogCount).ConfigureAwait(false);
            Assert.AreEqual(profile.Output.OutputIdentitySha256, output.OutputIdentitySha256);
            var backlog = new DurableBacklogEvidence(
                unavailable.PendingCount,
                unavailable.PendingBytes,
                unavailable.OldestPendingUtc,
                initialOldestAgeMilliseconds,
                outageQueueMilliseconds,
                unavailable.UnavailableCount,
                unavailable.AttemptCountSum,
                drainState.MaximumOldestAgeMilliseconds,
                drainMilliseconds,
                recoveryMilliseconds,
                DurableBacklogCount / (drainMilliseconds / 1000d),
                unavailable.PendingBytes / (drainMilliseconds / 1000d),
                drainState.SampleCount,
                drainState.Final.PendingCount,
                drainState.Final.PendingBytes,
                liveLatencies[DurableLiveCount / 2],
                Percentile95(liveLatencies),
                PercentChange(liveBaseline.MedianMilliseconds, liveLatencies[DurableLiveCount / 2]),
                PercentChange(liveBaseline.P95Milliseconds, Percentile95(liveLatencies)),
                liveStartJitter[DurableLiveCount / 2],
                Percentile95(liveStartJitter),
                liveStartedBeforeDrain,
                (host.TotalProcessorTime - hostCpuBefore).TotalMilliseconds,
                GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
                hostPeak,
                ReadProcessIo(host.Id) - hostIoBefore,
                (recoveredRunner.TotalProcessorTime - runnerCpuBefore).TotalMilliseconds,
                runnerPeak,
                ReadProcessIo(recoveredRunner.Id) - runnerIoBefore,
                metrics.Snapshot(),
                output);
            return new(profile, startup.Elapsed.TotalMilliseconds, liveBaseline, backlog);
        }
        finally
        {
            await session.Worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            if (!warmRunner.HasExited)
            {
                warmRunner.Kill(entireProcessTree: true);
                await warmRunner.WaitForExitAsync().ConfigureAwait(false);
            }
            if (recoveredRunner is not null)
            {
                if (!recoveredRunner.HasExited)
                {
                    recoveredRunner.Kill(entireProcessTree: true);
                    await recoveredRunner.WaitForExitAsync().ConfigureAwait(false);
                }
                recoveredRunner.Dispose();
            }
            File.Delete(socketPath);
        }
    }

    private static async Task<DurableProfileEvidence> MeasureDurableProfileAsync(
        string profile,
        string root,
        DurableSession session,
        Workload workload,
        Process? runner)
    {
        for (var index = 0; index < WarmupCount; index++)
        {
            var replay = await SubmitDurableReplayAsync(session, $"durable-{profile}-warmup-{index}")
                .ConfigureAwait(false);
            _ = await WaitForDurableTerminalAsync(session.Operations, replay.Execution.ExecutionId)
                .ConfigureAwait(false);
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var host = Process.GetCurrentProcess();
        host.Refresh();
        var hostCpuBefore = host.TotalProcessorTime;
        var hostIoBefore = ReadProcessIo(host.Id);
        var hostWorkingSetBefore = host.WorkingSet64;
        var allocationBefore = GC.GetTotalAllocatedBytes(precise: true);
        var runnerCpuBefore = TimeSpan.Zero;
        ProcessIo? runnerIoBefore = null;
        var runnerWorkingSetBefore = 0L;
        if (runner is not null)
        {
            runner.Refresh();
            runnerCpuBefore = runner.TotalProcessorTime;
            runnerIoBefore = ReadProcessIo(runner.Id);
            runnerWorkingSetBefore = runner.WorkingSet64;
        }
        using var sampling = new CancellationTokenSource();
        var hostPeakTask = SampleWorkingSetAsync(host, sampling.Token);
        var runnerPeakTask = runner is null
            ? Task.FromResult(0L)
            : SampleWorkingSetAsync(runner, sampling.Token);
        var samples = new double[MeasuredCount];
        var measuredStarted = Stopwatch.GetTimestamp();
        try
        {
            for (var index = 0; index < MeasuredCount; index++)
            {
                var started = Stopwatch.GetTimestamp();
                var replay = await SubmitDurableReplayAsync(session, $"durable-{profile}-measured-{index}")
                    .ConfigureAwait(false);
                _ = await WaitForDurableTerminalAsync(session.Operations, replay.Execution.ExecutionId)
                    .ConfigureAwait(false);
                samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }
        }
        finally
        {
            await sampling.CancelAsync().ConfigureAwait(false);
        }
        var totalMilliseconds = Stopwatch.GetElapsedTime(measuredStarted).TotalMilliseconds;
        var hostPeak = await hostPeakTask.ConfigureAwait(false);
        var runnerPeak = await runnerPeakTask.ConfigureAwait(false);
        Array.Sort(samples);
        host.Refresh();
        if (runner is not null) runner.Refresh();
        var finalState = await ReadDurableReplayStateAsync(Path.Combine(root, "journal", "raw-ingress.db"))
            .ConfigureAwait(false);
        Assert.AreEqual(0L, finalState.PendingCount);
        Assert.AreEqual((long)(WarmupCount + MeasuredCount), finalState.CompletedCount);
        Assert.AreEqual(0L, finalState.TerminalFailureCount);
        var output = await ReadDurableOutputEvidenceAsync(
            root,
            session.Source.Manifest.Descriptor.Artifact.ArtifactId,
            WarmupCount + MeasuredCount).ConfigureAwait(false);
        return new(
            profile,
            samples[MeasuredCount / 2],
            Percentile95(samples),
            samples[0],
            samples[^1],
            totalMilliseconds,
            MeasuredCount / (totalMilliseconds / 1000d),
            (host.TotalProcessorTime - hostCpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocationBefore,
            hostWorkingSetBefore,
            hostPeak,
            host.WorkingSet64,
            ReadProcessIo(host.Id) - hostIoBefore,
            runner is null ? null : (runner.TotalProcessorTime - runnerCpuBefore).TotalMilliseconds,
            runner is null ? null : runnerWorkingSetBefore,
            runner is null ? null : runnerPeak,
            runner is null ? null : runner.WorkingSet64,
            runner is null ? null : ReadProcessIo(runner.Id) - runnerIoBefore!,
            output);
    }

    private static async Task<DurableSession> PrepareDurableSessionAsync(
        ServiceProvider provider,
        Workload workload,
        byte[] payload)
    {
        var configuration = CreateDurableConfiguration(workload);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
        var registry = await operations.EnsureConfiguredBasicAsync(configuration, CancellationToken.None)
            .ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var laneHandler = provider.GetServices<ICaptureLaneHandler>().Single(static handler => handler.Lane == "standard");
        var standard = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(
            static lane => lane.Name == "standard");
        var source = await AcceptDurableLiveAsync(
            ingress, laneStore, laneHandler, standard, configuration, workload, payload, 0, operations)
            .ConfigureAwait(false);
        return new(
            configuration,
            ingress,
            laneStore,
            laneHandler,
            standard,
            operations,
            provider.GetRequiredService<ProcessingReplayWorker>(),
            registry.ActiveRevisionId,
            source);
    }

    private static async Task<DurableLiveEvidence> MeasureDurableLiveCadenceAsync(
        DurableSession session,
        Workload workload,
        byte[] payload,
        Task<DurableDrainObservation>? drain,
        int sequenceOffset)
    {
        var samples = new double[DurableLiveCount];
        var startJitter = new double[DurableLiveCount];
        var startedBeforeDrain = 0;
        var scheduleStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < DurableLiveCount; index++)
        {
            var targetMilliseconds = index * DurableLiveCadenceMilliseconds;
            var delayMilliseconds = targetMilliseconds - Stopwatch.GetElapsedTime(scheduleStarted).TotalMilliseconds;
            if (delayMilliseconds > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delayMilliseconds)).ConfigureAwait(false);
            }
            startJitter[index] = Math.Max(
                0,
                Stopwatch.GetElapsedTime(scheduleStarted).TotalMilliseconds - targetMilliseconds);
            if (drain is { IsCompleted: false }) startedBeforeDrain++;
            var started = Stopwatch.GetTimestamp();
            _ = await AcceptDurableLiveAsync(
                session.Ingress,
                session.LaneStore,
                session.LaneHandler,
                session.StandardLane,
                session.Configuration,
                workload,
                payload,
                sequenceOffset + index,
                session.Operations).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return new(samples, startJitter, startedBeforeDrain);
    }

    private static ValueTask<ProcessingReplaySubmissionResult> SubmitDurableReplayAsync(
        DurableSession session,
        string key)
        => session.Operations.SubmitReplayAsync(
            new ProcessingReplaySubmission(
                session.Source.Manifest.Descriptor.Capture.CaptureId,
                session.RevisionId,
                session.Source.Manifest.Descriptor.Artifact.ArtifactId,
                TriggerReference: key),
            key,
            "issue-425-performance",
            CancellationToken.None);

    private static async Task<ProcessingGraphExecutionDetail> WaitForDurableTerminalAsync(
        ProcessingGraphOperationsCoordinator operations,
        Guid executionId)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        ProcessingGraphExecutionDetail? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await operations.ReadExecutionDetailAsync(executionId, CancellationToken.None)
                .ConfigureAwait(false);
            if (last?.Execution.Status == ProcessingGraphExecutionStatus.Completed) return last;
            if (last?.Execution.Status is ProcessingGraphExecutionStatus.Failed or
                ProcessingGraphExecutionStatus.Cancelled or ProcessingGraphExecutionStatus.Expired)
            {
                throw new InvalidOperationException(
                    $"Durable replay {executionId:N} became {last.Execution.Status}: {last.Execution.FailureReason}.");
            }
            await Task.Delay(DurableStateSamplingInterval).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Durable replay {executionId:N} did not complete (status={last?.Execution.Status}, reason={last?.Execution.FailureReason}).");
    }

    private static async Task<DurableReplayState> WaitForUnavailableBacklogAsync(string databasePath)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        DurableReplayState? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await ReadDurableReplayStateAsync(databasePath).ConfigureAwait(false);
            if (last.PendingCount == DurableBacklogCount && last.LeasedCount == 0 &&
                last.UnavailableCount > 0 && last.AttemptCountSum == 0)
            {
                return last;
            }
            await Task.Delay(DurableStateSamplingInterval).ConfigureAwait(false);
        }
        throw new TimeoutException(
            $"Runner absence did not produce the expected durable backlog (pending={last?.PendingCount}, leased={last?.LeasedCount}, unavailable={last?.UnavailableCount}, attempts={last?.AttemptCountSum}).");
    }

    private static async Task<DurableDrainObservation> WaitForDurableDrainAsync(string databasePath)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        var maximumOldestAgeMilliseconds = 0d;
        var sampleCount = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await ReadDurableReplayStateAsync(databasePath).ConfigureAwait(false);
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
        throw new TimeoutException("The durable LocalRunner backlog did not drain within ten minutes.");
    }

    private static async Task<DurableReplayState> ReadDurableReplayStateAsync(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN execution.payload_bytes ELSE 0 END),
                MIN(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN execution.accepted_unix_ms END),
                SUM(CASE WHEN work.state = 'Completed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state IN ('Failed', 'Cancelled', 'Expired') THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state = 'Leased' THEN 1 ELSE 0 END),
                SUM(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait') THEN execution.attempt_count ELSE 0 END),
                SUM(CASE WHEN work.state IN ('Pending', 'Leased', 'RetryWait')
                              AND execution.failure_reason = 'processing.replay-runner-unavailable' THEN 1 ELSE 0 END)
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
            await reader.IsDBNullAsync(4).ConfigureAwait(false) ? 0 : reader.GetInt64(4),
            await reader.IsDBNullAsync(5).ConfigureAwait(false) ? 0 : reader.GetInt64(5),
            await reader.IsDBNullAsync(6).ConfigureAwait(false) ? 0 : reader.GetInt64(6),
            await reader.IsDBNullAsync(7).ConfigureAwait(false) ? 0 : reader.GetInt64(7));
    }

    private static async Task<DurableOutputEvidence> ReadDurableOutputEvidenceAsync(
        string root,
        Guid sourceArtifactId,
        int expectedReplayCount)
    {
        var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync().ConfigureAwait(false);
        string outputIdentity;
        long publishedAssociationCount;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*), COUNT(association.output_identity_sha256),
                       COUNT(DISTINCT association.output_identity_sha256),
                       COALESCE(SUM(association.published_flag), 0),
                       MIN(execution.attempt_count), MAX(execution.attempt_count),
                       MIN(association.output_identity_sha256)
                FROM processing_executions execution
                LEFT JOIN processing_execution_outputs association
                       ON association.execution_id = execution.execution_id
                WHERE execution.execution_class = 'Replay';
                """;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual((long)expectedReplayCount, reader.GetInt64(0));
            Assert.AreEqual((long)expectedReplayCount, reader.GetInt64(1));
            Assert.AreEqual(1L, reader.GetInt64(2));
            publishedAssociationCount = reader.GetInt64(3);
            Assert.AreEqual((long)expectedReplayCount, publishedAssociationCount);
            Assert.AreEqual(1L, reader.GetInt64(4));
            Assert.AreEqual(1L, reader.GetInt64(5));
            outputIdentity = reader.GetString(6);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM processing_outputs WHERE capture_id = $capture),
                    (SELECT association.output_identity_sha256
                     FROM processing_execution_outputs association
                     JOIN processing_executions execution ON execution.execution_id = association.execution_id
                     WHERE execution.execution_class = 'Live' AND execution.capture_id = $capture);
                """;
            command.Parameters.AddWithValue("$capture", sourceArtifactId == Guid.Empty
                ? string.Empty
                : await ReadCaptureIdForSourceAsync(connection, sourceArtifactId).ConfigureAwait(false));
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(1L, reader.GetInt64(0));
            Assert.AreEqual(outputIdentity, reader.GetString(1));
        }

        byte[] descriptorJson;
        string relativePath;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT descriptor_json, payload_relative_path
                FROM processing_outputs
                WHERE output_identity_sha256 = $identity AND availability_state = 'Available';
                """;
            command.Parameters.AddWithValue("$identity", outputIdentity);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            descriptorJson = await reader.GetFieldValueAsync<byte[]>(0).ConfigureAwait(false);
            relativePath = reader.GetString(1);
            Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source_artifact_id
                FROM processing_output_sources
                WHERE output_identity_sha256 = $identity
                ORDER BY source_ordinal;
                """;
            command.Parameters.AddWithValue("$identity", outputIdentity);
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
            Assert.AreEqual(sourceArtifactId, Guid.ParseExact(reader.GetString(0), "N"));
            Assert.IsFalse(await reader.ReadAsync().ConfigureAwait(false));
        }
        ArtifactDescriptor artifact;
        long byteLength;
        using (var evidence = JsonDocument.Parse(descriptorJson))
        {
            var schema = evidence.RootElement.GetProperty("schemaVersion").GetString();
            if (string.Equals(schema, ArtifactManifestV2.CurrentSchemaVersion, StringComparison.Ordinal))
            {
                var parsed = CaptureContractJson.ParseManifest(descriptorJson);
                Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
                var manifest = parsed.Document!.Manifest;
                artifact = manifest.Descriptor.Artifact;
                byteLength = manifest.Descriptor.Layout.ByteLength;
                Assert.AreEqual(relativePath, manifest.RelativeArtifactPath);
            }
            else
            {
                var manifest = DurableProcessingProductManifestJson.Parse(descriptorJson);
                Assert.AreEqual(outputIdentity, manifest.OutputIdentitySha256);
                artifact = manifest.Artifact;
                byteLength = manifest.ByteLength;
                Assert.AreEqual(relativePath, manifest.RelativeArtifactPath);
            }
        }
        Assert.HasCount(1, artifact.SourceArtifactIds);
        Assert.AreEqual(sourceArtifactId, artifact.SourceArtifactIds[0]);
        var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        using var payload = File.OpenRead(absolutePath);
        var checksum = Convert.ToHexString(await SHA256.HashDataAsync(payload).ConfigureAwait(false));
        Assert.AreEqual(artifact.ChecksumSha256, checksum, ignoreCase: true);
        Assert.AreEqual(byteLength, new FileInfo(absolutePath).Length);
        return new(outputIdentity, checksum, byteLength, relativePath, publishedAssociationCount, 1);
    }

    private static async Task<string> ReadCaptureIdForSourceAsync(
        SqliteConnection connection,
        Guid sourceArtifactId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT capture_id FROM raw_captures WHERE raw_artifact_id = $artifact;";
        command.Parameters.AddWithValue("$artifact", sourceArtifactId.ToString("N"));
        return Convert.ToString(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("The durable performance source capture is missing.");
    }

    private static ServiceProvider CreateDurableProvider(
        string root,
        ReplayExecutionProfile profile,
        string? socketPath)
    {
        var values = new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] = "false",
            ["CameraAgent:ProcessingGraphs:ReplayProfile"] = profile.ToString(),
            ["CameraAgent:ProcessingGraphs:ReplayMaximumConcurrency"] = "1",
            ["CameraAgent:ProcessingGraphs:ReplayMaximumPendingCount"] = "1000",
            ["CameraAgent:ProcessingGraphs:ReplayMaximumPendingBytes"] =
                (8L * 1024 * 1024 * 1024).ToString(CultureInfo.InvariantCulture),
            ["CameraAgent:ProcessingGraphs:ReplayDeadlineSeconds"] = "3600",
            ["CameraAgent:ProcessingGraphs:ReplayLeaseSeconds"] = "30",
            ["CameraAgent:ProcessingGraphs:ReplayRecoveryPollSeconds"] = "1"
        };
        if (socketPath is not null)
        {
            values["CameraAgent:ProcessingGraphs:LocalRunner:SocketPath"] = socketPath;
            values["CameraAgent:ProcessingGraphs:LocalRunner:AuthorizationKey"] =
                Encoding.UTF8.GetString(AuthenticationKey);
            values["CameraAgent:ProcessingGraphs:LocalRunner:ConnectTimeoutSeconds"] = "2";
            values["CameraAgent:ProcessingGraphs:LocalRunner:HeartbeatIntervalSeconds"] = "1";
            values["CameraAgent:ProcessingGraphs:LocalRunner:HeartbeatTimeoutSeconds"] = "10";
            values["CameraAgent:ProcessingGraphs:LocalRunner:MaximumTransferBytes"] =
                LocalReplayRunnerOptions.MaximumTransferBytes.ToString(CultureInfo.InvariantCulture);
        }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCameraAgentInfrastructure(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    private static CameraModuleConfig CreateDurableConfiguration(Workload workload)
        => new(
            new ObservatoryLocation(0, 0, 0, "UTC"),
            new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(
                new SensorProfile(
                    "W6-sized-preview-Bayer12-in-16",
                    workload.Width,
                    workload.Height,
                    1,
                    SensorColorMode.Color,
                    workload.PixelFormat),
                new OpticsProfile("issue-425-performance", 1, 1, 0),
                new RigOrientation(0, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromSeconds(5),
                    1,
                    1)),
            new CapturePipelineConfig(
                [new CaptureProcessingStepConfig("Preview", "preview", DependsOn: ["$raw"])],
                CapturePipelineSchemaVersions.ExplicitV2,
                CapturePipelineDependencyPolicy.RejectEnabledDependent),
            "issue-425-w6-sized-preview");

    private static async Task<RawCaptureReceipt> AcceptDurableLiveAsync(
        IRawCaptureIngress ingress,
        ICaptureLaneStore laneStore,
        ICaptureLaneHandler laneHandler,
        CaptureLaneDefinition standard,
        CameraModuleConfig configuration,
        Workload workload,
        byte[] payload,
        int index,
        ProcessingGraphOperationsCoordinator operations)
    {
        var receipt = await ingress.AcceptAsync(
            configuration,
            CreateDurableSubmission(workload, payload, index),
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(receipt);
        var lease = await laneStore.ClaimAsync(
            standard,
            "issue-425-performance-live",
            configuration,
            CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(lease);
        var result = await laneHandler.HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome, result.Reason);
        await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        operations.NotifyLiveWorkChanged();
        return receipt;
    }

    private static CaptureLoopSubmission CreateDurableSubmission(
        Workload workload,
        byte[] payload,
        int index)
    {
        var startedUtc = DurableFixtureUtc.AddSeconds(index * 10L);
        var exposure = TimeSpan.FromSeconds(5);
        var cadence = TimeSpan.FromSeconds(10);
        var setpoint = new CaptureSetpoint(exposure, 1, null, null);
        var frame = new CameraFrame(
            startedUtc,
            workload.Width,
            workload.Height,
            workload.PixelFormat,
            payload,
            new FrameMetadata(exposure, 1, 10, workload.Id));
        return new CaptureLoopSubmission(
            new CaptureRequest(startedUtc, cadence, CaptureMode.Still, setpoint),
            new CaptureResult(frame, setpoint, TimeSpan.Zero, CaptureMode.Still, false)
            {
                AcquisitionTiming = new CaptureAcquisitionTiming(
                    startedUtc,
                    startedUtc.Add(exposure),
                    startedUtc.Add(exposure).AddMilliseconds(100))
            },
            startedUtc,
            exposure,
            cadence);
    }

    private static byte[] CreateDurablePayload(int length)
    {
        var payload = GC.AllocateUninitializedArray<byte>(length);
        for (var offset = 0; offset < payload.Length; offset += 2)
        {
            var value = (ushort)(((offset / 2) * 31 + 17) & 0x0fff);
            payload[offset] = (byte)value;
            payload[offset + 1] = (byte)(value >> 8);
        }
        return payload;
    }

    private static PathEvidence Summarize(
        double[] samples,
        TimeSpan cpu,
        long? allocatedBytes,
        long workingSetBefore,
        long maximumWorkingSet,
        long requestPayloadBytes,
        long responsePayloadBytes,
        int payloadFrames,
        int heartbeatCount,
        ProcessIo? io) => new(
            Median(samples),
            Percentile95(samples),
            samples.Min(),
            samples.Max(),
            samples.Sum(),
            samples.Length / TimeSpan.FromMilliseconds(samples.Sum()).TotalSeconds,
            cpu.TotalMilliseconds,
            allocatedBytes,
            workingSetBefore,
            maximumWorkingSet,
            requestPayloadBytes,
            responsePayloadBytes,
            payloadFrames,
            heartbeatCount,
            io);

    private static ProcessingExecutionRequest CreateRequest(Workload workload)
    {
        var payload = GC.AllocateUninitializedArray<byte>(workload.PayloadBytes);
        for (var index = 0; index < payload.Length; index++) payload[index] = (byte)(index * 31 + 17);
        var input = new ProcessingArtifact(
            Guid.NewGuid(),
            FrameArtifactRole.Raw,
            "source",
            Convert.ToHexString(SHA256.HashData("issue-425-source-v1"u8)),
            "application/x-hvo-frame",
            new FrameLayoutDescriptor(
                workload.Width,
                workload.Height,
                checked(workload.Width * 2),
                workload.PixelFormat,
                FrameByteOrder.LittleEndian,
                16,
                16,
                FrameSamplePacking.ByteAligned,
                workload.PixelFormat == CameraPixelFormat.BayerRggb16
                    ? ColorFilterArrayPattern.Rggb
                    : ColorFilterArrayPattern.None,
                0,
                ushort.MaxValue,
                workload.PayloadBytes),
            payload,
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(5),
            new ProcessingCompatibilityIdentity("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "processing"));
        return new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")),
            ProcessingInputSelector.Raw("source"),
            [input],
            "performance",
            InputArtifactId: input.ArtifactId);
    }

    private static void VerifyOutcome(ProcessingOutcome expected, ProcessingOutcome actual)
    {
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, actual.Status, actual.ReasonCode);
        var expectedProduct = expected.Products.Single();
        var actualProduct = actual.Products.Single();
        Assert.AreEqual(expectedProduct.OutputIdentitySha256, actualProduct.OutputIdentitySha256);
        Assert.AreEqual(expectedProduct.ChecksumSha256, actualProduct.ChecksumSha256);
        Assert.IsTrue(expectedProduct.Payload.Span.SequenceEqual(actualProduct.Payload.Span));
    }

    private static ReplayRunnerJobContext CreateJobContext(string nodeId) => ReplayRunnerJobContext.Create(
        Guid.NewGuid().ToString("N"),
        "performance@1",
        new string('A', 64),
        nodeId,
        1,
        Guid.NewGuid().ToString("N"),
        DateTimeOffset.UtcNow.AddMinutes(10));

    private static Process StartRunner(string runnerPath, string socketPath)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(runnerPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        process.StartInfo.Environment["HVO_REPLAY_SOCKET_PATH"] = socketPath;
        process.StartInfo.Environment["HVO_REPLAY_AUTH_KEY"] = Encoding.UTF8.GetString(AuthenticationKey);
        process.StartInfo.Environment["HVO_REPLAY_MAX_CONCURRENCY"] = "1";
        process.StartInfo.Environment["HVO_REPLAY_HEARTBEAT_INTERVAL_SECONDS"] = "1";
        process.StartInfo.Environment["HVO_REPLAY_HEARTBEAT_TIMEOUT_SECONDS"] = "10";
        process.StartInfo.Environment["HVO_REPLAY_MAX_TRANSFER_BYTES"] =
            LocalReplayRunnerOptions.MaximumTransferBytes.ToString(CultureInfo.InvariantCulture);
        if (!process.Start()) throw new InvalidOperationException("The replay runner process did not start.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitForSocketAsync(string socketPath, Process runner)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!Path.Exists(socketPath))
        {
            if (runner.HasExited)
            {
                throw new InvalidOperationException($"Replay runner exited during startup with code {runner.ExitCode}.");
            }
            await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }
    }

    private static async Task<long> SampleWorkingSetAsync(Process process, CancellationToken cancellationToken)
    {
        long maximum = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested && !process.HasExited)
            {
                process.Refresh();
                maximum = Math.Max(maximum, process.WorkingSet64);
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return maximum;
    }

    private static ProcessIo ReadProcessIo(int processId)
    {
        var values = File.ReadLines($"/proc/{processId.ToString(CultureInfo.InvariantCulture)}/io")
            .Select(static line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(static parts => parts.Length == 2)
            .ToDictionary(
                static parts => parts[0],
                static parts => long.Parse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
        return new ProcessIo(
            values.GetValueOrDefault("rchar"),
            values.GetValueOrDefault("wchar"),
            values.GetValueOrDefault("syscr"),
            values.GetValueOrDefault("syscw"),
            values.GetValueOrDefault("read_bytes"),
            values.GetValueOrDefault("write_bytes"));
    }

    private static string ResolveRunnerPath(string repositoryRoot)
    {
        var configured = Environment.GetEnvironmentVariable("HVO_REPLAY_RUNNER_EXECUTABLE");
        var rid = RuntimeInformation.RuntimeIdentifier;
        var path = configured ?? Path.Combine(
            repositoryRoot,
            "src",
            "HVO.SkyMonitor.CameraAgent.ReplayRunner",
            "bin",
            "Release",
            "net10.0",
            rid,
            "HVO.SkyMonitor.CameraAgent.ReplayRunner");
        if (!File.Exists(path))
        {
            Assert.Inconclusive($"Publish or build the Release replay runner first: {path}");
        }
        return path;
    }

    private static string GetRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            current = current.Parent;
        }
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for retained performance evidence.");

    private static string TryReadPriority(Process process)
    {
        try
        {
            return process.PriorityClass.ToString();
        }
        catch (InvalidOperationException)
        {
            return "unavailable";
        }
    }

    private static double Median(double[] values)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length % 2 == 0
            ? (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2
            : ordered[ordered.Length / 2];
    }

    private static double Percentile95(double[] values)
    {
        var ordered = values.Order().ToArray();
        return ordered[(int)Math.Ceiling(ordered.Length * 0.95) - 1];
    }

    private static double PercentChange(double baseline, double candidate) =>
        baseline == 0 ? 0 : (candidate - baseline) / baseline * 100;

    private sealed record DurableTopologyEvidence(
        object Method,
        DurableProfileEnvelope InProcessBaseline,
        DurableLocalRunnerEnvelope LocalRunnerCandidate,
        object RegressionDisposition,
        DurableCorrectnessEvidence Correctness);

    private sealed record DurableProfileEnvelope(
        DurableProfileEvidence Profile,
        double StartupMilliseconds);

    private sealed record DurableLocalRunnerEnvelope(
        DurableProfileEvidence Profile,
        double StartupMilliseconds,
        DurableLivePathEvidence LiveBaseline,
        DurableBacklogEvidence Backlog);

    private sealed record DurableCorrectnessEvidence(
        string OutputIdentitySha256,
        string ChecksumSha256,
        long ByteLength,
        bool EquivalentIdentity,
        bool EquivalentChecksum,
        bool EquivalentBytes,
        bool OrderedRawLineage,
        bool OneDurableCompletionPerReplay,
        bool NoReplayLatestViewReplacement,
        bool RunnerAbsenceConsumedAttempts);

    private sealed record DurableProfileEvidence(
        string Profile,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double TotalMilliseconds,
        double OperationsPerSecond,
        double HostCpuMilliseconds,
        long HostAllocatedBytes,
        long HostWorkingSetBeforeBytes,
        long HostMaximumWorkingSetBytes,
        long HostWorkingSetAfterBytes,
        ProcessIo HostProcessIo,
        double? RunnerCpuMilliseconds,
        long? RunnerWorkingSetBeforeBytes,
        long? RunnerMaximumWorkingSetBytes,
        long? RunnerWorkingSetAfterBytes,
        ProcessIo? RunnerProcessIo,
        DurableOutputEvidence Output)
    {
        public double? MedianLatencyPercentChange { get; init; }
        public double? P95LatencyPercentChange { get; init; }
        public double? ThroughputPercentChange { get; init; }
    }

    private sealed record DurableBacklogEvidence(
        long InitialBacklogCount,
        long InitialBacklogBytes,
        DateTimeOffset? InitialOldestAcceptedUtc,
        double InitialOldestAgeMilliseconds,
        double OutageQueueMilliseconds,
        long UnavailableDeferredCount,
        long AttemptsConsumedDuringOutage,
        double MaximumOldestAgeMilliseconds,
        double DrainMilliseconds,
        double RecoveryMilliseconds,
        double DrainJobsPerSecond,
        double DrainBytesPerSecond,
        int DurableStateSampleCount,
        long FinalBacklogCount,
        long FinalBacklogBytes,
        double LiveMedianMilliseconds,
        double LiveP95Milliseconds,
        double LiveMedianPercentChange,
        double LiveP95PercentChange,
        double LiveStartJitterMedianMilliseconds,
        double LiveStartJitterP95Milliseconds,
        int LiveCapturesStartedBeforeDrain,
        double HostCpuMilliseconds,
        long HostAllocatedBytes,
        long HostMaximumWorkingSetBytes,
        ProcessIo HostProcessIo,
        double RunnerCpuMilliseconds,
        long RunnerMaximumWorkingSetBytes,
        ProcessIo RunnerProcessIo,
        ReplayMetricEvidence Protocol,
        DurableOutputEvidence Output);

    private sealed record DurableLivePathEvidence(
        double MedianMilliseconds,
        double P95Milliseconds,
        double StartJitterMedianMilliseconds,
        double StartJitterP95Milliseconds);

    private sealed record DurableOutputEvidence(
        string OutputIdentitySha256,
        string ChecksumSha256,
        long ByteLength,
        string RelativePath,
        long PublishedAssociationCount,
        long StoredOutputCount);

    private sealed record DurableReplayState(
        long PendingCount,
        long PendingBytes,
        DateTimeOffset? OldestPendingUtc,
        long CompletedCount,
        long TerminalFailureCount,
        long LeasedCount,
        long AttemptCountSum,
        long UnavailableCount);

    private sealed record DurableDrainObservation(
        DurableReplayState Final,
        double MaximumOldestAgeMilliseconds,
        int SampleCount);

    private sealed record DurableLiveEvidence(
        double[] Latencies,
        double[] StartJitter,
        int StartedBeforeDrain);

    private sealed record DurableSession(
        CameraModuleConfig Configuration,
        IRawCaptureIngress Ingress,
        ICaptureLaneStore LaneStore,
        ICaptureLaneHandler LaneHandler,
        CaptureLaneDefinition StandardLane,
        ProcessingGraphOperationsCoordinator Operations,
        ProcessingReplayWorker Worker,
        string RevisionId,
        RawCaptureReceipt Source);

    private sealed record ReplayMetricEvidence(
        long RequestMetadataBytes,
        long RequestPayloadBytes,
        long ResponseMetadataBytes,
        long ResponsePayloadBytes,
        long RequestPayloadFrames,
        long ResponsePayloadFrames,
        long CompletedJobs,
        long UnavailableJobs,
        long CancelledJobs);

    private sealed class ReplayMetricsCollector : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _requestMetadataBytes;
        private long _requestPayloadBytes;
        private long _responseMetadataBytes;
        private long _responsePayloadBytes;
        private long _requestPayloadFrames;
        private long _responsePayloadFrames;
        private long _completedJobs;
        private long _unavailableJobs;
        private long _cancelledJobs;

        public ReplayMetricsCollector()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, LocalReplayRunnerClient.MeterName, StringComparison.Ordinal))
                {
                    listener.EnableMeasurementEvents(instrument, this);
                }
            };
            _listener.SetMeasurementEventCallback<long>(static (instrument, measurement, tags, state) =>
                ((ReplayMetricsCollector)state!).Record(instrument.Name, measurement, tags));
            _listener.Start();
        }

        public ReplayMetricEvidence Snapshot() => new(
            Interlocked.Read(ref _requestMetadataBytes),
            Interlocked.Read(ref _requestPayloadBytes),
            Interlocked.Read(ref _responseMetadataBytes),
            Interlocked.Read(ref _responsePayloadBytes),
            Interlocked.Read(ref _requestPayloadFrames),
            Interlocked.Read(ref _responsePayloadFrames),
            Interlocked.Read(ref _completedJobs),
            Interlocked.Read(ref _unavailableJobs),
            Interlocked.Read(ref _cancelledJobs));

        public void Dispose() => _listener.Dispose();

        private void Record(
            string instrument,
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var direction = ReadTag(tags, "direction");
            var kind = ReadTag(tags, "kind");
            var outcome = ReadTag(tags, "outcome");
            if (string.Equals(instrument, "camera_agent.replay_runner.transfer.bytes", StringComparison.Ordinal))
            {
                ref var target = ref SelectTransferCounter(direction, kind);
                Interlocked.Add(ref target, measurement);
            }
            else if (string.Equals(instrument, "camera_agent.replay_runner.payload.frames", StringComparison.Ordinal))
            {
                ref var target = ref string.Equals(direction, "request", StringComparison.Ordinal)
                    ? ref _requestPayloadFrames
                    : ref _responsePayloadFrames;
                Interlocked.Add(ref target, measurement);
            }
            else if (string.Equals(instrument, "camera_agent.replay_runner.jobs", StringComparison.Ordinal))
            {
                if (string.Equals(outcome, "completed", StringComparison.Ordinal))
                    Interlocked.Add(ref _completedJobs, measurement);
                else if (string.Equals(outcome, "unavailable", StringComparison.Ordinal))
                    Interlocked.Add(ref _unavailableJobs, measurement);
                else if (string.Equals(outcome, "cancelled", StringComparison.Ordinal))
                    Interlocked.Add(ref _cancelledJobs, measurement);
            }
        }

        private ref long SelectTransferCounter(string? direction, string? kind)
        {
            if (string.Equals(direction, "request", StringComparison.Ordinal))
            {
                return ref string.Equals(kind, "metadata", StringComparison.Ordinal)
                    ? ref _requestMetadataBytes
                    : ref _requestPayloadBytes;
            }
            return ref string.Equals(kind, "metadata", StringComparison.Ordinal)
                ? ref _responseMetadataBytes
                : ref _responsePayloadBytes;
        }

        private static string? ReadTag(
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            string name)
        {
            foreach (var tag in tags)
            {
                if (string.Equals(tag.Key, name, StringComparison.Ordinal)) return tag.Value as string;
            }
            return null;
        }
    }

    private sealed record Workload(string Id, int Width, int Height, CameraPixelFormat PixelFormat)
    {
        public int PayloadBytes => checked(Width * Height * 2);
    }

    private sealed record WorkloadEvidence(
        Workload Workload,
        ColdEvidence FirstExternal,
        PathEvidence InProcess,
        ExternalEvidence WarmExternal,
        SimultaneousEvidence? SimultaneousW6SizedPreview)
    {
        public double MedianLatencyPercentChange => PercentChange(
            InProcess.MedianMilliseconds,
            WarmExternal.EndToEndAndRunner.MedianMilliseconds);

        public double P95LatencyPercentChange => PercentChange(
            InProcess.P95Milliseconds,
            WarmExternal.EndToEndAndRunner.P95Milliseconds);

        public double ThroughputPercentChange => PercentChange(
            InProcess.OperationsPerSecond,
            WarmExternal.EndToEndAndRunner.OperationsPerSecond);

        public double CombinedCpuPercentChange => PercentChange(
            InProcess.CpuMilliseconds,
            WarmExternal.EndToEndAndRunner.CpuMilliseconds + WarmExternal.ClientProcess.CpuMilliseconds);

        public double CombinedPeakWorkingSetPercentChange => PercentChange(
            InProcess.MaximumWorkingSetBytes,
            WarmExternal.EndToEndAndRunner.MaximumWorkingSetBytes +
            WarmExternal.ClientProcess.MaximumWorkingSetBytes);

        public double? SimultaneousLiveMedianPercentChange => SimultaneousW6SizedPreview is null
            ? null
            : PercentChange(InProcess.MedianMilliseconds, SimultaneousW6SizedPreview.LiveMedianMilliseconds);

        public double? SimultaneousLiveP95PercentChange => SimultaneousW6SizedPreview is null
            ? null
            : PercentChange(InProcess.P95Milliseconds, SimultaneousW6SizedPreview.LiveP95Milliseconds);
    }

    private sealed record ColdEvidence(
        double TotalMilliseconds,
        double DispatchMilliseconds,
        long RequestMetadataBytes,
        long RequestPayloadBytes,
        long ResponseMetadataBytes,
        long ResponsePayloadBytes);

    private sealed record PathEvidence(
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double TotalMilliseconds,
        double OperationsPerSecond,
        double CpuMilliseconds,
        long? AllocatedBytes,
        long WorkingSetBeforeBytes,
        long MaximumWorkingSetBytes,
        long RequestTransferBytes,
        long ResponseTransferBytes,
        int PayloadFrames,
        int HeartbeatCount,
        ProcessIo? ProcessIo);

    private sealed record ExternalEvidence(
        PathEvidence EndToEndAndRunner,
        ProcessResources ClientProcess);

    private sealed record ProcessResources(
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long MaximumWorkingSetBytes);

    private sealed record SimultaneousEvidence(
        double LiveMedianMilliseconds,
        double LiveP95Milliseconds,
        double StartDelayMedianMilliseconds,
        double StartDelayP95Milliseconds,
        double CampaignSeconds);

    private sealed record ProcessIo(
        long LogicalReadBytes,
        long LogicalWriteBytes,
        long ReadSyscalls,
        long WriteSyscalls,
        long PhysicalReadBytes,
        long PhysicalWriteBytes)
    {
        public static ProcessIo operator -(ProcessIo after, ProcessIo before) => new(
            after.LogicalReadBytes - before.LogicalReadBytes,
            after.LogicalWriteBytes - before.LogicalWriteBytes,
            after.ReadSyscalls - before.ReadSyscalls,
            after.WriteSyscalls - before.WriteSyscalls,
            after.PhysicalReadBytes - before.PhysicalReadBytes,
            after.PhysicalWriteBytes - before.PhysicalWriteBytes);
    }
}
