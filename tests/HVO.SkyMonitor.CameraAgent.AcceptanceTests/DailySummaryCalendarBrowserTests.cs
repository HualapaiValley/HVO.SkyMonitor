using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.NightlyProducts;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
        var serverLog = new ServerLogEvidence();
        await using var host = await DailySummaryCalendarScenario.CreateHostAsync(serverLog);
        var (product, facts) = await DailySummaryCalendarScenario.PublishAsync(host, complete);
        Assert.AreEqual(!complete, facts.Partial);
        Assert.AreEqual(DailySummaryCalendarScenario.SampledBins(complete), facts.SampledBins);

        await using var context = await browser.NewContextAsync(new()
        {
            BaseURL = host.BaseAddress.ToString(),
            ViewportSize = new() { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        await DailySummaryCalendarScenario.SignInAsync(page);
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

        // #1916: a failure from the day link click through the preview identity retains credential-free evidence of
        // navigation, the day card, browser and circuit signals and the preview outcome, then rethrows unchanged.
        using var evidence = CalendarDayPreviewEvidence.Observe(
            page, TestContext, DailySummaryCalendarScenario.SelectedDay, NightlyProductLinks.Preview(product.ProductId),
            DailySummaryCalendarScenario.RowLabel(width, height, complete), serverLog);
        await DailySummaryCalendarScenario.OpenDailySummaryFromCalendarAsync(
            page, cell, evidence, complete ? "available" : "partial", NightlyProductLinks.Preview(product.ProductId));
        await page.GotoAsync(NightlyProductLinks.Detail(product.ProductId));
        var detail = page.Locator("[data-nightly-section='daily-summary']");
        await detail.WaitForAsync();
        StringAssert.Contains(await detail.InnerTextAsync(), complete
            ? "Every five-minute bin contains an admitted exposure start" : "Partial: sampled coverage has gaps", StringComparison.Ordinal);
        TestContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Verified immutable product {product.ProductId:D}, {facts.SampledBins}/{facts.TotalBins} bins, Calendar {state}, Day and Detail."));
    }
}

/// <summary>The synthetic Daily Summary Calendar arrangement shared by the acceptance rows and the #1916 evidence tests.</summary>
internal static class DailySummaryCalendarScenario
{
    internal static readonly DateOnly SelectedDay = new(2026, 10, 1);

    /// <summary>How long a failed assertion keeps watching for the preview, to tell a late image from a missing one.</summary>
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(10);

    internal static string RowLabel(int width, int height, bool complete) =>
        string.Create(CultureInfo.InvariantCulture, $"{width}x{height}-{(complete ? "complete" : "partial")}");

    internal static int SampledBins(bool complete)
    {
        var window = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary).SourceWindow!;
        return complete ? (int)Math.Ceiling((window.EndUtc - window.StartUtc).TotalMinutes / 5) : 1;
    }

    internal static Task<CameraAgentKestrelFixture> CreateHostAsync(
        ServerLogEvidence serverLog, Action<IServiceCollection>? configureServices = null) =>
        CameraAgentKestrelFixture.CreateAsync(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IObservingDayCalendarProvider>(
                new FixedObservingDayCalendarProvider(NightlyProductFixture.Calendar)));
            services.AddSingleton<ILoggerProvider>(serverLog);
            configureServices?.Invoke(services);
        }, scratchRoot: Path.GetTempPath());

    internal static async Task<(NightlyProductSummary Product, DailySummaryV1 Facts)> PublishAsync(
        CameraAgentKestrelFixture host, bool complete)
    {
        var store = host.Services.GetRequiredService<SqliteNightlyProductStore>();
        var rawRoot = host.Services.GetRequiredService<IOptions<CameraAgentHostOptions>>().Value.RawIngressRoot;
        var occurrence = NightlyProductFixture.Occurrence(NightlyProductKind.DailySummary);
        var window = occurrence.SourceWindow!;
        var reader = new InMemoryNightlySourceReader();
        var count = SampledBins(complete);
        reader.Add([.. Enumerable.Range(0, count).Select(index =>
            NightlyProductFixture.Frame(index + 1, window.StartUtc.AddMinutes(index * 5)))]);
        using (var generator = new NightlyProductGenerator(
            NightlyProductFixture.HostOptions(rawRoot, NightlyProductFixture.Options()),
            new FixedConfigurationAccessor(NightlyProductFixture.Configuration()), reader, store,
            new AstronomyEnginePlanetEphemeris(), static () => null, new NightlyClock(window.EndUtc.AddDays(2))))
            Assert.AreEqual(1, (await generator.RunAsync(occurrence, CancellationToken.None).ConfigureAwait(false)).ProductsPublished);
        var product = (await store.ListAsync(NightlyProductFixture.ObservingDate, CancellationToken.None).ConfigureAwait(false)).Single();
        var facts = (await store.GetPresentationAsync(product.ProductId, CancellationToken.None).ConfigureAwait(false))!.DailySummary!;
        return (product, facts);
    }

    /// <summary>Signs in as the owner and returns the login form's antiforgery token, so evidence can be checked for it.</summary>
    internal static async Task<string?> SignInAsync(IPage page)
    {
        await page.GotoAsync("/Account/Login").ConfigureAwait(false);
        var antiforgery = await page.Locator("input[name='__RequestVerificationToken']").First
            .GetAttributeAsync("value").ConfigureAwait(false);
        await page.GetByLabel("Username or email").FillAsync(CameraAgentKestrelFixture.OwnerEmail).ConfigureAwait(false);
        await page.GetByLabel("Password").FillAsync(CameraAgentKestrelFixture.OwnerPassword).ConfigureAwait(false);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync(url => new Uri(url).AbsolutePath != "/Account/Login").ConfigureAwait(false);
        return antiforgery;
    }

    /// <summary>
    /// The Calendar-to-day assertions of the acceptance rows: follow the selected day's link, then require the Daily
    /// Summary preview image, its nightly state and the published product's preview identity. A failure retains #1916
    /// evidence before it is rethrown unchanged. The acceptance rows pass only the first five arguments and keep
    /// Playwright's default timeout. The induced-failure tests shorten <paramref name="timeout"/>, and may use
    /// <paramref name="inducedStateAfterClick"/> to wait for their induced state before the unchanged waits run.
    /// </summary>
    internal static async Task OpenDailySummaryFromCalendarAsync(
        IPage page, ILocator cell, CalendarDayPreviewEvidence evidence, string expectedState, string expectedPreview,
        float? timeout = null, TimeSpan? settleWindow = null, Func<Task>? inducedStateAfterClick = null)
    {
        try
        {
            await evidence.BeforeDayLinkClickAsync().ConfigureAwait(false);
            await cell.Locator(".calendar-day__link").ClickAsync(new() { Timeout = timeout }).ConfigureAwait(false);
            if (inducedStateAfterClick is not null)
            {
                evidence.Step = "induced state";
                await inducedStateAfterClick().ConfigureAwait(false);
            }
            var tile = page.Locator("[data-product-kind='daily summary']");
            evidence.Step = "preview image";
            await tile.Locator("img").WaitForAsync(new() { Timeout = timeout }).ConfigureAwait(false);
            evidence.Step = "tile state";
            Assert.AreEqual(expectedState, await tile.GetAttributeAsync("data-nightly-state", new() { Timeout = timeout }).ConfigureAwait(false));
            evidence.Step = "preview source";
            Assert.AreEqual(expectedPreview, await tile.Locator("img").GetAttributeAsync("src", new() { Timeout = timeout }).ConfigureAwait(false));
            evidence.ReportPassed();
        }
        catch (Exception failure) when (CalendarDayPreviewEvidence.IsAssertionFailure(failure))
        {
            await evidence.RetainAsync(failure, settleWindow ?? SettleWindow).ConfigureAwait(false);
            throw;
        }
    }
}
