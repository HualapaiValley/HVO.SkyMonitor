using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>Real component/CSS and disposable-host regressions with synthetic records; no physical camera or production data.</summary>
[TestClass]
[TestCategory("Integration")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Keep typed browser and renderer instances during async disposal.")]
public sealed class ObservingDayTimelineBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1440, 900)]
    [DataRow(390, 844)]
    public async Task CloseMarkersActivateTheirOwnVisibleGlyphAndKeepKeyboardFocus(int width, int height)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.LaunchOrInconclusiveAsync().ConfigureAwait(false);
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = width, Height = height } }).ConfigureAwait(false);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var css = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObservingDayTimeline.razor.css")).ConfigureAwait(false);
        var day = ObservingDayCalendar.Create("America/Phoenix").Resolve(new DateOnly(2026, 7, 21));
        var older = Candidate(day.StartUtc.AddHours(12), Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var newerId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var evidence = new List<object>();

        // Measure the real responsive track before converting pixel separations into observation-axis instants.
        await SetTimelineAsync([older]).ConfigureAwait(false);
        var track = await page.Locator(".timeline-track--markers").BoundingBoxAsync().ConfigureAwait(false);
        Assert.IsNotNull(track);
        foreach (var separation in new[] { 10, 15, 20, 30, 43 })
        {
            var newer = Candidate(older.CreatedUtc + TimeSpan.FromTicks((long)(day.Duration.Ticks * separation / track.Width)), newerId);
            // Production projection order: newest first, so the older sibling paints last.
            await SetTimelineAsync([newer, older]).ConfigureAwait(false);
            await page.EvaluateAsync("""
                () => document.addEventListener('click', event => {
                    const link = event.target.closest('a');
                    if (link) { event.preventDefault(); document.body.dataset.clicked = link.getAttribute('href'); }
                })
                """).ConfigureAwait(false);
            foreach (var candidate in new[] { newer, older })
            {
                var href = $"/transients/{candidate.CandidateId:D}";
                var marker = page.Locator($".timeline-marker[href='{href}']");
                var box = await marker.BoundingBoxAsync().ConfigureAwait(false);
                Assert.IsNotNull(box);
                var point = new { x = box.X + box.Width / 2, y = box.Y + box.Height / 2 };
                var hit = await marker.EvaluateAsync<string>("""
                    a => {
                        const box = a.getBoundingClientRect();
                        return document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2)?.closest('a')?.getAttribute('href');
                    }
                    """).ConfigureAwait(false);
                Assert.AreEqual(href, hit, $"Visible centre at {width}px, {separation}px separation.");
                await page.Mouse.ClickAsync(point.x, point.y).ConfigureAwait(false);
                Assert.AreEqual(href, await page.Locator("body").GetAttributeAsync("data-clicked").ConfigureAwait(false));
                evidence.Add(new { width, separation, href, hit, box.Width, box.Height });
            }
        }

        await page.Keyboard.PressAsync("Tab").ConfigureAwait(false);
        await page.Locator(".timeline-marker").First.FocusAsync().ConfigureAwait(false);
        Assert.IsTrue(await page.Locator(".timeline-marker").First.EvaluateAsync<bool>(
            "a => a.matches(':focus-visible') && getComputedStyle(a).outlineStyle === 'solid' && parseFloat(getComputedStyle(a).outlineWidth) >= 3").ConfigureAwait(false));
        Assert.AreEqual("/archive/day/2026-07-21?calendar=" + day.CalendarVersion + "#day-candidates", await page.GetByRole(AriaRole.Link,
            new() { Name = "Use the full-size candidate list controls" }).GetAttributeAsync("href").ConfigureAwait(false));

        // The same stylesheet gives retained and unavailable hourly slots equal, centred geometry (Sol L2).
        var pageCss = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ObservingDayPage.razor.css")).ConfigureAwait(false);
        await page.AddStyleTagAsync(new() { Content = pageCss.Replace("::deep", string.Empty, StringComparison.Ordinal) }).ConfigureAwait(false);
        await page.EvaluateAsync("""
            () => document.body.insertAdjacentHTML('beforeend', '<div class="day-page"><div class="product-hours"><ol><li><a href="/archive/products">01:00</a></li><li><span>02:00</span></li></ol></div></div>')
            """).ConfigureAwait(false);
        var hourSlots = await page.Locator(".product-hours li > *").EvaluateAllAsync<bool>("""
            slots => slots.every(slot => slot.getBoundingClientRect().height >= 44 &&
                getComputedStyle(slot).alignItems === 'center' && getComputedStyle(slot).justifyContent === 'center') &&
                Math.abs(slots[0].getBoundingClientRect().height - slots[1].getBoundingClientRect().height) < 1
            """).ConfigureAwait(false);
        Assert.IsTrue(hourSlots, "Available and unavailable hourly slots must align.");

        var output = Path.Combine(TestContext.ResultsDirectory!, $"timeline-{width}");
        Directory.CreateDirectory(output);
        var metrics = Path.Combine(output, "hit-tests.json");
        await File.WriteAllTextAsync(metrics, JsonSerializer.Serialize(evidence)).ConfigureAwait(false);
        TestContext.AddResultFile(metrics);
        var screenshot = Path.Combine(output, "synthetic-timeline.png");
        await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true }).ConfigureAwait(false);
        TestContext.AddResultFile(screenshot);
        var html = Path.Combine(output, "synthetic-timeline.html");
        await File.WriteAllTextAsync(html, await page.ContentAsync().ConfigureAwait(false)).ConfigureAwait(false);
        TestContext.AddResultFile(html);

        async Task SetTimelineAsync(CameraAgentTransientOperatorCandidate[] candidates)
        {
            var markup = await renderer.Dispatcher.InvokeAsync(async () =>
            {
                var rendered = await renderer.RenderComponentAsync<ObservingDayTimeline>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(ObservingDayTimeline.Day)] = day,
                    [nameof(ObservingDayTimeline.Candidates)] = candidates
                })).ConfigureAwait(false);
                return rendered.ToHtmlString();
            }).ConfigureAwait(false);
            await page.SetContentAsync($"<!doctype html><html><head><base href=\"/\"><style>html{{font-size:16px}}body{{margin:8px}}{css}</style></head><body>{markup}</body></html>").ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(1440, 900, true, "?calendar=hvo-noon-observing-day-v1")]
    [DataRow(390, 844, true, "?calendar=hvo-sunrise-reporting-period-v1")]
    [DataRow(1440, 900, false, "")]
    public async Task CandidateListLinkStaysOnTheSelectedDayAndReachesTheList(
        int width, int height, bool javaScriptEnabled, string calendarQuery)
    {
        using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true }).ConfigureAwait(false);
        // Replace only the day read model. App/base, routing, authentication, layouts, CSS and Blazor are real.
        // The fixture owns/removes its unique iv-* root: Unix socket paths must be short and symlink-free.
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<ICameraAgentObservingDayUiService>(new CandidateDayService())),
            scratchRoot: Path.GetTempPath()).ConfigureAwait(false);
        var initialQuery = javaScriptEnabled ? calendarQuery : string.Empty;
        var expectedQuery = javaScriptEnabled ? calendarQuery
            : "?calendar=" + Uri.EscapeDataString(NightlyProductFixture.Calendar.CalendarVersion);
        await using var context = await browser.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new() { Width = width, Height = height },
            JavaScriptEnabled = javaScriptEnabled
        }).ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        await page.GetByLabel("Username or email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Account/Login").ConfigureAwait(false);

        const string path = "/archive/day/2026-07-21";
        await page.GotoAsync(path + initialQuery).ConfigureAwait(false);
        if (javaScriptEnabled)
        {
            await page.Locator(".app-frame[data-interactive='true']").WaitForAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(host.BaseAddress.AbsoluteUri, await page.Locator("base").EvaluateAsync<string>("base => base.href").ConfigureAwait(false));
        var dayHeading = await page.Locator(".day-title h1").InnerTextAsync().ConfigureAwait(false);
        var candidateListLink = page.GetByRole(AriaRole.Link, new() { Name = "Use the full-size candidate list controls" });
        await candidateListLink.FocusAsync().ConfigureAwait(false);
        await page.Keyboard.PressAsync("Enter").ConfigureAwait(false);
        await page.WaitForFunctionAsync("""
                () => {
                    const list = document.getElementById('day-candidates');
                    return location.hash === '#day-candidates' && list !== null;
                }
                """).ConfigureAwait(false);
        var destination = new Uri(page.Url);
        Assert.AreEqual(path, destination.AbsolutePath, "The app's root base must not send this link to Current sky.");
        Assert.AreEqual(expectedQuery, destination.Query, "Keep the selected calendar interpretation.");
        Assert.AreEqual("#day-candidates", destination.Fragment);
        Assert.AreEqual(dayHeading, await page.Locator(".day-title h1").InnerTextAsync().ConfigureAwait(false));
        var candidate = page.Locator("#day-candidates .candidate-list a");
        Assert.AreEqual("/transients/10000000-0000-0000-0000-000000000001",
            await candidate.GetAttributeAsync("href").ConfigureAwait(false));
        var bounds = await candidate.BoundingBoxAsync().ConfigureAwait(false);
        Assert.IsNotNull(bounds);
        Assert.IsTrue(bounds.Height >= 44);
        var metrics = Path.Combine(TestContext.ResultsDirectory!, $"candidate-list-{width}-{javaScriptEnabled}.json");
        await File.WriteAllTextAsync(metrics, JsonSerializer.Serialize(new
        {
            width, height, javaScriptEnabled, browserVersion = browser.Version, url = page.Url,
            candidateHeight = bounds.Height, navigatedToList = true
        })).ConfigureAwait(false);
        TestContext.AddResultFile(metrics);
        var screenshot = Path.Combine(TestContext.ResultsDirectory!, $"candidate-list-{width}-{javaScriptEnabled}.png");
        await page.ScreenshotAsync(new() { Path = screenshot, FullPage = true }).ConfigureAwait(false);
        TestContext.AddResultFile(screenshot);
    }

    private sealed class CandidateDayService : ICameraAgentObservingDayUiService
    {
        public ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult(View(NightlyProductFixture.Calendar.Resolve(observingDate)));

        public ValueTask<OperatorUiResult<CameraAgentObservingDayView>> GetAsync(DateOnly observingDate,
            string calendarVersion, CancellationToken cancellationToken)
        {
            var calendar = calendarVersion == ObservingDayCalendar.LegacyNoonVersion
                ? ObservingDayCalendar.Create("America/Phoenix")
                : NightlyProductFixture.Calendar;
            return ValueTask.FromResult(View(calendar.Resolve(observingDate)));
        }

        private static OperatorUiResult<CameraAgentObservingDayView> View(ObservingDay day)
        {
            var candidate = Candidate(day.StartUtc.AddHours(12), Guid.Parse("10000000-0000-0000-0000-000000000001"));
            return OperatorUiResult<CameraAgentObservingDayView>.Success(new(
                new CameraAgentGalleryCalendarDay(day, 0, 1, null, null, null, null),
                [], TimeSpan.Zero, null, [candidate], false, [], null, null, null));
        }
    }

    private static CameraAgentTransientOperatorCandidate Candidate(DateTimeOffset created, Guid id)
        => new(id, Guid.NewGuid(), "Extracted", "None", "CausalRetained", created, created, "Retained", "Pending", "Pending", "Pending");
}
