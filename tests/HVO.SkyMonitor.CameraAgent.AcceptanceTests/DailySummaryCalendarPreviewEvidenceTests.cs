using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
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
    private const string OpaqueOrigin = "http://opaque-host-1916.invalid";
    private const string OpaquePreviewPath = "/api/v1/operations/still-products/00000000-0000-0000-0000-000000001916/preview";

    private static readonly string[] OpaqueSentinels =
    [
        "opaque-host-1916", "OPAQUE_SEGMENT_1916", "OPAQUE_QUERYNAME_1916", "OPAQUE_VALUE_1916", "OPAQUE_MONTH_1916",
        "OPAQUE_INTERACTIVE_1916", "OPAQUE_NOTICE_CLASS_1916", "OPAQUE_NOTICE_TITLE_1916", "OPAQUE_NOTICE_MESSAGE_1916",
        "OPAQUE_ATTRIBUTE_1916", "OPAQUE_CLASS_1916", "OPAQUE_STATE_ATTR_1916", "OPAQUE_LABEL_1916", "OPAQUE_TITLE_1916",
        "OPAQUE_ID_1916", "OPAQUE_TEXT_1916", "OPAQUE_LINK_SEGMENT_1916", "OPAQUE_LINK_QUERYNAME_1916", "OPAQUE_IMAGE_SEGMENT_1916",
        "OPAQUE_IMAGE_QUERYNAME_1916", "OPAQUE_ALT_1916", "opaque-element-1916", "OPAQUE_CUSTOM_1916", "OPAQUE_COMMENT_1916",
        "OPAQUE_CELL_CLASS_1916", "OPAQUE_CAL_1916", "OPAQUE_BADGE_TITLE_1916", "OPAQUE_RECONNECT_1916", "OPAQUE_CONSOLE_1916",
        "OPAQUE_ERROR_1916", "OPAQUE_FAILURE_LINE_1916", "OPAQUE_SELECTOR_1916", "OPAQUE_CATEGORY_1916", "OPAQUE_EVENT_1916",
        "OPAQUE_STATE_1916", "OPAQUE_EXCEPTION_1916"
    ];

    /// <summary>A product-shaped day page whose every free value is an opaque sentinel.</summary>
    private const string OpaqueDayPage = """
        <!DOCTYPE html><html><body>
        <div class="app-frame" data-interactive="OPAQUE_INTERACTIVE_1916"><main><div class="content-container"><div class="day-page">
          <section class="page-state page-state--error OPAQUE_NOTICE_CLASS_1916" role="alert"><div><strong>OPAQUE_NOTICE_TITLE_1916</strong><p>OPAQUE_NOTICE_MESSAGE_1916</p></div></section>
          <ol><li class="calendar-day OPAQUE_CELL_CLASS_1916"><a class="calendar-day__link" href="/archive/day/2026-10-01?calendar=OPAQUE_CAL_1916"><time datetime="2026-10-01">1</time><span class="calendar-products"><i class="partial" data-nightly-state="Partial" title="OPAQUE_BADGE_TITLE_1916">D</i></span></a></li></ol>
          <section class="daily-products" aria-busy="false" data-opaque="OPAQUE_ATTRIBUTE_1916">
            <article class="product-slot OPAQUE_CLASS_1916" data-product-kind="daily summary" data-nightly-state="OPAQUE_STATE_ATTR_1916" aria-label="OPAQUE_LABEL_1916" title="OPAQUE_TITLE_1916" id="OPAQUE_ID_1916">
              <button type="button" class="product-slot__media"><span class="product-slot__failed"><i class="bi bi-image"></i>Preview unavailable</span></button>
              <h3>Daily summary</h3><p>OPAQUE_TEXT_1916</p>
              <a href="/archive/day/OPAQUE_LINK_SEGMENT_1916?OPAQUE_LINK_QUERYNAME_1916=1">open</a>
              <img src="/api/v1/operations/still-products/OPAQUE_IMAGE_SEGMENT_1916/preview?OPAQUE_IMAGE_QUERYNAME_1916=1" alt="OPAQUE_ALT_1916" style="width:10px;height:10px">
              <opaque-element-1916>OPAQUE_CUSTOM_1916</opaque-element-1916>
              <!-- OPAQUE_COMMENT_1916 -->
            </article>
          </section>
        </div></div></main></div>
        <dialog id="components-reconnect-modal" class="components-reconnect-show OPAQUE_RECONNECT_1916"></dialog>
        </body></html>
        """;
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
    [DataRow(false, DisplayName = "late render")]
    [DataRow(true, DisplayName = "late render with unrelated host warning and error")]
    public async Task LateDayRenderIsRetainedAsTestSynchronization(bool unrelatedHostSignals)
    {
        NightlyDay.Delay(TimeSpan.FromSeconds(4));
        await using var run = await InducedRun.StartAsync(TestContext);
        // Host work unrelated to the page logs a warning and an exception after the click, during the late render.
        var unrelated = host!.Services.GetRequiredService<ILoggerFactory>().CreateLogger("HVO.SkyMonitor.CameraAgent.UnrelatedHostWork");
        Func<Task>? afterClick = null;
        if (unrelatedHostSignals)
        {
            afterClick = () =>
            {
                unrelated.Log(LogLevel.Warning, new EventId(1916, "UnrelatedWarning"), "unrelated warning", null, static (state, _) => state);
                unrelated.Log(LogLevel.Error, new EventId(1917, "UnrelatedError"), "unrelated error",
                    new InvalidOperationException("unrelated host failure"), static (state, _) => state);
                return Task.CompletedTask;
            };
        }

        var report = await run.FailAsync(TimeSpan.FromSeconds(20), afterClick, timeout: 1_000);

        CollectionAssert.AreEqual(new[] { Category.TestSynchronization }, report.Categories.ToArray());
        Assert.IsNotNull(report.Observation.ImageVisibleAfterSettleMilliseconds, "the preview appeared after the wait gave up");
        Assert.AreEqual(run.DayPath, report.UrlAfterSettle.Split('?')[0]);
        Assert.IsEmpty(report.Observation.PageErrorSignals);
        if (unrelatedHostSignals)
        {
            // Retained as coarse context, never as a cause.
            Assert.IsTrue(report.ServerLog.Any(static entry => entry.Level == LogLevel.Warning && entry.EventId == 1916));
            Assert.IsTrue(report.ServerLog.Any(static entry => entry.Level == LogLevel.Error && entry.EventId == 1917));
        }
        await run.AssertRetainedEvidenceAsync(report);
    }

    [TestMethod]
    public async Task OpaqueValuesNeverReachRetainedEvidence()
    {
        Assert.IsNotNull(browser);
        await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync();
        await page.RouteAsync(OpaqueOrigin + "/**", route => route.FulfillAsync(new() { Status = 200, ContentType = "text/html", Body = OpaqueDayPage }));
        await page.GotoAsync(OpaqueOrigin + "/archive/day/OPAQUE_SEGMENT_1916?OPAQUE_QUERYNAME_1916=OPAQUE_VALUE_1916&month=OPAQUE_MONTH_1916");
        var opaqueLog = new ServerLogEvidence();
        opaqueLog.CreateLogger("OPAQUE_CATEGORY_1916").Log(LogLevel.Error, new EventId(7, "OPAQUE_EVENT_1916"), "OPAQUE_STATE_1916",
            new InvalidOperationException("OPAQUE_EXCEPTION_1916"), static (state, _) => state);
        var directory = Path.Combine(TestContext.ResultsDirectory ?? Path.GetTempPath(), "opaque-" + Guid.NewGuid().ToString("N"));
        var attached = new List<string>();
        var lines = new List<string>();
        using var evidence = CalendarDayPreviewEvidence.Observe(
            page, TestContext, DailySummaryCalendarScenario.SelectedDay, OpaquePreviewPath, "opaque-1440x900",
            opaqueLog, new FileEvidenceSink(directory, attached.Add, lines.Add));
        await page.EvaluateAsync("""
            () => {
                console.error('OPAQUE_CONSOLE_1916');
                setTimeout(() => { throw new TypeError('OPAQUE_ERROR_1916'); }, 0);
            }
            """);
        await page.WaitForFunctionAsync("() => document.readyState === 'complete'");
        await evidence.BeforeDayLinkClickAsync();

        var report = await evidence.RetainAsync(new TimeoutException(
            "Timeout 30000ms exceeded.\nCall log:\n  - OPAQUE_FAILURE_LINE_1916 waiting for Locator(\"OPAQUE_SELECTOR_1916\")"),
            TimeSpan.FromMilliseconds(300));

        Assert.IsNotNull(report);
        Assert.HasCount(1, attached);
        var retained = await File.ReadAllTextAsync(report.EvidencePath!) + "\n" + string.Join('\n', lines);
        foreach (var opaque in OpaqueSentinels)
        {
            Assert.DoesNotContain(opaque, retained, StringComparison.Ordinal, "an opaque value reached retained evidence");
        }
        // The cause evidence survives the allowlist.
        CollectionAssert.Contains(report.Categories.ToArray(), Category.UnavailableProductImage);
        CollectionAssert.Contains(report.Categories.ToArray(), Category.PageError);
        CollectionAssert.Contains(report.Observation.PageErrorSignals.ToArray(), "script-error:TypeError");
        CollectionAssert.Contains(report.Observation.PageErrorSignals.ToArray(), "dom:error-notice");
        StringAssert.StartsWith(report.UrlAtFailure, "/archive/day/", StringComparison.Ordinal);
        StringAssert.Contains(report.AtFailure.Markup["dayTile"]!, "product-slot__failed", StringComparison.Ordinal);
        StringAssert.Contains(report.AtFailure.Markup["dayTile"]!, "Preview unavailable", StringComparison.Ordinal);
        StringAssert.Contains(report.AtFailure.Markup["dayTile"]!, "data-product-kind=\"daily summary\"", StringComparison.Ordinal);
        StringAssert.Contains(report.AtFailure.Markup["calendarCell"]!, "data-nightly-state=\"Partial\"", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("stalled", DisplayName = "stalled sink")]
    [DataRow("failing-attach", DisplayName = "sink whose attachment fails")]
    public async Task UnretainableEvidenceKeepsTheOriginalAssertion(string sinkKind)
    {
        var budget = TimeSpan.FromSeconds(3);
        var directory = Path.Combine(TestContext.ResultsDirectory ?? Path.GetTempPath(), "unretainable-" + Guid.NewGuid().ToString("N"));
        var attached = new List<string>();
        var stalled = new StalledSink(budget + TimeSpan.FromSeconds(4));
        ICalendarEvidenceSink sink = sinkKind == "stalled"
            ? stalled
            : new FileEvidenceSink(directory, _ => throw new IOException("ATTACH_SINK_FAULT_1916"), _ => { });
        var unobserved = new ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception.ToString().Contains("SINK_FAULT_1916", StringComparison.Ordinal)) unobserved.Enqueue(args.Exception);
        };
        TaskScheduler.UnobservedTaskException += observe;
        try
        {
            await using var run = await InducedRun.StartAsync(TestContext, sink: sink, budget: budget);
            var original = new AssertFailedException("ORIGINAL_ASSERTION_1916");
            var started = Stopwatch.StartNew();

            var thrown = await Assert.ThrowsExactlyAsync<AssertFailedException>(() => DailySummaryCalendarScenario.OpenDailySummaryFromCalendarAsync(
                run.Page, run.Cell, run.Evidence, "partial", run.PreviewPath, InducedTimeout, TimeSpan.FromMilliseconds(300), () => throw original));

            var elapsed = started.Elapsed;
            Assert.AreSame(original, thrown, "the original assertion object is rethrown unchanged");
            Assert.IsLessThan(budget + TimeSpan.FromSeconds(3), elapsed, "the whole evidence attempt is bounded");
            Assert.IsNull(run.Evidence.LastReport);
            Assert.IsEmpty(attached);
            Assert.IsFalse(Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(),
                "no complete or partial evidence file is left behind");
            if (sinkKind == "stalled")
            {
                // The abandoned write faults later; that fault must already be observed.
                await stalled.Faulted.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Delay(200);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Assert.IsEmpty(unobserved, "an abandoned evidence task left an unobserved fault");
            }
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= observe;
        }
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
        internal static async Task<InducedRun> StartAsync(
            TestContext testContext, bool dropCircuit = false, ICalendarEvidenceSink? sink = null, TimeSpan? budget = null)
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
                    DailySummaryCalendarScenario.RowLabel(1440, 900, complete: false), serverLog, sink, budget);
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
                Assert.AreEqual("/api/v1/operations/still-products/{expected-product}/preview", root.GetProperty("previewRoute").GetString());
                CollectionAssert.AreEqual(
                    report.Categories.Select(static category => category.ToString()).ToArray(),
                    root.GetProperty("categories").EnumerateArray().Select(static value => value.GetString()).ToArray());
                Assert.IsTrue(root.GetProperty("navigation").TryGetProperty("routeAtFailure", out _));
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

/// <summary>A sink that never finishes within the evidence budget, ignores cancellation, and faults afterwards.</summary>
internal sealed class StalledSink(TimeSpan faultAfter) : ICalendarEvidenceSink
{
    private readonly TaskCompletionSource faulted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Faulted => faulted.Task;

    public async Task<string> PersistAsync(string fileName, string content, CancellationToken cancellationToken)
    {
        await Task.Delay(faultAfter, CancellationToken.None).ConfigureAwait(false);
        faulted.TrySetResult();
        throw new IOException("STALLED_SINK_FAULT_1916");
    }

    public void WriteLine(string line)
    {
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
