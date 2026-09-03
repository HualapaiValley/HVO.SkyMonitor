using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

public sealed partial class LogicHostIngestPerformanceTests
{
    private const string Issue249BaselineRevision = "d188950b617f86476c69c1b1a21c7f67e80c7b6a";
    private static readonly string[] Issue249BaselineChangedPaths =
    [
        "scripts/test-categories/Program.cs",
        "tests/HVO.SkyMonitor.LogicHost.IntegrationTests/LogicHostIngestIssue249PerformanceTests.cs"
    ];
    private static readonly JsonSerializerOptions Issue249JsonOptions = new() { WriteIndented = true };

    [TestMethod]
    public async Task DuplicateVerification_W1W2ConcurrencyAndDelay_RecordsBaselineEvidence()
    {
        var phase = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PHASE") ?? "development";
        Assert.IsTrue(phase is "development" or "baseline" or "after");
        if (phase != "development")
        {
            Assert.IsTrue(GCSettings.IsServerGC, "claimable evidence requires DOTNET_gcServer=1");
        }
        var fixture = AssemblyHooks.Fixture;
        using var retryCollector = new Issue249RetryCollector();
        var w1 = CreateWorkload("W1", 1936, 1216, CameraPixelFormat.Mono16);
        var w2 = CreateWorkload("W2", 3096, 2080, CameraPixelFormat.BayerRggb16);
        await SeedWorkloadAsync(fixture, w1).ConfigureAwait(false);
        await SeedWorkloadAsync(fixture, w2).ConfigureAwait(false);

        using var client = fixture.Factory.CreateClient();
        var token = await HttpHelpers.GetClientCredentialsTokenAsync(
            client,
            "/connect/token",
            TestClients.SystemCameraAgent.ClientId,
            TestClients.SystemCameraAgent.ClientSecret,
            string.Join(' ', TestClients.SystemCameraAgent.Scopes)).ConfigureAwait(false);
        SetAuthorization(client, token.AccessToken);

        var allocator = new UploadAllocator(w1, w2);
        var scenarios = new List<Issue249DuplicateScenario>();
        foreach (var concurrency in ConcurrencyLevels)
        {
            scenarios.Add(new(w1, concurrency, allocator.Create(w1, StandardWarmups + StandardMeasurements)));
            scenarios.Add(new(w2, concurrency, allocator.Create(w2, StandardWarmups + StandardMeasurements)));
        }
        var delayScenarios = new[]
        {
            new Issue249DelayScenario(TimeSpan.Zero, allocator.Create(w2, 8)),
            new Issue249DelayScenario(TimeSpan.FromMilliseconds(250), allocator.Create(w2, 8)),
            new Issue249DelayScenario(TimeSpan.FromSeconds(2), allocator.Create(w2, 8))
        };
        var mechanismUpload = allocator.Create(w2, 1).Single();
        var allUploads = scenarios.SelectMany(static scenario => scenario.Uploads)
            .Concat(delayScenarios.SelectMany(static scenario => scenario.Uploads))
            .Append(mechanismUpload)
            .ToArray();
        await ExecuteAcceptedAsync(client, allUploads, 4, null, null).ConfigureAwait(false);
        var initialState = await ReadIssue249InitialStateAsync(fixture, allUploads).ConfigureAwait(false);

        using var protocolCounter = new ProtocolCounter(fixture.MinioEndpoint);
        var duplicateMeasurements = new List<IngestMeasurement>();
        foreach (var scenario in scenarios)
        {
            duplicateMeasurements.Add(await MeasureAcceptedAsync(
                fixture,
                client,
                protocolCounter,
                $"duplicate-{scenario.Workload.Id}-C{scenario.Concurrency}",
                scenario.Uploads,
                StandardWarmups,
                scenario.Concurrency).ConfigureAwait(false));
        }

        var delayMeasurements = new List<Issue249DelayEvidence>();
        foreach (var scenario in delayScenarios)
        {
            delayMeasurements.Add(await MeasureIssue249NaturalDelayAsync(
                fixture,
                token.AccessToken,
                protocolCounter,
                scenario.Uploads,
                scenario.Delay,
                phase).ConfigureAwait(false));
        }
        var contention = await MeasureIssue249WriterContentionAsync(
            fixture,
            token.AccessToken,
            mechanismUpload,
            phase).ConfigureAwait(false);

        await AssertIssue249FinalStateAsync(fixture, allUploads, initialState).ConfigureAwait(false);
        if (phase == "after")
        {
            Assert.AreEqual(0L, retryCollector.SqlDeadlockRetries,
                "candidate duplicate verification must not conceal SQL deadlocks behind internal retries");
        }
        var repositoryRoot = GetRepositoryRoot();
        var source = await EvidenceSourceIdentity.CaptureAsync(
            repositoryRoot,
            typeof(LogicHostIngestPerformanceTests),
            typeof(ApplicationDbContext),
            typeof(ArtifactManifestV2)).ConfigureAwait(false);
        var productionRevision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_PRODUCTION_REVISION")
            ?? source.Head;
        await ValidateIssue249EvidenceSourceAsync(
            repositoryRoot, source, phase, productionRevision).ConfigureAwait(false);
        var output = Path.Combine(
            repositoryRoot,
            "TestResults",
            "issue-249",
            source.OutputDirectoryName,
            source.RunId);
        Directory.CreateDirectory(output);
        var evidencePath = Path.Combine(output, "central-duplicate-verification-evidence.json");
        var evidence = new
        {
            Schema = "hvo-issue-249-central-duplicate-verification-evidence-v2",
            Issue = 249,
            Phase = phase,
            ProductionRevision = productionRevision,
            Source = source,
            Environment = new
            {
                Os = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Runtime = RuntimeInformation.FrameworkDescription,
                Sdk = ReadPinnedSdkVersion(repositoryRoot),
                Configuration = "Release",
                ServerGc = GCSettings.IsServerGC,
                CpuCount = Environment.ProcessorCount,
                CpuModel = ReadCpuModel(),
                TotalMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                SqlServer = "SQL Server 2022 CU26 Ubuntu 22.04 Testcontainer",
                ObjectStore = "MinIO RELEASE.2025-09-07T16-13-09Z Testcontainer",
                Host = "ASP.NET Core TestServer"
            },
            Command = "DOTNET_gcServer=1 HVO_EVIDENCE_PHASE=<baseline|after> HVO_EVIDENCE_REVISION=<HARNESS_OR_CANDIDATE_HEAD> HVO_EVIDENCE_PRODUCTION_REVISION=<PRODUCTION_HEAD> HVO_EVIDENCE_TRIAL=<1-5> dotnet test tests/HVO.SkyMonitor.LogicHost.IntegrationTests/HVO.SkyMonitor.LogicHost.IntegrationTests.csproj --no-build --configuration Release --filter FullyQualifiedName~LogicHostIngestPerformanceTests.DuplicateVerification_W1W2ConcurrencyAndDelay_RecordsBaselineEvidence",
            Workload = new
            {
                Boundary = "Authenticated multipart duplicate request through checksum-verified durable acknowledgement.",
                ArrivalModel = "Closed-loop duplicate requests over distinct object sets for each workload and concurrency.",
                W1 = WorkloadMetadata(w1, StandardWarmups, StandardMeasurements, ConcurrencyLevels),
                W2 = WorkloadMetadata(w2, StandardWarmups, StandardMeasurements, ConcurrencyLevels),
                Delay = new
                {
                    Payload = "W2",
                    ObjectsPerDelay = 8,
                    Concurrency = 4,
                    DelaysMilliseconds = new[] { 0, 250, 2000 }
                },
                MechanismProbe = "One W2 status request with a 2,000 ms GET pause and one exact-row competing writer; excluded from natural latency/resource results."
            },
            Method = new
            {
                Latency = "Nearest-rank median/p95/p99/maximum over 30 measured duplicates after five warmups. Eight-object delay runs report median and maximum only.",
                Resources = "Process CPU, sampled allocation rate, and 10 ms process RSS peak; inherited sampler setup overhead is reported consistently in baseline and after runs.",
                Sql = "EF diagnostic counts are process-wide during each isolated natural scenario. A separate untimed repeat uses timestamped 10 ms DMV samples attributed by Application Name and restricted to the synthetic GET-pause window.",
                ObjectStore = "System.Net.Http diagnostics count MinIO operations and available Content-Length bytes.",
                Writer = "The separately reported writer probe records completion, timeout, or SQL deadlock without contributing to natural latency/resource evidence."
            },
            DuplicateMeasurements = duplicateMeasurements,
            DelayMeasurements = delayMeasurements,
            WriterContention = contention,
            FaultEvidence = "Not claimed by this performance command; focused source-attributed stream, truncation, checksum, quarantine, retry, crash, and stale-generation acceptance evidence is a separate candidate gate.",
            BaselineOnlyNotApplicable = phase == "baseline"
                ? new[]
                {
                    "No durable verification reservation exists on baseline, so reservation crash boundaries and token backlog are N/A until candidate acceptance testing.",
                    "Stale verification generation fencing is absent on baseline and is exercised by candidate acceptance tests rather than represented as a successful baseline behavior."
                }
                : [],
            Correctness = new
            {
                ArtifactCount = allUploads.Length,
                AllAcknowledgementsValidated = true,
                EveryFinalObjectHeadLengthAndStreamedSha256Validated = true,
                FinalObjectState = CentralArtifactObjectState.Available.ToString(),
                FinalReconstructionState = CentralReconstructionState.Complete.ToString(),
                ExactDerivativeJobIdsUnchanged = true,
                ExactStorageReferenceAndRecoveryGenerationUnchanged = true,
                SqlDeadlockRetries = retryCollector.SqlDeadlockRetries,
                BaselineVerificationToken = "N/A: no durable verification reservation fields exist at the pinned baseline revision.",
                W1Sha256 = w1.ChecksumSha256,
                W2Sha256 = w2.ChecksumSha256
            },
            Interpretation = "Durable state and checksums are pass/fail. Timing is specific to this container environment; no physical deployment claim is made.",
            RecordedAtUtc = DateTimeOffset.UtcNow
        };
        var evidenceJson = JsonSerializer.Serialize(evidence, Issue249JsonOptions);
        AssertIssue249EvidenceIsSafe(evidenceJson, fixture);
        await EvidenceSourceIdentity.WriteJsonAsync(evidencePath, evidence, Issue249JsonOptions).ConfigureAwait(false);
        var evidenceBytes = await File.ReadAllBytesAsync(evidencePath).ConfigureAwait(false);
        var manifest = new
        {
            Schema = "hvo-evidence-manifest-v1",
            Issue = 249,
            Phase = phase,
            SourceHead = source.Head,
            Files = new[]
            {
                new
                {
                    Name = Path.GetFileName(evidencePath),
                    ByteLength = evidenceBytes.LongLength,
                    Sha256 = Convert.ToHexString(SHA256.HashData(evidenceBytes))
                }
            }
        };
        await EvidenceSourceIdentity.WriteJsonAsync(
            Path.Combine(output, "manifest.json"), manifest, Issue249JsonOptions).ConfigureAwait(false);
    }

