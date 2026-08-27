using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class TransientWorkerAcceptancePerformanceTests
{
    private const int WarmupCount = 5;
    private const int MeasuredCount = 30;
    private const int TrialCount = 5;
    private const int W3MetadataCount = 10_000;
    private const int W3PayloadCount = 100;
    private const int Seed = 63;
    private const double BlockedRelativeTolerance = 0.35;
    private const double BlockedAbsoluteToleranceMilliseconds = 2;
    private const double BlockedHardRatioLimit = 2;
    private static readonly DateTimeOffset Epoch = new(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [TestMethod]
    public async Task W1W2W3MAndW3PTransientWorkerEvidence()
    {
        Assert.AreEqual(Architecture.X64, RuntimeInformation.ProcessArchitecture,
            "Issue #63 performance evidence is currently approved only for x64.");
        var repositoryRoot = GetRepositoryRoot();
        var configurationName = typeof(TransientWorkerAcceptancePerformanceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        Assert.AreEqual("Release", configurationName, "Issue #63 performance evidence requires a Release build.");
        var git = ReadGitEvidence(repositoryRoot);
        var dirtyDevelopment = git.DirtyState.Length > 0;
        if (dirtyDevelopment && !string.Equals(
                Environment.GetEnvironmentVariable("HVO_PERF_ALLOW_DIRTY_DEVELOPMENT"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Dirty-tree measurements are development-only. Set HVO_PERF_ALLOW_DIRTY_DEVELOPMENT=1 to emit fingerprinted non-claimable evidence.");
        }
        var source = ReadSourceEvidence(repositoryRoot, git);
        var binaries = ReadBinaryEvidence();
        var outputRoot = Path.Combine(repositoryRoot, "TestResults", "issue-63");
        var workRoot = Path.Combine(outputRoot, "work");
        Directory.CreateDirectory(outputRoot);
        if (Directory.Exists(workRoot))
        {
            Directory.Delete(workRoot, recursive: true);
        }
        Directory.CreateDirectory(workRoot);
        using var signals = new RuntimeSignalCollector();
        var positive = await MeasurePositiveModesAsync(Path.Combine(workRoot, "positive"), signals).ConfigureAwait(false);
        var failure = await MeasureFailureTransitionAsync(
            Path.Combine(workRoot, "failure"), Workload.W1, signals).ConfigureAwait(false);

        var workloads = new[] { Workload.W1, Workload.W2 };
        var steady = new List<SteadyWorkloadEvidence>();
        foreach (var workload in workloads)
        {
            var trials = new List<WorkerTrial>();
            for (var trial = 0; trial < TrialCount; trial++)
            {
                trials.Add(await MeasureSteadyWorkerTrialAsync(
                    Path.Combine(workRoot, $"{workload.Id}-trial-{trial + 1}"),
                    workload,
                    signals).ConfigureAwait(false));
            }
            steady.Add(new SteadyWorkloadEvidence(workload.Id, trials, SummarizeTrials(trials)));
        }

        var w3m = await MeasureW3MetadataRecoveryAsync(Path.Combine(workRoot, "w3m"), Workload.W2).ConfigureAwait(false);
        var w3p = await MeasureW3PayloadDrainAsync(
            Path.Combine(workRoot, "w3p"), Workload.W2, signals).ConfigureAwait(false);
        var off = await MeasureOffModeAsync(Path.Combine(workRoot, "off")).ConfigureAwait(false);
        var isolation = await MeasureBlockedIsolationAsync(
            Path.Combine(workRoot, "isolation"), Workload.W1).ConfigureAwait(false);

        Assert.IsGreaterThan(1d, w3p.DrainCapturesPerSecond);
        Assert.AreEqual(0L, w3p.FinalBacklogCount);
        Assert.AreEqual(0L, w3p.FinalBacklogBytes);
        Assert.AreEqual(0L, off.TransientLaneRows);
        Assert.AreEqual(0L, off.TransientWorkerTables);
        AssertRegressionWithinBudget(
            isolation.BaselineAcceptMedianMilliseconds.Median,
            isolation.BlockedAcceptMedianMilliseconds.Median,
            "Blocked optional transient work materially regressed ingress.");
        AssertRegressionWithinBudget(
            isolation.BaselineAcceptP95Milliseconds.Median,
            isolation.BlockedAcceptP95Milliseconds.Median,
            "Blocked optional transient work materially regressed ingress p95.");
        AssertRegressionWithinBudget(
            isolation.BaselineStandardMedianMilliseconds.Median,
            isolation.BlockedStandardMedianMilliseconds.Median,
            "Blocked optional transient work materially regressed standard acknowledgement.");
        AssertRegressionWithinBudget(
            isolation.BaselineStandardP95Milliseconds.Median,
            isolation.BlockedStandardP95Milliseconds.Median,
            "Blocked optional transient work materially regressed standard acknowledgement p95.");

        signals.RecordObservableInstruments();
        var signalEvidence = signals.Snapshot();
        Assert.IsTrue(signalEvidence.MetricNames.Contains("hvo.transient.worker.outcomes"));
        Assert.IsTrue(signalEvidence.ActivityNames.Contains("transient.causal"));
        Assert.IsTrue(signalEvidence.ActivityNames.Contains("transient.centered"));
        Assert.IsTrue(signalEvidence.ActivityNames.Contains("transient.handoff"));
        Assert.IsTrue(signalEvidence.LogEventIds.Contains(2200));
        AssertTelemetryIsBoundedAndPrivate(signalEvidence, source, positive);
        var endGit = ReadGitEvidence(repositoryRoot);
        var endSource = ReadSourceEvidence(repositoryRoot, endGit);
        var endBinaries = ReadBinaryEvidence();
        AssertRunInputsUnchanged(git, source, binaries, endGit, endSource, endBinaries);
        var startEmbeddedRevisions = ReadAssemblyRepositoryRevisions(binaries);
        var endEmbeddedRevisions = ReadAssemblyRepositoryRevisions(endBinaries);
        var embeddedRevisionsMatchHead =
            TransientEvidenceProvenance.EmbeddedRevisionsMatchHead(git.Revision, startEmbeddedRevisions) &&
            TransientEvidenceProvenance.EmbeddedRevisionsMatchHead(endGit.Revision, endEmbeddedRevisions);
        if (!dirtyDevelopment)
        {
            TransientEvidenceProvenance.RequireEmbeddedRevisionsMatchHead(git.Revision, startEmbeddedRevisions);
            TransientEvidenceProvenance.RequireEmbeddedRevisionsMatchHead(endGit.Revision, endEmbeddedRevisions);
        }

        var evidence = new
        {
            SchemaVersion = "issue-63-transient-worker-performance-v1",
            RecordedUtc = DateTimeOffset.UtcNow,
            Revision = git.Revision,
            Branch = git.Branch,
            DirtyState = git.DirtyState,
            EvidenceDisposition = dirtyDevelopment ? "dirty-development-not-claimable" : "clean-candidate",
            CleanCommittedReplacementRequired = dirtyDevelopment,
            Source = source,
            Binaries = binaries,
            CleanCandidateRevisionBinding = new
            {
                Required = !dirtyDevelopment,
                RecordedHead = git.Revision,
                StartEmbeddedRevisions = startEmbeddedRevisions,
                EndEmbeddedRevisions = endEmbeddedRevisions,
                AllAssembliesMatchRecordedHead = embeddedRevisionsMatchHead
            },
            RunInputStability = new
            {
                Start = new { Git = git, Source = source, Binaries = binaries },
                End = new { Git = endGit, Source = endSource, Binaries = endBinaries },
                ExactMatch = true
            },
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                Sdk = ReadSdk(repositoryRoot),
                Configuration = configurationName,
                Environment.ProcessorCount,
                TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                ServerGc = System.Runtime.GCSettings.IsServerGC,
                StorageFormat = new DriveInfo(Path.GetPathRoot(outputRoot)!).DriveFormat,
                SqliteVersion = await ReadSqliteVersionAsync().ConfigureAwait(false)
            },
            Method = new
            {
                Command = "dotnet build tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --configuration Release --arch x64 -warnaserror && " +
                    (dirtyDevelopment ? "HVO_PERF_ALLOW_DIRTY_DEVELOPMENT=1 " : string.Empty) +
                    "DOTNET_gcServer=1 dotnet test tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build --configuration Release --arch x64 --filter FullyQualifiedName~TransientWorkerAcceptancePerformanceTests.W1W2W3MAndW3PTransientWorkerEvidence",
                TrialCount,
                WarmupCount,
                MeasuredCount,
                Concurrency = 1,
                W3MetadataCount,
                W3PayloadCount,
                ArrivalRate = "pre-staged; cadence metadata is 5 seconds",
                Sampling = "wall latency per operation; process CPU/approximate allocation/RSS and post-operation GC/LOH; /proc/self/io where available",
                P95 = "nearest-rank p95 from 30 measured operations in each trial; five-trial summaries report median/min/max of trial statistics",
                BlockedTolerance = "five interleaved independent trials; compare medians of per-trial median and p95; limit is min(baseline*2, baseline + max(35%, 2 ms))"
            },
            Workloads = new
            {
                W1 = new { Workload.W1.Width, Workload.W1.Height, Format = Workload.W1.Format.ToString(), Workload.W1.PayloadBytes },
                W2 = new { Workload.W2.Width, Workload.W2.Height, Format = Workload.W2.Format.ToString(), Workload.W2.PayloadBytes },
                W3M = new { Records = W3MetadataCount, PayloadCopies = 0 },
                W3P = new { Captures = W3PayloadCount, RawBytes = checked((long)W3PayloadCount * Workload.W2.PayloadBytes) }
            },
            Steady = steady,
            W3M = w3m,
            W3P = w3p,
            Off = off,
            BlockedIsolation = isolation,
            PositiveModes = positive,
            FailureTransition = failure,
            RuntimeSignals = signalEvidence,
            Correctness = new
            {
                DeterministicSeed = Seed,
                CandidateRate = "zero for no-event steady and W3P controls; positive event convergence is covered by the acceptance fault matrix",
                ExactIdentities = "each trial pins raw payload SHA-256 and a canonical SHA-256 over ordered durable frame outcome rows",
                PayloadCopies = "durable .bin files and runtime evidence load/file/byte counters are measured; no copy count is inferred from workload shape",
                PhysicalClaim = "none",
                Arm64Claim = "none; harness rejects non-x64 execution"
            },
            Limitations = new[]
            {
                "Synthetic deterministic zero-valued full-frame controls measure runtime cost, not physical sensitivity.",
                "RSS is sampled at operation boundaries and is not an in-operation native-memory peak.",
                "Filesystem and SQLite byte deltas combine logical counters with process/filesystem observations and do not claim device-level fsync attribution.",
                "W3M is an indexed metadata/recovery workload over shared payload references; it does not create 10,000 full payload files."
            }
        };
        var outputPath = Path.Combine(outputRoot, "transient-worker-performance.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceOptions)).ConfigureAwait(false);
        var checksum = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(outputPath).ConfigureAwait(false)));
        TestContext.WriteLine($"Issue #63 evidence: {outputPath}");
        TestContext.WriteLine($"Issue #63 evidence SHA-256: {checksum}");
        TestContext.WriteLine($"W3P drain: {w3p.DrainCapturesPerSecond:F3} captures/s");
        foreach (var item in steady)
        {
            TestContext.WriteLine(
                $"{item.Workload}: median={item.Summary.MedianMilliseconds.Median:F3}ms " +
                $"p95={item.Summary.P95Milliseconds.Median:F3}ms throughput={item.Summary.ThroughputPerSecond.Median:F3}/s");
        }
        Directory.Delete(workRoot, recursive: true);
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<WorkerTrial> MeasureSteadyWorkerTrialAsync(
        string root,
        Workload workload,
        RuntimeSignalCollector signals)
    {
        Directory.CreateDirectory(root);
        var configuration = CreateConfiguration(workload, "steady-agent");
        using var provider = CreateProvider(root, configuration, signals);
        await StageFramesAsync(provider, configuration, workload, WarmupCount + MeasuredCount + 2).ConfigureAwait(false);
        var worker = provider.GetRequiredService<TransientWorkerService>();
        Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
        Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
        for (var index = 0; index < WarmupCount; index++)
        {
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var ioStart = ReadProcessIo();
        var cpuStart = process.TotalProcessorTime;
        var allocationStart = GC.GetTotalAllocatedBytes(false);
        var evidenceLoadsStart = signals.MetricTotal("hvo.transient.worker.evidence.loads");
        var evidenceBytesStart = signals.MetricTotal("hvo.transient.worker.evidence.bytes");
        var evidenceFilesStart = signals.MetricTotal("hvo.transient.worker.evidence.files");
        var rssStart = process.WorkingSet64;
        var peakRss = rssStart;
        var lohStart = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        var samples = new double[MeasuredCount];
        for (var index = 0; index < samples.Length; index++)
        {
            var stopwatch = Stopwatch.StartNew();
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            stopwatch.Stop();
            samples[index] = stopwatch.Elapsed.TotalMilliseconds;
            process.Refresh();
            peakRss = Math.Max(peakRss, process.WorkingSet64);
        }
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuStart;
        var allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        Assert.IsGreaterThanOrEqualTo(0L, allocated, "The process allocation counter must not decrease during a trial.");
        var rssEnd = process.WorkingSet64;
        var ioEnd = ReadProcessIo();
        var lohEnd = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        var store = provider.GetRequiredService<SqliteTransientRuntimeStore>();
        await store.RetireBeforeAsync(configuration.AgentId!, long.MaxValue, CancellationToken.None).ConfigureAwait(false);
        var backlog = await provider.GetRequiredService<ITransientCandidateJournal>()
            .ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        var candidateCount = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false);
        Assert.AreEqual(0L, candidateCount);
        Assert.AreEqual(0L, backlog.ActiveCount);
        var rawSha = await ScalarStringAsync(connection,
            "SELECT payload_sha256 FROM raw_captures ORDER BY capture_sequence LIMIT 1;").ConfigureAwait(false);
        var stateIdentity = await StateIdentityAsync(connection).ConfigureAwait(false);
        var databaseBytes = DatabaseBytes(root);
        Array.Sort(samples);
        var totalSeconds = samples.Sum() / 1000d;

        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitUntilAsync(
            () => provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                TransientWorkerAvailability.Healthy,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        var health = await new TransientWorkerHealthCheck(provider.GetRequiredService<TransientWorkerState>())
            .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HealthStatus.Healthy, health.Status);

        var trial = new WorkerTrial(
            samples[samples.Length / 2],
            samples[(int)Math.Ceiling(samples.Length * 0.95) - 1],
            MeasuredCount / totalSeconds,
            cpu.TotalMilliseconds,
            cpu.TotalMilliseconds / MeasuredCount,
            allocated / (double)MeasuredCount,
            rssStart,
            peakRss,
            rssEnd,
            lohStart,
            lohEnd,
            Subtract(ioEnd.ReadBytes, ioStart.ReadBytes),
            Subtract(ioEnd.WriteBytes, ioStart.WriteBytes),
            Subtract(ioEnd.ReadOperations, ioStart.ReadOperations),
            Subtract(ioEnd.WriteOperations, ioStart.WriteOperations),
            databaseBytes,
            EvidenceReadBytes: checked((long)(signals.MetricTotal("hvo.transient.worker.evidence.bytes") - evidenceBytesStart)),
            EvidenceFilesRead: checked((long)(signals.MetricTotal("hvo.transient.worker.evidence.files") - evidenceFilesStart)),
            PayloadReloadsPerOperation:
                (signals.MetricTotal("hvo.transient.worker.evidence.loads") - evidenceLoadsStart) / MeasuredCount,
            CandidateCount: candidateCount,
            CandidateRatePerFrame: candidateCount / (double)MeasuredCount,
            FinalBacklogCount: backlog.ActiveCount,
            FinalBacklogBytes: backlog.HeldSourceBytes,
            RawPayloadSha256: rawSha,
            DurableStateSha256: stateIdentity,
            HealthStatus: health.Status.ToString());
        SqliteConnection.ClearAllPools();
        Directory.Delete(root, recursive: true);
        return trial;
    }

    private static async Task<W3MetadataEvidence> MeasureW3MetadataRecoveryAsync(string root, Workload workload)
    {
        Directory.CreateDirectory(root);
        var configuration = CreateConfiguration(workload, "w3m-agent");
        using var provider = CreateProvider(root, configuration, signals: null, required: true);
        await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await InsertMetadataWorkAsync(connection, configuration.AgentId!).ConfigureAwait(false);
        stopwatch.Stop();
        var sqliteBytes = DatabaseBytes(root);
        var store = provider.GetRequiredService<SqliteTransientRuntimeStore>();
        var recovery = Stopwatch.StartNew();
        await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(await store.ReadNextAsync(CancellationToken.None).ConfigureAwait(false));
        recovery.Stop();
        var backlog = await provider.GetRequiredService<ITransientCandidateJournal>()
            .ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(W3MetadataCount, backlog.ActiveCount);
        Assert.AreEqual(W3MetadataCount, await ScalarLongAsync(
            connection, "SELECT COUNT(*) FROM transient_worker_frames;").ConfigureAwait(false));
        return new W3MetadataEvidence(
            W3MetadataCount,
            stopwatch.Elapsed.TotalMilliseconds,
            W3MetadataCount / stopwatch.Elapsed.TotalSeconds,
            recovery.Elapsed.TotalMilliseconds,
            W3MetadataCount / recovery.Elapsed.TotalSeconds,
            sqliteBytes,
            backlog.ActiveCount,
            backlog.HeldSourceBytes,
            backlog.OldestCreatedUtc,
            PayloadCopies: CountStoredPayloadCopies(root));
    }

    private static async Task<W3PayloadEvidence> MeasureW3PayloadDrainAsync(
        string root,
        Workload workload,
        RuntimeSignalCollector signals)
    {
        Directory.CreateDirectory(root);
        var configuration = CreateConfiguration(workload, "w3p-agent");
        using var provider = CreateProvider(root, configuration, signals);
        await StageFramesAsync(provider, configuration, workload, W3PayloadCount).ConfigureAwait(false);
        var journal = provider.GetRequiredService<ITransientCandidateJournal>();
        var initial = await journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(W3PayloadCount, initial.ActiveCount);
        Assert.AreEqual(checked((long)W3PayloadCount * workload.PayloadBytes), initial.HeldSourceBytes);
        var worker = provider.GetRequiredService<TransientWorkerService>();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuStart = process.TotalProcessorTime;
        var allocationStart = GC.GetTotalAllocatedBytes(false);
        var evidenceLoadsStart = signals.MetricTotal("hvo.transient.worker.evidence.loads");
        var evidenceBytesStart = signals.MetricTotal("hvo.transient.worker.evidence.bytes");
        var evidenceFilesStart = signals.MetricTotal("hvo.transient.worker.evidence.files");
        var rssStart = process.WorkingSet64;
        var peakRss = rssStart;
        var ioStart = ReadProcessIo();
        var measuredSamples = new List<double>(MeasuredCount);
        var drain = Stopwatch.StartNew();
        for (var index = 0; index < W3PayloadCount; index++)
        {
            var operation = Stopwatch.StartNew();
            Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            operation.Stop();
            if (index >= WarmupCount && measuredSamples.Count < MeasuredCount)
            {
                measuredSamples.Add(operation.Elapsed.TotalMilliseconds);
            }
            process.Refresh();
            peakRss = Math.Max(peakRss, process.WorkingSet64);
        }
        await provider.GetRequiredService<SqliteTransientRuntimeStore>()
            .RetireBeforeAsync(configuration.AgentId!, long.MaxValue, CancellationToken.None).ConfigureAwait(false);
        drain.Stop();
        process.Refresh();
        var ioEnd = ReadProcessIo();
        var allocated = GC.GetTotalAllocatedBytes(false) - allocationStart;
        Assert.IsGreaterThanOrEqualTo(0L, allocated, "The process allocation counter must not decrease during backlog drain.");
        var final = await journal.ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        measuredSamples.Sort();
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        var stateIdentity = await StateIdentityAsync(connection).ConfigureAwait(false);
        var rawSha = await ScalarStringAsync(connection,
            "SELECT payload_sha256 FROM raw_captures ORDER BY capture_sequence LIMIT 1;").ConfigureAwait(false);
        var retainedBytes = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .Sum(static path => new FileInfo(path).Length);
        return new W3PayloadEvidence(
            W3PayloadCount,
            checked((long)W3PayloadCount * workload.PayloadBytes),
            initial.ActiveCount,
            initial.HeldSourceBytes,
            initial.OldestCreatedUtc,
            final.ActiveCount,
            final.HeldSourceBytes,
            drain.Elapsed.TotalMilliseconds,
            W3PayloadCount / drain.Elapsed.TotalSeconds,
            measuredSamples[measuredSamples.Count / 2],
            measuredSamples[(int)Math.Ceiling(measuredSamples.Count * 0.95) - 1],
            (process.TotalProcessorTime - cpuStart).TotalMilliseconds,
            allocated,
            rssStart,
            peakRss,
            process.WorkingSet64,
            GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes,
            Subtract(ioEnd.ReadBytes, ioStart.ReadBytes),
            Subtract(ioEnd.WriteBytes, ioStart.WriteBytes),
            EvidenceReadBytes: checked((long)(signals.MetricTotal("hvo.transient.worker.evidence.bytes") - evidenceBytesStart)),
            EvidenceFilesRead: checked((long)(signals.MetricTotal("hvo.transient.worker.evidence.files") - evidenceFilesStart)),
            DatabaseBytes: DatabaseBytes(root),
            RetainedFilesystemBytes: retainedBytes,
            StoredPayloadCopiesPerCapture: CountStoredPayloadCopies(root) / (double)W3PayloadCount,
            PayloadReloadsPerCapture:
                (signals.MetricTotal("hvo.transient.worker.evidence.loads") - evidenceLoadsStart) / W3PayloadCount,
            RawPayloadSha256: rawSha,
            DurableStateSha256: stateIdentity);
    }

    private static async Task<IReadOnlyList<PositiveModeEvidence>> MeasurePositiveModesAsync(
        string root,
        RuntimeSignalCollector signals)
    {
        var cases = new[]
        {
            (Mode: TransientOperatingMode.Edge, Workload: Workload.W1),
            (Mode: TransientOperatingMode.Hybrid, Workload: Workload.W2)
        };
        var output = new List<PositiveModeEvidence>();
        foreach (var scenario in cases)
        {
            var trials = new List<PositiveModeTrial>();
            for (var trial = 0; trial < TrialCount; trial++)
            {
                trials.Add(await MeasurePositiveModeTrialAsync(
                    Path.Combine(root, $"{scenario.Mode}-{scenario.Workload.Id}-trial-{trial + 1}"),
                    scenario.Workload,
                    scenario.Mode,
                    trial + 1,
                    signals).ConfigureAwait(false));
            }
            Assert.AreEqual(1, trials.Select(static value => value.RawPayloadSha256).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(1, trials.Select(static value => value.DurableStateSha256).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(1, trials.Select(static value => value.CanonicalOutcomeSha256).Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(1, trials.Select(static value => value.CandidateCount).Distinct().Count());
            var identities = trials.SelectMany(static value => value.Identities).ToArray();
            Assert.AreEqual(identities.Length, identities.Select(static value => value.CandidateId).Distinct().Count());
            Assert.AreEqual(identities.Length, identities.Select(static value => value.EventId).Distinct().Count());
            Assert.AreEqual(identities.Length, identities.Select(static value => value.EventVersionId).Distinct().Count());
            Assert.AreEqual(identities.Length, identities.Select(static value => value.DeliveryIdentitySha256).Distinct(StringComparer.Ordinal).Count());
            output.Add(new PositiveModeEvidence(
                scenario.Mode.ToString(),
                scenario.Workload.Id,
                trials,
                Summarize(trials.Select(static value => value.ElapsedMilliseconds)),
                Summarize(trials.Select(static value => value.EvidenceLoads)),
                Summarize(trials.Select(static value => (double)value.EvidenceReadBytes))));
        }
        return output;
    }

    private static async Task<PositiveModeTrial> MeasurePositiveModeTrialAsync(
        string root,
        Workload workload,
        TransientOperatingMode mode,
        int trial,
        RuntimeSignalCollector signals)
    {
        Directory.CreateDirectory(root);
        var configuration = CreatePositiveConfiguration(workload, $"positive-{mode.ToString().ToUpperInvariant()}-{trial}");
        using var provider = CreateProvider(root, configuration, signals, mode);
        var worker = provider.GetRequiredService<TransientWorkerService>();
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitUntilAsync(
            () => provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                TransientWorkerAvailability.Healthy,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await StagePositiveFramesAsync(provider, configuration, workload, 7).ConfigureAwait(false);
        var evidenceLoadsStart = signals.MetricTotal("hvo.transient.worker.evidence.loads");
        var evidenceBytesStart = signals.MetricTotal("hvo.transient.worker.evidence.bytes");
        var evidenceFilesStart = signals.MetricTotal("hvo.transient.worker.evidence.files");
        var stopwatch = Stopwatch.StartNew();
        await DrainWorkerAsync(worker, 64).ConfigureAwait(false);
        await provider.GetRequiredService<SqliteTransientRuntimeStore>()
            .RetireBeforeAsync(configuration.AgentId!, long.MaxValue, CancellationToken.None).ConfigureAwait(false);
        stopwatch.Stop();
        var health = await new TransientWorkerHealthCheck(provider.GetRequiredService<TransientWorkerState>())
            .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        var candidateCount = await ScalarLongAsync(connection, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false);
        Assert.IsGreaterThan(0L, candidateCount);
        var expectedPhase = mode == TransientOperatingMode.Edge ? "finalized" : "handoff_pending";
        Assert.AreEqual(0L, await ScalarLongAsync(connection,
            $"SELECT COUNT(*) FROM transient_candidates WHERE phase != '{expectedPhase}';").ConfigureAwait(false));
        Assert.AreEqual(HealthStatus.Healthy, health.Status);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.candidate_id, c.event_id, c.event_version_id, j.phase,
                   j.candidate_payload, j.finalization_payload, j.submission_payload,
                   j.finalization_receipt_identity_sha256, j.submission_identity_sha256
            FROM transient_worker_candidates c
            JOIN transient_candidates j ON j.candidate_id = c.candidate_id
            ORDER BY c.slot_ordinal LIMIT 1;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        var candidateId = Guid.ParseExact(reader.GetString(0), "N");
        var eventId = Guid.ParseExact(reader.GetString(1), "N");
        var eventVersionId = Guid.ParseExact(reader.GetString(2), "N");
        var phase = reader.GetString(3);
        var candidateBytes = await reader.GetFieldValueAsync<byte[]>(4).ConfigureAwait(false);
        var parsedCandidate = TransientContractJson.ParseCandidate(candidateBytes);
        Assert.IsTrue(parsedCandidate.Validation.IsValid, parsedCandidate.Validation.ReasonCode);
        Assert.IsNotNull(parsedCandidate.Value);
        Assert.AreEqual(candidateId, parsedCandidate.Value.CandidateId);
        Assert.AreEqual(eventId, parsedCandidate.Value.EventId);
        string deliveryIdentity;
        var eventVersion = 0;
        if (mode == TransientOperatingMode.Edge)
        {
            Assert.AreEqual("finalized", phase);
            var receipt = TransientCandidateDeliveryJson.ParseFinalization(
                await reader.GetFieldValueAsync<byte[]>(5).ConfigureAwait(false));
            Assert.IsTrue(receipt.Validation.IsValid, receipt.Validation.ReasonCode);
            Assert.IsNotNull(receipt.Value);
            Assert.AreEqual(candidateId, receipt.Value.CandidateId);
            Assert.AreEqual(eventId, receipt.Value.EventId);
            Assert.AreEqual(eventVersionId, receipt.Value.Event.EventVersionId);
            Assert.AreEqual(1, receipt.Value.Event.Version);
            Assert.HasCount(1, receipt.Value.Event.Observations);
            Assert.HasCount(1, receipt.Value.Event.Assessments);
            deliveryIdentity = reader.GetString(7);
            Assert.AreEqual(deliveryIdentity, receipt.Value.ReceiptIdentitySha256);
            eventVersion = receipt.Value.Event.Version;
        }
        else
        {
            Assert.AreEqual("handoff_pending", phase);
            var submission = TransientCandidateDeliveryJson.ParseSubmission(
                await reader.GetFieldValueAsync<byte[]>(6).ConfigureAwait(false));
            Assert.IsTrue(submission.Validation.IsValid, submission.Validation.ReasonCode);
            Assert.IsNotNull(submission.Value);
            Assert.AreEqual(candidateId, submission.Value.CandidateId);
            Assert.AreEqual(eventId, submission.Value.EventId);
            Assert.AreEqual(candidateId, submission.Value.Candidate.CandidateId);
            deliveryIdentity = reader.GetString(8);
            Assert.AreEqual(deliveryIdentity, submission.Value.SubmissionIdentitySha256);
        }
        await reader.DisposeAsync().ConfigureAwait(false);
        var identities = await ValidatePositiveIdentitiesAsync(connection, mode).ConfigureAwait(false);
        Assert.AreEqual(candidateCount, identities.Count);
        var rawSha = await ScalarStringAsync(connection,
            "SELECT payload_sha256 FROM raw_captures WHERE capture_sequence = 5;").ConfigureAwait(false);
        var stateSha = await StateIdentityAsync(connection).ConfigureAwait(false);
        var canonicalOutcome = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{mode}|{phase}|{candidateCount}|{eventVersion}|{await ScalarLongAsync(connection, "SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'completed';").ConfigureAwait(false)}")));
        var result = new PositiveModeTrial(
            trial,
            stopwatch.Elapsed.TotalMilliseconds,
            candidateCount,
            identities,
            candidateId,
            eventId,
            eventVersionId,
            eventVersion,
            phase,
            Convert.ToHexString(SHA256.HashData(candidateBytes)),
            deliveryIdentity,
            rawSha,
            stateSha,
            canonicalOutcome,
            signals.MetricTotal("hvo.transient.worker.evidence.loads") - evidenceLoadsStart,
            checked((long)(signals.MetricTotal("hvo.transient.worker.evidence.bytes") - evidenceBytesStart)),
            checked((long)(signals.MetricTotal("hvo.transient.worker.evidence.files") - evidenceFilesStart)),
            CountStoredPayloadCopies(root),
            health.Status.ToString());
        SqliteConnection.ClearAllPools();
        return result;
    }

    private static async Task<IReadOnlyList<PositiveIdentityEvidence>> ValidatePositiveIdentitiesAsync(
        SqliteConnection connection,
        TransientOperatingMode mode)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.candidate_id, c.event_id, c.observation_id, c.assessment_id, c.event_version_id,
                   j.candidate_payload, j.finalization_payload, j.submission_payload,
                   j.finalization_receipt_identity_sha256, j.submission_identity_sha256
            FROM transient_worker_candidates c
            JOIN transient_candidates j ON j.candidate_id = c.candidate_id
            ORDER BY c.slot_ordinal;
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var output = new List<PositiveIdentityEvidence>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var candidateId = Guid.ParseExact(reader.GetString(0), "N");
            var eventId = Guid.ParseExact(reader.GetString(1), "N");
            var observationId = Guid.ParseExact(reader.GetString(2), "N");
            var assessmentId = Guid.ParseExact(reader.GetString(3), "N");
            var eventVersionId = Guid.ParseExact(reader.GetString(4), "N");
            var candidateBytes = await reader.GetFieldValueAsync<byte[]>(5).ConfigureAwait(false);
            var candidate = TransientContractJson.ParseCandidate(candidateBytes);
            Assert.IsTrue(candidate.Validation.IsValid, candidate.Validation.ReasonCode);
            Assert.IsNotNull(candidate.Value);
            Assert.AreEqual(candidateId, candidate.Value.CandidateId);
            Assert.AreEqual(eventId, candidate.Value.EventId);
            string deliveryIdentity;
            var eventVersion = 0;
            if (mode == TransientOperatingMode.Edge)
            {
                var receipt = TransientCandidateDeliveryJson.ParseFinalization(
                    await reader.GetFieldValueAsync<byte[]>(6).ConfigureAwait(false));
                Assert.IsTrue(receipt.Validation.IsValid, receipt.Validation.ReasonCode);
                Assert.IsNotNull(receipt.Value);
                Assert.AreEqual(candidateId, receipt.Value.CandidateId);
                Assert.AreEqual(eventId, receipt.Value.EventId);
                Assert.AreEqual(eventVersionId, receipt.Value.Event.EventVersionId);
                Assert.IsTrue(receipt.Value.Event.Observations.Any(value =>
                    value.ObservationId == observationId && value.Extraction.OriginatingCandidateId == candidateId));
                Assert.IsTrue(receipt.Value.Event.Assessments.Any(value => value.AssessmentId == assessmentId));
                deliveryIdentity = reader.GetString(8);
                Assert.AreEqual(deliveryIdentity, receipt.Value.ReceiptIdentitySha256);
                eventVersion = receipt.Value.Event.Version;
            }
            else
            {
                var submission = TransientCandidateDeliveryJson.ParseSubmission(
                    await reader.GetFieldValueAsync<byte[]>(7).ConfigureAwait(false));
                Assert.IsTrue(submission.Validation.IsValid, submission.Validation.ReasonCode);
                Assert.IsNotNull(submission.Value);
                Assert.AreEqual(candidateId, submission.Value.CandidateId);
                Assert.AreEqual(eventId, submission.Value.EventId);
                Assert.AreEqual(candidateId, submission.Value.Candidate.CandidateId);
                deliveryIdentity = reader.GetString(9);
                Assert.AreEqual(deliveryIdentity, submission.Value.SubmissionIdentitySha256);
            }
            output.Add(new PositiveIdentityEvidence(
                candidateId,
                eventId,
                observationId,
                assessmentId,
                eventVersionId,
                eventVersion,
                Convert.ToHexString(SHA256.HashData(candidateBytes)),
                deliveryIdentity));
        }
        return output;
    }

    private static async Task<FailureTransitionEvidence> MeasureFailureTransitionAsync(
        string root,
        Workload workload,
        RuntimeSignalCollector signals)
    {
        Directory.CreateDirectory(root);
        var configuration = CreatePositiveConfiguration(workload, "positive-failure");
        var injector = new SqliteRuntimeFaultInjector(
            TransientRuntimeFaultPoint.BeforeIdentityBatchCommit,
            errorCode: 6);
        TransientWorkerAvailability failedAvailability;
        HealthStatus failedHealth;
        using (var failed = CreateProvider(
                   root, configuration, signals, TransientOperatingMode.Edge, faultInjector: injector))
        {
            await StagePositiveFramesAsync(failed, configuration, workload, 7).ConfigureAwait(false);
            var worker = failed.GetRequiredService<TransientWorkerService>();
            for (var index = 0; index < 4; index++)
            {
                Assert.IsTrue(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            }
            Assert.IsFalse(await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false));
            Assert.AreEqual(
                TransientWorkerAvailability.Unhealthy,
                failed.GetRequiredService<TransientWorkerState>().Snapshot.Availability);
            failedAvailability = failed.GetRequiredService<TransientWorkerState>().Snapshot.Availability;
            failedHealth = (await new TransientWorkerHealthCheck(failed.GetRequiredService<TransientWorkerState>())
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false)).Status;
            Assert.AreEqual(HealthStatus.Unhealthy, failedHealth);
        }
        SqliteConnection.ClearAllPools();
        using var recovered = CreateProvider(root, configuration, signals, TransientOperatingMode.Edge);
        var recoveredWorker = recovered.GetRequiredService<TransientWorkerService>();
        await recoveredWorker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitUntilAsync(async () =>
        {
            using var observed = await OpenAsync(root).ConfigureAwait(false);
            return await ScalarLongAsync(observed, "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false) > 0 &&
                await ScalarLongAsync(observed,
                    "SELECT COUNT(*) FROM transient_candidates WHERE phase != 'finalized';").ConfigureAwait(false) == 0 &&
                await ScalarLongAsync(observed,
                    "SELECT COUNT(*) FROM transient_worker_candidates WHERE state != 'completed';").ConfigureAwait(false) == 0;
        }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        await WaitUntilAsync(
            () => recovered.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                TransientWorkerAvailability.Healthy,
            TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var recoveredAvailability = recovered.GetRequiredService<TransientWorkerState>().Snapshot.Availability;
        var recoveredHealth = (await new TransientWorkerHealthCheck(recovered.GetRequiredService<TransientWorkerState>())
            .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false)).Status;
        Assert.AreEqual(HealthStatus.Healthy, recoveredHealth);
        await recoveredWorker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        await recovered.GetRequiredService<SqliteTransientRuntimeStore>()
            .RetireBeforeAsync(configuration.AgentId!, long.MaxValue, CancellationToken.None).ConfigureAwait(false);
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        var workflowPhase = await ScalarStringAsync(
            connection, "SELECT phase FROM transient_candidates;").ConfigureAwait(false);
        Assert.AreEqual("finalized", workflowPhase);
        Assert.AreEqual(0L, await ScalarLongAsync(
            connection, "SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'quarantined';").ConfigureAwait(false));
        var backlog = await recovered.GetRequiredService<ITransientCandidateJournal>()
            .ReadBacklogAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0L, backlog.ActiveCount);
        return new FailureTransitionEvidence(
            "SQLITE_LOCKED",
            failedAvailability.ToString(),
            failedHealth.ToString(),
            recoveredAvailability.ToString(),
            recoveredHealth.ToString(),
            workflowPhase,
            backlog.ActiveCount,
            backlog.HeldSourceBytes);
    }

    private static async Task DrainWorkerAsync(TransientWorkerService worker, int maximumIterations)
    {
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var worked = await worker.ProcessCandidateAsync(CancellationToken.None).ConfigureAwait(false) ||
                await worker.ProcessFrameAsync(CancellationToken.None).ConfigureAwait(false);
            if (!worked)
            {
                return;
            }
        }
        Assert.Fail($"Transient worker did not drain within {maximumIterations} iterations.");
    }

    private static async Task<OffEvidence> MeasureOffModeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var configuration = CreateConfiguration(Workload.W1, "off-agent");
        using var provider = CreateProvider(root, configuration, signals: null, mode: TransientOperatingMode.Off);
        await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var worker = provider.GetRequiredService<TransientWorkerService>();
        await worker.StartAsync(CancellationToken.None).ConfigureAwait(false);
        await WaitUntilAsync(
            () => provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability ==
                TransientWorkerAvailability.Disabled,
            TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        await worker.StopAsync(CancellationToken.None).ConfigureAwait(false);
        using var connection = await OpenAsync(root).ConfigureAwait(false);
        return new OffEvidence(
            await ScalarLongAsync(connection,
                "SELECT COUNT(*) FROM capture_lane_definitions WHERE lane_name = 'transient';").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM transient_capture_work;").ConfigureAwait(false),
            await ScalarLongAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'transient_worker_%';").ConfigureAwait(false),
            provider.GetRequiredService<TransientWorkerState>().Snapshot.Availability.ToString());
    }

    private static async Task<BlockedIsolationEvidence> MeasureBlockedIsolationAsync(string root, Workload workload)
    {
        var trials = new List<BlockedIsolationTrial>();
        for (var trial = 0; trial < TrialCount; trial++)
        {
            var trialRoot = Path.Combine(root, $"trial-{trial + 1}");
            IngressScenario baseline;
            IngressScenario blocked;
            if (trial % 2 == 0)
            {
                baseline = await MeasureIngressScenarioAsync(
                    Path.Combine(trialRoot, "baseline"), workload, TransientOperatingMode.Off).ConfigureAwait(false);
                blocked = await MeasureIngressScenarioAsync(
                    Path.Combine(trialRoot, "blocked"), workload, TransientOperatingMode.Edge).ConfigureAwait(false);
            }
            else
            {
                blocked = await MeasureIngressScenarioAsync(
                    Path.Combine(trialRoot, "blocked"), workload, TransientOperatingMode.Edge).ConfigureAwait(false);
                baseline = await MeasureIngressScenarioAsync(
                    Path.Combine(trialRoot, "baseline"), workload, TransientOperatingMode.Off).ConfigureAwait(false);
            }
            trials.Add(new BlockedIsolationTrial(trial + 1, baseline, blocked));
        }
        return new BlockedIsolationEvidence(
            trials,
            Summarize(trials.Select(static value => value.Baseline.AcceptMedianMilliseconds)),
            Summarize(trials.Select(static value => value.Baseline.AcceptP95Milliseconds)),
            Summarize(trials.Select(static value => value.Baseline.StandardMedianMilliseconds)),
            Summarize(trials.Select(static value => value.Baseline.StandardP95Milliseconds)),
            Summarize(trials.Select(static value => value.Blocked.AcceptMedianMilliseconds)),
            Summarize(trials.Select(static value => value.Blocked.AcceptP95Milliseconds)),
            Summarize(trials.Select(static value => value.Blocked.StandardMedianMilliseconds)),
            Summarize(trials.Select(static value => value.Blocked.StandardP95Milliseconds)),
            BlockedRelativeTolerance,
            BlockedAbsoluteToleranceMilliseconds,
            BlockedHardRatioLimit);
    }

    private static async Task<IngressScenario> MeasureIngressScenarioAsync(
        string root,
        Workload workload,
        TransientOperatingMode mode)
    {
        Directory.CreateDirectory(root);
        var configuration = CreateConfiguration(workload, $"isolation-{mode.ToString().ToUpperInvariant()}");
        using var provider = CreateProvider(root, configuration, signals: null, mode: mode);
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var policy = provider.GetRequiredService<CaptureLanePolicy>();
        var standard = policy.Definitions.Single(static lane => lane.Name == "standard");
        var payload = CreatePayload(workload);
        var accept = new double[WarmupCount + MeasuredCount];
        var acknowledge = new double[WarmupCount + MeasuredCount];
        for (var index = 0; index < accept.Length; index++)
        {
            var submission = CreateSubmission(workload, payload, index);
            var timer = Stopwatch.StartNew();
            Assert.IsNotNull(await ingress.AcceptAsync(configuration, submission, CancellationToken.None).ConfigureAwait(false));
            timer.Stop();
            accept[index] = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var lease = await laneStore.ClaimAsync(
                standard, "issue-63-standard", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            timer.Stop();
            acknowledge[index] = timer.Elapsed.TotalMilliseconds;
        }
        var acceptMeasured = accept.Skip(WarmupCount).Order().ToArray();
        var acknowledgeMeasured = acknowledge.Skip(WarmupCount).Order().ToArray();
        var transient = mode == TransientOperatingMode.Off
            ? null
            : (await laneStore.ReadBacklogsAsync(CancellationToken.None).ConfigureAwait(false))
                .Single(static item => item.Lane == "transient");
        return new IngressScenario(
            acceptMeasured[acceptMeasured.Length / 2],
            acceptMeasured[(int)Math.Ceiling(acceptMeasured.Length * 0.95) - 1],
            acknowledgeMeasured[acknowledgeMeasured.Length / 2],
            acknowledgeMeasured[(int)Math.Ceiling(acknowledgeMeasured.Length * 0.95) - 1],
            transient?.PendingCount ?? 0,
            transient?.PendingBytes ?? 0,
            transient?.OldestPendingUtc);
    }

    private static ServiceProvider CreateProvider(
        string root,
        CameraModuleConfig configuration,
        RuntimeSignalCollector? signals,
        TransientOperatingMode mode = TransientOperatingMode.Edge,
        bool required = false,
        ITransientRuntimeFaultInjector? faultInjector = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["CameraAgent:RawIngressRoot"] = root,
            ["CameraAgent:RawIngressReserveBytes"] = "0",
            ["CameraAgent:TransientDetection:Mode"] = mode.ToString(),
            ["CameraAgent:TransientDetection:Required"] = required.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CameraAgent:TransientDetection:WorkerPollIntervalMilliseconds"] = "100",
            ["CameraAgent:CaptureDistribution:UploadEnabled"] =
                (mode == TransientOperatingMode.Hybrid).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CameraAgent:CaptureDistribution:RequiredMaximumPendingCount"] = "20000",
            ["CameraAgent:CaptureDistribution:RequiredMaximumPendingBytes"] = "200000000000",
            ["CameraAgent:CaptureDistribution:OptionalMaximumPendingBytes"] = "20000000000"
        };
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            if (signals is not null)
            {
                builder.AddProvider(signals);
            }
        });
        services.AddSingleton<ICelestialCatalog>(new InMemoryCelestialCatalog([]));
        services.AddCameraAgentInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        if (faultInjector is not null)
        {
            services.AddSingleton(faultInjector);
            services.AddSingleton<ITransientRuntimeFaultInjector>(faultInjector);
        }
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(configuration);
        return provider;
    }

    private static async Task StageFramesAsync(
        ServiceProvider provider,
        CameraModuleConfig configuration,
        Workload workload,
        int count)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var policy = provider.GetRequiredService<CaptureLanePolicy>();
        var standard = policy.Definitions.Single(static lane => lane.Name == "standard");
        var transient = policy.Definitions.Single(static lane => lane.Name == "transient");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static item => item.Lane == "transient");
        var payload = CreatePayload(workload);
        for (var index = 0; index < count; index++)
        {
            Assert.IsNotNull(await ingress.AcceptAsync(
                configuration, CreateSubmission(workload, payload, index), CancellationToken.None).ConfigureAwait(false));
            var standardLease = await laneStore.ClaimAsync(
                standard, "issue-63-standard", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(standardLease);
            await laneStore.CompleteAsync(standardLease, CancellationToken.None).ConfigureAwait(false);
            var transientLease = await laneStore.ClaimAsync(
                transient, "issue-63-transient", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(transientLease);
            var result = await handler.HandleAsync(transientLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
            await laneStore.CompleteAsync(transientLease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static CaptureLoopSubmission CreateSubmission(Workload workload, byte[] payload, int index)
    {
        var timestamp = Epoch.AddSeconds(index * 5);
        var metadata = new FrameMetadata(
            TimeSpan.FromSeconds(1),
            1,
            double.NaN,
            "Issue63Acceptance",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["blackLevelAdu"] = "0",
                ["whiteLevelAdu"] = ushort.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sensorAdcBitDepth"] = "16"
            });
        var frame = new CameraFrame(
            timestamp,
            workload.Width,
            workload.Height,
            workload.Format,
            payload,
            metadata,
            workload.Width * 2);
        var request = new CaptureRequest(
            timestamp,
            TimeSpan.FromSeconds(5),
            CaptureMode.Still,
            new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
        return new CaptureLoopSubmission(
            request,
            new CaptureResult(
                frame,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null),
                TimeSpan.Zero,
                CaptureMode.Still,
                false),
            timestamp,
            TimeSpan.FromSeconds(1),
            TimeSpan.Zero);
    }

    private static byte[] CreatePayload(Workload workload)
    {
        var payload = new byte[workload.PayloadBytes];
        for (var index = 0; index < payload.Length; index += 2)
        {
            payload[index] = (byte)((index / 2 + Seed) % 3);
        }
        return payload;
    }

    private static CameraModuleConfig CreateConfiguration(Workload workload, string agentId)
        => new(
            new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix"),
            new CameraModuleDescriptor("Issue63Acceptance"),
            new CameraRigConfig(
                new SensorProfile(
                    workload.Id,
                    workload.Width,
                    workload.Height,
                    workload == Workload.W1 ? 5.86 : 2.4,
                    workload == Workload.W1 ? SensorColorMode.Mono : SensorColorMode.Color,
                    workload.Format,
                    workload == Workload.W1 ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw,
                    workload.Width * 2,
                    SensorRecipeVersion: $"issue-63-{workload.Id}-v1"),
                new OpticsProfile(
                    "EquidistantFisheye",
                    workload == Workload.W1 ? 0 : 1.8,
                    workload == Workload.W1 ? 180 : 185,
                    0,
                    LensKind.Fisheye,
                    workload.Width / 2d,
                    workload.Height / 2d,
                    workload.ImageCircleRadius,
                    workload.FocalLengthPixels,
                    workload.FocalLengthPixels,
                    CalibrationVersion: $"issue-63-{workload.Id}-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            CapturePipelineConfig.Empty,
            AgentId: agentId);

    private static CameraModuleConfig CreatePositiveConfiguration(Workload workload, string agentId)
    {
        var scenario = new VirtualTransientScenarioDefinition
        {
            ScenarioId = $"issue-63-positive-{workload.Id}",
            ScenarioVersion = "1",
            Seed = Seed,
            EpochUtc = Epoch,
            TemporalSampleCount = 8,
            SkyTracks =
            [
                new VirtualTransientSkyTrack
                {
                    PrimitiveId = "issue-63-positive-sky-track",
                    Keyframes =
                    [
                        new VirtualTransientSkyKeyframe
                        {
                            OffsetSeconds = 20,
                            AltitudeDegrees = 65,
                            AzimuthDegrees = 270,
                            Magnitude = -10,
                            AngularWidthDegrees = 0.25
                        },
                        new VirtualTransientSkyKeyframe
                        {
                            OffsetSeconds = 21,
                            AltitudeDegrees = 65,
                            AzimuthDegrees = 90,
                            Magnitude = -10,
                            AngularWidthDegrees = 0.25
                        }
                    ]
                }
            ]
        };
        var options = JsonSerializer.SerializeToElement(new
        {
            seed = Seed,
            maximumResults = 1,
            magnitudeZeroElectronsPerSecond = workload == Workload.W1 ? 300d : 18_000d,
            backgroundElectronsPerSecond = 1d,
            psfSigmaPixels = 1d,
            psfRadiusPixels = 4d,
            vignettingStrength = 0.1,
            bias = 0d,
            readNoiseStandardDeviation = 0d,
            shotNoiseEnabled = false,
            darkCurrentElectronsPerSecond = 0d,
            asi174Sensor = new { enabled = workload == Workload.W1, blackLevelAdu = 64d },
            asi178Sensor = new { enabled = workload == Workload.W2, blackLevelContainerAdu = 64d },
            transientScenario = scenario
        });
        return new CameraModuleConfig(
            new ObservatoryLocation(35.347, -113.878, 1000, "America/Phoenix"),
            new CameraModuleDescriptor("VirtualSky", options),
            new CameraRigConfig(
                new SensorProfile(
                    $"Issue63Positive{workload.Id}",
                    workload.Width,
                    workload.Height,
                    workload == Workload.W1 ? 5.86 : 2.4,
                    workload == Workload.W1 ? SensorColorMode.Mono : SensorColorMode.Color,
                    workload.Format,
                    workload == Workload.W1 ? SensorResponseMode.Monochrome : SensorResponseMode.BayerRaw,
                    workload.Width * 2,
                    SensorRecipeVersion: $"issue-63-positive-{workload.Id}-v1"),
                new OpticsProfile(
                    "EquidistantFisheye",
                    workload == Workload.W1 ? 0 : 1.8,
                    workload == Workload.W1 ? 180 : 185,
                    0,
                    LensKind.Fisheye,
                    workload.Width / 2d,
                    workload.Height / 2d,
                    workload.ImageCircleRadius,
                    workload.FocalLengthPixels,
                    workload.FocalLengthPixels,
                    CalibrationVersion: $"issue-63-positive-{workload.Id}-optics-v1"),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            CapturePipelineConfig.Empty,
            AgentId: agentId);
    }

    private static async Task StagePositiveFramesAsync(
        ServiceProvider provider,
        CameraModuleConfig configuration,
        Workload workload,
        int count)
    {
        var ingress = provider.GetRequiredService<IRawCaptureIngress>();
        await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        var module = new VirtualSkyCameraModule(
            TimeProvider.System,
            provider.GetRequiredService<ICelestialCatalog>(),
            new ProjectedSceneStore());
        await module.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
        var laneStore = provider.GetRequiredService<ICaptureLaneStore>();
        var definitions = provider.GetRequiredService<CaptureLanePolicy>().Definitions;
        var transient = definitions.Single(static value => value.Name == "transient");
        var handler = provider.GetServices<ICaptureLaneHandler>().Single(static value => value.Lane == "transient");
        for (var index = 0; index < count; index++)
        {
            var request = new CaptureRequest(
                Epoch.AddSeconds(index * 5),
                TimeSpan.FromSeconds(5),
                CaptureMode.Still,
                new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null));
            var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            var frame = capture.Frame!;
            Assert.AreEqual(workload.PayloadBytes, frame.PixelData.Length);
            var extra = new Dictionary<string, string>(
                frame.Metadata.Extra ?? new Dictionary<string, string>(),
                StringComparer.Ordinal)
            {
                ["blackLevelAdu"] = "0",
                ["whiteLevelAdu"] = ushort.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["sensorAdcBitDepth"] = "16"
            };
            capture = capture with { Frame = frame with { Metadata = frame.Metadata with { Extra = extra } } };
            var submission = new CaptureLoopSubmission(
                request,
                capture,
                request.RequestedStartUtc,
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero);
            Assert.IsNotNull(await ingress.AcceptAsync(
                configuration, submission, CancellationToken.None).ConfigureAwait(false));
            foreach (var lane in definitions.Where(static value => value.Name != "transient"))
            {
                var lease = await laneStore.ClaimAsync(
                    lane, $"issue-63-{lane.Name}", configuration, CancellationToken.None).ConfigureAwait(false);
                if (lease is not null)
                {
                    await laneStore.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                }
            }
            var transientLease = await laneStore.ClaimAsync(
                transient, "issue-63-transient", configuration, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(transientLease);
            var result = await handler.HandleAsync(transientLease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, result.Outcome);
            await laneStore.CompleteAsync(transientLease, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Statements are internal constants and all runtime values are parameterized.")]
    private static async Task InsertMetadataWorkAsync(SqliteConnection connection, string agentId)
    {
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
        var statements = new[]
        {
            """
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 10000)
            INSERT INTO raw_capture_assignments(capture_id, raw_artifact_id, agent_id, capture_sequence)
            SELECT printf('%032x', value), printf('%032x', 100000 + value), $agent, value FROM n;
            """,
            """
            WITH RECURSIVE n(value) AS (SELECT 1 UNION ALL SELECT value + 1 FROM n WHERE value < 10000)
            INSERT INTO raw_captures(
                capture_id, raw_artifact_id, agent_id, capture_sequence, descriptor_sha256,
                manifest_sha256, payload_sha256, payload_length, payload_relative_path,
                sidecar_relative_path, manifest_json, exposure_started_unix_ms,
                durable_ingress_unix_ms, committed_unix_ms, state, retention_hold)
            SELECT printf('%032x', value), printf('%032x', 100000 + value), $agent, value,
                   printf('%064x', value), $sha, $sha, 0,
                   printf('metadata/%d.bin', value), printf('metadata/%d.json', value), X'7B7D',
                   $now + value, $now + value, $now + value, 'committed', 1 FROM n;
            """,
            """
            INSERT INTO capture_lane_work(
                raw_capture_row_id, lane_name, agent_id, capture_sequence, required, ordered,
                state, attempt_count, available_unix_ms, created_unix_ms, updated_unix_ms)
            SELECT raw_capture_row_id, 'transient', agent_id, capture_sequence, 1, 1,
                   'completed', 0, committed_unix_ms, committed_unix_ms, committed_unix_ms
            FROM raw_captures WHERE agent_id = $agent;
            """,
            """
            INSERT INTO transient_capture_work(
                raw_capture_row_id, lane_work_id, mode, required, state, artifact_id,
                manifest_sha256, created_unix_ms, updated_unix_ms)
            SELECT r.raw_capture_row_id, w.work_id, 'edge', 1, 'pending', r.raw_artifact_id,
                   r.manifest_sha256, r.committed_unix_ms, r.committed_unix_ms
            FROM raw_captures r
            JOIN capture_lane_work w ON w.raw_capture_row_id = r.raw_capture_row_id AND w.lane_name = 'transient'
            WHERE r.agent_id = $agent;
            """
        };
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$agent", agentId);
            command.Parameters.AddWithValue("$sha", new string('A', 64));
            command.Parameters.AddWithValue("$now", Epoch.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static TrialSummary SummarizeTrials(IReadOnlyList<WorkerTrial> trials)
        => new(
            Summarize(trials.Select(static item => item.MedianMilliseconds)),
            Summarize(trials.Select(static item => item.P95Milliseconds)),
            Summarize(trials.Select(static item => item.ThroughputPerSecond)),
            Summarize(trials.Select(static item => item.CpuMillisecondsPerOperation)),
            Summarize(trials.Select(static item => item.AllocatedBytesPerOperation)),
            trials.Min(static item => item.PeakRssBytes),
            trials.Max(static item => item.PeakRssBytes),
            trials.Min(static item => item.LohEndBytes),
            trials.Max(static item => item.LohEndBytes));

    private static DistributionSummary Summarize(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new DistributionSummary(sorted[sorted.Length / 2], sorted[0], sorted[^1]);
    }

    private static void AssertRegressionWithinBudget(double baseline, double candidate, string message)
    {
        var toleranceLimit = baseline + Math.Max(
            baseline * BlockedRelativeTolerance,
            BlockedAbsoluteToleranceMilliseconds);
        var hardRatioLimit = baseline * BlockedHardRatioLimit;
        Assert.IsLessThanOrEqualTo(Math.Min(toleranceLimit, hardRatioLimit), candidate, message);
    }

    private static void AssertTelemetryIsBoundedAndPrivate(
        RuntimeSignalEvidence evidence,
        SourceEvidence source,
        IReadOnlyList<PositiveModeEvidence> positive)
    {
        var allowedMetricKeys = new HashSet<string>(["stage", "outcome", "kind"], StringComparer.Ordinal);
        Assert.IsTrue(evidence.MetricTagKeys.All(allowedMetricKeys.Contains));
        Assert.IsLessThanOrEqualTo(24, evidence.TagValues.Count);
        var allowedActivityKeys = new HashSet<string>(["transient.stage", "transient.outcome"], StringComparer.Ordinal);
        Assert.IsTrue(evidence.Activities.All(activity => activity.Tags.Keys.All(allowedActivityKeys.Contains)));
        var allowedLogKeys = new HashSet<string>(
            ["Stage", "Outcome", "Reason", "FailureType", "{OriginalFormat}"],
            StringComparer.Ordinal);
        Assert.IsTrue(evidence.Logs.All(log => log.State.Keys.All(allowedLogKeys.Contains)));
        Assert.IsTrue(evidence.Logs.Any(static log =>
            log.EventId == 2200 && log.Message.Contains("storage-unavailable", StringComparison.Ordinal)));
        var forbidden = positive.SelectMany(static item => item.Trials)
            .SelectMany(static trial => new[]
            {
                trial.CandidateId.ToString("N"),
                trial.CandidateId.ToString(),
                trial.EventId.ToString("N"),
                trial.EventId.ToString(),
                trial.EventVersionId.ToString("N"),
                trial.EventVersionId.ToString(),
                trial.DeliveryIdentitySha256,
                trial.CandidatePayloadSha256
            })
            .Concat(positive.SelectMany(static item => item.Trials)
                .SelectMany(static trial => trial.Identities)
                .SelectMany(static identity => new[]
                {
                    identity.CandidateId.ToString("N"),
                    identity.EventId.ToString("N"),
                    identity.ObservationId.ToString("N"),
                    identity.AssessmentId.ToString("N"),
                    identity.EventVersionId.ToString("N"),
                    identity.DeliveryIdentitySha256,
                    identity.CandidatePayloadSha256
                }))
            .Concat(source.UntrackedFiles.Select(static item => item.Path))
            .ToArray();
        var emitted = evidence.TagValues
            .Concat(evidence.Activities.SelectMany(static item => item.Tags.Keys.Concat(item.Tags.Values)))
            .Concat(evidence.Logs.SelectMany(static item =>
                item.State.Keys.Concat(item.State.Values).Append(item.Message).Append(item.Category)))
            .ToArray();
        Assert.IsFalse(emitted.Any(value =>
            value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal) ||
            value.Contains('\\', StringComparison.Ordinal) ||
            forbidden.Any(secret => secret.Length > 0 && value.Contains(secret, StringComparison.OrdinalIgnoreCase)) ||
            value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("payload", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<string> StateIdentityAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(group_concat(value, '|'), '') FROM (
                SELECT printf('%d:%s:%d:%s', r.capture_sequence, f.state, f.causal_succeeded, COALESCE(f.failure_reason, '')) AS value
                FROM transient_worker_frames f JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id
                ORDER BY r.capture_sequence);
            """;
        var value = Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static async Task<SqliteConnection> OpenAsync(string root)
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        return connection;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Harness callers pass internal constant SQL only.")]
    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Harness callers pass internal constant SQL only.")]
    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static long DatabaseBytes(string root)
        => Directory.EnumerateFiles(Path.Combine(root, "journal"), "raw-ingress.db*", SearchOption.TopDirectoryOnly)
            .Sum(static path => new FileInfo(path).Length);

    private static int CountStoredPayloadCopies(string root)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Count()
            : 0;

    private static ProcessIo ReadProcessIo()
    {
        const string path = "/proc/self/io";
        if (!File.Exists(path))
        {
            return new ProcessIo(0, 0, 0, 0);
        }
        var values = File.ReadAllLines(path)
            .Select(static line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(static parts => parts.Length == 2 && long.TryParse(
                parts[1], System.Globalization.CultureInfo.InvariantCulture, out _))
            .ToDictionary(
                static parts => parts[0],
                static parts => long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
        return new ProcessIo(
            values.GetValueOrDefault("read_bytes"),
            values.GetValueOrDefault("write_bytes"),
            values.GetValueOrDefault("syscr"),
            values.GetValueOrDefault("syscw"));
    }

    private static long Subtract(long end, long start) => Math.Max(0, end - start);

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                Assert.Fail($"Condition was not satisfied within {timeout}.");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (!await condition().ConfigureAwait(false))
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                Assert.Fail($"Condition was not satisfied within {timeout}.");
            }
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static GitEvidence ReadGitEvidence(string root)
        => new(
            ReadGit(root, "rev-parse HEAD"),
            ReadGit(root, "branch --show-current"),
            ReadGit(root, "status --short"));

    private static SourceEvidence ReadSourceEvidence(string root, GitEvidence git)
    {
        var trackedDiff = ReadGitRaw(root, "diff --binary --no-ext-diff HEAD -- .");
        var untracked = ReadGitRaw(root, "ls-files --others --exclude-standard -z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal)
            .Select(path =>
            {
                var content = File.ReadAllBytes(Path.Combine(root, path));
                return new UntrackedSourceEvidence(
                    path.Replace(Path.DirectorySeparatorChar, '/'),
                    content.LongLength,
                    Convert.ToHexString(SHA256.HashData(content)));
            })
            .ToArray();
        var trackedDiffBytes = Encoding.UTF8.GetBytes(trackedDiff);
        var trackedDiffSha256 = Convert.ToHexString(SHA256.HashData(trackedDiffBytes));
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            git.Revision,
            TrackedDiffSha256 = trackedDiffSha256,
            Untracked = untracked
        });
        return new SourceEvidence(
            Convert.ToHexString(SHA256.HashData(canonical)),
            trackedDiffSha256,
            trackedDiffBytes.LongLength,
            untracked);
    }

    private static IReadOnlyList<BinaryEvidence> ReadBinaryEvidence()
        =>
        [
            ReadBinaryEvidence(typeof(TransientWorkerAcceptancePerformanceTests).Assembly),
            ReadBinaryEvidence(typeof(TransientWorkerService).Assembly)
        ];

    private static BinaryEvidence ReadBinaryEvidence(Assembly assembly)
    {
        var path = assembly.Location;
        var content = File.ReadAllBytes(path);
        var embedded = TransientEvidenceProvenance.ReadEmbeddedRepositoryRevision(assembly);
        return new BinaryEvidence(
            assembly.GetName().Name!,
            Path.GetFullPath(path),
            Path.GetFileName(path),
            content.LongLength,
            Convert.ToHexString(SHA256.HashData(content)),
            assembly.ManifestModule.ModuleVersionId,
            embedded.InformationalVersion,
            embedded.RepositoryCommit,
            embedded.EmbeddedRepositoryRevision);
    }

    private static AssemblyRepositoryRevision[] ReadAssemblyRepositoryRevisions(
        IReadOnlyList<BinaryEvidence> binaries)
        => binaries.Select(static binary =>
            new AssemblyRepositoryRevision(binary.Assembly, binary.EmbeddedRepositoryRevision)).ToArray();

    private static void AssertRunInputsUnchanged(
        GitEvidence startGit,
        SourceEvidence startSource,
        IReadOnlyList<BinaryEvidence> startBinaries,
        GitEvidence endGit,
        SourceEvidence endSource,
        IReadOnlyList<BinaryEvidence> endBinaries)
    {
        Assert.AreEqual(
            JsonSerializer.Serialize(startGit),
            JsonSerializer.Serialize(endGit),
            "HEAD, branch, or dirty state changed during measurement.");
        Assert.AreEqual(
            JsonSerializer.Serialize(startSource),
            JsonSerializer.Serialize(endSource),
            "Tracked or untracked source content changed during measurement.");
        Assert.AreEqual(
            JsonSerializer.Serialize(startBinaries),
            JsonSerializer.Serialize(endBinaries),
            "A tested assembly path, SHA-256, or MVID changed during measurement.");
    }

    private static string ReadGit(string root, string arguments)
        => ReadGitRaw(root, arguments).Trim();

    private static string ReadGitRaw(string root, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
        return output;
    }

    private static string ReadSdk(string root)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()!;
    }

    private static async Task<string> ReadSqliteVersionAsync()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync().ConfigureAwait(false);
        return await ScalarStringAsync(connection, "SELECT sqlite_version();").ConfigureAwait(false);
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

    private sealed class RuntimeSignalCollector : ILoggerProvider, IDisposable
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _metricNames = new(StringComparer.Ordinal);
        private readonly HashSet<string> _activityNames = new(StringComparer.Ordinal);
        private readonly HashSet<int> _logEventIds = [];
        private readonly HashSet<string> _tagValues = new(StringComparer.Ordinal);
        private readonly HashSet<string> _metricTagKeys = new(StringComparer.Ordinal);
        private readonly Dictionary<string, double> _metricTotals = new(StringComparer.Ordinal);
        private readonly List<ActivitySignal> _activities = [];
        private readonly List<LogSignal> _logs = [];
        private readonly MeterListener _meterListener;
        private readonly ActivityListener _activityListener;

        internal RuntimeSignalCollector()
        {
            _meterListener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (string.Equals(instrument.Meter.Name, TransientWorkerTelemetry.MeterName, StringComparison.Ordinal))
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => RecordMetric(instrument, value, tags));
            _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => RecordMetric(instrument, value, tags));
            _meterListener.Start();
            _activityListener = new ActivityListener
            {
                ShouldListenTo = source => string.Equals(
                    source.Name, TransientWorkerTelemetry.ActivitySourceName, StringComparison.Ordinal),
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (_gate)
                    {
                        _activityNames.Add(activity.OperationName);
                        foreach (var tag in activity.TagObjects)
                        {
                            if (tag.Value is not null)
                            {
                                _tagValues.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
                            }
                        }
                        _activities.Add(new ActivitySignal(
                            activity.OperationName,
                            activity.Status.ToString(),
                            activity.TagObjects.ToDictionary(
                                static value => value.Key,
                                static value => Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                                StringComparer.Ordinal)));
                    }
                }
            };
            ActivitySource.AddActivityListener(_activityListener);
        }

        public ILogger CreateLogger(string categoryName) => new EvidenceLogger(this, categoryName);

        public void Dispose()
        {
            _meterListener.Dispose();
            _activityListener.Dispose();
        }

        internal void RecordObservableInstruments() => _meterListener.RecordObservableInstruments();

        internal RuntimeSignalEvidence Snapshot()
        {
            lock (_gate)
            {
                return new RuntimeSignalEvidence(
                    _metricNames.Order().ToArray(),
                    _activityNames.Order().ToArray(),
                    _logEventIds.Order().ToArray(),
                    _tagValues.Order().ToArray(),
                    _metricTagKeys.Order().ToArray(),
                    _metricTotals.OrderBy(static pair => pair.Key).ToDictionary(
                        static pair => pair.Key,
                        static pair => pair.Value,
                        StringComparer.Ordinal),
                    _activities.ToArray(),
                    _logs.ToArray());
            }
        }

        internal double MetricTotal(string name)
        {
            lock (_gate)
            {
                return _metricTotals.GetValueOrDefault(name);
            }
        }

        private void RecordMetric(
            Instrument instrument,
            double value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (_gate)
            {
                _metricNames.Add(instrument.Name);
                _metricTotals[instrument.Name] = _metricTotals.GetValueOrDefault(instrument.Name) + value;
                foreach (var tag in tags)
                {
                    _metricTagKeys.Add(tag.Key);
                    if (tag.Value is not null)
                    {
                        _tagValues.Add(Convert.ToString(tag.Value, System.Globalization.CultureInfo.InvariantCulture)!);
                    }
                }
            }
        }

        private sealed class EvidenceLogger(RuntimeSignalCollector owner, string categoryName) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (eventId.Id is not (2200 or 2201))
                {
                    return;
                }
                lock (owner._gate)
                {
                    owner._logEventIds.Add(eventId.Id);
                    var structured = state is IEnumerable<KeyValuePair<string, object?>> values
                        ? values.ToDictionary(
                            static value => value.Key,
                            static value => Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                            StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal);
                    owner._logs.Add(new LogSignal(
                        categoryName,
                        logLevel.ToString(),
                        eventId.Id,
                        formatter(state, exception),
                        exception?.GetType().Name,
                        structured));
                }
            }
        }
    }

    private sealed record Workload(
        string Id,
        int Width,
        int Height,
        CameraPixelFormat Format,
        int PayloadBytes,
        double ImageCircleRadius,
        double FocalLengthPixels)
    {
        internal static Workload W1 { get; } = new("W1", 1936, 1216, CameraPixelFormat.Mono16, 4_708_352, 590, 616.2477);
        internal static Workload W2 { get; } = new("W2", 3096, 2080, CameraPixelFormat.BayerRggb16, 12_879_360, 1040, 1081.4605);
    }

    private sealed record WorkerTrial(
        double MedianMilliseconds,
        double P95Milliseconds,
        double ThroughputPerSecond,
        double CpuMilliseconds,
        double CpuMillisecondsPerOperation,
        double AllocatedBytesPerOperation,
        long RssStartBytes,
        long PeakRssBytes,
        long RssEndBytes,
        long LohStartBytes,
        long LohEndBytes,
        long ProcessReadBytes,
        long ProcessWriteBytes,
        long ProcessReadOperations,
        long ProcessWriteOperations,
        long SqliteBytes,
        long EvidenceReadBytes,
        long EvidenceFilesRead,
        double PayloadReloadsPerOperation,
        long CandidateCount,
        double CandidateRatePerFrame,
        long FinalBacklogCount,
        long FinalBacklogBytes,
        string RawPayloadSha256,
        string DurableStateSha256,
        string HealthStatus);

    private sealed record SteadyWorkloadEvidence(
        string Workload,
        IReadOnlyList<WorkerTrial> Trials,
        TrialSummary Summary);

    private sealed record TrialSummary(
        DistributionSummary MedianMilliseconds,
        DistributionSummary P95Milliseconds,
        DistributionSummary ThroughputPerSecond,
        DistributionSummary CpuMillisecondsPerOperation,
        DistributionSummary AllocatedBytesPerOperation,
        long MinimumPeakRssBytes,
        long MaximumPeakRssBytes,
        long MinimumLohEndBytes,
        long MaximumLohEndBytes);

    private sealed record DistributionSummary(double Median, double Minimum, double Maximum);

    private sealed record W3MetadataEvidence(
        int Records,
        double InsertMilliseconds,
        double InsertRecordsPerSecond,
        double RecoveryMilliseconds,
        double RecoveryRecordsPerSecond,
        long SqliteBytes,
        long BacklogCount,
        long BacklogBytes,
        DateTimeOffset? OldestCreatedUtc,
        int PayloadCopies);

    private sealed record W3PayloadEvidence(
        int Captures,
        long RawBytes,
        long InitialBacklogCount,
        long InitialBacklogBytes,
        DateTimeOffset? InitialOldestUtc,
        long FinalBacklogCount,
        long FinalBacklogBytes,
        double DrainMilliseconds,
        double DrainCapturesPerSecond,
        double MedianMilliseconds,
        double P95Milliseconds,
        double CpuMilliseconds,
        long AllocatedBytes,
        long RssStartBytes,
        long PeakRssBytes,
        long RssEndBytes,
        long LohEndBytes,
        long ProcessReadBytes,
        long ProcessWriteBytes,
        long EvidenceReadBytes,
        long EvidenceFilesRead,
        long DatabaseBytes,
        long RetainedFilesystemBytes,
        double StoredPayloadCopiesPerCapture,
        double PayloadReloadsPerCapture,
        string RawPayloadSha256,
        string DurableStateSha256);

    private sealed record OffEvidence(
        long TransientLaneDefinitions,
        long TransientLaneRows,
        long TransientWorkerTables,
        string WorkerAvailability);

    private sealed record PositiveModeEvidence(
        string Mode,
        string Workload,
        IReadOnlyList<PositiveModeTrial> Trials,
        DistributionSummary ElapsedMilliseconds,
        DistributionSummary EvidenceLoads,
        DistributionSummary EvidenceReadBytes);

    private sealed record PositiveModeTrial(
        int Trial,
        double ElapsedMilliseconds,
        long CandidateCount,
        IReadOnlyList<PositiveIdentityEvidence> Identities,
        Guid CandidateId,
        Guid EventId,
        Guid EventVersionId,
        int EventVersion,
        string WorkflowPhase,
        string CandidatePayloadSha256,
        string DeliveryIdentitySha256,
        string RawPayloadSha256,
        string DurableStateSha256,
        string CanonicalOutcomeSha256,
        double EvidenceLoads,
        long EvidenceReadBytes,
        long EvidenceFilesRead,
        int StoredPayloadCopies,
        string HealthStatus);

    private sealed record PositiveIdentityEvidence(
        Guid CandidateId,
        Guid EventId,
        Guid ObservationId,
        Guid AssessmentId,
        Guid EventVersionId,
        int EventVersion,
        string CandidatePayloadSha256,
        string DeliveryIdentitySha256);

    private sealed record FailureTransitionEvidence(
        string Failure,
        string FailedAvailability,
        string FailedHealth,
        string RecoveredAvailability,
        string RecoveredHealth,
        string WorkflowPhase,
        long FinalBacklogCount,
        long FinalBacklogBytes);

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Instances are returned from the asynchronous scenario measurement helper.")]
    private sealed record IngressScenario(
        double AcceptMedianMilliseconds,
        double AcceptP95Milliseconds,
        double StandardMedianMilliseconds,
        double StandardP95Milliseconds,
        long TransientBacklogCount,
        long TransientBacklogBytes,
        DateTimeOffset? TransientOldestUtc);

    private sealed record BlockedIsolationEvidence(
        IReadOnlyList<BlockedIsolationTrial> Trials,
        DistributionSummary BaselineAcceptMedianMilliseconds,
        DistributionSummary BaselineAcceptP95Milliseconds,
        DistributionSummary BaselineStandardMedianMilliseconds,
        DistributionSummary BaselineStandardP95Milliseconds,
        DistributionSummary BlockedAcceptMedianMilliseconds,
        DistributionSummary BlockedAcceptP95Milliseconds,
        DistributionSummary BlockedStandardMedianMilliseconds,
        DistributionSummary BlockedStandardP95Milliseconds,
        double RelativeTolerance,
        double AbsoluteToleranceMilliseconds,
        double HardRatioLimit);

    private sealed record BlockedIsolationTrial(int Trial, IngressScenario Baseline, IngressScenario Blocked);

    private sealed record RuntimeSignalEvidence(
        IReadOnlyList<string> MetricNames,
        IReadOnlyList<string> ActivityNames,
        IReadOnlyList<int> LogEventIds,
        IReadOnlyList<string> TagValues,
        IReadOnlyList<string> MetricTagKeys,
        IReadOnlyDictionary<string, double> MetricTotals,
        IReadOnlyList<ActivitySignal> Activities,
        IReadOnlyList<LogSignal> Logs);

    private sealed record ActivitySignal(
        string Name,
        string Status,
        IReadOnlyDictionary<string, string> Tags);

    private sealed record LogSignal(
        string Category,
        string Level,
        int EventId,
        string Message,
        string? ExceptionType,
        IReadOnlyDictionary<string, string> State);

    private sealed record ProcessIo(long ReadBytes, long WriteBytes, long ReadOperations, long WriteOperations);

    private sealed record GitEvidence(string Revision, string Branch, string DirtyState);

    private sealed record SourceEvidence(
        string CanonicalFingerprintSha256,
        string TrackedDiffSha256,
        long TrackedDiffBytes,
        IReadOnlyList<UntrackedSourceEvidence> UntrackedFiles);

    private sealed record UntrackedSourceEvidence(string Path, long Bytes, string ContentSha256);

    private sealed record BinaryEvidence(
        string Assembly,
        string Path,
        string FileName,
        long Bytes,
        string Sha256,
        Guid Mvid,
        string? InformationalVersion,
        string? RepositoryCommit,
        string EmbeddedRepositoryRevision);

    private sealed class SqliteRuntimeFaultInjector(
        TransientRuntimeFaultPoint point,
        int errorCode) : ITransientRuntimeFaultInjector
    {
        private int _thrown;

        public void Inject(TransientRuntimeFaultPoint current)
        {
            if (current == point && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new SqliteException("Injected transient SQLite storage failure.", errorCode, errorCode);
            }
        }
    }
}
