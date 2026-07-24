using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Minio;
using Minio.DataModel.Args;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Process observations and streamed SHA-256 validation are intentionally synchronous.")]
public sealed class LogicHostIngestPerformanceTests
{
    private const int StandardWarmups = 5;
    private const int StandardMeasurements = 30;
    private const int ConcurrentWarmups = 20;
    private const int ConcurrentMeasurements = 200;
    private const int ProfilesPerFrame = 5;
    private const string ArtifactBucket = "skymonitor-artifacts";
    private static readonly int[] ConcurrencyLevels = [1, 4, 8];
    private static readonly DateTimeOffset CaptureStartUtc = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NativeManifestV2Ingest_W1W2AndW4_RecordsPerformanceEvidence()
    {
        var evidenceRun = Issue170PerformanceEvidence.Create();
        var harnessStarted = Stopwatch.GetTimestamp();
        var fixture = AssemblyHooks.Fixture;
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

        using var protocolCounter = new ProtocolCounter(fixture.MinioEndpoint);
        var allocator = new UploadAllocator(w1, w2);
        var successfulUploads = new List<ExpectedUpload>();
        var steadyState = new List<IngestMeasurement>();

        var normalW1 = allocator.Create(w1, StandardWarmups + StandardMeasurements);
        steadyState.Add(await MeasureAcceptedAsync(
            fixture, client, protocolCounter, "normal-W1", normalW1, StandardWarmups, 1).ConfigureAwait(false));
        successfulUploads.AddRange(normalW1);

        var normalW2 = allocator.Create(w2, StandardWarmups + StandardMeasurements);
        steadyState.Add(await MeasureAcceptedAsync(
            fixture, client, protocolCounter, "normal-W2", normalW2, StandardWarmups, 1).ConfigureAwait(false));
        successfulUploads.AddRange(normalW2);

        foreach (var concurrency in ConcurrencyLevels)
        {
            var uploads = allocator.Create(w1, ConcurrentWarmups + ConcurrentMeasurements);
            steadyState.Add(await MeasureAcceptedAsync(
                fixture, client, protocolCounter, $"W4-W1-C{concurrency}", uploads, ConcurrentWarmups, concurrency)
                .ConfigureAwait(false));
            successfulUploads.AddRange(uploads);
        }

        var duplicateSources = Enumerable.Range(0, StandardWarmups + StandardMeasurements)
            .Select(index => index % 2 == 0 ? normalW1[index / 2] : normalW2[index / 2])
            .ToArray();
        var duplicate = await MeasureAcceptedAsync(
            fixture,
            client,
            protocolCounter,
            "duplicate-mixed-W1-W2",
            duplicateSources,
            StandardWarmups,
            1).ConfigureAwait(false);

        var outOfOrderUploads = allocator.CreateMixed(StandardWarmups)
            .OrderByDescending(static upload => upload.Manifest.Descriptor.Capture.CaptureSequence)
            .ThenByDescending(static upload => upload.Workload.Id, StringComparer.Ordinal)
            .Concat(allocator.CreateMixed(StandardMeasurements)
                .OrderByDescending(static upload => upload.Manifest.Descriptor.Capture.CaptureSequence)
                .ThenByDescending(static upload => upload.Workload.Id, StringComparer.Ordinal))
            .ToArray();
        var outOfOrder = await MeasureAcceptedAsync(
            fixture, client, protocolCounter, "out-of-order-mixed-W1-W2", outOfOrderUploads, StandardWarmups, 1)
            .ConfigureAwait(false);
        successfulUploads.AddRange(outOfOrderUploads);
        await AssertSequencesPersistedAsync(outOfOrderUploads).ConfigureAwait(false);

        var corruptPayloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [w1.Id] = CreateCorruptPayload(w1.Payload),
            [w2.Id] = CreateCorruptPayload(w2.Payload)
        };
        var corruptUploads = allocator.CreateMixed(StandardWarmups + StandardMeasurements);
        var corrupt = await MeasureFaultRecoveryAsync(
            fixture,
            client,
            protocolCounter,
            "corrupt-mixed-W1-W2",
            corruptUploads,
            _ => { },
            static () => { },
            upload => corruptPayloads[upload.Workload.Id],
            HttpStatusCode.BadRequest,
            durableFailures: false).ConfigureAwait(false);
        successfulUploads.AddRange(corruptUploads);

        var objectFault = new ObjectProtocolFaultHandler { InnerHandler = new SocketsHttpHandler() };
        using var objectFaultFactory = CreateObjectFaultFactory(fixture, objectFault, startWorker: false);
        using var objectFaultClient = objectFaultFactory.CreateClient();
        SetAuthorization(objectFaultClient, token.AccessToken);
        var objectFaultUploads = allocator.CreateMixed(StandardWarmups + StandardMeasurements);
        var objectStoreFault = await MeasureFaultRecoveryAsync(
            fixture,
            objectFaultClient,
            protocolCounter,
            "object-store-fault-mixed-W1-W2",
            objectFaultUploads,
            _ => objectFault.Arm(),
            objectFault.Disarm,
            static upload => upload.Workload.Payload,
            HttpStatusCode.InternalServerError,
            durableFailures: false).ConfigureAwait(false);
        successfulUploads.AddRange(objectFaultUploads);

        var sqlFault = new EverySecondCommitFaultInterceptor();
        using var sqlFaultFactory = CreateSqlFaultFactory(fixture, sqlFault, startWorker: false);
        using var sqlFaultClient = sqlFaultFactory.CreateClient();
        SetAuthorization(sqlFaultClient, token.AccessToken);
        var sqlFaultUploads = allocator.CreateMixed(StandardWarmups + StandardMeasurements);
        var sqlStoreFault = await MeasureFaultRecoveryAsync(
            fixture,
            sqlFaultClient,
            protocolCounter,
            "sql-commit-fault-mixed-W1-W2",
            sqlFaultUploads,
            sqlFault.Arm,
            sqlFault.Disarm,
            static upload => upload.Workload.Payload,
            HttpStatusCode.InternalServerError).ConfigureAwait(false);
        successfulUploads.AddRange(sqlFaultUploads);

        var restartUploads = allocator.CreateMixed(StandardWarmups + StandardMeasurements);
        var restart = await MeasureRestartRecoveryAsync(
            fixture, token.AccessToken, protocolCounter, restartUploads).ConfigureAwait(false);
        successfulUploads.AddRange(restartUploads);

        var correctness = await ValidatePersistedResultsAsync(fixture, successfulUploads, w1, w2).ConfigureAwait(false);
        var locationBindingMethod = typeof(ArtifactIngestService).GetMethod(
            "BindCaptureLocationAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var locationBindingCanAccessPayload = locationBindingMethod?.GetParameters().Any(parameter =>
            parameter.ParameterType == typeof(byte[])
            || typeof(Stream).IsAssignableFrom(parameter.ParameterType)
            || parameter.ParameterType == typeof(Memory<byte>)
            || parameter.ParameterType == typeof(ReadOnlyMemory<byte>)) == true;
        Assert.IsFalse(locationBindingCanAccessPayload);
        var command = evidenceRun.CreateTestCommand(
            "LogicHostIngestPerformanceTests.NativeManifestV2Ingest_W1W2AndW4_RecordsPerformanceEvidence");
        var evidence = new
        {
            Schema = "hvo-logichost-ingest-performance-v3",
            Issue = 170,
            Revision = evidenceRun.Revision,
            Environment = new
            {
                Observed = evidenceRun.Environment,
                Declared = new
                {
                    Sdk = ReadPinnedSdkVersion(evidenceRun.RepositoryRoot),
                    Configuration = "Release",
                    SqlServer = "SQL Server 2022 CU14 Ubuntu 22.04 Testcontainer",
                    Minio = "MinIO RELEASE.2025-09-07T16-13-09Z Testcontainer",
                    Http = "ASP.NET Core TestServer"
                }
            },
            BuildCommand = Issue170PerformanceEvidence.BuildCommand,
            Command = command,
            Workload = new
            {
                FixtureVersion = "issue-98-coordinate-linear16-v2",
                Boundary = "HTTP request start through durable acknowledgement/failure; restart convergence starts immediately before constructing one fresh production host and ends at zero pending rows.",
                ArrivalModel = "Closed-loop; all standard and fault scenarios use concurrency 1, W4 uses concurrency 1/4/8.",
                Normal = new[] { WorkloadMetadata(w1, StandardWarmups, StandardMeasurements, [1]), WorkloadMetadata(w2, StandardWarmups, StandardMeasurements, [1]) },
                FaultAndOrdering = new
                {
                    Payloads = "Thirty measured operations alternate W1 and W2 (15 each); five warmups alternate 3 W1 and 2 W2.",
                    Warmups = StandardWarmups,
                    Measurements = StandardMeasurements,
                    Scenarios = new[] { "duplicate", "out-of-order", "corrupt", "object-store-fault", "sql-commit-fault", "restart-reconciliation" }
                },
                W4 = WorkloadMetadata(w1, ConcurrentWarmups, ConcurrentMeasurements, ConcurrencyLevels),
                Restart = "Five genuine pending warmups are reconciled by the production reconciliation method. Thirty new genuine pending intents with published final objects are then recovered after exactly one fresh production host/worker start."
            },
            Method = new
            {
                Latency = "Nearest-rank median/p95/maximum over 30 independent measured logical operations after five warmups; W4 uses 200 measured operations after 20 warmups.",
                Resources = "Process.TotalProcessorTime, monotonic GC.GetTotalAllocatedBytes(true), and 10 ms Process.WorkingSet64 sampling for the test process including TestServer.",
                Sql = "EF Core diagnostic events observe commands and transaction start/commit/rollback/failure. SQL wire bytes are unavailable from the provider and are reported as unavailable, not estimated.",
                ObjectStore = "System.Net.Http diagnostics and the deterministic boundary handler observe MinIO methods and request/response Content-Length when supplied. Content-Length is a header observation, not a claim that HEAD response bodies transferred; missing values remain unavailable.",
                Faults = "Object faults return HTTP 503 from a delegating handler at the MinIO S3 PUT boundary. SQL faults throw at EF TransactionCommittingAsync on each second ingest transaction, after the durable intent commit and object publication.",
                HttpBytes = "Logical payload bytes are fixture-derived application bytes. Exact request-content body bytes, including multipart framing, are counted separately for every multipart and manifest-only status POST; TestServer transport headers are excluded.",
                Correctness = "Acknowledgement identity/checksum/length, normalized SQL state/profile/layout/recipe/empty ordered lineage, reconstruction, and every final object SHA-256 are asserted.",
                PayloadMemory = "N/A: phase-isolated object-type and copy attribution requires profiler instrumentation disproportionate to this metadata-only change."
            },
            Measurements = new
            {
                SteadyState = steadyState,
                Duplicate = duplicate,
                OutOfOrder = outOfOrder,
                Corrupt = corrupt,
                ObjectStoreFault = new
                {
                    BoundaryObservedIncludingWarmupAndRecovery = objectFault.Snapshot(),
                    InjectedFailureSplit = new { Warmup = StandardWarmups, Measured = StandardMeasurements },
                    Evidence = objectStoreFault
                },
                SqlCommitFault = new { sqlFault.InjectedFailures, Evidence = sqlStoreFault },
                RestartReconciliation = restart
            },
            Correctness = correctness,
            Result = new
            {
                Candidate = "Location-bound native manifest-v2 measurements are recorded in Measurements.",
                BaselineChange = "A reviewed summary must first verify five clean baseline and candidate trials have matching workload and environment fingerprints.",
                LocationBindingPayloadAccess = locationBindingCanAccessPayload,
                LocationBindingFullFrameCopies = locationBindingCanAccessPayload
                    ? "Unproven"
                    : "Structurally zero within location binding: the method accepts only frame metadata, coordinate-free provenance, and cancellation; no payload, stream, or memory buffer is in scope.",
                LohInterpretation = "Payload arrays are LOH-sized. Process allocation and 10 ms RSS peaks include LOH effects, but System.Runtime does not provide a phase-isolated LOH byte counter; no separate LOH byte value is claimed.",
                W4Scaling = CreateW4ScalingEvidence(steadyState),
                Interpretation = "Physical timing is environment-specific; durable state, identity, lineage, and checksums are pass/fail.",
                ResidualRisk = "TestServer excludes kernel TCP/TLS and process counters exclude SQL Server/MinIO containers. SQL wire bytes and absent HTTP Content-Length values are unavailable."
            },
            HarnessElapsedMilliseconds = Stopwatch.GetElapsedTime(harnessStarted).TotalMilliseconds,
            RecordedAtUtc = DateTimeOffset.UtcNow
        };

