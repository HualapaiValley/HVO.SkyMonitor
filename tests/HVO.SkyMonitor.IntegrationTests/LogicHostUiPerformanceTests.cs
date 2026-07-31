using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class LogicHostUiPerformanceTests
{
    private const int WarmupOperations = 20;
    private const int MeasuredOperations = 200;
    private static readonly int[] ConcurrencyLevels = [1, 10, 50];
    private static readonly string[] NetNewBaselineMetrics =
    [
        "public/protected route latency, CPU, allocations, response bytes and session RSS",
        "authority and archive SQL commands, projected rows, plans and logical reads",
        "public-preview transfer throughput",
        "W2 full/range/conditional/cancellation behavior"
    ];
    private static readonly JsonSerializerOptions EvidenceJsonOptions = new() { WriteIndented = true };

    [TestMethod]
    [Timeout(1_800_000)]
    public async Task Issue107_RouteMixesMeetDeclaredConcurrentSessionLatencyAndResourceGates()
    {
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync(enableArtifactResponseThrottle: true)
            .ConfigureAwait(false);
        var dataset = await Issue107PerformanceDatasetSeeder.SeedAsync(fixture.Services).ConfigureAwait(false);
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_107_DATASET_ONLY"), "1", StringComparison.Ordinal))
        {
            return;
        }
        var sqlEvidence = await Issue107SqlPerformanceProbe.MeasureAsync(fixture.Services, dataset).ConfigureAwait(false);
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_107_SQL_ONLY"), "1", StringComparison.Ordinal))
        {
            return;
        }
        if (string.Equals(Environment.GetEnvironmentVariable("HVO_ISSUE_107_BOUNDARIES_ONLY"), "1", StringComparison.Ordinal))
        {
            foreach (var concurrency in ConcurrencyLevels)
            {
                _ = await MeasurePublicPreviewTransfersAsync(fixture.BaseAddress, dataset, concurrency)
                    .ConfigureAwait(false);
            }
            _ = await MeasureRetrievalBoundariesAsync(fixture.BaseAddress, dataset, fixture.Services)
                .ConfigureAwait(false);
            return;
        }
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Args = ["--disable-dev-shm-usage"]
        }).ConfigureAwait(false);
        var authenticatedStorageState = await CreateAuthenticatedStorageStateAsync(browser, fixture.BaseAddress)
            .ConfigureAwait(false);
        var results = new List<RouteMixEvidence>();
        var transferResults = new List<TransferEvidence>();
        var processSamples = new List<ProcessSample>();
        var resourceResults = new List<SessionResourceEvidence>();
        var concurrencyFilter = int.TryParse(
            Environment.GetEnvironmentVariable("HVO_ISSUE_107_CONCURRENCY"),
            CultureInfo.InvariantCulture,
            out var requestedConcurrency)
                ? requestedConcurrency
                : (int?)null;
        var mixFilter = Environment.GetEnvironmentVariable("HVO_ISSUE_107_MIX");
        foreach (var concurrency in ConcurrencyLevels.Where(level => concurrencyFilter is null || level == concurrencyFilter))
        {
            var sessions = new List<(IBrowserContext Context, IPage Page)>(concurrency);
            var interactiveSessions = new List<(IBrowserContext Context, IPage Page)>(concurrency);
            try
            {
                foreach (var mix in CreateRouteMixes(dataset).Where(mix =>
                             string.IsNullOrWhiteSpace(mixFilter)
                             || string.Equals(mix.Name, mixFilter, StringComparison.Ordinal)))
                {
                    for (var index = 0; index < concurrency; index++)
                    {
                        var context = await browser.NewContextAsync(new BrowserNewContextOptions
                        {
                            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                            ServiceWorkers = ServiceWorkerPolicy.Block,
                            StorageState = mix.IsPublic
                                ? null
                                : authenticatedStorageState
                        }).ConfigureAwait(false);
                        await context.RouteAsync("**/_framework/blazor.web*.js", route => route.AbortAsync())
                            .ConfigureAwait(false);
                        await context.RouteAsync("**/api/v1.0/public/artifacts/*/content", route => route.AbortAsync())
                            .ConfigureAwait(false);
                        var page = await context.NewPageAsync().ConfigureAwait(false);
                        sessions.Add((context, page));
                    }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    await RunOperationsAsync(
                        fixture.BaseAddress,
                        sessions,
                        mix.Routes,
                        WarmupOperations,
                        record: false,
                        null,
                        null,
                        timeout.Token).ConfigureAwait(false);
                    var latencies = new ConcurrentBag<double>();
                    var responseBytes = new ConcurrentBag<long>();
                    var process = Process.GetCurrentProcess();
                    var cpuBefore = process.TotalProcessorTime;
                    var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
                    var started = Stopwatch.GetTimestamp();
                    await RunOperationsAsync(
                        fixture.BaseAddress,
                        sessions,
                        mix.Routes,
                        MeasuredOperations,
                        record: true,
                        latencies,
                        responseBytes,
                        timeout.Token).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(started);
                    var ordered = latencies.Order().ToArray();
                    var p95 = Percentile(ordered, 0.95);
                    var maximum = ordered[^1];
                    var result = new RouteMixEvidence(
                        mix.Name,
                        mix.HistoryRows,
                        concurrency,
                        ordered.Length,
                        Percentile(ordered, 0.50),
                        p95,
                        maximum,
                        responseBytes.Max(),
                        elapsed.TotalSeconds,
                        ordered.Length / elapsed.TotalSeconds,
                        (process.TotalProcessorTime - cpuBefore).TotalMilliseconds / ordered.Length,
                        (GC.GetTotalAllocatedBytes(precise: true) - allocationsBefore) / ordered.Length);
                    results.Add(result);
                    result.ManagedAllocatedBytesPerRequest.Should().BeGreaterThan(0,
                        $"{mix.Name} at {concurrency} sessions must retain credible allocation evidence");
                    await WriteCheckpointAsync(dataset, results).ConfigureAwait(false);
                    p95.Should().BeLessThanOrEqualTo(5_000, $"{mix.Name} at {concurrency} sessions");
                    maximum.Should().BeLessThanOrEqualTo(15_000, $"{mix.Name} at {concurrency} sessions");
                    responseBytes.Should().OnlyContain(bytes => bytes <= 512 * 1024,
                        $"{mix.Name} at {concurrency} sessions");
                    foreach (var session in sessions)
                    {
                        await session.Context.DisposeAsync().ConfigureAwait(false);
                    }
                    sessions.Clear();
                }
                transferResults.Add(await MeasurePublicPreviewTransfersAsync(
                    fixture.BaseAddress, dataset, concurrency).ConfigureAwait(false));
                var workingSetBeforeInteractiveSessions = Process.GetCurrentProcess().WorkingSet64;
                for (var index = 0; index < concurrency; index++)
                {
                    var context = await browser.NewContextAsync(new BrowserNewContextOptions
                    {
                        ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
                        ServiceWorkers = ServiceWorkerPolicy.Block,
                        StorageState = authenticatedStorageState
                    }).ConfigureAwait(false);
                    var page = await context.NewPageAsync().ConfigureAwait(false);
                    await page.GotoAsync(new Uri(fixture.BaseAddress, "/app").AbsoluteUri).ConfigureAwait(false);
                    await page.GetByRole(AriaRole.Heading, new() { Name = "Accessible observatories" }).WaitForAsync()
                        .ConfigureAwait(false);
                    interactiveSessions.Add((context, page));
                }
                await Task.Delay(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
                for (var second = 0; second < 60; second++)
                {
                    var process = Process.GetCurrentProcess();
                    var memory = GC.GetGCMemoryInfo();
                    processSamples.Add(new(
                        concurrency,
                        second,
                        process.TotalProcessorTime.TotalMilliseconds,
                        process.WorkingSet64,
                        process.PrivateMemorySize64,
                        GC.GetTotalAllocatedBytes(precise: true),
                        memory.HeapSizeBytes,
                        interactiveSessions.Count));
                    await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                if (concurrency == 50)
                {
                    (processSamples.Where(sample => sample.IndependentSessions == concurrency)
                        .Max(sample => sample.WorkingSetBytes) - workingSetBeforeInteractiveSessions)
                        .Should().BeLessThanOrEqualTo(256L * 1024 * 1024);
                }
                var maximumWorkingSet = processSamples.Where(sample => sample.IndependentSessions == concurrency)
                    .Max(sample => sample.WorkingSetBytes);
                resourceResults.Add(new(
                    concurrency,
                    workingSetBeforeInteractiveSessions,
                    maximumWorkingSet,
                    maximumWorkingSet - workingSetBeforeInteractiveSessions));
            }
            finally
            {
                foreach (var session in sessions)
                {
                    await session.Context.DisposeAsync().ConfigureAwait(false);
                }
                foreach (var session in interactiveSessions)
                {
                    await session.Context.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        if (concurrencyFilter is null)
        {
            var ten = resourceResults.Single(item => item.IndependentSessions == 10);
            var fifty = resourceResults.Single(item => item.IndependentSessions == 50);
            ((fifty.WorkingSetGrowthBytes - ten.WorkingSetGrowthBytes) / 40d)
                .Should().BeLessThanOrEqualTo(4d * 1024 * 1024);
        }
        var retrieval = await MeasureRetrievalBoundariesAsync(fixture.BaseAddress, dataset, fixture.Services)
            .ConfigureAwait(false);
        await WriteEvidenceAsync(
                browser.Version, dataset, results, transferResults, retrieval, sqlEvidence, resourceResults, processSamples)
            .ConfigureAwait(false);
    }

    private static async Task RunOperationsAsync(
        Uri baseAddress,
        List<(IBrowserContext Context, IPage Page)> sessions,
        IReadOnlyList<string> routes,
        int operationCount,
        bool record,
        ConcurrentBag<double>? latencies,
        ConcurrentBag<long>? responseBytes,
        CancellationToken cancellationToken)
    {
        await Task.WhenAll(sessions.Select((session, worker) => Task.Run(async () =>
        {
            for (var operation = worker; operation < operationCount; operation += sessions.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var started = Stopwatch.GetTimestamp();
                var response = await session.Page.GotoAsync(
                    new Uri(baseAddress, routes[operation % routes.Count]).AbsoluteUri,
                    new PageGotoOptions
                    {
                        WaitUntil = WaitUntilState.DOMContentLoaded,
                        Timeout = 30_000
                    }).ConfigureAwait(false);
                response.Should().NotBeNull();
                response!.Status.Should().Be((int)HttpStatusCode.OK);
                if (!record) continue;
                latencies!.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                responseBytes!.Add((await response.BodyAsync().ConfigureAwait(false)).LongLength);
            }
        }))).ConfigureAwait(false);
    }

    private static RouteMix[] CreateRouteMixes(Issue107PerformanceDataset dataset) =>
    [
        new("1k-public", 1_000, true,
            ["/", "/observatories", "/observatories/i107-observatory-00", "/events"]),
        new("1k-archive", 1_000, false,
            [$"/app/captures?observatoryId={dataset.ThousandObservatoryId:D}",
             $"/app/captures/{dataset.ThousandCaptureId:D}"]),
        new("1k-operations", 1_000, false,
            ["/app", $"/app/observatories/{dataset.ThousandObservatoryId:D}", "/app/processing"]),
        new("1k-events", 1_000, false, ["/app/events"]),
        new("1k-authority", 1_000, false,
            [$"/app/observatories/{dataset.ThousandObservatoryId:D}/members",
             $"/app/observatories/{dataset.ThousandObservatoryId:D}/publication"]),
        new("10k-public", 10_000, true,
            ["/", "/observatories", "/observatories/i107-observatory-01", "/events"]),
        new("10k-archive", 10_000, false,
            [$"/app/captures?observatoryId={dataset.TenThousandObservatoryId:D}",
             $"/app/captures/{dataset.TenThousandCaptureId:D}"]),
        new("10k-operations", 10_000, false,
            ["/app", $"/app/observatories/{dataset.TenThousandObservatoryId:D}", "/app/processing"]),
        new("10k-events", 10_000, false, ["/app/events"]),
        new("10k-authority", 10_000, false,
            [$"/app/observatories/{dataset.TenThousandObservatoryId:D}/members",
             $"/app/observatories/{dataset.TenThousandObservatoryId:D}/publication"])
    ];

    private static async Task<string> CreateAuthenticatedStorageStateAsync(IBrowser browser, Uri baseAddress)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ServiceWorkers = ServiceWorkerPolicy.Block
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync(new Uri(baseAddress, "/app").AbsoluteUri).ConfigureAwait(false);
        await page.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(TestUsers.Operator.Email).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(TestUsers.Operator.Password).ConfigureAwait(false);
        var navigation = page.WaitForURLAsync(
            url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase),
            new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 15_000 });
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync()
            .ConfigureAwait(false);
        await navigation.ConfigureAwait(false);
        return await context.StorageStateAsync().ConfigureAwait(false);
    }

    private static double Percentile(double[] ordered, double percentile)
        => ordered[Math.Min(ordered.Length - 1, (int)Math.Ceiling(ordered.Length * percentile) - 1)];

    private static async Task<TransferEvidence> MeasurePublicPreviewTransfersAsync(
        Uri baseAddress,
        Issue107PerformanceDataset dataset,
        int concurrency)
    {
        using var client = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        var path = new Uri($"/api/v1.0/public/artifacts/{dataset.PrimaryReleasedPreviewPublicId:D}/content", UriKind.Relative);
        await RunPreviewTransfersAsync(client, path, dataset.PreviewChecksumSha256, WarmupOperations, concurrency, null)
            .ConfigureAwait(false);
        var latencies = new ConcurrentBag<double>();
        var started = Stopwatch.GetTimestamp();
        await RunPreviewTransfersAsync(
            client, path, dataset.PreviewChecksumSha256, MeasuredOperations, concurrency, latencies)
            .ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var ordered = latencies.Order().ToArray();
        var p95 = Percentile(ordered, 0.95);
        p95.Should().BeLessThanOrEqualTo(5_000, $"public preview transfer at {concurrency} clients");
        return new(concurrency, ordered.Length, Percentile(ordered, 0.5), p95, ordered[^1],
            ordered.Length / elapsed.TotalSeconds);
    }

    private static async Task RunPreviewTransfersAsync(
        HttpClient client,
        Uri path,
        string expectedChecksum,
        int count,
        int concurrency,
        ConcurrentBag<double>? latencies)
    {
        for (var offset = 0; offset < count; offset += concurrency)
        {
            await Task.WhenAll(Enumerable.Range(offset, Math.Min(concurrency, count - offset)).Select(async _ =>
            {
                var started = Stopwatch.GetTimestamp();
                var bytes = await client.GetByteArrayAsync(path).ConfigureAwait(false);
                Convert.ToHexString(SHA256.HashData(bytes)).Should().Be(expectedChecksum);
                latencies?.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            })).ConfigureAwait(false);
        }
    }

    private static async Task<RetrievalBoundaryEvidence> MeasureRetrievalBoundariesAsync(
        Uri baseAddress,
        Issue107PerformanceDataset dataset,
        IServiceProvider services)
    {
        using var publicClient = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(15) };
        var publicPath = new Uri(
            $"/api/v1.0/public/artifacts/{dataset.PrimaryReleasedPreviewPublicId:D}/content", UriKind.Relative);
        using var initial = await publicClient.GetAsync(publicPath).ConfigureAwait(false);
        initial.EnsureSuccessStatusCode();
        var etag = initial.Headers.ETag ?? throw new InvalidOperationException("Public preview did not return an ETag.");
        for (var index = 0; index < 20; index++)
        {
            await AssertNotModifiedAsync(publicClient, publicPath, etag).ConfigureAwait(false);
        }
        await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            AssertNotModifiedAsync(publicClient, publicPath, etag))).ConfigureAwait(false);

        using var retrievalMetrics = new RetrievalMeterCapture();
        using var ownerClient = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        var token = await HttpHelpers.GetPasswordTokenAsync(
            ownerClient,
            "/connect/token",
            TestUsers.Operator.Username,
            TestUsers.Operator.Password,
            TestClients.WebUI.ClientId,
            string.Join(' ', TestClients.WebUI.Scopes)).ConfigureAwait(false);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        var rawPath = new Uri(
            $"/api/v1.0/devices/{dataset.W2DevicePublicId:D}/artifacts/{dataset.W2ArtifactId:D}/content",
            UriKind.Relative);
        await AuthorizeRawDownloadAsync(ownerClient, rawPath, null).ConfigureAwait(false);
        var fullStarted = Stopwatch.GetTimestamp();
        var fullBytes = await ownerClient.GetByteArrayAsync(rawPath).ConfigureAwait(false);
        var fullMilliseconds = Stopwatch.GetElapsedTime(fullStarted).TotalMilliseconds;
        fullBytes.LongLength.Should().Be(12_879_360);
        Convert.ToHexString(SHA256.HashData(fullBytes)).Should().Be(dataset.W2ChecksumSha256);

        const string rangeHeader = "bytes=1048576-2097151";
        await AuthorizeRawDownloadAsync(ownerClient, rawPath, rangeHeader).ConfigureAwait(false);
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, rawPath);
        rangeRequest.Headers.Range = new RangeHeaderValue(1_048_576, 2_097_151);
        var rangeStarted = Stopwatch.GetTimestamp();
        using var rangeResponse = await ownerClient.SendAsync(rangeRequest).ConfigureAwait(false);
        var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var rangeMilliseconds = Stopwatch.GetElapsedTime(rangeStarted).TotalMilliseconds;
        rangeResponse.StatusCode.Should().Be(HttpStatusCode.PartialContent);
        rangeBytes.LongLength.Should().Be(1_048_576);
        rangeBytes.AsSpan().SequenceEqual(fullBytes.AsSpan(1_048_576, rangeBytes.Length)).Should().BeTrue();

        await AuthorizeRawDownloadAsync(ownerClient, rawPath, null).ConfigureAwait(false);
        var readsBeforePreCancellation = retrievalMetrics.TotalReads;
        var preCancelled = 0;
        for (var index = 0; index < 30; index++)
        {
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                _ = await ownerClient.GetAsync(rawPath, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                preCancelled++;
            }
        }
        preCancelled.Should().Be(30);
        retrievalMetrics.TotalReads.Should().Be(readsBeforePreCancellation,
            "pre-cancelled requests must not reach object storage");
        var aborted = 0;
        var maximumServerCancellationSettlementMilliseconds = 0d;
        for (var index = 0; index < 30; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, rawPath);
            request.Headers.Add(Issue107ArtifactResponseThrottleStartupFilter.HeaderName, "true");
            var response = await ownerClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
                .ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var firstByte = new byte[1];
            (await stream.ReadAsync(firstByte).ConfigureAwait(false)).Should().Be(1);
            var expectedServerCancellations = retrievalMetrics.SumReads("serve", "cancelled") + 1;
            using var cancellation = new CancellationTokenSource();
            var copy = stream.CopyToAsync(Stream.Null, cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
            var settlementStarted = Stopwatch.GetTimestamp();
            await cancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await copy.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                aborted++;
            }
            await stream.DisposeAsync().ConfigureAwait(false);
            response.Dispose();
            await retrievalMetrics.WaitForReadsAsync(
                "serve", "cancelled", expectedServerCancellations, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            maximumServerCancellationSettlementMilliseconds = Math.Max(
                maximumServerCancellationSettlementMilliseconds,
                Stopwatch.GetElapsedTime(settlementStarted).TotalMilliseconds);
        }
        aborted.Should().Be(30);
        maximumServerCancellationSettlementMilliseconds.Should().BeLessThanOrEqualTo(2_000);
        var telemetry = services.GetRequiredService<CentralArtifactRetrievalTelemetry>();
        var activeStreamSettlementStarted = Stopwatch.GetTimestamp();
        var streamDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (telemetry.ActiveStreams != 0 && DateTimeOffset.UtcNow < streamDeadline)
        {
            await Task.Delay(25).ConfigureAwait(false);
        }
        telemetry.ActiveStreams.Should().Be(0);
        var activeStreamSettlementMilliseconds = Stopwatch.GetElapsedTime(activeStreamSettlementStarted).TotalMilliseconds;
        var objectIo = new RetrievalObjectIoEvidence(
            retrievalMetrics.SumReads("verify", "completed"),
            retrievalMetrics.SumBytes("verify", "completed"),
            retrievalMetrics.SumReads("serve", "completed"),
            retrievalMetrics.SumBytes("serve", "completed"),
            retrievalMetrics.SumReads("serve", "cancelled"),
            retrievalMetrics.TotalReads
                - retrievalMetrics.SumReads("verify", "completed")
                - retrievalMetrics.SumReads("serve", "completed")
                - retrievalMetrics.SumReads("serve", "cancelled"));
        objectIo.VerifyCompletedOperations.Should().Be(32);
        objectIo.VerifyCompletedBytes.Should().Be(412_139_520);
        objectIo.ServeCompletedOperations.Should().Be(2);
        objectIo.ServeCompletedBytes.Should().Be(14_976_512);
        objectIo.ServeCancelledOperations.Should().Be(30);
        objectIo.UnexpectedOperations.Should().Be(0);
        return new(
            70,
            fullBytes.LongLength,
            fullMilliseconds,
            rangeBytes.LongLength,
            rangeMilliseconds,
            preCancelled,
            aborted,
            maximumServerCancellationSettlementMilliseconds,
            activeStreamSettlementMilliseconds,
            telemetry.ActiveStreams,
            objectIo,
            true);
    }

    private static async Task AuthorizeRawDownloadAsync(HttpClient client, Uri contentPath, string? range)
    {
        var authorizationPath = new Uri(
            contentPath.OriginalString.Replace("/content", "/download-authorizations", StringComparison.Ordinal),
            UriKind.Relative);
        using var response = await client.PostAsJsonAsync(authorizationPath, new { range }).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private static async Task AssertNotModifiedAsync(HttpClient client, Uri path, EntityTagHeaderValue etag)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.IfNoneMatch.Add(etag);
        using var response = await client.SendAsync(request).ConfigureAwait(false);
        response.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false)).Should().BeEmpty();
    }

    private static async Task WriteEvidenceAsync(
        string browserVersion,
        Issue107PerformanceDataset dataset,
        IReadOnlyList<RouteMixEvidence> results,
        IReadOnlyList<TransferEvidence> transferResults,
        RetrievalBoundaryEvidence retrieval,
        SqlPerformanceEvidence sqlEvidence,
        IReadOnlyList<SessionResourceEvidence> resourceResults,
        IReadOnlyList<ProcessSample> processSamples)
    {
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE_107_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        var environmentEvidence = CreateEnvironmentEvidence(browserVersion);
        var evidence = new
        {
            Schema = "hvo-logichost-ui-107-performance-v2",
            Workload = Issue107PerformanceDatasetSeeder.Workload,
            CanonicalDatasetComplete = true,
            DatasetSha256 = dataset.DatasetSha256,
            Trial = int.TryParse(Environment.GetEnvironmentVariable("HVO_ISSUE_107_TRIAL"), out var trial)
                ? trial
                : (int?)null,
            Dataset = new
            {
                Observatories = 10,
                LogicalCameras = 20,
                Installations = 40,
                Memberships = 40,
                Invitations = 10,
                CaptureHistories = new[] { 1_000, 10_000 },
                Artifacts = new[] { 6_000, 60_000 },
                DerivativeJobs = new[] { 4_000, 40_000 },
                EnvironmentalObservations = new[] { 2_000, 20_000 },
                TransientEvents = new[] { 50, 500 },
                ReleasedEvents = new[] { 10, 100 },
                ReleasedCentralPreviews = new[] { 60, 600 },
                PageSize = 50
            },
            Revision = Environment.GetEnvironmentVariable("HVO_ISSUE_107_REVISION") ?? "development",
            Browser = browserVersion,
            Environment = environmentEvidence,
            WarmupOperations,
            MeasuredOperations,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Results = results,
            PublicPreviewTransfers = transferResults,
            RetrievalBoundaries = retrieval,
            Sql = sqlEvidence,
            SessionResources = resourceResults
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "performance.json"),
            JsonSerializer.Serialize(evidence, EvidenceJsonOptions)).ConfigureAwait(false);
        var csv = new List<string>
        {
            "independent_sessions,elapsed_seconds,process_cpu_ms,working_set_bytes,private_bytes,managed_allocated_bytes,gc_heap_bytes,active_pages"
        };
        csv.AddRange(processSamples.Select(sample => string.Join(',',
            sample.IndependentSessions.ToString(CultureInfo.InvariantCulture),
            sample.ElapsedSeconds.ToString(CultureInfo.InvariantCulture),
            sample.ProcessCpuMilliseconds.ToString(CultureInfo.InvariantCulture),
            sample.WorkingSetBytes.ToString(CultureInfo.InvariantCulture),
            sample.PrivateBytes.ToString(CultureInfo.InvariantCulture),
            sample.ManagedAllocatedBytes.ToString(CultureInfo.InvariantCulture),
            sample.GcHeapBytes.ToString(CultureInfo.InvariantCulture),
            sample.ActivePages.ToString(CultureInfo.InvariantCulture))));
        await File.WriteAllLinesAsync(Path.Combine(root, "process.csv"), csv).ConfigureAwait(false);
        var evidenceSetRoot = Environment.GetEnvironmentVariable("HVO_ISSUE_107_EVIDENCE_SET_ROOT");
        if (!string.IsNullOrWhiteSpace(evidenceSetRoot))
        {
            await File.WriteAllTextAsync(
                Path.Combine(evidenceSetRoot, "environment.json"),
                JsonSerializer.Serialize(environmentEvidence, EvidenceJsonOptions)).ConfigureAwait(false);
        }
        if (trial == 5 && !string.IsNullOrWhiteSpace(evidenceSetRoot))
        {
            await WriteCandidateSummaryAsync(evidenceSetRoot).ConfigureAwait(false);
        }
    }

    private static object CreateEnvironmentEvidence(string browserVersion)
    {
        var cpuModel = RuntimeInformation.ProcessArchitecture.ToString();
        const string cpuInfoPath = "/proc/cpuinfo";
        if (File.Exists(cpuInfoPath))
        {
            cpuModel = File.ReadLines(cpuInfoPath)
                .FirstOrDefault(line => line.StartsWith("model name", StringComparison.OrdinalIgnoreCase))?
                .Split(':', 2)[^1].Trim() ?? cpuModel;
        }
        return new
        {
            Schema = "hvo-logichost-ui-107-environment-v1",
            Branch = Environment.GetEnvironmentVariable("HVO_ISSUE_107_BRANCH") ?? "unknown",
            Revision = Environment.GetEnvironmentVariable("HVO_ISSUE_107_REVISION") ?? "development",
            DirtySha256 = Environment.GetEnvironmentVariable("HVO_ISSUE_107_DIRTY_SHA256") ?? "unknown",
            OperatingSystem = RuntimeInformation.OSDescription,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            CpuModel = cpuModel,
            LogicalProcessorCount = Environment.ProcessorCount,
            AvailableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            Storage = "container filesystem; physical medium unavailable",
            DotnetSdk = "10.0.100 (pinned global.json)",
            DotnetRuntime = RuntimeInformation.FrameworkDescription,
            Configuration = "Release",
            ServerGarbageCollection = GCSettings.IsServerGC,
            GarbageCollectionLatencyMode = GCSettings.LatencyMode.ToString(),
            Browser = browserVersion,
            HostMode = "loopback Kestrel",
            DependencyMode = "Testcontainers",
            Images = new
            {
                SqlServer = IntegrationTestFixture.SqlServerImage,
                Redis = IntegrationTestFixture.RedisImage,
                Minio = IntegrationTestFixture.MinioImage,
                Mailpit = IntegrationTestFixture.MailpitImage
            }
        };
    }

    private static async Task WriteCandidateSummaryAsync(string evidenceSetRoot)
    {
        var trials = new List<IReadOnlyList<RouteMixEvidence>>(5);
        var transferTrials = new List<IReadOnlyList<TransferEvidence>>(5);
        var retrievalTrials = new List<RetrievalBoundaryEvidence>(5);
        var encodingTrials = new List<EncodingTrial>(5);
        var sqlTrials = new List<SqlPerformanceEvidence>(5);
        var resourceTrials = new List<IReadOnlyList<SessionResourceEvidence>>(5);
        string? datasetSha256 = null;
        for (var trial = 1; trial <= 5; trial++)
        {
            var path = Path.Combine(evidenceSetRoot, "candidate", $"trial-{trial:D2}", "performance.json");
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            var root = document.RootElement;
            root.GetProperty("Workload").GetString().Should().Be(Issue107PerformanceDatasetSeeder.Workload);
            root.GetProperty("CanonicalDatasetComplete").GetBoolean().Should().BeTrue();
            var currentDatasetSha256 = root.GetProperty("DatasetSha256").GetString();
            datasetSha256 ??= currentDatasetSha256;
            currentDatasetSha256.Should().Be(datasetSha256);
            root.GetProperty("Trial").GetInt32().Should().Be(trial);
            trials.Add(JsonSerializer.Deserialize<RouteMixEvidence[]>(
                root.GetProperty("Results").GetRawText())!);
            transferTrials.Add(JsonSerializer.Deserialize<TransferEvidence[]>(
                root.GetProperty("PublicPreviewTransfers").GetRawText())!);
            retrievalTrials.Add(JsonSerializer.Deserialize<RetrievalBoundaryEvidence>(
                root.GetProperty("RetrievalBoundaries").GetRawText())!);
            sqlTrials.Add(JsonSerializer.Deserialize<SqlPerformanceEvidence>(
                root.GetProperty("Sql").GetRawText())!);
            resourceTrials.Add(JsonSerializer.Deserialize<SessionResourceEvidence[]>(
                root.GetProperty("SessionResources").GetRawText())!);
            var encodingPath = Path.Combine(
                evidenceSetRoot,
                "candidate",
                $"trial-{trial:D2}",
                "encoding",
                "processing-performance-W1-encoded-preview-jpeg.json");
            await using var encodingStream = File.OpenRead(encodingPath);
            using var encodingDocument = await JsonDocument.ParseAsync(encodingStream).ConfigureAwait(false);
            var encodingRoot = encodingDocument.RootElement;
            encodingRoot.GetProperty("warmups").GetInt32().Should().Be(5);
            encodingRoot.GetProperty("repetitions").GetInt32().Should().Be(30);
            var encoding = encodingRoot.GetProperty("results")[0];
            encoding.GetProperty("width").GetInt32().Should().Be(1936);
            encoding.GetProperty("height").GetInt32().Should().Be(1216);
            encoding.GetProperty("inputBytes").GetInt64().Should().Be(4_708_352);
            encoding.GetProperty("outputBytes").GetInt64().Should().BeLessThanOrEqualTo(16L * 1024 * 1024);
            encodingTrials.Add(new(
                encoding.GetProperty("p95Milliseconds").GetDouble(),
                encoding.GetProperty("cpuMillisecondsPerOperation").GetDouble(),
                encoding.GetProperty("allocatedBytesPerOperation").GetInt64(),
                encoding.GetProperty("operationsPerSecond").GetDouble(),
                encoding.GetProperty("outputBytes").GetInt64(),
                encoding.GetProperty("outputChecksumSha256").GetString()!));
        }
        trials.Should().OnlyContain(items => items.Count == 30);
        transferTrials.Should().OnlyContain(items => items.Count == 3);
        sqlTrials.Should().OnlyContain(item => item.Pages.Count == 9);
        resourceTrials.Should().OnlyContain(items => items.Count == 3);
        var summary = trials[0].Select(sample =>
        {
            var comparable = trials.Select(items => items.Single(item =>
                item.Mix == sample.Mix
                && item.HistoryRows == sample.HistoryRows
                && item.IndependentSessions == sample.IndependentSessions)).ToArray();
            return new
            {
                sample.Mix,
                sample.HistoryRows,
                sample.IndependentSessions,
                P95Milliseconds = Statistics(comparable.Select(item => item.P95Milliseconds)),
                MaximumMilliseconds = Statistics(comparable.Select(item => item.MaximumMilliseconds)),
                MaximumResponseBytes = Statistics(comparable.Select(item => (double)item.MaximumResponseBytes)),
                OperationsPerSecond = Statistics(comparable.Select(item => item.OperationsPerSecond)),
                ProcessCpuMillisecondsPerRequest = Statistics(
                    comparable.Select(item => item.ProcessCpuMillisecondsPerRequest)),
                ManagedAllocatedBytesPerRequest = Statistics(
                    comparable.Select(item => (double)item.ManagedAllocatedBytesPerRequest))
            };
        }).ToArray();
        var transferSummary = transferTrials[0].Select(sample =>
        {
            var comparable = transferTrials.Select(items => items.Single(item =>
                item.IndependentClients == sample.IndependentClients)).ToArray();
            return new
            {
                sample.IndependentClients,
                P95Milliseconds = Statistics(comparable.Select(item => item.P95Milliseconds)),
                OperationsPerSecond = Statistics(comparable.Select(item => item.OperationsPerSecond))
            };
        }).ToArray();
        encodingTrials.Select(item => item.OutputChecksumSha256).Distinct(StringComparer.Ordinal)
            .Should().ContainSingle();
        var baseline = await ReadBaselineAsync().ConfigureAwait(false);
        encodingTrials.Select(item => item.OutputChecksumSha256).Should()
            .OnlyContain(checksum => checksum == baseline.Encoding[0].OutputChecksumSha256);
        var encodingComparisons = new[]
        {
            CompareLower("encoding-p95-ms", encodingTrials.Select(item => item.P95Milliseconds),
                baseline.Encoding.Select(item => item.P95Milliseconds)),
            CompareLower("encoding-cpu-ms-per-operation",
                encodingTrials.Select(item => item.CpuMillisecondsPerOperation),
                baseline.Encoding.Select(item => item.CpuMillisecondsPerOperation)),
            CompareLower("encoding-allocated-bytes-per-operation",
                encodingTrials.Select(item => (double)item.AllocatedBytesPerOperation),
                baseline.Encoding.Select(item => (double)item.AllocatedBytesPerOperation)),
            CompareHigher("encoding-operations-per-second",
                encodingTrials.Select(item => item.OperationsPerSecond),
                baseline.Encoding.Select(item => item.OperationsPerSecond))
        };
        encodingComparisons.Should().OnlyContain(comparison => !comparison.Blocked);
        await File.WriteAllTextAsync(
            Path.Combine(evidenceSetRoot, "performance-candidate-summary.json"),
            JsonSerializer.Serialize(new
            {
                Schema = "hvo-logichost-ui-107-five-trial-candidate-summary-v2",
                Workload = Issue107PerformanceDatasetSeeder.Workload,
                DatasetSha256 = datasetSha256,
                TrialCount = 5,
                Baseline = new
                {
                    baseline.Revision,
                    TrialCount = 5,
                    ExistingHomeShell = baseline.Home[0].Select(sample => new
                    {
                        sample.IndependentSessions,
                        P95Milliseconds = Statistics(baseline.Home.Select(items => items.Single(item =>
                            item.IndependentSessions == sample.IndependentSessions).P95Milliseconds)),
                        MaximumResponseBytes = Statistics(baseline.Home.Select(items => (double)items.Single(item =>
                            item.IndependentSessions == sample.IndependentSessions).MaximumResponseBytes)),
                        OperationsPerSecond = Statistics(baseline.Home.Select(items => items.Single(item =>
                            item.IndependentSessions == sample.IndependentSessions).OperationsPerSecond)),
                        ProcessCpuMillisecondsPerRequest = Statistics(baseline.Home.Select(items => items.Single(item =>
                            item.IndependentSessions == sample.IndependentSessions).ProcessCpuMillisecondsPerRequest)),
                        ManagedAllocatedBytesPerRequest = Statistics(baseline.Home.Select(items => (double)items.Single(item =>
                            item.IndependentSessions == sample.IndependentSessions).ManagedAllocatedBytesPerRequest))
                    }),
                    Disposition = "Nearest existing-shell evidence is informational because candidate public routes and projections are net-new."
                },
                RegressionDisposition = new
                {
                    BlockingComparable = encodingComparisons,
                    NetNewAbsoluteBaseline = NetNewBaselineMetrics,
                    ThresholdPercent = 15,
                    Passed = encodingComparisons.All(comparison => !comparison.Blocked)
                },
                Candidate = summary,
                PublicPreviewTransfers = transferSummary,
                Retrieval = new
                {
                    W2FullMilliseconds = Statistics(retrievalTrials.Select(item => item.W2FullMilliseconds)),
                    W2RangeMilliseconds = Statistics(retrievalTrials.Select(item => item.W2RangeMilliseconds)),
                    EmptyNotModifiedResponses = retrievalTrials.Select(item => item.EmptyNotModifiedResponses).ToArray(),
                    PreCancelledReads = retrievalTrials.Select(item => item.PreCancelledReads).ToArray(),
                    AbortedTransfers = retrievalTrials.Select(item => item.AbortedTransfers).ToArray(),
                    MaximumServerCancellationSettlementMilliseconds = Statistics(
                        retrievalTrials.Select(item => item.MaximumServerCancellationSettlementMilliseconds)),
                    MaximumActiveStreamSettlementMilliseconds = Statistics(
                        retrievalTrials.Select(item => item.MaximumActiveStreamSettlementMilliseconds)),
                    ActiveStreamsAfterCancellation = retrievalTrials.Select(item => item.ActiveStreamsAfterCancellation).ToArray(),
                    ObjectReaderIo = retrievalTrials.Select(item => item.ObjectReaderIo).ToArray()
                },
                Sql = sqlTrials[0].Pages.Select(sample => new
                {
                    sample.Name,
                    sample.HistoryRows,
                    sample.Offset,
                    sample.LogicalCameraFiltered,
                    SqlCommands = Statistics(sqlTrials.Select(items => (double)items.Pages.Single(item => item.Name == sample.Name).SqlCommands)),
                    ProjectedRootRows = Statistics(sqlTrials.Select(items => (double)items.Pages.Single(item => item.Name == sample.Name).ProjectedRootRows)),
                    LogicalReads = Statistics(sqlTrials.Select(items => (double)items.Pages.Single(item => item.Name == sample.Name).LogicalReads)),
                    sample.NormalizedCommandSha256,
                    sample.UsesBoundedCaptureIndex,
                    sample.StableExpectedItems
                }),
                SessionResources = resourceTrials[0].Select(sample => new
                {
                    sample.IndependentSessions,
                    WorkingSetGrowthBytes = Statistics(resourceTrials.Select(items => (double)items.Single(item =>
                        item.IndependentSessions == sample.IndependentSessions).WorkingSetGrowthBytes))
                }),
                Encoding = new
                {
                    P95Milliseconds = Statistics(encodingTrials.Select(item => item.P95Milliseconds)),
                    CpuMillisecondsPerOperation = Statistics(
                        encodingTrials.Select(item => item.CpuMillisecondsPerOperation)),
                    AllocatedBytesPerOperation = Statistics(
                        encodingTrials.Select(item => (double)item.AllocatedBytesPerOperation)),
                    OperationsPerSecond = Statistics(encodingTrials.Select(item => item.OperationsPerSecond)),
                    OutputBytes = encodingTrials[0].OutputBytes,
                    OutputChecksumSha256 = encodingTrials[0].OutputChecksumSha256
                }
            }, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    private static async Task<BaselineEvidence> ReadBaselineAsync()
    {
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE_107_BASELINE_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new InvalidOperationException("HVO_ISSUE_107_BASELINE_ROOT must identify the pinned mainline baseline.");
        }
        var home = new List<IReadOnlyList<BaselineHomeEvidence>>(5);
        var encoding = new List<EncodingTrial>(5);
        string? revision = null;
        for (var trial = 1; trial <= 5; trial++)
        {
            await using var homeStream = File.OpenRead(Path.Combine(root, $"home-trial-{trial:D2}.json"));
            using var homeDocument = await JsonDocument.ParseAsync(homeStream).ConfigureAwait(false);
            var homeRoot = homeDocument.RootElement;
            homeRoot.GetProperty("Schema").GetString().Should().Be("hvo-logichost-ui-107-main-home-baseline-v1");
            homeRoot.GetProperty("Trial").GetInt32().Should().Be(trial);
            var currentRevision = homeRoot.GetProperty("Revision").GetString();
            revision ??= currentRevision;
            currentRevision.Should().Be(revision);
            home.Add(JsonSerializer.Deserialize<BaselineHomeEvidence[]>(
                homeRoot.GetProperty("Results").GetRawText())!);

            await using var encodingStream = File.OpenRead(Path.Combine(
                root, $"encoding-trial-{trial:D2}", "processing-performance-W1-encoded-preview-jpeg.json"));
            using var encodingDocument = await JsonDocument.ParseAsync(encodingStream).ConfigureAwait(false);
            var result = encodingDocument.RootElement.GetProperty("results")[0];
            encoding.Add(new(
                result.GetProperty("p95Milliseconds").GetDouble(),
                result.GetProperty("cpuMillisecondsPerOperation").GetDouble(),
                result.GetProperty("allocatedBytesPerOperation").GetInt64(),
                result.GetProperty("operationsPerSecond").GetDouble(),
                result.GetProperty("outputBytes").GetInt64(),
                result.GetProperty("outputChecksumSha256").GetString()!));
        }
        home.Should().OnlyContain(items => items.Count == 3);
        encoding.Select(item => item.OutputChecksumSha256).Distinct(StringComparer.Ordinal).Should().ContainSingle();
        return new(revision!, home, encoding);
    }

    private static RegressionComparison CompareLower(
        string metric,
        IEnumerable<double> candidate,
        IEnumerable<double> baseline)
    {
        var candidateStatistics = Statistics(candidate);
        var baselineStatistics = Statistics(baseline);
        var regression = (candidateStatistics.Median / baselineStatistics.Median - 1) * 100;
        return new(metric, candidateStatistics, baselineStatistics, regression,
            regression > 15 && candidateStatistics.Median > baselineStatistics.Maximum);
    }

    private static RegressionComparison CompareHigher(
        string metric,
        IEnumerable<double> candidate,
        IEnumerable<double> baseline)
    {
        var candidateStatistics = Statistics(candidate);
        var baselineStatistics = Statistics(baseline);
        var regression = (baselineStatistics.Median - candidateStatistics.Median) / baselineStatistics.Median * 100;
        return new(metric, candidateStatistics, baselineStatistics, regression,
            regression > 15 && candidateStatistics.Median < baselineStatistics.Minimum);
    }

    private static TrialStatistics Statistics(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return new(ordered[0], ordered[2], ordered[^1]);
    }

    private static async Task WriteCheckpointAsync(
        Issue107PerformanceDataset dataset,
        IReadOnlyList<RouteMixEvidence> results)
    {
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE_107_EVIDENCE_ROOT");
        if (string.IsNullOrWhiteSpace(root)) return;
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "performance-checkpoint.json"),
            JsonSerializer.Serialize(new
            {
                Schema = "hvo-logichost-ui-107-performance-checkpoint-v1",
                Workload = Issue107PerformanceDatasetSeeder.Workload,
                DatasetSha256 = dataset.DatasetSha256,
                Trial = Environment.GetEnvironmentVariable("HVO_ISSUE_107_TRIAL") ?? "development",
                Results = results
            }, EvidenceJsonOptions)).ConfigureAwait(false);
    }

    private sealed record RouteMix(
        string Name,
        int HistoryRows,
        bool IsPublic,
        IReadOnlyList<string> Routes);

    private sealed record RouteMixEvidence(
        string Mix,
        int HistoryRows,
        int IndependentSessions,
        int Samples,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        long MaximumResponseBytes,
        double ElapsedSeconds,
        double OperationsPerSecond,
        double ProcessCpuMillisecondsPerRequest,
        long ManagedAllocatedBytesPerRequest);

    private sealed class RetrievalMeterCapture : IDisposable
    {
        private const string Reads = "skymonitor.central.retrieval.reads";
        private const string Bytes = "skymonitor.central.retrieval.bytes";
        private readonly ConcurrentQueue<RetrievalMeasurement> measurements = [];
        private readonly SemaphoreSlim changed = new(0);
        private readonly MeterListener listener = new();

        internal RetrievalMeterCapture()
        {
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == CentralArtifactRetrievalTelemetry.MeterName
                    && instrument.Name is Reads or Bytes)
                {
                    current.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? operation = null;
                string? outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "operation") operation = tag.Value?.ToString();
                    else if (tag.Key == "outcome") outcome = tag.Value?.ToString();
                }
                if (operation is null || outcome is null) return;
                measurements.Enqueue(new(instrument.Name, operation, outcome, value));
                changed.Release();
            });
            listener.Start();
        }

        internal long TotalReads => measurements.Where(item => item.Instrument == Reads).Sum(item => item.Value);

        internal long SumReads(string operation, string outcome) => Sum(Reads, operation, outcome);

        internal long SumBytes(string operation, string outcome) => Sum(Bytes, operation, outcome);

        internal async Task WaitForReadsAsync(
            string operation,
            string outcome,
            long expected,
            TimeSpan timeout)
        {
            using var timeoutSource = new CancellationTokenSource(timeout);
            while (SumReads(operation, outcome) < expected)
            {
                await changed.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
        }

        private long Sum(string instrument, string operation, string outcome) => measurements
            .Where(item => item.Instrument == instrument
                && item.Operation == operation
                && item.Outcome == outcome)
            .Sum(item => item.Value);

        public void Dispose()
        {
            listener.Dispose();
            changed.Dispose();
        }

        private sealed record RetrievalMeasurement(
            string Instrument,
            string Operation,
            string Outcome,
            long Value);
    }

    private sealed record ProcessSample(
        int IndependentSessions,
        int ElapsedSeconds,
        double ProcessCpuMilliseconds,
        long WorkingSetBytes,
        long PrivateBytes,
        long ManagedAllocatedBytes,
        long GcHeapBytes,
        int ActivePages);

    private sealed record TransferEvidence(
        int IndependentClients,
        int Samples,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        double OperationsPerSecond);

    private sealed record RetrievalBoundaryEvidence(
        int EmptyNotModifiedResponses,
        long W2FullBytes,
        double W2FullMilliseconds,
        long W2RangeBytes,
        double W2RangeMilliseconds,
        int PreCancelledReads,
        int AbortedTransfers,
        double MaximumServerCancellationSettlementMilliseconds,
        double MaximumActiveStreamSettlementMilliseconds,
        long ActiveStreamsAfterCancellation,
        RetrievalObjectIoEvidence ObjectReaderIo,
        bool ChecksumsAndRangesVerified);

    private sealed record RetrievalObjectIoEvidence(
        long VerifyCompletedOperations,
        long VerifyCompletedBytes,
        long ServeCompletedOperations,
        long ServeCompletedBytes,
        long ServeCancelledOperations,
        long UnexpectedOperations);

    private sealed record SessionResourceEvidence(
        int IndependentSessions,
        long WorkingSetBeforeBytes,
        long MaximumWorkingSetBytes,
        long WorkingSetGrowthBytes);

    private sealed record TrialStatistics(double Minimum, double Median, double Maximum);

    private sealed record EncodingTrial(
        double P95Milliseconds,
        double CpuMillisecondsPerOperation,
        long AllocatedBytesPerOperation,
        double OperationsPerSecond,
        long OutputBytes,
        string OutputChecksumSha256);

    private sealed record BaselineEvidence(
        string Revision,
        IReadOnlyList<IReadOnlyList<BaselineHomeEvidence>> Home,
        IReadOnlyList<EncodingTrial> Encoding);

    private sealed record BaselineHomeEvidence(
        int IndependentSessions,
        int Samples,
        double MedianMilliseconds,
        double P95Milliseconds,
        double MaximumMilliseconds,
        long MaximumResponseBytes,
        double OperationsPerSecond,
        double ProcessCpuMillisecondsPerRequest,
        long ManagedAllocatedBytesPerRequest);

    private sealed record RegressionComparison(
        string Metric,
        TrialStatistics Candidate,
        TrialStatistics Baseline,
        double RegressionPercent,
        bool Blocked);
}
