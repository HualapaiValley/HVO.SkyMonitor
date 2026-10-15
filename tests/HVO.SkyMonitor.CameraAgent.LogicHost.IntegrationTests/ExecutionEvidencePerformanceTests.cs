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
    private const int ColdStartOperations = 30;
    private const string SteadyStateEnvironmentVariable = "HVO_ISSUE_538_STEADY_STATE";
    private const long RawBytes = 4_708_352;
    private const string RepeatCommand = "dotnet test tests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests/HVO.SkyMonitor.CameraAgent.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~ExecutionEvidencePerformanceTests.Issue538_MetadataAndLiveInterferenceAcceptance";
    private static string DiagnosticRepeatCommand => ExecutionEvidenceDiagnosticCollector.DiagnosticRepeatCommand(RepeatCommand);
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
        var diagnosticMode = ExecutionEvidenceDiagnosticCollector.IsEnabled;
        var steadyStateMode = string.Equals(Environment.GetEnvironmentVariable(SteadyStateEnvironmentVariable),
            "1", StringComparison.Ordinal);
        Assert.IsFalse(steadyStateMode && (diagnosticMode || ExecutionEvidenceDiagnosticCollector.IsTraceRequested),
            "Steady-state acceptance cannot be mixed with diagnostic instrumentation or EventPipe.");
        Assert.IsTrue(diagnosticMode || !ExecutionEvidenceDiagnosticCollector.IsTraceRequested,
            "EventPipe attach is available only in explicitly enabled diagnostic mode.");
        if (diagnosticMode)
        {
            TestContext.WriteLine($"DiagnosticOnly-NotAcceptance source={source.Sha} dirty={source.Dirty} repeat={DiagnosticRepeatCommand}");
        }
        if (diagnosticMode) { destination = Path.Combine(destination, "diagnostic"); }
        if (steadyStateMode) { destination = Path.Combine(destination, "steady-state"); }
        var repeatCommand = PerformanceRepeatCommand(diagnosticMode, steadyStateMode);
        TestContext.WriteLine($"mode={PerformanceMode(diagnosticMode, steadyStateMode)} source={source.Sha} dirty={source.Dirty} repeat={repeatCommand}");
        Directory.CreateDirectory(destination);
        using var counters = new ExecutionEvidencePerformanceCounters(Fixture.HostFixture.SqlServerConnectionString);
        // The idle bracket must precede the large metadata histories and their real reconciliation work.
        var interference = await MeasureInterferenceAsync(counters, destination, source, diagnosticMode, steadyStateMode).ConfigureAwait(false);
        var comparisons = Compare(interference.Phases);
        if (diagnosticMode)
        {
            await SaveIntermediateAsync(destination, "diagnostic-interference-completed", source, new
            {
                Mode = "DiagnosticOnly-NotAcceptance",
                Interference = interference,
                Comparisons = comparisons,
                MetadataWorkloads = "Not run in diagnostic mode; the declared E538-I 4 x (5 + 30) workload is unchanged."
            }, diagnosticMode: true).ConfigureAwait(false);
            Assert.Inconclusive("Diagnostic-only E538-I completed; instrumentation overhead and causes require review. This is not performance acceptance.");
        }
        var metadata = await MeasureMetadataAsync(counters).ConfigureAwait(false);
        await SaveIntermediateAsync(destination, "metadata-initial-baseline", source, metadata,
            steadyStateMode: steadyStateMode).ConfigureAwait(false);
        var concurrency = await MeasureMetadataConcurrencyAsync(counters).ConfigureAwait(false);
        await SaveIntermediateAsync(destination, "metadata-concurrency", source, concurrency,
            steadyStateMode: steadyStateMode).ConfigureAwait(false);
        var evidence = new
        {
            Schema = "hvo-issue538-execution-evidence-acceptance-v1",
            Mode = PerformanceMode(diagnosticMode, steadyStateMode),
            SteadyStateModeEnabled = steadyStateMode,
            Source = source,
            RecordedAtUtc = DateTimeOffset.UtcNow,
            RepeatCommand = repeatCommand,
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
                StageOrder = steadyStateMode
                    ? "I: separately reported 30 cold-start operations and complete admission/upload/job/export drain, then unchanged idle-before/active/denied/idle-after, then M, then C"
                    : "I (idle-before/active/denied/idle-after) on the fresh fixture, then M, then C",
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
                    ColdStartSelectedOperations = steadyStateMode ? ColdStartOperations : 0,
                    TotalSelectedOperations = steadyStateMode ? ColdStartOperations + 4 * (Warmup + Measured) : 4 * (Warmup + Measured),
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
                Trials = "One bracketed four-phase run with thirty distinct measured operations per phase; p95 is per operation. This is not a five-independent-trial confidence interval.",
                Startup = steadyStateMode
                    ? "Thirty separately retained cold-start operations precede the unchanged comparison phases. Every natural startup/drain operation is retained; none enters the comparisons. This amended run does not replace earlier Inconclusive evidence."
                    : "Original fixed manifest; no added cold-start prefix."
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

    private static string PerformanceRepeatCommand(bool diagnosticMode, bool steadyStateMode)
        => diagnosticMode ? DiagnosticRepeatCommand : steadyStateMode ? SteadyStateEnvironmentVariable + "=1 " + RepeatCommand : RepeatCommand;

    private static string PerformanceMode(bool diagnosticMode, bool steadyStateMode)
        => diagnosticMode ? "DiagnosticOnly-NotAcceptance" : steadyStateMode ? "SteadyStateAcceptance" : "AcceptanceStage";

    private static async Task SaveIntermediateAsync(string directory, string name, SourceEvidence source, object value,
        bool diagnosticMode = false, bool steadyStateMode = false)
    {
        var path = Path.Combine(directory, name + ".json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(new
        {
            Schema = "hvo-issue538-partial-performance-stage-v1",
            Mode = PerformanceMode(diagnosticMode, steadyStateMode),
            DiagnosticModeEnabled = diagnosticMode,
            SteadyStateModeEnabled = steadyStateMode,
            Source = source,
            RepeatCommand = PerformanceRepeatCommand(diagnosticMode, steadyStateMode),
            RecordedAtUtc = DateTimeOffset.UtcNow,
            CompletedStage = name,
            Evidence = value,
            OverallAcceptance = diagnosticMode ? "DiagnosticOnly-NotAcceptance" : "Pending remaining stages and interference comparisons"
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
        string destination, SourceEvidence source, bool diagnosticMode, bool steadyStateMode)
    {
        using var diagnostics = diagnosticMode ? new ExecutionEvidenceDiagnosticCollector() : null;
        SteadyStateRecorder? steadyState = null;
        var workloadCompleted = false;
        try
        {
            if (diagnostics is not null)
            {
                await diagnostics.AttachTraceAsync(source, DiagnosticRepeatCommand).ConfigureAwait(false);
            }
            var startupSqlBefore = steadyStateMode ? await SqlContainerAsync().ConfigureAwait(false) : null;
            var startupProcessBefore = steadyStateMode ? counters.BeginMeasurement() : null;
            var fixtureStartedUtc = DateTimeOffset.UtcNow;
            await using var edge = await ExecutionEvidenceCombinedHarness.CreateAsync(Fixture, w1Geometry: true, diagnostics: diagnostics).ConfigureAwait(false);
            var central = await ExecutionEvidenceCentralWorkload.BindAsync(Fixture, edge).ConfigureAwait(false);
            object? coldStart = null;
            if (steadyStateMode)
            {
                steadyState = new(edge, central, counters);
                central.OperationCompleted = steadyState.ObserveAsync;
                coldStart = await MeasureColdStartAsync(edge, central, counters, steadyState, destination, source,
                    fixtureStartedUtc, startupProcessBefore!, startupSqlBefore!).ConfigureAwait(false);
                await edge.ResumeCaptureAsync().ConfigureAwait(false);
            }
            else
            {
                await edge.WaitForRealExecutionAsync().ConfigureAwait(false);
                await edge.DrainAsync().ConfigureAwait(false);
            }
            var phases = new List<InterferencePhase>();
            foreach (var name in new[] { "idle-before", "active", "denied", "idle-after" })
            {
                using var stop = new CancellationTokenSource();
                edge.Http.Fault = name == "denied" ? EvidenceHttpFault.Unavailable : EvidenceHttpFault.None;
                var load = name is "active" or "denied" ? RunEvidenceLoadAsync(edge, name, stop.Token) : Task.CompletedTask;
                try
                {
                    var phase = await MeasureLivePhaseAsync(name, edge, central, counters, diagnostics, steadyState).ConfigureAwait(false);
                    phases.Add(phase);
                    await SaveIntermediateAsync(destination, "interference-" + name, source, phase,
                        diagnosticMode, steadyStateMode).ConfigureAwait(false);
                }
                finally
                {
                    await stop.CancelAsync().ConfigureAwait(false);
                    await load.ConfigureAwait(false);
                }
                edge.Http.Fault = EvidenceHttpFault.None;
                if (steadyState is not null) { steadyState.Stage = name + "-recovery"; }
                await edge.DrainAsync().ConfigureAwait(false); // Recovery is excluded from the next idle measurement.
            }
            if (steadyState is not null) { steadyState.Stage = "final-drain"; }
            await edge.StopCaptureAndWaitForTerminalAsync().ConfigureAwait(false);
            using var convergenceDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var converged = await central.ConvergeUploadsAndJobsAsync(convergenceDeadline.Token).ConfigureAwait(false);
            await edge.DrainAsync().ConfigureAwait(false);
            await central.AssertPersistedAsync(edge.ReadLocalRows()).ConfigureAwait(false);
            steadyState?.AssertConverged(converged);
            workloadCompleted = true;
            return new(phases, converged, coldStart);
        }
        catch
        {
            workloadCompleted = false;
            throw;
        }
        finally
        {
            if (steadyState is not null)
            {
                try
                {
                    await SaveIntermediateAsync(destination, "steady-state-all-capture-operations", source,
                        new { WorkloadCompleted = workloadCompleted, Retention = steadyState.Snapshot() },
                        steadyStateMode: true).ConfigureAwait(false);
                }
                catch (Exception exception) when (!workloadCompleted)
                {
                    Trace.TraceError("Issue538 steady-state retention failed after the original workload failure: {0}", exception);
                }
            }
            if (diagnostics is not null)
            {
                var detached = await diagnostics.DetachTraceAsync(workloadCompleted).ConfigureAwait(false);
                await SaveIntermediateAsync(destination, "diagnostic-producer-runtime-retention", source,
                    diagnostics.Snapshot(DiagnosticRepeatCommand), diagnosticMode: true).ConfigureAwait(false);
                if (workloadCompleted) { Assert.IsTrue(detached, "EventPipe detach/finalization failed; retain handshake and collector diagnostics. This cannot satisfy acceptance."); }
            }
        }
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

    private static async Task<object> MeasureColdStartAsync(ExecutionEvidenceCombinedHarness edge,
        ExecutionEvidenceCentralWorkload central, ExecutionEvidencePerformanceCounters counters,
        SteadyStateRecorder retention, string destination, SourceEvidence source, DateTimeOffset fixtureStartedUtc,
        ResourceSnapshot startupProcessBefore, SqlContainerSnapshot startupSqlBefore)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        var selectionStartedUtc = DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        await edge.WaitForRealExecutionAsync(deadline.Token).ConfigureAwait(false);
        var healthBefore = await central.ReadHealthAsync(deadline.Token).ConfigureAwait(false);
        Assert.IsNull(healthBefore.CaptureLastFailedUtc);
        Assert.AreEqual(0, healthBefore.FailedCaptureSamples);
        Assert.AreEqual(0, healthBefore.TransactionFailures);
        Assert.AreEqual(0, healthBefore.Counters.IngressFailures + healthBefore.Counters.IngressCancellations);
        Assert.AreEqual(0, healthBefore.BadRawCaptures + healthBefore.QuarantinedLaneRecords + healthBefore.AbandonedLaneRecords);
        Assert.AreEqual(0, healthBefore.FailedCentralJobs + healthBefore.Upload.QuarantinedCount + healthBefore.Upload.AbandonedCount);
        retention.Baseline = healthBefore;
        var statusBefore = await central.ReadStatusAsync(deadline.Token).ConfigureAwait(false);
        var ingress = edge.Agent.Services.GetRequiredService<RawIngressTelemetry>();
        var ingressBefore = Ingress(ingress);
        var rows = new List<InterferenceOperation>();
        while (rows.Count < ColdStartOperations)
        {
            var raw = await central.UploadNextRawAsync(deadline.Token).ConfigureAwait(false);
            _ = await central.ExecutePreviewAsync(raw, deadline.Token).ConfigureAwait(false);
            rows.Add(retention.Find(raw.CaptureId).Operation);
            retention.Select(raw.CaptureId, "cold-start", "ColdStart", rows.Count);
        }
        // No exporter discovery, negotiation or submission precedes these exact30 operations.
        Assert.AreEqual(0, edge.Http.Exchanges.Count);
        Assert.HasCount(ColdStartOperations, rows.Select(row => row.Raw.CaptureId).Distinct().ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, ColdStartOperations)
            .Select(index => rows[0].Raw.CaptureSequence + index).ToArray(), rows.Select(row => row.Raw.CaptureSequence).ToArray());
        var coldElapsed = Stopwatch.GetElapsedTime(started);
        var processAfter = counters.Snapshot();
        var sqlAfter = await SqlContainerAsync().ConfigureAwait(false);
        var cold = new
        {
            Name = "E538-I-cold-start-separately-reported",
            SelectedOperations = ColdStartOperations,
            DeadlineSeconds = 180,
            FixtureStartedUtc = fixtureStartedUtc,
            SelectionStartedUtc = selectionStartedUtc,
            Operations = rows,
            ElapsedMilliseconds = coldElapsed.TotalMilliseconds,
            OperationsPerSecond = ColdStartOperations / coldElapsed.TotalSeconds,
            CaptureLatency = LatencyEvidence.From(rows.Select(row => row.CaptureToIngressMilliseconds)),
            RawIngressLatency = LatencyEvidence.From(rows.Select(row => row.RawIngressCommitMilliseconds)),
            UploadLatency = LatencyEvidence.From(rows.Select(row => row.Raw.UploadMilliseconds)),
            CentralLatency = LatencyEvidence.From(rows.Select(row => row.Job.ClaimAndExecuteMilliseconds)),
            Process = startupProcessBefore.Delta(processAfter),
            ProcessScope = "Pre-host construction through selected cold operations; fixture startup is included in resource counters and separately timestamped.",
            SqlBefore = startupSqlBefore,
            SqlAfter = sqlAfter,
            HealthBefore = healthBefore,
            IngressBefore = ingressBefore,
            IngressAfter = Ingress(ingress),
            StatusBefore = statusBefore,
            StatusAfter = await central.ReadStatusAsync(deadline.Token).ConfigureAwait(false),
            EffectiveConfigurationSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(
                Path.Combine(edge.Root, "cameraagent.integration.json"), deadline.Token).ConfigureAwait(false))),
            ComparedWithInterferencePhases = false
        };
        await SaveIntermediateAsync(destination, "cold-start-selected-operations", source, cold,
            steadyStateMode: true).ConfigureAwait(false);

        retention.Stage = "cold-start-drain";
        var drainBefore = counters.BeginMeasurement();
        var drainSqlBefore = await SqlContainerAsync().ConfigureAwait(false);
        var backlogBefore = await edge.Outbox.ReadBacklogAsync(edge.Root, deadline.Token).ConfigureAwait(false);
        await edge.PauseCaptureAndWaitForTerminalAsync(deadline.Token).ConfigureAwait(false);
        var healthAfter = await central.ConvergeUploadsAndJobsAsync(deadline.Token).ConfigureAwait(false);
        ExecutionEvidenceCentralWorkload.AssertHealthy(healthBefore, healthAfter);
        retention.AssertAllRawAccounted(healthAfter);
        await edge.DrainAsync(cancellationToken: deadline.Token).ConfigureAwait(false);
        var local = edge.ReadLocalRows();
        await central.AssertPersistedAsync(local).ConfigureAwait(false);
        ExecutionEvidenceCombinedTests.AssertExactAcknowledgements(edge, local);
        var statusAfter = await central.ReadStatusAsync(deadline.Token).ConfigureAwait(false);
        var backlogAfter = await edge.Outbox.ReadBacklogAsync(edge.Root, deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(0, backlogAfter.PendingCount + backlogAfter.RetryCount);
        var barrier = new
        {
            Admission = "Paused; every accepted edge execution terminal; all raw and derivative uploads/jobs converged before original phases resume.",
            TotalPrefixMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            ColdStart = cold,
            Health = healthAfter,
            BacklogBefore = backlogBefore,
            BacklogAfter = backlogAfter,
            StatusBefore = statusBefore,
            StatusAfter = statusAfter,
            AddedImportedUnits = statusAfter.UnitCount - statusBefore.UnitCount,
            AddedObservations = statusAfter.ObservationCount - statusBefore.ObservationCount,
            DueArtifactChecksBefore = statusBefore.DueArtifactChecks,
            DueArtifactChecksAfter = statusAfter.DueArtifactChecks,
            DueArtifactChecksAfterTruncated = statusAfter.DueArtifactChecksTruncated,
            CanonicalPayloadSetSha256 = HashSet(local),
            NaturalAndSelectedOperations = retention.Snapshot(),
            Process = drainBefore.Delta(counters.Snapshot()),
            SqlBefore = drainSqlBefore,
            SqlAfter = await SqlContainerAsync().ConfigureAwait(false),
            ProductionReconcilerEnabled = true
        };
        await SaveIntermediateAsync(destination, "cold-start-terminal-barrier", source, barrier,
            steadyStateMode: true).ConfigureAwait(false);
        deadline.Token.ThrowIfCancellationRequested();
        return barrier;
    }

    private static async Task<InterferencePhase> MeasureLivePhaseAsync(string name, ExecutionEvidenceCombinedHarness edge,
        ExecutionEvidenceCentralWorkload central, ExecutionEvidencePerformanceCounters counters,
        ExecutionEvidenceDiagnosticCollector? diagnostics, SteadyStateRecorder? steadyState)
    {
        if (steadyState is not null) { steadyState.Stage = name + "-warmup"; }
        var phaseStart = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var healthBefore = await central.ReadHealthAsync(deadline.Token).ConfigureAwait(false);
        var healthSamples = new List<EdgeHealthEvidence>();
        var warmup = new List<Guid>();
        while (warmup.Count < Warmup)
        {
            var raw = await central.UploadNextRawAsync(deadline.Token).ConfigureAwait(false);
            _ = await central.ExecutePreviewAsync(raw, deadline.Token).ConfigureAwait(false);
            var health = steadyState?.Find(raw.CaptureId).Health ?? await central.ReadHealthAsync(deadline.Token).ConfigureAwait(false);
            ExecutionEvidenceCentralWorkload.AssertHealthy(healthBefore, health);
            if (raw.CapturedAtUtc >= phaseStart)
            {
                warmup.Add(raw.CaptureId);
                steadyState?.Select(raw.CaptureId, name, "Warmup", warmup.Count);
                diagnostics?.RetainCapture(name, "Warmup", warmup.Count, raw);
            }
        }
        // Delimit acquisition as well as upload: no raw acquired during warmup can enter the measured set.
        if (steadyState is not null) { steadyState.Stage = name + "-warmup-drain"; }
        await edge.PauseCaptureAndWaitForTerminalAsync(deadline.Token).ConfigureAwait(false);
        _ = await central.ConvergeUploadsAndJobsAsync(deadline.Token).ConfigureAwait(false);
        var rows = new List<InterferenceOperation>();
        var ingress = edge.Agent.Services.GetRequiredService<RawIngressTelemetry>();
        var ingressBefore = Ingress(ingress);
        var statusBefore = await central.ReadStatusAsync(deadline.Token).ConfigureAwait(false);
        var sqlBefore = await SqlContainerAsync().ConfigureAwait(false);
        var processBefore = counters.BeginMeasurement();
        var exchangeOffset = edge.Http.Exchanges.Count;
        var started = Stopwatch.GetTimestamp();
        var measuredAdmissionUtc = DateTimeOffset.UtcNow;
        if (steadyState is not null) { steadyState.Stage = name + "-measured"; }
        await edge.ResumeCaptureAsync(deadline.Token).ConfigureAwait(false);
        DateTimeOffset? previousCaptureStart = null;
        while (rows.Count < Measured)
        {
            var raw = await central.UploadNextRawAsync(deadline.Token).ConfigureAwait(false);
            diagnostics?.RetainCapture(name, "Measured", rows.Count + 1, raw);
            var job = await central.ExecutePreviewAsync(raw, deadline.Token).ConfigureAwait(false);
            var operation = steadyState?.Find(raw.CaptureId).Operation ??
                await ReadInterferenceOperationAsync(edge, raw, job, deadline.Token).ConfigureAwait(false);
            Assert.IsGreaterThanOrEqualTo(measuredAdmissionUtc, operation.CaptureStartedUtc);
            double? startGap = previousCaptureStart is { } prior ? (raw.ModuleCallStartedUtc - prior).TotalMilliseconds : null;
            previousCaptureStart = raw.ModuleCallStartedUtc;
            rows.Add(operation with { CaptureStartGapMilliseconds = startGap });
            steadyState?.Select(raw.CaptureId, name, "Measured", rows.Count);
            var health = steadyState?.Find(raw.CaptureId).Health ?? await central.ReadHealthAsync(deadline.Token).ConfigureAwait(false);
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

    private static async Task<InterferenceOperation> ReadInterferenceOperationAsync(ExecutionEvidenceCombinedHarness edge,
        RawUploadEvidence raw, CentralJobEvidence job, CancellationToken cancellationToken)
    {
        Assert.AreEqual(RawBytes, raw.Bytes);
        var telemetry = edge.Agent.Services.GetRequiredService<ICaptureTelemetryProvider>();
        CaptureTelemetrySample? sample = null;
        await ExecutionEvidenceCombinedHarness.WaitUntilAsync(() =>
        {
            // Descriptor persistence truncates milliseconds; retain and validate the original full-precision sample.
            sample = telemetry.GetSnapshot().Samples.SingleOrDefault(item =>
                item.StartedUtc.ToUnixTimeMilliseconds() == raw.ModuleCallStartedUtc.ToUnixTimeMilliseconds());
            return Task.FromResult(sample is not null);
        }, "capture telemetry corresponding to the uploaded raw", cancellationToken: cancellationToken).ConfigureAwait(false);
        Assert.IsNotNull(sample);
        var descriptorPrecisionOffset = sample.StartedUtc - raw.ModuleCallStartedUtc;
        Assert.IsGreaterThanOrEqualTo(TimeSpan.Zero, descriptorPrecisionOffset);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(1), descriptorPrecisionOffset);
        Assert.IsTrue(sample.FrameStored);
        var commits = edge.Metrics.Commits;
        Assert.IsGreaterThanOrEqualTo(raw.RawCommitOrdinal, commits.Count);
        var commit = commits[raw.RawCommitOrdinal - 1];
        Assert.IsLessThan(1000, Math.Abs((commit.CompletedAtUtc - raw.JournalCommittedAtUtc).TotalMilliseconds),
            "The isolated ingress histogram sample must match this durable commit ordinal and timestamp.");
        return new(raw, job, sample.StartedUtc, sample.LoopDuration.TotalMilliseconds,
            sample.ProcessingLatency.TotalMilliseconds, sample.ProcessingSteps.Sum(step => step.Duration.TotalMilliseconds),
            (sample.StartedUtc - raw.CapturedAtUtc).TotalMilliseconds, commit.Milliseconds,
            (commit.CompletedAtUtc - raw.ModuleCallStartedUtc).TotalMilliseconds, null);
    }

    private sealed class SteadyStateRecorder(ExecutionEvidenceCombinedHarness edge,
        ExecutionEvidenceCentralWorkload central, ExecutionEvidencePerformanceCounters counters)
    {
        private readonly Dictionary<Guid, RetainedCaptureOperation> _operations = [];
        private readonly Dictionary<Guid, CaptureSelection> _selected = [];
        private DateTimeOffset? _previousCaptureStart;
        private bool _allRawAccounted;
        internal string Stage { get; set; } = "cold-start";
        internal EdgeHealthEvidence? Baseline { get; set; }

        internal async Task ObserveAsync(RawUploadEvidence raw, CentralJobEvidence job, CancellationToken cancellationToken)
        {
            _allRawAccounted = false;
            Assert.IsLessThan(4096, _operations.Count, "Bounded retention filled; fail rather than omit any admitted capture.");
            var operation = await ReadInterferenceOperationAsync(edge, raw, job, cancellationToken).ConfigureAwait(false);
            operation = operation with
            {
                CaptureStartGapMilliseconds = _previousCaptureStart is { } prior
                    ? (raw.ModuleCallStartedUtc - prior).TotalMilliseconds : null
            };
            _previousCaptureStart = raw.ModuleCallStartedUtc;
            var health = await central.ReadHealthAsync(cancellationToken).ConfigureAwait(false);
            Assert.IsTrue(_operations.TryAdd(raw.CaptureId,
                new(Stage, operation, health, counters.Snapshot(), DateTimeOffset.UtcNow)),
                "Every actual capture/upload/job tuple must occur exactly once in retention.");
            Assert.IsNotNull(Baseline);
            ExecutionEvidenceCentralWorkload.AssertHealthy(Baseline, health);
        }

        internal RetainedCaptureOperation Find(Guid captureId) => _operations[captureId];

        internal void Select(Guid captureId, string phase, string role, int ordinal)
        {
            Assert.IsTrue(_operations.ContainsKey(captureId));
            Assert.IsTrue(_selected.TryAdd(captureId, new(phase, role, ordinal)),
                "A cold, warmup or measured capture cannot be selected or classified twice.");
        }

        internal object Snapshot() => new
        {
            RetentionBound = 4096,
            AllAdmittedOperationsVerified = _allRawAccounted,
            OmittedOperations = _allRawAccounted ? 0 : (int?)null,
            SelectedCount = _selected.Count,
            NaturalCount = _operations.Count - _selected.Count,
            Operations = _operations.Values.OrderBy(row => row.Operation.Raw.CaptureSequence).Select(row => new
            {
                Observation = row,
                Selection = _selected.GetValueOrDefault(row.Operation.Raw.CaptureId),
                Accounting = _selected.ContainsKey(row.Operation.Raw.CaptureId) ? "DeclaredSelection" : "NaturallyAdmittedStartupOrDrain"
            }).ToArray(),
            Attribution = "Absolute per-operation process snapshots and phase deltas aggregate both in-process hosts and harness; SQL cgroup bookends are separate. Retention/health probes remain inside operation wall time."
        };

        internal void AssertConverged(EdgeHealthEvidence final)
        {
            AssertAllRawAccounted(final);
            Assert.AreEqual(ColdStartOperations + 4 * (Warmup + Measured), _selected.Count);
            Assert.AreEqual(ColdStartOperations, _selected.Values.Count(row => row.Role == "ColdStart"));
            foreach (var name in new[] { "idle-before", "active", "denied", "idle-after" })
            {
                Assert.AreEqual(Warmup, _selected.Values.Count(row => row.Phase == name && row.Role == "Warmup"));
                Assert.AreEqual(Measured, _selected.Values.Count(row => row.Phase == name && row.Role == "Measured"));
            }
        }

        internal void AssertAllRawAccounted(EdgeHealthEvidence final)
        {
            Assert.AreEqual(final.RawCaptures, (long)_operations.Count, "Every naturally admitted capture must have a retained upload/job tuple.");
            var sequences = _operations.Values.Select(row => row.Operation.Raw.CaptureSequence).Order().ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, sequences.Length).Select(index => sequences[0] + index).ToArray(), sequences);
            Assert.AreEqual(_operations.Count, _operations.Values.Select(row => row.Operation.Job.JobId).Distinct().Count());
            _allRawAccounted = true;
        }
    }

    private sealed record CaptureSelection(string Phase, string Role, int Ordinal);
    private sealed record RetainedCaptureOperation(string Stage, InterferenceOperation Operation, EdgeHealthEvidence Health,
        ResourceSnapshot ProcessAtCompletion, DateTimeOffset ObservedAtUtc);

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
internal sealed record InterferenceEvidence(IReadOnlyList<InterferencePhase> Phases, EdgeHealthEvidence FinalConvergence,
    object? ColdStart);