        await evidenceRun.WriteTrialAsync("logichost-ingest-performance.json", evidence).ConfigureAwait(false);
    }

    private static async Task<IngestMeasurement> MeasureAcceptedAsync(
        IntegrationTestFixture fixture,
        HttpClient client,
        ProtocolCounter protocolCounter,
        string scenario,
        IReadOnlyList<ExpectedUpload> uploads,
        int warmupCount,
        int concurrency)
    {
        var warmups = uploads.Take(warmupCount).ToArray();
        var measured = uploads.Skip(warmupCount).ToArray();
        await ExecuteAcceptedAsync(client, warmups, concurrency, null, null).ConfigureAwait(false);
        var initialBacklog = await ReadBacklogAsync(fixture, uploads).ConfigureAwait(false);
        Assert.AreEqual(0, initialBacklog.Count);

        StabilizeGc();
        using var resources = new ResourceSampler();
        var latencies = new ConcurrentBag<double>();
        var attempts = new HttpAttemptCounter();
        protocolCounter.Start();
        var started = Stopwatch.GetTimestamp();
        await ExecuteAcceptedAsync(client, measured, concurrency, latencies, attempts).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var protocols = protocolCounter.Stop();
        var resourceResult = await resources.StopAsync().ConfigureAwait(false);
        var finalBacklog = await ReadBacklogAsync(fixture, uploads).ConfigureAwait(false);
        Assert.AreEqual(0, finalBacklog.Count);
        var sorted = latencies.Order().ToArray();
        Assert.AreEqual(measured.Length, sorted.Length);
        var payloadBytes = measured.Sum(static upload => upload.Workload.Payload.LongLength);
        return new IngestMeasurement(
            scenario,
            measured.Select(static upload => upload.Workload.Id).Distinct().Order().ToArray(),
            concurrency,
            warmupCount,
            measured.Length,
            sorted,
            payloadBytes,
            elapsed.TotalMilliseconds,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1],
            measured.Length / elapsed.TotalSeconds,
            payloadBytes / elapsed.TotalSeconds,
            resourceResult,
            initialBacklog,
            finalBacklog,
            CreateProtocolEvidence(attempts, protocols, payloadBytes),
            "No injected outage exists for normal, duplicate, or ordering conditions; fault/recovery backlog is N/A. Initial and final unresolved backlog are observed.");
    }

    private static async Task<FaultRecoveryMeasurement> MeasureFaultRecoveryAsync(
        IntegrationTestFixture fixture,
        HttpClient client,
        ProtocolCounter protocolCounter,
        string scenario,
        IReadOnlyList<ExpectedUpload> uploads,
        Action<int> armFault,
        Action disarmFault,
        Func<ExpectedUpload, byte[]> faultPayload,
        HttpStatusCode faultStatus,
        bool durableFailures = true)
    {
        var warmups = uploads.Take(StandardWarmups).ToArray();
        var measured = uploads.Skip(StandardWarmups).ToArray();
        armFault(warmups.Length);
        await ExecuteExpectedAsync(client, warmups, faultPayload, faultStatus, null, null).ConfigureAwait(false);
        disarmFault();
        await ExecuteAcceptedAsync(client, warmups, 1, null, null).ConfigureAwait(false);
        var initialBacklog = await ReadBacklogAsync(fixture, uploads).ConfigureAwait(false);
        Assert.AreEqual(0, initialBacklog.Count);

        StabilizeGc();
        using var resources = new ResourceSampler();
        var faultLatencies = new ConcurrentBag<double>();
        var faultAttempts = new HttpAttemptCounter();
        var transitionStarted = Stopwatch.GetTimestamp();
        armFault(measured.Length);
        protocolCounter.Start();
        var faultStarted = Stopwatch.GetTimestamp();
        await ExecuteExpectedAsync(client, measured, faultPayload, faultStatus, faultLatencies, faultAttempts).ConfigureAwait(false);
        var faultElapsed = Stopwatch.GetElapsedTime(faultStarted);
        var faultProtocols = protocolCounter.Stop();
        disarmFault();
        var faultBacklog = await ReadBacklogAsync(fixture, uploads).ConfigureAwait(false);
        Assert.AreEqual(durableFailures ? measured.Length : 0, faultBacklog.Count);

        var recoveryLatencies = new ConcurrentBag<double>();
        var recoveryAttempts = new HttpAttemptCounter();
        protocolCounter.Start();
        var recoveryStarted = Stopwatch.GetTimestamp();
        await ExecuteAcceptedAsync(client, measured, 1, recoveryLatencies, recoveryAttempts).ConfigureAwait(false);
        var recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
        var recoveryProtocols = protocolCounter.Stop();
        var transitionElapsed = Stopwatch.GetElapsedTime(transitionStarted);
        var resourceResult = await resources.StopAsync().ConfigureAwait(false);
        var recoveredBacklog = await ReadBacklogAsync(fixture, uploads).ConfigureAwait(false);
        Assert.AreEqual(0, recoveredBacklog.Count);

        var faultSorted = faultLatencies.Order().ToArray();
        var recoverySorted = recoveryLatencies.Order().ToArray();
        var payloadBytes = measured.Sum(static upload => upload.Workload.Payload.LongLength);
        return new FaultRecoveryMeasurement(
            scenario,
            measured.Select(static upload => upload.Workload.Id).Distinct().Order().ToArray(),
            StandardWarmups,
            measured.Length,
            transitionElapsed.TotalMilliseconds,
            measured.Length / transitionElapsed.TotalSeconds,
            resourceResult,
            initialBacklog,
            faultBacklog,
            recoveredBacklog,
            CreatePhaseEvidence(faultElapsed, faultSorted, faultAttempts, faultProtocols, payloadBytes),
            CreatePhaseEvidence(recoveryElapsed, recoverySorted, recoveryAttempts, recoveryProtocols, payloadBytes),
            durableFailures
                ? "All measured failures created durable unresolved rows; valid recovery requests converged each identity to one Available/Complete row and zero unresolved backlog."
                : "All measured failures occurred before durable intent creation; valid recovery requests created one Available/Complete row per identity and zero unresolved backlog.");
    }

    private static async Task<RestartRecoveryMeasurement> MeasureRestartRecoveryAsync(
        IntegrationTestFixture fixture,
        string accessToken,
        ProtocolCounter protocolCounter,
        IReadOnlyList<ExpectedUpload> uploads)
    {
        var warmups = uploads.Take(StandardWarmups).ToArray();
        var measured = uploads.Skip(StandardWarmups).ToArray();
        var interceptor = new EverySecondCommitFaultInterceptor();
        BacklogSnapshot initialBacklog;
        using (var faultFactory = CreateSqlFaultFactory(fixture, interceptor, startWorker: false))
        {
            using var faultClient = faultFactory.CreateClient();
            SetAuthorization(faultClient, accessToken);
            interceptor.Arm(warmups.Length);
            await ExecuteExpectedAsync(
                faultClient, warmups, static upload => upload.Workload.Payload,
                HttpStatusCode.InternalServerError, null, null).ConfigureAwait(false);
            interceptor.Disarm();
            await RunReconciliationCycleAsync(faultFactory.Services).ConfigureAwait(false);
            var warmupBacklog = await ReadBacklogAsync(fixture, warmups).ConfigureAwait(false);
            Assert.AreEqual(0, warmupBacklog.Count);
            initialBacklog = await ReadBacklogAsync(fixture, measured).ConfigureAwait(false);
            Assert.AreEqual(0, initialBacklog.Count);

            interceptor.Arm(measured.Length);
            await ExecuteExpectedAsync(
                faultClient, measured, static upload => upload.Workload.Payload,
                HttpStatusCode.InternalServerError, null, null).ConfigureAwait(false);
            interceptor.Disarm();
        }

        var faultBacklog = await ReadBacklogAsync(fixture, measured).ConfigureAwait(false);
        Assert.AreEqual(StandardMeasurements, faultBacklog.Count);
        Assert.AreEqual(StandardMeasurements, faultBacklog.PendingObjectCount);
        Assert.AreEqual(measured.Sum(static upload => upload.Workload.Payload.LongLength), faultBacklog.Bytes);
        StabilizeGc();
        using var resources = new ResourceSampler();
        protocolCounter.Start();
        var recoveryStarted = Stopwatch.GetTimestamp();
        var recoveryObjectProtocol = new ObjectProtocolFaultHandler { InnerHandler = new SocketsHttpHandler() };
        using (var recoveryFactory = CreateObjectFaultFactory(fixture, recoveryObjectProtocol, startWorker: true))
        {
            _ = recoveryFactory.Services;
            await WaitForArtifactsAsync(recoveryFactory.Services, measured).ConfigureAwait(false);
        }
        var recoveryElapsed = Stopwatch.GetElapsedTime(recoveryStarted);
        var protocols = protocolCounter.Stop();
        var resourceResult = await resources.StopAsync().ConfigureAwait(false);
        var finalBacklog = await ReadBacklogAsync(fixture, measured).ConfigureAwait(false);
        Assert.AreEqual(0, finalBacklog.Count);
        return new RestartRecoveryMeasurement(
            StandardWarmups,
            StandardMeasurements,
            initialBacklog,
            faultBacklog,
            finalBacklog,
            recoveryElapsed.TotalMilliseconds,
            StandardMeasurements / recoveryElapsed.TotalSeconds,
            faultBacklog.Bytes / recoveryElapsed.TotalSeconds,
            resourceResult,
            protocols,
            recoveryObjectProtocol.Snapshot(),
            "Observed after one fresh production host/worker start: all 30 previously committed Pending intents with genuine final W1/W2 objects converged to Available/Complete and zero backlog.");
    }

    private static async Task RunReconciliationCycleAsync(IServiceProvider services)
    {
        using var telemetry = new CentralIngestTelemetry();
        var reconciler = new CentralArtifactReconciliationService(
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            telemetry,
            NullLogger<CentralArtifactReconciliationService>.Instance);
        await reconciler.ReconcileAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateObjectFaultFactory(
        IntegrationTestFixture fixture,
        ObjectProtocolFaultHandler faultHandler,
        bool startWorker)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IMinioClient>();
            services.AddSingleton<IMinioClient>(_ =>
            {
                var httpClient = new HttpClient(faultHandler, disposeHandler: false);
                return new MinioClient()
                    .WithEndpoint(fixture.MinioEndpoint)
                    .WithCredentials(IntegrationTestFixture.MinioAccessKey, IntegrationTestFixture.MinioSecretKey)
                    .WithHttpClient(httpClient, disposeHttpClient: true)
                    .Build();
            });
            if (startWorker)
            {
                services.AddHostedService<CentralArtifactReconciliationService>();
            }
        }));

    private static WebApplicationFactory<HVO.SkyMonitor.LogicHost.Program> CreateSqlFaultFactory(
        IntegrationTestFixture fixture,
        EverySecondCommitFaultInterceptor interceptor,
        bool startWorker)
        => fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DbContextOptions<ApplicationDbContext>>();
            services.RemoveAll<ApplicationDbContext>();
            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseSqlServer(fixture.SqlServerConnectionString);
                options.AddInterceptors(interceptor);
                options.EnableSensitiveDataLogging();
                options.EnableDetailedErrors();
            });
            if (startWorker)
            {
                services.AddHostedService<CentralArtifactReconciliationService>();
            }
        }));

    private static async Task ExecuteAcceptedAsync(
        HttpClient client,
        IReadOnlyList<ExpectedUpload> uploads,
        int concurrency,
        ConcurrentBag<double>? latencies,
        HttpAttemptCounter? attempts)
    {
        await Parallel.ForEachAsync(
            uploads,
            new ParallelOptions { MaxDegreeOfParallelism = concurrency },
            async (upload, cancellationToken) =>
            {
                var started = Stopwatch.GetTimestamp();
                var statusOnly = false;
                for (var attempt = 0; ; attempt++)
                {
                    var response = statusOnly
                        ? await SendStatusAsync(client, upload, cancellationToken).ConfigureAwait(false)
                        : await SendUploadAsync(client, upload, upload.Workload.Payload, cancellationToken).ConfigureAwait(false);
                    if (statusOnly)
                    {
                        attempts?.RecordStatus(response.RequestBodyBytes);
                    }
                    else
                    {
                        attempts?.RecordMultipart(response.RequestBodyBytes, upload.Workload.Payload.LongLength);
                    }
                    if (response.StatusCode == HttpStatusCode.InternalServerError && attempt < 99)
                    {
                        attempts?.RecordServerErrorRetry();
                        if (response.Body.Contains("deadlocked on lock resources", StringComparison.OrdinalIgnoreCase))
                        {
                            attempts?.RecordDeadlockRetry();
                        }
                        var jitter = upload.Manifest.Descriptor.Artifact.ArtifactId.ToByteArray()[0] % 32;
                        var backoff = Math.Min(1000, 25 * (attempt + 1)) + jitter;
                        await Task.Delay(TimeSpan.FromMilliseconds(backoff), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    if ((int)response.StatusCode == 425 && attempt < 99)
                    {
                        attempts?.RecordPendingReferenceRetry();
                        statusOnly = true;
                        await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    Assert.AreEqual(HttpStatusCode.Accepted, response.StatusCode, response.Body);
                    AssertAcknowledgement(upload, response.Body);
                    latencies?.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    break;
                }
            }).ConfigureAwait(false);
    }

    private static async Task ExecuteExpectedAsync(
        HttpClient client,
        IReadOnlyList<ExpectedUpload> uploads,
        Func<ExpectedUpload, byte[]> payload,
        HttpStatusCode expectedStatus,
        ConcurrentBag<double>? latencies,
        HttpAttemptCounter? attempts)
    {
        foreach (var upload in uploads)
        {
            var attemptPayload = payload(upload);
            var response = await SendUploadAsync(client, upload, attemptPayload, CancellationToken.None).ConfigureAwait(false);
            attempts?.RecordMultipart(response.RequestBodyBytes, attemptPayload.LongLength);
            Assert.AreEqual(expectedStatus, response.StatusCode, response.Body);
            latencies?.Add(response.ElapsedMilliseconds);
        }
    }

    private static async Task<UploadResponse> SendUploadAsync(
        HttpClient client,
        ExpectedUpload upload,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(upload.ManifestJson)
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
        }, "manifest");
        content.Add(new ByteArrayContent(payload)
        {
            Headers = { ContentType = new MediaTypeHeaderValue(upload.Manifest.Descriptor.Artifact.MediaType) }
        }, "payload", "artifact.bin");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1.0/artifacts", UriKind.Relative))
        {
            Content = content
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", upload.Manifest.IdempotencyKey);
        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var requestBodyBytes = request.Content.Headers.ContentLength
            ?? throw new InvalidOperationException("Multipart request content length was not available.");
        return new(response.StatusCode, body, Stopwatch.GetElapsedTime(started).TotalMilliseconds, requestBodyBytes);
    }

    private static async Task<UploadResponse> SendStatusAsync(
        HttpClient client,
        ExpectedUpload upload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1.0/artifacts/status", UriKind.Relative))
        {
            Content = new ByteArrayContent(upload.ManifestJson)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") }
            }
        };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", upload.Manifest.IdempotencyKey);
        var started = Stopwatch.GetTimestamp();
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return new(
            response.StatusCode,
            body,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            request.Content.Headers.ContentLength ?? upload.ManifestJson.LongLength);
    }

    private static void AssertAcknowledgement(ExpectedUpload upload, string body)
    {
        var acknowledgement = JsonSerializer.Deserialize<ArtifactUploadAcknowledgement>(body, HttpHelpers.DefaultJsonOptions);
        Assert.IsNotNull(acknowledgement);
        acknowledgement.Validate();
        Assert.AreEqual(ArtifactUploadAcknowledgement.CurrentSchemaVersion, acknowledgement.SchemaVersion);
        Assert.AreEqual(upload.Manifest.IdempotencyKey, acknowledgement.IdempotencyKey, ignoreCase: true);
        Assert.AreEqual(upload.Manifest.Descriptor.Artifact.ArtifactId, acknowledgement.ArtifactId);
        Assert.AreEqual(upload.Workload.ChecksumSha256, acknowledgement.ChecksumSha256, ignoreCase: true);
        Assert.AreEqual(upload.Workload.Payload.LongLength, acknowledgement.ByteLength);
        Assert.AreEqual(ArtifactManifestV2.CurrentSchemaVersion, acknowledgement.AcceptedManifestSchemaVersion);
    }

    private static async Task<BacklogSnapshot> ReadBacklogAsync(
        IntegrationTestFixture fixture,
        IReadOnlyList<ExpectedUpload> uploads)
    {
        var artifactIds = uploads.Select(static upload => upload.Manifest.Descriptor.Artifact.ArtifactId).ToArray();
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var rows = await db.CentralArtifacts.AsNoTracking()
            .Where(artifact => artifactIds.Contains(artifact.ArtifactId)
                && (artifact.ObjectState == CentralArtifactObjectState.Pending
                    || artifact.ObjectState == CentralArtifactObjectState.Quarantined
                    || artifact.ReconstructionState == CentralReconstructionState.PendingReference
                    || artifact.ReconstructionState == CentralReconstructionState.Quarantined))
            .Select(artifact => new
            {
                artifact.ByteLength,
                artifact.ReceivedAtUtc,
                artifact.ObjectState,
                artifact.ReconstructionState
            })
            .ToListAsync().ConfigureAwait(false);
        var oldest = rows.Count == 0 ? 0 : Math.Max(0, (DateTimeOffset.UtcNow - rows.Min(static row => row.ReceivedAtUtc)).TotalMilliseconds);
        return new(
            rows.Count,
            rows.Sum(static row => row.ByteLength),
            oldest,
            rows.Count(static row => row.ObjectState == CentralArtifactObjectState.Pending),
            rows.Count(static row => row.ObjectState == CentralArtifactObjectState.Quarantined),
            rows.Count(static row => row.ReconstructionState == CentralReconstructionState.PendingReference),
            rows.Count(static row => row.ReconstructionState == CentralReconstructionState.Quarantined));
    }

    private static async Task WaitForArtifactsAsync(IServiceProvider services, IReadOnlyList<ExpectedUpload> uploads)
    {
        var ids = uploads.Select(static upload => upload.Manifest.Descriptor.Artifact.ArtifactId).ToArray();
        var timeoutAt = DateTimeOffset.UtcNow.AddSeconds(60);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var completed = await db.CentralArtifacts.AsNoTracking().CountAsync(artifact =>
                ids.Contains(artifact.ArtifactId)
                && artifact.ObjectState == CentralArtifactObjectState.Available
                && artifact.ReconstructionState == CentralReconstructionState.Complete).ConfigureAwait(false);
            if (completed == ids.Length)
            {
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        }
        Assert.Fail("The fresh production reconciliation worker did not converge all 30 pending artifacts.");
    }

    private static async Task AssertSequencesPersistedAsync(IReadOnlyList<ExpectedUpload> uploads)
    {
        await using var scope = AssemblyHooks.Fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        foreach (var workloadGroup in uploads.GroupBy(static upload => upload.Workload))
        {
            var expected = workloadGroup.Select(static upload => upload.Manifest.Descriptor.Capture.CaptureSequence).Order().ToArray();
            var captureIds = workloadGroup.Select(static upload => upload.Manifest.Descriptor.Capture.CaptureId).ToArray();
            var actual = await db.CentralFrames.AsNoTracking()
                .Where(frame => captureIds.Contains(frame.FrameId))
                .OrderBy(frame => frame.CaptureSequence)
                .Select(frame => frame.CaptureSequence!.Value)
                .ToArrayAsync().ConfigureAwait(false);
            CollectionAssert.AreEqual(expected, actual);
        }
    }

    private static async Task<CorrectnessEvidence> ValidatePersistedResultsAsync(
        IntegrationTestFixture fixture,
        IReadOnlyList<ExpectedUpload> uploads,
        Workload w1,
        Workload w2)
    {
        var expectedByArtifact = uploads.ToDictionary(static upload => upload.Manifest.Descriptor.Artifact.ArtifactId);
        Assert.AreEqual(uploads.Count, expectedByArtifact.Count);
        var artifactIds = expectedByArtifact.Keys.ToArray();
        List<CentralArtifact> artifacts;
        int frameCount;
        int timingCount;
        int controlCount;
        int profileCount;
        int layoutCount;
        int recipeCount;
        int sourceCount;
        int identityCount;
        int jobCount;
        int[] jobsPerArtifact;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            artifacts = await db.CentralArtifacts
                .Include(static artifact => artifact.Layout)
                .Include(static artifact => artifact.Recipe)
                .Include(static artifact => artifact.Sources)
                .Include(static artifact => artifact.IngestIdentities)
                .Include(static artifact => artifact.Frame)!.ThenInclude(static frame => frame!.Timing)
                .Include(static artifact => artifact.Frame)!.ThenInclude(static frame => frame!.Control)
                .Include(static artifact => artifact.Frame)!.ThenInclude(static frame => frame!.Profiles)
                .AsSplitQuery()
                .Where(artifact => artifactIds.Contains(artifact.ArtifactId))
                .ToListAsync().ConfigureAwait(false);
            var frameIds = artifacts.Select(static artifact => artifact.CentralFrameId).ToArray();
            frameCount = await db.CentralFrames.CountAsync(frame => frameIds.Contains(frame.Id)).ConfigureAwait(false);
            timingCount = await db.CentralCaptureTimings.CountAsync(timing => frameIds.Contains(timing.CentralFrameId)).ConfigureAwait(false);
            controlCount = await db.CentralCaptureControls.CountAsync(control => frameIds.Contains(control.CentralFrameId)).ConfigureAwait(false);
            profileCount = await db.CentralCaptureProfiles.CountAsync(profile => frameIds.Contains(profile.CentralFrameId)).ConfigureAwait(false);
            layoutCount = await db.CentralArtifactLayouts.CountAsync(layout => artifactIds.Contains(layout.Artifact!.ArtifactId)).ConfigureAwait(false);
            recipeCount = await db.CentralArtifactRecipes.CountAsync(recipe => artifactIds.Contains(recipe.Artifact!.ArtifactId)).ConfigureAwait(false);
            sourceCount = await db.CentralArtifactSources.CountAsync(source => artifactIds.Contains(source.Artifact!.ArtifactId)).ConfigureAwait(false);
            identityCount = await db.CentralArtifactIngestIdentities.CountAsync(identity => artifactIds.Contains(identity.Artifact!.ArtifactId)).ConfigureAwait(false);
            jobsPerArtifact = await db.CentralDerivativeJobs
                .Where(job => artifactIds.Contains(job.SourceArtifact!.ArtifactId))
                .GroupBy(job => job.SourceArtifact!.ArtifactId)
                .Select(group => group.Count())
                .ToArrayAsync().ConfigureAwait(false);
            jobCount = jobsPerArtifact.Sum();
        }

        Assert.AreEqual(uploads.Count, artifacts.Count);
        Assert.AreEqual(uploads.Count, frameCount);
        Assert.AreEqual(uploads.Count, timingCount);
        Assert.AreEqual(uploads.Count, controlCount);
        Assert.AreEqual(uploads.Count * ProfilesPerFrame, profileCount);
        Assert.AreEqual(uploads.Count, layoutCount);
        Assert.AreEqual(uploads.Count, recipeCount);
        Assert.AreEqual(0, sourceCount);
        Assert.AreEqual(uploads.Count, identityCount);
        Assert.AreEqual(uploads.Count, jobsPerArtifact.Length);
        Assert.AreEqual(1, jobsPerArtifact.Distinct().Count());
        Assert.IsTrue(jobsPerArtifact[0] > 0);
        Assert.AreEqual(uploads.Count * jobsPerArtifact[0], jobCount);
        Assert.AreEqual(uploads.Count, artifacts.Select(static artifact => artifact.StorageReference).Distinct(StringComparer.Ordinal).Count());

        await using var validationScope = fixture.Factory.Services.CreateAsyncScope();
        var minio = validationScope.ServiceProvider.GetRequiredService<IMinioClient>();
        long objectBytesValidated = 0;
        foreach (var artifact in artifacts)
        {
            var expected = expectedByArtifact[artifact.ArtifactId];
            var descriptor = expected.Manifest.Descriptor;
            Assert.AreEqual(ArtifactManifestV2.CurrentSchemaVersion, artifact.ManifestSchemaVersion);
            Assert.AreEqual(expected.Manifest.IdempotencyKey, artifact.IdempotencyKey, ignoreCase: true);
            Assert.AreEqual(expected.Workload.ChecksumSha256, artifact.ChecksumSha256, ignoreCase: true);
            Assert.AreEqual(expected.Workload.Payload.LongLength, artifact.ByteLength);
            Assert.AreEqual(CentralArtifactObjectState.Available, artifact.ObjectState);
            Assert.AreEqual(CentralReconstructionState.Complete, artifact.ReconstructionState);
            Assert.IsNull(artifact.StateReasonCode);
            Assert.IsNotNull(artifact.ReconciledAtUtc);
            Assert.IsNotNull(artifact.Layout);
            Assert.AreEqual(descriptor.Layout.Width, artifact.Layout.Width);
            Assert.AreEqual(descriptor.Layout.Height, artifact.Layout.Height);
            Assert.AreEqual(descriptor.Layout.StrideBytes, artifact.Layout.StrideBytes);
            Assert.AreEqual(descriptor.Layout.PixelFormat.ToString(), artifact.Layout.PixelFormat);
            Assert.AreEqual(descriptor.Layout.ByteLength, artifact.Layout.ByteLength);
            Assert.IsNotNull(artifact.Recipe);
            Assert.AreEqual(descriptor.Artifact.Recipe.OptionsSha256, artifact.Recipe.OptionsSha256, ignoreCase: true);
            Assert.AreEqual(1, artifact.IngestIdentities.Count);
            Assert.AreEqual(expected.Manifest.IdempotencyKey, artifact.IngestIdentities.Single().IdempotencyKey, ignoreCase: true);
            Assert.AreEqual(0, artifact.Sources.Count);
            Assert.IsNotNull(artifact.Frame);
            Assert.AreEqual(descriptor.Capture.CaptureId, artifact.Frame.FrameId);
            Assert.AreEqual(descriptor.Capture.CaptureSequence, artifact.Frame.CaptureSequence);
            Assert.AreEqual(expected.Workload.RigProfileId, artifact.Frame.DeviceRigProfileId);
            Assert.AreEqual(1, artifact.Frame.RigProfileVersion);
            Assert.IsNotNull(artifact.Frame.Timing);
            Assert.IsNotNull(artifact.Frame.Control);
            Assert.AreEqual(ProfilesPerFrame, artifact.Frame.Profiles.Count);
            var locationProperty = artifact.Frame.GetType().GetProperty("Location");
            if (expected.Workload.Location is not null && locationProperty is not null)
            {
                var persistedLocation = locationProperty.GetValue(artifact.Frame);
                Assert.IsNotNull(persistedLocation);
                var state = artifact.Frame.GetType().GetProperty("LocationEvidenceState")?.GetValue(artifact.Frame);
                Assert.AreEqual("ReportedResolved", state?.ToString());
                Assert.AreEqual(
                    expected.Workload.Location,
                    new CaptureLocationProvenance(
                        (string)persistedLocation.GetType().GetProperty("LocationId")!.GetValue(persistedLocation)!,
                        (long)persistedLocation.GetType().GetProperty("Version")!.GetValue(persistedLocation)!,
                        (string)persistedLocation.GetType().GetProperty("Source")!.GetValue(persistedLocation)!,
                        (double?)persistedLocation.GetType().GetProperty("HorizontalAccuracyMeters")!.GetValue(persistedLocation),
                        (DateTimeOffset)persistedLocation.GetType().GetProperty("EffectiveFromUtc")!.GetValue(persistedLocation)!,
                        (DateTimeOffset?)persistedLocation.GetType().GetProperty("EffectiveUntilUtc")!.GetValue(persistedLocation)));
            }
            Assert.IsTrue(artifact.Frame.Profiles.Any(profile =>
                profile.Kind == CentralProfileKind.Rig
                && profile.DeviceRigProfileId == expected.Workload.RigProfileId
                && string.Equals(profile.Sha256, expected.Workload.RigSha256, StringComparison.OrdinalIgnoreCase)));

            var persistedDescriptor = CentralReconstructionDescriptorFactory.Create(artifact.Frame, artifact);
            var reconstruction = FrameReconstructor.TryReconstruct(persistedDescriptor, expected.Workload.Payload, out var frame);
            Assert.IsTrue(reconstruction.IsValid, $"{reconstruction.ReasonCode} at {reconstruction.FieldPath}");
            Assert.IsNotNull(frame);
            Assert.AreEqual(expected.Workload.Payload.LongLength, frame.PixelData.Length);

            var objectKey = artifact.StorageReference[$"minio://{ArtifactBucket}/".Length..];
            string? objectChecksum = null;
            var objectInfo = await minio.StatObjectAsync(new StatObjectArgs()
                .WithBucket(ArtifactBucket).WithObject(objectKey)).ConfigureAwait(false);
            await minio.GetObjectAsync(new GetObjectArgs()
                .WithBucket(ArtifactBucket)
                .WithObject(objectKey)
                .WithCallbackStream(stream => objectChecksum = Convert.ToHexString(SHA256.HashData(stream))))
                .ConfigureAwait(false);
            Assert.AreEqual(expected.Workload.Payload.LongLength, objectInfo.Size);
            Assert.AreEqual(expected.Workload.ChecksumSha256, objectChecksum, ignoreCase: true);
            objectBytesValidated += objectInfo.Size;
        }

        return new(
            uploads.Count,
            artifacts.Count,
            frameCount,
            timingCount,
            controlCount,
            profileCount,
            layoutCount,
            recipeCount,
            sourceCount,
            identityCount,
            jobCount,
            artifacts.Count,
            objectBytesValidated,
            artifacts.Count,
            artifacts.Count,
            objectBytesValidated,
            w1.ChecksumSha256,
            w2.ChecksumSha256,
            "Every successful acknowledgement and normalized identity/layout/profile/recipe/state was checked; raw lineage was exactly the declared empty ordered list; every final object passed HEAD length and streamed SHA-256 verification.");
    }

    private static Workload CreateWorkload(string id, int width, int height, CameraPixelFormat format)
    {
        var stride = checked(width * 2);
        var payload = new byte[checked(stride * height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = format == CameraPixelFormat.Mono16
                    ? (ushort)((2025L + 257L * (y * (long)width + x)) & 0xFFFF)
                    : (ushort)((64 + 2025 + 31 * x + 17 * y + 997 * ((y & 1) * 2 + (x & 1))) & 0x3FFF);
                var offset = y * stride + x * 2;
                payload[offset] = (byte)value;
                payload[offset + 1] = (byte)(value >> 8);
            }
        }

        var isColor = format == CameraPixelFormat.BayerRggb16;
        var rig = new CameraRigConfig(
            new SensorProfile(
                id == "W1" ? "ASI174MM" : "ASI178MC",
                width,
                height,
                id == "W2" ? 2.4 : 5.86,
                isColor ? SensorColorMode.Color : SensorColorMode.Mono,
                format,
                isColor ? SensorResponseMode.BayerRaw : SensorResponseMode.Monochrome,
                stride,
                SampleByteOrder.LittleEndian,
                isColor ? "asi178mc-native-rggb16-v1" : "native-mono16-v1"),
            new OpticsProfile("EquidistantFisheye", 1.5, 180, 0, LensKind.Fisheye),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), 100, 100),
            ProfileVersion: $"issue-98-{id}-rig-v1");
        return new(
            id,
            $"issue-98-{id}-{Guid.NewGuid():N}",
            $"issue-98-{id}-rig",
            rig,
            CameraRigProfileIdentity.ComputeSha256(rig),
            new FrameLayoutDescriptor(
                width,
                height,
                stride,
                format,
                FrameByteOrder.LittleEndian,
                16,
                16,
                FrameSamplePacking.ByteAligned,
                isColor ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
                isColor ? 64 : 0,
                isColor ? 16383 : ushort.MaxValue,
                payload.LongLength),
            isColor ? "application/x-skymonitor-rggb16" : "application/x-skymonitor-mono16",
            payload,
            Convert.ToHexString(SHA256.HashData(payload)));
    }

    private static ExpectedUpload CreateUpload(Workload workload, long sequence)
    {
        var capturedAt = CaptureStartUtc.AddSeconds(sequence);
        var descriptor = new ReconstructionDescriptor(
            new CaptureIdentityDescriptor(workload.DeviceId, workload.RigId, sequence, Guid.NewGuid()),
            new CaptureTimingDescriptor(
                capturedAt.AddMilliseconds(-10), capturedAt, capturedAt.AddMilliseconds(50),
                capturedAt.AddMilliseconds(60), capturedAt.AddMilliseconds(70)),
            new CaptureControlDescriptor(
                TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50), 100, 100, 10, 10, null, null),
            new CaptureProfileSet(
                new ProfileIdentityDescriptor("rig", workload.Rig.ProfileVersion, workload.RigSha256),
                new ProfileIdentityDescriptor("calibration", "none-v1", new string('A', 64)),
                new ProfileIdentityDescriptor("mask", "none-v1", new string('C', 64)),
                new ProfileIdentityDescriptor(workload.Rig.Sensor.Name, workload.Rig.Sensor.SensorRecipeVersion, new string('B', 64)),
                new ProfileIdentityDescriptor("processing", "raw-ingress-v1", new string('D', 64))),
            workload.Layout,
            new ArtifactDescriptor(
                Guid.NewGuid(),
                FrameArtifactRole.Raw,
                "native-manifest-v2-performance",
                "raw",
                capturedAt.AddMilliseconds(60),
                [],
                RecipeIdentityDescriptor.Create(
                    "capture-raw", "1.0.0", "raw-ingress-v1",
                    JsonSerializer.SerializeToElement(new { normalization = "none" })),
                workload.MediaType,
                workload.ChecksumSha256))
        {
            Location = workload.Location
        };
        var manifest = new ArtifactManifestV2(
            ArtifactManifestV2.CurrentSchemaVersion, descriptor, $"frames/{sequence:D6}.raw");
        Assert.IsTrue(manifest.Validate().IsValid);
        return new(workload, manifest, CaptureContractJson.Serialize(manifest));
    }

    private static async Task SeedWorkloadAsync(IntegrationTestFixture fixture, Workload workload)
    {
        await fixture.SeedActiveDeviceAsync(workload.DeviceId).ConfigureAwait(false);
        await fixture.SeedRigProfileAsync(workload.DeviceId, workload.Rig).ConfigureAwait(false);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var profile = await db.DeviceRigProfiles.SingleAsync(item =>
            item.Registration!.DeviceId == workload.DeviceId
            && item.ProfileSha256 == workload.RigSha256).ConfigureAwait(false);
        workload.RigProfileId = profile.Id;
        workload.DevicePublicId = profile.DevicePublicId;
        var registration = await db.DeviceRegistrations.Include(item => item.Observatory)
            .SingleAsync(item => item.Id == profile.RegistrationId).ConfigureAwait(false);
        var observatory = registration.Observatory!;
        var deployment = DeploymentLocationSnapshot.Create(
            $"issue-170-{workload.Id}",
            1,
            "performance-observatory-fallback",
            null,
            CaptureStartUtc.AddDays(-1),
            null,
            observatory.LatitudeDegrees,
            observatory.LongitudeDegrees,
            observatory.ElevationMeters,
            observatory.TimeZoneId);
        var authorityType = typeof(HVO.SkyMonitor.LogicHost.Program).Assembly.GetType(
            "HVO.SkyMonitor.LogicHost.Services.IDeploymentLocationAuthorityService");
        if (authorityType is not null)
        {
            var locationAuthorityType = typeof(HVO.SkyMonitor.LogicHost.Program).Assembly.GetType(
                "HVO.SkyMonitor.LogicHost.Services.ObservatoryLocationAuthority")
                ?? throw new InvalidOperationException("Observatory location authority is unavailable.");
            var ensureCurrent = locationAuthorityType.GetMethod(
                "EnsureCurrentVersionAsync",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Observatory location authority initialization is unavailable.");
            var authorityInitialization = (Task)(ensureCurrent.Invoke(
                null,
                [
                    db,
                    observatory,
                    CaptureStartUtc.AddDays(-1),
                    "performance-harness",
                    CancellationToken.None
                ]) ?? throw new InvalidOperationException("Observatory authority initialization returned no task."));
            await authorityInitialization.ConfigureAwait(false);
            var propose = authorityType.GetMethod("ProposeAsync")
                ?? throw new InvalidOperationException("Deployment-location authority proposal method is unavailable.");
            var sourceKind = Enum.Parse(propose.GetParameters()[2].ParameterType, "Inherited");
            var task = (Task)(propose.Invoke(
                scope.ServiceProvider.GetRequiredService(authorityType),
                [registration, deployment, sourceKind, "performance-harness", CancellationToken.None])
                ?? throw new InvalidOperationException("Deployment-location authority did not return a task."));
            await task.ConfigureAwait(false);
            var acknowledgment = task.GetType().GetProperty("Result")?.GetValue(task)
                ?? throw new InvalidOperationException("Deployment-location authority omitted its acknowledgment.");
            Assert.AreEqual("Acknowledged", acknowledgment.GetType().GetProperty("Status")?.GetValue(acknowledgment)?.ToString());
            await db.SaveChangesAsync().ConfigureAwait(false);
            workload.Location = deployment.ToProvenance();
        }
    }

    private static object WorkloadMetadata(Workload workload, int warmups, int measurements, int[] concurrency)
        => new
        {
            workload.Id,
            workload.Layout.Width,
            workload.Layout.Height,
            PixelFormat = workload.Layout.PixelFormat.ToString(),
            Cfa = workload.Layout.CfaPattern.ToString(),
            ByteOrder = workload.Layout.ByteOrder.ToString(),
            workload.Layout.StrideBytes,
            PayloadBytes = workload.Payload.LongLength,
            workload.ChecksumSha256,
            IntentionalLocationMode = workload.Location is null ? "location-null" : "location-bound",
            Recipe = "capture-raw/1.0.0/raw-ingress-v1; canonical options {normalization:none}",
            PayloadGenerator = workload.Layout.PixelFormat == CameraPixelFormat.Mono16
                ? "little-endian ushort ((2025 + 257 * linearPixelIndex) & 0xFFFF)"
                : "little-endian ushort ((64 + 2025 + 31*x + 17*y + 997*((y&1)*2+(x&1))) & 0x3FFF)",
            warmups,
            MeasurementsPerConcurrency = measurements,
            Concurrency = concurrency,
            TotalOperations = concurrency.Length * (warmups + measurements),
            TotalMeasuredPayloadBytes = workload.Payload.LongLength * measurements * concurrency.Length
        };

    private static PhaseEvidence CreatePhaseEvidence(
        TimeSpan elapsed,
        double[] sorted,
        HttpAttemptCounter attempts,
        ProtocolSnapshot protocols,
        long payloadBytes)
        => new(
            elapsed.TotalMilliseconds,
            sorted,
            Percentile(sorted, 0.50),
            Percentile(sorted, 0.95),
            Percentile(sorted, 0.99),
            sorted[^1],
            sorted.Length / elapsed.TotalSeconds,
            CreateProtocolEvidence(attempts, protocols, payloadBytes));

    private static ProtocolEvidence CreateProtocolEvidence(
        HttpAttemptCounter attempts,
        ProtocolSnapshot protocols,
        long payloadBytes)
        => new(
            new(
                attempts.MultipartPosts,
                attempts.MultipartRequestBodyBytes,
                attempts.MultipartPayloadBytes,
                attempts.StatusPosts,
                attempts.StatusRequestBodyBytes,
                attempts.ServerErrorRetries,
                attempts.DeadlockRetries,
                attempts.PendingReferenceRetries,
                payloadBytes,
                "Logical payload is counted once per measured operation. Request body bytes are exact HttpContent lengths for every attempt and include multipart framing or the status manifest; TestServer transport headers are excluded."),
            protocols,
            "Unavailable: Microsoft.Data.SqlClient does not expose SQL wire byte counts through EF diagnostics.");

    private static object CreateW4ScalingEvidence(IReadOnlyList<IngestMeasurement> measurements)
    {
        var c1 = measurements.Single(static measurement => measurement.Scenario == "W4-W1-C1");
        var c4 = measurements.Single(static measurement => measurement.Scenario == "W4-W1-C4");
        var c8 = measurements.Single(static measurement => measurement.Scenario == "W4-W1-C8");
        return new
        {
            C1CapturesPerSecond = c1.CapturesPerSecond,
            C4CapturesPerSecond = c4.CapturesPerSecond,
            C8CapturesPerSecond = c8.CapturesPerSecond,
            C4ThroughputChangePercent = (c4.CapturesPerSecond / c1.CapturesPerSecond - 1) * 100,
            ThroughputChangePercent = (c8.CapturesPerSecond / c1.CapturesPerSecond - 1) * 100,
            C1P95Milliseconds = c1.P95Milliseconds,
            C4P95Milliseconds = c4.P95Milliseconds,
            C8P95Milliseconds = c8.P95Milliseconds,
            C4ServerErrorRetries = c4.Protocol.Http.ServerErrorRetries,
            C4PendingReferenceRetries = c4.Protocol.Http.PendingReferenceRetries,
            C8ServerErrorRetries = c8.Protocol.Http.ServerErrorRetries,
            C8PendingReferenceRetries = c8.Protocol.Http.PendingReferenceRetries,
            Interpretation = "C1, C4, and C8 are all retained; reviewed baseline/candidate comparisons come from the five-trial summary rather than this within-trial scaling view."
        };
    }

    private static byte[] CreateCorruptPayload(byte[] payload)
    {
        var corrupt = payload.ToArray();
        corrupt[0] ^= 0xff;
        return corrupt;
    }

    private static double Percentile(double[] sorted, double percentile)
        => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * percentile) - 1)];

    private static void StabilizeGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static void SetAuthorization(HttpClient client, string accessToken)
        => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    private static string GetEvidenceRevision()
    {
        var revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION");
        if (string.IsNullOrWhiteSpace(revision))
        {
            revision = RunGit(GetRepositoryRoot(), "rev-parse", "--short", "HEAD");
        }
        if (revision is "." or ".."
            || revision.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_' and not '.'))
        {
            throw new InvalidOperationException("HVO_EVIDENCE_REVISION must be a single safe path segment.");
        }
        return revision;
    }

    private static GitEvidence ReadGitEvidence(string repositoryRoot)
        => new(
            RunGit(repositoryRoot, "rev-parse", "--short", "HEAD"),
            RunGit(repositoryRoot, "branch", "--show-current"),
            !string.IsNullOrWhiteSpace(RunGit(repositoryRoot, "status", "--porcelain")));

    private static string RunGit(string repositoryRoot, params string[] arguments)
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
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? output.Trim()
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
    }

    private static string ReadPinnedSdkVersion(string repositoryRoot)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "global.json")));
        return document.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            ?? throw new InvalidDataException("global.json does not contain an SDK version.");
    }

    private static string ReadCpuModel()
    {
        const string cpuInfo = "/proc/cpuinfo";
        if (!File.Exists(cpuInfo))
        {
            return "unavailable";
        }
        var model = File.ReadLines(cpuInfo).FirstOrDefault(static line => line.StartsWith("model name", StringComparison.Ordinal));
        return model?.Split(':', 2)[1].Trim() ?? "unavailable";
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

    private sealed class UploadAllocator(Workload w1, Workload w2)
    {
        private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal)
        {
            [w1.Id] = 1,
            [w2.Id] = 1
        };

        public ExpectedUpload[] Create(Workload workload, int count)
            => Enumerable.Range(0, count).Select(_ => Next(workload)).ToArray();

        public ExpectedUpload[] CreateMixed(int count)
            => Enumerable.Range(0, count).Select(index => Next(index % 2 == 0 ? w1 : w2)).ToArray();

        private ExpectedUpload Next(Workload workload)
        {
            var sequence = _sequences[workload.Id];
            _sequences[workload.Id] = sequence + 1;
            return CreateUpload(workload, sequence);
        }
    }

    private sealed class EverySecondCommitFaultInterceptor : DbTransactionInterceptor
    {
        private int _armed;
        private int _commits;
        private int _remaining;
        private long _injectedFailures;

        public long InjectedFailures => Interlocked.Read(ref _injectedFailures);

        public void Arm(int logicalFailures)
        {
            Interlocked.Exchange(ref _commits, 0);
            Interlocked.Exchange(ref _remaining, logicalFailures);
            Volatile.Write(ref _armed, 1);
        }

        public void Disarm() => Volatile.Write(ref _armed, 0);

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1
                && Interlocked.Increment(ref _commits) % 2 == 0
                && Interlocked.Decrement(ref _remaining) >= 0)
            {
                Interlocked.Increment(ref _injectedFailures);
                throw new IOException("issue-98 deterministic SQL transaction commit fault");
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ObjectProtocolFaultHandler : DelegatingHandler
    {
        private int _armed;
        private long _injectedFailures;
        private long _requests;
        private long _get;
        private long _put;
        private long _post;
        private long _delete;
        private long _head;
        private long _requestContentLengthBytes;
        private long _responseContentLengthBytes;
        private long _requestsWithoutContentLength;
        private long _responsesWithoutContentLength;

        public long InjectedFailures => Interlocked.Read(ref _injectedFailures);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Disarm() => Volatile.Write(ref _armed, 0);

        public ObjectBoundarySnapshot Snapshot()
            => new(
                Interlocked.Read(ref _requests),
                Interlocked.Read(ref _get),
                Interlocked.Read(ref _put),
                Interlocked.Read(ref _post),
                Interlocked.Read(ref _delete),
                Interlocked.Read(ref _head),
                Interlocked.Read(ref _requestContentLengthBytes),
                Interlocked.Read(ref _responseContentLengthBytes),
                Interlocked.Read(ref _requestsWithoutContentLength),
                Interlocked.Read(ref _responsesWithoutContentLength),
                Interlocked.Read(ref _injectedFailures));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (request.Content?.Headers.ContentLength is long requestBytes)
            {
                Interlocked.Add(ref _requestContentLengthBytes, requestBytes);
            }
            else
            {
                Interlocked.Increment(ref _requestsWithoutContentLength);
            }
            if (request.Method == HttpMethod.Get) Interlocked.Increment(ref _get);
            else if (request.Method == HttpMethod.Put) Interlocked.Increment(ref _put);
            else if (request.Method == HttpMethod.Post) Interlocked.Increment(ref _post);
            else if (request.Method == HttpMethod.Delete) Interlocked.Increment(ref _delete);
            else if (request.Method == HttpMethod.Head) Interlocked.Increment(ref _head);
            if (Volatile.Read(ref _armed) == 1 && request.Method == HttpMethod.Put)
            {
                Interlocked.Increment(ref _injectedFailures);
                var faultResponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    RequestMessage = request,
                    Content = new StringContent("issue-98 deterministic MinIO PUT fault", Encoding.UTF8, "text/plain")
                };
                ObserveResponse(faultResponse);
                return faultResponse;
            }
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            ObserveResponse(response);
            return response;
        }

        private void ObserveResponse(HttpResponseMessage response)
        {
            if (response.Content.Headers.ContentLength is long responseBytes)
            {
                Interlocked.Add(ref _responseContentLengthBytes, responseBytes);
            }
            else
            {
                Interlocked.Increment(ref _responsesWithoutContentLength);
            }
        }
    }

    private sealed class ProtocolCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private const string CommandExecuted = "Microsoft.EntityFrameworkCore.Database.Command.CommandExecuted";
        private const string TransactionStarted = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionStarted";
        private const string TransactionCommitted = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionCommitted";
        private const string TransactionRolledBack = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionRolledBack";
        private const string TransactionFailed = "Microsoft.EntityFrameworkCore.Database.Transaction.TransactionFailed";
        private const string HttpRequestStart = "System.Net.Http.HttpRequestOut.Start";
        private const string HttpRequestStop = "System.Net.Http.HttpRequestOut.Stop";
        private readonly ConcurrentBag<IDisposable> _subscriptions = [];
        private readonly IDisposable _allListeners;
        private readonly string _minioAuthority;
        private long _sqlCommands;
        private long _transactionsStarted;
        private long _transactionsCommitted;
        private long _transactionsRolledBack;
        private long _transactionsFailed;
        private long _minioRequests;
        private long _minioGet;
        private long _minioPut;
        private long _minioPost;
        private long _minioDelete;
        private long _minioHead;
        private long _minioRequestContentBytes;
        private long _minioResponseContentBytes;
        private long _minioRequestsWithoutContentLength;
        private long _minioResponsesWithoutContentLength;
        private int _active;

        public ProtocolCounter(string minioEndpoint)
        {
            _minioAuthority = minioEndpoint;
            _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public void Start()
        {
            _sqlCommands = 0;
            _transactionsStarted = 0;
            _transactionsCommitted = 0;
            _transactionsRolledBack = 0;
            _transactionsFailed = 0;
            _minioRequests = 0;
            _minioGet = 0;
            _minioPut = 0;
            _minioPost = 0;
            _minioDelete = 0;
            _minioHead = 0;
            _minioRequestContentBytes = 0;
            _minioResponseContentBytes = 0;
            _minioRequestsWithoutContentLength = 0;
            _minioResponsesWithoutContentLength = 0;
            Volatile.Write(ref _active, 1);
        }

        public ProtocolSnapshot Stop()
        {
            Volatile.Write(ref _active, 0);
            return new(
                Interlocked.Read(ref _sqlCommands),
                Interlocked.Read(ref _transactionsStarted),
                Interlocked.Read(ref _transactionsCommitted),
                Interlocked.Read(ref _transactionsRolledBack),
                Interlocked.Read(ref _transactionsFailed),
                Interlocked.Read(ref _minioRequests),
                Interlocked.Read(ref _minioGet),
                Interlocked.Read(ref _minioPut),
                Interlocked.Read(ref _minioPost),
                Interlocked.Read(ref _minioDelete),
                Interlocked.Read(ref _minioHead),
                Interlocked.Read(ref _minioRequestContentBytes),
                Interlocked.Read(ref _minioResponseContentBytes),
                Interlocked.Read(ref _minioRequestsWithoutContentLength),
                Interlocked.Read(ref _minioResponsesWithoutContentLength));
        }

        public void OnNext(DiagnosticListener listener)
        {
            if (string.Equals(listener.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            {
                _subscriptions.Add(listener.Subscribe(this, static eventName => eventName is
                    CommandExecuted or TransactionStarted or TransactionCommitted or TransactionRolledBack or TransactionFailed));
            }
            else if (string.Equals(listener.Name, "HttpHandlerDiagnosticListener", StringComparison.Ordinal))
            {
                _subscriptions.Add(listener.Subscribe(this, static eventName => eventName is HttpRequestStart or HttpRequestStop));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (Volatile.Read(ref _active) != 1)
            {
                return;
            }
            switch (value.Key)
            {
                case CommandExecuted:
                    Interlocked.Increment(ref _sqlCommands);
                    return;
                case TransactionStarted:
                    Interlocked.Increment(ref _transactionsStarted);
                    return;
                case TransactionCommitted:
                    Interlocked.Increment(ref _transactionsCommitted);
                    return;
                case TransactionRolledBack:
                    Interlocked.Increment(ref _transactionsRolledBack);
                    return;
                case TransactionFailed:
                    Interlocked.Increment(ref _transactionsFailed);
                    return;
            }

            var payloadType = value.Value?.GetType();
            var request = payloadType?.GetProperty("Request")?.GetValue(value.Value) as HttpRequestMessage;
            if (request is null || !string.Equals(request.RequestUri?.Authority, _minioAuthority, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (value.Key == HttpRequestStart)
            {
                Interlocked.Increment(ref _minioRequests);
                if (request.Content?.Headers.ContentLength is long requestBytes)
                {
                    Interlocked.Add(ref _minioRequestContentBytes, requestBytes);
                }
                else
                {
                    Interlocked.Increment(ref _minioRequestsWithoutContentLength);
                }
                if (request.Method == HttpMethod.Get) Interlocked.Increment(ref _minioGet);
                else if (request.Method == HttpMethod.Put) Interlocked.Increment(ref _minioPut);
                else if (request.Method == HttpMethod.Post) Interlocked.Increment(ref _minioPost);
                else if (request.Method == HttpMethod.Delete) Interlocked.Increment(ref _minioDelete);
                else if (request.Method == HttpMethod.Head) Interlocked.Increment(ref _minioHead);
                return;
            }
            if (value.Key == HttpRequestStop)
            {
                var response = payloadType?.GetProperty("Response")?.GetValue(value.Value) as HttpResponseMessage;
                if (response?.Content.Headers.ContentLength is long responseBytes)
                {
                    Interlocked.Add(ref _minioResponseContentBytes, responseBytes);
                }
                else
                {
                    Interlocked.Increment(ref _minioResponsesWithoutContentLength);
                }
            }
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void Dispose()
        {
            _allListeners.Dispose();
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }
        }
    }

    private sealed class HttpAttemptCounter
    {
        private long _multipartPosts;
        private long _multipartRequestBodyBytes;
        private long _multipartPayloadBytes;
        private long _statusPosts;
        private long _statusRequestBodyBytes;
        private long _serverErrorRetries;
        private long _deadlockRetries;
        private long _pendingReferenceRetries;

        public long MultipartPosts => Interlocked.Read(ref _multipartPosts);
        public long MultipartRequestBodyBytes => Interlocked.Read(ref _multipartRequestBodyBytes);
        public long MultipartPayloadBytes => Interlocked.Read(ref _multipartPayloadBytes);
        public long StatusPosts => Interlocked.Read(ref _statusPosts);
        public long StatusRequestBodyBytes => Interlocked.Read(ref _statusRequestBodyBytes);
        public long ServerErrorRetries => Interlocked.Read(ref _serverErrorRetries);
        public long DeadlockRetries => Interlocked.Read(ref _deadlockRetries);
        public long PendingReferenceRetries => Interlocked.Read(ref _pendingReferenceRetries);
        public void RecordMultipart(long requestBodyBytes, long payloadBytes)
        {
            Interlocked.Increment(ref _multipartPosts);
            Interlocked.Add(ref _multipartRequestBodyBytes, requestBodyBytes);
            Interlocked.Add(ref _multipartPayloadBytes, payloadBytes);
        }
        public void RecordStatus(long requestBodyBytes)
        {
            Interlocked.Increment(ref _statusPosts);
            Interlocked.Add(ref _statusRequestBodyBytes, requestBodyBytes);
        }
        public void RecordServerErrorRetry() => Interlocked.Increment(ref _serverErrorRetries);
        public void RecordDeadlockRetry() => Interlocked.Increment(ref _deadlockRetries);
        public void RecordPendingReferenceRetry() => Interlocked.Increment(ref _pendingReferenceRetries);
    }

    private sealed class ResourceSampler : IDisposable
    {
        private readonly Process _process = Process.GetCurrentProcess();
        private readonly TimeSpan _cpuStart;
        private readonly long _runtimeAllocatedStart;
        private readonly RssSampler _rss;
        private bool _stopped;

        public ResourceSampler()
        {
            _process.Refresh();
            _cpuStart = _process.TotalProcessorTime;
            _runtimeAllocatedStart = GC.GetTotalAllocatedBytes(precise: true);
            _rss = new RssSampler(_process.WorkingSet64);
        }

        public async Task<ResourceEvidence> StopAsync()
        {
            _stopped = true;
            _process.Refresh();
            var cpuMilliseconds = (_process.TotalProcessorTime - _cpuStart).TotalMilliseconds;
            var runtimeAllocatedEnd = GC.GetTotalAllocatedBytes(precise: true);
            var runtimeAllocatedDelta = runtimeAllocatedEnd - _runtimeAllocatedStart;
            var rssEnd = _process.WorkingSet64;
            var peak = Math.Max(
                Math.Max(_rss.Initial, rssEnd),
                await _rss.StopAsync().ConfigureAwait(false));
            return new(
                cpuMilliseconds,
                runtimeAllocatedDelta,
                _runtimeAllocatedStart,
                runtimeAllocatedEnd,
                runtimeAllocatedDelta,
                "Monotonic process-wide GC.GetTotalAllocatedBytes(true) delta for the measured phase.",
                _rss.Initial,
                peak,
                rssEnd);
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                _rss.StopAsync().GetAwaiter().GetResult();
            }
            _rss.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _process.Dispose();
        }
    }

    private sealed class RssSampler : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _sampling;
        private long _peak;
        private bool _stopped;

        public RssSampler(long initial)
        {
            Initial = initial;
            _peak = initial;
            _sampling = SampleAsync(_stopping.Token);
        }

        public long Initial { get; }

        public async Task<long> StopAsync()
        {
            if (!_stopped)
            {
                _stopped = true;
                _stopping.Cancel();
                await _sampling.ConfigureAwait(false);
                Observe();
            }
            return Interlocked.Read(ref _peak);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            _stopping.Dispose();
        }

        private async Task SampleAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    Observe();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        private void Observe()
        {
            using var process = Process.GetCurrentProcess();
            var observed = process.WorkingSet64;
            long current;
            do
            {
                current = Interlocked.Read(ref _peak);
                if (observed <= current) return;
            }
            while (Interlocked.CompareExchange(ref _peak, observed, current) != current);
        }
    }

    private sealed record GitEvidence(string Commit, string Branch, bool Dirty);
    private sealed record UploadResponse(
        HttpStatusCode StatusCode,
        string Body,
        double ElapsedMilliseconds,
        long RequestBodyBytes);
    private sealed record ExpectedUpload(Workload Workload, ArtifactManifestV2 Manifest, byte[] ManifestJson);
    private sealed record Workload(
        string Id,
        string DeviceId,
        string RigId,
        CameraRigConfig Rig,
        string RigSha256,
        FrameLayoutDescriptor Layout,
        string MediaType,
        byte[] Payload,
        string ChecksumSha256)
    {
        public Guid RigProfileId { get; set; }
        public Guid DevicePublicId { get; set; }
        public CaptureLocationProvenance? Location { get; set; }
    }

    private sealed record BacklogSnapshot(
        long Count,
        long Bytes,
        double OldestAgeMilliseconds,
        long PendingObjectCount,
        long QuarantinedObjectCount,
        long PendingReferenceCount,
        long QuarantinedReconstructionCount);
    private sealed record ResourceEvidence(
        double CpuMilliseconds,
        long? AllocatedBytes,
        long RuntimeAllocationCounterStartBytes,
        long RuntimeAllocationCounterEndBytes,
        long? RuntimeAllocationCounterDeltaBytes,
        string AllocationInterpretation,
        long RssStartBytes,
        long RssPeakBytes,
        long RssEndBytes);
    private sealed record HttpEvidence(
        long MultipartPosts,
        long MultipartRequestBodyBytes,
        long MultipartPayloadBytes,
        long StatusPosts,
        long StatusRequestBodyBytes,
        long ServerErrorRetries,
        long DeadlockRetriesIdentifiedFromResponse,
        long PendingReferenceRetries,
        long LogicalPayloadBytes,
        string ByteScope);
    private sealed record ProtocolSnapshot(
        long SqlCommandsObserved,
        long SqlTransactionsStartedObserved,
        long SqlTransactionsCommittedObserved,
        long SqlTransactionsRolledBackObserved,
        long SqlTransactionsFailedObserved,
        long MinioRequestsObserved,
        long MinioGetObserved,
        long MinioPutObserved,
        long MinioPostObserved,
        long MinioDeleteObserved,
        long MinioHeadObserved,
        long MinioRequestContentLengthBytesObserved,
        long MinioResponseContentLengthBytesObserved,
        long MinioRequestsWithoutContentLength,
        long MinioResponsesWithoutContentLength);
    private sealed record ObjectBoundarySnapshot(
        long RequestsObserved,
        long GetObserved,
        long PutObserved,
        long PostObserved,
        long DeleteObserved,
        long HeadObserved,
        long RequestContentLengthBytesObserved,
        long ResponseContentLengthBytesObserved,
        long RequestsWithoutContentLength,
        long ResponsesWithoutContentLength,
        long InjectedFailures);
    private sealed record ProtocolEvidence(HttpEvidence Http, ProtocolSnapshot Observed, string SqlWireBytes);
    private sealed record PhaseEvidence(
        double ElapsedMilliseconds,
        IReadOnlyList<double> LatencySamplesMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds,
        double OperationsPerSecond,
        ProtocolEvidence Protocol);
    private sealed record IngestMeasurement(
        string Scenario,
        string[] PayloadWorkloads,
        int Concurrency,
        int WarmupOperations,
        int MeasuredOperations,
        double[] LatencySamplesMilliseconds,
        long PayloadBytes,
        double ElapsedMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        double P99Milliseconds,
        double MaximumMilliseconds,
        double CapturesPerSecond,
        double BytesPerSecond,
        ResourceEvidence Resources,
        BacklogSnapshot InitialBacklog,
        BacklogSnapshot FinalBacklog,
        ProtocolEvidence Protocol,
        string BacklogInterpretation);
    private sealed record FaultRecoveryMeasurement(
        string Scenario,
        string[] PayloadWorkloads,
        int WarmupTransitions,
        int MeasuredTransitions,
        double TotalTransitionMilliseconds,
        double ConvergencesPerSecond,
        ResourceEvidence Resources,
        BacklogSnapshot InitialBacklog,
        BacklogSnapshot FaultBacklog,
        BacklogSnapshot RecoveredBacklog,
        PhaseEvidence Fault,
        PhaseEvidence Recovery,
        string StateInvariant);
    private sealed record RestartRecoveryMeasurement(
        int WarmupPendingIntents,
        int MeasuredPendingIntents,
        BacklogSnapshot InitialBacklog,
        BacklogSnapshot FaultBacklog,
        BacklogSnapshot FinalBacklog,
        double RecoveryLatencyMilliseconds,
        double ConvergencesPerSecond,
        double PayloadBytesPerSecond,
        ResourceEvidence Resources,
        ProtocolSnapshot ObservedRecoveryProtocol,
        ObjectBoundarySnapshot ObservedRecoveryObjectBoundary,
        string StateInvariant);
    private sealed record CorrectnessEvidence(
        int AcknowledgementsValidated,
        int SqlArtifacts,
        int SqlFrames,
        int SqlTimings,
        int SqlControls,
        int SqlProfiles,
        int SqlLayouts,
        int SqlRecipes,
        int SqlSources,
        int SqlIngestIdentities,
        int SqlDerivativeJobs,
        int MinioObjectsValidated,
        long MinioBytesValidated,
        int MinioHeadOperations,
        int MinioGetOperations,
        long MinioGetBytes,
        string W1PayloadSha256,
        string W2PayloadSha256,
        string Checks);
}