    private static async Task<Issue249DelayEvidence> MeasureIssue249NaturalDelayAsync(
        IntegrationTestFixture fixture,
        string accessToken,
        ProtocolCounter protocolCounter,
        IReadOnlyList<ExpectedUpload> uploads,
        TimeSpan delay,
        string phase)
    {
        var naturalApplicationName =
            $"HVO.SkyMonitor.Issue249.Delay.Natural.{delay.TotalMilliseconds:0}.{Guid.NewGuid():N}";
        var naturalConnectionString = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            ApplicationName = naturalApplicationName
        }.ConnectionString;
        using var naturalHandler = new Issue249DelayedGetHandler(delay, expectedFirstWave: 4)
        {
            InnerHandler = new SocketsHttpHandler()
        };
        using var naturalFactory = CreateIssue249IngestFactory(fixture, naturalConnectionString, naturalHandler);
        using var naturalClient = naturalFactory.CreateClient();
        SetAuthorization(naturalClient, accessToken);

        var latencies = new ConcurrentBag<double>();
        var attempts = new HttpAttemptCounter();
        using var operationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        Task? naturalDuplicates = null;
        StabilizeGc();
        using var resources = new ResourceSampler();
        protocolCounter.Start();
        ProtocolSnapshot? protocols = null;
        ResourceEvidence? resourceEvidence = null;
        var started = Stopwatch.GetTimestamp();
        try
        {
            naturalDuplicates = ExecuteIssue249AcceptedAsync(
                naturalClient, uploads, 4, latencies, attempts, operationCancellation.Token);
            await naturalDuplicates.ConfigureAwait(false);
            protocols = protocolCounter.Stop();
            resourceEvidence = await resources.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            if (naturalDuplicates is not null && !naturalDuplicates.IsCompleted)
            {
                await operationCancellation.CancelAsync().ConfigureAwait(false);
                await EvidenceTaskCleanup.DrainAsync(
                    [naturalDuplicates], operationCancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            protocols ??= protocolCounter.Stop();
            resourceEvidence ??= await resources.StopAsync().ConfigureAwait(false);
        }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var sorted = latencies.Order().ToArray();
        Assert.AreEqual(uploads.Count, sorted.Length);

        var sql = CreateIssue249SqlEvidence([]);
        if (delay > TimeSpan.Zero)
        {
            var correlationApplicationName =
                $"HVO.SkyMonitor.Issue249.Delay.Correlation.{delay.TotalMilliseconds:0}.{Guid.NewGuid():N}";
            var correlationConnectionString = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
            {
                ApplicationName = correlationApplicationName
            }.ConnectionString;
            using var correlationHandler = new Issue249DelayedGetHandler(
                delay, expectedFirstWave: 4, holdFirstWave: true)
            {
                InnerHandler = new SocketsHttpHandler()
            };
            using var correlationFactory = CreateIssue249IngestFactory(
                fixture, correlationConnectionString, correlationHandler);
            using var correlationClient = correlationFactory.CreateClient();
            SetAuthorization(correlationClient, accessToken);
            using var correlationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var samplingCancellation = new CancellationTokenSource();
            Task? correlationDuplicates = null;
            Task<IReadOnlyList<Issue249SqlSample>>? samplingTask = null;
            try
            {
                correlationDuplicates = ExecuteIssue249AcceptedAsync(
                    correlationClient, uploads, 4, null, null, correlationCancellation.Token);
                await correlationHandler.FirstWaveEntered.WaitAsync(
                    TimeSpan.FromSeconds(30), correlationCancellation.Token).ConfigureAwait(false);
                samplingTask = SampleIssue249SqlAsync(
                    fixture.SqlServerConnectionString,
                    correlationApplicationName,
                    samplingCancellation.Token);
                await correlationHandler.FirstWaveDelayCompleted.WaitAsync(
                    TimeSpan.FromSeconds(30), correlationCancellation.Token).ConfigureAwait(false);
                await samplingCancellation.CancelAsync().ConfigureAwait(false);
                sql = CreateIssue249SqlEvidence(await samplingTask.ConfigureAwait(false));
                correlationHandler.ReleaseFirstWave();
                await correlationDuplicates.ConfigureAwait(false);
            }
            finally
            {
                correlationHandler.ReleaseFirstWave();
                await samplingCancellation.CancelAsync().ConfigureAwait(false);
                if (samplingTask is not null && !samplingTask.IsCompleted)
                {
                    await samplingTask.ConfigureAwait(false);
                }
                if (correlationDuplicates is not null && !correlationDuplicates.IsCompleted)
                {
                    await correlationCancellation.CancelAsync().ConfigureAwait(false);
                    await EvidenceTaskCleanup.DrainAsync(
                        [correlationDuplicates], correlationCancellation, TimeSpan.FromSeconds(30))
                        .ConfigureAwait(false);
                }
            }
            var minimumCoverage = Math.Max(10, delay.TotalMilliseconds - 150);
            Assert.IsTrue(sql.ApplicationLockWindowMilliseconds >= minimumCoverage);
            if (phase == "baseline")
            {
                Assert.IsTrue(sql.TransactionOverlapMilliseconds >= minimumCoverage);
            }
            if (phase == "after")
            {
                Assert.AreEqual(0, sql.MaximumOpenTransactionSessions);
                Assert.AreEqual(0d, sql.TransactionOverlapMilliseconds);
            }
        }
        return new(
            delay.TotalMilliseconds,
            uploads.Count,
            4,
            elapsed.TotalMilliseconds,
            Percentile(sorted, 0.50),
            sorted[^1],
            sql,
            resourceEvidence,
            CreateProtocolEvidence(attempts, protocols, uploads.Sum(static upload => upload.Workload.Payload.LongLength)));
    }

    private static async Task<Issue249ContentionEvidence> MeasureIssue249WriterContentionAsync(
        IntegrationTestFixture fixture,
        string accessToken,
        ExpectedUpload upload,
        string phase)
    {
        var applicationName = $"HVO.SkyMonitor.Issue249.Writer.{Guid.NewGuid():N}";
        var connectionString = new SqlConnectionStringBuilder(fixture.SqlServerConnectionString)
        {
            ApplicationName = applicationName
        }.ConnectionString;
        using var handler = new Issue249DelayedGetHandler(
            TimeSpan.FromSeconds(2), expectedFirstWave: 1, holdFirstWave: true)
        {
            InnerHandler = new SocketsHttpHandler()
        };
        using var factory = CreateIssue249IngestFactory(fixture, connectionString, handler);
        using var client = factory.CreateClient();
        SetAuthorization(client, accessToken);
        var artifactId = await ReadIssue249ArtifactIdAsync(fixture, upload).ConfigureAwait(false);
        using var operationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var samplingCancellation = new CancellationTokenSource();
        Task<IReadOnlyList<Issue249SqlSample>>? samplingTask = null;
        Task<UploadResponse>? statusTask = null;
        Task<Issue249WriterEvidence>? writerTask = null;
        try
        {
            statusTask = SendStatusAsync(client, upload, operationCancellation.Token);
            await handler.FirstWaveEntered.WaitAsync(
                TimeSpan.FromSeconds(30), operationCancellation.Token).ConfigureAwait(false);
            samplingTask = SampleIssue249SqlAsync(
                fixture.SqlServerConnectionString,
                applicationName,
                samplingCancellation.Token);
            writerTask = ExecuteIssue249WriterAsync(
                fixture.SqlServerConnectionString,
                applicationName,
                artifactId,
                operationCancellation.Token);
            await handler.FirstWaveDelayCompleted.WaitAsync(
                TimeSpan.FromSeconds(30), operationCancellation.Token).ConfigureAwait(false);
            await samplingCancellation.CancelAsync().ConfigureAwait(false);
            var samples = await samplingTask.ConfigureAwait(false);
            handler.ReleaseFirstWave();
            var writer = await writerTask.ConfigureAwait(false);
            var status = await statusTask.ConfigureAwait(false);
            Assert.AreEqual(HttpStatusCode.Accepted, status.StatusCode, status.Body);
            AssertAcknowledgement(upload, status.Body);
            var sql = CreateIssue249SqlEvidence(samples);
            Assert.IsTrue(sql.ApplicationLockWindowMilliseconds >= 1850);
            if (phase == "baseline")
            {
                Assert.IsTrue(sql.TransactionOverlapMilliseconds >= 1850);
                Assert.IsTrue(sql.MaximumBlockedWriterRequests > 0 || writer.Outcome == "deadlock");
            }
            if (phase == "after")
            {
                Assert.AreEqual(0, sql.MaximumOpenTransactionSessions);
                Assert.AreEqual("completed", writer.Outcome);
                Assert.IsTrue(writer.ElapsedMilliseconds < 1000);
            }
            return new(2000, writer, sql, status.ElapsedMilliseconds);
        }
        finally
        {
            handler.ReleaseFirstWave();
            await samplingCancellation.CancelAsync().ConfigureAwait(false);
            if (samplingTask is not null && !samplingTask.IsCompleted)
            {
                await samplingTask.ConfigureAwait(false);
            }
            var unfinished = new Task?[] { statusTask, writerTask }
                .Where(static task => task is { IsCompleted: false })
                .Cast<Task>()
                .ToArray();
            if (unfinished.Length > 0)
            {
                await operationCancellation.CancelAsync().ConfigureAwait(false);
                await EvidenceTaskCleanup.DrainAsync(
                    unfinished, operationCancellation, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
        }
    }

    private static async Task ExecuteIssue249AcceptedAsync(
        HttpClient client,
        IReadOnlyList<ExpectedUpload> uploads,
        int concurrency,
        ConcurrentBag<double>? latencies,
        HttpAttemptCounter? attempts,
        CancellationToken cancellationToken)
    {
        await Parallel.ForEachAsync(
            uploads,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = concurrency,
                CancellationToken = cancellationToken
            },
            async (upload, operationCancellation) =>
            {
                var started = Stopwatch.GetTimestamp();
                var response = await SendUploadAsync(
                    client, upload, upload.Workload.Payload, operationCancellation).ConfigureAwait(false);
                attempts?.RecordMultipart(response.RequestBodyBytes, upload.Workload.Payload.LongLength);
                Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, response.Body);
                AssertAcknowledgement(upload, response.Body);
                latencies?.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }).ConfigureAwait(false);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateIssue249IngestFactory(
        IntegrationTestFixture fixture,
        string connectionString,
        Issue249DelayedGetHandler handler)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ => new MinioClient()
                .WithEndpoint(fixture.MinioEndpoint)
                .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                .WithHttpClient(new HttpClient(handler, disposeHandler: false), disposeHttpClient: true)
                .Build());
            ObjectStoreTestClient.Replace(services);
        }));

    private static async Task<Guid> ReadIssue249ArtifactIdAsync(
        IntegrationTestFixture fixture,
        ExpectedUpload upload)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CentralArtifacts
            .Where(artifact => artifact.ArtifactId == upload.Manifest.Descriptor.Artifact.ArtifactId)
            .Select(artifact => artifact.Id)
            .SingleAsync().ConfigureAwait(false);
    }

    private static async Task<Issue249WriterEvidence> ExecuteIssue249WriterAsync(
        string connectionString,
        string applicationName,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationName = applicationName + ".Writer"
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = "UPDATE [CentralArtifacts] SET [StateReasonCode] = [StateReasonCode] WHERE [Id] = @id;";
        command.Parameters.AddWithValue("@id", artifactId);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new("completed", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (SqlException exception) when (exception.Number == 1205)
        {
            return new("deadlock", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (SqlException exception) when (exception.Number == -2)
        {
            return new("timeout", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static async Task<IReadOnlyList<Issue249SqlSample>> SampleIssue249SqlAsync(
        string connectionString,
        string applicationName,
        CancellationToken cancellationToken)
    {
        var samples = new List<Issue249SqlSample>();
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (!cancellationToken.IsCancellationRequested)
            {
                samples.Add(await ReadIssue249SqlSampleAsync(
                    connection,
                    applicationName,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    CancellationToken.None).ConfigureAwait(false));
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return samples;
    }

    private static async Task<Issue249SqlSample> ReadIssue249SqlSampleAsync(
        SqlConnection connection,
        string applicationName,
        double elapsedMilliseconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DECLARE @attributed TABLE ([session_id] smallint PRIMARY KEY);
            INSERT INTO @attributed ([session_id])
            SELECT [session_id]
            FROM [sys].[dm_exec_sessions]
            WHERE [program_name] = @application_name;

            WITH [transaction_log] AS
            (
                SELECT COALESCE(SUM([database_transaction_log_bytes_used]), 0) AS [bytes]
                FROM [sys].[dm_tran_database_transactions] AS [database_transaction]
                INNER JOIN [sys].[dm_tran_session_transactions] AS [session_transaction]
                    ON [session_transaction].[transaction_id] = [database_transaction].[transaction_id]
                WHERE [session_transaction].[session_id] IN (SELECT [session_id] FROM @attributed)
                    AND [database_transaction].[database_id] = DB_ID()
            )
            SELECT
                (SELECT COUNT(*) FROM @attributed),
                (SELECT COUNT(*) FROM [sys].[dm_exec_requests]
                    WHERE [session_id] IN (SELECT [session_id] FROM @attributed)),
                (SELECT COUNT(DISTINCT [session_id]) FROM [sys].[dm_tran_session_transactions]
                    WHERE [session_id] IN (SELECT [session_id] FROM @attributed)),
                (SELECT COUNT(DISTINCT [request_session_id]) FROM [sys].[dm_tran_locks]
                    WHERE [request_session_id] IN (SELECT [session_id] FROM @attributed)
                        AND [resource_type] = N'APPLICATION'
                        AND [request_owner_type] = N'SESSION'
                        AND [request_status] = N'GRANT'),
                (SELECT COUNT(*) FROM [sys].[dm_exec_requests] AS writer_request
                    INNER JOIN [sys].[dm_exec_sessions] AS writer_session
                        ON writer_session.[session_id] = writer_request.[session_id]
                    INNER JOIN [sys].[dm_exec_sessions] AS blocker_session
                        ON blocker_session.[session_id] = writer_request.[blocking_session_id]
                    WHERE writer_session.[program_name] = @writer_application_name
                        AND blocker_session.[program_name] = @application_name),
                (SELECT [bytes] FROM [transaction_log]);
            """;
        command.Parameters.AddWithValue("@application_name", applicationName);
        command.Parameters.AddWithValue("@writer_application_name", applicationName + ".Writer");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
        return new(
            elapsedMilliseconds,
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt64(5));
    }

    private static Issue249SqlEvidence CreateIssue249SqlEvidence(IReadOnlyList<Issue249SqlSample> samples)
        => new(
            samples.Count,
            samples.Count == 0 ? 0 : samples.Max(static sample => sample.Sessions),
            samples.Count == 0 ? 0 : samples.Max(static sample => sample.ActiveRequests),
            samples.Count == 0 ? 0 : samples.Max(static sample => sample.OpenTransactionSessions),
            samples.Count == 0 ? 0 : samples.Max(static sample => sample.SessionApplicationLocks),
            samples.Count == 0 ? 0 : samples.Max(static sample => sample.BlockedWriterRequests),
            samples.Count == 0 ? 0 : samples.Max(static sample => sample.ActiveTransactionLogBytes),
            CalculateIssue249ObservedWindow(samples, static sample => sample.OpenTransactionSessions > 0),
            CalculateIssue249ObservedWindow(samples, static sample => sample.SessionApplicationLocks > 0),
            samples);

    private static double CalculateIssue249ObservedWindow(
        IReadOnlyList<Issue249SqlSample> samples,
        Func<Issue249SqlSample, bool> predicate)
    {
        var intervals = samples.Zip(samples.Skip(1),
                static (previous, current) => current.ElapsedMilliseconds - previous.ElapsedMilliseconds)
            .Order()
            .ToArray();
        var interval = intervals.Length == 0 ? 10 : Percentile(intervals, 0.50);
        var longest = 0d;
        Issue249SqlSample? first = null;
        Issue249SqlSample? last = null;
        foreach (var sample in samples)
        {
            if (!predicate(sample))
            {
                first = null;
                last = null;
                continue;
            }
            first ??= sample;
            last = sample;
            longest = Math.Max(longest, last.ElapsedMilliseconds - first.ElapsedMilliseconds + interval);
        }
        return longest;
    }

    private static async Task<Dictionary<Guid, Issue249InitialArtifactState>> ReadIssue249InitialStateAsync(
        IntegrationTestFixture fixture,
        IReadOnlyList<ExpectedUpload> uploads)
    {
        var artifactIds = uploads.Select(static upload => upload.Manifest.Descriptor.Artifact.ArtifactId).ToArray();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifacts = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifactIds.Contains(artifact.ArtifactId))
            .Select(artifact => new
            {
                artifact.ArtifactId,
                artifact.StorageReference,
                artifact.RecoveryGeneration
            })
            .ToListAsync().ConfigureAwait(false);
        var jobs = await db.CentralDerivativeJobs.AsNoTracking()
            .Where(job => artifactIds.Contains(job.SourceArtifact!.ArtifactId))
            .Select(job => new { job.SourceArtifact!.ArtifactId, job.Id })
            .ToListAsync().ConfigureAwait(false);
        var jobIds = jobs.GroupBy(static job => job.ArtifactId)
            .ToDictionary(static group => group.Key, static group => group.Select(static job => job.Id).Order().ToArray());
        var result = artifacts.ToDictionary(
            static artifact => artifact.ArtifactId,
            artifact => new Issue249InitialArtifactState(
                artifact.StorageReference,
                artifact.RecoveryGeneration,
                jobIds[artifact.ArtifactId]));
        Assert.AreEqual(uploads.Count, result.Count);
        Assert.IsTrue(result.Values.All(static state => state.JobIds.Length > 0));
        return result;
    }

    private static async Task AssertIssue249FinalStateAsync(
        IntegrationTestFixture fixture,
        IReadOnlyList<ExpectedUpload> uploads,
        Dictionary<Guid, Issue249InitialArtifactState> initialState)
    {
        var artifactIds = uploads.Select(static upload => upload.Manifest.Descriptor.Artifact.ArtifactId).ToArray();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var artifacts = await db.CentralArtifacts.AsNoTracking()
            .Include(static artifact => artifact.Frame)
            .Include(static artifact => artifact.IngestIdentities)
            .Where(artifact => artifactIds.Contains(artifact.ArtifactId))
            .ToListAsync().ConfigureAwait(false);
        Assert.AreEqual(uploads.Count, artifacts.Count);
        var minio = scope.ServiceProvider.GetRequiredService<IMinioClient>();
        foreach (var artifact in artifacts)
        {
            var expected = uploads.Single(upload => upload.Manifest.Descriptor.Artifact.ArtifactId == artifact.ArtifactId);
            Assert.AreEqual(CentralArtifactObjectState.Available, artifact.ObjectState);
            Assert.AreEqual(CentralReconstructionState.Complete, artifact.ReconstructionState);
            Assert.IsNull(artifact.StateReasonCode);
            Assert.AreEqual(expected.Manifest.Descriptor.Capture.CaptureId, artifact.Frame!.FrameId);
            Assert.AreEqual(expected.Workload.DevicePublicId, artifact.DevicePublicId);
            Assert.AreEqual(expected.Manifest.IdempotencyKey, artifact.IdempotencyKey, ignoreCase: true);
            Assert.AreEqual(expected.Workload.Payload.LongLength, artifact.ByteLength);
            Assert.AreEqual(expected.Workload.ChecksumSha256, artifact.ChecksumSha256, ignoreCase: true);
            Assert.AreEqual(1, artifact.IngestIdentities.Count);
            Assert.AreEqual(initialState[artifact.ArtifactId].StorageReference, artifact.StorageReference);
            Assert.AreEqual(initialState[artifact.ArtifactId].RecoveryGeneration, artifact.RecoveryGeneration);
            Assert.IsNull(artifact.ObjectVerificationToken);
            Assert.IsNull(artifact.ObjectVerificationRequestedAtUtc);
            Assert.AreEqual(0, artifact.ObjectVerificationRetryCount);
            Assert.IsNull(artifact.ObjectVerificationRetryAtUtc);
            var objectKey = artifact.StorageReference[$"s3://{ArtifactBucket}/".Length..];
            var stat = await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(ArtifactBucket)
                .WithObject(objectKey)).ConfigureAwait(false);
            Assert.AreEqual(expected.Workload.Payload.LongLength, stat.Size);
            string? checksum = null;
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(ArtifactBucket)
                .WithObject(objectKey)
                .WithCallbackStream(stream => checksum = Convert.ToHexString(SHA256.HashData(stream))))
                .ConfigureAwait(false);
            Assert.AreEqual(expected.Workload.ChecksumSha256, checksum, ignoreCase: true);
        }
        var finalState = await ReadIssue249InitialStateAsync(fixture, uploads).ConfigureAwait(false);
        Assert.AreEqual(initialState.Count, finalState.Count);
        foreach (var (artifactId, expected) in initialState)
        {
            CollectionAssert.AreEqual(expected.JobIds, finalState[artifactId].JobIds);
        }
    }

    private static async Task ValidateIssue249EvidenceSourceAsync(
        string repositoryRoot,
        EvidenceSourceSnapshot source,
        string phase,
        string productionRevision)
    {
        if (phase == "development")
        {
            return;
        }
        Assert.IsFalse(source.Dirty, "claimable evidence requires a clean worktree");
        Assert.IsNotNull(source.RequestedRevision, "claimable evidence requires HVO_EVIDENCE_REVISION");
        Assert.IsNotNull(source.Trial, "claimable evidence requires HVO_EVIDENCE_TRIAL");
        Assert.AreEqual("clean-source-attributed-review-required", source.Claimability);
        var resolvedProduction = (await RunIssue249GitAsync(
            repositoryRoot, "rev-parse", $"{productionRevision}^{{commit}}").ConfigureAwait(false)).Trim();
        Assert.AreEqual(productionRevision, resolvedProduction, ignoreCase: true);
        if (phase == "baseline")
        {
            Assert.AreEqual(Issue249BaselineRevision, productionRevision, ignoreCase: true);
            var ancestor = await RunIssue249GitExitCodeAsync(
                repositoryRoot, "merge-base", "--is-ancestor", Issue249BaselineRevision, source.Head)
                .ConfigureAwait(false);
            Assert.AreEqual(0, ancestor, "the pinned baseline production revision must be an ancestor");
            var changedPaths = (await RunIssue249GitAsync(
                    repositoryRoot,
                    "diff",
                    "--name-only",
                    $"{productionRevision}..{source.Head}",
                    "--").ConfigureAwait(false))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            CollectionAssert.AreEqual(
                Issue249BaselineChangedPaths,
                changedPaths);
        }
        else
        {
            Assert.AreEqual(source.Head, productionRevision, ignoreCase: true);
        }
    }

    private static async Task<string> RunIssue249GitAsync(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        Assert.AreEqual(0, process.ExitCode, error);
        return output;
    }

    private static async Task<int> RunIssue249GitExitCodeAsync(
        string repositoryRoot,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = new Process { StartInfo = startInfo };
        Assert.IsTrue(process.Start());
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static void AssertIssue249EvidenceIsSafe(string json, IntegrationTestFixture fixture)
    {
        var forbidden = new[]
        {
            IntegrationTestFixture.MinioAccessKey,
            IntegrationTestFixture.MinioSecretKey,
            fixture.SqlServerConnectionString,
            "Authorization",
            "AccessToken",
            "s3://skymonitor-artifacts/"
        };
        foreach (var value in forbidden.Where(static value => !string.IsNullOrEmpty(value)))
        {
            Assert.IsFalse(json.Contains(value, StringComparison.OrdinalIgnoreCase));
        }
    }

    private sealed class Issue249DelayedGetHandler(
        TimeSpan delay,
        int expectedFirstWave,
        bool holdFirstWave = false) : DelegatingHandler
    {
        private readonly TaskCompletionSource _firstWaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWaveDelayCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstWaveRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;
        private int _completed;

        internal Task FirstWaveEntered => _firstWaveEntered.Task;

        internal Task FirstWaveDelayCompleted => _firstWaveDelayCompleted.Task;

        internal void ReleaseFirstWave() => _firstWaveRelease.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                var sequence = Interlocked.Increment(ref _entered);
                if (sequence == expectedFirstWave)
                {
                    _firstWaveEntered.TrySetResult();
                }
                if (holdFirstWave && sequence <= expectedFirstWave)
                {
                    await _firstWaveEntered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                if (sequence <= expectedFirstWave
                    && Interlocked.Increment(ref _completed) == expectedFirstWave)
                {
                    _firstWaveDelayCompleted.TrySetResult();
                }
                if (holdFirstWave && sequence <= expectedFirstWave)
                {
                    await _firstWaveRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class Issue249RetryCollector : IDisposable
    {
        private readonly MeterListener listener = new();
        private long sqlDeadlockRetries;

        public Issue249RetryCollector()
        {
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == CentralIngestTelemetry.MeterName
                    && instrument.Name == "skymonitor.central.ingest.reconciliation_concurrency")
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == "outcome"
                        && string.Equals(tag.Value as string, "sql-deadlock-retry", StringComparison.Ordinal))
                    {
                        Interlocked.Add(ref sqlDeadlockRetries, measurement);
                    }
                }
            });
            listener.Start();
        }

        public long SqlDeadlockRetries => Interlocked.Read(ref sqlDeadlockRetries);

        public void Dispose() => listener.Dispose();
    }

    private sealed record Issue249DuplicateScenario(
        Workload Workload,
        int Concurrency,
        ExpectedUpload[] Uploads);

    private sealed record Issue249DelayScenario(TimeSpan Delay, ExpectedUpload[] Uploads);

    private sealed record Issue249InitialArtifactState(
        string StorageReference,
        long RecoveryGeneration,
        Guid[] JobIds);

    private sealed record Issue249DelayEvidence(
        double DelayMilliseconds,
        int Objects,
        int Concurrency,
        double ElapsedMilliseconds,
        double MedianMilliseconds,
        double MaximumMilliseconds,
        Issue249SqlEvidence SqlDuringSyntheticDelay,
        ResourceEvidence Resources,
        ProtocolEvidence Protocol);

    private sealed record Issue249ContentionEvidence(
        double GetDelayMilliseconds,
        Issue249WriterEvidence Writer,
        Issue249SqlEvidence SqlDuringSyntheticDelay,
        double StatusElapsedMilliseconds);

    private sealed record Issue249WriterEvidence(string Outcome, double ElapsedMilliseconds);

    private sealed record Issue249SqlEvidence(
        int Samples,
        int MaximumSessions,
        int MaximumActiveRequests,
        int MaximumOpenTransactionSessions,
        int MaximumSessionApplicationLocks,
        int MaximumBlockedWriterRequests,
        long MaximumActiveTransactionLogBytes,
        double TransactionOverlapMilliseconds,
        double ApplicationLockWindowMilliseconds,
        IReadOnlyList<Issue249SqlSample> RawSamples);

    private sealed record Issue249SqlSample(
        double ElapsedMilliseconds,
        int Sessions,
        int ActiveRequests,
        int OpenTransactionSessions,
        int SessionApplicationLocks,
        int BlockedWriterRequests,
        long ActiveTransactionLogBytes);
}
