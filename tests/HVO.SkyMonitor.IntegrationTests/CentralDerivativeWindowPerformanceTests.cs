using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class CentralDerivativeWindowPerformanceTests
{
    private const string ArtifactBucket = "skymonitor-artifacts";
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };
    private static readonly TransientTemporalPosition[] OrderedTransientPositions =
    [
        TransientTemporalPosition.NMinus2,
        TransientTemporalPosition.NMinus1,
        TransientTemporalPosition.N,
        TransientTemporalPosition.NPlus1,
        TransientTemporalPosition.NPlus2
    ];

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Phase10HistoryScaling_RecordsCanonicalEvidence()
    {
        Assert.IsTrue(AppContext.BaseDirectory.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal),
            "Canonical issue #101 scaling evidence must be collected from a Release build.");
        var repositoryRoot = FindRepositoryRoot();
        var commit = RunGit(repositoryRoot, "rev-parse", "HEAD");
        var dirty = !string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status", "--porcelain"));
        var evidenceRevision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? (dirty ? "local-dirty" : commit);
        var builder = new SqlConnectionStringBuilder(AssemblyHooks.Fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorWindowPerformance_{Guid.NewGuid():N}"
        };
        var counter = new CountingCommandInterceptor();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .AddInterceptors(counter)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(options);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        try
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
            var results = new List<WindowScalingResult>();
            foreach (var historySize in new[] { 1_000, 10_000, 100_000 })
            {
                results.Add(await MeasureHistoryAsync(db, telemetry, counter, historySize).ConfigureAwait(false));
            }

            var evidence = new WindowPerformanceEvidence(
                Schema: "hvo-central-window-performance-v1",
                Revision: new(
                    evidenceRevision,
                    RunGit(repositoryRoot, "merge-base", "HEAD", "main"),
                    commit,
                    RunGit(repositoryRoot, "branch", "--show-current"),
                    dirty,
                    CreateDirtyFingerprint(repositoryRoot)),
                RecordedAtUtc: DateTimeOffset.UtcNow,
                Environment: new(
                    Environment.OSVersion.ToString(),
                    System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    Environment.Version.ToString(),
                    "Release",
                    "SQL Server Testcontainer; no MinIO reads during resolution"),
                Workload: "Thirty measured centered required N-2..N+2 windows after five warmups per history size",
                Results: results,
                Interpretation: "The selected row count and SQL command count remain fixed across 1K/10K/100K history. "
                    + "Latency, CPU, allocation, and RSS are absolute candidate baselines because no pre-window resolver exists.");
            var revision = evidence.Revision.EvidenceDirectoryRevision.Length > 12
                ? evidence.Revision.EvidenceDirectoryRevision[..12]
                : evidence.Revision.EvidenceDirectoryRevision;
            var directory = Path.Combine(
                FindRepositoryRoot(),
                "tests",
                "HVO.SkyMonitor.IntegrationTests",
                "TestResults",
                "central-window-processing",
                revision);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "logichost-window-performance.json");
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(evidence, EvidenceJsonOptions))
                .ConfigureAwait(false);
            TestContext.WriteLine($"Window performance evidence: {path}");

            Assert.IsTrue(results.All(result => result.SelectedInputs == 150));
            Assert.AreEqual(1, results.Select(result => result.SqlCommands).Distinct().Count(),
                "Resolution SQL command count must depend on the fixed window, not history size.");
            Assert.IsTrue(results.All(result => result.SequenceIndexPresent));
            Assert.IsTrue(results.All(result => result.SequenceIndexUsedByPlan));
            Assert.IsTrue(results.All(result => result.ObjectRequests == 0));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Phase10P1ToP4_RecordsCanonicalEvidence()
    {
        var fixture = AssemblyHooks.Fixture;
        _ = fixture.Factory.Services;
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.Ordinal,
            "Canonical issue #101 evidence must be collected from a Release build.");
        var runId = Guid.NewGuid().ToString("N");
        var harnessStarted = Stopwatch.GetTimestamp();
        using var telemetry = new CentralDerivativeWorkerTelemetry();

        var p1 = await MeasureTransitionsAsync(
            fixture.SqlServerConnectionString, telemetry, runId).ConfigureAwait(false);
        var p2 = new List<SchedulerNotificationResult>();
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            p2.Add(await MeasureDuplicateNotificationsAsync(
                fixture, telemetry, runId, concurrency).ConfigureAwait(false));
        }

        var p4 = await MeasureLeaseAndRetentionRecoveryAsync(fixture, runId).ConfigureAwait(false);
        var p3 = new List<RollingExecutionResult>();
        p3.Add(await MeasureRollingExecutionAsync(
            fixture, runId, "W1", 1936, 1216, CameraPixelFormat.Mono16).ConfigureAwait(false));
        p3.Add(await MeasureRollingExecutionAsync(
            fixture, runId, "W2", 3096, 2080, CameraPixelFormat.BayerRggb16).ConfigureAwait(false));

        var repositoryRoot = FindRepositoryRoot();
        var commit = RunGit(repositoryRoot, "rev-parse", "HEAD");
        var branch = RunGit(repositoryRoot, "branch", "--show-current");
        var dirty = !string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status", "--porcelain"));
        var revision = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? (dirty ? "local-dirty" : commit);
        var sqlVersion = await ReadSqlVersionAsync(fixture.SqlServerConnectionString).ConfigureAwait(false);
        var evidence = new
        {
            Schema = "hvo-central-window-performance-v2",
            Issue = 101,
            Revision = new
            {
                EvidenceDirectoryRevision = revision,
                Base = RunGit(repositoryRoot, "merge-base", "HEAD", "main"),
                Candidate = commit,
                Branch = branch,
                Dirty = dirty,
                DirtyFingerprintSha256 = CreateDirtyFingerprint(repositoryRoot)
            },
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Command = "dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --no-build --configuration Release --filter \"FullyQualifiedName~CentralDerivativeWindowPerformanceTests\"",
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                BuildConfiguration = "Release",
                AppContext.BaseDirectory,
                Environment.ProcessorCount,
                ProcessorModel = ReadProcessorModel(),
                AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Storage = "Docker-backed SQL Server and MinIO Testcontainers",
                SqlServerVersion = sqlVersion,
                MinioVersion = "RELEASE.2025-09-07T16-13-09Z",
                fixture.MinioEndpoint,
                SqlServer = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString).DataSource
            },
            P0 = new
            {
                EvidenceFile = "logichost-window-performance.json",
                Test = nameof(Phase10HistoryScaling_RecordsCanonicalEvidence),
                PreservedWorkloads = new[] { 1_000, 10_000, 100_000 },
                Note = "P0 remains a separate retained evidence file because its 100K history setup is intentionally not repeated by this under-five-minute P1-P4 gate."
            },
            P1 = p1,
            P2 = new
            {
                NotificationsPerConcurrency = 1_000,
                ResolverConcurrency = new[] { 1, 4, 8 },
                Measurements = p2
            },
            P3 = new
            {
                Boundary = "Production SQL claim through five verified MinIO input reads, rolling recipe execution, output publication, ordered lineage persistence, and durable completion.",
                Measurements = p3
            },
            P4 = p4,
            Method = new
            {
                Timing = "Stopwatch monotonic elapsed time; nearest-rank median/p95/maximum after declared warmups.",
                Resources = "Process.TotalProcessorTime, GC.GetTotalAllocatedBytes(true), and Process.WorkingSet64 cover the in-process test/LogicHost only.",
                TimeControl = "Deadline and lease expiry are injected as durable UTC values; no wall-clock lease or timeout waits are used.",
                CollisionAccounting = "SQL error 1205 is counted as a deadlock; 2601/2627 are counted as uniqueness collisions and retried by the notification driver.",
                InputTraffic = "Logical object requests and bytes come from the frozen durable input rows consumed by the production input reader; output verification reads are reported separately."
            },
            HarnessElapsedMilliseconds = Stopwatch.GetElapsedTime(harnessStarted).TotalMilliseconds
        };
        var directory = GetEvidenceDirectory(revision);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "logichost-window-p1-p4-performance.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, EvidenceJsonOptions))
            .ConfigureAwait(false);
        TestContext.WriteLine($"Window P1-P4 performance evidence: {path}");
        TestContext.WriteLine($"Harness elapsed: {evidence.HarnessElapsedMilliseconds:F0} ms");

        Assert.IsTrue(evidence.HarnessElapsedMilliseconds < TimeSpan.FromMinutes(5).TotalMilliseconds,
            "The canonical P1-P4 harness must remain under five minutes.");
    }

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue116CentralTransientW2W3MW4_RecordsAcceptanceEvidence()
    {
        var testAssembly = typeof(CentralDerivativeWindowPerformanceTests).Assembly;
        Assert.AreEqual(Architecture.X64, RuntimeInformation.OSArchitecture,
            "Issue #116 acceptance evidence requires an x64 host.");
        Assert.AreEqual(Architecture.X64, RuntimeInformation.ProcessArchitecture,
            "Issue #116 acceptance evidence requires an x64 process; run with --arch x64.");
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.Ordinal,
            "Issue #116 acceptance evidence must be collected from a Release build.");
        Assert.AreEqual("Release", testAssembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
            "Issue #116 acceptance evidence requires a Release assembly identity.");

        var fixture = AssemblyHooks.Fixture;
        _ = fixture.Factory.Services;
        var runId = Guid.NewGuid().ToString("N");
        var harnessStarted = Stopwatch.GetTimestamp();
        var centralIoW2 = await MeasureRollingExecutionAsync(
            fixture, runId, "W2", 3096, 2080, CameraPixelFormat.BayerRggb16).ConfigureAwait(false);
        var transientW2 = await MeasureCentralTransientExecutionAsync(fixture, runId).ConfigureAwait(false);

        var w3m = await MeasureTransientMetadataScalingAsync(fixture.SqlServerConnectionString)
            .ConfigureAwait(false);

        var w4 = new List<TransientSchedulingResult>();
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            w4.Add(await MeasureTransientDuplicateSchedulingAsync(
                fixture, runId, concurrency, warmups: 20, measurements: 200).ConfigureAwait(false));
        }
        var recovery = await MeasureLeaseAndRetentionRecoveryAsync(fixture, runId).ConfigureAwait(false);

        var repositoryRoot = FindRepositoryRoot();
        var commit = RunGit(repositoryRoot, "rev-parse", "HEAD");
        var dirty = !string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status", "--porcelain"));
        var evidenceRevision = dirty
            ? "local-dirty"
            : Environment.GetEnvironmentVariable("GITHUB_SHA") ?? commit;
        var evidence = new
        {
            Schema = "hvo-central-transient-acceptance-performance-v1",
            Issue = 116,
            Revision = new
            {
                EvidenceDirectoryRevision = evidenceRevision,
                Base = RunGit(repositoryRoot, "merge-base", "HEAD", "main"),
                Candidate = commit,
                Branch = RunGit(repositoryRoot, "branch", "--show-current"),
                Dirty = dirty,
                DirtyFingerprintSha256 = CreateDirtyFingerprint(repositoryRoot)
            },
            RecordedAtUtc = DateTimeOffset.UtcNow,
            Command = "dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --configuration Release --arch x64 --filter FullyQualifiedName~CentralDerivativeWindowPerformanceTests.Issue116CentralTransientW2W3MW4_RecordsAcceptanceEvidence",
            Environment = new
            {
                Framework = RuntimeInformation.FrameworkDescription,
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Configuration = "Release",
                Environment.ProcessorCount,
                ProcessorModel = ReadProcessorModel(),
                AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                Storage = "Docker-backed SQL Server and MinIO Testcontainers",
                SqlServerVersion = await ReadSqlVersionAsync(fixture.SqlServerConnectionString).ConfigureAwait(false),
                MinioVersion = "RELEASE.2025-09-07T16-13-09Z",
                TestedAssemblies = new[]
                {
                    CreateAssemblyEvidence(testAssembly),
                    CreateAssemblyEvidence(typeof(CentralTransientValidationExecutor).Assembly)
                }
            },
            Boundary = new
            {
                Central = "Production SQL claim, frozen five-input window, verified MinIO reads, recipe execution, ordered lineage persistence, generic completion, duplicate scheduling, and lease recovery.",
                Detector = "The isolated candidate extraction/assessment component is emitted by TransientCandidateExtractionPerformanceTests.Issue116W2CandidateExtractionRecordsAcceptanceEvidence. Review both files; process-wide CPU/allocation measurements are intentionally not combined.",
                Baseline = "Net-new Central transient composition; before is N/A. These absolute candidate measurements establish the non-regression baseline."
            },
            Workloads = new
            {
                W2 = new
                {
                    Definition = "ASI178 3096x2080 RGGB16, 12,879,360 bytes; five warmups and 30 measurements.",
                    CentralTransientExecution = transientW2,
                    CentralIoControl = centralIoW2,
                    CandidateRate = transientW2.CandidatesPerFrame,
                    LiveBuffers = "Five frozen logical source inputs; production input reader verifies and transfers each object without retaining an unbounded queue."
                },
                W3M = new
                {
                    Definition = "10,000 metadata-only retrospective Central transient candidates; one bounded production batch schedules 100 jobs, 3,200 identity slots, resolves windows, and creates 100 claim attempts.",
                    TransientMetadata = w3m,
                    Complexity = "Retrospective selection is bounded to 100 and uses indexed artifact/job predicates; claim selection uses the durable status/availability index. Durable row counts and both plan hashes are recorded."
                },
                W4 = new
                {
                    Definition = "Concurrency 1/4/8; 20 warmups and 200 measured duplicate scheduling operations per level.",
                    Measurements = w4,
                    QueueInvariant = "Each level converges to one Central transient job, five ordered frozen inputs, and 32 durable opaque identity slots."
                }
            },
            Recovery = recovery,
            IoAccounting = new
            {
                Sql = "W3M records commands, selected rows, logical reads, plan SHA-256, and index use. W4 records deadlocks and uniqueness collisions.",
                TransientProtocol = transientW2.Protocol,
                TransientSelectedInputBytes = transientW2.SelectedInputBytes,
                TransientAttemptInputBytes = transientW2.AttemptInputBytes,
                TransientObjectReadDisposition = "Counts and bytes are observed at the production ICentralArtifactObjectReader boundary. Each verification reads and hashes the complete MinIO object, and each payload read transfers the complete object into the production input buffer; HTTP framing bytes are excluded.",
                RollingControl = new
                {
                    centralIoW2.LogicalInputObjectRequests,
                    centralIoW2.InputStatRequests,
                    centralIoW2.InputChecksumGetRequests,
                    centralIoW2.InputPayloadGetRequests,
                    centralIoW2.PhysicalInputTransferredBytes,
                    centralIoW2.InputBytes,
                    centralIoW2.OutputBytes
                },
                Filesystem = "N/A; the Central path uses SQL Server and MinIO, not host filesystem artifacts."
            },
            Correctness = new
            {
                Transient = new
                {
                    transientW2.InputChecksumSha256,
                    transientW2.TargetChecksumSha256,
                    transientW2.ExtractionReceiptChecksumsSha256,
                    transientW2.ExtractionReceiptCount,
                    transientW2.EventVersionCount,
                    transientW2.OrderedTemporalLineageVerified
                },
                RollingIoControl = new
                {
                    centralIoW2.InputChecksumSha256,
                    centralIoW2.OutputChecksumsSha256,
                    centralIoW2.OrderedLineageVerified,
                    centralIoW2.VerifiedOutputObjects
                },
                W3MRetrospectiveIndexUsed = w3m.RetrospectivePlan.IndexUsed,
                W3MClaimIndexUsed = w3m.ClaimPlan.IndexUsed,
                W4Converged = w4.All(item => item.FinalJobs == 1 && item.FinalInputRows == 5
                    && item.FinalIdentitySlots == 32),
                RecoveryTrials = recovery.Trials
            },
            HarnessElapsedMilliseconds = Stopwatch.GetElapsedTime(harnessStarted).TotalMilliseconds
        };
        var boundedRevision = evidenceRevision.Length > 12 ? evidenceRevision[..12] : evidenceRevision;
        var outputDirectory = Path.Combine(repositoryRoot, "TestResults", "issue-116", boundedRevision);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "central-w2-w3m-w4.json");
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(evidence, EvidenceJsonOptions))
            .ConfigureAwait(false);
        TestContext.WriteLine($"Issue #116 Central performance evidence: {outputPath}");

        Assert.AreEqual(12_879_360, transientW2.InputArtifactBytes);
        Assert.AreEqual(30, transientW2.MeasuredJobs);
        Assert.IsTrue(transientW2.OrderedTemporalLineageVerified);
        Assert.AreEqual(150, transientW2.Protocol.ObjectVerifications);
        Assert.AreEqual(150, transientW2.Protocol.ObjectPayloadReads);
        Assert.AreEqual(transientW2.SelectedInputBytes, transientW2.Protocol.ChecksumVerificationBytes);
        Assert.AreEqual(transientW2.SelectedInputBytes, transientW2.Protocol.PayloadReadBytes);
        Assert.AreEqual(0, transientW2.AttemptInputBytes,
            "Transient attempt input-byte accounting is a disclosed residual gap, not inferred evidence.");
        Assert.AreEqual(10_000, w3m.CandidateArtifacts);
        Assert.AreEqual(100, w3m.ScheduledJobs);
        Assert.AreEqual(3_200, w3m.IdentitySlots);
        Assert.IsTrue(w3m.RetrospectivePlan.IndexUsed);
        Assert.IsTrue(w3m.ClaimPlan.IndexUsed);
        Assert.AreEqual("1,4,8", string.Join(',', w4.Select(item => item.Concurrency)));
        Assert.IsTrue(evidence.Correctness.W4Converged);
        Assert.AreEqual(5, recovery.Trials);
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<TransientMetadataScalingResult> MeasureTransientMetadataScalingAsync(
        string connectionString)
    {
        const int candidateArtifacts = 10_000;
        const int batchSize = 100;
        const int claimWarmups = 20;
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = $"SkyMonitorIssue116TransientMetadata_{Guid.NewGuid():N}"
        };
        var counter = new CountingCommandInterceptor();
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .AddInterceptors(counter)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        using var telemetry = new CentralDerivativeWorkerTelemetry();
        try
        {
            await db.Database.MigrateAsync().ConfigureAwait(false);
            var agentId = $"issue-116-w3m-{Guid.NewGuid():N}";
            var rigId = $"{agentId}-rig";
            var devicePublicId = Guid.NewGuid();
            _ = await SeedHistoryAsync(db, candidateArtifacts, agentId, rigId, devicePublicId)
                .ConfigureAwait(false);
            _ = await db.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE artifact
                SET [ReceivedAtUtc] = DATEADD(millisecond,
                    CASE WHEN frame.[CaptureSequence] <= 2 THEN frame.[CaptureSequence] + 20000
                         ELSE frame.[CaptureSequence] END,
                    CAST('2026-01-01T00:00:00+00:00' AS datetimeoffset))
                FROM [CentralArtifacts] AS artifact
                INNER JOIN [CentralFrames] AS frame ON frame.[Id] = artifact.[CentralFrameId]
                WHERE frame.[AgentId] = {{agentId}};
                """).ConfigureAwait(false);
            await EnrichSelectedFramesAsync(db, agentId, Enumerable.Range(3, batchSize).Select(value => (long)value))
                .ConfigureAwait(false);
            _ = await db.Database.ExecuteSqlRawAsync(
                "UPDATE STATISTICS [CentralArtifacts] WITH FULLSCAN; UPDATE STATISTICS [CentralDerivativeJobs] WITH FULLSCAN;")
                .ConfigureAwait(false);

            var transientOptions = new CentralTransientOptions
            {
                Mode = TransientDetectorExecutionMode.Central,
                StarMaximumMagnitude = -30
            };
            var catalog = new TransientOnlyRecipeCatalog(transientOptions);
            var recipe = catalog.GetRequiredRecipes(FrameArtifactRole.Raw).Single();
            var retrospectiveSql = CentralTransientRetrospectiveScheduler.CreateRetrospectiveCandidateQuery(
                    db, FrameArtifactRole.Raw, recipe, recipe.Transient!.ExecutionOptionsIdentitySha256)
                .OrderBy(artifact => artifact.ReceivedAtUtc)
                .ThenBy(artifact => artifact.Id)
                .Select(artifact => new { artifact.DevicePublicId, artifact.ArtifactId })
                .Take(batchSize)
                .ToQueryString();
            var retrospectivePlan = await MeasureSqlTextEvidenceAsync(
                builder.ConnectionString,
                retrospectiveSql,
                "IX_CentralArtifacts_ObjectState_ReconstructionState_ReceivedAtUtc")
                .ConfigureAwait(false);
            var resolver = new CentralDerivativeWindowResolver(
                db, telemetry, TimeProvider.System, NullLogger<CentralDerivativeWindowResolver>.Instance);
            var scheduler = new CentralDerivativeJobScheduler(db, catalog, resolver);
            var retrospective = new CentralTransientRetrospectiveScheduler(
                db,
                scheduler,
                catalog,
                Options.Create(transientOptions),
                telemetry,
                NullLogger<CentralTransientRetrospectiveScheduler>.Instance);

            counter.Reset();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var scheduleCpuBefore = process.TotalProcessorTime;
            var scheduleAllocationsBefore = GC.GetTotalAllocatedBytes(true);
            var scheduleStarted = Stopwatch.GetTimestamp();
            await retrospective.ScheduleBatchAsync(DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            var scheduleElapsed = Stopwatch.GetElapsedTime(scheduleStarted);
            process.Refresh();
            var scheduleCpu = process.TotalProcessorTime - scheduleCpuBefore;
            var scheduleAllocations = GC.GetTotalAllocatedBytes(true) - scheduleAllocationsBefore;
            var scheduleStatements = counter.Count;
            db.ChangeTracker.Clear();

            var jobIds = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.RecipeName == CentralTransientRuntime.RecipeName
                    && job.SourceArtifact!.Frame!.AgentId == agentId)
                .Select(job => job.Id).ToArrayAsync().ConfigureAwait(false);
            var scheduledJobs = jobIds.Length;
            var identitySlots = await db.CentralTransientValidationIdentitySlots.CountAsync(slot =>
                jobIds.Contains(slot.CentralDerivativeJobId)).ConfigureAwait(false);
            var inputRows = await db.CentralDerivativeJobInputs.CountAsync(input =>
                jobIds.Contains(input.CentralDerivativeJobId)).ConfigureAwait(false);
            var canonicalRows = await db.CentralDerivativeJobCanonicalInputs.CountAsync(input =>
                jobIds.Contains(input.CentralDerivativeJobId)).ConfigureAwait(false);
            var requirementRows = await db.CentralDerivativeJobInputRequirements.CountAsync(requirement =>
                jobIds.Contains(requirement.CentralDerivativeJobId)).ConfigureAwait(false);
            var pendingJobs = await db.CentralDerivativeJobs.CountAsync(job =>
                jobIds.Contains(job.Id) && job.Status == CentralDerivativeJobStatus.Pending).ConfigureAwait(false);
            Assert.AreEqual(batchSize, scheduledJobs);
            Assert.AreEqual(batchSize * 32, identitySlots);
            Assert.AreEqual(batchSize * 5, inputRows);
            Assert.AreEqual(batchSize, canonicalRows);
            Assert.AreEqual(batchSize * 6, requirementRows);
            Assert.AreEqual(batchSize, pendingJobs);

            var claimSql = $$"""
                DECLARE @now datetimeoffset = '{{DateTimeOffset.UtcNow:O}}';
                SELECT TOP(1) job.[Id]
                FROM [CentralDerivativeJobs] AS job
                WHERE job.[Status] = N'Pending'
                  AND job.[AttemptCount] < job.[MaxAttempts]
                  AND job.[InputSetIdentitySha256] IS NOT NULL
                  AND job.[AvailableAtUtc] <= @now
                  AND EXISTS (SELECT 1 FROM [CentralDerivativeJobInputs] AS input
                              WHERE input.[CentralDerivativeJobId] = job.[Id])
                ORDER BY job.[AvailableAtUtc], job.[CreatedAtUtc], job.[Id];
                """;
            var claimPlan = await MeasureSqlTextEvidenceAsync(
                builder.ConnectionString,
                claimSql,
                "IX_CentralDerivativeJobs_Status_AvailableAtUtc_CreatedAtUtc_Id")
                .ConfigureAwait(false);

            counter.Reset();
            var claimSamples = new double[batchSize];
            process.Refresh();
            var claimCpuBefore = process.TotalProcessorTime;
            var claimAllocationsBefore = GC.GetTotalAllocatedBytes(true);
            for (var index = 0; index < batchSize; index++)
            {
                var claimStarted = Stopwatch.GetTimestamp();
                var lease = await new CentralDerivativeJobService(db, TimeProvider.System, telemetry)
                    .ClaimNextAsync($"issue-116-w3m-{index:D3}", TimeSpan.FromMinutes(10), CancellationToken.None)
                    .ConfigureAwait(false);
                claimSamples[index] = Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds;
                Assert.IsNotNull(lease);
                Assert.AreEqual(CentralTransientRuntime.RecipeName, lease.RecipeName);
                Assert.AreEqual(5, lease.Inputs!.Count);
                db.ChangeTracker.Clear();
            }
            process.Refresh();
            var claimCpu = process.TotalProcessorTime - claimCpuBefore;
            var claimAllocations = GC.GetTotalAllocatedBytes(true) - claimAllocationsBefore;
            var claimStatements = counter.Count;
            var attempts = await db.CentralDerivativeJobAttempts.CountAsync(attempt =>
                jobIds.Contains(attempt.CentralDerivativeJobId)).ConfigureAwait(false);
            var leased = await db.CentralDerivativeJobs.CountAsync(job =>
                jobIds.Contains(job.Id) && job.Status == CentralDerivativeJobStatus.Leased).ConfigureAwait(false);
            Assert.AreEqual(batchSize, attempts);
            Assert.AreEqual(batchSize, leased);
            var measuredClaimSamples = claimSamples.Skip(claimWarmups).Order().ToArray();
            return new TransientMetadataScalingResult(
                candidateArtifacts,
                batchSize,
                scheduledJobs,
                identitySlots,
                requirementRows,
                inputRows,
                canonicalRows,
                attempts,
                scheduleElapsed.TotalMilliseconds,
                scheduleCpu.TotalMilliseconds,
                scheduleAllocations,
                scheduleStatements,
                claimWarmups,
                measuredClaimSamples.Length,
                Percentile(measuredClaimSamples, 0.5),
                Percentile(measuredClaimSamples, 0.95),
                measuredClaimSamples[^1],
                measuredClaimSamples.Length / (measuredClaimSamples.Sum() / 1000),
                claimCpu.TotalMilliseconds,
                claimAllocations,
                claimStatements,
                retrospectivePlan,
                claimPlan,
                pendingJobs,
                leased);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The performance harness passes only test-owned EF-generated or constant SQL text.")]
    private static async Task<SqlTextEvidence> MeasureSqlTextEvidenceAsync(
        string connectionString,
        string query,
        string expectedIndex)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        var plan = string.Empty;
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = $"SET STATISTICS XML ON; SET STATISTICS IO ON; {query} SET STATISTICS IO OFF; SET STATISTICS XML OFF;";
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                if (reader.FieldCount == 1 && !await reader.IsDBNullAsync(0).ConfigureAwait(false)
                    && reader.GetValue(0) is string value && value.Contains("ShowPlanXML", StringComparison.Ordinal))
                {
                    plan = value;
                }
            }
        }
        while (await reader.NextResultAsync().ConfigureAwait(false));
        var logicalReads = Regex.Matches(messages.ToString(), @"logical reads (?<value>\d+)", RegexOptions.IgnoreCase)
            .Sum(match => int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture));
        Assert.IsFalse(string.IsNullOrWhiteSpace(plan));
        return new SqlTextEvidence(
            expectedIndex,
            plan.Contains(expectedIndex, StringComparison.Ordinal),
            logicalReads,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan))),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(query))));
    }

    private static async Task<WindowScalingResult> MeasureHistoryAsync(
        ApplicationDbContext db,
        CentralDerivativeWorkerTelemetry telemetry,
        CountingCommandInterceptor counter,
        int historySize)
    {
        var agentId = $"phase10-{historySize}";
        var rigId = $"phase10-rig-{historySize}";
        var devicePublicId = Guid.NewGuid();
        _ = await SeedHistoryAsync(db, historySize, agentId, rigId, devicePublicId).ConfigureAwait(false);
        _ = await db.Database.ExecuteSqlRawAsync(
            "UPDATE STATISTICS [CentralFrames] [IX_CentralFrames_AgentId_CaptureSequence] WITH FULLSCAN;")
            .ConfigureAwait(false);
        var firstCenter = historySize / 2L - 200;
        var warmupCenters = Enumerable.Range(0, 5).Select(index => firstCenter + index * 10L).ToArray();
        var measuredCenters = Enumerable.Range(5, 30).Select(index => firstCenter + index * 10L).ToArray();
        await EnrichSelectedFramesAsync(db, agentId, warmupCenters.Concat(measuredCenters)).ConfigureAwait(false);
        _ = await AddJobsAsync(db, agentId, warmupCenters).ConfigureAwait(false);
        var resolver = new CentralDerivativeWindowResolver(
            db, telemetry, TimeProvider.System, NullLogger<CentralDerivativeWindowResolver>.Instance);
        await resolver.ResolveWaitingAsync(DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
        var measuredJobIds = await AddJobsAsync(db, agentId, measuredCenters).ConfigureAwait(false);

        db.ChangeTracker.Clear();
        counter.Reset();
        var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var rssBefore = process.WorkingSet64;
        var started = Stopwatch.GetTimestamp();
        var samples = new double[measuredJobIds.Length];
        for (var index = 0; index < measuredJobIds.Length; index++)
        {
            var sampleStarted = Stopwatch.GetTimestamp();
            await resolver.ResolveAsync(measuredJobIds[index], DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            samples[index] = Stopwatch.GetElapsedTime(sampleStarted).TotalMilliseconds;
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var resolutionSqlCommands = counter.Count;
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var rssAfter = process.WorkingSet64;

        var selected = await db.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => measuredJobIds.Contains(input.CentralDerivativeJobId))
            .GroupBy(_ => 1)
            .Select(group => new { Count = group.Count(), Bytes = group.Sum(input => input.ByteLength) })
            .SingleAsync().ConfigureAwait(false);
        var pending = await db.CentralDerivativeJobs.CountAsync(job => measuredJobIds.Contains(job.Id)
            && job.Status == CentralDerivativeJobStatus.Pending).ConfigureAwait(false);
        Assert.AreEqual(30, pending);
        var indexPresent = await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*) AS [Value]
            FROM sys.indexes
            WHERE [object_id] = OBJECT_ID(N'[CentralFrames]')
              AND [name] = N'IX_CentralFrames_AgentId_CaptureSequence'
            """).SingleAsync().ConfigureAwait(false) == 1;
        var sqlEvidence = await MeasureSqlSelectionAsync(
            db.Database.GetConnectionString()!, agentId, $"phase10-rig-{historySize}", measuredCenters[0])
            .ConfigureAwait(false);

        Array.Sort(samples);
        return new WindowScalingResult(
            HistorySize: historySize,
            WindowOffsets: [-2, -1, 0, 1, 2],
            Warmups: 5,
            MeasuredJobs: 30,
            ResolutionTotalMilliseconds: elapsed.TotalMilliseconds,
            ResolutionMillisecondsPerJob: elapsed.TotalMilliseconds / 30,
            ResolutionMedianMilliseconds: Percentile(samples, 0.5),
            ResolutionP95Milliseconds: Percentile(samples, 0.95),
            ResolutionMaximumMilliseconds: samples[^1],
            CpuTotalMilliseconds: cpu.TotalMilliseconds,
            AllocatedBytes: allocated,
            WorkingSetBeforeBytes: rssBefore,
            WorkingSetAfterBytes: rssAfter,
            SqlCommands: resolutionSqlCommands,
            SelectedInputs: selected.Count,
            SelectedBytes: selected.Bytes,
            ObjectRequests: 0,
            SequenceIndexPresent: indexPresent,
            SequenceIndexUsedByPlan: sqlEvidence.UsesSequenceIndex,
            LogicalReads: sqlEvidence.LogicalReads,
            PlanSha256: sqlEvidence.PlanSha256,
            DurablePendingJobs: pending);
    }

    private static async Task<SqlSelectionEvidence> MeasureSqlSelectionAsync(
        string connectionString,
        string agentId,
        string rigId,
        long captureSequence)
    {
        const string query = """
            SELECT TOP(2) artifact.[Id]
            FROM [CentralArtifacts] AS artifact
            INNER JOIN [CentralFrames] AS frame ON frame.[Id] = artifact.[CentralFrameId]
            WHERE frame.[AgentId] = @agentId
              AND frame.[CaptureSequence] = @captureSequence
              AND frame.[RigId] = @rigId
              AND artifact.[Role] = N'Raw'
              AND artifact.[ObjectState] = N'Available'
              AND artifact.[ReconstructionState] = N'Complete'
            ORDER BY artifact.[Id];
            """;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        var plan = string.Empty;
        await using (var statistics = CreateSelectionCommand(connection,
            $"SET STATISTICS XML ON; SET STATISTICS IO ON; {query} SET STATISTICS IO OFF; SET STATISTICS XML OFF;",
            agentId, rigId, captureSequence))
        await using (var reader = await statistics.ExecuteReaderAsync().ConfigureAwait(false))
        {
            do
            {
                while (await reader.ReadAsync().ConfigureAwait(false))
                {
                    if (reader.FieldCount == 1
                        && !await reader.IsDBNullAsync(0).ConfigureAwait(false)
                        && reader.GetValue(0) is string value
                        && value.Contains("ShowPlanXML", StringComparison.Ordinal))
                    {
                        plan = value;
                    }
                }
            }
            while (await reader.NextResultAsync().ConfigureAwait(false));
        }
        var logicalReads = Regex.Matches(messages.ToString(), @"logical reads (?<value>\d+)", RegexOptions.IgnoreCase)
            .Sum(match => int.Parse(match.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture));
        return new SqlSelectionEvidence(
            plan.Contains("IX_CentralFrames_AgentId_CaptureSequence", StringComparison.Ordinal),
            logicalReads,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan))));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The command text is assembled only from test-owned SQL constants; workload values remain parameters.")]
    private static SqlCommand CreateSelectionCommand(
        SqlConnection connection,
        string commandText,
        string agentId,
        string rigId,
        long captureSequence)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("@agentId", agentId);
        command.Parameters.AddWithValue("@captureSequence", captureSequence);
        command.Parameters.AddWithValue("@rigId", rigId);
        return command;
    }

    private static Task<int> SeedHistoryAsync(
        ApplicationDbContext db,
        int historySize,
        string agentId,
        string rigId,
        Guid devicePublicId)
        => db.Database.ExecuteSqlInterpolatedAsync($$"""
            CREATE TABLE #History
            (
                [Sequence] bigint NOT NULL,
                [FrameId] uniqueidentifier NOT NULL,
                [ArtifactRowId] uniqueidentifier NOT NULL,
                [ArtifactId] uniqueidentifier NOT NULL
            );

            INSERT INTO #History ([Sequence], [FrameId], [ArtifactRowId], [ArtifactId])
            SELECT TOP ({{historySize}})
                ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), NEWID(), NEWID(), NEWID()
            FROM sys.all_objects AS first_source
            CROSS JOIN sys.all_objects AS second_source;

            INSERT INTO [CentralFrames]
                ([Id], [RegistrationId], [DevicePublicId], [ObservatoryId], [AgentId], [FrameId],
                 [CapturedAtUtc], [FirstReceivedAtUtc], [RigProfileVersion], [RigId], [CaptureSequence])
            SELECT [FrameId], NEWID(), {{devicePublicId}}, NEWID(), {{agentId}}, NEWID(),
                   DATEADD(millisecond, [Sequence], CAST('2026-01-01T00:00:00+00:00' AS datetimeoffset)),
                   CAST('2026-01-01T00:00:00+00:00' AS datetimeoffset), 1, {{rigId}}, [Sequence]
            FROM #History;

            INSERT INTO [CentralArtifacts]
                ([Id], [CentralFrameId], [ArtifactId], [DevicePublicId], [Role], [RecipeVersion],
                 [ManifestSchemaVersion], [MediaType], [ByteLength], [ChecksumSha256], [StorageReference],
                 [ReceivedAtUtc], [IdempotencyKey], [SourceId], [Variant], [CreatedUtc], [ObjectState],
                 [ReconstructionState], [ReconciledAtUtc], [ObjectVerificationRetryCount],
                 [RecoveryGeneration], [ReferenceRetryCount])
            SELECT [ArtifactRowId], [FrameId], [ArtifactId], {{devicePublicId}}, N'Raw', N'phase10-raw-v1',
                   N'v2', N'application/x-hvo-linear-frame', 8,
                   REPLICATE('0', 64), N'minio://skymonitor-artifacts/performance/not-read',
                   CAST('2026-01-01T00:00:00+00:00' AS datetimeoffset),
                   REPLACE(CONVERT(varchar(36), [ArtifactRowId]), '-', '')
                     + REPLACE(CONVERT(varchar(36), [ArtifactRowId]), '-', ''),
                    N'phase10-performance', N'native',
                    DATEADD(millisecond, [Sequence], CAST('2026-01-01T00:00:00+00:00' AS datetimeoffset)),
                    N'Available', N'Complete', CAST('2026-01-01T00:00:00+00:00' AS datetimeoffset), 0, 0, 0
            FROM #History;
            """);

    private static async Task EnrichSelectedFramesAsync(
        ApplicationDbContext db,
        string agentId,
        IEnumerable<long> centers)
    {
        var sequences = centers.SelectMany(center => new[] { center - 2, center - 1, center, center + 1, center + 2 })
            .Distinct().ToArray();
        var frames = await db.CentralFrames.Include(frame => frame.Artifacts)
            .Where(frame => frame.AgentId == agentId && sequences.Contains(frame.CaptureSequence!.Value))
            .ToArrayAsync().ConfigureAwait(false);
        var profileSha = HashText(agentId);
        var rawOptions = CaptureContractJson.SerializeToElement(new { });
        foreach (var frame in frames)
        {
            frame.Timing = new CentralCaptureTiming
            {
                RequestedStartUtc = frame.CapturedAtUtc.AddSeconds(-2),
                ExposureStartedUtc = frame.CapturedAtUtc.AddSeconds(-1),
                ExposureEndedUtc = frame.CapturedAtUtc.AddMilliseconds(-100),
                ReadoutCompletedUtc = frame.CapturedAtUtc.AddMilliseconds(-50),
                DurableIngressUtc = frame.CapturedAtUtc
            };
            frame.Control = new CentralCaptureControl
            {
                RequestedExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                EffectiveExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                RequestedGain = 100,
                EffectiveGain = 100,
                RequestedOffset = 1,
                EffectiveOffset = 1,
                TemperatureSetpointC = -5,
                EffectiveTemperatureC = -5
            };
            db.Entry(frame.Timing).State = EntityState.Added;
            db.Entry(frame.Control).State = EntityState.Added;
            foreach (var kind in Enum.GetValues<CentralProfileKind>())
            {
                var profile = new CentralCaptureProfile
                {
                    Kind = kind,
                    Name = $"phase10-{kind}",
                    Version = "1",
                    Sha256 = profileSha
                };
                frame.Profiles.Add(profile);
                db.Entry(profile).State = EntityState.Added;
            }
            var artifact = frame.Artifacts.Single();
            artifact.Layout = new CentralArtifactLayout
            {
                Width = 2,
                Height = 2,
                StrideBytes = 4,
                PixelFormat = CameraPixelFormat.Mono16.ToString(),
                ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                SampleDepthBits = 16,
                ContainerDepthBits = 16,
                Packing = FrameSamplePacking.ByteAligned.ToString(),
                CfaPattern = ColorFilterArrayPattern.None.ToString(),
                BlackLevel = 0,
                WhiteLevel = ushort.MaxValue,
                ByteLength = 8
            };
            artifact.Recipe = new CentralArtifactRecipe
            {
                Name = "raw-capture",
                SemanticVersion = "1.0.0",
                ImplementationVersion = "phase10-v1",
                OptionsJson = CaptureContractJson.Canonicalize(rawOptions).GetRawText(),
                OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(rawOptions)
            };
            db.Entry(artifact.Layout).State = EntityState.Added;
            db.Entry(artifact.Recipe).State = EntityState.Added;
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid[]> AddJobsAsync(ApplicationDbContext db, string agentId, IEnumerable<long> centers)
    {
        var values = centers.ToArray();
        var sources = await db.CentralArtifacts.Include(artifact => artifact.Frame)
            .Where(artifact => artifact.Frame!.AgentId == agentId
                && values.Contains(artifact.Frame.CaptureSequence!.Value))
            .ToArrayAsync().ConfigureAwait(false);
        var recipe = new CentralDerivativeRecipeCatalog().GetRequiredRecipes(FrameArtifactRole.Raw)
            .Single(item => item.RecipeName == BuiltInProcessingRecipes.RollingMean);
        var now = DateTimeOffset.UtcNow;
        var jobIds = new List<Guid>(sources.Length);
        foreach (var source in sources)
        {
            var job = new CentralDerivativeJob
            {
                SourceCentralArtifactId = source.Id,
                TargetRole = recipe.TargetRole,
                TargetRecipeVersion = recipe.RecipeVersion,
                TargetVariant = recipe.TargetVariant,
                RecipeName = recipe.RecipeName,
                RecipeOptionsJson = CaptureContractJson.Canonicalize(recipe.Options).GetRawText(),
                InputSelectorJson = CaptureContractJson.Canonicalize(
                    CaptureContractJson.SerializeToElement(recipe.InputSelector)).GetRawText(),
                RequestedRecipeIdentitySha256 = recipe.RequestedRecipeIdentitySha256,
                RequestIdentitySha256 = HashText($"{agentId}-{source.Frame!.CaptureSequence}"),
                Status = CentralDerivativeJobStatus.Waiting,
                MaxAttempts = recipe.MaxAttempts,
                ResolutionStartedAtUtc = now,
                ResolutionDeadlineUtc = now.AddMinutes(5),
                MissingInputOutcome = CentralDerivativeWindowOutcome.Skip,
                StateReasonCode = CentralDerivativeWindowReasonCodes.WaitingRequiredInput,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            jobIds.Add(job.Id);
            foreach (var position in recipe.Window!.Positions.OrderBy(position => position.SequenceOffset))
            {
                job.InputRequirements.Add(new CentralDerivativeJobInputRequirement
                {
                    Job = job,
                    CentralDerivativeJobId = job.Id,
                    Ordinal = job.InputRequirements.Count,
                    BindingName = "input",
                    SourceKind = CentralDerivativeInputSourceKind.Artifact,
                    SequenceOffset = position.SequenceOffset,
                    IsRequired = position.IsRequired,
                    SelectorJson = job.InputSelectorJson,
                    CompatibilityMode = position.CompatibilityMode,
                    ExpectedAgentId = agentId,
                    ExpectedRigId = source.Frame.RigId,
                    ExpectedCaptureSequence = source.Frame.CaptureSequence + position.SequenceOffset,
                    ResolutionState = CentralDerivativeInputResolutionState.Waiting
                });
            }
            db.CentralDerivativeJobs.Add(job);
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return jobIds.ToArray();
    }

    private static async Task<TransitionEvidence> MeasureTransitionsAsync(
        string connectionString,
        CentralDerivativeWorkerTelemetry telemetry,
        string runId)
    {
        var options = CreateOptions(connectionString);
        var now = DateTimeOffset.UtcNow;
        var delayedAgent = $"issue-101-p1-delayed-{runId}";
        Guid delayedJobId;
        Guid[] delayedSources;
        double initialMilliseconds;
        int initialRows;
        int initialPins;
        long[] initialSequences;
        double initialWaitAgeMilliseconds;
        string? initialReason;
        await using (var db = new ApplicationDbContext(options))
        {
            delayedSources = (await SeedWindowSourcesAsync(
                db, delayedAgent, 4, 2, 2, CameraPixelFormat.Mono16,
                "minio://skymonitor-artifacts/performance/issue-101/not-read", 8).ConfigureAwait(false)).Values.ToArray();
            delayedJobId = (await AddJobsAsync(db, delayedAgent, [3]).ConfigureAwait(false)).Single();
            var resolver = CreateResolver(db, telemetry);
            var started = Stopwatch.GetTimestamp();
            await resolver.ResolveAsync(delayedJobId, now, CancellationToken.None).ConfigureAwait(false);
            initialMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            initialRows = await db.CentralDerivativeJobInputs.CountAsync(input =>
                input.CentralDerivativeJobId == delayedJobId).ConfigureAwait(false);
            initialSequences = await db.CentralDerivativeJobInputs.AsNoTracking()
                .Where(input => input.CentralDerivativeJobId == delayedJobId)
                .OrderBy(input => input.Ordinal)
                .Select(input => input.CaptureSequence!.Value)
                .ToArrayAsync().ConfigureAwait(false);
            var references = new CentralArtifactRetentionReferences(db);
            initialPins = 0;
            foreach (var sourceId in delayedSources)
            {
                initialPins += await references.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false) ? 1 : 0;
            }
            Assert.AreEqual(4, initialRows);
            Assert.AreEqual(4, initialPins);
            CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, initialSequences);
            initialWaitAgeMilliseconds = Math.Max(0, (now - (await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.Id == delayedJobId)
                .Select(job => job.ResolutionStartedAtUtc!.Value)
                .SingleAsync().ConfigureAwait(false))).TotalMilliseconds);
            initialReason = await db.CentralDerivativeJobs.AsNoTracking().Where(job => job.Id == delayedJobId)
                .Select(job => job.StateReasonCode).SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(CentralDerivativeWindowReasonCodes.WaitingRequiredInput, initialReason);
        }

        double restartMilliseconds;
        int delayedRows;
        string delayedStatus;
        int delayedPins;
        double delayedWaitAgeMilliseconds;
        string? delayedReason;
        await using (var restartedDb = new ApplicationDbContext(options))
        {
            var arrived = await SeedWindowSourcesAsync(
                restartedDb, delayedAgent, 1, 2, 2, CameraPixelFormat.Mono16,
                "minio://skymonitor-artifacts/performance/issue-101/not-read", 8,
                firstSequence: 5).ConfigureAwait(false);
            delayedSources = [.. delayedSources, arrived[5]];
            var started = Stopwatch.GetTimestamp();
            await CreateResolver(restartedDb, telemetry)
                .ResolveAsync(delayedJobId, now.AddSeconds(1), CancellationToken.None).ConfigureAwait(false);
            restartMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var delayed = await restartedDb.CentralDerivativeJobs.AsNoTracking()
                .SingleAsync(job => job.Id == delayedJobId).ConfigureAwait(false);
            delayedStatus = delayed.Status.ToString();
            delayedReason = delayed.StateReasonCode;
            delayedWaitAgeMilliseconds = Math.Max(0,
                (now.AddSeconds(1) - delayed.ResolutionStartedAtUtc!.Value).TotalMilliseconds);
            delayedRows = await restartedDb.CentralDerivativeJobInputs.CountAsync(input =>
                input.CentralDerivativeJobId == delayedJobId).ConfigureAwait(false);
            var references = new CentralArtifactRetentionReferences(restartedDb);
            delayedPins = 0;
            foreach (var sourceId in delayedSources)
            {
                delayedPins += await references.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false) ? 1 : 0;
            }
            Assert.AreEqual(CentralDerivativeJobStatus.Pending, delayed.Status);
            Assert.IsNull(delayedReason);
            Assert.AreEqual(5, delayedRows);
            Assert.AreEqual(5, delayedPins);
            CollectionAssert.AreEqual(
                new long[] { 1, 2, 3, 4, 5 },
                await restartedDb.CentralDerivativeJobInputs.AsNoTracking()
                    .Where(input => input.CentralDerivativeJobId == delayedJobId)
                    .OrderBy(input => input.Ordinal)
                    .Select(input => input.CaptureSequence!.Value)
                    .ToArrayAsync().ConfigureAwait(false));
        }

        var missingAgent = $"issue-101-p1-missing-{runId}";
        Guid missingJobId;
        Guid[] missingSources;
        DateTimeOffset deadline;
        await using (var db = new ApplicationDbContext(options))
        {
            missingSources = (await SeedWindowSourcesAsync(
                db, missingAgent, 5, 2, 2, CameraPixelFormat.Mono16,
                "minio://skymonitor-artifacts/performance/issue-101/not-read", 8).ConfigureAwait(false)).Values.ToArray();
            await db.CentralArtifacts.Where(artifact => missingSources.Contains(artifact.Id)
                    && artifact.Frame!.CaptureSequence != 3)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(artifact => artifact.ObjectState, CentralArtifactObjectState.Pending)
                    .SetProperty(artifact => artifact.ReconstructionState, CentralReconstructionState.PendingReference))
                .ConfigureAwait(false);
            missingJobId = (await AddJobsAsync(db, missingAgent, [3]).ConfigureAwait(false)).Single();
            deadline = now.AddMinutes(5);
            await db.CentralDerivativeJobs.Where(job => job.Id == missingJobId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.ResolutionDeadlineUtc, deadline))
                .ConfigureAwait(false);
            await CreateResolver(db, telemetry).ResolveAsync(missingJobId, now, CancellationToken.None)
                .ConfigureAwait(false);
        }

        double missingMilliseconds;
        int missingRows;
        int missingRequirements;
        int terminalPins;
        string missingStatus;
        double missingWaitAgeMilliseconds;
        string? missingReason;
        await using (var restartedDb = new ApplicationDbContext(options))
        {
            var started = Stopwatch.GetTimestamp();
            await CreateResolver(restartedDb, telemetry)
                .ResolveWaitingAsync(deadline.AddTicks(1), CancellationToken.None).ConfigureAwait(false);
            missingMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var missing = await restartedDb.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.InputRequirements)
                .SingleAsync(job => job.Id == missingJobId).ConfigureAwait(false);
            missingStatus = missing.Status.ToString();
            missingReason = missing.StateReasonCode;
            missingWaitAgeMilliseconds = Math.Max(0,
                (deadline.AddTicks(1) - missing.ResolutionStartedAtUtc!.Value).TotalMilliseconds);
            missingRows = await restartedDb.CentralDerivativeJobInputs.CountAsync(input =>
                input.CentralDerivativeJobId == missingJobId).ConfigureAwait(false);
            missingRequirements = missing.InputRequirements.Count(requirement =>
                requirement.ResolutionState == CentralDerivativeInputResolutionState.Missing);
            var references = new CentralArtifactRetentionReferences(restartedDb);
            terminalPins = 0;
            foreach (var sourceId in missingSources)
            {
                terminalPins += await references.IsHeldAsync(sourceId, CancellationToken.None).ConfigureAwait(false) ? 1 : 0;
            }
            Assert.AreEqual(CentralDerivativeJobStatus.Skipped, missing.Status);
            Assert.AreEqual(CentralDerivativeWindowReasonCodes.RequiredInputTimeout, missingReason);
            Assert.AreEqual(1, missingRows);
            Assert.AreEqual(4, missingRequirements);
            Assert.AreEqual(0, terminalPins);
            await DisableJobsAsync(restartedDb, [delayedJobId]).ConfigureAwait(false);
        }

        return new TransitionEvidence(
            new TransitionResult("waiting-n-minus-2-through-n-plus-1", initialMilliseconds, initialWaitAgeMilliseconds,
                "Waiting", initialReason, initialRows, initialPins, 0, initialSequences),
            new TransitionResult("fresh-context-n-plus-2-arrival", restartMilliseconds, delayedWaitAgeMilliseconds,
                delayedStatus, delayedReason, delayedRows, delayedPins, 0, [1, 2, 3, 4, 5]),
            new TransitionResult("fresh-context-deadline-expiry", missingMilliseconds, missingWaitAgeMilliseconds,
                missingStatus, missingReason,
                missingRows, terminalPins, missingRequirements, [3]),
            "A fresh DbContext is the in-process restart boundary. Durable input rows and production retention-reference queries are asserted before and after each transition.");
    }

    private static async Task<SchedulerNotificationResult> MeasureDuplicateNotificationsAsync(
        IntegrationTestFixture fixture,
        CentralDerivativeWorkerTelemetry telemetry,
        string runId,
        int concurrency,
        int warmups = 0,
        int notificationCount = 1_000)
    {
        var options = CreateOptions(fixture.SqlServerConnectionString);
        var agentId = $"issue-101-p2-c{concurrency}-{runId}";
        var payload = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
        var objectKey = $"performance/central-window-processing/{runId}/p2-c{concurrency}.raw";
        await PublishPayloadAsync(fixture, objectKey, payload).ConfigureAwait(false);
        Guid sourceId;
        Guid notificationSourceId;
        Guid rollingJobId;
        await using (var db = new ApplicationDbContext(options))
        {
            var sources = await SeedWindowSourcesAsync(
                db, agentId, 5, 2, 2, CameraPixelFormat.Mono16,
                $"minio://{ArtifactBucket}/{objectKey}", payload.LongLength,
                Convert.ToHexString(SHA256.HashData(payload))).ConfigureAwait(false);
            sourceId = sources[3];
            notificationSourceId = sources[5];
            await db.CentralArtifacts.Where(item => item.Id == notificationSourceId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Pending)
                    .SetProperty(item => item.ReconstructionState, CentralReconstructionState.PendingReference))
                .ConfigureAwait(false);
            var artifact = await db.CentralArtifacts.Include(item => item.Frame)!
                .ThenInclude(frame => frame!.Artifacts)
                .SingleAsync(item => item.Id == sourceId).ConfigureAwait(false);
            var resolver = CreateResolver(db, telemetry);
            await new CentralDerivativeJobScheduler(db, new CentralDerivativeRecipeCatalog(), resolver)
                .EnsureRequiredJobsAsync(artifact, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
            rollingJobId = await db.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == sourceId
                    && job.RecipeName == BuiltInProcessingRecipes.RollingMean)
                .Select(job => job.Id).SingleAsync().ConfigureAwait(false);
            Assert.AreEqual(CentralDerivativeJobStatus.Waiting, await db.CentralDerivativeJobs
                .Where(job => job.Id == rollingJobId).Select(job => job.Status).SingleAsync().ConfigureAwait(false));
            Assert.AreEqual(4, await db.CentralDerivativeJobInputs.CountAsync(input =>
                input.CentralDerivativeJobId == rollingJobId).ConfigureAwait(false));
            await db.CentralArtifacts.Where(item => item.Id == notificationSourceId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.ObjectState, CentralArtifactObjectState.Available)
                    .SetProperty(item => item.ReconstructionState, CentralReconstructionState.Complete))
                .ConfigureAwait(false);
        }

        var deadlocks = 0;
        var uniquenessCollisions = 0;
        var latencies = new ConcurrentBag<double>();
        for (var index = 0; index < warmups; index++)
        {
            await using var warmupDb = new ApplicationDbContext(options);
            var warmupArtifact = await warmupDb.CentralArtifacts.Include(item => item.Frame)!
                .ThenInclude(frame => frame!.Artifacts)
                .SingleAsync(item => item.Id == notificationSourceId).ConfigureAwait(false);
            await new CentralDerivativeJobScheduler(
                    warmupDb,
                    new CentralDerivativeRecipeCatalog(),
                    CreateResolver(warmupDb, telemetry))
                .EnsureRequiredJobsAsync(warmupArtifact, DateTimeOffset.UtcNow, CancellationToken.None)
                .ConfigureAwait(false);
        }
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, notificationCount),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (_, cancellationToken) =>
            {
                var notificationStarted = Stopwatch.GetTimestamp();
                for (var retry = 0; ; retry++)
                {
                    try
                    {
                        await using var db = new ApplicationDbContext(options);
                        var artifact = await db.CentralArtifacts.Include(item => item.Frame)!
                            .ThenInclude(frame => frame!.Artifacts)
                            .SingleAsync(item => item.Id == notificationSourceId, cancellationToken).ConfigureAwait(false);
                        var resolver = CreateResolver(db, telemetry);
                        var scheduler = new CentralDerivativeJobScheduler(
                            db, new CentralDerivativeRecipeCatalog(), resolver);
                        await scheduler.EnsureRequiredJobsAsync(
                            artifact, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception exception) when (TryGetSqlNumber(exception) is 1205 or 2601 or 2627 && retry < 20)
                    {
                        var number = TryGetSqlNumber(exception);
                        if (number == 1205)
                        {
                            Interlocked.Increment(ref deadlocks);
                        }
                        else
                        {
                            Interlocked.Increment(ref uniquenessCollisions);
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(retry + 1), cancellationToken).ConfigureAwait(false);
                    }
                }
                latencies.Add(Stopwatch.GetElapsedTime(notificationStarted).TotalMilliseconds);
            }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);

        await using var assertionDb = new ApplicationDbContext(options);
        var rollingJobs = await assertionDb.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.SourceCentralArtifactId == sourceId
                && job.RecipeName == BuiltInProcessingRecipes.RollingMean)
            .Select(job => job.Id).ToArrayAsync().ConfigureAwait(false);
        var inputRows = await assertionDb.CentralDerivativeJobInputs.CountAsync(input =>
            rollingJobs.Contains(input.CentralDerivativeJobId)).ConfigureAwait(false);
        var orderedSequences = await assertionDb.CentralDerivativeJobInputs.AsNoTracking()
            .Where(input => input.CentralDerivativeJobId == rollingJobId)
            .OrderBy(input => input.Ordinal)
            .Select(input => input.CaptureSequence!.Value)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.AreEqual(1, rollingJobs.Length);
        Assert.AreEqual(5, inputRows);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5 }, orderedSequences);
        await DisableOtherActiveJobsAsync(assertionDb, [rollingJobId]).ConfigureAwait(false);
        await using (var claimScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync($"issue-101-p2-c{concurrency}", TimeSpan.FromMinutes(1), CancellationToken.None)
                .ConfigureAwait(false))!;
            Assert.AreEqual(rollingJobId, lease.JobId);
            await using var executionScope = fixture.Factory.Services.CreateAsyncScope();
            var execution = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, execution.Status);
        }
        assertionDb.ChangeTracker.Clear();
        var completedStatus = await assertionDb.CentralDerivativeJobs.AsNoTracking()
            .Where(job => job.Id == rollingJobId).Select(job => job.Status).SingleAsync().ConfigureAwait(false);
        var evidenceRows = await assertionDb.CentralArtifactProcessingEvidence.CountAsync(evidence =>
            evidence.CentralDerivativeJobId == rollingJobId).ConfigureAwait(false);
        var resultRows = await assertionDb.CentralDerivativeJobs.CountAsync(job => job.Id == rollingJobId
            && job.ResultCentralArtifactId != null).ConfigureAwait(false);
        Assert.AreEqual(CentralDerivativeJobStatus.Completed, completedStatus);
        Assert.AreEqual(1, evidenceRows);
        Assert.AreEqual(1, resultRows);
        var ordered = latencies.Order().ToArray();
        Assert.AreEqual(notificationCount, ordered.Length);
        await DisableSourceJobsAsync(assertionDb, sourceId).ConfigureAwait(false);
        return new SchedulerNotificationResult(
            concurrency,
            warmups,
            notificationCount,
            elapsed.TotalMilliseconds,
            notificationCount / elapsed.TotalSeconds,
            Percentile(ordered, 0.5),
            Percentile(ordered, 0.95),
            ordered[^1],
            deadlocks,
            uniquenessCollisions,
            rollingJobs.Length,
            inputRows,
            orderedSequences,
            completedStatus.ToString(),
            evidenceRows,
            resultRows);
    }

    private static async Task<RollingExecutionResult> MeasureRollingExecutionAsync(
        IntegrationTestFixture fixture,
        string runId,
        string workloadId,
        int width,
        int height,
        CameraPixelFormat pixelFormat)
    {
        const int warmups = 5;
        const int measurements = 30;
        var payload = CreatePayload(width, height, pixelFormat, seed: 101);
        var checksum = Convert.ToHexString(SHA256.HashData(payload));
        var objectKey = $"performance/central-window-processing/{runId}/{workloadId}.raw";
        await PublishPayloadAsync(fixture, objectKey, payload).ConfigureAwait(false);
        var agentId = $"issue-101-p3-{workloadId}-{runId}";
        Guid[] jobIds;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            _ = await SeedWindowSourcesAsync(
                db, agentId, warmups + measurements + 4, width, height, pixelFormat,
                $"minio://{ArtifactBucket}/{objectKey}", payload.LongLength, checksum).ConfigureAwait(false);
            jobIds = await AddJobsAsync(db, agentId, Enumerable.Range(3, warmups + measurements).Select(value => (long)value))
                .ConfigureAwait(false);
            using var telemetry = new CentralDerivativeWorkerTelemetry();
            var resolver = CreateResolver(db, telemetry);
            foreach (var jobId in jobIds)
            {
                await resolver.ResolveAsync(jobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
            }
            await DisableOtherActiveJobsAsync(db, jobIds).ConfigureAwait(false);
        }

        await ExecuteRollingJobsAsync(fixture, warmups, latencies: null).ConfigureAwait(false);
        Guid[] measuredJobIds;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            measuredJobIds = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => jobIds.Contains(job.Id) && job.Status == CentralDerivativeJobStatus.Pending)
                .Select(job => job.Id).ToArrayAsync().ConfigureAwait(false);
            Assert.AreEqual(measurements, measuredJobIds.Length);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var rssBefore = process.WorkingSet64;
        var lohBefore = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;
        var latencies = new ConcurrentBag<double>();
        var started = Stopwatch.GetTimestamp();
        await ExecuteRollingJobsAsync(fixture, measurements, latencies).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocations = GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore;
        var rssAfter = process.WorkingSet64;
        var peakWorkingSet = process.PeakWorkingSet64;
        var lohAfter = GC.GetGCMemoryInfo().GenerationInfo[3].SizeAfterBytes;

        List<CentralDerivativeJob> jobs;
        List<CentralArtifactProcessingEvidence> processingEvidence;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobs = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => measuredJobIds.Contains(job.Id))
                .Include(job => job.Attempts)
                .Include(job => job.Inputs)
                .Include(job => job.ResultArtifact)!.ThenInclude(artifact => artifact!.Sources)
                .AsSplitQuery()
                .ToListAsync().ConfigureAwait(false);
            processingEvidence = await db.CentralArtifactProcessingEvidence.AsNoTracking()
                .Where(item => measuredJobIds.Contains(item.CentralDerivativeJobId))
                .ToListAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(measurements, jobs.Count);
        Assert.AreEqual(measurements, jobs.Count(job => job.Status == CentralDerivativeJobStatus.Completed));
        Assert.AreEqual(measurements, processingEvidence.Count);
        Assert.AreEqual(measurements, jobs.Select(job => job.ResultCentralArtifactId).Distinct().Count());
        var inputRows = jobs.Sum(job => job.Inputs.Count);
        Assert.AreEqual(measurements * 5, inputRows);
        var orderedLineage = true;
        foreach (var job in jobs)
        {
            Assert.AreEqual(1, job.Attempts.Count);
            Assert.AreEqual(CentralDerivativeAttemptOutcome.Completed, job.Attempts.Single().Outcome);
            var expected = job.Inputs.OrderBy(input => input.Ordinal)
                .Select(input => input.CentralArtifactId).Cast<Guid?>().ToArray();
            var actual = job.ResultArtifact!.Sources.OrderBy(source => source.Ordinal)
                .Select(source => source.ResolvedCentralArtifactId).ToArray();
            orderedLineage &= expected.SequenceEqual(actual);
        }
        Assert.IsTrue(orderedLineage);
        Assert.IsTrue(processingEvidence.All(item => !string.IsNullOrWhiteSpace(item.CompatibilityJson)));

        var verifiedOutputs = 0;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
            foreach (var artifact in jobs.Select(job => job.ResultArtifact!))
            {
                string? observedChecksum = null;
                var outputKey = artifact.StorageReference[$"minio://{ArtifactBucket}/".Length..];
                await minio.GetObjectAsync(new GetObjectArgs()
                    .WithBucket(ArtifactBucket)
                    .WithObject(outputKey)
                    .WithCallbackStream(stream => observedChecksum = Convert.ToHexString(SHA256.HashData(stream))))
                    .ConfigureAwait(false);
                Assert.AreEqual(artifact.ChecksumSha256, observedChecksum, ignoreCase: true);
                verifiedOutputs++;
            }
        }
        var orderedLatencies = latencies.Order().ToArray();
        var recipeMilliseconds = jobs.Select(job => TimeSpan.FromTicks(job.Attempts.Single().RecipeDurationTicks)
                .TotalMilliseconds)
            .Order().ToArray();
        var inputBytes = jobs.Sum(job => job.Attempts.Single().InputBytes);
        Assert.AreEqual(checked(payload.LongLength * 5 * measurements), inputBytes);
        Assert.AreEqual(measurements, verifiedOutputs);
        return new RollingExecutionResult(
            workloadId,
            width,
            height,
            pixelFormat.ToString(),
            payload.LongLength,
            checksum,
            warmups,
            measurements,
            elapsed.TotalMilliseconds,
            Percentile(orderedLatencies, 0.5),
            Percentile(orderedLatencies, 0.95),
            orderedLatencies[^1],
            Percentile(recipeMilliseconds, 0.5),
            Percentile(recipeMilliseconds, 0.95),
            recipeMilliseconds[^1],
            measurements / elapsed.TotalSeconds,
            cpu.TotalMilliseconds,
            allocations,
            rssBefore,
            rssAfter,
            peakWorkingSet,
            lohBefore,
            lohAfter,
            inputRows,
            inputRows,
            inputRows,
            inputRows,
            checked(inputBytes * 2),
            inputBytes,
            jobs.Sum(job => job.ResultArtifact!.ByteLength),
            jobs.Select(job => job.ResultArtifact!.ChecksumSha256).Distinct().Order().ToArray(),
            verifiedOutputs,
            processingEvidence.Select(item => item.CompatibilityJson).Distinct().Count(),
            orderedLineage);
    }

    private static async Task<TransientExecutionPerformanceResult> MeasureCentralTransientExecutionAsync(
        IntegrationTestFixture fixture,
        string runId)
    {
        const int width = 3096;
        const int height = 2080;
        const int warmups = 5;
        const int measurements = 30;
        var backgroundPayload = CreateUniformPayload(width, height, 100);
        var targetPayload = CreatePositiveTransientPayload(backgroundPayload, width, height);
        var backgroundChecksum = Convert.ToHexString(SHA256.HashData(backgroundPayload));
        var targetChecksum = Convert.ToHexString(SHA256.HashData(targetPayload));
        var backgroundObjectKey = $"performance/issue-116/{runId}/w2-synthetic-background.raw";
        var targetObjectKey = $"performance/issue-116/{runId}/w2-synthetic-target.raw";
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-20);
        var jobIds = new List<Guid>(warmups + measurements);
        for (var operation = 0; operation < warmups + measurements; operation++)
        {
            var scenario = $"issue-116-w2-transient-{operation:D2}-{runId}";
            var devicePublicId = Guid.NewGuid();
            var sources = new Dictionary<long, Guid>();
            for (var sequence = 1L; sequence <= 5; sequence++)
            {
                var isTarget = sequence == 3;
                sources[sequence] = await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                    scenario,
                    devicePublicId,
                    sequence,
                    capturedBase.AddMinutes(operation),
                    isTarget ? targetPayload : backgroundPayload,
                    "issue-116-w2-synthetic-compatible",
                    width: width,
                    height: height,
                    pixelFormat: CameraPixelFormat.BayerRggb16,
                    objectKeyOverride: isTarget ? targetObjectKey : backgroundObjectKey,
                    publishPayload: operation == 0 && sequence is 1 or 3).ConfigureAwait(false);
            }
            var options = new CentralTransientOptions
            {
                Mode = TransientDetectorExecutionMode.Central,
                StarMaximumMagnitude = -30
            };
            await CentralDerivativeWindowIntegrationTests.ScheduleTransientAsync(sources[3], options)
                .ConfigureAwait(false);
            await using var jobScope = fixture.Factory.Services.CreateAsyncScope();
            var jobDb = jobScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            jobIds.Add(await jobDb.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == sources[3]
                    && job.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(job => job.Id).SingleAsync().ConfigureAwait(false));
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.AreEqual(warmups + measurements, jobIds.Count);
            Assert.AreEqual(jobIds.Count, await db.CentralDerivativeJobs.CountAsync(job =>
                jobIds.Contains(job.Id) && job.Status == CentralDerivativeJobStatus.Pending).ConfigureAwait(false));
            await DisableOtherActiveJobsAsync(db, jobIds.ToArray()).ConfigureAwait(false);
        }

        var warmupJobIds = await ExecuteTransientJobsAsync(fixture, warmups, latencies: null).ConfigureAwait(false);
        var adoption = await MeasureTransientAdoptionRecoveryAsync(fixture, warmupJobIds[0]).ConfigureAwait(false);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        var gcBefore = GC.GetGCMemoryInfo();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(true);
        var rssBefore = process.WorkingSet64;
        var liveBefore = GC.GetTotalMemory(false);
        var latencies = new ConcurrentBag<double>();
        using var protocol = new TransientProtocolCounter();
        protocol.Start();
        var measuredJobIds = await ExecuteTransientJobsAsync(fixture, measurements, latencies, protocol)
            .ConfigureAwait(false);
        var elapsed = TimeSpan.FromMilliseconds(latencies.Sum());
        var protocolSnapshot = protocol.Stop();
        process.Refresh();
        var cpu = process.TotalProcessorTime - cpuBefore;
        var allocations = GC.GetTotalAllocatedBytes(true) - allocationsBefore;
        var rssAfter = process.WorkingSet64;
        var peakRss = process.PeakWorkingSet64;
        var liveAfter = GC.GetTotalMemory(false);
        var gcAfter = GC.GetGCMemoryInfo();

        int receiptCount;
        int sourceRows;
        int eventVersions;
        int completedJobs;
        int persistedJobs;
        int candidateCount;
        long inputBytes;
        long attemptInputBytes;
        bool orderedTemporalLineage;
        string[] receiptIdentities;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var jobs = await db.CentralDerivativeJobs.AsNoTracking()
                .Include(job => job.Attempts)
                .Where(job => measuredJobIds.Contains(job.Id))
                .ToListAsync().ConfigureAwait(false);
            var receipts = await db.CentralTransientExtractionReceipts.AsNoTracking()
                .Include(receipt => receipt.Sources)
                .Where(receipt => measuredJobIds.Contains(receipt.CentralDerivativeJobId))
                .ToListAsync().ConfigureAwait(false);
            receiptCount = receipts.Count;
            sourceRows = receipts.Sum(receipt => receipt.Sources.Count);
            completedJobs = jobs.Count(job => job.Status == CentralDerivativeJobStatus.Completed);
            persistedJobs = jobs.Count(job => job.StateReasonCode == CentralTransientRuntimeReasonCodes.Persisted);
            inputBytes = await db.CentralDerivativeJobInputs.AsNoTracking()
                .Where(input => measuredJobIds.Contains(input.CentralDerivativeJobId))
                .SumAsync(input => input.ByteLength).ConfigureAwait(false);
            attemptInputBytes = jobs.Sum(job => job.Attempts.Single().InputBytes);
            var committedSlots = await db.CentralTransientValidationIdentitySlots.AsNoTracking()
                .Where(slot => measuredJobIds.Contains(slot.CentralDerivativeJobId)
                    && slot.State == CentralTransientValidationIdentitySlotState.Committed)
                .ToArrayAsync().ConfigureAwait(false);
            candidateCount = committedSlots.Length;
            eventVersions = committedSlots.Select(slot => slot.PersistedEventVersionId).Distinct().Count();
            orderedTemporalLineage = receipts.All(receipt => receipt.Sources.OrderBy(source => source.Ordinal)
                .Select(source => source.Position).SequenceEqual(OrderedTransientPositions));
            receiptIdentities = receipts.Select(receipt => receipt.CanonicalReceiptSha256)
                .Order(StringComparer.Ordinal).ToArray();
        }
        Assert.AreEqual(measurements, completedJobs);
        Assert.AreEqual(measurements, persistedJobs);
        Assert.AreEqual(measurements, receiptCount);
        Assert.AreEqual(measurements * 5, sourceRows);
        Assert.AreEqual(measurements, candidateCount);
        Assert.AreEqual(measurements, eventVersions);
        Assert.IsTrue(orderedTemporalLineage);
        Assert.IsTrue(receiptIdentities.All(identity => identity.Length == 64));
        Assert.AreEqual(checked(backgroundPayload.LongLength * 5 * measurements), inputBytes);
        var orderedLatencies = latencies.Order().ToArray();
        return new TransientExecutionPerformanceResult(
            width,
            height,
            CameraPixelFormat.BayerRggb16.ToString(),
            backgroundPayload.LongLength,
            backgroundChecksum,
            targetChecksum,
            "Deterministic synthetic RGGB residual: uniform 100-DN context and a 200x6-pixel 10,000-DN target streak; not a VirtualSky render or physical sensitivity claim.",
            warmups,
            measurements,
            elapsed.TotalMilliseconds,
            Percentile(orderedLatencies, 0.5),
            Percentile(orderedLatencies, 0.95),
            orderedLatencies[^1],
            measurements / elapsed.TotalSeconds,
            cpu.TotalMilliseconds,
            allocations,
            rssBefore,
            rssAfter,
            peakRss,
            liveBefore,
            liveAfter,
            gcBefore.GenerationInfo[3].SizeAfterBytes,
            gcAfter.GenerationInfo[3].SizeAfterBytes,
            checked(backgroundPayload.LongLength * 5),
            5,
            inputBytes,
            attemptInputBytes,
            receiptCount,
            sourceRows,
            eventVersions,
            candidateCount / (double)measurements,
            receiptIdentities,
            orderedTemporalLineage,
            adoption,
            protocolSnapshot);
    }

    private static async Task<Guid[]> ExecuteTransientJobsAsync(
        IntegrationTestFixture fixture,
        int count,
        ConcurrentBag<double>? latencies,
        TransientProtocolCounter? protocol = null)
    {
        var jobIds = new Guid[count];
        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            CentralDerivativeJobLease lease;
            await using (var claimScope = fixture.Factory.Services.CreateAsyncScope())
            {
                lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync("issue-116-transient-performance", TimeSpan.FromMinutes(10), CancellationToken.None)
                    .ConfigureAwait(false))!;
            }
            Assert.IsNotNull(lease);
            Assert.AreEqual(CentralTransientRuntime.RecipeName, lease.RecipeName);
            Assert.AreEqual(5, lease.Inputs!.Count);
            await using (var executionScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var services = executionScope.ServiceProvider;
                var executor = protocol is null
                    ? services.GetRequiredService<ICentralDerivativeJobExecutor>()
                    : CreateObservedTransientExecutor(services, protocol);
                var result = await executor.ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
                Assert.IsNull(result.ReasonCode);
            }
            latencies?.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            await using (var boundaryScope = fixture.Factory.Services.CreateAsyncScope())
            {
                await boundaryScope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CentralDerivativeJobs
                    .Where(job => job.RecipeName == CentralTransientDerivativeRuntime.RecipeName &&
                        job.Status == CentralDerivativeJobStatus.Pending)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                        .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                        .SetProperty(job => job.StateReasonCode, "performance.downstream-boundary"))
                    .ConfigureAwait(false);
            }
            jobIds[index] = lease.JobId;
        }
        return jobIds;
    }

    private static CentralDerivativeJobExecutor CreateObservedTransientExecutor(
        IServiceProvider services,
        TransientProtocolCounter? protocol)
    {
        var db = services.GetRequiredService<ApplicationDbContext>();
        var jobService = services.GetRequiredService<ICentralDerivativeJobService>();
        var telemetry = services.GetRequiredService<CentralDerivativeWorkerTelemetry>();
        var timeProvider = services.GetRequiredService<TimeProvider>();
        var objectReader = services.GetRequiredService<ICentralArtifactObjectReader>();
        if (protocol is not null)
        {
            objectReader = new ObservedCentralArtifactObjectReader(objectReader, protocol);
        }
        var inputReader = new CentralDerivativeJobInputReader(
            db, objectReader, jobService, telemetry, timeProvider);
        var transientExecutor = new CentralTransientValidationExecutor(
            db,
            inputReader,
            services.GetRequiredService<ICentralTransientEventPersistence>(),
            services.GetRequiredService<ICentralTransientDerivativeScheduler>(),
            jobService,
            services.GetRequiredService<ICentralTransientMaskFactory>(),
            telemetry,
            timeProvider,
            services.GetRequiredService<ILogger<CentralTransientValidationExecutor>>());
        return new CentralDerivativeJobExecutor(
            inputReader,
            services.GetRequiredService<LogicHostRecipeExecutionAdapter>(),
            services.GetRequiredService<ICentralDerivativeOutputWriter>(),
            jobService,
            services.GetRequiredService<ICentralDerivativeJobScheduler>(),
            transientExecutor,
            services.GetRequiredService<ICentralTransientDerivativeExecutor>(),
            services.GetRequiredService<ICentralTransientReprocessingExecutor>(),
            telemetry,
            timeProvider);
    }

    private static async Task<TransientAdoptionRecoveryResult> MeasureTransientAdoptionRecoveryAsync(
        IntegrationTestFixture fixture,
        Guid jobId)
    {
        Guid eventVersionId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            eventVersionId = await db.CentralTransientValidationIdentitySlots.AsNoTracking()
                .Where(slot => slot.CentralDerivativeJobId == jobId
                    && slot.State == CentralTransientValidationIdentitySlotState.Committed)
                .Select(slot => slot.PersistedEventVersionId!.Value)
                .SingleAsync().ConfigureAwait(false);
            await db.CentralDerivativeJobs.Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Status, CentralDerivativeJobStatus.Pending)
                    .SetProperty(job => job.AvailableAtUtc, DateTimeOffset.UnixEpoch)
                    .SetProperty(job => job.CompletedAtUtc, (DateTimeOffset?)null)
                    .SetProperty(job => job.StateReasonCode, (string?)null))
                .ConfigureAwait(false);
        }

        var started = Stopwatch.GetTimestamp();
        CentralDerivativeJobLease lease;
        await using (var claimScope = fixture.Factory.Services.CreateAsyncScope())
        {
            lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                .ClaimNextAsync("issue-116-transient-adoption", TimeSpan.FromMinutes(2), CancellationToken.None)
                .ConfigureAwait(false))!;
            Assert.AreEqual(jobId, lease.JobId);
        }
        CentralDerivativeExecutionResult result;
        await using (var executionScope = fixture.Factory.Services.CreateAsyncScope())
        {
            result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status);
        Assert.AreEqual(CentralTransientRuntimeReasonCodes.OutputAdopted, result.ReasonCode);

        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persistedVersionIds = await verify.CentralTransientValidationIdentitySlots.AsNoTracking()
            .Where(slot => slot.CentralDerivativeJobId == jobId
                && slot.State == CentralTransientValidationIdentitySlotState.Committed)
            .Select(slot => slot.PersistedEventVersionId!.Value)
            .ToArrayAsync().ConfigureAwait(false);
        Assert.HasCount(1, persistedVersionIds);
        Assert.AreEqual(eventVersionId, persistedVersionIds[0]);
        Assert.AreEqual(1, await verify.CentralTransientEventVersions.CountAsync(version =>
            version.EventVersionId == eventVersionId).ConfigureAwait(false));
        return new TransientAdoptionRecoveryResult(
            elapsed.TotalMilliseconds,
            jobId,
            eventVersionId,
            result.ReasonCode!,
            1,
            true);
    }

    private static async Task<TransientSchedulingResult> MeasureTransientDuplicateSchedulingAsync(
        IntegrationTestFixture fixture,
        string runId,
        int concurrency,
        int warmups,
        int measurements)
    {
        const int width = 3096;
        const int height = 2080;
        var payload = CreatePayload(width, height, CameraPixelFormat.BayerRggb16, seed: 116);
        var scenario = $"issue-116-w4-c{concurrency}-{runId}";
        var devicePublicId = Guid.NewGuid();
        var capturedBase = DateTimeOffset.UtcNow.AddMinutes(-20);
        var sources = new List<Guid>(5);
        for (var sequence = 1L; sequence <= 5; sequence++)
        {
            sources.Add(await CentralDerivativeWindowIntegrationTests.SeedAndScheduleSourceAsync(
                scenario,
                devicePublicId,
                sequence,
                capturedBase,
                payload,
                "issue-116-w4-compatible",
                width: width,
                height: height,
                pixelFormat: CameraPixelFormat.BayerRggb16).ConfigureAwait(false));
        }
        var options = new CentralTransientOptions
        {
            Mode = TransientDetectorExecutionMode.Central,
            StarMaximumMagnitude = -30
        };
        var centerSourceId = sources[2];
        var deadlocks = 0;
        var uniquenessCollisions = 0;
        var firstCreateLatencies = new ConcurrentBag<double>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;
        var firstCreateStarted = Stopwatch.GetTimestamp();
        var firstCreateTasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            if (Interlocked.Increment(ref readyCount) == concurrency)
            {
                ready.SetResult();
            }
            await release.Task.ConfigureAwait(false);
            var operationStarted = Stopwatch.GetTimestamp();
            for (var retry = 0; ; retry++)
            {
                try
                {
                    await CentralDerivativeWindowIntegrationTests.ScheduleTransientAsync(centerSourceId, options)
                        .ConfigureAwait(false);
                    break;
                }
                catch (Exception exception) when (
                    TryGetSqlNumber(exception) is 1205 or 2601 or 2627 && retry < 20)
                {
                    if (TryGetSqlNumber(exception) == 1205)
                    {
                        Interlocked.Increment(ref deadlocks);
                    }
                    else
                    {
                        Interlocked.Increment(ref uniquenessCollisions);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(retry + 1)).ConfigureAwait(false);
                }
            }
            firstCreateLatencies.Add(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
        }).ToArray();
        await ready.Task.ConfigureAwait(false);
        release.SetResult();
        await Task.WhenAll(firstCreateTasks).ConfigureAwait(false);
        var firstCreateElapsed = Stopwatch.GetElapsedTime(firstCreateStarted);
        for (var index = 0; index < warmups; index++)
        {
            await CentralDerivativeWindowIntegrationTests.ScheduleTransientAsync(centerSourceId, options)
                .ConfigureAwait(false);
        }

        var latencies = new ConcurrentBag<double>();
        var started = Stopwatch.GetTimestamp();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, measurements),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (_, cancellationToken) =>
            {
                var operationStarted = Stopwatch.GetTimestamp();
                for (var retry = 0; ; retry++)
                {
                    try
                    {
                        await CentralDerivativeWindowIntegrationTests.ScheduleTransientAsync(centerSourceId, options)
                            .ConfigureAwait(false);
                        break;
                    }
                    catch (Exception exception) when (
                        TryGetSqlNumber(exception) is 1205 or 2601 or 2627 && retry < 20)
                    {
                        if (TryGetSqlNumber(exception) == 1205)
                        {
                            Interlocked.Increment(ref deadlocks);
                        }
                        else
                        {
                            Interlocked.Increment(ref uniquenessCollisions);
                        }
                        await Task.Delay(TimeSpan.FromMilliseconds(retry + 1), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
                latencies.Add(Stopwatch.GetElapsedTime(operationStarted).TotalMilliseconds);
            }).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);

        int jobs;
        int inputRows;
        int identitySlots;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var jobIds = await db.CentralDerivativeJobs.AsNoTracking()
                .Where(job => job.SourceCentralArtifactId == centerSourceId
                    && job.RecipeName == CentralTransientRuntime.RecipeName)
                .Select(job => job.Id).ToArrayAsync().ConfigureAwait(false);
            jobs = jobIds.Length;
            inputRows = await db.CentralDerivativeJobInputs.CountAsync(input =>
                jobIds.Contains(input.CentralDerivativeJobId)).ConfigureAwait(false);
            identitySlots = await db.CentralTransientValidationIdentitySlots.CountAsync(slot =>
                jobIds.Contains(slot.CentralDerivativeJobId)).ConfigureAwait(false);
            foreach (var sourceId in sources)
            {
                await DisableSourceJobsAsync(db, sourceId).ConfigureAwait(false);
            }
        }
        Assert.AreEqual(1, jobs);
        Assert.AreEqual(5, inputRows);
        Assert.AreEqual(32, identitySlots);
        var orderedFirstCreateLatencies = firstCreateLatencies.Order().ToArray();
        var orderedLatencies = latencies.Order().ToArray();
        return new TransientSchedulingResult(
            concurrency,
            concurrency,
            firstCreateElapsed.TotalMilliseconds,
            orderedFirstCreateLatencies,
            warmups,
            measurements,
            elapsed.TotalMilliseconds,
            measurements / elapsed.TotalSeconds,
            Percentile(orderedLatencies, 0.5),
            Percentile(orderedLatencies, 0.95),
            orderedLatencies[^1],
            deadlocks,
            uniquenessCollisions,
            jobs,
            inputRows,
            identitySlots);
    }

    private static async Task<LeaseRetentionEvidence> MeasureLeaseAndRetentionRecoveryAsync(
        IntegrationTestFixture fixture,
        string runId)
    {
        var trials = new List<LeaseRetentionTrial>();
        for (var trial = 1; trial <= 5; trial++)
        {
            var objectKey = $"performance/central-window-processing/{runId}/P4-T{trial}.raw";
            var payload = new byte[] { 1, 0, 2, 0, 3, 0, 4, 0 };
            await PublishPayloadAsync(fixture, objectKey, payload).ConfigureAwait(false);
            var agentId = $"issue-101-p4-t{trial}-{runId}";
            Guid predecessorJobId;
            Guid[] sourceIds;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                sourceIds = (await SeedWindowSourcesAsync(
                    db, agentId, 5, 2, 2, CameraPixelFormat.Mono16,
                    $"minio://{ArtifactBucket}/{objectKey}", payload.LongLength,
                    Convert.ToHexString(SHA256.HashData(payload))).ConfigureAwait(false)).Values.ToArray();
                predecessorJobId = (await AddJobsAsync(db, agentId, [3]).ConfigureAwait(false)).Single();
                using var telemetry = new CentralDerivativeWorkerTelemetry();
                await CreateResolver(db, telemetry).ResolveAsync(
                    predecessorJobId, DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                await DisableOtherActiveJobsAsync(db, [predecessorJobId]).ConfigureAwait(false);
            }

            CentralDerivativeJobLease predecessorLease;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                predecessorLease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync($"issue-101-p4-original-{trial}", TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false))!;
                Assert.AreEqual(predecessorJobId, predecessorLease.JobId);
            }
            await using (var executionScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                    .ExecuteAsync(predecessorLease, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status);
            }

            Guid predecessorResultId;
            Guid predecessorArtifactId;
            string predecessorStorageReference;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var predecessor = await db.CentralDerivativeJobs.AsNoTracking()
                    .Include(job => job.ResultArtifact)
                    .SingleAsync(job => job.Id == predecessorJobId).ConfigureAwait(false);
                Assert.AreEqual(CentralDerivativeJobStatus.Completed, predecessor.Status);
                Assert.AreEqual(CentralArtifactObjectState.Available, predecessor.ResultArtifact!.ObjectState);
                predecessorResultId = predecessor.ResultCentralArtifactId!.Value;
                predecessorArtifactId = predecessor.ResultArtifact.ArtifactId;
                predecessorStorageReference = predecessor.ResultArtifact.StorageReference;
            }
            Assert.IsTrue(await ObjectExistsAsync(fixture, predecessorStorageReference).ConfigureAwait(false));

            var replacementVariant = $"central-rolling-mean-reprocessed-{runId}-{trial}";
            Guid replacementJobId;
            int pinsWhileActive;
            long pinnedBytes;
            DateTimeOffset pinStartedAtUtc;
            DateTimeOffset replacementCreatedAtUtc;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                replacementJobId = await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobOperationsService>()
                    .ReprocessAsync(
                        predecessorJobId,
                        new CentralDerivativeReprocessRequest(
                            BuiltInProcessingRecipes.RollingMean,
                            CaptureContractJson.SerializeToElement(new RollingMeanOptions()),
                            replacementVariant,
                            Supersede: true),
                        "issue-101-performance",
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var predecessor = await db.CentralDerivativeJobs.AsNoTracking()
                    .Include(job => job.ResultArtifact)
                    .SingleAsync(job => job.Id == predecessorJobId).ConfigureAwait(false);
                var replacement = await db.CentralDerivativeJobs.AsNoTracking()
                    .Include(job => job.Inputs)
                    .SingleAsync(job => job.Id == replacementJobId).ConfigureAwait(false);
                Assert.AreEqual(CentralDerivativeJobStatus.Completed, predecessor.Status);
                Assert.AreEqual(CentralArtifactObjectState.Available, predecessor.ResultArtifact!.ObjectState);
                Assert.AreEqual(predecessorJobId, replacement.PredecessorJobId);
                Assert.AreEqual(replacementVariant, replacement.TargetVariant);
                CollectionAssert.AreEqual(
                    new long[] { 1, 2, 3, 4, 5 },
                    replacement.Inputs.OrderBy(input => input.Ordinal)
                        .Select(input => input.CaptureSequence!.Value).ToArray());
                var references = new CentralArtifactRetentionReferences(db);
                pinsWhileActive = 0;
                foreach (var sourceId in sourceIds)
                {
                    pinsWhileActive += await references.IsHeldAsync(sourceId, CancellationToken.None)
                        .ConfigureAwait(false) ? 1 : 0;
                }
                Assert.AreEqual(5, pinsWhileActive);
                pinnedBytes = replacement.Inputs.Sum(input => input.ByteLength);
                pinStartedAtUtc = replacement.Inputs.Min(input => input.SelectedAtUtc);
                replacementCreatedAtUtc = replacement.CreatedAtUtc;
                Assert.IsTrue(await references.IsHeldAsync(predecessorResultId, CancellationToken.None)
                    .ConfigureAwait(false));
                await DisableOtherActiveJobsAsync(db, [replacementJobId]).ConfigureAwait(false);
            }
            Assert.IsTrue(await ObjectExistsAsync(fixture, predecessorStorageReference).ConfigureAwait(false));
            var heldResults = await Task.WhenAll(sourceIds.Select(sourceId =>
                ReleaseArtifactAsync(fixture, sourceId))).ConfigureAwait(false);
            Assert.IsTrue(heldResults.All(result => result == CentralArtifactRetentionResult.Held));

            CentralDerivativeJobLease replacementLease;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                replacementLease = (await scope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync($"issue-101-p4-replacement-{trial}", TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false))!;
                Assert.AreEqual(replacementJobId, replacementLease.JobId);
            }
            var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.CentralDerivativeJobs.Where(item => item.Id == replacementJobId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseExpiresAtUtc, expiredAt))
                    .ConfigureAwait(false);
                await db.CentralDerivativeJobAttempts.Where(item => item.CentralDerivativeJobId == replacementJobId
                        && item.AttemptNumber == 1)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LeaseExpiresAtUtc, expiredAt))
                    .ConfigureAwait(false);
            }

            CentralDerivativeJobLease recoveredLease;
            var recoveryStarted = Stopwatch.GetTimestamp();
            await using (var restartedScope = fixture.Factory.Services.CreateAsyncScope())
            {
                recoveredLease = (await restartedScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync($"issue-101-p4-restarted-{trial}", TimeSpan.FromMinutes(1), CancellationToken.None)
                    .ConfigureAwait(false))!;
            }
            var recoveryMilliseconds = Stopwatch.GetElapsedTime(recoveryStarted).TotalMilliseconds;
            Assert.AreEqual(replacementJobId, recoveredLease.JobId);
            Assert.AreEqual(2, recoveredLease.AttemptCount);

            await using (var executionScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                    .ExecuteAsync(recoveredLease, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status);
            }

            int leaseExpiredAttempts;
            int completedAttempts;
            int pinsAfterCompletion;
            Guid replacementResultId;
            Guid replacementArtifactId;
            string replacementStorageReference;
            double pinDurationMilliseconds;
            double backlogAgeMilliseconds;
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                leaseExpiredAttempts = await db.CentralDerivativeJobAttempts.CountAsync(item =>
                    item.CentralDerivativeJobId == replacementJobId
                    && item.Outcome == CentralDerivativeAttemptOutcome.LeaseExpired).ConfigureAwait(false);
                completedAttempts = await db.CentralDerivativeJobAttempts.CountAsync(item =>
                    item.CentralDerivativeJobId == replacementJobId
                    && item.Outcome == CentralDerivativeAttemptOutcome.Completed).ConfigureAwait(false);
                var predecessor = await db.CentralDerivativeJobs.AsNoTracking()
                    .Include(job => job.ResultArtifact)
                    .SingleAsync(job => job.Id == predecessorJobId).ConfigureAwait(false);
                var replacement = await db.CentralDerivativeJobs.AsNoTracking()
                    .Include(job => job.ResultArtifact)
                    .SingleAsync(job => job.Id == replacementJobId).ConfigureAwait(false);
                Assert.AreEqual(CentralDerivativeJobStatus.Superseded, predecessor.Status);
                Assert.AreEqual(replacementJobId, predecessor.SupersededByJobId);
                Assert.AreEqual(CentralArtifactObjectState.Available, predecessor.ResultArtifact!.ObjectState);
                Assert.AreEqual(CentralDerivativeJobStatus.Completed, replacement.Status);
                replacementResultId = replacement.ResultCentralArtifactId!.Value;
                replacementArtifactId = replacement.ResultArtifact!.ArtifactId;
                replacementStorageReference = replacement.ResultArtifact.StorageReference;
                pinDurationMilliseconds = Math.Max(0,
                    (replacement.CompletedAtUtc!.Value - pinStartedAtUtc).TotalMilliseconds);
                backlogAgeMilliseconds = Math.Max(0,
                    (replacement.CompletedAtUtc.Value - replacementCreatedAtUtc).TotalMilliseconds);
                Assert.AreNotEqual(predecessorResultId, replacementResultId);
                Assert.AreNotEqual(predecessorArtifactId, replacementArtifactId);
                Assert.AreNotEqual(predecessorStorageReference, replacementStorageReference);
                var references = new CentralArtifactRetentionReferences(db);
                pinsAfterCompletion = 0;
                foreach (var sourceId in sourceIds)
                {
                    pinsAfterCompletion += await references.IsHeldAsync(sourceId, CancellationToken.None)
                        .ConfigureAwait(false) ? 1 : 0;
                }
                Assert.IsFalse(await references.IsHeldAsync(predecessorResultId, CancellationToken.None)
                    .ConfigureAwait(false));
                Assert.AreEqual(0, await db.CentralDerivativeJobs.CountAsync(item =>
                    (item.Id == predecessorJobId || item.Id == replacementJobId)
                    && (item.Status == CentralDerivativeJobStatus.Pending
                        || item.Status == CentralDerivativeJobStatus.Leased
                        || item.Status == CentralDerivativeJobStatus.RetryableFailure)).ConfigureAwait(false));
            }
            Assert.AreEqual(1, leaseExpiredAttempts);
            Assert.AreEqual(1, completedAttempts);
            Assert.AreEqual(0, pinsAfterCompletion);
            Assert.IsTrue(await ObjectExistsAsync(fixture, predecessorStorageReference).ConfigureAwait(false));
            Assert.IsTrue(await ObjectExistsAsync(fixture, replacementStorageReference).ConfigureAwait(false));
            var releasedResults = await Task.WhenAll(sourceIds.Select(sourceId =>
                ReleaseArtifactAsync(fixture, sourceId))).ConfigureAwait(false);
            Assert.IsTrue(releasedResults.All(result => result == CentralArtifactRetentionResult.Released));
            Assert.IsTrue(await ObjectExistsAsync(fixture, predecessorStorageReference).ConfigureAwait(false));
            trials.Add(new LeaseRetentionTrial(
                trial,
                recoveryMilliseconds,
                predecessorJobId,
                replacementJobId,
                predecessorResultId,
                replacementResultId,
                BacklogBeforeExpiry: 1,
                BacklogAfterRestart: 1,
                FinalBacklog: 0,
                PinsWhileActive: pinsWhileActive,
                PinnedBytes: pinnedBytes,
                PinDurationMilliseconds: pinDurationMilliseconds,
                BacklogAgeMilliseconds: backlogAgeMilliseconds,
                DrainJobsPerSecond: recoveryMilliseconds <= 0 ? 0 : 1000 / recoveryMilliseconds,
                PinsAfterCompletion: pinsAfterCompletion,
                RetentionReleased: releasedResults.Count(result => result == CentralArtifactRetentionResult.Released),
                LeaseExpiredAttempts: leaseExpiredAttempts,
                CompletedAttempts: completedAttempts,
                PredecessorStatusBeforeReplacementCompletion: CentralDerivativeJobStatus.Completed.ToString(),
                PredecessorStatusAfterReplacementCompletion: CentralDerivativeJobStatus.Superseded.ToString(),
                PredecessorArtifactAvailable: true,
                PredecessorObjectAvailableAfterReplacementAndRetention: true,
                ReplacementOutputUnique: predecessorArtifactId != replacementArtifactId
                    && predecessorStorageReference != replacementStorageReference));
        }
        return new LeaseRetentionEvidence(
            Trials: trials.Count,
            SourcesPerTrial: 5,
            RecoveryMedianMilliseconds: Percentile(trials.Select(trial => trial.RecoveryMilliseconds).Order().ToArray(), 0.5),
            RecoveryMinimumMilliseconds: trials.Min(trial => trial.RecoveryMilliseconds),
            RecoveryMaximumMilliseconds: trials.Max(trial => trial.RecoveryMilliseconds),
            Results: trials,
            Boundary: "Each trial completes a production rolling predecessor, creates a distinct superseding rolling replacement through the operations service, expires its durable SQL lease/attempt, reclaims it through a fresh scope, completes it, verifies the predecessor artifact and object remain available, and concurrently releases all five terminal source holds.");
    }

    private static async Task<Dictionary<long, Guid>> SeedWindowSourcesAsync(
        ApplicationDbContext db,
        string agentId,
        int count,
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        string storageReference,
        long byteLength,
        string? checksum = null,
        long firstSequence = 1)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var devicePublicId = Guid.NewGuid();
        var profileSha = HashText($"{agentId}-profiles");
        var rawOptions = CaptureContractJson.SerializeToElement(new { });
        var sources = new Dictionary<long, Guid>();
        for (var index = 0; index < count; index++)
        {
            var sequence = checked(firstSequence + index);
            var captured = now.AddMilliseconds(sequence);
            var frame = new CentralFrame
            {
                RegistrationId = Guid.NewGuid(),
                DevicePublicId = devicePublicId,
                ObservatoryId = Guid.NewGuid(),
                AgentId = agentId,
                FrameId = Guid.NewGuid(),
                CapturedAtUtc = captured,
                FirstReceivedAtUtc = captured,
                RigProfileVersion = 1,
                RigId = $"{agentId}-rig",
                CaptureSequence = sequence
            };
            frame.Timing = new CentralCaptureTiming
            {
                RequestedStartUtc = captured.AddSeconds(-2),
                ExposureStartedUtc = captured.AddSeconds(-1),
                ExposureEndedUtc = captured.AddMilliseconds(-100),
                ReadoutCompletedUtc = captured.AddMilliseconds(-50),
                DurableIngressUtc = captured
            };
            frame.Control = new CentralCaptureControl
            {
                RequestedExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                EffectiveExposureTicks = TimeSpan.FromMilliseconds(900).Ticks,
                RequestedGain = 100,
                EffectiveGain = 100,
                RequestedOffset = 1,
                EffectiveOffset = 1,
                TemperatureSetpointC = -5,
                EffectiveTemperatureC = -5
            };
            foreach (var kind in Enum.GetValues<CentralProfileKind>())
            {
                frame.Profiles.Add(new CentralCaptureProfile
                {
                    Kind = kind,
                    Name = $"issue-101-{kind}",
                    Version = "1",
                    Sha256 = profileSha
                });
            }
            var artifact = new CentralArtifact
            {
                Frame = frame,
                CentralFrameId = frame.Id,
                ArtifactId = Guid.NewGuid(),
                DevicePublicId = devicePublicId,
                Role = FrameArtifactRole.Raw,
                RecipeVersion = "issue-101-raw-v1",
                ManifestSchemaVersion = ArtifactManifestV2.CurrentSchemaVersion,
                MediaType = "application/x-hvo-linear-frame",
                ByteLength = byteLength,
                ChecksumSha256 = checksum ?? new string('0', 64),
                StorageReference = storageReference,
                ReceivedAtUtc = captured,
                IdempotencyKey = HashText($"{agentId}-{sequence}"),
                SourceId = "issue-101-performance",
                Variant = "native",
                CreatedUtc = captured,
                ObjectState = CentralArtifactObjectState.Available,
                ReconstructionState = CentralReconstructionState.Complete,
                ReconciledAtUtc = captured,
                Layout = new CentralArtifactLayout
                {
                    Width = width,
                    Height = height,
                    StrideBytes = checked(width * 2),
                    PixelFormat = pixelFormat.ToString(),
                    ByteOrder = FrameByteOrder.LittleEndian.ToString(),
                    SampleDepthBits = 16,
                    ContainerDepthBits = 16,
                    Packing = FrameSamplePacking.ByteAligned.ToString(),
                    CfaPattern = (pixelFormat == CameraPixelFormat.BayerRggb16
                        ? ColorFilterArrayPattern.Rggb
                        : ColorFilterArrayPattern.None).ToString(),
                    BlackLevel = pixelFormat == CameraPixelFormat.BayerRggb16 ? 64 : 0,
                    WhiteLevel = pixelFormat == CameraPixelFormat.BayerRggb16 ? 16383 : ushort.MaxValue,
                    ByteLength = byteLength
                },
                Recipe = new CentralArtifactRecipe
                {
                    Name = "raw-capture",
                    SemanticVersion = "1.0.0",
                    ImplementationVersion = "issue-101-v1",
                    OptionsJson = CaptureContractJson.Canonicalize(rawOptions).GetRawText(),
                    OptionsSha256 = CaptureContractJson.ComputeCanonicalJsonSha256(rawOptions)
                }
            };
            sources[sequence] = artifact.Id;
            db.CentralFrames.Add(frame);
            db.CentralArtifacts.Add(artifact);
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return sources;
    }

    private static async Task ExecuteRollingJobsAsync(
        IntegrationTestFixture fixture,
        int count,
        ConcurrentBag<double>? latencies)
    {
        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            CentralDerivativeJobLease lease;
            await using (var claimScope = fixture.Factory.Services.CreateAsyncScope())
            {
                lease = (await claimScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobService>()
                    .ClaimNextAsync("issue-101-rolling-performance", TimeSpan.FromMinutes(10), CancellationToken.None)
                    .ConfigureAwait(false))!;
            }
            Assert.IsNotNull(lease);
            Assert.AreEqual(BuiltInProcessingRecipes.RollingMean, lease.RecipeName);
            Assert.AreEqual(5, lease.Inputs!.Count);
            await using (var executionScope = fixture.Factory.Services.CreateAsyncScope())
            {
                var result = await executionScope.ServiceProvider.GetRequiredService<ICentralDerivativeJobExecutor>()
                    .ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
            }
            latencies?.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static async Task PublishPayloadAsync(
        IntegrationTestFixture fixture,
        string objectKey,
        byte[] payload)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(ArtifactBucket)).ConfigureAwait(false))
        {
            await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(ArtifactBucket)).ConfigureAwait(false);
        }
        await using var stream = new MemoryStream(payload, writable: false);
        await minio.PutObjectAsync(new PutObjectArgs()
            .WithBucket(ArtifactBucket)
            .WithObject(objectKey)
            .WithStreamData(stream)
            .WithObjectSize(payload.LongLength)
            .WithContentType("application/x-hvo-linear-frame")).ConfigureAwait(false);
    }

    private static byte[] CreatePayload(
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        int seed)
    {
        var stride = checked(width * 2);
        var payload = GC.AllocateUninitializedArray<byte>(checked(stride * height));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = pixelFormat == CameraPixelFormat.Mono16
                    ? (ushort)((seed + 257L * (y * (long)width + x)) & 0xFFFF)
                    : (ushort)((64 + seed + 31 * x + 17 * y + 997 * ((y & 1) * 2 + (x & 1))) & 0x3FFF);
                var offset = y * stride + x * 2;
                payload[offset] = (byte)value;
                payload[offset + 1] = (byte)(value >> 8);
            }
        }
        return payload;
    }

    private static byte[] CreateUniformPayload(int width, int height, ushort value)
    {
        var payload = GC.AllocateUninitializedArray<byte>(checked(width * height * sizeof(ushort)));
        for (var offset = 0; offset < payload.Length; offset += sizeof(ushort))
        {
            payload[offset] = (byte)value;
            payload[offset + 1] = (byte)(value >> 8);
        }
        return payload;
    }

    private static byte[] CreatePositiveTransientPayload(byte[] background, int width, int height)
    {
        var payload = background.ToArray();
        const ushort signal = 10_000;
        var startX = width / 3;
        var startY = height / 2;
        for (var y = startY; y < Math.Min(height, startY + 6); y++)
        {
            for (var x = startX; x < Math.Min(width, startX + 200); x++)
            {
                var offset = checked((y * width + x) * sizeof(ushort));
                payload[offset] = (byte)(signal & 0xFF);
                payload[offset + 1] = (byte)(signal >> 8);
            }
        }
        return payload;
    }

    private static async Task<CentralArtifactRetentionResult> ReleaseArtifactAsync(
        IntegrationTestFixture fixture,
        Guid sourceId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ICentralArtifactRetentionService>()
            .ReleaseAsync(sourceId, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task<bool> ObjectExistsAsync(
        IntegrationTestFixture fixture,
        string storageReference)
    {
        var prefix = $"minio://{ArtifactBucket}/";
        Assert.IsTrue(storageReference.StartsWith(prefix, StringComparison.Ordinal));
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        try
        {
            await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(ArtifactBucket)
                .WithObject(storageReference[prefix.Length..])).ConfigureAwait(false);
            return true;
        }
        catch (Minio.Exceptions.MinioException exception) when (MinioObjectVerification.IsNotFound(exception))
        {
            return false;
        }
    }

    private static CentralDerivativeWindowResolver CreateResolver(
        ApplicationDbContext db,
        CentralDerivativeWorkerTelemetry telemetry)
        => new(db, telemetry, TimeProvider.System, NullLogger<CentralDerivativeWindowResolver>.Instance);

    private static DbContextOptions<ApplicationDbContext> CreateOptions(string connectionString)
        => new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;

    private static int? TryGetSqlNumber(Exception exception)
        => exception.GetBaseException() is SqlException sqlException ? sqlException.Number : null;

    private static Task<int> DisableJobsAsync(ApplicationDbContext db, Guid[] jobIds)
        => db.CentralDerivativeJobs.Where(job => jobIds.Contains(job.Id)
                && (job.Status == CentralDerivativeJobStatus.Waiting
                    || job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null));

    private static Task<int> DisableSourceJobsAsync(ApplicationDbContext db, Guid sourceId)
        => db.CentralDerivativeJobs.Where(job => job.SourceCentralArtifactId == sourceId
                && (job.Status == CentralDerivativeJobStatus.Waiting
                    || job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null));

    private static Task<int> DisableOtherActiveJobsAsync(ApplicationDbContext db, Guid[] allowedJobIds)
        => db.CentralDerivativeJobs.Where(job => !allowedJobIds.Contains(job.Id)
                && (job.Status == CentralDerivativeJobStatus.Waiting
                    || job.Status == CentralDerivativeJobStatus.Pending
                    || job.Status == CentralDerivativeJobStatus.RetryableFailure))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.TerminalFailure)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null));

    private static string GetEvidenceDirectory(string revision)
    {
        var boundedRevision = revision.Length > 12 ? revision[..12] : revision;
        return Path.Combine(
            FindRepositoryRoot(),
            "tests",
            "HVO.SkyMonitor.IntegrationTests",
            "TestResults",
            "central-window-processing",
            boundedRevision);
    }

    private static double Percentile(double[] ordered, double percentile)
    {
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static string HashText(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static async Task<string> ReadSqlVersionAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CONVERT(nvarchar(4000), SERVERPROPERTY('ProductVersion'));";
        return (string?)await command.ExecuteScalarAsync().ConfigureAwait(false) ?? "unknown";
    }

    private static string ReadProcessorModel()
        => File.Exists("/proc/cpuinfo")
            ? File.ReadLines("/proc/cpuinfo")
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))?
                .Split(':', 2)[1].Trim() ?? "unknown"
            : "unknown";

    private static string RunGit(string repositoryRoot, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }
        _ = process.Start();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : string.Empty;
    }

    private static string CreateDirtyFingerprint(string repositoryRoot)
    {
        var value = new StringBuilder(RunGit(repositoryRoot, "diff", "--binary", "HEAD"));
        var untracked = RunGit(repositoryRoot, "ls-files", "--others", "--exclude-standard", "-z")
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Order(StringComparer.Ordinal);
        foreach (var relativePath in untracked)
        {
            var path = Path.Combine(repositoryRoot, relativePath);
            value.Append('\n').Append(relativePath).Append(':')
                .Append(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        }
        return HashText(value.ToString());
    }

    private static TestedAssemblyEvidence CreateAssemblyEvidence(Assembly assembly)
        => new(
            assembly.GetName().Name ?? "unknown",
            assembly.Location,
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            assembly.ManifestModule.ModuleVersionId,
            assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "unknown");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root could not be located.");
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public void Reset() => Interlocked.Exchange(ref _count, 0);
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref _count);
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed record WindowPerformanceEvidence(
        string Schema,
        WindowRevision Revision,
        DateTimeOffset RecordedAtUtc,
        WindowEnvironment Environment,
        string Workload,
        IReadOnlyList<WindowScalingResult> Results,
        string Interpretation);

    private sealed record TestedAssemblyEvidence(
        string Name,
        string Path,
        string Sha256,
        Guid ModuleVersionId,
        string Configuration);

    private sealed record WindowRevision(
        string EvidenceDirectoryRevision,
        string Base,
        string Candidate,
        string Branch,
        bool Dirty,
        string DirtyFingerprintSha256);

    private sealed record WindowEnvironment(
        string OS,
        string Architecture,
        string Runtime,
        string BuildConfiguration,
        string Topology);

    private sealed record WindowScalingResult(
        int HistorySize,
        IReadOnlyList<int> WindowOffsets,
        int Warmups,
        int MeasuredJobs,
        double ResolutionTotalMilliseconds,
        double ResolutionMillisecondsPerJob,
        double ResolutionMedianMilliseconds,
        double ResolutionP95Milliseconds,
        double ResolutionMaximumMilliseconds,
        double CpuTotalMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        int SqlCommands,
        int SelectedInputs,
        long SelectedBytes,
        int ObjectRequests,
        bool SequenceIndexPresent,
        bool SequenceIndexUsedByPlan,
        int LogicalReads,
        string PlanSha256,
        int DurablePendingJobs);

    private sealed record SqlSelectionEvidence(bool UsesSequenceIndex, int LogicalReads, string PlanSha256);

    private sealed record SqlTextEvidence(
        string ExpectedIndex,
        bool IndexUsed,
        int LogicalReads,
        string PlanSha256,
        string QuerySha256);

    private sealed record TransientMetadataScalingResult(
        int CandidateArtifacts,
        int BatchLimit,
        int ScheduledJobs,
        int IdentitySlots,
        int InputRequirements,
        int ArtifactInputs,
        int CanonicalInputs,
        int ClaimAttempts,
        double ScheduleElapsedMilliseconds,
        double ScheduleCpuMilliseconds,
        long ScheduleAllocatedBytes,
        int ScheduleSqlStatements,
        int ClaimWarmups,
        int MeasuredClaims,
        double ClaimMedianMilliseconds,
        double ClaimP95Milliseconds,
        double ClaimMaximumMilliseconds,
        double ClaimsPerSecond,
        double ClaimCpuMilliseconds,
        long ClaimAllocatedBytes,
        int ClaimSqlStatements,
        SqlTextEvidence RetrospectivePlan,
        SqlTextEvidence ClaimPlan,
        int PendingBeforeClaims,
        int LeasedAfterClaims);

    private sealed record TransitionEvidence(
        TransitionResult PartialArrival,
        TransitionResult DelayedArrivalAfterRestart,
        TransitionResult MissingAfterRestart,
        string Correctness);

    private sealed record TransitionResult(
        string Transition,
        double ElapsedMilliseconds,
        double SourceWaitAgeMilliseconds,
        string FinalStatus,
        string? ReasonCode,
        int DurableInputRows,
        int RetentionPins,
        int MissingRequirements,
        IReadOnlyList<long> OrderedCaptureSequences);

    private sealed record SchedulerNotificationResult(
        int Concurrency,
        int Warmups,
        int Notifications,
        double ElapsedMilliseconds,
        double NotificationsPerSecond,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        int SqlDeadlocks,
        int UniqueConstraintCollisions,
        int FinalRollingJobs,
        int FinalInputRows,
        IReadOnlyList<long> OrderedCaptureSequences,
        string FinalStatus,
        int ProcessingEvidenceRows,
        int ResultRows);

    private sealed record RollingExecutionResult(
        string Workload,
        int Width,
        int Height,
        string PixelFormat,
        long InputArtifactBytes,
        string InputChecksumSha256,
        int Warmups,
        int MeasuredJobs,
        double ElapsedMilliseconds,
        double EndToEndMedianMilliseconds,
        double EndToEndP95Milliseconds,
        double EndToEndMaximumMilliseconds,
        double RecipeMedianMilliseconds,
        double RecipeP95Milliseconds,
        double RecipeMaximumMilliseconds,
        double JobsPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long PeakWorkingSetBytes,
        long LohBeforeBytes,
        long LohAfterBytes,
        int LogicalInputObjectRequests,
        int InputStatRequests,
        int InputChecksumGetRequests,
        int InputPayloadGetRequests,
        long PhysicalInputTransferredBytes,
        long InputBytes,
        long OutputBytes,
        IReadOnlyList<string> OutputChecksumsSha256,
        int VerifiedOutputObjects,
        int DistinctCompatibilitySnapshots,
        bool OrderedLineageVerified);

    private sealed record TransientExecutionPerformanceResult(
        int Width,
        int Height,
        string PixelFormat,
        long InputArtifactBytes,
        string InputChecksumSha256,
        string TargetChecksumSha256,
        string FixtureDisclosure,
        int Warmups,
        int MeasuredJobs,
        double ElapsedMilliseconds,
        double EndToEndMedianMilliseconds,
        double EndToEndP95Milliseconds,
        double EndToEndMaximumMilliseconds,
        double JobsPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long PeakWorkingSetBytes,
        long ManagedLiveBeforeBytes,
        long ManagedLiveAfterBytes,
        long LohBeforeBytes,
        long LohAfterBytes,
        long LogicalLiveInputBytes,
        int LogicalLiveInputBuffers,
        long SelectedInputBytes,
        long AttemptInputBytes,
        int ExtractionReceiptCount,
        int ExtractionSourceRows,
        int EventVersionCount,
        double CandidatesPerFrame,
        IReadOnlyList<string> ExtractionReceiptChecksumsSha256,
        bool OrderedTemporalLineageVerified,
        TransientAdoptionRecoveryResult AdoptionRecovery,
        TransientProtocolSnapshot Protocol);

    private sealed record TransientAdoptionRecoveryResult(
        double ElapsedMilliseconds,
        Guid JobId,
        Guid EventVersionId,
        string ReasonCode,
        int EventVersionCount,
        bool ExactVersionPreserved);

    private sealed record TransientProtocolSnapshot(
        long SqlCommands,
        long TransactionsStarted,
        long TransactionsCommitted,
        long TransactionsRolledBack,
        long ObjectVerifications,
        long ObjectPayloadReads,
        long ChecksumVerificationBytes,
        long PayloadReadBytes,
        long PhysicalInputReadBytes);

    private sealed record TransientSchedulingResult(
        int Concurrency,
        int FirstCreateContenders,
        double FirstCreateElapsedMilliseconds,
        IReadOnlyList<double> FirstCreateLatenciesMilliseconds,
        int Warmups,
        int Measurements,
        double ElapsedMilliseconds,
        double OperationsPerSecond,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        int SqlDeadlocks,
        int UniqueConstraintCollisions,
        int FinalJobs,
        int FinalInputRows,
        int FinalIdentitySlots);

    private sealed record LeaseRetentionEvidence(
        int Trials,
        int SourcesPerTrial,
        double RecoveryMedianMilliseconds,
        double RecoveryMinimumMilliseconds,
        double RecoveryMaximumMilliseconds,
        IReadOnlyList<LeaseRetentionTrial> Results,
        string Boundary);

    private sealed record LeaseRetentionTrial(
        int Trial,
        double RecoveryMilliseconds,
        Guid PredecessorJobId,
        Guid ReplacementJobId,
        Guid PredecessorResultCentralArtifactId,
        Guid ReplacementResultCentralArtifactId,
        int BacklogBeforeExpiry,
        int BacklogAfterRestart,
        int FinalBacklog,
        int PinsWhileActive,
        long PinnedBytes,
        double PinDurationMilliseconds,
        double BacklogAgeMilliseconds,
        double DrainJobsPerSecond,
        int PinsAfterCompletion,
        int RetentionReleased,
        int LeaseExpiredAttempts,
        int CompletedAttempts,
        string PredecessorStatusBeforeReplacementCompletion,
        string PredecessorStatusAfterReplacementCompletion,
        bool PredecessorArtifactAvailable,
        bool PredecessorObjectAvailableAfterReplacementAndRetention,
        bool ReplacementOutputUnique);

    private sealed class TransientOnlyRecipeCatalog : ICentralDerivativeRecipeCatalog
    {
        private readonly CentralDerivativeRecipe recipe;

        public TransientOnlyRecipeCatalog(CentralTransientOptions options)
        {
            recipe = new CentralDerivativeRecipeCatalog(options).GetRequiredRecipes(options.SourceRole)
                .Single(item => item.RecipeName == CentralTransientRuntime.RecipeName);
        }

        public IReadOnlyList<CentralDerivativeRecipe> GetRequiredRecipes(FrameArtifactRole sourceRole)
            => sourceRole == recipe.SourceRole ? [recipe] : [];
    }

    private sealed class TransientProtocolCounter :
        IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>,
        IDisposable
    {
        private const string CommandExecuted = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";
        private const string TransactionStarted = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionStarted";
        private const string TransactionCommitted = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted";
        private const string TransactionRolledBack = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionRolledBack";
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private readonly IDisposable allListeners;
        private long sqlCommands;
        private long transactionsStarted;
        private long transactionsCommitted;
        private long transactionsRolledBack;
        private long objectVerifications;
        private long objectPayloadReads;
        private long checksumVerificationBytes;
        private long payloadReadBytes;
        private int active;

        public TransientProtocolCounter()
        {
            allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void Start() => Volatile.Write(ref active, 1);

        public TransientProtocolSnapshot Stop()
        {
            Volatile.Write(ref active, 0);
            return new(
                Interlocked.Read(ref sqlCommands),
                Interlocked.Read(ref transactionsStarted),
                Interlocked.Read(ref transactionsCommitted),
                Interlocked.Read(ref transactionsRolledBack),
                Interlocked.Read(ref objectVerifications),
                Interlocked.Read(ref objectPayloadReads),
                Interlocked.Read(ref checksumVerificationBytes),
                Interlocked.Read(ref payloadReadBytes),
                checked(Interlocked.Read(ref checksumVerificationBytes)
                    + Interlocked.Read(ref payloadReadBytes)));
        }

        public void RecordVerification(long bytes)
        {
            if (Volatile.Read(ref active) != 1) return;
            Interlocked.Increment(ref objectVerifications);
            Interlocked.Add(ref checksumVerificationBytes, bytes);
        }

        public void RecordPayloadRead(long bytes)
        {
            if (Volatile.Read(ref active) != 1) return;
            Interlocked.Increment(ref objectPayloadReads);
            Interlocked.Add(ref payloadReadBytes, bytes);
        }

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "Microsoft.EntityFrameworkCore")
            {
                subscriptions.Add(value.Subscribe(this, static eventName => eventName is
                    CommandExecuted or TransactionStarted or TransactionCommitted or TransactionRolledBack));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Volatile.Read(ref active) != 1)
            {
                return;
            }
            switch (value.Key)
            {
                case CommandExecuted:
                    Interlocked.Increment(ref sqlCommands);
                    return;
                case TransactionStarted:
                    Interlocked.Increment(ref transactionsStarted);
                    return;
                case TransactionCommitted:
                    Interlocked.Increment(ref transactionsCommitted);
                    return;
                case TransactionRolledBack:
                    Interlocked.Increment(ref transactionsRolledBack);
                    return;
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            allListeners.Dispose();
            foreach (var subscription in subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    private sealed class ObservedCentralArtifactObjectReader(
        ICentralArtifactObjectReader inner,
        TransientProtocolCounter protocol) : ICentralArtifactObjectReader
    {
        public async Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact,
            CancellationToken cancellationToken)
        {
            var snapshot = await inner.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            protocol.RecordVerification(snapshot.ByteLength);
            return snapshot;
        }

        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact,
            string storageETag,
            CancellationToken cancellationToken)
            => inner.IsCurrentGenerationAsync(artifact, storageETag, cancellationToken);

        public async Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot,
            Stream destination,
            CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
        {
            await inner.CopyToAsync(snapshot, destination, range, cancellationToken).ConfigureAwait(false);
            protocol.RecordPayloadRead(range?.Length ?? snapshot.ByteLength);
        }
    }
}
