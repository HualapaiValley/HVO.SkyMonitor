using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Scheduling;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class SqliteCaptureScheduleStorePerformanceTests
{
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
    private const int RevisionCount = 303;
    private const int StageCommandCount = RevisionCount - 1;
    private const int PreviewDayCount = 30;
    private const int ReaderCount = 4;
    private const int ReaderQueriesPerReader = 32;
    private const int HistoryPageSize = 100;
    private const int ExpectedAggregateRecords = 10_000;
    private static readonly DateOnly PreviewFirstDate = new(2025, 1, 1);
    private static readonly DateTimeOffset FixedUtcNow = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions EvidenceJsonOptions = CreateEvidenceJsonOptions();

    [TestMethod]
    public async Task TenThousandImmutableRecords_ConcurrentReadersAndReopen_WriteEvidence()
    {
        if (!string.Equals(BuildConfiguration, "Release", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Issue #207 durable SQLite evidence requires a Release build.");
        }

        var repositoryRoot = GetRepositoryRoot();
        var revision = GetEvidenceRevision();
        var outputDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-207", revision);
        var evidencePath = Path.Combine(outputDirectory, "capture-schedule-durable-metadata-performance.json");
        var root = Path.Combine(Path.GetTempPath(), "hvo-schedule-performance", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outputDirectory);
        SqliteCaptureScheduleStore? writer = null;
        SqliteCaptureScheduleStore[] readers = [];
        SemaphoreSlim? readersReady = null;
        CancellationTokenSource? readerCancellation = null;
        Task<ReaderResult>[] readerTasks = [];

        try
        {
            var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
            var options = Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 30
            });
            var configuration = Configuration(Definition(0));
            var location = new CaptureLocationProvenance(
                "issue-207-performance-location", 1, "deterministic-performance-workload", null,
                FixedUtcNow, null);
            var calculator = new AstronomyEngineSolarEventCalculator();
            var timeProvider = new FixedTimeProvider(FixedUtcNow);
            var warmInitializer = new MemoizedJournalInitializer(root);

            // Schema setup is outside measured schedule operations; later ingress calls reuse this task.
            await warmInitializer.InitializeAsync(CancellationToken.None).ConfigureAwait(false);

            writer = new SqliteCaptureScheduleStore(warmInitializer, options, timeProvider);
            readers = Enumerable.Range(0, ReaderCount)
                .Select(_ => new SqliteCaptureScheduleStore(warmInitializer, options, timeProvider))
                .ToArray();
            var writeSamples = new List<double>();
            var bootstrapSamples = new List<double>();
            var stageSamples = new List<double>();
            var previewSamples = new List<double>();
            var admissionSamples = new List<double>();
            var writeThreadAllocations = new ThreadAllocationTracker();
            var queryThreadAllocations = new ThreadAllocationTracker();
            var concurrency = new ReaderConcurrencyTracker();
            var startReaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            readersReady = new SemaphoreSlim(0, ReaderCount);
            readerCancellation = new CancellationTokenSource();
            var writerProgress = 0;
            var writerRunning = 1;

            readerTasks = readers.Select((store, readerId) => RunReaderAsync(
                store,
                readerId,
                startReaders.Task,
                readersReady,
                queryThreadAllocations,
                concurrency,
                () => Volatile.Read(ref writerProgress),
                () => Volatile.Read(ref writerRunning),
                readerCancellation.Token)).ToArray();
            for (var index = 0; index < ReaderCount; index++)
            {
                await readersReady.WaitAsync().ConfigureAwait(false);
            }

            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var rssBefore = process.WorkingSet64;
            var maximumSampledRss = rssBefore;
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
            var writerStarted = Stopwatch.GetTimestamp();
            startReaders.SetResult();

            CaptureScheduleStoreSnapshot current;
            var bootstrap = await MeasureAsync(
                () => writer.InitializeAsync(configuration, CancellationToken.None),
                writeSamples,
                bootstrapSamples,
                writeThreadAllocations).ConfigureAwait(false);
            current = bootstrap;
            var activeRevision = bootstrap.ActiveRevision;
            var activePreview = Expand(activeRevision.Definition, configuration, calculator);
            Assert.AreEqual(PreviewDayCount, activePreview.Intervals.Count);
            await MeasureAsync(
                () => writer.PersistPreviewAsync(activeRevision, location, activePreview, CancellationToken.None),
                writeSamples,
                previewSamples,
                writeThreadAllocations).ConfigureAwait(false);
            Volatile.Write(ref writerProgress, 1);

            for (var revisionIndex = 1; revisionIndex < RevisionCount; revisionIndex++)
            {
                var profile = LocalCaptureProfileDefinition.Create(configuration, Definition(revisionIndex));
                current = await MeasureAsync(
                    () => writer.StageAsync(
                        profile,
                        $"stage-{revisionIndex:D4}",
                        current.Version,
                        "issue-207-performance",
                        "deterministic durable metadata workload",
                        CancellationToken.None),
                    writeSamples,
                    stageSamples,
                    writeThreadAllocations).ConfigureAwait(false);
                var pending = current.PendingRevision
                    ?? throw new InvalidOperationException("The staged performance revision was not pending.");
                var preview = Expand(pending.Definition, configuration, calculator);
                Assert.AreEqual(PreviewDayCount, preview.Intervals.Count);
                await MeasureAsync(
                    () => writer.PersistPreviewAsync(pending, location, preview, CancellationToken.None),
                    writeSamples,
                    previewSamples,
                    writeThreadAllocations).ConfigureAwait(false);
                Volatile.Write(ref writerProgress, revisionIndex + 1);
                process.Refresh();
                maximumSampledRss = Math.Max(maximumSampledRss, process.WorkingSet64);
            }

            var admissionUtc = activePreview.Intervals[0].StartUtc.AddMinutes(1);
            var granted = await MeasureAsync(
                () => writer.GrantAdmissionAsync(
                    "admission-0001", activeRevision.RevisionId, null, admissionUtc, CancellationToken.None),
                writeSamples,
                admissionSamples,
                writeThreadAllocations).ConfigureAwait(false);
            Assert.IsTrue(granted);
            var writerElapsed = Stopwatch.GetElapsedTime(writerStarted);
            await SetActivationAbortTriggerAsync(databasePath, enabled: true).ConfigureAwait(false);
            try
            {
                _ = await Assert.ThrowsExactlyAsync<SqliteException>(() => writer.ActivateAsync(
                    current.PendingRevision!.RevisionId,
                    "interrupted-activation",
                    current.Version,
                    "issue-207-performance",
                    "abort after state update before activation commit",
                    CancellationToken.None)).ConfigureAwait(false);
            }
            finally
            {
                await SetActivationAbortTriggerAsync(databasePath, enabled: false).ConfigureAwait(false);
            }
            var stateAfterInterruptedActivation = await writer.GetSnapshotAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(current.ActiveRevision.RevisionId, stateAfterInterruptedActivation.ActiveRevision.RevisionId);
            Assert.AreEqual(current.PendingRevision!.RevisionId, stateAfterInterruptedActivation.PendingRevision?.RevisionId);
            Assert.AreEqual(current.Version, stateAfterInterruptedActivation.Version);
            Volatile.Write(ref writerRunning, 0);
            var readerResults = await Task.WhenAll(readerTasks).ConfigureAwait(false);
            var totalElapsed = Stopwatch.GetElapsedTime(writerStarted);
            var cpuElapsed = process.TotalProcessorTime - cpuBefore;
            var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            process.Refresh();
            var rssAfter = process.WorkingSet64;
            maximumSampledRss = Math.Max(maximumSampledRss, rssAfter);
            var workloadFiles = ReadFileSizes(databasePath);

            Assert.HasCount(1 + StageCommandCount + RevisionCount + 1, writeSamples);
            Assert.AreEqual(ReaderCount, concurrency.MaximumConcurrentReaders);
            Assert.IsTrue(readerResults.All(static result => result.QueryCount == ReaderQueriesPerReader));
            Assert.IsTrue(readerResults.All(static result => result.MaximumPageCount == HistoryPageSize));
            Assert.IsTrue(readerResults.All(static result => result.FinalPageCount == HistoryPageSize));
            Assert.IsGreaterThan(0, concurrency.QueriesStartedDuringWriterPhase);
            Assert.AreEqual(1L, current.ActiveRevision.RevisionNumber);
            Assert.AreEqual(RevisionCount, current.PendingRevision?.RevisionNumber);

            foreach (var reader in readers)
            {
                reader.Dispose();
            }
            readers = [];
            writer.Dispose();
            writer = null;
            readersReady.Dispose();
            readersReady = null;
            readerCancellation.Dispose();
            readerCancellation = null;
            SqliteConnection.ClearAllPools();

            var beforeReopen = await ReadDatabaseEvidenceAsync(databasePath).ConfigureAwait(false);
            AssertDatabaseInvariants(beforeReopen);
            Assert.AreEqual(current.ActiveRevision.RevisionId, beforeReopen.ActiveRevisionId);
            Assert.AreEqual(current.PendingRevision!.RevisionId, beforeReopen.PendingRevisionId);

            var freshInitializer = new MemoizedJournalInitializer(root);
            CaptureScheduleStoreSnapshot reopenedSnapshot;
            CaptureScheduleStoreSnapshot reopenedReadSnapshot;
            IReadOnlyList<CaptureScheduleRevisionSnapshot> reopenedHistory;
            CaptureSchedulePreview? reopenedPreview;
            var recoveryStarted = Stopwatch.GetTimestamp();
            TimeSpan recoveryElapsed;
            var validationStarted = 0L;
            using (var reopened = new SqliteCaptureScheduleStore(freshInitializer, options, timeProvider))
            {
                reopenedSnapshot = await reopened.InitializeAsync(configuration, CancellationToken.None).ConfigureAwait(false);
                recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
                validationStarted = Stopwatch.GetTimestamp();
                reopenedReadSnapshot = await reopened.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                reopenedHistory = await reopened.GetHistoryAsync(HistoryPageSize, CancellationToken.None)
                    .ConfigureAwait(false);
                reopenedPreview = await reopened.TryReadPreviewAsync(
                    reopenedSnapshot.ActiveRevision,
                    location,
                    activePreview.PreviewStartUtc.AddHours(12),
                    CancellationToken.None).ConfigureAwait(false);
            }
            var reopenValidationElapsed = Stopwatch.GetElapsedTime(validationStarted);
            SqliteConnection.ClearAllPools();

            Assert.AreEqual(current.ActiveRevision.RevisionId, reopenedSnapshot.ActiveRevision.RevisionId);
            Assert.AreEqual(current.PendingRevision.RevisionId, reopenedSnapshot.PendingRevision?.RevisionId);
            Assert.AreEqual(reopenedSnapshot.ActiveRevision.RevisionId, reopenedReadSnapshot.ActiveRevision.RevisionId);
            Assert.AreEqual(reopenedSnapshot.PendingRevision?.RevisionId, reopenedReadSnapshot.PendingRevision?.RevisionId);
            Assert.AreEqual(reopenedSnapshot.Version, reopenedReadSnapshot.Version);
            Assert.AreEqual(reopenedSnapshot.LastEvaluatedUtc, reopenedReadSnapshot.LastEvaluatedUtc);
            Assert.AreEqual(reopenedSnapshot.UpdatedUtc, reopenedReadSnapshot.UpdatedUtc);
            Assert.HasCount(HistoryPageSize, reopenedHistory);
            AssertOrderedLatestHistory(reopenedHistory);
            Assert.AreEqual(RevisionCount, reopenedHistory[0].RevisionNumber);
            Assert.AreEqual(activePreview.ExpansionSha256, reopenedPreview?.ExpansionSha256);
            Assert.AreEqual(1, freshInitializer.PhysicalInitializationCount);

            var afterReopen = await ReadDatabaseEvidenceAsync(databasePath).ConfigureAwait(false);
            AssertDatabaseInvariants(afterReopen);
            Assert.AreEqual(beforeReopen.LogicalOrderedSha256, afterReopen.LogicalOrderedSha256);
            Assert.AreEqual(beforeReopen.Counts, afterReopen.Counts);
            Assert.AreEqual(beforeReopen.ActiveRevisionId, afterReopen.ActiveRevisionId);
            Assert.AreEqual(beforeReopen.PendingRevisionId, afterReopen.PendingRevisionId);
            var afterReopenFiles = ReadFileSizes(databasePath);

            var querySamples = readerResults.SelectMany(static result => result.LatencyMilliseconds).ToArray();
            var writerPhaseQuerySamples = readerResults
                .SelectMany(static result => result.WriterPhaseLatencyMilliseconds)
                .ToArray();
            var uncontendedQuerySamples = new List<double>(32);
            using (var uncontendedReader = new SqliteCaptureScheduleStore(
                new MemoizedJournalInitializer(root), options, timeProvider))
            {
                for (var index = 0; index < 32; index++)
                {
                    var started = Stopwatch.GetTimestamp();
                    var page = await uncontendedReader.GetHistoryAsync(
                        HistoryPageSize, CancellationToken.None).ConfigureAwait(false);
                    uncontendedQuerySamples.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    Assert.HasCount(HistoryPageSize, page);
                }
            }
            var uncontendedMedian = Percentile(uncontendedQuerySamples.Order().ToArray(), 0.50);
            var estimatedContentionWaitSamples = querySamples
                .Select(sample => Math.Max(0, sample - uncontendedMedian))
                .Order()
                .ToArray();
            var throughputInitializers = Enumerable.Range(0, ReaderCount)
                .Select(_ => new MemoizedJournalInitializer(root))
                .ToArray();
            await Task.WhenAll(throughputInitializers.Select(initializer =>
                initializer.InitializeAsync(CancellationToken.None).AsTask())).ConfigureAwait(false);
            var throughputStores = throughputInitializers
                .Select(initializer => new SqliteCaptureScheduleStore(initializer, options, timeProvider))
                .ToArray();
            double[] throughputQuerySamples;
            TimeSpan throughputElapsed;
            try
            {
                var startThroughputReaders = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var throughputTasks = throughputStores.Select(store => RunUnpacedReaderAsync(
                    store, startThroughputReaders.Task, CancellationToken.None)).ToArray();
                var throughputStarted = Stopwatch.GetTimestamp();
                startThroughputReaders.SetResult();
                throughputQuerySamples = (await Task.WhenAll(throughputTasks).ConfigureAwait(false))
                    .SelectMany(static samples => samples)
                    .ToArray();
                throughputElapsed = Stopwatch.GetElapsedTime(throughputStarted);
            }
            finally
            {
                foreach (var store in throughputStores)
                {
                    store.Dispose();
                }
            }
            var git = await ReadGitEvidenceAsync(repositoryRoot).ConfigureAwait(false);
            var writeServiceElapsed = TimeSpan.FromMilliseconds(writeSamples.Sum());
            var exactCommand = $"DOTNET_gcServer=1 HVO_EVIDENCE_REVISION={revision} dotnet test " +
                "tests/HVO.SkyMonitor.CameraAgent.Tests/HVO.SkyMonitor.CameraAgent.Tests.csproj --no-build " +
                "--configuration Release --filter \"FullyQualifiedName=" +
                "HVO.SkyMonitor.CameraAgent.Tests.Scheduling.SqliteCaptureScheduleStorePerformanceTests." +
                "TenThousandImmutableRecords_ConcurrentReadersAndReopen_WriteEvidence\"";
            var evidence = new
            {
                Issue = 207,
                Scope = "Production SqliteCaptureScheduleStore durable metadata, concurrent latest-history reads, and reopen recovery",
                Revision = revision,
                Source = new
                {
                    Git = git,
                    Assemblies = new[]
                    {
                        ReadAssemblyEvidence(typeof(SqliteCaptureScheduleStorePerformanceTests)),
                        ReadAssemblyEvidence(typeof(SqliteCaptureScheduleStore)),
                        ReadAssemblyEvidence(typeof(CaptureScheduleDefinition))
                    }
                },
                GeneratedUtc = DateTimeOffset.UtcNow,
                ExactCommand = exactCommand,
                Baseline = new
                {
                    Revision = "N/A",
                    Measurements = "N/A",
                    Reason = "N/A: this is the first durable capture-schedule metadata evidence workload."
                },
                Environment = new
                {
                    OperatingSystem = RuntimeInformation.OSDescription,
                    OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Runtime = RuntimeInformation.FrameworkDescription,
                    RuntimeVersion = Environment.Version.ToString(),
                    Configuration = BuildConfiguration,
                    PinnedSdk = ReadPinnedSdkVersion(repositoryRoot),
                    ServerGc = GCSettings.IsServerGC,
                    GcServerEnvironment = Environment.GetEnvironmentVariable("DOTNET_gcServer"),
                    ProcessorCount = Environment.ProcessorCount,
                    Cpu = ReadCpuModel(),
                    TotalAvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    Storage = new
                    {
                        FileSystem = new DriveInfo(Path.GetPathRoot(root)!).DriveFormat,
                        PathRoot = Path.GetPathRoot(root),
                        PhysicalDeviceClass = "Unavailable inside the development-container overlay; compare SQLite latency only on equivalently provisioned hosts."
                    },
                    StopwatchFrequency = Stopwatch.Frequency,
                    SQLiteVersion = beforeReopen.SqliteVersion,
                    ExecutionMode = "Native dotnet test process; local temporary SQLite database; no application container or external service"
                },
                WorkloadManifest = new
                {
                    ProductionType = nameof(SqliteCaptureScheduleStore),
                    WriterCount = 1,
                    ReaderCount,
                    ReaderQueriesPerReader,
                    LatestHistoryPageSize = HistoryPageSize,
                    ScheduleRevisionCount = RevisionCount,
                    StageCommandCount,
                    BootstrapActivationCount = 1,
                    ExpansionCount = RevisionCount,
                    PreviewDayCount,
                    IntervalsPerRevision = PreviewDayCount,
                    IntervalCount = RevisionCount * PreviewDayCount,
                    AdmissionCount = 1,
                    AggregateImmutableRecords = ExpectedAggregateRecords,
                    PreviewFirstDate,
                    TimeZone = TimeZoneInfo.Utc.Id,
                    WindowComposition = "Seven deterministic same-day fixed local-time windows, one for each weekday, expanded for 30 days.",
                    Initializer = new
                    {
                        WarmOperation = "One shared memoized IRawCaptureIngress initializer; schema initialization completed before measurement.",
                        WarmInitializerCalls = warmInitializer.InitializeCallCount,
                        WarmPhysicalInitializations = warmInitializer.PhysicalInitializationCount,
                        ReopenOperation = "A distinct fresh memoized initializer; its first physical initialization is included in recovery duration.",
                        ReopenInitializerCalls = freshInitializer.InitializeCallCount,
                        ReopenPhysicalInitializations = freshInitializer.PhysicalInitializationCount
                    }
                },
                Measurements = new
                {
                    TotalWallMilliseconds = totalElapsed.TotalMilliseconds,
                    WriteOperationService = CreateMeasurement(writeSamples, writeServiceElapsed),
                    ConcurrentWriterWorkload = new
                    {
                        Operations = writeSamples.Count,
                        WallMilliseconds = writerElapsed.TotalMilliseconds,
                        CompletionRateOperationsPerSecond = writeSamples.Count / writerElapsed.TotalSeconds,
                        Boundary = "Writer initialization, expansion computation, assertions, RSS sampling, and contention from paced readers are included."
                    },
                    WriteOperationLatency = new
                    {
                        Bootstrap = CreateLatencySummary(bootstrapSamples),
                        StageRevisionAndCommand = CreateLatencySummary(stageSamples),
                        PersistExpansionAndIntervals = CreateLatencySummary(previewSamples),
                        Admission = CreateLatencySummary(admissionSamples)
                    },
                    LatestHistoryQueryDuringWriter = CreateLatencySummary(writerPhaseQuerySamples),
                    UncontendedLatestHistoryQuery = CreateLatencySummary(uncontendedQuerySamples),
                    FourReaderQueryThroughput = new
                    {
                        Operations = throughputQuerySamples.Length,
                        WallMilliseconds = throughputElapsed.TotalMilliseconds,
                        OperationsPerSecond = throughputQuerySamples.Length / throughputElapsed.TotalSeconds,
                        Latency = CreateLatencySummary(throughputQuerySamples),
                        Boundary = "Four readers start together after writes complete; wall time runs from release through the last completed page query."
                    },
                    ProcessCpuMilliseconds = cpuElapsed.TotalMilliseconds,
                    ProcessCpuUtilizationOfOneCorePercent = cpuElapsed.TotalMilliseconds * 100d / totalElapsed.TotalMilliseconds,
                    ProcessTotalAllocatedBytes = allocatedBytes,
                    ProcessAllocationBoundary = "The concurrent writer/reader workload total includes expansion computation, reader pacing, assertions, and harness bookkeeping; no per-operation value is inferred.",
                    WriteThreadAllocation = writeThreadAllocations.ToEvidence(),
                    QueryThreadAllocation = queryThreadAllocations.ToEvidence(),
                    ThreadAllocationLimitation = "Current-thread allocation is reported only when an async call resumes on its starting managed thread; process total allocation is the complete concurrency-safe measure.",
                    RssBeforeBytes = rssBefore,
                    MaximumSampledRssBytes = maximumSampledRss,
                    RssAfterBytes = rssAfter,
                    RssDeltaBytes = rssAfter - rssBefore,
                    WorkloadFiles = workloadFiles,
                    FilesAfterReopen = afterReopenFiles,
                    LockWait = new
                    {
                        DirectRecorder = "N/A: SqliteCaptureScheduleStore exposes no SQLite lock-wait callback.",
                        EstimatedSamples = estimatedContentionWaitSamples.Length,
                        EstimatedMedianMilliseconds = Percentile(estimatedContentionWaitSamples, 0.50),
                        EstimatedP95Milliseconds = Percentile(estimatedContentionWaitSamples, 0.95),
                        EstimationMethod = "Each concurrent query latency minus the median of 32 post-writer queries, floored at zero; this includes all contention-correlated delay and is not a direct SQLite busy-handler duration."
                    }
                },
                TransactionModel = new
                {
                    MeasuredScheduleWriteTransactions = 1 + StageCommandCount + RevisionCount + 1,
                    BootstrapTransactions = 1,
                    StageTransactions = StageCommandCount,
                    PreviewTransactions = RevisionCount,
                    AdmissionTransactions = 1,
                    ConcurrentHistoryReadTransactions = querySamples.Length,
                    ReopenStoreTransactions = 4,
                    Limitations = "Counts are modeled from one transaction per observed production method boundary. The store has no transaction recorder; raw-ingress initialization internals and independent evidence SQL are excluded. GetHistoryAsync uses a coherent deferred read transaction."
                },
                FourReaderEvidence = new
                {
                    RequestedReaders = ReaderCount,
                    concurrency.MaximumConcurrentReaders,
                    concurrency.QueriesStartedDuringWriterPhase,
                    TotalSuccessfulQueries = querySamples.Length,
                    Errors = 0,
                    Readers = readerResults.Select(static result => new
                    {
                        result.ReaderId,
                        result.QueryCount,
                        WriterPhaseQueryCount = result.WriterPhaseLatencyMilliseconds.Length,
                        result.MaximumPageCount,
                        result.FinalPageCount,
                        result.FinalNewestRevisionNumber,
                        result.FinalOldestRevisionNumber
                    }).ToArray()
                },
                DurabilityAndCorrectness = new
                {
                    ExactRowsBeforeReopen = beforeReopen.Counts,
                    ExactRowsAfterReopen = afterReopen.Counts,
                    ExpectedAggregateRecords,
                    LogicalOrderedSha256BeforeReopen = beforeReopen.LogicalOrderedSha256,
                    LogicalOrderedSha256AfterReopen = afterReopen.LogicalOrderedSha256,
                    ChecksumStable = string.Equals(
                        beforeReopen.LogicalOrderedSha256,
                        afterReopen.LogicalOrderedSha256,
                        StringComparison.Ordinal),
                    BeforeReopenPragmas = beforeReopen.Pragmas,
                    AfterReopenPragmas = afterReopen.Pragmas,
                    LatestHistoryQueryPlan = beforeReopen.LatestHistoryQueryPlan,
                    QueryPlanUsesIndex = beforeReopen.LatestHistoryQueryPlan.Any(
                        static detail => detail.Contains("INDEX", StringComparison.OrdinalIgnoreCase)),
                    ActiveRevisionIdBeforeReopen = beforeReopen.ActiveRevisionId,
                    ActiveRevisionIdAfterReopen = afterReopen.ActiveRevisionId,
                    PendingRevisionIdBeforeReopen = beforeReopen.PendingRevisionId,
                    PendingRevisionIdAfterReopen = afterReopen.PendingRevisionId,
                    ActiveRevisionNumber = current.ActiveRevision.RevisionNumber,
                    PendingRevisionNumber = current.PendingRevision.RevisionNumber,
                    AdmissionId = "admission-0001",
                    RestartRecoveryMilliseconds = recoveryElapsed.TotalMilliseconds,
                    PostRecoveryValidationMilliseconds = reopenValidationElapsed.TotalMilliseconds,
                    ReopenedHistoryRows = reopenedHistory.Count,
                    ReopenedPreviewSha256 = reopenedPreview!.ExpansionSha256
                },
                InterruptedActivationCoverage = new
                {
                    Disposition = "A SQLite trigger aborted ActivateAsync for pending revision 303 after the state update reached the activation-audit insert but before commit. The transaction rolled back: revision 303 remained pending while revision 1 remained active, then both identities and the activation count survived a fresh store initialization unchanged.",
                    ActiveRevisionIdBeforeReopen = beforeReopen.ActiveRevisionId,
                    ActiveRevisionIdAfterReopen = afterReopen.ActiveRevisionId,
                    PendingRevisionIdBeforeReopen = beforeReopen.PendingRevisionId,
                    PendingRevisionIdAfterReopen = afterReopen.PendingRevisionId,
                    ActivationRowsBeforeReopen = beforeReopen.Counts.Activations,
                    ActivationRowsAfterReopen = afterReopen.Counts.Activations,
                    FaultInjection = "A temporary persistent BEFORE INSERT trigger raises SQLITE_CONSTRAINT only for idempotency key interrupted-activation. The trigger is removed immediately after the expected failure.",
                    FocusedTests = new[]
                    {
                        "HVO.SkyMonitor.CameraAgent.Tests.Scheduling.SqliteCaptureScheduleStoreTests.StageActivateAndOneShotConsumption_AreDurableAndIdempotent",
                        "HVO.SkyMonitor.CameraAgent.Tests.Scheduling.CaptureScheduleRuntimeCoordinatorTests.Activation_WaitsForPublicationBoundaryAndCancelsOldRevision"
                    },
                    CoverageStatement = "The workload covers restart recovery before activation commit; focused tests cover committed activation durability and publication-boundary behavior."
                }
            };

            await File.WriteAllTextAsync(
                evidencePath,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions) + Environment.NewLine).ConfigureAwait(false);
        }
        finally
        {
            if (readerCancellation is not null)
            {
                await readerCancellation.CancelAsync().ConfigureAwait(false);
            }
            if (readerTasks.Length > 0)
            {
                try
                {
                    _ = await Task.WhenAll(readerTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (readerCancellation?.IsCancellationRequested == true)
                {
                }
            }
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
            writer?.Dispose();
            readersReady?.Dispose();
            readerCancellation?.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CaptureSchedulePreview Expand(
        CaptureScheduleDefinition definition,
        CameraModuleConfig configuration,
        AstronomyEngineSolarEventCalculator calculator)
        => CaptureScheduleIntervalExpander.Expand(
            definition,
            PreviewFirstDate,
            PreviewDayCount,
            TimeZoneInfo.Utc,
            configuration.Observatory,
            calculator);

    private static async Task<ReaderResult> RunReaderAsync(
        SqliteCaptureScheduleStore store,
        int readerId,
        Task start,
        SemaphoreSlim ready,
        ThreadAllocationTracker allocationTracker,
        ReaderConcurrencyTracker concurrency,
        Func<int> readWriterProgress,
        Func<int> readWriterRunning,
        CancellationToken cancellationToken)
    {
        ready.Release();
        await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        var samples = new List<double>(ReaderQueriesPerReader);
        var writerPhaseSamples = new List<double>(ReaderQueriesPerReader);
        var maximumPageCount = 0;
        IReadOnlyList<CaptureScheduleRevisionSnapshot> finalPage = [];

        for (var queryIndex = 0; queryIndex < ReaderQueriesPerReader; queryIndex++)
        {
            var targetProgress = Math.Max(1, (queryIndex + 1) * RevisionCount / ReaderQueriesPerReader);
            while (readWriterProgress() < targetProgress && readWriterRunning() != 0)
            {
                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }

            var startedDuringWriterPhase = readWriterRunning() != 0;
            concurrency.Enter(startedDuringWriterPhase);
            try
            {
                finalPage = await MeasureAsync(
                    () => store.GetHistoryAsync(HistoryPageSize, cancellationToken),
                    samples,
                    secondarySamples: null,
                    allocationTracker).ConfigureAwait(false);
            }
            finally
            {
                concurrency.Exit();
            }
            if (startedDuringWriterPhase)
            {
                writerPhaseSamples.Add(samples[^1]);
            }
            AssertOrderedLatestHistory(finalPage);
            maximumPageCount = Math.Max(maximumPageCount, finalPage.Count);
        }

        return new ReaderResult(
            readerId,
            samples.ToArray(),
            writerPhaseSamples.ToArray(),
            maximumPageCount,
            finalPage.Count,
            finalPage[0].RevisionNumber,
            finalPage[^1].RevisionNumber);
    }

    private static async Task<double[]> RunUnpacedReaderAsync(
        SqliteCaptureScheduleStore store,
        Task start,
        CancellationToken cancellationToken)
    {
        await start.WaitAsync(cancellationToken).ConfigureAwait(false);
        var samples = new double[ReaderQueriesPerReader];
        for (var index = 0; index < samples.Length; index++)
        {
            var started = Stopwatch.GetTimestamp();
            var page = await store.GetHistoryAsync(HistoryPageSize, cancellationToken).ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.HasCount(HistoryPageSize, page);
        }
        return samples;
    }

    private static async Task<T> MeasureAsync<T>(
        Func<Task<T>> operation,
        List<double> samples,
        List<double>? secondarySamples,
        ThreadAllocationTracker allocationTracker)
    {
        var startingThread = Environment.CurrentManagedThreadId;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        var result = await operation().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        samples.Add(elapsed);
        secondarySamples?.Add(elapsed);
        allocationTracker.Record(startingThread, allocatedBefore);
        return result;
    }

    private static async Task MeasureAsync(
        Func<Task> operation,
        List<double> samples,
        List<double>? secondarySamples,
        ThreadAllocationTracker allocationTracker)
    {
        var startingThread = Environment.CurrentManagedThreadId;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        await operation().ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        samples.Add(elapsed);
        secondarySamples?.Add(elapsed);
        allocationTracker.Record(startingThread, allocatedBefore);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The method selects between two fixed trigger statements and accepts no SQL input.")]
    private static async Task SetActivationAbortTriggerAsync(string databasePath, bool enabled)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = enabled
            ? """
                CREATE TRIGGER issue_207_interrupt_activation
                BEFORE INSERT ON capture_schedule_activations
                WHEN NEW.idempotency_key = 'interrupted-activation'
                BEGIN
                    SELECT RAISE(ABORT, 'issue-207 interrupted activation');
                END;
                """
            : "DROP TRIGGER issue_207_interrupt_activation;";
        _ = await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<DatabaseEvidence> ReadDatabaseEvidenceAsync(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using (var settings = connection.CreateCommand())
        {
            settings.CommandText = "PRAGMA foreign_keys=ON;";
            _ = await settings.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var counts = new ScheduleRowCounts(
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_schedule_revisions;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_schedule_commands;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_schedule_activations;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_schedule_expansions;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_schedule_intervals;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "SELECT COUNT(*) FROM capture_schedule_admissions;").ConfigureAwait(false));
        var integrityResults = await ReadStringsAsync(connection, "PRAGMA integrity_check;", 0).ConfigureAwait(false);
        var foreignKeyFailures = await CountRowsAsync(connection, "PRAGMA foreign_key_check;").ConfigureAwait(false);
        var pragmas = new PragmaEvidence(
            integrityResults,
            foreignKeyFailures,
            await ScalarLongAsync(connection, "PRAGMA foreign_keys;").ConfigureAwait(false),
            await ScalarStringAsync(connection, "PRAGMA journal_mode;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "PRAGMA user_version;").ConfigureAwait(false),
            await ScalarLongAsync(connection, "PRAGMA synchronous;").ConfigureAwait(false));
        var queryPlan = await ReadStringsAsync(
            connection,
            "EXPLAIN QUERY PLAN SELECT revision_id FROM capture_schedule_revisions " +
            "ORDER BY revision_number DESC LIMIT 100;",
            3).ConfigureAwait(false);
        var activeRevisionId = await ScalarStringAsync(
            connection, "SELECT active_revision_id FROM capture_schedule_state WHERE state_key=1;")
            .ConfigureAwait(false);
        var pendingRevisionId = await NullableScalarStringAsync(
            connection, "SELECT pending_revision_id FROM capture_schedule_state WHERE state_key=1;")
            .ConfigureAwait(false);

        return new DatabaseEvidence(
            counts,
            await ComputeLogicalChecksumAsync(connection).ConfigureAwait(false),
            pragmas,
            queryPlan,
            activeRevisionId,
            pendingRevisionId,
            await ScalarStringAsync(connection, "SELECT sqlite_version();").ConfigureAwait(false));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only the fixed SQL statements declared in this method are executed.")]
    private static async Task<string> ComputeLogicalChecksumAsync(SqliteConnection connection)
    {
        var queries = new[]
        {
            ("revisions", "SELECT revision_id,revision_number,profile_json,profile_sha256,schedule_sha256,source,actor,reason,created_unix_ms FROM capture_schedule_revisions ORDER BY revision_number;"),
            ("commands", "SELECT idempotency_key,command_kind,payload_sha256,result_active_revision_id,result_pending_revision_id,result_state_version,result_last_evaluated_unix_ms,created_unix_ms,completed_unix_ms FROM capture_schedule_commands ORDER BY idempotency_key;"),
            ("activations", "SELECT activation_id,idempotency_key,from_revision_id,to_revision_id,actor,reason,state_version,activated_unix_ms FROM capture_schedule_activations ORDER BY activation_id;"),
            ("expansions", "SELECT expansion_key,expansion_sha256,revision_id,deployment_location_id,deployment_location_version,preview_start_unix_ms,preview_end_unix_ms,expansion_algorithm_version,time_zone_rule_sha256,solar_algorithm_version,created_unix_ms FROM capture_schedule_expansions ORDER BY expansion_key;"),
            ("intervals", "SELECT expansion_key,ordinal,interval_id,source,disposition,start_unix_ms,end_unix_ms,local_date,setpoint_profile_id,solar_algorithm_version FROM capture_schedule_intervals ORDER BY expansion_key,ordinal;"),
            ("admissions", "SELECT admission_id,revision_id,override_id,decision_unix_ms,created_unix_ms FROM capture_schedule_admissions ORDER BY admission_id;")
        };
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (name, sql) in queries)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(name));
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                var values = new object?[reader.FieldCount];
                for (var ordinal = 0; ordinal < values.Length; ordinal++)
                {
                    values[ordinal] = await reader.IsDBNullAsync(ordinal).ConfigureAwait(false)
                        ? null
                        : reader.GetValue(ordinal);
                }
                hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(values));
                hash.AppendData("\n"u8);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AssertDatabaseInvariants(DatabaseEvidence evidence)
    {
        Assert.AreEqual(RevisionCount, evidence.Counts.Revisions);
        Assert.AreEqual(StageCommandCount, evidence.Counts.Commands);
        Assert.AreEqual(1, evidence.Counts.Activations);
        Assert.AreEqual(RevisionCount, evidence.Counts.Expansions);
        Assert.AreEqual(RevisionCount * PreviewDayCount, evidence.Counts.Intervals);
        Assert.AreEqual(1, evidence.Counts.Admissions);
        Assert.AreEqual(ExpectedAggregateRecords, evidence.Counts.Aggregate);
        Assert.HasCount(1, evidence.Pragmas.IntegrityCheckResults);
        Assert.AreEqual("ok", evidence.Pragmas.IntegrityCheckResults[0]);
        Assert.AreEqual(0, evidence.Pragmas.ForeignKeyCheckRows);
        Assert.AreEqual(1, evidence.Pragmas.ForeignKeysEnabled);
        Assert.AreEqual("wal", evidence.Pragmas.JournalMode, ignoreCase: true);
        Assert.AreEqual(8, evidence.Pragmas.UserVersion);
        Assert.AreEqual(2, evidence.Pragmas.Synchronous);
        Assert.IsTrue(evidence.LatestHistoryQueryPlan.Any(
            static detail => detail.Contains("INDEX", StringComparison.OrdinalIgnoreCase)));
    }

    private static void AssertOrderedLatestHistory(IReadOnlyList<CaptureScheduleRevisionSnapshot> history)
    {
        Assert.IsLessThanOrEqualTo(HistoryPageSize, history.Count);
        for (var index = 1; index < history.Count; index++)
        {
            Assert.AreEqual(history[index - 1].RevisionNumber - 1, history[index].RevisionNumber);
        }
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
        => Convert.ToInt64(await ScalarAsync(connection, sql).ConfigureAwait(false), CultureInfo.InvariantCulture);

    private static async Task<string> ScalarStringAsync(SqliteConnection connection, string sql)
        => Convert.ToString(await ScalarAsync(connection, sql).ConfigureAwait(false), CultureInfo.InvariantCulture)
            ?? throw new InvalidDataException("A required SQLite scalar was null.");

    private static async Task<string?> NullableScalarStringAsync(SqliteConnection connection, string sql)
    {
        var value = await ScalarAsync(connection, sql).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant SQL statements are passed to this helper.")]
    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync().ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant SQL statements are passed to this helper.")]
    private static async Task<long> CountRowsAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var count = 0L;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            count++;
        }
        return count;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only internal constant SQL statements are passed to this helper.")]
    private static async Task<string[]> ReadStringsAsync(SqliteConnection connection, string sql, int ordinal)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        var values = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            values.Add(reader.GetString(ordinal));
        }
        return values.ToArray();
    }

    private static OperationMeasurement CreateMeasurement(IEnumerable<double> values, TimeSpan batchElapsed)
    {
        var samples = values.Order().ToArray();
        return new OperationMeasurement(
            samples.Length,
            Percentile(samples, 0.50),
            Percentile(samples, 0.95),
            samples[0],
            samples[^1],
            batchElapsed.TotalMilliseconds,
            samples.Length / batchElapsed.TotalSeconds);
    }

    private static LatencySummary CreateLatencySummary(IEnumerable<double> values)
    {
        var samples = values.Order().ToArray();
        return new LatencySummary(
            samples.Length,
            Percentile(samples, 0.50),
            samples.Length >= 30 ? Percentile(samples, 0.95) : null,
            samples[0],
            samples[^1]);
    }

    private static double Percentile(double[] sortedSamples, double percentile)
        => sortedSamples[(int)Math.Ceiling(percentile * sortedSamples.Length) - 1];

    private static FileSizeEvidence ReadFileSizes(string databasePath)
        => new(
            FileSize(databasePath),
            FileSize(string.Concat(databasePath, "-wal")),
            FileSize(string.Concat(databasePath, "-shm")));

    private static long FileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static CameraModuleConfig Configuration(CaptureScheduleDefinition schedule)
        => new CameraModuleConfig(
            new ObservatoryLocation(35, -114, 1_000, "UTC"),
            new CameraModuleDescriptor("test"),
            new CameraRigConfig(
                new SensorProfile("test", 2, 2, 5, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("Perspective", 50, 10, 0),
                new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(5),
                    1,
                    10)),
            CapturePipelineConfig.Empty)
        {
            Schedule = schedule
        };

    private static CaptureScheduleDefinition Definition(int revisionIndex)
    {
        var profileId = "night";
        var windows = Enum.GetValues<DayOfWeek>()
            .Select(day => new CaptureWeeklyScheduleWindow(
                $"window-{(int)day}",
                day,
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(1, 0)),
                new CaptureScheduleBoundary(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(2, 0)),
                profileId))
            .ToArray();
        return new CaptureScheduleDefinition(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile(
                profileId,
                TimeSpan.FromSeconds(5),
                100 + revisionIndex,
                TimeSpan.FromSeconds(10))],
            windows);
    }

    private static string GetEvidenceRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION") ?? "working-tree";
        if (string.IsNullOrWhiteSpace(revision) || revision is "." or ".." ||
            revision.Any(static character => !IsPathSafeRevisionCharacter(character)))
        {
            throw new InvalidOperationException(
                "HVO_EVIDENCE_REVISION must contain only ASCII letters, digits, period, underscore, or hyphen.");
        }
        return revision;
    }

    private static bool IsPathSafeRevisionCharacter(char character)
        => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-';

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

    private static AssemblyEvidence ReadAssemblyEvidence(Type type)
    {
        var location = type.Assembly.Location;
        using var stream = File.OpenRead(location);
        return new AssemblyEvidence(
            type.Assembly.GetName().Name ?? type.FullName ?? "unknown",
            location,
            Convert.ToHexString(SHA256.HashData(stream)));
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

    private static JsonSerializerOptions CreateEvidenceJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class MemoizedJournalInitializer(string root) : IRawCaptureIngress
    {
        private readonly object _sync = new();
        private Task? _initialization;
        private int _initializeCallCount;
        private int _physicalInitializationCount;

        public int InitializeCallCount => Volatile.Read(ref _initializeCallCount);

        public int PhysicalInitializationCount => Volatile.Read(ref _physicalInitializationCount);

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _initializeCallCount);
            Task initialization;
            lock (_sync)
            {
                initialization = _initialization ??= InitializeCoreAsync();
            }
            return new ValueTask(initialization.WaitAsync(cancellationToken));
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        private async Task InitializeCoreAsync()
        {
            Interlocked.Increment(ref _physicalInitializationCount);
            var journal = new SqliteRawCaptureJournal(
                Path.Combine(root, "journal", "raw-ingress.db"),
                busyTimeoutSeconds: 30);
            await journal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class ThreadAllocationTracker
    {
        private readonly object _sync = new();
        private long _bytes;
        private int _stableSamples;
        private int _discardedThreadHopSamples;

        public void Record(int startingThread, long allocatedBefore)
        {
            lock (_sync)
            {
                if (Environment.CurrentManagedThreadId == startingThread)
                {
                    _bytes += Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    _stableSamples++;
                }
                else
                {
                    _discardedThreadHopSamples++;
                }
            }
        }

        public ThreadAllocationEvidence ToEvidence()
        {
            lock (_sync)
            {
                return new ThreadAllocationEvidence(
                    _stableSamples,
                    _discardedThreadHopSamples,
                    _bytes,
                    _stableSamples == 0 ? null : _bytes / (double)_stableSamples);
            }
        }
    }

    private sealed class ReaderConcurrencyTracker
    {
        private int _activeReaders;
        private int _maximumConcurrentReaders;
        private int _queriesStartedDuringWriterPhase;

        public int MaximumConcurrentReaders => Volatile.Read(ref _maximumConcurrentReaders);

        public int QueriesStartedDuringWriterPhase => Volatile.Read(ref _queriesStartedDuringWriterPhase);

        public void Enter(bool writerRunning)
        {
            var active = Interlocked.Increment(ref _activeReaders);
            var observedMaximum = Volatile.Read(ref _maximumConcurrentReaders);
            while (active > observedMaximum)
            {
                var prior = Interlocked.CompareExchange(ref _maximumConcurrentReaders, active, observedMaximum);
                if (prior == observedMaximum)
                {
                    break;
                }
                observedMaximum = prior;
            }
            if (writerRunning)
            {
                Interlocked.Increment(ref _queriesStartedDuringWriterPhase);
            }
        }

        public void Exit() => Interlocked.Decrement(ref _activeReaders);
    }

    private sealed record ReaderResult(
        int ReaderId,
        double[] LatencyMilliseconds,
        double[] WriterPhaseLatencyMilliseconds,
        int MaximumPageCount,
        int FinalPageCount,
        long FinalNewestRevisionNumber,
        long FinalOldestRevisionNumber)
    {
        public int QueryCount => LatencyMilliseconds.Length;
    }

    private sealed record OperationMeasurement(
        int Operations,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds,
        double AggregateServiceMilliseconds,
        double SerialServiceOperationsPerSecond);

    private sealed record LatencySummary(
        int Operations,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MinimumMilliseconds,
        double MaximumMilliseconds);

    private sealed record ThreadAllocationEvidence(
        int StableThreadSamples,
        int DiscardedThreadHopSamples,
        long StableThreadAllocatedBytes,
        double? StableThreadAllocatedBytesPerSample);

    private sealed record FileSizeEvidence(long DatabaseBytes, long WalBytes, long ShmBytes);

    private sealed record ScheduleRowCounts(
        long Revisions,
        long Commands,
        long Activations,
        long Expansions,
        long Intervals,
        long Admissions)
    {
        public long Aggregate => Revisions + Commands + Activations + Expansions + Intervals + Admissions;
    }

    private sealed record PragmaEvidence(
        string[] IntegrityCheckResults,
        long ForeignKeyCheckRows,
        long ForeignKeysEnabled,
        string JournalMode,
        long UserVersion,
        long Synchronous);

    private sealed record DatabaseEvidence(
        ScheduleRowCounts Counts,
        string LogicalOrderedSha256,
        PragmaEvidence Pragmas,
        string[] LatestHistoryQueryPlan,
        string ActiveRevisionId,
        string? PendingRevisionId,
        string SqliteVersion);

    private sealed record GitEvidence(
        string Head,
        string Branch,
        bool Dirty,
        string DirtyDiffSha256);

    private sealed record AssemblyEvidence(
        string Name,
        string Path,
        string Sha256);
}
