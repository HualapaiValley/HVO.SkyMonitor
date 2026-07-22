using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.LogicHost.Services.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[DoNotParallelize]
public sealed class CentralTransientReviewPerformanceTests
{
    private const string Bucket = "skymonitor-artifacts";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Manual")]
    public async Task Issue118ReviewHistoryContentionAndReprocessing_RecordsAcceptanceEvidence()
    {
        Assert.AreEqual(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        Assert.AreEqual("Release", typeof(CentralTransientReviewPerformanceTests).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration);
        var fixture = AssemblyHooks.Fixture;
        _ = fixture.Factory.Services;
        var builder = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            InitialCatalog = $"SkyMonitorIssue118ReviewPerformance_{Guid.NewGuid():N}"
        };
        var counter = new CountingCommandInterceptor();
        var dbOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(builder.ConnectionString)
            .AddInterceptors(counter)
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ApplicationDbContext(dbOptions);
        var minio = fixture.Factory.Services.GetRequiredService<IMinioClient>();
        var publishedKeys = new List<string>();
        try
        {
            if (!await minio.BucketExistsAsync(new BucketExistsArgs().WithBucket(Bucket)).ConfigureAwait(false))
            {
                await minio.MakeBucketAsync(new MakeBucketArgs().WithBucket(Bucket)).ConfigureAwait(false);
            }
            await db.Database.MigrateAsync().ConfigureAwait(false);
            var reprocessing = await MeasureReprocessingAsync(db, minio, publishedKeys).ConfigureAwait(false);
            var history = await MeasureHistoryAsync(db, dbOptions, builder.ConnectionString, counter)
                .ConfigureAwait(false);

            var repositoryRoot = FindRepositoryRoot();
            var commit = Git(repositoryRoot, "rev-parse", "HEAD");
            var dirty = !string.IsNullOrWhiteSpace(Git(repositoryRoot, "status", "--porcelain"));
            var evidenceRevision = dirty ? "local-dirty" : Environment.GetEnvironmentVariable("GITHUB_SHA") ?? commit;
            var evidence = new
            {
                Schema = "hvo-central-transient-review-performance-v1",
                Issue = 118,
                Revision = new
                {
                    Candidate = commit,
                    Base = Git(repositoryRoot, "merge-base", "HEAD", "main"),
                    Branch = Git(repositoryRoot, "branch", "--show-current"),
                    Dirty = dirty,
                    DirtyFingerprintSha256 = DirtyFingerprint(repositoryRoot)
                },
                RecordedAtUtc = DateTimeOffset.UtcNow,
                Command = "dotnet test tests/HVO.SkyMonitor.IntegrationTests/HVO.SkyMonitor.IntegrationTests.csproj --configuration Release --arch x64 --filter FullyQualifiedName~CentralTransientReviewPerformanceTests.Issue118ReviewHistoryContentionAndReprocessing_RecordsAcceptanceEvidence",
                Environment = new
                {
                    Framework = RuntimeInformation.FrameworkDescription,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                    Environment.ProcessorCount,
                    AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                    Storage = "Docker-backed SQL Server and MinIO Testcontainers",
                    SqlServer = builder.DataSource,
                    fixture.MinioEndpoint
                },
                Workloads = new
                {
                    W3M = history,
                    W4 = history.Contention,
                    Reprocessing = reprocessing
                },
                Boundary = new
                {
                    Review = "Production admin-authorized keyset list service over 10,000 W3M events and immutable event-version rows, plus one separately measured reprocessing event.",
                    Reprocessing = "Production schedule, SQL claim, five real MinIO object verifications, deterministic reassessment, immutable event-version append, derivative scheduling, and generic completion.",
                    Reconstruction = "W1/W2 reconstruction CPU, allocations, memory, and five product bytes are recorded by Linear16TransientReconstructionPerformanceTests.",
                    Baseline = "Net-new review and reprocessing paths; before is N/A. These are absolute candidate baselines."
                },
                Result = "The run is accepted only when all rows are paged exactly once, the keyset plan uses the review index, all reprocessing jobs converge, checksums remain verified, and durable active backlog is zero."
            };
            var directory = Path.Combine(repositoryRoot, "TestResults", "issue-118",
                evidenceRevision[..Math.Min(12, evidenceRevision.Length)]);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "central-review-reprocessing-performance.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(evidence, JsonOptions)).ConfigureAwait(false);
            TestContext.WriteLine($"Issue #118 LogicHost evidence: {path}");

            Assert.AreEqual(10_000, history.HistoryRows);
            Assert.AreEqual(10_001, history.EventsRead);
            Assert.IsTrue(history.QueryPlanUsesEventCreatedIndex);
            Assert.AreEqual("1,4,8", string.Join(',', history.Contention.Select(item => item.Concurrency)));
            Assert.AreEqual(30, reprocessing.CompletedJobs);
            Assert.AreEqual(150, reprocessing.DerivativeCount);
            Assert.AreEqual(0, reprocessing.ActiveBacklog);
            Assert.AreEqual(reprocessing.ExpectedVerifiedBytes, reprocessing.VerifiedBytes);
        }
        finally
        {
            if (await db.Database.CanConnectAsync().ConfigureAwait(false))
            {
                var derivativeReferences = await db.CentralTransientDerivativeOutputIntents.AsNoTracking()
                    .Where(item => item.StorageReference.StartsWith("minio://skymonitor-artifacts/"))
                    .Select(item => item.StorageReference).ToArrayAsync().ConfigureAwait(false);
                publishedKeys.AddRange(derivativeReferences.Select(item =>
                    item["minio://skymonitor-artifacts/".Length..]));
            }
            foreach (var objectKey in publishedKeys)
            {
                await minio.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(Bucket).WithObject(objectKey))
                    .ConfigureAwait(false);
            }
            await db.Database.EnsureDeletedAsync().ConfigureAwait(false);
        }
    }

    private static async Task<ReprocessingResult> MeasureReprocessingAsync(
        ApplicationDbContext db,
        IMinioClient minio,
        List<string> publishedKeys)
    {
        const int operations = 30;
        var fixture = CentralTransientPersistenceFixture.Create();
        var seeded = await CentralTransientEventPersistenceIntegrationTests.SeedAsync(db, fixture)
            .ConfigureAwait(false);
        db.ChangeTracker.Clear();
        foreach (var artifact in seeded.Artifacts)
        {
            var frame = artifact.Frame!;
            await db.CentralFrames.Where(item => item.Id == frame.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    item => item.RigId, "issue-118-performance-rig")).ConfigureAwait(false);
            db.CentralCaptureTimings.Add(new CentralCaptureTiming
            {
                CentralFrameId = frame.Id,
                RequestedStartUtc = frame.CapturedAtUtc,
                ExposureStartedUtc = frame.CapturedAtUtc,
                ExposureEndedUtc = frame.FirstReceivedAtUtc,
                ReadoutCompletedUtc = frame.FirstReceivedAtUtc,
                DurableIngressUtc = frame.FirstReceivedAtUtc
            });
            db.CentralCaptureControls.Add(new CentralCaptureControl
            {
                CentralFrameId = frame.Id,
                RequestedExposureTicks = TimeSpan.FromSeconds(20).Ticks,
                EffectiveExposureTicks = TimeSpan.FromSeconds(20).Ticks,
                RequestedGain = 150,
                EffectiveGain = 150
            });
            foreach (var kind in Enum.GetValues<CentralProfileKind>())
            {
                db.CentralCaptureProfiles.Add(new CentralCaptureProfile
                {
                    CentralFrameId = frame.Id,
                    Kind = kind,
                    Name = $"issue-118-{kind}",
                    Version = "1",
                    Sha256 = kind switch
                    {
                        CentralProfileKind.Rig => HVO.SkyMonitor.TestSupport.ProcessingConformanceFixture.RigProfileSha256,
                        CentralProfileKind.Calibration => HVO.SkyMonitor.TestSupport.ProcessingConformanceFixture.CalibrationProfileSha256,
                        CentralProfileKind.Mask => HVO.SkyMonitor.TestSupport.ProcessingConformanceFixture.MaskProfileSha256,
                        CentralProfileKind.Sensor => HVO.SkyMonitor.TestSupport.ProcessingConformanceFixture.SensorProfileSha256,
                        _ => HVO.SkyMonitor.TestSupport.ProcessingConformanceFixture.ProcessingProfileSha256
                    }
                });
            }
            db.CentralArtifactLayouts.Add(new CentralArtifactLayout
            {
                CentralArtifactId = artifact.Id,
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
                ByteLength = artifact.ByteLength
            });
        }
        await db.SaveChangesAsync().ConfigureAwait(false);
        var artifactIds = seeded.Artifacts.Select(item => item.Id).ToArray();
        await db.CentralArtifacts.Where(item => artifactIds.Contains(item.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                item => item.SourceId, "issue-118-performance")).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        _ = await new CentralTransientEventPersistence(db).AppendAsync(
            fixture.Request with { CentralDerivativeJobId = seeded.JobId }, CancellationToken.None).ConfigureAwait(false);
        await db.CentralDerivativeJobs.Where(job => job.Id == seeded.JobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, CentralDerivativeJobStatus.Completed)
                .SetProperty(job => job.CompletedAtUtc, DateTimeOffset.UtcNow)
                .SetProperty(job => job.AvailableAtUtc, (DateTimeOffset?)null)
                .SetProperty(job => job.StateReasonCode, "performance.fixture-completed"))
            .ConfigureAwait(false);
        foreach (var artifact in seeded.Artifacts)
        {
            var objectKey = artifact.StorageReference["minio://skymonitor-artifacts/".Length..];
            var payload = fixture.Payloads[artifact.ArtifactId];
            await using var stream = new MemoryStream(payload, writable: false);
            await minio.PutObjectAsync(new PutObjectArgs().WithBucket(Bucket).WithObject(objectKey)
                .WithStreamData(stream).WithObjectSize(payload.LongLength)
                .WithContentType("application/octet-stream")).ConfigureAwait(false);
            publishedKeys.Add(objectKey);
        }

        using var retrievalTelemetry = new CentralArtifactRetrievalTelemetry();
        var reader = new CountingArtifactObjectReader(new CentralArtifactObjectReader(
            minio, retrievalTelemetry, TimeProvider.System, NullLogger<CentralArtifactObjectReader>.Instance));
        var jobService = new CentralDerivativeJobService(db, TimeProvider.System);
        var scheduler = new CentralTransientDerivativeScheduler(db);
        using var workerTelemetry = new CentralDerivativeWorkerTelemetry();
        var inputReader = new CentralDerivativeJobInputReader(
            db, reader, jobService, workerTelemetry, TimeProvider.System);
        var derivativeExecutor = new CentralTransientDerivativeExecutor(
            new CentralTransientDerivativeBundleFactory(db, inputReader, new FixtureMaskFactory()),
            new CentralTransientDerivativeOutputWriter(
                db, minio, new CentralTransientEventVersionAppender(db), TimeProvider.System),
            jobService);
        var executor = new CentralTransientReprocessingExecutor(
            db,
            new CentralTransientEventVersionAppender(db),
            scheduler,
            jobService,
            reader,
            TimeProvider.System);
        var service = new CentralTransientReprocessingService(db, TimeProvider.System);
        var principal = AdminPrincipal();
        var latencies = new double[operations];
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(true);
        var rssBefore = process.WorkingSet64;
        var startedAll = Stopwatch.GetTimestamp();
        for (var index = 0; index < operations; index++)
        {
            db.ChangeTracker.Clear();
            var current = await db.CentralTransientEventCurrent.AsNoTracking().SingleAsync().ConfigureAwait(false);
            var options = fixture.AssessmentOptions with { FireballMinimumIntegratedSignalAdu = 1_001 + index };
            var started = Stopwatch.GetTimestamp();
            var scheduled = await service.ScheduleAsync(
                principal,
                current.CentralTransientEventId,
                current.RowVersion,
                $"performance-reprocessing-{index:D2}",
                new CentralTransientReprocessingRequest(options),
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CentralTransientReprocessingStatus.Scheduled, scheduled.Status);
            db.ChangeTracker.Clear();
            var lease = await jobService.ClaimNextAsync(
                "issue-118-performance", TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            Assert.AreEqual(CentralTransientReprocessingRuntime.RecipeName, lease.RecipeName);
            var result = await executor.ExecuteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, result.Status, result.ReasonCode);
            db.ChangeTracker.Clear();
            var derivativeLease = await jobService.ClaimNextAsync(
                "issue-118-derivative-performance", TimeSpan.FromMinutes(5), CancellationToken.None)
                .ConfigureAwait(false);
            Assert.IsNotNull(derivativeLease);
            Assert.AreEqual(CentralTransientDerivativeRuntime.RecipeName, derivativeLease.RecipeName);
            var derivativeResult = await derivativeExecutor.ExecuteAsync(derivativeLease, CancellationToken.None)
                .ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, derivativeResult.Status, derivativeResult.ReasonCode);
            latencies[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var elapsed = Stopwatch.GetElapsedTime(startedAll);
        process.Refresh();
        Array.Sort(latencies);
        var activeStates = new[]
        {
            CentralDerivativeJobStatus.Waiting,
            CentralDerivativeJobStatus.Pending,
            CentralDerivativeJobStatus.Leased,
            CentralDerivativeJobStatus.RetryableFailure,
            CentralDerivativeJobStatus.CancelRequested
        };
        var activeBacklog = await db.CentralDerivativeJobs.CountAsync(job => activeStates.Contains(job.Status))
            .ConfigureAwait(false);
        var expectedBytes = seeded.Artifacts.Sum(item => item.ByteLength) * operations * 2;
        var derivativeCount = await db.CentralTransientDerivatives.CountAsync().ConfigureAwait(false);
        var publishedOutputBytes = await db.CentralTransientDerivativeOutputIntents
            .Where(item => item.CommittedAtUtc != null).SumAsync(item => item.ByteLength).ConfigureAwait(false);
        return new(
            operations,
            operations,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.95),
            latencies[^1],
            operations / elapsed.TotalSeconds,
            (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
            GC.GetTotalAllocatedBytes(true) - allocationsBefore,
            rssBefore,
            process.WorkingSet64,
            reader.VerificationCount,
            expectedBytes,
            reader.VerifiedBytes,
            await db.CentralTransientEventVersions.CountAsync().ConfigureAwait(false),
            await db.CentralTransientAssessments.CountAsync().ConfigureAwait(false),
            derivativeCount,
            publishedOutputBytes,
            activeBacklog,
            "Each reprocessing operation includes its downstream production five-output SQL and MinIO publication before the next operation starts; W1/W2 full-frame algorithm cost is measured separately by the component harness.");
    }

    private static async Task<HistoryResult> MeasureHistoryAsync(
        ApplicationDbContext db,
        DbContextOptions<ApplicationDbContext> options,
        string connectionString,
        CountingCommandInterceptor counter)
    {
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
        var templateAssessmentId = await db.CentralTransientAssessments.Select(item => item.AssessmentId)
            .FirstAsync().ConfigureAwait(false);
        var templateVersionId = await db.CentralTransientEventVersions.Select(item => item.EventVersionId)
            .FirstAsync().ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            DECLARE @i int = 0;
            WHILE @i < 10000
            BEGIN
                DECLARE @eventId uniqueidentifier = NEWID();
                DECLARE @publicEventId uniqueidentifier = NEWID();
                DECLARE @assessmentId uniqueidentifier = NEWID();
                DECLARE @v1 uniqueidentifier = NEWID();
                DECLARE @created datetimeoffset = DATEADD(millisecond, @i, CAST('2026-07-01T00:00:00+00:00' AS datetimeoffset));

                INSERT INTO [CentralTransientEvents] ([Id], [AgentId], [EventId], [EventCreatedUtc])
                VALUES (@eventId, N'issue-118-w3m', @publicEventId, @created);

                INSERT INTO [CentralTransientAssessments]
                    ([AssessmentId], [CentralTransientEventId], [CreatedUtc], [Authority], [Classification],
                     [MeteorSeverity], [ConfidenceMillionths], [SupersedesAssessmentId], [SupersedesAssessmentCreatedUtc],
                     [ProducerSchemaVersion], [ProducerKind], [ProducerName], [ProducerVersion], [RecipeIdentitySha256],
                     [ReceiptSchemaVersion], [ExecutionIdentitySha256], [OptionsIdentitySha256], [CanonicalReceiptJson],
                     [CanonicalReceiptSha256], [CanonicalReceiptByteLength])
                SELECT @assessmentId, @eventId, @created, [Authority], [Classification], [MeteorSeverity],
                     [ConfidenceMillionths], NULL, NULL, [ProducerSchemaVersion], [ProducerKind], [ProducerName],
                     [ProducerVersion], [RecipeIdentitySha256], [ReceiptSchemaVersion],
                     CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(varbinary(16), @assessmentId)), 2),
                     [OptionsIdentitySha256], [CanonicalReceiptJson], [CanonicalReceiptSha256], [CanonicalReceiptByteLength]
                FROM [CentralTransientAssessments] WHERE [AssessmentId] = {{templateAssessmentId}};

                INSERT INTO [CentralTransientEventVersions]
                    ([EventVersionId], [CentralTransientEventId], [Version], [PreviousVersionNumber],
                     [PreviousEventVersionId], [PreviousVersionCreatedUtc], [State], [VersionCreatedUtc],
                     [FirstObservedUtc], [LastObservedUtc], [SchemaVersion], [CanonicalEventJson],
                     [CanonicalEventSha256], [CanonicalEventByteLength])
                SELECT @v1, @eventId, 1, NULL, NULL, NULL, [State], DATEADD(second, 1, @created),
                     @created, DATEADD(millisecond, 1, @created), [SchemaVersion], [CanonicalEventJson],
                     [CanonicalEventSha256], [CanonicalEventByteLength]
                FROM [CentralTransientEventVersions] WHERE [EventVersionId] = {{templateVersionId}};
                INSERT INTO [CentralTransientEventCurrent]
                    ([CentralTransientEventId], [LatestEventVersionId], [LatestVersion], [ActiveAssessmentId],
                     [LatestReviewId], [ReviewState], [EffectiveClassification], [EffectiveMeteorSeverity],
                     [EffectiveConfidenceMillionths], [UpdatedUtc])
                SELECT @eventId, @v1, 1, @assessmentId, NULL, N'NeedsReview', [Classification], [MeteorSeverity],
                     [ConfidenceMillionths], DATEADD(second, 1, @created)
                FROM [CentralTransientAssessments] WHERE [AssessmentId] = @assessmentId;
                SET @i += 1;
            END;
            """).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(
            "UPDATE STATISTICS [CentralTransientEvents] WITH FULLSCAN; UPDATE STATISTICS [CentralTransientEventCurrent] WITH FULLSCAN;")
            .ConfigureAwait(false);

        var plan = await ReadPlanAsync(connectionString).ConfigureAwait(false);
        var logicalReads = await ReadLogicalReadsAsync(connectionString).ConfigureAwait(false);
        var principal = AdminPrincipal();
        var readService = new CentralTransientEventReadService(db);
        counter.Reset();
        var pageLatencies = new List<double>();
        var eventsRead = 0;
        string? cursor = null;
        do
        {
            var started = Stopwatch.GetTimestamp();
            var page = await readService.ListAsync(principal, 100, cursor, CancellationToken.None).ConfigureAwait(false);
            pageLatencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            eventsRead += page.Items.Count;
            cursor = page.NextCursor;
        } while (cursor is not null);
        var paginationCommands = counter.Count;
        var historyRows = await db.CentralTransientEventVersions.CountAsync(version =>
            version.Event!.AgentId == "issue-118-w3m").ConfigureAwait(false);
        var contention = new List<ContentionResult>();
        foreach (var concurrency in new[] { 1, 4, 8 })
        {
            contention.Add(await MeasureContentionAsync(options, principal, concurrency).ConfigureAwait(false));
        }
        pageLatencies.Sort();
        return new(
            historyRows,
            eventsRead,
            100,
            pageLatencies.Count,
            paginationCommands,
            logicalReads,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plan))),
            plan.Contains("IX_CentralTransientEvents_EventCreatedUtc_Id", StringComparison.Ordinal),
            Percentile(pageLatencies.ToArray(), 0.5),
            pageLatencies.Max(),
            contention);
    }

    private static async Task<ContentionResult> MeasureContentionAsync(
        DbContextOptions<ApplicationDbContext> options,
        ClaimsPrincipal principal,
        int concurrency)
    {
        const int warmups = 20;
        const int measurements = 200;
        for (var index = 0; index < warmups; index++)
        {
            await ExecuteReadAsync(options, principal).ConfigureAwait(false);
        }
        var latencies = new ConcurrentBag<double>();
        var startedAll = Stopwatch.GetTimestamp();
        for (var offset = 0; offset < measurements; offset += concurrency)
        {
            var batch = Math.Min(concurrency, measurements - offset);
            await Task.WhenAll(Enumerable.Range(0, batch).Select(async _ =>
            {
                var started = Stopwatch.GetTimestamp();
                await ExecuteReadAsync(options, principal).ConfigureAwait(false);
                latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            })).ConfigureAwait(false);
        }
        var elapsed = Stopwatch.GetElapsedTime(startedAll);
        var ordered = latencies.Order().ToArray();
        return new(concurrency, warmups, measurements, Percentile(ordered, 0.5),
            Percentile(ordered, 0.95), ordered[^1], measurements / elapsed.TotalSeconds);
    }

    private static async Task ExecuteReadAsync(
        DbContextOptions<ApplicationDbContext> options,
        ClaimsPrincipal principal)
    {
        await using var context = new ApplicationDbContext(options);
        var page = await new CentralTransientEventReadService(context)
            .ListAsync(principal, 50, null, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(50, page.Items.Count);
    }

    private static async Task<string> ReadPlanAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync().ConfigureAwait(false);
        await using (var enable = connection.CreateCommand())
        {
            enable.CommandText = "SET SHOWPLAN_XML ON";
            await enable.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        string plan;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = ReviewQuery;
            await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
            plan = await reader.ReadAsync().ConfigureAwait(false) ? reader.GetString(0) : string.Empty;
        }
        await using (var disable = connection.CreateCommand())
        {
            disable.CommandText = "SET SHOWPLAN_XML OFF";
            await disable.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        return plan;
    }

    private static async Task<long> ReadLogicalReadsAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        var messages = new StringBuilder();
        connection.InfoMessage += (_, args) => messages.AppendLine(args.Message);
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SET STATISTICS IO ON; {ReviewQuery}; SET STATISTICS IO OFF;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return Regex.Matches(messages.ToString(), @"logical reads (?<count>\d+)", RegexOptions.IgnoreCase)
            .Sum(match => long.Parse(match.Groups["count"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    private const string ReviewQuery = """
        SELECT TOP(101) [current].[CentralTransientEventId], [event].[EventId], [event].[AgentId],
            [event].[EventCreatedUtc], [version].[FirstObservedUtc], [version].[LastObservedUtc],
            [current].[LatestVersion], [version].[State], [current].[ReviewState],
            [current].[ActiveAssessmentId], [current].[EffectiveClassification],
            [current].[EffectiveMeteorSeverity], [current].[EffectiveConfidenceMillionths], [current].[RowVersion]
        FROM [CentralTransientEventCurrent] AS [current]
        INNER JOIN [CentralTransientEvents] AS [event] ON [current].[CentralTransientEventId] = [event].[Id]
        INNER JOIN [CentralTransientEventVersions] AS [version]
            ON [current].[CentralTransientEventId] = [version].[CentralTransientEventId]
           AND [current].[LatestEventVersionId] = [version].[EventVersionId]
        ORDER BY [event].[EventCreatedUtc] DESC, [current].[CentralTransientEventId] DESC
        """;

    private static ClaimsPrincipal AdminPrincipal()
        => new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "issue-118-performance-admin"),
            new Claim("sub", "issue-118-performance-admin"),
            new Claim("account_type", "User"),
            new Claim("scope", "api.viewer api.admin")
        ], "Performance"));

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HVO.SkyMonitor.v9.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found.");
    }

    private static string Git(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git could not be started.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output.Trim() : "unknown";
    }

    private static string DirtyFingerprint(string repositoryRoot)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(string.Join('\n',
            Git(repositoryRoot, "status", "--porcelain"), Git(repositoryRoot, "diff", "--binary"))));
        foreach (var relativePath in Git(repositoryRoot, "ls-files", "--others", "--exclude-standard")
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData(File.ReadAllBytes(Path.Combine(repositoryRoot, relativePath)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private sealed class CountingCommandInterceptor : DbCommandInterceptor
    {
        private int count;
        public int Count => Volatile.Read(ref count);
        public void Reset() => Interlocked.Exchange(ref count, 0);
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref count);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref count);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class CountingArtifactObjectReader(ICentralArtifactObjectReader inner) : ICentralArtifactObjectReader
    {
        public int VerificationCount { get; private set; }
        public long VerifiedBytes { get; private set; }
        public async Task<CentralArtifactObjectSnapshot> VerifyAsync(
            CentralArtifact artifact, CancellationToken cancellationToken)
        {
            var result = await inner.VerifyAsync(artifact, cancellationToken).ConfigureAwait(false);
            VerificationCount++;
            VerifiedBytes += result.ByteLength;
            return result;
        }
        public Task<bool> IsCurrentGenerationAsync(
            CentralArtifact artifact, string storageETag, CancellationToken cancellationToken)
            => inner.IsCurrentGenerationAsync(artifact, storageETag, cancellationToken);
        public Task CopyToAsync(
            CentralArtifactObjectSnapshot snapshot, Stream destination, CentralArtifactByteRange? range,
            CancellationToken cancellationToken)
            => inner.CopyToAsync(snapshot, destination, range, cancellationToken);
    }

    private sealed class FixtureMaskFactory : ICentralTransientMaskFactory
    {
        public Task<TransientDetectorMask[]?> CreateAsync(
            IReadOnlyList<CentralTransientMaskSource> sources,
            CentralTransientExecutionOptionsV1 options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layout = sources[0].Input.Descriptor.Layout;
            var empty = Linear16MaskOperations.Empty(layout.Width, layout.Height);
            var masks = new[]
            {
                TransientDetectorMaskKind.Sky,
                TransientDetectorMaskKind.ImageCircle,
                TransientDetectorMaskKind.Horizon,
                TransientDetectorMaskKind.Obstruction,
                TransientDetectorMaskKind.BadPixel,
                TransientDetectorMaskKind.Star
            }.Select(kind => TransientDetectorMask.Create(
                kind,
                new ProcessingAlgorithmIdentity($"fixture-{kind.ToString().ToUpperInvariant()}-mask", "v1"),
                empty)).ToArray();
            return Task.FromResult<TransientDetectorMask[]?>(masks);
        }
    }

    private sealed record ReprocessingResult(
        int ScheduledJobs,
        int CompletedJobs,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double JobsPerSecond,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        int MinioVerificationRequests,
        long ExpectedVerifiedBytes,
        long VerifiedBytes,
        int EventVersionCount,
        int AssessmentCount,
        int DerivativeCount,
        long PublishedOutputBytes,
        int ActiveBacklog,
        string DerivativeBoundary);

    private sealed record HistoryResult(
        int HistoryRows,
        int EventsRead,
        int PageSize,
        int PageCount,
        int SqlCommands,
        long LogicalReads,
        string QueryPlanSha256,
        bool QueryPlanUsesEventCreatedIndex,
        double MedianPageMilliseconds,
        double MaximumPageMilliseconds,
        IReadOnlyList<ContentionResult> Contention);

    private sealed record ContentionResult(
        int Concurrency,
        int Warmups,
        int Measurements,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double OperationsPerSecond);
}
