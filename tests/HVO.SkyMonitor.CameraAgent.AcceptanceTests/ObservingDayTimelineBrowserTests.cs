using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>Real component/CSS hit-testing with synthetic records; no host, camera or production data.</summary>
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
        Assert.AreEqual("#day-candidates", await page.GetByRole(AriaRole.Link,
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
            await page.SetContentAsync($"<!doctype html><html><head><style>html{{font-size:16px}}body{{margin:8px}}{css}</style></head><body>{markup}</body></html>").ConfigureAwait(false);
        }
    }

    private static CameraAgentTransientOperatorCandidate Candidate(DateTimeOffset created, Guid id)
        => new(id, Guid.NewGuid(), "Extracted", "None", "CausalRetained", created, created, "Retained", "Pending", "Pending", "Pending");
}
