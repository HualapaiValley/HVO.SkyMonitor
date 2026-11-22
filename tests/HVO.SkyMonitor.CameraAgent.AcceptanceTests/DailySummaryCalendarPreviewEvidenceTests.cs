using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Category = HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure.CalendarDayPreviewFailureCategory;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>
/// Induces each failure #1916 must distinguish on the real Calendar-to-day path, with the same synthetic product,
/// sign-in and assertions as the 1440 × 900 partial acceptance row, and verifies the retained evidence names it
/// without any credential, cookie, antiforgery token, circuit token or free-text browser message. The cases share one
/// host and published product; each signs in through its own browser context and sets its own nightly fault.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Browser acceptance test owns all asynchronous resources.")]
public sealed class DailySummaryCalendarPreviewEvidenceTests
{
    /// <summary>Short enough that an induced failure is quick, long enough that an uninduced step still completes.</summary>
    private const float InducedTimeout = 2_500;

    private const string ScriptErrorSentinel = "PAGE_ERROR_SENTINEL_1916";
    private const string ConsoleSentinel = "CONSOLE_SENTINEL_1916";
    private static readonly string[] ExpectedPageErrorSignals = ["dom:nightly-products-unavailable", "script-error:TypeError"];
    private static readonly InducedNightlyDay NightlyDay = new();
    private static IPlaywright? playwright;
    private static IBrowser? browser;
    private static CameraAgentKestrelFixture? host;
    private static ServerLogEvidence? serverLog;
    private static string previewPath = null!;

    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static async Task InitializeAsync(TestContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            playwright = await Playwright.CreateAsync();
            // Browser absence is a failure here, as in the acceptance rows this evidence serves.
            browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            serverLog = new ServerLogEvidence();
            // Each test host's reload-on-change configuration watchers outlive it, holding inotify instances until the
            // process exits, and this assembly's Integration selection runs close to the 128-instance per-user limit.
            // One shared host that never reloads its settings keeps these cases within that budget. Its settings file
            // and every other path stay in the fixture's own generated root.
            host = await DailySummaryCalendarScenario.CreateHostAsync(serverLog, services =>
            {
                services.AddScoped<CameraAgentNightlyProductUiService>();
                services.Replace(ServiceDescriptor.Scoped<ICameraAgentNightlyProductUiService>(provider =>
                    NightlyDay.Wrap(provider.GetRequiredService<CameraAgentNightlyProductUiService>())));
            }, reloadConfigurationOnChange: false);
            // The opt-out changes only reloading: the host still reads the fixture's own operator settings file.
            var settingsFile = host.Services.GetRequiredService<OperatorSettingsFile>().FilePath;
            Assert.AreEqual(host.SettingsFilePath, settingsFile);
            StringAssert.StartsWith(settingsFile, Path.GetFullPath(host.Root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var (product, _) = await DailySummaryCalendarScenario.PublishAsync(host, complete: false);
            previewPath = NightlyProductLinks.Preview(product.ProductId);
        }
        catch
        {
            await CleanupAsync();
            throw;
        }
    }

    [ClassCleanup]
    public static async Task CleanupAsync()
    {
        if (host is not null) await host.DisposeAsync();
        if (browser is not null) await browser.DisposeAsync();
        playwright?.Dispose();
        host = null;
        browser = null;
        playwright = null;
    }

    [TestCleanup]
    public void ClearNightlyFault() => NightlyDay.Clear();

    [TestMethod]
    public async Task SwallowedDayLinkClickIsRetainedAsLostNavigation()
    {
        await using var run = await InducedRun.StartAsync(TestContext);
        // A capture-phase listener consumes the click before both the browser and Blazor's router see it.
        await run.Page.EvaluateAsync("""
            () => document.addEventListener('click', event => {
                if (event.target instanceof Element && event.target.closest('.calendar-day__link')) {
                    event.preventDefault();
                    event.stopImmediatePropagation();
                }
            }, true)
            """);

        var report = await run.FailAsync(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(new[] { Category.LostNavigation }, report.Categories.ToArray());
        Assert.AreEqual("preview image", report.FailedStep);
        Assert.AreEqual("/archive/calendar", report.UrlAtFailure.Split('?')[0]);
        Assert.AreEqual("/archive/calendar", report.UrlAfterSettle.Split('?')[0]);
        Assert.IsNotNull(report.AtFailure.Markup["calendarCell"], "the selected Calendar cell is retained");
        StringAssert.Contains(report.AtFailure.Markup["calendarCell"]!, "datetime=\"2026-10-01\"", StringComparison.Ordinal);
        Assert.IsNull(report.AtFailure.Markup["dayTile"]);
        Assert.IsEmpty(report.Observation.PreviewRequests, "the preview was never requested");
        Assert.IsTrue(report.Observation.Probe!.Available, "the product image itself is available");
        await run.AssertRetainedEvidenceAsync(report);
    }

    [TestMethod]
    public async Task FailedPreviewResponseIsRetainedAsUnavailableProductImage()
    {
        await using var run = await InducedRun.StartAsync(TestContext);
        // Only the browser's preview responses fail; the authenticated re-read still reaches the real endpoint.
        await run.Page.RouteAsync(
            url => new Uri(url).AbsolutePath == run.PreviewPath,
            route => route.FulfillAsync(new() { Status = 503, ContentType = "text/plain", Body = "PREVIEW_BODY_SENTINEL_1916" }));

        // The image element is visible in its sized frame until its error replaces it, so the unchanged wait could
        // observe it first; waiting for the tile's own failure marker makes the induced outcome deterministic.
        var report = await run.FailAsync(TimeSpan.FromSeconds(2), () =>
            run.Page.Locator("[data-product-kind='daily summary'] .product-slot__failed").WaitForAsync(new() { Timeout = 20_000 }));

        CollectionAssert.AreEqual(new[] { Category.UnavailableProductImage }, report.Categories.ToArray());
        Assert.AreEqual(run.DayPath, report.UrlAtFailure.Split('?')[0]);
        Assert.IsTrue(report.Observation.PreviewFailedMarker);
        Assert.IsTrue(report.Observation.PreviewRequests.Any(static request => request.Status == 503 && request.ContentType == "text/plain"));
        Assert.IsTrue(report.Observation.Probe!.Available, "the server still serves the image, so the failure was the browser's response");
        StringAssert.Contains(report.AtFailure.Markup["dayTile"]!, "product-slot__failed", StringComparison.Ordinal);
        await run.AssertRetainedEvidenceAsync(report, "PREVIEW_BODY_SENTINEL_1916");
    }

    [TestMethod]
    public async Task NightlyFailureAndScriptErrorAreRetainedAsPageErrors()
    {
        NightlyDay.FailWithUnavailable();
        await using var run = await InducedRun.StartAsync(TestContext);
        await run.Page.EvaluateAsync($$"""
            () => {
                console.error('{{ConsoleSentinel}} password=console-secret');
                setTimeout(() => { throw new TypeError('{{ScriptErrorSentinel}} token=script-secret'); }, 0);
            }
            """);

        var report = await run.FailAsync(TimeSpan.FromSeconds(2), () =>
            run.Page.Locator(".daily-products__note--warning").WaitForAsync(new() { Timeout = 20_000 }));

        CollectionAssert.AreEqual(new[] { Category.PageError }, report.Categories.ToArray());
        CollectionAssert.IsSubsetOf(ExpectedPageErrorSignals, report.Observation.PageErrorSignals.ToArray());
        Assert.IsTrue(report.ConsoleLabels.Any(static label => label.Key == "console:other-error"));
        Assert.IsNull(report.AtFailure.Markup["dayTile"], "the failed nightly read shows no Daily Summary tile");
        await run.AssertRetainedEvidenceAsync(report, ScriptErrorSentinel, ConsoleSentinel, "console-secret", "script-secret");
    }

    [TestMethod]
    public async Task DroppedCircuitIsRetainedAsPageErrorWithCircuitSignals()
    {
        await using var run = await InducedRun.StartAsync(TestContext, dropCircuit: true);
        await run.DropCircuitAsync();

        var report = await run.FailAsync(TimeSpan.FromSeconds(2));

        CollectionAssert.Contains(report.Categories.ToArray(), Category.PageError);
        CollectionAssert.Contains(report.Observation.PageErrorSignals.ToArray(), "dom:reconnect-dialog");
        Assert.IsTrue(report.AtFailure.ReconnectOpen);
        // The modal reconnect dialog leaves the Calendar inert, so the day link click itself cannot land.
        Assert.AreEqual("click day link", report.FailedStep);
        await run.AssertRetainedEvidenceAsync(report, "induced-drop");
    }

    [TestMethod]
    public async Task LateDayRenderIsRetainedAsTestSynchronization()
    {
        NightlyDay.Delay(TimeSpan.FromSeconds(4));
        await using var run = await InducedRun.StartAsync(TestContext);

        var report = await run.FailAsync(TimeSpan.FromSeconds(20), timeout: 1_000);

        CollectionAssert.AreEqual(new[] { Category.TestSynchronization }, report.Categories.ToArray());
        Assert.IsNotNull(report.Observation.ImageVisibleAfterSettleMilliseconds, "the preview appeared after the wait gave up");
        Assert.AreEqual(run.DayPath, report.UrlAfterSettle.Split('?')[0]);
        Assert.IsEmpty(report.Observation.PageErrorSignals);
        await run.AssertRetainedEvidenceAsync(report);
    }

    /// <summary>One induced Calendar run: its own browser context and signed-in page, and the secrets to exclude.</summary>
    private sealed class InducedRun : IAsyncDisposable
    {
        private readonly ConcurrentBag<string> circuitTokens = [];
        private readonly List<IAsyncDisposable> owned = [];
        private IWebSocketRoute? circuitSocket;
        private volatile bool circuitDropped;

        private InducedRun(TestContext testContext) => TestContext = testContext;

        internal TestContext TestContext { get; }
        internal IPage Page { get; private set; } = null!;
        internal ILocator Cell { get; private set; } = null!;
        internal string PreviewPath { get; private set; } = null!;
        internal string DayPath { get; } = "/archive/day/2026-10-01";
        internal CalendarDayPreviewEvidence Evidence { get; private set; } = null!;
        private string? Antiforgery { get; set; }

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
            Justification = "The browser context is transferred to the run, which disposes it.")]
        internal static async Task<InducedRun> StartAsync(TestContext testContext, bool dropCircuit = false)
        {
            Assert.IsNotNull(browser, "the class host and browser were initialized");
            Assert.IsNotNull(host);
            var run = new InducedRun(testContext) { PreviewPath = previewPath };
            try
            {
                var context = await browser.NewContextAsync(new()
                {
                    BaseURL = host.BaseAddress.ToString(),
                    ViewportSize = new() { Width = 1440, Height = 900 }
                });
                run.owned.Add(context);
                run.Page = await context.NewPageAsync();
                run.Page.WebSocket += (_, socket) => run.RememberCircuitToken(socket.Url);
                if (dropCircuit)
                {
                    await run.RouteCircuitAsync();
                }
                run.Antiforgery = await DailySummaryCalendarScenario.SignInAsync(run.Page);
                await run.Page.GotoAsync("/archive/calendar?month=2026-10");
                run.Cell = run.Page.Locator(".calendar-day").Filter(new() { Has = run.Page.Locator("time[datetime='2026-10-01']") });
                await run.Cell.Locator(".calendar-products i[data-nightly-state='Partial']").Filter(new() { HasText = "D" }).WaitForAsync();
                run.Evidence = CalendarDayPreviewEvidence.Observe(
                    run.Page, testContext, DailySummaryCalendarScenario.SelectedDay, run.PreviewPath,
                    DailySummaryCalendarScenario.RowLabel(1440, 900, complete: false), serverLog);
                return run;
            }
            catch
            {
                await run.DisposeAsync();
                throw;
            }
        }

        /// <summary>Runs the shared Calendar-to-day assertions, which must fail, and returns the evidence they retained.</summary>
        internal async Task<CalendarDayPreviewReport> FailAsync(
            TimeSpan settleWindow, Func<Task>? inducedStateAfterClick = null, float timeout = InducedTimeout)
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => DailySummaryCalendarScenario.OpenDailySummaryFromCalendarAsync(
                Page, Cell, Evidence, "partial", PreviewPath, timeout, settleWindow, inducedStateAfterClick));
            var report = Evidence.LastReport;
            Assert.IsNotNull(report, "the failure retained evidence");
            Assert.IsNotNull(report.EvidencePath);
            return report;
        }

        internal async Task DropCircuitAsync()
        {
            await Page.Locator(".app-frame[data-interactive='true']").WaitForAsync();
            circuitDropped = true;
            await circuitSocket!.CloseAsync(new() { Code = 4000, Reason = "induced-drop" });
            await Page.Locator("#components-reconnect-modal[open]").WaitForAsync(new() { Timeout = 20_000 });
        }

        /// <summary>Checks the retained file against the report and for every credential and session value the run used.</summary>
        internal async Task AssertRetainedEvidenceAsync(CalendarDayPreviewReport report, params string[] sentinels)
        {
            var json = await File.ReadAllTextAsync(report.EvidencePath!);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                Assert.AreEqual(CalendarDayPreviewEvidence.Schema, root.GetProperty("schema").GetString());
                Assert.AreEqual("2026-10-01", root.GetProperty("selectedDay").GetString());
                Assert.AreEqual(PreviewPath, root.GetProperty("previewPath").GetString());
                CollectionAssert.AreEqual(
                    report.Categories.Select(static category => category.ToString()).ToArray(),
                    root.GetProperty("categories").EnumerateArray().Select(static value => value.GetString()).ToArray());
                Assert.IsTrue(root.GetProperty("navigation").TryGetProperty("urlAtFailure", out _));
                Assert.IsTrue(root.GetProperty("preview").TryGetProperty("authenticatedReRead", out _));
                Assert.IsTrue(root.GetProperty("atFailure").TryGetProperty("markup", out _));
            }
            Assert.AreEqual(CalendarDayPreviewEvidence.EvidenceDirectoryName, Path.GetFileName(Path.GetDirectoryName(report.EvidencePath)));

            var secrets = new List<string>
            {
                CameraAgentKestrelFixture.OwnerPassword,
                CameraAgentKestrelFixture.OwnerEmail,
                "__RequestVerificationToken",
                "CfDJ8",
                "<!--Blazor",
                "Blazor:{"
            };
            secrets.AddRange(sentinels);
            if (Antiforgery is { Length: > 0 } antiforgery) secrets.Add(antiforgery);
            secrets.AddRange((await Page.Context.CookiesAsync()).Select(static cookie => cookie.Value).Where(static value => value.Length >= 8));
            secrets.AddRange(circuitTokens);
            Assert.IsGreaterThan(0, circuitTokens.Count, "the run must have observed a circuit token to prove it is excluded");
            foreach (var secret in secrets.Distinct(StringComparer.Ordinal))
            {
                Assert.DoesNotContain(secret, json, StringComparison.Ordinal, "retained evidence must exclude credential and session values");
            }
        }

        private async Task RouteCircuitAsync()
        {
            await Page.RouteWebSocketAsync("**/_blazor*", route =>
            {
                RememberCircuitToken(route.Url);
                if (circuitDropped)
                {
                    _ = route.CloseAsync(new() { Code = 4000, Reason = "induced-drop" });
                    return;
                }
                route.ConnectToServer();
                circuitSocket = route;
            });
            // After the drop, negotiation and the long-polling fallback fail as well, so the circuit stays lost.
            await Page.RouteAsync(
                url => new Uri(url).AbsolutePath.StartsWith("/_blazor", StringComparison.Ordinal),
                route => circuitDropped ? route.AbortAsync("connectionrefused") : route.ContinueAsync());
        }

        private void RememberCircuitToken(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = pair.Split('=', 2);
                    if (parts.Length == 2 && parts[0] == "id" && parts[1].Length >= 8)
                    {
                        circuitTokens.Add(Uri.UnescapeDataString(parts[1]));
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            Evidence?.Dispose();
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                await owned[index].DisposeAsync();
            }
        }
    }
}

