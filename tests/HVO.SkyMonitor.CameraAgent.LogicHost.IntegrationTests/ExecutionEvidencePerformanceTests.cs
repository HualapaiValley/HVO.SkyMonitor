using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.IntegrationTests;

/// <summary>Issue-specific metadata and W1-size interference acceptance. This does not claim W4 or W6 qualification.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ExecutionEvidencePerformanceTests
{
    private const int HistoryCount = 10_000;
    private const int Warmup = 5;
    private const int Measured = 30;
    private const long RawBytes = 4_708_352;
    private const string RepeatCommand = "dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~ExecutionEvidencePerformanceTests.Issue538_MetadataAndLiveInterferenceAcceptance";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static CameraAgentIntegrationFixture Fixture => AssemblyHooks.Fixture;
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue538_MetadataAndLiveInterferenceAcceptance()
    {
        Assert.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", AppContext.BaseDirectory,
            StringComparison.Ordinal, "Performance acceptance requires Release binaries.");
        var repository = ExecutionEvidencePerformanceCounters.RepositoryRoot();
        var source = ExecutionEvidencePerformanceCounters.Source(repository);
        var destination = Path.Combine(repository, "tests", "HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests",
            "TestResults", "issue-538", source.Sha, source.Dirty ? source.DirtyFingerprintSha256 : "clean");
        Directory.CreateDirectory(destination);
        using var counters = new ExecutionEvidencePerformanceCounters(Fixture.HostFixture.SqlServerConnectionString);
        // The idle bracket must precede the large metadata histories and their real reconciliation work.
        var interference = await MeasureInterferenceAsync(counters, destination, source).ConfigureAwait(false);
        var comparisons = Compare(interference.Phases);
        var metadata = await MeasureMetadataAsync(counters).ConfigureAwait(false);
        await SaveIntermediateAsync(destination, "metadata-initial-baseline", source, metadata).ConfigureAwait(false);
        var concurrency = await MeasureMetadataConcurrencyAsync(counters).ConfigureAwait(false);
        await SaveIntermediateAsync(destination, "metadata-concurrency", source, concurrency).ConfigureAwait(false);
        var evidence = new
        {
            Schema = "hvo-issue538-execution-evidence-acceptance-v1",
            Source = source,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            RepeatCommand,
            Environment = new
            {
                Os = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                Sdk = ExecutionEvidencePerformanceCounters.Run("dotnet", repository, "--version"),
                Cpu = (await File.ReadAllLinesAsync("/proc/cpuinfo").ConfigureAwait(false))
                    .FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal)),
                LogicalProcessors = Environment.ProcessorCount,
                MemoryLimitBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                SqlImage = IntegrationTestFixture.SqlServerImage,
                SqlVersion = await SqlVersionAsync().ConfigureAwait(false),
                Docker = ExecutionEvidencePerformanceCounters.Run("docker", repository, "version", "--format", "{{.Server.Version}}"),
                Configuration = "Release",
                ServerGc = System.Runtime.GCSettings.IsServerGC,
                FixtureSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "cameraagent.integration.json")).ConfigureAwait(false))),
                CatalogSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                    Path.Combine(AppContext.BaseDirectory, "Fixtures", "hyg-v42-bright-stars.sqlite")).ConfigureAwait(false)))
            },
            Workload = new
            {
                StageOrder = "I (idle-before/active/denied/idle-after) on the fresh fixture, then M, then C",
                E538M = "W3M: 10,000 one-artifact availability observations derived from a real exported execution; 64-unit batches, 100-row keyset pages.",
                E538C = "Metadata requests at concurrency 1/4/8, 20 warmup and 200 measured logical requests per level; 50% duplicate attempts. This is not payload W4.",
                E538I = new
                {
                    Width = 1936,
                    Height = 1216,
                    Format = "Mono16",
                    Bytes = RawBytes,
                    Seed = 2025,
                    MaximumCatalogResults = 9,
                    ExposureMilliseconds = 10,
                    CadenceMilliseconds = 500,
                    Warmup,
                    Measured,
                    Phases = new[] { "idle-before", "active", "denied", "idle-after" },
                    CentralRecipe = BuiltInProcessingRecipes.EncodedPreview,
                    PhaseDeadlineSeconds = 180,
                    MaximumRawUploadPendingRecords = 256,
                    MaximumHeldUploadBytes = 2L * 1024 * 1024 * 1024,
                    MaximumPendingCentralJobs = 128,
                    CaptureMeasurement = "Module start through complete ingress commit; full ingress histogram is isolated by Meter instance and matched to durable capture commit ordinal/time. Telemetry LoopDuration is retained only as module-side evidence.",
                    Boundaries = "Pause admission and converge warmup uploads/jobs before resuming measured captures. Exact raw capture IDs, consecutive sequences, SHA-256 and job/output tuples identify each measured set.",
                    Active = "One actual exporter cycle and 64 new metadata observations per iteration, then 100 ms delay; one sender request at a time.",
                    Denied = "HTTP 503 evidence-only sink for the complete phase; actual capture/upload/central job execution remains live.",
                    Scope = "W1 full raw byte geometry with the existing small catalog and short fixture cadence; no physical camera, WAN, W4 or W6 claim."
                },
                Regression = "Idle p95 brackets differing >25% are inconclusive. Otherwise >max(25%, bracket drift) is a blocking material latency regression. Throughput below the reciprocal bound also blocks."
            },
            Metadata = metadata,
            MetadataConcurrency = concurrency,
            Interference = interference,
            Comparisons = comparisons,
            Limitations = new
            {
                Process = "CPU, sampled RSS, allocations and /proc/self/io include both in-process hosts, shared fixture capture and harness; they exclude SQL Server.",
                Sql = "SQL container cgroup CPU/RSS/I/O measured separately; SqlClient diagnostics count this database's command starts, including background work. No SQL logical-page or wire-byte claim.",
                Http = "TestServer entity bytes, not physical network traffic.",
                Io = "OS I/O counters and SQLite file footprints are distinct; physical media, per-transaction fsync latency and SQL logical reads are not inferred.",
                Baseline = "Importer has no predecessor; metadata measurements are its initial absolute baseline. Interference compares evidence off/on on this same candidate.",
                Trials = "One bracketed four-phase run with thirty distinct measured operations per phase; p95 is per operation. This is not a five-independent-trial confidence interval."
            },
            Outcome = comparisons.All(item => item.Outcome == "Pass") ? "Pass" : "Investigate"
        };
        var path = Path.Combine(destination, "execution-evidence-performance.json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(evidence, Json)).ConfigureAwait(false);
        File.Move(path + ".tmp", path, overwrite: true);
        TestContext.WriteLine($"Issue #538 evidence: {path}");
        Assert.IsTrue(comparisons.All(item => item.Outcome == "Pass"),
            "Interference failed or is inconclusive; inspect the retained comparisons before accepting this candidate.");
    }

    private static async Task SaveIntermediateAsync(string directory, string name, SourceEvidence source, object value)
    {
        var path = Path.Combine(directory, name + ".json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(new
        {
            Schema = "hvo-issue538-partial-performance-stage-v1",
            Source = source,
            RepeatCommand,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CompletedStage = name,
            Evidence = value,
            OverallAcceptance = "Pending remaining stages and interference comparisons"
        }, Json)).ConfigureAwait(false);
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private static async Task<object> MeasureMetadataAsync(ExecutionEvidencePerformanceCounters counters)
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        var sourceRows = edge.ReadLocalRows().Count;
        var enlistStart = Stopwatch.GetTimestamp();
        await edge.EnlistAvailabilityHistoryAsync(HistoryCount, "w3m").ConfigureAwait(false);
        var enlistMs = Stopwatch.GetElapsedTime(enlistStart).TotalMilliseconds;
        var initial = await edge.Outbox.ReadBacklogAsync(edge.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(HistoryCount, initial.PendingCount);
        var offset = edge.Http.Exchanges.Count;
        var processBefore = counters.BeginMeasurement();
        var sqlBefore = await SqlContainerAsync().ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        await edge.DrainAsync().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var processAfter = counters.Snapshot();
        var sqlAfter = await SqlContainerAsync().ConfigureAwait(false);
        var requests = edge.Http.Exchanges.Skip(offset).Where(item => item.Route == "/api/device/execution-evidence").ToArray();
        var pages = new List<double>();
        var ids = new HashSet<long>();
        long cursor = 0;
        while (true)
        {
            using var scope = Fixture.CreateHostScope();
            started = Stopwatch.GetTimestamp();
            var page = await scope.ServiceProvider.GetRequiredService<ExecutionEvidenceService>().ListUnitsAsync(
                central.Binding.Id, central.OwnerUserId, edge.Agent.ObservatoryId, cursor, 100).ConfigureAwait(false);
            pages.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            Assert.IsNotNull(page);
            Assert.IsLessThanOrEqualTo(100, page.Count);
            if (page.Count == 0) { break; }
            Assert.IsTrue(page.All(item => item.Id > cursor && item.Accepted));
            foreach (var row in page) { Assert.IsTrue(ids.Add(row.Id), "Keyset paging returned a duplicate."); }
            cursor = page[^1].Id;
        }
        Assert.AreEqual(sourceRows + HistoryCount, ids.Count);
        var rows = edge.ReadLocalRows();
        await central.AssertPersistedAsync(rows).ConfigureAwait(false);
        ExecutionEvidenceCombinedTests.AssertExactAcknowledgements(edge, rows);
        var final = await edge.Outbox.ReadBacklogAsync(edge.Root, CancellationToken.None).ConfigureAwait(false);
        return new
        {
            Rows = HistoryCount,
            SourceRows = sourceRows,
            EnlistMilliseconds = enlistMs,
            SubmissionLatency = LatencyEvidence.From(requests.Select(item => item.Milliseconds)),
            KeysetPageLatency = LatencyEvidence.From(pages),
            PageCount = pages.Count,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            UnitsPerSecond = HistoryCount / elapsed.TotalSeconds,
            RequestEntityBytes = requests.Sum(item => (long)item.Request.Length),
            ResponseEntityBytes = requests.Sum(item => (long)item.Response.Length),
            Requests = requests.Length,
            MaximumUnitsPerRequest = requests.Max(item => ExecutionEvidenceBatchCodec.Decode(item.Request, 64).Count),
            edge.Http.MaximumInFlight,
            InitialBacklog = initial,
            FinalBacklog = final,
            Process = processBefore.Delta(processAfter),
            SqlContainerBefore = sqlBefore,
            SqlContainerAfter = sqlAfter,
            CanonicalPayloadSetSha256 = HashSet(rows),
            SqliteFootprintBytes = SqliteBytes(edge.Root)
        };
    }

    private static async Task<IReadOnlyList<object>> MeasureMetadataConcurrencyAsync(ExecutionEvidencePerformanceCounters counters)
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture).ConfigureAwait(false);
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        var evidence = new List<object>();
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            var beforeId = edge.ReadLocalRows().Max(row => row.Id);
            await edge.EnlistAvailabilityHistoryAsync(110, $"concurrency-{concurrency}").ConfigureAwait(false);
            var rows = edge.ReadLocalRows().Where(row => row.Id > beforeId).ToArray();
            await SubmitAtConcurrencyAsync(rows.Take(10).SelectMany(row => new[] { row, row }).ToArray(), concurrency, edge)
                .ConfigureAwait(false);
            var offset = edge.Http.Exchanges.Count;
            var processBefore = counters.BeginMeasurement();
            var sqlBefore = await SqlContainerAsync().ConfigureAwait(false);
            var started = Stopwatch.GetTimestamp();
            var latencies = await SubmitAtConcurrencyAsync(rows.Skip(10).SelectMany(row => new[] { row, row }).ToArray(),
                concurrency, edge).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started);
            var processAfter = counters.Snapshot();
            var sqlAfter = await SqlContainerAsync().ConfigureAwait(false);
            var exchanges = edge.Http.Exchanges.Skip(offset).ToArray();
            Assert.HasCount(200, latencies);
            evidence.Add(new
            {
                Concurrency = concurrency,
                WarmupRequests = 20,
                MeasuredRequests = 200,
                Latency = LatencyEvidence.From(latencies),
                LogicalRequestsPerSecond = 200 / elapsed.TotalSeconds,
                PhysicalRequests = exchanges.Length,
                PressureRetries = exchanges.Count(item => item.Status == HttpStatusCode.ServiceUnavailable),
                RequestEntityBytes = exchanges.Sum(item => (long)item.Request.Length),
                ResponseEntityBytes = exchanges.Sum(item => (long)item.Response.Length),
                Process = processBefore.Delta(processAfter),
                SqlContainerBefore = sqlBefore,
                SqlContainerAfter = sqlAfter
            });
            await edge.DrainAsync().ConfigureAwait(false); // Real exporter settles the direct-request receipts by duplicate replay.
        }
        await central.AssertPersistedAsync(edge.ReadLocalRows()).ConfigureAwait(false);
        return evidence;
    }

    private static async Task<double[]> SubmitAtConcurrencyAsync(EdgeEvidenceRow[] rows, int concurrency,
        ExecutionEvidenceCombinedHarness edge)
    {
        var durations = new ConcurrentBag<double>();
        await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (row, token) =>
        {
            var started = Stopwatch.GetTimestamp();
            while (true)
            {
                var result = await edge.Transport.SubmitAsync(row.Envelope.Origin.IdentitySha256, [row.Bytes], token)
                    .ConfigureAwait(false);
                Assert.IsTrue(result.Disposition is ExecutionEvidenceTransportDisposition.Completed or ExecutionEvidenceTransportDisposition.Retry,
                    result.ReasonCode);
                if (result.Feedback?.Facts.Any(fact => fact.Kind == ExecutionEvidenceFactKind.Acknowledged &&
                    fact.PayloadSha256 == row.Envelope.PayloadSha256 && fact.EvidenceId == row.Envelope.EvidenceId) == true)
                {
                    durations.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return;
                }
                Assert.IsFalse(result.Feedback?.Facts.Any(fact => fact.Kind == ExecutionEvidenceFactKind.Rejected) == true);
                Assert.IsLessThan(TimeSpan.FromSeconds(60), Stopwatch.GetElapsedTime(started), "Submission retry budget exhausted.");
                await Task.Delay(25, token).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        return durations.ToArray();
    }

    private static async Task<InterferenceEvidence> MeasureInterferenceAsync(ExecutionEvidencePerformanceCounters counters,
        string destination, SourceEvidence source)
    {
        await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture, w1Geometry: true).ConfigureAwait(false);
        var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
        await edge.WaitForRealExecutionAsync().ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        var phases = new List<InterferencePhase>();
        foreach (var name in new[] { "idle-before", "active", "denied", "idle-after" })
        {
            using var stop = new CancellationTokenSource();
            edge.Http.Fault = name == "denied" ? EvidenceHttpFault.Unavailable : EvidenceHttpFault.None;
            var load = name is "active" or "denied" ? RunEvidenceLoadAsync(edge, name, stop.Token) : Task.CompletedTask;
            try
            {
                var phase = await MeasureLivePhaseAsync(name, edge, central, counters).ConfigureAwait(false);
                phases.Add(phase);
                await SaveIntermediateAsync(destination, "interference-" + name, source, phase).ConfigureAwait(false);
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                await load.ConfigureAwait(false);
            }
            edge.Http.Fault = EvidenceHttpFault.None;
            await edge.DrainAsync().ConfigureAwait(false); // Recovery is excluded from the next idle measurement.
        }
        await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
        using var convergenceDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var converged = await central.ConvergeUploadsAndJobsAsync(convergenceDeadline.Token).ConfigureAwait(false);
        await edge.DrainAsync().ConfigureAwait(false);
        await central.AssertPersistedAsync(edge.ReadLocalRows()).ConfigureAwait(false);
        return new(phases, converged);
    }

    private static async Task RunEvidenceLoadAsync(ExecutionEvidenceCombinedHarness edge, string phase, CancellationToken token)
    {
        var sequence = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (phase == "active")
                {
                    await edge.EnlistAvailabilityHistoryAsync(64, $"active-{sequence++}").ConfigureAwait(false);
                }
                await edge.RunCycleAsync(token).ConfigureAwait(false);
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task<InterferencePhase> MeasureLivePhaseAsync(string name, ExecutionEvidenceCombinedHarness edge,
        ExecutionEvidenceCentralWorkload central, ExecutionEvidencePerformanceCounters counters)
    {
        var phaseStart = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var healthBefore = await central.ReadHealthAsync(deadline.Token).ConfigureAwait(false);
        var healthSamples = new List<EdgeHealthEvidence>();
        var warmup = new List<Guid>();
        while (warmup.Count < Warmup)
        {
            var raw = await central.UploadNextRawAsync(deadline.Token).ConfigureAwait(false);
            _ = await central.ExecutePreviewAsync(raw, deadline.Token).ConfigureAwait(false);
            var health = await central.ReadHealthAsync(deadline.Token).ConfigureAwait(false);
            ExecutionEvidenceCentralWorkload.AssertHealthy(healthBefore, health);
            if (raw.CapturedAtUtc >= phaseStart) { warmup.Add(raw.CaptureId); }
        }
        // Delimit acquisition as well as upload: no raw acquired during warmup can enter the measured set.
        await edge.PauseCaptureAndWaitForTerminalAsync(deadline.Token).ConfigureAwait(false);
        _ = await central.ConvergeUploadsAndJobsAsync(deadline.Token).ConfigureAwait(false);
        var rows = new List<InterferenceOperation>();
        var telemetry = edge.Agent.Services.GetRequiredService<ICaptureTelemetryProvider>();
        var ingress = edge.Agent.Services.GetRequiredService<RawIngressTelemetry>();
        var ingressBefore = Ingress(ingress);
        var statusBefore = await central.ReadStatusAsync(deadline.Token).ConfigureAwait(false);
        var sqlBefore = await SqlContainerAsync().ConfigureAwait(false);
        var processBefore = counters.BeginMeasurement();
        var exchangeOffset = edge.Http.Exchanges.Count;
        var started = Stopwatch.GetTimestamp();
        var measuredAdmissionUtc = DateTimeOffset.UtcNow;
        await edge.ResumeCaptureAsync(deadline.Token).ConfigureAwait(false);
        DateTimeOffset? previousCaptureStart = null;
        while (rows.Count < Measured)
        {
            var raw = await central.UploadNextRawAsync(deadline.Token).ConfigureAwait(false);
            var job = await central.ExecutePreviewAsync(raw, deadline.Token).ConfigureAwait(false);
            Assert.AreEqual(RawBytes, raw.Bytes);
            Assert.IsGreaterThanOrEqualTo(measuredAdmissionUtc, raw.ModuleCallStartedUtc);
            CaptureTelemetrySample? sample = null;
            await ExecutionEvidenceCombinedHarness.WaitUntilAsync(() =>
            {
                sample = telemetry.GetSnapshot().Samples.SingleOrDefault(item => item.StartedUtc == raw.ModuleCallStartedUtc);
                return Task.FromResult(sample is not null);
            }, "capture telemetry corresponding to the uploaded raw", cancellationToken: deadline.Token).ConfigureAwait(false);
            Assert.IsNotNull(sample);
            Assert.IsTrue(sample.FrameStored);
            var commits = edge.Metrics.Commits;
            Assert.IsGreaterThanOrEqualTo(raw.RawCommitOrdinal, commits.Count);
            var commit = commits[raw.RawCommitOrdinal - 1];
            Assert.IsLessThan(1000, Math.Abs((commit.CompletedAtUtc - raw.JournalCommittedAtUtc).TotalMilliseconds),
                "The isolated ingress histogram sample must match this durable commit ordinal and timestamp.");
            double? startGap = previousCaptureStart is { } prior ? (raw.ModuleCallStartedUtc - prior).TotalMilliseconds : null;
            previousCaptureStart = raw.ModuleCallStartedUtc;
            rows.Add(new(raw, job, sample.StartedUtc, sample.LoopDuration.TotalMilliseconds,
                sample.ProcessingLatency.TotalMilliseconds, sample.ProcessingSteps.Sum(step => step.Duration.TotalMilliseconds),
                (sample.StartedUtc - raw.CapturedAtUtc).TotalMilliseconds, commit.Milliseconds,
                (commit.CompletedAtUtc - raw.ModuleCallStartedUtc).TotalMilliseconds, startGap));
            var health = await central.ReadHealthAsync(deadline.Token).ConfigureAwait(false);
            ExecutionEvidenceCentralWorkload.AssertHealthy(healthBefore, health);
            healthSamples.Add(health);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var processAfter = counters.Snapshot();
        var sqlAfter = await SqlContainerAsync().ConfigureAwait(false);
        var statusAfter = await central.ReadStatusAsync(deadline.Token).ConfigureAwait(false);
        Assert.HasCount(Measured, rows.Select(row => row.Raw.CaptureId).Distinct().ToArray());
        Assert.HasCount(Measured, rows.Select(row => row.CaptureStartedUtc).Distinct().ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, Measured).Select(index => rows[0].Raw.CaptureSequence + index).ToArray(),
            rows.Select(row => row.Raw.CaptureSequence).ToArray(), "The measured raw sequence cannot hide a lost capture.");
        Assert.IsFalse(rows.Any(row => warmup.Contains(row.Raw.CaptureId)));
        var backlog = await edge.Outbox.ReadBacklogAsync(edge.Root, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, backlog.QuarantinedCount);
        Assert.AreEqual(0, backlog.ProjectionRejectedEvents);
        Assert.IsLessThan(50_000, backlog.PendingCount + backlog.RetryCount);
        var exchanges = edge.Http.Exchanges.Skip(exchangeOffset).ToArray();
        if (name == "active")
        {
            Assert.IsGreaterThan(0, exchanges.Count(item => item.Route == "/api/device/execution-evidence" && item.Status == HttpStatusCode.OK));
        }
        if (name == "denied")
        {
            Assert.IsGreaterThan(0, exchanges.Count(item => item.Status == HttpStatusCode.ServiceUnavailable));
            Assert.IsGreaterThan(0, backlog.PendingCount + backlog.RetryCount);
        }
        return new(name, warmup, rows, elapsed.TotalMilliseconds, Measured / elapsed.TotalSeconds,
            LatencyEvidence.From(rows.Select(row => row.CaptureToIngressMilliseconds)),
            LatencyEvidence.From(rows.Select(row => row.RawIngressCommitMilliseconds)),
            LatencyEvidence.From(rows.Select(row => row.Raw.UploadMilliseconds)),
            LatencyEvidence.From(rows.Select(row => row.Job.ClaimAndExecuteMilliseconds)),
            processBefore.Delta(processAfter), sqlBefore, sqlAfter, ingressBefore, Ingress(ingress), backlog,
            healthBefore, healthSamples, statusBefore, statusAfter,
            exchanges.Sum(item => (long)item.Request.Length), exchanges.Sum(item => (long)item.Response.Length),
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Combine(edge.Root, "cameraagent.integration.json")).ConfigureAwait(false))));
    }

    private static List<RegressionComparison> Compare(IReadOnlyList<InterferencePhase> phases)
    {
        var first = phases[0];
        var last = phases[^1];
        var result = new List<RegressionComparison>();
        foreach (var metric in new[] { "capture-to-ingress-p95", "raw-ingress-p95", "upload-p95", "central-processing-p95", "throughput" })
        {
            double Value(InterferencePhase phase) => metric switch
            {
                "capture-to-ingress-p95" => phase.CaptureLatency.P95,
                "raw-ingress-p95" => phase.RawIngressLatency.P95,
                "upload-p95" => phase.UploadLatency.P95,
                "central-processing-p95" => phase.CentralLatency.P95,
                _ => phase.OperationsPerSecond
            };
            var baseline = (Value(first) + Value(last)) / 2;
            var drift = Math.Abs(Value(first) - Value(last)) / Math.Max(.0001, Math.Min(Value(first), Value(last)));
            foreach (var phase in phases.Skip(1).Take(2))
            {
                var ratio = metric == "throughput" ? baseline / Value(phase) : Value(phase) / baseline;
                result.Add(new(metric, phase.Name, baseline, Value(phase), drift, .25,
                    drift > .25 ? "Inconclusive-idle-drift" : ratio > 1 + Math.Max(.25, drift) ? "Regression" : "Pass"));
            }
        }
        return result;
    }

    private static object Ingress(RawIngressTelemetry value) => new
    {
        value.TransactionCount,
        value.TransactionFailureCount,
        value.FileFlushCount,
        value.DirectorySyncCount,
        value.CheckpointCount,
        SqliteLockWaitMilliseconds = value.TotalLockWait.TotalMilliseconds
    };

    private static string HashSet(IReadOnlyList<EdgeEvidenceRow> rows)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var row in rows.OrderBy(row => row.Id)) { hash.AppendData(row.Bytes); }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static long SqliteBytes(string root) => Directory.EnumerateFiles(root, "*.db*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    private static async Task<string> SqlVersionAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(Fixture.HostFixture.SqlServerConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @@VERSION";
        return (string)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    private static async Task<SqlContainerSnapshot> SqlContainerAsync()
    {
        var container = Fixture.HostFixture.GetDependencyContainer(IntegrationDependency.SqlServer);
        async Task<string> Read(string path)
        {
            var result = await container.ExecAsync(["cat", path]).ConfigureAwait(false);
            Assert.AreEqual(0, result.ExitCode, $"SQL container cgroup read failed: {result.Stderr}");
            return result.Stdout.Trim();
        }
        var cpu = await Read("/sys/fs/cgroup/cpu.stat").ConfigureAwait(false);
        var memory = await Read("/sys/fs/cgroup/memory.current").ConfigureAwait(false);
        var peak = await Read("/sys/fs/cgroup/memory.peak").ConfigureAwait(false);
        var io = await Read("/sys/fs/cgroup/io.stat").ConfigureAwait(false);
        var usage = long.Parse(cpu.Split('\n').Single(line => line.StartsWith("usage_usec ", StringComparison.Ordinal))
            .Split(' ')[1], CultureInfo.InvariantCulture);
        return new(usage, long.Parse(memory, CultureInfo.InvariantCulture), long.Parse(peak, CultureInfo.InvariantCulture), io);
    }
}

