using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>Actual generated products, authenticated app pages and Chromium; no physical camera or prototype facts.</summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Browser acceptance test owns all asynchronous resources.")]
public sealed class DailySummaryCalendarBrowserTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(1440, 900, false)]
    [DataRow(390, 844, false)]
    [DataRow(1440, 900, true)]
    [DataRow(390, 844, true)]
    public async Task CalendarExplainsCompleteAndPartialPublishedDailySummaries(int width, int height, bool complete)
    {
        using var playwright = await Playwright.CreateAsync();
        // Browser absence is a failure here: these regressions require Chromium actually to execute.
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        TestContext.WriteLine($"Chromium {browser.Version}; viewport {width}x{height}; complete={complete}");
        await using var host = await CameraAgentKestrelFixture.CreateAsync(services =>
            services.Replace(ServiceDescriptor.Singleton<IObservingDayCalendarProvider>(
                new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar))), scratchRoot: Path.GetTempPath());
        var store = host.Services.GetRequiredService<SqliteNightlyProductStore>();
        var rawRoot = host.Services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot;
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
        var window = occurrence.SourceWindow!;
        var reader = new InMemoryNightlySourceReader();
        var count = complete ? (int)Math.Ceiling((window.EndUtc - window.StartUtc).TotalMinutes / 5) : 1;
        reader.Add([.. Enumerable.Range(0, count).Select(index =>
            NightlyProductFixture.Frame(index + 1, window.StartUtc.AddMinutes(index * 5)))]);
        using (var generator = new NightlyProductGenerator(
            NightlyProductFixture.HostOptions(rawRoot, NightlyProductFixture.Options()),
            new FixedConfigurationAccessor(NightlyProductFixture.Configuration()), reader, store,
            new AstronomyEnginePlanetEphemeris(), static () => null, new NightlyClock(window.EndUtc.AddDays(2))))
            Assert.AreEqual(1, (await generator.RunAsync(occurrence, CancellationToken.None)).ProductsPublished);
        var product = (await store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None)).Single();
        var facts = (await store.GetPresentationAsync(product.ProductId, CancellationToken.None))!.DailySummary!;
        Assert.AreEqual(!complete, facts.Partial);
        Assert.AreEqual(count, facts.SampledBins);

        await using var context = await browser.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new() { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/Account/Login");
        await page.GetByLabel("Username or email").FillAsync(CameraAgentKestrelFixture.OwnerEmail);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Account/Login");
        await page.GotoAsync("/archive/calendar?month=2026-10");
        var state = complete ? "Produced" : "Partial";
        var cell = page.Locator(".calendar-day").Filter(new()
        {
            Has = page.Locator("time[datetime='2026-10-01']")
        });
        var badge = cell.Locator($".calendar-products i[data-nightly-state='{state}']").Filter(new() { HasText = "D" });
        await badge.WaitForAsync();
        StringAssert.Contains((await badge.GetAttributeAsync("title"))!, complete
            ? "daily summary produced" : "daily summary produced with partial sampled coverage", StringComparison.OrdinalIgnoreCase);
        var legend = await page.Locator(".calendar-legend").InnerTextAsync();
        Assert.DoesNotContain("hourly only", legend, StringComparison.OrdinalIgnoreCase);
        StringAssert.Contains(legend, "D daily summary", StringComparison.Ordinal);
        StringAssert.Contains(legend, "partial sampled coverage", StringComparison.Ordinal);
        StringAssert.Contains(legend, "hourly products without a nightly final", StringComparison.Ordinal);

        await cell.Locator(".calendar-day__link").ClickAsync();
        var tile = page.Locator("[data-product-kind='daily summary']");
        await tile.Locator("img").WaitForAsync();
        Assert.AreEqual(complete ? "available" : "partial", await tile.GetAttributeAsync("data-nightly-state"));
        Assert.AreEqual(NightlyProductLinks.Preview(product.ProductId), await tile.Locator("img").GetAttributeAsync("src"));
        await page.GotoAsync(NightlyProductLinks.Detail(product.ProductId));
        var detail = page.Locator("[data-nightly-section='daily-summary']");
        await detail.WaitForAsync();
        StringAssert.Contains(await detail.InnerTextAsync(), complete
            ? "Every five-minute bin contains an admitted exposure start" : "Partial: sampled coverage has gaps", StringComparison.Ordinal);
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Verified immutable product {product.ProductId:D}, {facts.SampledBins}/{facts.TotalBins} bins, Calendar {state}, Day and Detail."));
    }
}