/// <summary>
/// Induces a nightly day-read fault for the selected day while every other nightly read stays real. The fault is read
/// at each call, so one host serves every case; the test class clears it after each case.
/// </summary>
internal sealed class InducedNightlyDay
{
    private Fault current = Fault.None;

    internal void FailWithUnavailable() => Volatile.Write(ref current, new Fault(true, null));

    internal void Delay(TimeSpan delay) => Volatile.Write(ref current, new Fault(false, delay));

    internal void Clear() => Volatile.Write(ref current, Fault.None);

    internal ICameraAgentNightlyProductUiService Wrap(ICameraAgentNightlyProductUiService inner) => new Service(inner, this);

    private sealed record Fault(bool Unavailable, TimeSpan? Delay)
    {
        internal static readonly Fault None = new(false, null);
    }

    private sealed class Service(ICameraAgentNightlyProductUiService inner, InducedNightlyDay owner) : ICameraAgentNightlyProductUiService
    {
        public async ValueTask<OperatorUiResult<CameraAgentNightlyDayView>> GetDayAsync(DateOnly observingDate, CancellationToken cancellationToken)
        {
            var fault = Volatile.Read(ref owner.current);
            if (observingDate == DailySummaryCalendarScenario.SelectedDay && fault.Unavailable)
            {
                return OperatorUiResult<CameraAgentNightlyDayView>.Failure(OperatorUiResultKind.Unavailable, "Induced nightly product failure.");
            }
            if (observingDate == DailySummaryCalendarScenario.SelectedDay && fault.Delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            return await inner.GetDayAsync(observingDate, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask<OperatorUiResult<IReadOnlyList<NightlyProductDateSummary>>> SummarizeAsync(
            DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken) =>
            inner.SummarizeAsync(firstDate, lastDate, cancellationToken);

        public ValueTask<OperatorUiResult<NightlyProductPresentation>> GetPresentationAsync(Guid productId, CancellationToken cancellationToken) =>
            inner.GetPresentationAsync(productId, cancellationToken);

        public ValueTask<OperatorUiResult<NightlyProductLibraryPage>> ListLibraryAsync(
            NightlyProductLibraryQuery query, CancellationToken cancellationToken) =>
            inner.ListLibraryAsync(query, cancellationToken);

        public ValueTask<OperatorUiResult<CameraAgentTimeLapsePresentation>> GetTimeLapseAsync(Guid productId, CancellationToken cancellationToken) =>
            inner.GetTimeLapseAsync(productId, cancellationToken);

        public ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseSummary>>> ListTimeLapsesAsync(
            DateOnly? before, CancellationToken cancellationToken) =>
            inner.ListTimeLapsesAsync(before, cancellationToken);

        public ValueTask<OperatorUiResult<CameraAgentTimeLapseLibraryPage>> ListTimeLapseLibraryAsync(
            NightlyProductLibraryQuery query, CancellationToken cancellationToken) =>
            inner.ListTimeLapseLibraryAsync(query, cancellationToken);

        public ValueTask<OperatorUiResult<bool>> RetryTimeLapseAsync(
            Guid jobId, long expectedRevision, Guid requestId, CancellationToken cancellationToken) =>
            inner.RetryTimeLapseAsync(jobId, expectedRevision, requestId, cancellationToken);

        public ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentTimeLapseDateSummary>>> SummarizeTimeLapsesAsync(
            DateOnly firstDate, DateOnly lastDate, CancellationToken cancellationToken) =>
            inner.SummarizeTimeLapsesAsync(firstDate, lastDate, cancellationToken);
    }
}
