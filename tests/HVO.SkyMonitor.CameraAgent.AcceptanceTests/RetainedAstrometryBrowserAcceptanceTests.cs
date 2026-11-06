using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Configured async disposal would hide the strongly typed browser and host fixtures.")]
public sealed partial class RetainedAstrometryBrowserAcceptanceTests
{
    private const float DefaultTimeoutMilliseconds = 45_000;
    public TestContext TestContext { get; set; } = null!;

    private static readonly JsonSerializerOptions AstrometryBrowserJson = new() { WriteIndented = true };

    [TestMethod]
    [DataRow("accepted", "Accepted")]
    [DataRow("rejected", "Rejected")]
    [DataRow("unavailable", "Unavailable")]
    [DataRow("budget", "Budget exceeded")]
    [DataRow("no-data", "Unavailable")]
    public async Task RetainedAstrometryUsesRealCaptureBrowserAndBoundedReadsAsync(string scenario, string status)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(status);
        var fixtureRoot = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_VIEWER_FIXTURE_ROOT");
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_BROWSER_EVIDENCE_ROOT");
        Assert.IsNotNull(fixtureRoot, "Generate the real retained fixtures as documented in retained-astrometry.md.");
        Assert.IsNotNull(evidenceRoot, "Provide a fresh registered evidence scratch root.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(fixtureRoot), "Generate the real retained fixtures as documented in retained-astrometry.md.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(evidenceRoot), "Provide a fresh registered evidence scratch root.");
        var source = Path.Combine(fixtureRoot, scenario);
        var identity = JsonSerializer.Deserialize<AstrometryBrowserFixture>(
            await File.ReadAllTextAsync(Path.Combine(source, "viewer-fixture.json")).ConfigureAwait(false))!;
        Assert.AreEqual(scenario, identity.Scenario);
        Assert.IsTrue(identity.HostCompatible, "Regenerate host-compatible fixtures; do not bypass native recovery.");
        var output = Path.Combine(evidenceRoot, scenario);
        Assert.IsFalse(Directory.Exists(output), "Preserve previous evidence; select a fresh evidence directory.");
        Directory.CreateDirectory(output);
        var originalHashes = await HashAstrometryFixtureAsync(source).ConfigureAwait(false);
        Assert.AreEqual(identity.RawChecksum, originalHashes["raw.bin"]);
        await File.WriteAllTextAsync(Path.Combine(output, "original-baseline.json"), JsonSerializer.Serialize(originalHashes, AstrometryBrowserJson)).ConfigureAwait(false);

        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        Dictionary<string, string>? preparedHashes = null;
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
        {
            // Freeze acquisition and retention in the disposable test host. Authentication, gallery,
            // artifact verification, retained reconstruction, composition and Razor/JS remain real.
            var paused = new HashSet<Type>
            {
                typeof(CameraCaptureService), typeof(RetentionBackgroundService), typeof(CaptureDistributionService),
                typeof(DerivedProductReconciliationService), typeof(ProjectedSceneStageReconciliationService),
                typeof(ProcessingReplayWorker), typeof(TransientWorkerService)
            };
            foreach (var descriptor in services.Where(value => value.ServiceType == typeof(IHostedService) &&
                paused.Contains(value.ImplementationType ?? value.ImplementationFactory?.Method.ReturnType ?? typeof(void))).ToArray()) services.Remove(descriptor);
        }, prepareRoot: async root =>
        {
            await RetainedAstrometryBrowserFixture.CopyAsync(source, root).ConfigureAwait(false);
            preparedHashes = await HashAstrometryFixtureAsync(root).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(output, "prepared-baseline.json"), JsonSerializer.Serialize(new { root, files = preparedHashes }, AstrometryBrowserJson)).ConfigureAwait(false);
        }, scratchRoot: evidenceRoot, preserveRoot: true).ConfigureAwait(false);
        Assert.IsNotNull(preparedHashes);
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext, successfulEvidenceDirectory: Path.Combine(output, "diagnostics"));
        await using var context = await diagnostics.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new() { Width = 1440, Height = 1000 }
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        page.SetDefaultTimeout(DefaultTimeoutMilliseconds);
        var api = $"/api/v1/operations/gallery/{identity.CaptureId:D}";
        await AssertAstrometryStatusAsync(context.APIRequest.GetAsync(api + "/astrometry"), 401).ConfigureAwait(false);
        await LoginAsync(page, CameraAgentKestrelFixture.OwnerEmail, CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await using (var viewer = await diagnostics.NewContextAsync(new() { BaseURL = host.BaseAddress.ToString() }).ConfigureAwait(false))
        {
            var viewerPage = await viewer.NewPageAsync().ConfigureAwait(false);
            await LoginAsync(viewerPage, CameraAgentKestrelFixture.NonOwnerEmail, CameraAgentKestrelFixture.NonOwnerPassword).ConfigureAwait(false);
            await AssertAstrometryStatusAsync(viewer.APIRequest.GetAsync(api + "/astrometry"), 403).ConfigureAwait(false);
            await diagnostics.ReleaseAsync(viewer).ConfigureAwait(false);
        }
        await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
        await page.Locator(".app-frame nav a[href='/operations']").First.ClickAsync().ConfigureAwait(false);
        await page.Locator(".operations-layout").WaitForAsync().ConfigureAwait(false);
        var reads = scenario == "accepted"
            ? await RecordAstrometryReadBoundsAsync(host, context, identity, output).ConfigureAwait(false) : null;
        var expectedPresentation = (await host.Services.GetRequiredService<ICameraAgentLayeredPresentationService>()
            .GetAsync(identity.CaptureId, CancellationToken.None).ConfigureAwait(false)).Presentation!;
        Assert.AreEqual(identity.ManifestIdentitySha256, expectedPresentation.ManifestIdentitySha256);
        var renderSamples = new List<object>();
        var browserMetrics = await context.NewCDPSessionAsync(page).ConfigureAwait(false);
        await browserMetrics.SendAsync("Performance.enable", new Dictionary<string, object> { ["timeDomain"] = "threadTicks" }).ConfigureAwait(false);
        foreach (var route in scenario == "accepted" ? new[] { "/", $"/gallery/{identity.CaptureId:D}" } : new[] { $"/gallery/{identity.CaptureId:D}" })
        {
            var browserBefore = await ReadAstrometryBrowserMetricsAsync(browserMetrics).ConfigureAwait(false);
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            var rssBefore = process.WorkingSet64;
            var started = Stopwatch.GetTimestamp();
            await page.GotoAsync(route).ConfigureAwait(false);
            await WaitForInteractiveShellAsync(page).ConfigureAwait(false);
            await page.Locator(".astrometry-card .state-chip").Filter(new() { HasText = status }).WaitForAsync().ConfigureAwait(false);
            await page.Locator(".stage-selector button").Filter(new() { Has = page.Locator("span", new() { HasTextString = "Processed" }) }).ClickAsync().ConfigureAwait(false);
            await page.WaitForFunctionAsync("() => { const image = document.querySelector('.sky-layer-canvas > img'); return image?.naturalWidth === 512 && image.naturalHeight === 512; }").ConfigureAwait(false);
            await page.WaitForFunctionAsync("expected => document.querySelector('.sky-layer-workspace')?.dataset.presentationIdentity === expected", expectedPresentation.PresentationIdentitySha256).ConfigureAwait(false);
            var renderMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Assert.IsLessThan(10_000d, renderMilliseconds, "The frozen 512-square viewer must become interactive in ten seconds.");
            var associations = page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Measured stellar associations" }).Locator("input");
            var residuals = page.Locator(".sky-layer-controls label").Filter(new() { HasText = "Astrometric residuals" }).Locator("input");
            var card = page.Locator(".astrometry-card");
            if (scenario == "accepted")
            {
                await page.GetByLabel("X pixel", new() { Exact = true }).WaitForAsync().ConfigureAwait(false);
                Assert.IsFalse(await page.GetByLabel("X pixel", new() { Exact = true }).IsDisabledAsync().ConfigureAwait(false));
                Assert.Contains("MeasuredFit", await card.InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);
                Assert.Contains("engineering; not probability", await card.InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);
                await page.Locator(".astrometry-provenance summary").ClickAsync().ConfigureAwait(false);
                Assert.Contains(identity.CaptureId.ToString("D"), await page.Locator(".astrometry-provenance").InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);
                // Clear persisted visibility between Current Sky and archive, then exercise the real JS
                // change handlers independently. Checking a checkbox alone would miss a broken SVG binding.
                await associations.UncheckAsync().ConfigureAwait(false);
                await residuals.UncheckAsync().ConfigureAwait(false);
                var off = await ScreenshotAstrometryAsync(page, output, route == "/" ? "current-off" : "archive-off").ConfigureAwait(false);
                var offBrowser = await ReadAstrometryBrowserMetricsAsync(browserMetrics).ConfigureAwait(false);
                process.Refresh();
                var offCpu = process.TotalProcessorTime;
                var offAllocations = GC.GetTotalAllocatedBytes(true);
                var offRss = process.WorkingSet64;
                renderSamples.Add(new
                {
                    route,
                    phase = "navigate-and-render-diagnostics-off",
                    wallMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    hostCpuMilliseconds = (offCpu - cpuBefore).TotalMilliseconds,
                    hostAllocatedBytes = offAllocations - allocatedBefore,
                    hostRssBefore = rssBefore,
                    hostRssAfter = offRss,
                    browserBefore,
                    browserAfter = offBrowser
                });
                var enableStarted = Stopwatch.GetTimestamp();
                await associations.CheckAsync().ConfigureAwait(false);
                await AssertAstrometryGroupAsync(page, associations, true).ConfigureAwait(false);
                await AssertAstrometryGroupAsync(page, residuals, false).ConfigureAwait(false);
                await associations.UncheckAsync().ConfigureAwait(false);
                await residuals.CheckAsync().ConfigureAwait(false);
                await AssertAstrometryGroupAsync(page, associations, false).ConfigureAwait(false);
                await AssertAstrometryGroupAsync(page, residuals, true).ConfigureAwait(false);
                await page.GetByLabel("X pixel", new() { Exact = true }).FillAsync("280").ConfigureAwait(false);
                await page.GetByLabel("Y pixel", new() { Exact = true }).FillAsync("240").ConfigureAwait(false);
                await page.GetByRole(AriaRole.Button, new() { Name = "Pixel to sky", Exact = true }).ClickAsync().ConfigureAwait(false);
                await page.Locator(".astrometry-calculator-result").Filter(new() { HasText = "RA " }).WaitForAsync().ConfigureAwait(false);
                var sky = await page.Locator(".astrometry-calculator-result").InnerTextAsync().ConfigureAwait(false);
                Assert.Contains("altitude", sky, StringComparison.Ordinal);
                Assert.Contains("Point uncertainty withheld", sky, StringComparison.Ordinal);
                var on = await ScreenshotAstrometryAsync(page, output, route == "/" ? "current-residual-calculator" : "archive-residual-calculator").ConfigureAwait(false);
                var onBrowser = await ReadAstrometryBrowserMetricsAsync(browserMetrics).ConfigureAwait(false);
                process.Refresh();
                renderSamples.Add(new
                {
                    route,
                    phase = "toggle-associations-residuals-calculate-and-render-on",
                    wallMilliseconds = Stopwatch.GetElapsedTime(enableStarted).TotalMilliseconds,
                    hostCpuMilliseconds = (process.TotalProcessorTime - offCpu).TotalMilliseconds,
                    hostAllocatedBytes = GC.GetTotalAllocatedBytes(true) - offAllocations,
                    hostRssBefore = offRss,
                    hostRssAfter = process.WorkingSet64,
                    browserBefore = offBrowser,
                    browserAfter = onBrowser,
                    browserTaskCpuMilliseconds = (onBrowser["TaskDuration"] - offBrowser["TaskDuration"]) * 1000
                });
                Assert.IsLessThan(5d, onBrowser["TaskDuration"] - offBrowser["TaskDuration"], "Measured layer interaction CPU budget in one document.");
                await page.GetByRole(AriaRole.Button, new() { Name = "Sky to pixel", Exact = true }).ClickAsync().ConfigureAwait(false);
                await page.Locator(".astrometry-calculator-result").Filter(new() { HasText = "source pixels" }).WaitForAsync().ConfigureAwait(false);
                Assert.AreEqual(280d, double.Parse(await page.GetByLabel("X pixel", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-7);
                Assert.AreEqual(240d, double.Parse(await page.GetByLabel("Y pixel", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-7);
                await page.GetByRole(AriaRole.Combobox, new() { Name = "Coordinates" }).SelectOptionAsync("horizontal").ConfigureAwait(false);
                // Exercise the human-visible horizontal values as inputs, including their display precision.
                var horizontal = new
                {
                    AltitudeDegrees = double.Parse(sky.Split("altitude ", StringSplitOptions.None)[1].Split('°')[0], CultureInfo.InvariantCulture),
                    AzimuthDegrees = double.Parse(sky.Split("azimuth ", StringSplitOptions.None)[1].Split('°')[0], CultureInfo.InvariantCulture)
                };
                await page.GetByLabel("Altitude degrees", new() { Exact = true }).FillAsync(horizontal.AltitudeDegrees.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await page.GetByLabel("Azimuth degrees", new() { Exact = true }).FillAsync(horizontal.AzimuthDegrees.ToString("R", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await page.GetByLabel("X pixel", new() { Exact = true }).FillAsync("1").ConfigureAwait(false);
                await page.GetByLabel("Y pixel", new() { Exact = true }).FillAsync("1").ConfigureAwait(false);
                await page.GetByRole(AriaRole.Button, new() { Name = "Sky to pixel", Exact = true }).ClickAsync().ConfigureAwait(false);
                await page.WaitForFunctionAsync("() => Math.abs(Number(document.querySelector('.pixel-sky-calculator input').value) - 280) < 1e-7").ConfigureAwait(false);
                Assert.AreEqual(280d, double.Parse(await page.GetByLabel("X pixel", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-7);
                Assert.AreEqual(240d, double.Parse(await page.GetByLabel("Y pixel", new() { Exact = true }).InputValueAsync().ConfigureAwait(false), CultureInfo.InvariantCulture), 1e-7);
                renderSamples.Add(new { route, renderMilliseconds, off, on, sky, horizontal });
                if (route != "/") await SaveAstrometrySelectionAsync(page, host, identity, output).ConfigureAwait(false);
            }
            else
            {
                if (scenario == "no-data") Assert.Contains("No astrometric assessment was retained for this capture.", await card.InnerTextAsync().ConfigureAwait(false), StringComparison.Ordinal);
                Assert.IsTrue(await page.GetByRole(AriaRole.Button, new() { Name = "Pixel to sky", Exact = true }).IsDisabledAsync().ConfigureAwait(false));
                Assert.IsTrue(await associations.IsDisabledAsync().ConfigureAwait(false));
                Assert.IsTrue(await residuals.IsDisabledAsync().ConfigureAwait(false));
                await ScreenshotAstrometryAsync(page, output, scenario).ConfigureAwait(false);
                renderSamples.Add(new { route, renderMilliseconds });
            }
            var browserAfter = await ReadAstrometryBrowserMetricsAsync(browserMetrics).ConfigureAwait(false);
            process.Refresh();
            renderSamples.Add(new
            {
                route,
                phase = "navigate-toggle-calculate-screenshot-and-archive-export",
                browserBefore,
                browserAfter,
                browserTimeDomain = "threadTicks",
                browserProcessCpuMilliseconds = (browserAfter["ProcessTime"] - browserBefore["ProcessTime"]) * 1000,
                hostCpuMilliseconds = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds,
                hostAllocatedBytes = GC.GetTotalAllocatedBytes(true) - allocatedBefore,
                hostRssBefore = rssBefore,
                hostRssAfter = process.WorkingSet64
            });
            Assert.IsLessThan(64d * 1024 * 1024, browserAfter["JSHeapUsedSize"], "Retained viewer Chromium JS heap ceiling.");
            Assert.IsTrue(browserAfter["ProcessTime"] >= browserBefore["ProcessTime"], "Renderer process counter must remain comparable across navigation.");
            Assert.IsLessThan(5d, browserAfter["ProcessTime"] - browserBefore["ProcessTime"], "Retained viewer renderer process CPU ceiling (seconds).");
        }
        await browserMetrics.DetachAsync().ConfigureAwait(false);

        await page.SetViewportSizeAsync(390, 844).ConfigureAwait(false);
        Assert.IsTrue(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth + 1").ConfigureAwait(false), "Viewer must fit a 390px viewport.");
        await ScreenshotAstrometryAsync(page, output, "narrow").ConfigureAwait(false);
        if (scenario == "accepted")
        {
            await page.GetByLabel("X pixel", new() { Exact = true }).FocusAsync().ConfigureAwait(false);
            await page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
            Assert.AreEqual("Y pixel", await page.EvaluateAsync<string>("() => document.activeElement.closest('label').textContent.trim()").ConfigureAwait(false));
        }
        var actualHashes = await HashAstrometryFixtureAsync(host.Root, preparedHashes.Keys).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(preparedHashes.ToArray(), actualHashes.ToArray(), "Every prepared non-database fixture file must remain byte-identical after read, UI and export.");
        CollectionAssert.AreEquivalent(originalHashes.ToArray(), (await HashAstrometryFixtureAsync(source).ConfigureAwait(false)).ToArray(), "Never change the retained evidence source.");
        await File.WriteAllTextAsync(Path.Combine(output, "browser-evidence.json"), JsonSerializer.Serialize(new
        {
            schema = "issue-1644-browser-v1",
            measuredUtc = DateTimeOffset.UtcNow,
            revision = Environment.GetEnvironmentVariable("HVO_EVIDENCE_REVISION"),
            scenario,
            status,
            identity,
            browser = browser.Version,
            reads,
            renderSamples,
            originalFixtureFilesSha256 = originalHashes,
            unchangedFilesSha256 = actualHashes,
            limitation = "512x512 seeded virtual software acceptance; no calibrated probability, physical accuracy or full-resolution qualification."
        }, AstrometryBrowserJson)).ConfigureAwait(false);
        await diagnostics.CompleteAsync().ConfigureAwait(false);
        var retainedDiagnostics = Directory.GetFiles(Path.Combine(output, "diagnostics"));
        foreach (var suffix in new[] { ".trace.zip", ".dom.html", ".console.log", ".page-errors.log", ".network.log" })
            Assert.AreEqual(2, retainedDiagnostics.Count(path => path.EndsWith(suffix, StringComparison.Ordinal)), "Retain owner and nonowner diagnostics: " + suffix);
    }

    private async Task<double> ScreenshotAstrometryAsync(IPage page, string output, string name)
    {
        var start = Stopwatch.GetTimestamp();
        var path = Path.Combine(output, name + ".png");
        // Put sticky navigation at the top of the full-page artifact instead of across the
        // calculator's current scroll offset. This is ordinary scrolling, with no style edits.
        await page.EvaluateAsync("() => { document.activeElement?.blur(); window.scrollTo(0, 0); }").ConfigureAwait(false);
        await page.ScreenshotAsync(new() { Path = path, FullPage = true }).ConfigureAwait(false);
        TestContext.AddResultFile(path);
        var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Assert.IsLessThan(5000d, elapsed, "512-square viewer full-page screenshot must complete within 5 seconds.");
        if (name.EndsWith("residual-calculator", StringComparison.Ordinal))
        {
            foreach (var (selector, suffix) in new[] { (".sky-figure", "image"), (".astrometry-card", "card") })
            {
                var detail = Path.Combine(output, name + "-" + suffix + ".png");
                await page.Locator(selector).ScreenshotAsync(new() { Path = detail }).ConfigureAwait(false);
                TestContext.AddResultFile(detail);
            }
        }
        return elapsed;
    }

    private static async Task AssertAstrometryGroupAsync(IPage page, ILocator checkbox, bool visible)
    {
        var group = await checkbox.GetAttributeAsync("data-layer-target").ConfigureAwait(false);
        Assert.IsNotNull(group);
        await page.WaitForFunctionAsync("({id, visible}) => { const g = document.getElementById(id); return !!g && (getComputedStyle(g).display !== 'none') === visible; }", new { id = group, visible }).ConfigureAwait(false);
    }

    private static async Task AssertAstrometryStatusAsync(Task<IAPIResponse> request, int status)
    {
        var response = await request.ConfigureAwait(false);
        try { Assert.AreEqual(status, response.Status, response.Url); }
        finally { await response.DisposeAsync().ConfigureAwait(false); }
    }

    private static async Task<Dictionary<string, string>> HashAstrometryFixtureAsync(string root, IEnumerable<string>? relativePaths = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relative in relativePaths ?? Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".db", StringComparison.Ordinal) && !path.EndsWith("-wal", StringComparison.Ordinal) && !path.EndsWith("-shm", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.Ordinal))
        {
            await using var file = File.OpenRead(Path.Combine(root, relative));
            result.Add(relative, Convert.ToHexString(await SHA256.HashDataAsync(file).ConfigureAwait(false)));
        }
        return result;
    }

    private static async Task SaveAstrometrySelectionAsync(IPage page, CameraAgentKestrelFixture host, AstrometryBrowserFixture identity, string output)
    {
        await page.Locator(".sky-layer-save button").ClickAsync().ConfigureAwait(false);
        var link = page.Locator(".sky-layer-result:visible a").Filter(new() { HasText = "Download original full-resolution stack" });
        await link.WaitForAsync().ConfigureAwait(false);
        var url = (await link.GetAttributeAsync("href").ConfigureAwait(false))!;
        var id = Guid.Parse(new Uri(host.BaseAddress, url).Segments[^2].Trim('/'));
        var product = await host.Services.GetRequiredService<SqliteCaptureProcessingStore>()
            .ReadOutputByArtifactIdAsync(id, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(product);
        Assert.IsNotNull(product.Descriptor);
        Assert.AreEqual(512, product.Descriptor.Layout.Width);
        Assert.AreEqual(512, product.Descriptor.Layout.Height);
        var payload = await File.ReadAllBytesAsync(Path.Combine(host.Root, product.PayloadRelativePath)).ConfigureAwait(false);
        Assert.AreEqual(product.Artifact.ChecksumSha256, PayloadChecksum.ComputeSha256(payload));
        var downloaded = await page.Context.APIRequest.GetAsync(new Uri(host.BaseAddress, url).ToString()).ConfigureAwait(false);
        try
        {
            Assert.AreEqual(200, downloaded.Status);
            CollectionAssert.AreEqual(payload, await downloaded.BodyAsync().ConfigureAwait(false), "Authenticated frozen download must match the durable bytes.");
        }
        finally { await downloaded.DisposeAsync().ConfigureAwait(false); }
        var shown = (await host.Services.GetRequiredService<ICameraAgentLayeredPresentationService>()
            .GetAsync(identity.CaptureId, CancellationToken.None).ConfigureAwait(false)).Presentation!;
        Assert.AreEqual(identity.ManifestIdentitySha256, shown.ManifestIdentitySha256);
        Assert.AreEqual(shown.PresentationIdentitySha256, await page.Locator(".sky-layer-workspace").GetAttributeAsync("data-presentation-identity").ConfigureAwait(false));
        var manifest = await host.Services.GetRequiredService<SqliteCaptureProcessingStore>()
            .ReadOutputByArtifactIdAsync(shown.ManifestArtifactId, CancellationToken.None).ConfigureAwait(false);
        var manifestBytes = await File.ReadAllBytesAsync(Path.Combine(host.Root, manifest!.PayloadRelativePath)).ConfigureAwait(false);
        var document = LayeredPresentationJson.ParseManifest(manifestBytes).Document!;
        var selected = shown.Layers.Single(layer => layer.Kind == AstrometricDiagnosticPresentation.ResidualsLayer);
        var residualArtifact = document.Layers.Single(layer => layer.LayerIdentitySha256 == selected.IdentitySha256).SourceProduct.ArtifactId;
        CollectionAssert.AreEquivalent(new[] { shown.BaseArtifactId, shown.ManifestArtifactId, residualArtifact }, product.Artifact.SourceArtifactIds.ToArray(), "Frozen export must contain only the selected residual layer, its manifest and the base.");
        await File.WriteAllBytesAsync(Path.Combine(output, "frozen-export.bin"), payload).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(output, "frozen-export.json"), JsonSerializer.Serialize(new
        {
            identity.CaptureId,
            product.Artifact,
            product.Descriptor.Layout,
            product.PayloadRelativePath,
            shown.ManifestIdentitySha256,
            selected.IdentitySha256,
            residualArtifact
        }, AstrometryBrowserJson)).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, double>> ReadAstrometryBrowserMetricsAsync(ICDPSession session)
    {
        var result = await session.SendAsync("Performance.getMetrics").ConfigureAwait(false);
        return result!.Value.GetProperty("metrics").EnumerateArray().ToDictionary(
            metric => metric.GetProperty("name").GetString()!, metric => metric.GetProperty("value").GetDouble(), StringComparer.Ordinal);
    }

    private static Task WaitForInteractiveShellAsync(IPage page)
        => page.Locator(".app-frame[data-interactive='true']").WaitForAsync();

    private static async Task LoginAsync(IPage page, string email, string password)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Username or email").FillAsync(email).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(password).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
    }

    [SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Constructed by System.Text.Json from the retained fixture manifest.")]
    private sealed record AstrometryBrowserFixture(string Scenario, Guid CaptureId, Guid RawArtifactId, Guid BaseArtifactId,
        string ManifestIdentitySha256, string BaseChecksum, string RawChecksum, bool HostCompatible);
}
