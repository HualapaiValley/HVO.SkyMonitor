using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using HVO.SkyMonitor.IntegrationTests.Infrastructure;
using HVO.SkyMonitor.LogicHost.Data;
using HVO.SkyMonitor.LogicHost.Services;
using HVO.SkyMonitor.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using SkiaSharp;

namespace HVO.SkyMonitor.IntegrationTests;

[TestClass]
[TestCategory("Manual")]
[DoNotParallelize]
public sealed class CameraArchiveBrowserAcceptanceTests
{
    public TestContext TestContext { get; set; } = null!;
    private static readonly JsonSerializerOptions EvidenceJson = new() { WriteIndented = true };
    private static string EvidenceRoot => Environment.GetEnvironmentVariable("HVO_ISSUE1536_EVIDENCE") ?? "TestResults/issue-1536";

    [TestMethod]
    public async Task Archive_ExactSameTimeReplacementCaptureBackFocusResponsiveAndDenial()
    {
        Directory.CreateDirectory(EvidenceRoot);
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        LogicalCameraArchiveIntegrationTests.Dataset data;
        Guid selected;
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            data = await LogicalCameraArchiveIntegrationTests.SeedAsync(scope.ServiceProvider, 70, equalTimes: true);
            var service = scope.ServiceProvider.GetRequiredService<ILogicalCameraArchiveReadService>();
            var first = await service.ListCapturesAsync(LogicalCameraArchiveIntegrationTests.Principal(data.Owner), data.Observatory, data.Camera, new(), 25, null);
            var second = await service.ListCapturesAsync(LogicalCameraArchiveIntegrationTests.Principal(data.Owner), data.Observatory, data.Camera, new(), 25, first.NextCursor);
            // Retired installation and equal capture times: central UUID, never timestamp, identifies the target.
            var retired = data.Frames[0].LogicalCameraInstallationId;
            selected = second.Items.First(i => i.InstallationId == retired).CaptureId;
            await LogicalCameraArchiveIntegrationTests.AddPreviewAsync(scope.ServiceProvider, data.Frames.Single(f => f.Id == selected), true, LargePreview());
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        await using var diagnostics = new PlaywrightDiagnostics(browser, TestContext);
        await using var context = await diagnostics.NewContextAsync(new() { HasTouch = true, ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        var url = new Uri(fixture.BaseAddress, $"/app/network/observatories/{data.Observatory:D}/cameras/{data.Camera:D}/archive").AbsoluteUri;
        await LoginAsync(page, url);
        await ReadyAsync(page);
        await page.GetByLabel("Captured from (UTC)", new() { Exact = true }).FillAsync("2026-10-01T00:00");
        await page.GetByLabel("Captured until (UTC, exclusive)", new() { Exact = true }).FillAsync("2026-10-02T00:00");
        await page.GetByRole(AriaRole.Button, new() { Name = "Apply archive filters", Exact = true }).ClickAsync();
        await ReadyAsync(page);
        await Assertions.Expect(page.GetByLabel("Captured from (UTC)", new() { Exact = true })).ToHaveValueAsync("2026-10-01T00:00");
        await Assertions.Expect(page.Locator(".archive-items a").First).ToHaveAttributeAsync("href",
            new System.Text.RegularExpressions.Regex("from=2026-10-01", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)));
        var firstItem = await page.Locator(".archive-items a").First.GetAttributeAsync("data-capture");
        await page.GetByRole(AriaRole.Link, new() { Name = "Next archive page", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".archive-items a").First).Not.ToHaveAttributeAsync("data-capture", firstItem!);
        await ReadyAsync(page);
        var pageUrl = page.Url;
        pageUrl.Should().Contain("cursor=");
        var locator = $"#archive-capture-{selected:D}";
        var snapshots = new List<object>();
        foreach (var (width, height) in new[] { (1440, 900), (390, 844), (844, 390), (320, 700) })
        {
            await page.SetViewportSizeAsync(width, height);
            try { await page.Locator(locator).FocusAsync(); }
            catch (TimeoutException)
            {
                TestContext.WriteLine(await page.Locator(".camera-archive").InnerTextAsync());
                TestContext.WriteLine(page.Url);
                throw;
            }
            if (width == 390) await page.Locator(locator).TapAsync();
            else await page.Keyboard.PressAsync("Enter");
            await Assertions.Expect(page.Locator(".archive-exact")).ToContainTextAsync(selected.ToString());
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator(".archive-exact")).ToContainTextAsync(selected.ToString());
            await Assertions.Expect(page.Locator(".camera-archive img")).ToBeVisibleAsync();
            (await page.Locator(".camera-archive img").GetAttributeAsync("src")).Should().Contain("/artifacts/");
            var tree = await page.Locator(".camera-archive").AriaSnapshotAsync();
            tree.Should().Contain("Capture provenance").And.Contain("Back to archive page");
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            await page.ScreenshotAsync(new() { Path = Path.Combine(EvidenceRoot, $"archive-detail-{width}x{height}.png"), FullPage = true });
            await page.GetByRole(AriaRole.Button, new() { Name = "Load bounded capture trace", Exact = true }).ClickAsync();
            try { await page.GetByRole(AriaRole.Region, new() { Name = "Bounded capture trace" }).WaitForAsync(); }
            catch (TimeoutException)
            {
                TestContext.WriteLine(await page.Locator(".scope-shell").InnerTextAsync());
                throw;
            }
            if (width == 844) await page.GoBackAsync();
            else await page.GetByRole(AriaRole.Link, new() { Name = "Back to archive page", Exact = true }).ClickAsync();
            await page.Locator(locator).WaitForAsync();
            await ReadyAsync(page);
            await Assertions.Expect(page.Locator(locator)).ToBeFocusedAsync();
            page.Url.Should().Contain(new Uri(pageUrl).Query.TrimStart('?'));
            (await page.Locator(".archive-items li").CountAsync()).Should().Be(25);
            (await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth")).Should().BeTrue();
            snapshots.Add(new { width, height, Focus = await page.Locator(locator).EvaluateAsync<bool>("e => e === document.activeElement"), Items = 25 });
        }
        // Exact content loss produces metadata-only state without borrowing a sibling image.
        await page.Locator(locator).ClickAsync();
        await Assertions.Expect(page.Locator(".archive-exact")).ToContainTextAsync(selected.ToString());
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator(".camera-archive img")).ToBeVisibleAsync();
        await page.RouteAsync("**/artifacts/*/content", route => route.FulfillAsync(new() { Status = 404 }));
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh archive", Exact = true }).ClickAsync();
        await page.GetByText("The Preview for this capture is unavailable.", new() { Exact = false }).WaitForAsync();
        (await page.Locator(".camera-archive img").CountAsync()).Should().Be(0);
        await Assertions.Expect(page.Locator(".archive-exact")).ToContainTextAsync(selected.ToString());
        await page.UnrouteAsync("**/artifacts/*/content");
        await page.GetByRole(AriaRole.Button, new() { Name = "Retry image", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".camera-archive img")).ToBeVisibleAsync();
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ObservatoryMemberships
                .Where(m => m.ObservatoryId == data.Observatory && m.UserId == data.Owner).ExecuteDeleteAsync();
        }
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh archive", Exact = true }).ClickAsync();
        await page.GetByText("Capture not found or access was removed.", new() { Exact = true }).WaitForAsync();
        (await page.Locator(".camera-archive img").CountAsync()).Should().Be(0);
        (await page.Locator(".archive-exact").CountAsync()).Should().Be(0);
        await File.WriteAllTextAsync(Path.Combine(EvidenceRoot, "browser-acceptance.json"), JsonSerializer.Serialize(new
        {
            Source = Environment.GetEnvironmentVariable("HVO_ISSUE1536_REVISION"),
            Utc = DateTimeOffset.UtcNow,
            History = 70,
            EqualTimes = true,
            RetiredInstallationTarget = selected,
            Viewports = snapshots,
            ContentFailure = "404 clears exact image; retry recovers same artifact",
            MembershipRemoval = "refresh clears image and metadata",
            Browser = browser.Version
        }, EvidenceJson));
        await diagnostics.CompleteAsync();
    }

    [TestMethod]
    public async Task Archive_W3MHistoryBrowserMemoryAndImageCosts()
    {
        Directory.CreateDirectory(EvidenceRoot);
        await using var fixture = await LogicHostUiKestrelFixture.CreateAsync();
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        var results = new List<object>();
        var browserSession = await browser.NewBrowserCDPSessionAsync();
        var preview = LargePreview();
        foreach (var size in new[] { 1_000, 10_000 })
        {
            LogicalCameraArchiveIntegrationTests.Dataset data;
            await using (var scope = fixture.Services.CreateAsyncScope())
            {
                data = await LogicalCameraArchiveIntegrationTests.SeedAsync(scope.ServiceProvider, size);
                await LogicalCameraArchiveIntegrationTests.AddPreviewAsync(scope.ServiceProvider, data.Frames[0], true, preview);
            }
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
            var page = await context.NewPageAsync();
            var path = $"/app/network/observatories/{data.Observatory:D}/cameras/{data.Camera:D}/archive";
            await LoginAsync(page, new Uri(fixture.BaseAddress, path).AbsoluteUri);
            var cdp = await context.NewCDPSessionAsync(page);
            await cdp.SendAsync("Performance.enable");
            var samples = new List<object>();
            var baselineSamples = new List<object>();
            for (var run = 0; run < 6; run++)
            {
                var baselineStart = Stopwatch.GetTimestamp();
                await page.GotoAsync(new Uri(fixture.BaseAddress, $"/app/cameras/{data.Camera:D}").AbsoluteUri);
                await page.Locator("a[href*='app/captures/']").First.WaitForAsync();
                var baselineMs = Stopwatch.GetElapsedTime(baselineStart).TotalMilliseconds;
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                var baselineHeap = await cdp.SendAsync("Runtime.getHeapUsage");
                var baselineProcesses = await BrowserProcessesAsync(browserSession);
                var baselineItems = await page.Locator("a[href*='app/captures/']").CountAsync();
                var baselineDocument = await page.EvaluateAsync<JsonElement>("(() => {const n=performance.getEntriesByType('navigation')[0]; return {encoded:n.encodedBodySize,decoded:n.decodedBodySize,transfer:n.transferSize};})()");
                if (run > 0) baselineSamples.Add(new { RenderMs = baselineMs, Heap = baselineHeap, Processes = baselineProcesses, Items = baselineItems, Document = baselineDocument });
                var start = Stopwatch.GetTimestamp();
                await page.GotoAsync(new Uri(fixture.BaseAddress, path).AbsoluteUri);
                await ReadyAsync(page);
                var renderMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                (await page.Locator(".archive-items li").CountAsync()).Should().Be(25);
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                var heap = await cdp.SendAsync("Runtime.getHeapUsage");
                var dom = await cdp.SendAsync("Memory.getDOMCounters");
                var processes = await BrowserProcessesAsync(browserSession);
                var document = await page.EvaluateAsync<JsonElement>("(() => {const n=performance.getEntriesByType('navigation')[0]; return {encoded:n.encodedBodySize,decoded:n.decodedBodySize,transfer:n.transferSize};})()");
                var archiveResources = await page.EvaluateAsync<JsonElement>("performance.getEntriesByType('resource').filter(r=>r.name.includes('/artifacts/')).map(r=>({encoded:r.encodedBodySize,decoded:r.decodedBodySize}))");
                archiveResources.GetArrayLength().Should().Be(0);
                var exact = page.Locator($"#archive-capture-{data.Frames[0].Id:D}");
                start = Stopwatch.GetTimestamp();
                await exact.ClickAsync();
                await Assertions.Expect(page.Locator(".archive-exact")).ToContainTextAsync(data.Frames[0].Id.ToString());
                await ReadyAsync(page);
                await page.Locator(".camera-archive img").WaitForAsync();
                await page.Locator(".camera-archive img").EvaluateAsync("async img => { await img.decode(); await new Promise(requestAnimationFrame); }");
                var imageMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                var imageHeap = await cdp.SendAsync("Runtime.getHeapUsage");
                var imageProcesses = await BrowserProcessesAsync(browserSession);
                var image = await page.Locator(".camera-archive img").EvaluateAsync<JsonElement>("img => ({width:img.naturalWidth,height:img.naturalHeight,src:img.getAttribute('src')})");
                var resources = await page.EvaluateAsync<JsonElement>("performance.getEntriesByType('resource').filter(r=>r.name.includes('/artifacts/')).map(r=>({encoded:r.encodedBodySize,decoded:r.decodedBodySize,transfer:r.transferSize,duration:r.duration}))");
                if (run > 0) samples.Add(new { Run = run, ArchiveRenderMs = renderMs, BrowserHeap = heap, Processes = processes, Dom = dom, Document = document, ImageRenderMs = imageMs, ImageHeap = imageHeap, ImageProcesses = imageProcesses, Image = image, ImageResources = resources });
            }
            await page.GetByRole(AriaRole.Link, new() { Name = "Back to archive page", Exact = true }).ClickAsync();
            var paging = new List<object>();
            await page.Locator(".archive-items a").First.WaitForAsync();
            await ReadyAsync(page);
            for (var index = 0; index < 10; index++)
            {
                var previousFirst = await page.Locator(".archive-items a").First.GetAttributeAsync("data-capture");
                await page.GetByRole(AriaRole.Link, new() { Name = "Next archive page", Exact = true }).ClickAsync();
                await Assertions.Expect(page.Locator(".archive-items a").First).Not.ToHaveAttributeAsync("data-capture", previousFirst!);
                await ReadyAsync(page);
                (await page.Locator(".archive-items li").CountAsync()).Should().Be(25);
                await cdp.SendAsync("HeapProfiler.collectGarbage");
                paging.Add(new { Page = index + 2, Heap = await cdp.SendAsync("Runtime.getHeapUsage"), Dom = await cdp.SendAsync("Memory.getDOMCounters"), Processes = await BrowserProcessesAsync(browserSession) });
            }
            results.Add(new
            {
                HistoryRows = size,
                PageSize = 25,
                Warmup = 1,
                Operations = 5,
                Samples = samples,
                NearestExistingCameraPage = baselineSamples,
                ConsecutivePages = paging
            });
        }
        await File.WriteAllTextAsync(Path.Combine(EvidenceRoot, "browser-costs.json"), JsonSerializer.Serialize(new
        {
            Source = Environment.GetEnvironmentVariable("HVO_ISSUE1536_REVISION"),
            Utc = DateTimeOffset.UtcNow,
            Browser = browser.Version,
            PreviewWidth = 1936,
            PreviewHeight = 1216,
            PreviewEncodedBytes = preview.Length,
            PreviewSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(preview)),
            Baseline = "Unchanged existing /app/cameras/{cameraId}, first capture links available; includes installation detail and 50-item page, unlike interactive 25-item scoped archive. Nearest-path context, not equivalent timing or a speedup claim.",
            Method = "W3M 1k/10k across two installations, page 25; one warmup + five full navigation/detail/decode cycles, Chromium CDP post-GC heap/DOM and native per-process RSS/CPU snapshots; no automatic page drain",
            Results = results
        }, EvidenceJson));
    }

    private sealed record BrowserProcessSample(string Kind, double CpuSeconds, long? WorkingSetBytes);
    private static async Task<IReadOnlyList<BrowserProcessSample>> BrowserProcessesAsync(ICDPSession browserSession)
    {
        var result = await browserSession.SendAsync("SystemInfo.getProcessInfo");
        var samples = new List<BrowserProcessSample>();
        foreach (var entry in result!.Value.GetProperty("processInfo").EnumerateArray())
        {
            long? rss = null;
            try
            {
                using var process = Process.GetProcessById(entry.GetProperty("id").GetInt32());
                rss = process.WorkingSet64;
            }
            catch (ArgumentException) { /* Process exited between the two samples. */ }
            catch (InvalidOperationException) { /* Process exited between the two samples. */ }
            catch (System.ComponentModel.Win32Exception) { /* Unavailable process is recorded explicitly. */ }
            samples.Add(new(entry.GetProperty("type").GetString()!, entry.GetProperty("cpuTime").GetDouble(), rss));
        }
        return samples;
    }

    private static async Task ReadyAsync(IPage page)
    {
        await page.Locator(".scope-shell[data-scope-interactive='true'][aria-busy='false'] .camera-archive[data-scope-interactive='true'][aria-busy='false']").WaitForAsync();
    }
    private static async Task LoginAsync(IPage page, string url)
    {
        await page.GotoAsync(url);
        await page.GetByLabel("Email").FillAsync(TestUsers.Operator.Email);
        await page.GetByLabel("Password").FillAsync(TestUsers.Operator.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await ReadyAsync(page);
    }
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded synthetic image pixels, not a security boundary.")]
    private static byte[] LargePreview()
    {
        using var bitmap = new SKBitmap(1936, 1216);
        var noise = new Random(1536);
        for (var y = 0; y < bitmap.Height; y++)
            for (var x = 0; x < bitmap.Width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)noise.Next(8, 40), (byte)noise.Next(8, 40), (byte)noise.Next(16, 56)));
        using var canvas = new SKCanvas(bitmap);
        using var paint = new SKPaint();
        paint.Color = SKColors.White;
        for (var star = 0; star < 1000; star++) canvas.DrawCircle(noise.Next(bitmap.Width), noise.Next(bitmap.Height), 1, paint);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}