internal sealed record SqlContainerSnapshot(long CpuMicroseconds, long MemoryBytes, long PeakMemoryBytes, string BlockIoCounters);
internal sealed record InterferenceOperation(RawUploadEvidence Raw, CentralJobEvidence Job, DateTimeOffset CaptureStartedUtc,
    double CaptureLoopMilliseconds, double ModuleMilliseconds, double EdgeStepMilliseconds, double TelemetryTimestampOffsetMilliseconds,
    double RawIngressCommitMilliseconds, double CaptureToIngressMilliseconds, double? CaptureStartGapMilliseconds);
internal sealed record InterferencePhase(string Name, IReadOnlyList<Guid> WarmupCaptureIds,
    IReadOnlyList<InterferenceOperation> Operations, double ElapsedMilliseconds, double OperationsPerSecond,
    LatencyEvidence CaptureLatency, LatencyEvidence RawIngressLatency, LatencyEvidence UploadLatency, LatencyEvidence CentralLatency,
    object Process, SqlContainerSnapshot SqlBefore, SqlContainerSnapshot SqlAfter,
    object IngressBefore, object IngressAfter, ExecutionEvidenceBacklog Backlog,
    EdgeHealthEvidence HealthBefore, IReadOnlyList<EdgeHealthEvidence> HealthSamples,
    ExecutionEvidenceStatusView StatusBefore, ExecutionEvidenceStatusView StatusAfter,
    long EvidenceRequestEntityBytes, long EvidenceResponseEntityBytes, string EffectiveConfigurationSha256);
internal sealed record RegressionComparison(string Metric, string Phase, double Baseline, double Candidate,
    double IdleBracketDriftFraction, double ToleranceFraction, string Outcome);
internal sealed record InterferenceEvidence(IReadOnlyList<InterferencePhase> Phases, EdgeHealthEvidence FinalConvergence);
