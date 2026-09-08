using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CameraAgentGalleryPerformanceTests
{
    private const int PageSize = 50;
    private const int MaximumResponseBytes = 512 * 1024;
    private const double MaximumP95Milliseconds = 5_000;

    // Browser-driven latency is load-sensitive in a way the read-model measurements are not: under
    // contention the two browser families move +41.6% and +46.0% while the SQLite and Kestrel
    // families stay flat (see #785). A wall-clock p95 measured on a busy host therefore describes
    // the host rather than the product, so the browser bounds are only asserted when the machine was
    // quiet before the workload began. Being wrong about this ceiling is safe in both directions: too
    // strict yields Inconclusive, which scripts/lib/trx-evidence.sh refuses as "proved nothing", and
    // too lenient is no worse than asserting unconditionally as before. It can never manufacture a
    // false pass or a false regression, which is why a constant is defensible here and is not for the
    // latency bound itself.
    private const double MaximumAdmissibleLoadPerCore = 0.40;
    private const long MaximumWorkingSetGrowthBytes = 256L * 1024 * 1024;
    private const long MaximumRenderedPageBytes = 1024L * 1024;
    private const long MaximumPerSessionWorkingSetBytes = 32L * 1024 * 1024;
    private static readonly int[] CaptureCounts = [1_000, 10_000];
    private static readonly int[] ConcurrencyLevels = [1, 10, 50];
    private static readonly JsonSerializerOptions EvidenceJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("Manual")]
    [DoNotParallelize]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each asynchronously created performance fixture is immediately owned by a using declaration for its workload iteration.")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Await-using must retain the strongly typed Kestrel fixture for authenticated HTTP measurements.")]
    public async Task GalleryReadModelRecordsIssue106AcceptanceEvidenceAsync()
    {
        // Sampled before any workload so it reflects the host we inherited rather than the load this
        // test itself generates; by the browser phase the run has driven load well above the ceiling.
        var contention = SampleHostContention();
        var allMeasurements = new List<GalleryMeasurement>();
        var plans = new List<SqlPlanEvidence>();
        var correctness = new List<CorrectnessEvidence>();
        var apiMeasurements = new List<ApiMeasurement>();
        var browserMeasurements = new List<BrowserRenderMeasurement>();
        var browserPreviewFailureMeasurements = new List<BrowserPreviewFailureMeasurement>();
        PreviewCacheEvidence? previewCache = null;
        string? sqliteVersion = null;
        var evidenceLabel = string.Equals(
            Environment.GetEnvironmentVariable("HVO_GALLERY_EVIDENCE_LABEL"),
            "baseline",
            StringComparison.Ordinal)
            ? "baseline"
            : "candidate";
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        if (!File.Exists(playwright.Chromium.ExecutablePath))
        {
            Assert.Inconclusive(
                "Pinned Playwright Chromium is absent. Run `scripts/test:cameraagent-ui --install-browser` from the repository root.");
        }
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        }).ConfigureAwait(false);

        foreach (var captureCount in CaptureCounts)
        {
            using var fixture = await GalleryPerformanceFixture.CreateAsync(captureCount).ConfigureAwait(false);
            await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
            {
                services.RemoveAll<ICameraAgentGallery>();
                services.AddSingleton<ICameraAgentGallery>(fixture.Gallery);
            }).ConfigureAwait(false);
            using var ownerClient = await host.CreateOwnerClientAsync().ConfigureAwait(false);
            sqliteVersion ??= await fixture.ReadSqliteVersionAsync().ConfigureAwait(false);
            plans.AddRange(await CollectPlansAsync(fixture, captureCount).ConfigureAwait(false));
            var cursors = await BuildCursorsAsync(fixture.Gallery, captureCount).ConfigureAwait(false);
            correctness.Add(await AssertCompletePagingAsync(fixture.Gallery, captureCount).ConfigureAwait(false));
            await AssertCancellationAsync(fixture.Gallery).ConfigureAwait(false);

            var firstExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize), CancellationToken.None).ConfigureAwait(false);
            var middleExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Middle), CancellationToken.None).ConfigureAwait(false);
            var laterExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Later), CancellationToken.None).ConfigureAwait(false);
            var filteredExpected = await fixture.Gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, EvidenceOrigin: GalleryEvidenceOrigin.DeveloperFixture),
                CancellationToken.None).ConfigureAwait(false);
            var detailId = middleExpected.Items[middleExpected.Items.Count / 2].CaptureId;
            var detailExpected = await fixture.Gallery.GetCaptureAsync(detailId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detailExpected);

            var scenarios = new[]
            {
                PageScenario("first", new CameraAgentGalleryQuery(PageSize: PageSize), firstExpected),
                PageScenario("middle", new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Middle), middleExpected),
                PageScenario("later", new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursors.Later), laterExpected),
                PageScenario("filtered-developer-fixture", new CameraAgentGalleryQuery(
                    PageSize: PageSize, EvidenceOrigin: GalleryEvidenceOrigin.DeveloperFixture), filteredExpected),
                DetailScenario(detailId, detailExpected)
            };

            foreach (var scenario in scenarios)
            {
                await scenario.Execute(fixture.Gallery, CancellationToken.None).ConfigureAwait(false);
                foreach (var concurrency in ConcurrencyLevels)
                {
                    allMeasurements.Add(await MeasureAsync(
                        fixture.Gallery,
                        captureCount,
                        scenario,
                        concurrency).ConfigureAwait(false));
                }
            }
            foreach (var concurrency in ConcurrencyLevels)
            {
                apiMeasurements.Add(await MeasureHttpApiAsync(
                    ownerClient, captureCount, concurrency).ConfigureAwait(false));
            }
            var browserSessionMeasurements = await MeasureBrowserSessionsAsync(
                browser, host.BaseAddress, captureCount, evidenceLabel != "baseline", contention).ConfigureAwait(false);
            browserMeasurements.AddRange(browserSessionMeasurements.Renders);
            browserPreviewFailureMeasurements.AddRange(browserSessionMeasurements.PreviewFailures);

            previewCache ??= await MeasurePreviewCacheAsync(fixture).ConfigureAwait(false);
        }

        AssertScaling(allMeasurements);
        var evidence = new
        {
            SchemaVersion = "cameraagent-archive-441-performance-v2",
            Revision = ReadRevisionEvidence(),
            RecordedUtc = DateTimeOffset.UtcNow,
            Command = "dotnet test tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj --configuration Release --filter FullyQualifiedName~CameraAgentGalleryPerformanceTests",
            Environment = new
            {
                OperatingSystem = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                ProcessorCount = Environment.ProcessorCount,
                ServerGc = System.Runtime.GCSettings.IsServerGC,
                SqliteVersion = sqliteVersion,
                PreWorkloadLoadPerCore = contention.LoadPerCore,
                ContentionCeilingPerCore = MaximumAdmissibleLoadPerCore
            },
            Workload = new
            {
                CaptureCounts,
                ConcurrencyLevels,
                PageSize,
                Origins = new[] { "Simulated", "DeveloperFixture", "Unknown" },
                ProcessingStatuses = new[] { "Completed", "RetryableFailure", "TerminalFailure" },
                DerivedOutputsPerCapture = 1,
                Scenarios = new[] { "first", "middle", "later", "filtered-developer-fixture", "detail" },
                Persistence = "production raw-ingress and capture-processing SQLite schemas with production gallery read service"
            },
            Thresholds = new
            {
                MaximumResponseBytes,
                MaximumP95Milliseconds,
                MaximumWorkingSetGrowthBytes,
                MaximumRenderedPageBytes,
                MaximumPerSessionWorkingSetBytes,
                LaterPageMultiplier = 4,
                LaterPageAllowanceMilliseconds = 25,
                HistoryAllocationMultiplier = 2,
                HistoryAllocationAllowanceBytes = 4 * 1024 * 1024
            },
            SqlPlans = plans,
            Measurements = allMeasurements,
            AuthenticatedKestrelApiMeasurements = apiMeasurements,
            AuthenticatedBrowserMeasurements = browserMeasurements,
            AuthenticatedBrowserPreviewFailureMeasurements = browserPreviewFailureMeasurements,
            Baseline = new
            {
                Status = evidenceLabel == "baseline" ? "Captured" : "Candidate",
                EvidenceLabel = evidenceLabel,
                Comparison = "Compare equivalent issue-441 baseline and candidate files by capture count and concurrency."
            },
            Correctness = correctness,
            Cancellation = new { PreCancelledReadThrows = true, MaximumObservedMilliseconds = 2_000 },
            PreviewCache = previewCache,
            Privacy = new
            {
                InternalPathsEmitted = false,
                SecretsEmitted = false,
                SqlValues = "fixed synthetic identities only"
            }
        };

        var outputDirectory = Path.Combine(GetRepositoryRoot(), "TestResults", "issue-441", evidenceLabel);
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "cameraagent-gallery-performance.json");
        var evidenceBytes = JsonSerializer.SerializeToUtf8Bytes(evidence, EvidenceJson);
        await File.WriteAllBytesAsync(outputPath, evidenceBytes).ConfigureAwait(false);
        TestContext.WriteLine($"Issue #441 performance evidence: {outputPath}");
        TestContext.WriteLine($"Issue #441 performance evidence SHA-256: {Convert.ToHexString(SHA256.HashData(evidenceBytes))}");
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Await-using must retain the strongly typed Playwright context for the measured session scope.")]
    private static async Task<BrowserSessionMeasurements> MeasureBrowserSessionsAsync(
        IBrowser browser,
        Uri baseAddress,
        int captureCount,
        bool measurePreviewFailures,
        HostContention contention)
    {
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = baseAddress.ToString(),
            ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
            ColorScheme = ColorScheme.Dark,
            ReducedMotion = ReducedMotion.Reduce
        }).ConfigureAwait(false);
        context.SetDefaultTimeout(45_000);
        context.SetDefaultNavigationTimeout(45_000);
        var loginPage = await context.NewPageAsync().ConfigureAwait(false);
        await LoginAsync(loginPage).ConfigureAwait(false);
        await loginPage.CloseAsync().ConfigureAwait(false);
        await context.RouteAsync(
            "**/api/v1/operations/artifacts/*/preview",
            static route => route.FulfillAsync(new RouteFulfillOptions
            {
                Status = 200,
                ContentType = "image/svg+xml",
                Body = "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>",
            })).ConfigureAwait(false);
        var measurements = new List<BrowserRenderMeasurement>();
        var previewFailureMeasurements = new List<BrowserPreviewFailureMeasurement>();
        foreach (var concurrency in ConcurrencyLevels)
        {
            ForceCollection();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var workingSetBefore = process.WorkingSet64;
            var rssBefore = ReadRssBytes();
            var cpuBefore = process.TotalProcessorTime;
            var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
            var pages = await Task.WhenAll(Enumerable.Range(0, concurrency)
                .Select(_ => context.NewPageAsync())).ConfigureAwait(false);
            var overall = Stopwatch.StartNew();
            var renders = await Task.WhenAll(pages.Select(async page =>
            {
                var timer = Stopwatch.StartNew();
                await page.GotoAsync("/gallery?pageSize=50").ConfigureAwait(false);
                await page.Locator(".capture-card").First.WaitForAsync().ConfigureAwait(false);
                timer.Stop();
                var cardCount = await page.Locator(".capture-card").CountAsync().ConfigureAwait(false);
                var content = await page.ContentAsync().ConfigureAwait(false);
                return new BrowserRenderResult(
                    timer.Elapsed.TotalMilliseconds,
                    System.Text.Encoding.UTF8.GetByteCount(content),
                    cardCount);
            })).ConfigureAwait(false);
            overall.Stop();
            process.Refresh();
            var workingSetAfter = process.WorkingSet64;
            var rssAfter = ReadRssBytes();
            var cpuAfter = process.TotalProcessorTime;
            var allocationsAfter = GC.GetTotalAllocatedBytes(precise: true);
            Assert.IsTrue(renders.All(static render => render.CardCount == PageSize));
            Assert.IsTrue(renders.All(static render => render.RenderedBytes <= MaximumRenderedPageBytes));
            var workingSetGrowth = Math.Max(0, workingSetAfter - workingSetBefore);
            var perSessionGrowth = workingSetGrowth / concurrency;
            Assert.IsLessThanOrEqualTo(MaximumPerSessionWorkingSetBytes, perSessionGrowth);
            var latencies = renders.Select(static render => render.ElapsedMilliseconds).Order().ToArray();
            AssertBrowserP95Admissible(contention, Percentile(latencies, 0.95), "browser render sessions");
            measurements.Add(new BrowserRenderMeasurement(
                captureCount,
                concurrency,
                renders.Length,
                overall.Elapsed.TotalMilliseconds,
                Percentile(latencies, 0.5),
                latencies.Length >= 30 ? Percentile(latencies, 0.95) : null,
                latencies[^1],
                renders.Min(static render => render.RenderedBytes),
                renders.Max(static render => render.RenderedBytes),
                renders[0].CardCount,
                (cpuAfter - cpuBefore).TotalMilliseconds,
                Math.Max(0, allocationsAfter - allocationsBefore),
                workingSetBefore,
                workingSetAfter,
                rssBefore,
                rssAfter,
                perSessionGrowth,
                concurrency / Math.Max(overall.Elapsed.TotalSeconds, 0.000_001)));
            if (measurePreviewFailures)
            {
                previewFailureMeasurements.Add(await MeasurePreviewFailuresAsync(
                    pages,
                    captureCount,
                    concurrency,
                    workingSetBefore,
                    rssBefore,
                    contention).ConfigureAwait(false));
            }
            await Task.WhenAll(pages.Select(static page => page.CloseAsync())).ConfigureAwait(false);
            await Task.Delay(250).ConfigureAwait(false);
        }
        return new BrowserSessionMeasurements(measurements, previewFailureMeasurements);
    }

    private static async Task<BrowserPreviewFailureMeasurement> MeasurePreviewFailuresAsync(
        IPage[] pages,
        int captureCount,
        int concurrency,
        long sessionWorkingSetBefore,
        long sessionRssBefore,
        HostContention contention)
    {
        foreach (var page in pages)
        {
            Assert.AreEqual(PageSize, await page.Locator(".capture-card__image img").CountAsync().ConfigureAwait(false));
            Assert.AreEqual(0, await page.GetByRole(AriaRole.Button, new()
            {
                Name = "Try preview again",
                Exact = true
            }).CountAsync().ConfigureAwait(false));
        }
        ForceCollection();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var overall = Stopwatch.StartNew();
        var latencies = await Task.WhenAll(pages.Select(async page =>
        {
            var timer = Stopwatch.StartNew();
            await page.WaitForFunctionAsync("""
                () => {
                    document.querySelectorAll('.capture-card__image img')
                        .forEach(image => image.dispatchEvent(new Event('error')));
                    return [...document.querySelectorAll('.capture-card__actions button')]
                        .filter(button => button.textContent.includes('Try preview again')).length === 50;
                }
                """).ConfigureAwait(false);
            timer.Stop();
            return timer.Elapsed.TotalMilliseconds;
        })).ConfigureAwait(false);
        overall.Stop();
        process.Refresh();
        var workingSetAfter = process.WorkingSet64;
        var rssAfter = ReadRssBytes();
        var cpuAfter = process.TotalProcessorTime;
        var allocationsAfter = GC.GetTotalAllocatedBytes(precise: true);
        var workingSetGrowth = Math.Max(0, workingSetAfter - sessionWorkingSetBefore);
        var perSessionGrowth = workingSetGrowth / concurrency;
        Assert.IsLessThanOrEqualTo(MaximumPerSessionWorkingSetBytes, perSessionGrowth);
        Array.Sort(latencies);
        AssertBrowserP95Admissible(contention, Percentile(latencies, 0.95), "browser preview failures");
        return new BrowserPreviewFailureMeasurement(
            captureCount,
            concurrency,
            pages.Length,
            overall.Elapsed.TotalMilliseconds,
            Percentile(latencies, 0.5),
            latencies.Length >= 30 ? Percentile(latencies, 0.95) : null,
            latencies[^1],
            PageSize,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            Math.Max(0, allocationsAfter - allocationsBefore),
            sessionWorkingSetBefore,
            workingSetAfter,
            sessionRssBefore,
            rssAfter,
            perSessionGrowth);
    }

    private static async Task LoginAsync(IPage page)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase))
            .ConfigureAwait(false);
    }

    private static async Task<ApiMeasurement> MeasureHttpApiAsync(
        HttpClient client,
        int captureCount,
        int concurrency)
    {
        var overall = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var timer = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                new Uri("/api/v1/operations/gallery?pageSize=50", UriKind.Relative),
                HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            timer.Stop();
            Assert.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.IsLessThanOrEqualTo(MaximumResponseBytes, payload.Length);
            return new ResponseMeasurement(
                timer.Elapsed.TotalMilliseconds,
                payload.Length,
                Convert.ToHexString(SHA256.HashData(payload)));
        }).ToArray();
        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        overall.Stop();
        Assert.HasCount(1, responses.Select(static response => response.Sha256).Distinct().ToArray());
        var latencies = responses.Select(static response => response.ElapsedMilliseconds).Order().ToArray();
        return new ApiMeasurement(
            captureCount,
            concurrency,
            responses[0].Bytes,
            Percentile(latencies, 0.5),
            Percentile(latencies, 0.95),
            concurrency / Math.Max(overall.Elapsed.TotalSeconds, 0.000_001),
            responses[0].Sha256);
    }

    private static PerformanceScenario PageScenario(
        string name,
        CameraAgentGalleryQuery query,
        CameraAgentGalleryPage expected)
    {
        var expectedIds = expected.Items.Select(static item => item.CaptureId).ToArray();
        return new PerformanceScenario(name, async (gallery, cancellationToken) =>
        {
            var page = await gallery.GetPageAsync(query, cancellationToken).ConfigureAwait(false);
            Assert.IsLessThanOrEqualTo(PageSize, page.Items.Count);
            CollectionAssert.AreEqual(expectedIds, page.Items.Select(static item => item.CaptureId).ToArray());
            Assert.AreEqual(page.Items.Count, page.Items.Select(static item => item.CaptureId).Distinct().Count());
            foreach (var capture in page.Items)
            {
                Assert.HasCount(2, capture.Artifacts);
                Assert.HasCount(1, capture.ProcessingNodes);
                Assert.IsTrue(capture.Artifacts.All(static artifact =>
                    artifact.ChecksumSha256.Length == 64 && artifact.ChecksumSha256.All(Uri.IsHexDigit)));
                var raw = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
                var preview = capture.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Preview);
                CollectionAssert.AreEqual(new[] { raw.ArtifactId }, preview.SourceArtifactIds.ToArray());
            }
            return page;
        });
    }

    private static PerformanceScenario DetailScenario(Guid captureId, CameraAgentGalleryCapture expected)
        => new("detail", async (gallery, cancellationToken) =>
        {
            var detail = await gallery.GetCaptureAsync(captureId, cancellationToken).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            Assert.AreEqual(expected.CaptureId, detail.CaptureId);
            Assert.AreEqual(expected.CaptureSequence, detail.CaptureSequence);
            Assert.IsNotNull(detail.Detail);
            Assert.AreEqual("Available", detail.Detail.EvidenceAvailability);
            Assert.AreEqual(ArtifactManifestV2.CurrentSchemaVersion, detail.Detail.ManifestSchemaVersion);
            var raw = detail.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Raw);
            var preview = detail.Artifacts.Single(static artifact => artifact.Role == FrameArtifactRole.Preview);
            CollectionAssert.AreEqual(new[] { raw.ArtifactId }, preview.SourceArtifactIds.ToArray());
            Assert.IsNotNull(preview.Recipe);
            Assert.AreEqual("gallery-preview", preview.Recipe.Name);
            return detail;
        });

    private static async Task<GalleryMeasurement> MeasureAsync(
        SqliteCameraAgentGallery gallery,
        int captureCount,
        PerformanceScenario scenario,
        int concurrency)
    {
        ForceCollection();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSetBefore = process.WorkingSet64;
        var rssBefore = ReadRssBytes();
        var cpuBefore = process.TotalProcessorTime;
        var allocationsBefore = GC.GetTotalAllocatedBytes(precise: true);
        var overall = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            var timer = Stopwatch.StartNew();
            var result = await scenario.Execute(gallery, CancellationToken.None).ConfigureAwait(false);
            var serialized = JsonSerializer.SerializeToUtf8Bytes(result, result.GetType(), EvidenceJson);
            timer.Stop();
            return new ResponseMeasurement(
                timer.Elapsed.TotalMilliseconds,
                serialized.Length,
                Convert.ToHexString(SHA256.HashData(serialized)));
        }).ToArray();
        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        overall.Stop();
        process.Refresh();
        var workingSetAfter = process.WorkingSet64;
        var rssAfter = ReadRssBytes();
        var cpuAfter = process.TotalProcessorTime;
        var allocationsAfter = GC.GetTotalAllocatedBytes(precise: true);

        Assert.IsTrue(responses.All(response => response.Bytes <= MaximumResponseBytes));
        Assert.HasCount(1, responses.Select(static response => response.Sha256).Distinct().ToArray());
        var latencies = responses.Select(static response => response.ElapsedMilliseconds).Order().ToArray();
        var p95 = Percentile(latencies, 0.95);
        Assert.IsLessThanOrEqualTo(MaximumP95Milliseconds, p95,
            $"{captureCount}/{scenario.Name}/{concurrency} p95 exceeded its acceptance bound.");
        var workingSetGrowth = Math.Max(0, workingSetAfter - workingSetBefore);
        Assert.IsLessThanOrEqualTo(MaximumWorkingSetGrowthBytes, workingSetGrowth);

        return new GalleryMeasurement(
            captureCount,
            scenario.Name,
            concurrency,
            overall.Elapsed.TotalMilliseconds,
            Percentile(latencies, 0.5),
            p95,
            responses.Min(static response => response.Bytes),
            responses.Max(static response => response.Bytes),
            (cpuAfter - cpuBefore).TotalMilliseconds,
            Math.Max(0, allocationsAfter - allocationsBefore),
            workingSetBefore,
            workingSetAfter,
            rssBefore,
            rssAfter,
            responses[0].Sha256);
    }

    private static async Task<GalleryCursors> BuildCursorsAsync(SqliteCameraAgentGallery gallery, int captureCount)
    {
        string? cursor = null;
        string? middle = null;
        string? later = null;
        var traversed = 0;
        while (traversed < captureCount)
        {
            if (traversed == captureCount / 2)
            {
                middle = cursor;
            }
            if (traversed == captureCount * 9 / 10)
            {
                later = cursor;
            }
            var page = await gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: PageSize, Cursor: cursor), CancellationToken.None).ConfigureAwait(false);
            traversed += page.Items.Count;
            cursor = page.NextCursor;
            if (cursor is null)
            {
                break;
            }
        }
        Assert.IsNotNull(middle);
        Assert.IsNotNull(later);
        return new GalleryCursors(middle, later);
    }

    private static async Task<CorrectnessEvidence> AssertCompletePagingAsync(
        SqliteCameraAgentGallery gallery,
        int captureCount)
    {
        var sequences = new List<long>(captureCount);
        string? cursor = null;
        do
        {
            var page = await gallery.GetPageAsync(
                new CameraAgentGalleryQuery(PageSize: 100, Cursor: cursor), CancellationToken.None).ConfigureAwait(false);
            sequences.AddRange(page.Items.Select(static item => item.CaptureSequence));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        CollectionAssert.AreEqual(
            Enumerable.Range(1, captureCount).Reverse().Select(static value => (long)value).ToArray(),
            sequences.ToArray());
        Assert.AreEqual(captureCount, sequences.Distinct().Count());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sequences);
        return new CorrectnessEvidence(
            captureCount,
            sequences.Count,
            sequences.Distinct().Count(),
            sequences[0],
            sequences[^1],
            Convert.ToHexString(SHA256.HashData(bytes)),
            "exact descending keyset sequence with no duplicates or gaps; raw/preview checksum and lineage asserted per response");
    }

    private static async Task AssertCancellationAsync(SqliteCameraAgentGallery gallery)
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync().ConfigureAwait(false);
        var timer = Stopwatch.StartNew();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await gallery.GetPageAsync(new CameraAgentGalleryQuery(PageSize: 100), cancellation.Token)
                .ConfigureAwait(false)).ConfigureAwait(false);
        timer.Stop();
        Assert.IsLessThanOrEqualTo(2_000, timer.Elapsed.TotalMilliseconds);
    }

    private static async Task<IReadOnlyList<SqlPlanEvidence>> CollectPlansAsync(
        GalleryPerformanceFixture fixture,
        int captureCount)
    {
        var middle = captureCount / 2;
        var statements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["canonical-keyset"] = $"SELECT raw_capture_row_id FROM raw_captures AS raw WHERE (raw.capture_sequence < {middle} OR (raw.capture_sequence = {middle} AND raw.raw_capture_row_id < {middle})) ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC LIMIT 51;",
            ["origin-filter"] = "SELECT raw_capture_row_id FROM raw_captures AS raw WHERE raw.evidence_origin = 'DeveloperFixture' ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC LIMIT 51;",
            ["status-filter"] = "SELECT raw_capture_row_id FROM raw_captures AS raw WHERE EXISTS (SELECT 1 FROM processing_nodes AS node WHERE node.capture_id = raw.capture_id AND node.status = 'Completed') ORDER BY raw.capture_sequence DESC, raw.raw_capture_row_id DESC LIMIT 51;"
        };
        var evidence = new List<SqlPlanEvidence>();
        foreach (var statement in statements)
        {
            var plan = await fixture.ExplainAsync(statement.Value).ConfigureAwait(false);
            Assert.IsFalse(plan.Any(static detail => detail.Contains("USE TEMP B-TREE", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", plan));
            Assert.IsFalse(plan.Any(static detail => detail.StartsWith("SCAN raw", StringComparison.OrdinalIgnoreCase) &&
                !detail.Contains("USING", StringComparison.OrdinalIgnoreCase)), string.Join(" | ", plan));
            evidence.Add(new SqlPlanEvidence(captureCount, statement.Key, statement.Value, plan));
        }
        return evidence;
    }

    private static async Task<PreviewCacheEvidence> MeasurePreviewCacheAsync(GalleryPerformanceFixture fixture)
    {
        var encoder = new CountingPreviewEncoder();
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        using var service = new CameraAgentArtifactService(fixture.Options, store, encoder);
        var artifact = fixture.PreviewSeeds[0];
        var first = await service.GetPreviewAsync(artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentArtifactReadStatus.Found, first.Status);
        var firstChecksum = first.ChecksumSha256;
        for (var index = 0; index < 20; index++)
        {
            var cached = await service.GetPreviewAsync(artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentArtifactReadStatus.Found, cached.Status);
            Assert.AreEqual(firstChecksum, cached.ChecksumSha256);
            CollectionAssert.AreEqual(first.Content.ToArray(), cached.Content.ToArray());
        }
        Assert.AreEqual(1, encoder.Count);

        var concurrent = await Task.WhenAll(Enumerable.Range(0, 50).Select(async _ =>
            await service.GetPreviewAsync(artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false)))
            .ConfigureAwait(false);
        Assert.IsTrue(concurrent.All(static result => result.Status == CameraAgentArtifactReadStatus.Found));
        Assert.HasCount(1, concurrent.Select(static result => result.ChecksumSha256).Distinct().ToArray());
        Assert.IsTrue(concurrent.All(result => result.Content.Span.SequenceEqual(first.Content.Span)));
        Assert.AreEqual(1, encoder.Count);
        return new PreviewCacheEvidence(
            1,
            20,
            50,
            concurrent.Count(static result => result.Status == CameraAgentArtifactReadStatus.Found),
            0,
            encoder.Count,
            first.Content.Length,
            firstChecksum!,
            "CameraAgentArtifactServiceTests.PreviewAllowsOnlyDurablePreviewRolesAndCachesByChecksumAsync",
            "CameraAgentArtifactServiceTests.PreviewDimensionsBytesAndConcurrencyAreBoundedAsync");
    }

    private static void AssertScaling(IReadOnlyList<GalleryMeasurement> measurements)
    {
        foreach (var captureCount in CaptureCounts)
        {
            foreach (var concurrency in ConcurrencyLevels)
            {
                var first = Find(measurements, captureCount, "first", concurrency);
                var later = Find(measurements, captureCount, "later", concurrency);
                Assert.IsLessThanOrEqualTo(
                    first.MedianMilliseconds * 4 + 25,
                    later.MedianMilliseconds,
                    $"Later-page cost scaled with preceding history for {captureCount}/{concurrency}.");
            }
        }

        foreach (var concurrency in ConcurrencyLevels)
        {
            var small = Find(measurements, 1_000, "first", concurrency);
            var large = Find(measurements, 10_000, "first", concurrency);
            Assert.IsLessThanOrEqualTo(
                small.AllocatedBytes * 2 + 4 * 1024 * 1024,
                large.AllocatedBytes,
                $"Page allocation scaled materially with gallery history at concurrency {concurrency}.");
        }
    }

    private static GalleryMeasurement Find(
        IReadOnlyList<GalleryMeasurement> measurements,
        int captureCount,
        string scenario,
        int concurrency)
        => measurements.Single(item => item.CaptureCount == captureCount &&
            item.Scenario == scenario && item.Concurrency == concurrency);

    private static void AssertBrowserP95Admissible(HostContention contention, double p95, string scenario)
    {
        if (contention.LoadPerCore is { } loadPerCore && loadPerCore > MaximumAdmissibleLoadPerCore)
        {
            Assert.Inconclusive(
                $"Host contention makes this browser latency measurement inadmissible. The one-minute load " +
                $"average before the workload started was {loadPerCore:F2} per core across " +
                $"{contention.ProcessorCount} cores, above the {MaximumAdmissibleLoadPerCore:F2} admissibility " +
                $"ceiling. Browser-driven p95 moves by more than 40% under contention, so the {p95:F1} ms " +
                $"measured for {scenario} describes this host rather than the product, whether it passes or " +
                $"fails the {MaximumP95Milliseconds:F0} ms bound. This is not a latency regression and the run " +
                $"proves nothing about performance. Re-run on a quiet host.");
        }

        Assert.IsLessThanOrEqualTo(MaximumP95Milliseconds, p95, scenario);
    }

    private static HostContention SampleHostContention()
    {
        var processorCount = Environment.ProcessorCount;

        // /proc/loadavg is Linux-only. Where it is unavailable the precondition cannot be evaluated and
        // the bound is asserted exactly as it was before, so no platform loses coverage it already had.
        try
        {
            if (File.Exists("/proc/loadavg"))
            {
                var fields = File.ReadAllText("/proc/loadavg")
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 0
                    && double.TryParse(fields[0], System.Globalization.CultureInfo.InvariantCulture, out var oneMinute)
                    && processorCount > 0)
                {
                    return new HostContention(oneMinute / processorCount, processorCount);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return new HostContention(null, processorCount);
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static long ReadRssBytes()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/self/statm"))
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return process.WorkingSet64;
        }
        var fields = File.ReadAllText("/proc/self/statm").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && long.TryParse(fields[1], out var pages)
            ? checked(pages * Environment.SystemPageSize)
            : 0;
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    private static object ReadRevisionEvidence()
    {
        var root = GetRepositoryRoot();
        var head = RunGit(root, "rev-parse HEAD").Trim();
        var trackedDiff = RunGitBytes(root, "diff --binary HEAD");
        var status = RunGitBytes(root, "status --porcelain=v1 --untracked-files=all");
        using var combined = new MemoryStream();
        combined.Write(trackedDiff);
        combined.Write(status);
        var untracked = System.Text.Encoding.UTF8.GetString(status)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(static line => line.StartsWith("?? ", StringComparison.Ordinal))
            .Select(static line => line[3..])
            .Order(StringComparer.Ordinal);
        foreach (var relativePath in untracked)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relativePath);
            combined.Write(pathBytes);
            combined.Write(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relativePath))));
        }
        return new
        {
            Head = head,
            Dirty = status.Length > 0,
            DirtyDiffSha256 = Convert.ToHexString(SHA256.HashData(combined.ToArray())),
            Algorithm = "SHA-256(tracked binary diff, porcelain status, and sorted untracked path/content hashes)"
        };
    }

    private static string RunGit(string workingDirectory, string arguments) =>
        System.Text.Encoding.UTF8.GetString(RunGitBytes(workingDirectory, arguments));

    private static byte[] RunGitBytes(string workingDirectory, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start git for evidence revision binding.");
        using var memory = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(memory);
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Could not bind performance evidence to the repository revision.");
        }
        return memory.ToArray();
    }

    private sealed record PerformanceScenario(
        string Name,
        Func<SqliteCameraAgentGallery, CancellationToken, Task<object>> Execute);

    private sealed record HostContention(double? LoadPerCore, int ProcessorCount);

    private sealed record GalleryCursors(string Middle, string Later);

    private sealed record ResponseMeasurement(double ElapsedMilliseconds, int Bytes, string Sha256);

    private sealed record GalleryMeasurement(
        int CaptureCount,
        string Scenario,
        int Concurrency,
        double WallMilliseconds,
        double MedianMilliseconds,
        double P95Milliseconds,
        int MinimumResponseBytes,
        int MaximumResponseBytes,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        string ResponseSha256);

    private sealed record ApiMeasurement(
        int CaptureCount,
        int Concurrency,
        int ResponseBytes,
        double MedianMilliseconds,
        double P95Milliseconds,
        double ThroughputRequestsPerSecond,
        string ResponseSha256);

    private sealed record BrowserRenderResult(
        double ElapsedMilliseconds,
        int RenderedBytes,
        int CardCount);

    private sealed record BrowserRenderMeasurement(
        int CaptureCount,
        int Concurrency,
        int SampleCount,
        double WallMilliseconds,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds,
        int MinimumRenderedBytes,
        int MaximumRenderedBytes,
        int CardCount,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        long PerSessionWorkingSetGrowthBytes,
        double ThroughputPagesPerSecond);

    private sealed record BrowserSessionMeasurements(
        IReadOnlyList<BrowserRenderMeasurement> Renders,
        IReadOnlyList<BrowserPreviewFailureMeasurement> PreviewFailures);

    private sealed record BrowserPreviewFailureMeasurement(
        int CaptureCount,
        int Concurrency,
        int SampleCount,
        double WallMilliseconds,
        double MedianMilliseconds,
        double? P95Milliseconds,
        double MaximumMilliseconds,
        int FailedPreviewsPerPage,
        double CpuMilliseconds,
        long AllocatedBytes,
        long WorkingSetBeforeBytes,
        long WorkingSetAfterBytes,
        long RssBeforeBytes,
        long RssAfterBytes,
        long PerSessionWorkingSetGrowthBytes);

    private sealed record SqlPlanEvidence(
        int CaptureCount,
        string Name,
        string Statement,
        IReadOnlyList<string> Plan);

    private sealed record CorrectnessEvidence(
        int CaptureCount,
        int ObservedCount,
        int DistinctCount,
        long FirstSequence,
        long LastSequence,
        string SequenceSha256,
        string Invariants);

    private sealed record PreviewCacheEvidence(
        int WarmEncodes,
        int SequentialCacheReads,
        int ConcurrentRequests,
        int ConcurrentFound,
        int ConcurrentRejectedByBound,
        int TotalEncoderCalls,
        int EncodedBytes,
        string EncodedSha256,
        string CacheTest,
        string ConcurrencyBoundTest);

    private sealed class CountingPreviewEncoder : ICameraAgentPreviewEncoder
    {
        internal int Count { get; private set; }

        public CameraAgentEncodedPreview Encode(
            FrameLayoutDescriptor layout,
            ReadOnlyMemory<byte> payload,
            int maximumDimension,
            int maximumEncodedBytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Count++;
            var scale = Math.Min(1d, Math.Min(
                (double)maximumDimension / layout.Width,
                (double)maximumDimension / layout.Height));
            return new(
                [0xFF, 0xD8, .. payload.ToArray(), 0xFF, 0xD9],
                Math.Max(1, (int)Math.Floor(layout.Width * scale)),
                Math.Max(1, (int)Math.Floor(layout.Height * scale)));
        }
    }
}
