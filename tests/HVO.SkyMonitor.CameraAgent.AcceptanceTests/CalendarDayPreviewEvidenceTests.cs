using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;
using Microsoft.Extensions.Logging;
using Category = HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure.CalendarDayPreviewFailureCategory;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests;

/// <summary>The #1916 classification and redaction rules, verified without a browser.</summary>
[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class CalendarDayPreviewEvidenceTests
{
    private static readonly CalendarDayPreviewProbe AvailableProbe = new(200, "image/png", 1024, true, null);
    private static readonly Guid ExpectedProduct = Guid.Parse("00000000-0000-0000-0000-000000001916");
    private static readonly Uri AppBaseAddress = new("http://127.0.0.1:5130/");
    private static readonly string[] ExpectedReconnectStates = ["components-reconnect-show"];
    private static readonly string[] ExpectedSnapshotSignals = ["dom:reconnect-dialog", "dom:error-notice"];
    private static readonly string[] ExpectedComposedSignals = ["console:connection-lost", "script-error:TypeError"];

    [TestMethod]
    public void DayNeverReachedIsLostNavigation() =>
        AssertCategories(Observation(onDayAtFailure: false, onDayAfterSettle: false), Category.LostNavigation);

    [TestMethod]
    public void DayReachedOnlyDuringSettleWithImageIsTestSynchronization() =>
        AssertCategories(Observation(onDayAtFailure: false, onDayAfterSettle: true, visibleAfterSettle: 2_400), Category.TestSynchronization);

    [TestMethod]
    public void ImageVisibleAtFailureIsTestSynchronization() =>
        AssertCategories(Observation(imageVisibleAtFailure: true), Category.TestSynchronization);

    [TestMethod]
    public void DayStillLoadingWithoutImageIsRenderIncomplete() =>
        AssertCategories(Observation(dayLoadingAfterSettle: true), Category.RenderIncomplete);

    [TestMethod]
    public void AssertionMismatchWithDisplayedImageIsProductStateMismatch() =>
        AssertCategories(Observation(imageVisibleAtFailure: true, assertionMismatch: true), Category.ProductStateMismatch);

    [TestMethod]
    public void ReachedDayWithoutAnySignalIsUnclassified() =>
        AssertCategories(Observation(), Category.Unclassified);

    [TestMethod]
    [DataRow(503, "text/plain", DisplayName = "server error")]
    [DataRow(404, "application/problem+json", DisplayName = "not found")]
    [DataRow(200, "text/html", DisplayName = "non-image success")]
    public void FailedBrowserPreviewResponseIsUnavailableProductImage(int status, string contentType) =>
        AssertCategories(
            Observation(requests: [new(40, "response", status, contentType, null)], imageVisibleAtFailure: true),
            Category.UnavailableProductImage);

    [TestMethod]
    public void NetworkFailureWithoutAnyImageResponseIsUnavailableProductImage() =>
        AssertCategories(Observation(requests: [new(40, "failed", null, null, "net::ERR_CONNECTION_REFUSED")]), Category.UnavailableProductImage);

    [TestMethod]
    public void AbortedPrerenderRequestFollowedByImageIsNotUnavailable() =>
        AssertCategories(
            Observation(
                requests: [new(10, "failed", null, null, "net::ERR_ABORTED"), new(90, "response", 200, "image/png", null)],
                imageVisibleAtFailure: true),
            Category.TestSynchronization);

    [TestMethod]
    public void TileFailureMarkerIsUnavailableProductImage() =>
        AssertCategories(Observation(failedMarker: true), Category.UnavailableProductImage);

    [TestMethod]
    public void FailedAuthenticatedReReadIsUnavailableProductImage() =>
        AssertCategories(Observation(probe: new(404, "application/problem+json", 90, false, null)), Category.UnavailableProductImage);

    [TestMethod]
    public void UnreadableAuthenticatedReReadIsUnavailableProductImage() =>
        AssertCategories(Observation(probe: new(null, null, 0, false, "TimeoutException")), Category.UnavailableProductImage);

    [TestMethod]
    public void PageSignalIsPageErrorAndSuppressesSynchronizationVerdicts() =>
        AssertCategories(Observation(signals: ["dom:reconnect-dialog"], imageVisibleAtFailure: true, dayLoadingAfterSettle: true), Category.PageError);

    [TestMethod]
    public void IndependentCausesAreAllReported() =>
        AssertCategories(
            Observation(onDayAtFailure: false, onDayAfterSettle: false, failedMarker: true, signals: ["console:connection-lost"]),
            Category.LostNavigation, Category.UnavailableProductImage, Category.PageError);

    [TestMethod]
    [DataRow("http://127.0.0.1:5130/archive/day/2026-10-01?calendar=hvo-sunrise-reporting-period-v1#day-candidates", "/archive/day/2026-10-01?calendar=hvo-sunrise-reporting-period-v1")]
    [DataRow("http://127.0.0.1:5130/archive/calendar?month=2026-10&OPAQUE_QUERYNAME_1916=1", "/archive/calendar?month=2026-10&[other-query]")]
    [DataRow("http://127.0.0.1:5130/archive/calendar?calendar=OPAQUE_1916&month=OPAQUE_1916", "/archive/calendar?calendar=[other]&month=[other]")]
    [DataRow("http://127.0.0.1:5130/archive/day/2026-02-30", "/archive/day/{invalid-date}")]
    [DataRow("/api/v1/operations/still-products/00000000-0000-0000-0000-000000001916/preview?download=1", "/api/v1/operations/still-products/{expected-product}/preview?download=1")]
    [DataRow("/api/v1/operations/still-products/11111111-1111-1111-1111-111111111111/preview", "/api/v1/operations/still-products/{other-product}/preview")]
    [DataRow("/api/v1/operations/still-products/OPAQUE_1916/preview?OPAQUE_QUERYNAME_1916=1", "/api/v1/operations/still-products/{invalid-product}/preview?[other-query]")]
    [DataRow("/archive/products/nightly/00000000-0000-0000-0000-000000001916", "/archive/products/nightly/{expected-product}")]
    [DataRow("/api/v1/operations/gallery/OPAQUE_1916/thumbnail", "/api/v1/operations/gallery/{capture}/thumbnail")]
    [DataRow("/archive/products?view=retained", "/archive/products?view=retained")]
    [DataRow("http://127.0.0.1:5130/_blazor?id=opaque-circuit-token", "/_blazor?[other-query]")]
    [DataRow("http://127.0.0.1:5130/Account/Login?ReturnUrl=%2Farchive%2Fday%2F2026-10-01", "/Account/Login?[other-query]")]
    [DataRow("https://owner:password@opaque-host-1916.example/Account/Login?ReturnUrl=%2Fsecret", "[other-origin]")]
    [DataRow("http://127.0.0.1:5130/OPAQUE_1916/route", "[other-route]")]
    [DataRow("about:blank", "about:blank")]
    [DataRow("data:text/html,OPAQUE_1916", "[unparsed]")]
    [DataRow("", "[empty]")]
    public void RoutesKeepOnlyKnownShapesWithPublicComponents(string address, string expected) =>
        Assert.AreEqual(expected, CalendarEvidenceVocabulary.RouteShape(address, ExpectedProduct, AppBaseAddress));

    [TestMethod]
    [DataRow("error", "[2026-11-22T13:45:16.123Z] Error: There was an unhandled exception on the current circuit, so this circuit will be terminated.", "console:circuit-terminated")]
    [DataRow("error", "[2026-11-22T13:45:16.123Z] Error: Connection disconnected with error 'Error: WebSocket closed with status code: 1006 ().'.", "console:connection-lost")]
    [DataRow("error", "Error: Invocation canceled due to the underlying connection being closed.", "console:circuit-closed")]
    [DataRow("error", "Failed to load resource: the server responded with a status of 503 ()", "console:resource-load-failed")]
    [DataRow("warning", "[2026-11-22T13:45:16.123Z] Warning: token=opaque-value", "console:blazor-warning")]
    [DataRow("error", "password=hunter2 something else", "console:other-error")]
    [DataRow("log", "Connection disconnected", null)]
    [DataRow("info", "[2026-11-22T13:45:16.123Z] Information: WebSocket connected", null)]
    public void ConsoleMessagesBecomeBoundedLabels(string type, string text, string? expected) =>
        Assert.AreEqual(expected, CalendarDayPreviewEvidence.ClassifyConsole(type, text));

    [TestMethod]
    public void OnlyCircuitAndBlazorErrorsAreConsolePageErrors()
    {
        Assert.IsTrue(CalendarDayPreviewEvidence.IsPageErrorConsoleLabel("console:circuit-terminated"));
        Assert.IsTrue(CalendarDayPreviewEvidence.IsPageErrorConsoleLabel("console:connection-lost"));
        Assert.IsTrue(CalendarDayPreviewEvidence.IsPageErrorConsoleLabel("console:blazor-error"));
        Assert.IsFalse(CalendarDayPreviewEvidence.IsPageErrorConsoleLabel("console:resource-load-failed"));
        Assert.IsFalse(CalendarDayPreviewEvidence.IsPageErrorConsoleLabel("console:other-error"));
        Assert.IsFalse(CalendarDayPreviewEvidence.IsPageErrorConsoleLabel("console:blazor-warning"));
    }

    [TestMethod]
    [DataRow("TypeError: Cannot read properties of null (reading 'password')", "TypeError")]
    [DataRow("Error: secret=value\n    at http://127.0.0.1/app.js:1:1", "Error")]
    [DataRow("Uncaught ReferenceError: x is not defined", "ReferenceError")]
    [DataRow("OwnerSecretError: hunter2", "OTHER")]
    [DataRow("hunter2", "OTHER")]
    [DataRow("", "OTHER")]
    public void PageErrorsKeepOnlyBuiltInErrorNames(string error, string expected) =>
        Assert.AreEqual(expected, CalendarDayPreviewEvidence.ErrorName(error));

    [TestMethod]
    [DataRow("net::ERR_ABORTED", "net::ERR_ABORTED")]
    [DataRow("net::ERR_CONNECTION_REFUSED", "net::ERR_CONNECTION_REFUSED")]
    [DataRow("net::ERR_FAILED at /home/user/secret", "OTHER")]
    [DataRow("net::ERR_OPAQUE_1916", "OTHER")]
    [DataRow(null, "OTHER")]
    public void NetworkFailuresKeepOnlyChromiumErrorCodes(string? failure, string expected) =>
        Assert.AreEqual(expected, CalendarDayPreviewEvidence.BoundedNetworkError(failure));

    [TestMethod]
    [DataRow("image/png", "image/png")]
    [DataRow("IMAGE/PNG; charset=binary", "image/png")]
    [DataRow("text/plain; charset=utf-8", "text/plain")]
    [DataRow("application/x-secret-token", "other")]
    [DataRow(null, "none")]
    public void ContentTypesAreBounded(string? contentType, string expected) =>
        Assert.AreEqual(expected, CalendarDayPreviewEvidence.BoundedContentType(contentType));

    [TestMethod]
    public void ImageSignaturesAreRecognizedWithoutTrustingContentType()
    {
        Assert.IsTrue(CalendarDayPreviewEvidence.HasImageSignature([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
        Assert.IsTrue(CalendarDayPreviewEvidence.HasImageSignature([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.IsTrue(CalendarDayPreviewEvidence.HasImageSignature("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.IsFalse(CalendarDayPreviewEvidence.HasImageSignature("<!DOCTYPE html>"u8));
        Assert.IsFalse(CalendarDayPreviewEvidence.HasImageSignature([]));
    }

    [TestMethod]
    [DataRow("Timeout 30000ms exceeded.\nCall log:\n  - OPAQUE_FAILURE_LINE_1916", 30000)]
    [DataRow("Timeout 2500ms exceeded.", 2500)]
    [DataRow("OPAQUE_FAILURE_LINE_1916 Timeout 30000ms exceeded.", null)]
    [DataRow("Assert.AreEqual failed. Expected:<partial>. Actual:<OPAQUE_1916>.", null)]
    public void FailureMessagesKeepOnlyThePlaywrightTimeout(string message, int? expected) =>
        Assert.AreEqual(expected, CalendarDayPreviewEvidence.TimeoutMilliseconds(message));

    [TestMethod]
    public void MarkupKeepsOnlyAllowlistedStructureAttributesAndLabels()
    {
        using var tree = JsonDocument.Parse("""
            {"t":"section","a":[["class","daily-products OPAQUE_CLASS_1916"],["aria-busy","false"],["data-opaque","OPAQUE_ATTRIBUTE_1916"],["style","OPAQUE_STYLE_1916"]],"c":[
              {"t":"article","a":[["class","product-slot"],["data-product-kind","star trail"],["aria-label","OPAQUE_LABEL_1916"]],"c":["OPAQUE_OTHER_TILE_1916"]},
              {"t":"article","a":[["class","product-slot OPAQUE_CLASS_1916"],["data-product-kind","daily summary"],["data-nightly-state","OPAQUE_STATE_1916"],["id","OPAQUE_ID_1916"],["title","OPAQUE_TITLE_1916"],["onclick","OPAQUE_HANDLER_1916"]],"c":[
                {"t":"button","a":[["type","button"],["class","product-slot__media"],["aria-haspopup","dialog"]],"c":[
                  {"t":"span","a":[["class","product-slot__failed"]],"c":[{"t":"i","a":[["class","bi bi-image"]],"c":[]},"Preview unavailable"]}]},
                {"t":"h3","a":[],"c":["Daily summary"]},
                {"t":"p","a":[],"c":["OPAQUE_TEXT_1916"]},
                {"t":"span","a":[["class","hvo-chip hvo-chip--warning"],["title","OPAQUE_CHIP_TITLE_1916"]],"c":["Partial"]},
                {"t":"a","a":[["href","http://OPAQUE_USER_1916:OPAQUE_PASSWORD_1916@127.0.0.1:5130/archive/day/OPAQUE_LINK_SEGMENT_1916?OPAQUE_LINK_QUERYNAME_1916=1"]],"c":["open"]},
                {"t":"a","a":[["href","http://127.0.0.1:5131/archive/day/2026-10-01"]],"c":["Daily summary"]},
                {"t":"img","a":[["src","/api/v1/operations/still-products/00000000-0000-0000-0000-000000001916/preview?OPAQUE_IMAGE_QUERYNAME_1916=1"],["alt","OPAQUE_ALT_1916"],["loading","lazy"]],"c":[]},
                {"t":"time","a":[["datetime","OPAQUE_DATE_1916"]],"c":["1"]},
                {"t":"opaque-element-1916","a":[["class","product-slot"]],"c":["OPAQUE_CUSTOM_1916"]},
                {"t":"script","a":[],"c":["OPAQUE_SCRIPT_1916"]}]}]}
            """);

        var markup = CalendarEvidenceVocabulary.RenderMarkup(tree.RootElement, ExpectedProduct, AppBaseAddress, omitOtherProducts: true)!;

        foreach (var opaque in new[]
        {
            "OPAQUE_CLASS_1916", "OPAQUE_ATTRIBUTE_1916", "OPAQUE_STYLE_1916", "OPAQUE_LABEL_1916", "OPAQUE_OTHER_TILE_1916",
            "OPAQUE_STATE_1916", "OPAQUE_ID_1916", "OPAQUE_TITLE_1916", "OPAQUE_HANDLER_1916", "OPAQUE_TEXT_1916",
            "OPAQUE_CHIP_TITLE_1916", "OPAQUE_LINK_SEGMENT_1916", "OPAQUE_LINK_QUERYNAME_1916", "OPAQUE_IMAGE_QUERYNAME_1916", "OPAQUE_ALT_1916",
            "OPAQUE_DATE_1916", "opaque-element-1916", "OPAQUE_CUSTOM_1916", "OPAQUE_SCRIPT_1916", "00000000-0000-0000-0000-000000001916",
            "OPAQUE_USER_1916", "OPAQUE_PASSWORD_1916", "127.0.0.1"
        })
        {
            Assert.DoesNotContain(opaque, markup, StringComparison.Ordinal);
        }
        StringAssert.Contains(markup, "<section class=\"daily-products\" aria-busy=\"false\">", StringComparison.Ordinal);
        StringAssert.Contains(markup, "<article data-product-kind=\"star trail\" data-evidence=\"omitted\"></article>", StringComparison.Ordinal);
        StringAssert.Contains(markup, "<article class=\"product-slot\" data-product-kind=\"daily summary\">", StringComparison.Ordinal);
        StringAssert.Contains(markup, "<span class=\"product-slot__failed\"><i class=\"bi bi-image\"></i>Preview unavailable</span>", StringComparison.Ordinal);
        StringAssert.Contains(markup, "<h3>Daily summary</h3><p></p>", StringComparison.Ordinal);
        StringAssert.Contains(markup, ">Partial</span>", StringComparison.Ordinal);
        StringAssert.Contains(markup, "href=\"/archive/day/{invalid-date}?[other-query]\"", StringComparison.Ordinal);
        StringAssert.Contains(markup, "<a href=\"[other-origin]\">Daily summary</a>", StringComparison.Ordinal);
        StringAssert.Contains(markup, "<img src=\"/api/v1/operations/still-products/{expected-product}/preview?[other-query]\" loading=\"lazy\">", StringComparison.Ordinal);
        StringAssert.Contains(markup, "<time>1</time>", StringComparison.Ordinal);
    }

    [TestMethod]
    public void SnapshotKeepsOnlyAllowlistedFacts()
    {
        using var raw = JsonDocument.Parse("""
            {"interactive":"OPAQUE_INTERACTIVE_1916","readyState":"OPAQUE_READY_1916","dayPage":true,"dayLoading":false,
             "nightlyUnavailable":false,"previewFailedMarker":true,"tileState":"OPAQUE_TILE_STATE_1916",
             "image":{"present":true,"connected":true,"complete":true,"naturalWidth":0,"naturalHeight":0,
                      "src":"/archive/OPAQUE_SRC_1916","currentSrc":"http://opaque-host-1916.example/api/v1/operations/still-products/00000000-0000-0000-0000-000000001916/preview?OPAQUE_QUERYNAME_1916=1",
                      "loading":"OPAQUE_LOADING_1916","width":10,"height":10,"visible":true},
             "blazorErrorUi":false,"errorBoundary":false,
             "reconnect":{"open":true,"classes":["components-reconnect-show","OPAQUE_RECONNECT_1916"]},
             "notices":[{"classes":["page-state","page-state--error","OPAQUE_NOTICE_CLASS_1916"],"title":"OPAQUE_NOTICE_TITLE_1916"},
                        {"classes":["page-state"],"title":"Loading observing day"}],
             "trees":{"calendarCell":null,"dayTile":{"t":"article","a":[["data-product-kind","daily summary"]],"c":["OPAQUE_TILE_TEXT_1916"]},
                      "dailyProducts":null,"pageStates":[{"t":"section","a":[["class","page-state OPAQUE_PAGE_STATE_1916"]],"c":["OPAQUE_PAGE_STATE_TEXT_1916"]}]}}
            """);

        var snapshot = PageSnapshot.From(raw.RootElement, ExpectedProduct, AppBaseAddress);
        var retained = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("OPAQUE", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-host-1916", retained, StringComparison.Ordinal);
        Assert.AreEqual("other", snapshot.Interactive);
        Assert.AreEqual("other", snapshot.ReadyState);
        Assert.AreEqual("other", snapshot.TileState);
        Assert.AreEqual("[other-route]", snapshot.Image.Source);
        Assert.AreEqual("[other-origin]", snapshot.Image.CurrentSource);
        CollectionAssert.AreEqual(ExpectedReconnectStates, snapshot.ReconnectStates.ToArray());
        Assert.AreEqual(new PageNotice("page-state--error", "other"), snapshot.Notices[0]);
        Assert.AreEqual(new PageNotice("page-state", "Loading observing day"), snapshot.Notices[1]);
        CollectionAssert.AreEquivalent(ExpectedSnapshotSignals, snapshot.Signals.ToArray());
    }

    [TestMethod]
    public void PageErrorSignalsComeFromThePageAndCircuitOnly()
    {
        var clean = PageSnapshot.FromUnreadable("unreadable");

        Assert.IsEmpty(CalendarDayPreviewEvidence.ComposePageErrorSignals([], clean, clean));
        CollectionAssert.AreEqual(
            ExpectedComposedSignals,
            CalendarDayPreviewEvidence.ComposePageErrorSignals(["script-error:TypeError", "console:connection-lost", "script-error:TypeError"], clean).ToArray());
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task BoundedAwaitGivesUpAtItsCap() =>
        _ = await VerifyBoundedAwaitAsync().ConfigureAwait(false);

    internal static async Task<TimeSpan> VerifyBoundedAwaitAsync()
    {
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CalendarDayPreviewEvidence.Bounded(never.Task, TimeSpan.FromMilliseconds(100), CancellationToken.None)).ConfigureAwait(false);

        return started.Elapsed;
    }

    [TestMethod]
    public void ServerLogKeepsCoarseComponentLevelEventIdAndExceptionTypeOnly()
    {
        using var provider = new ServerLogEvidence();
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost");
        Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
        logger.Log(LogLevel.Information, default, "Circuit opaque-circuit-id started for owner@cameraagent.browser", null,
            static (state, _) => state);
        logger.Log(LogLevel.Error, new EventId(111, "OPAQUE_EVENT_1916"),
            "Unhandled exception in circuit 'opaque-circuit-id' for owner@cameraagent.browser",
            new InvalidOperationException("Circuit opaque-circuit-id failed for password=hunter2", new TimeoutException("inner secret")),
            static (state, _) => state);
        provider.CreateLogger("OPAQUE_CATEGORY_1916 secret=1")
            .Log(LogLevel.Warning, new EventId(7, "bad name!"), "warning", null, static (state, _) => state);
        provider.CreateLogger("HVO.SkyMonitor.CameraAgent.OPAQUE_COMPONENT_1916")
            .Log(LogLevel.Warning, new EventId(8), "warning", null, static (state, _) => state);

        var entries = provider.Snapshot();
        Assert.HasCount(3, entries);
        Assert.AreEqual(new ServerLogEntry(entries[0].AtUtc, LogLevel.Error, "Microsoft.AspNetCore.Components", 111,
            typeof(InvalidOperationException).FullName, typeof(TimeoutException).FullName), entries[0]);
        Assert.AreEqual("other", entries[1].Category);
        Assert.AreEqual("HVO.SkyMonitor.CameraAgent", entries[2].Category);
        var retained = JsonSerializer.Serialize(entries);
        foreach (var secret in new[] { "opaque-circuit-id", "owner@cameraagent.browser", "hunter2", "inner secret", "OPAQUE", "secret=1", "bad name" })
        {
            Assert.DoesNotContain(secret, retained, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void ReportJsonCarriesTheCategoriesAndEvidenceSections()
    {
        var observation = Observation(failedMarker: true, requests: [new(40, "response", 503, "text/plain", null)]);
        var categories = CalendarDayPreviewClassifier.Classify(observation);
        var markup = new Dictionary<string, string?>(StringComparer.Ordinal) { ["calendarCell"] = null, ["dayTile"] = "<article></article>", ["dailyProducts"] = null };
        var snapshot = new PageSnapshot(null, "true", "complete", true, false, false, true, "partial",
            new PageImageState(false, false, false, 0, 0, null, null, null, 0, 0, false), false, false, false, [], [], markup, []);
        var report = new CalendarDayPreviewReport(
            "Class", "Method", "1440x900-partial", "2026-10-01", "/archive/day/2026-10-01",
            "/api/v1/operations/still-products/{expected-product}/preview", "preview image", "System.TimeoutException", 30_000,
            categories, observation, "true", "/archive/calendar?month=2026-10", "/archive/day/2026-10-01", "/archive/day/2026-10-01",
            0, 30_000, 10_000, snapshot, snapshot, [new("console:resource-load-failed", 1)],
            [new(250, LogLevel.Error, "HVO.SkyMonitor.CameraAgent", 1, "System.Exception", null)],
            [new(1, 0, "click", "interactive=true route=/archive/calendar?month=2026-10")], null);

        using var document = JsonDocument.Parse(report.ToJson());
        var root = document.RootElement;
        Assert.AreEqual(CalendarDayPreviewEvidence.Schema, root.GetProperty("schema").GetString());
        Assert.AreEqual("/api/v1/operations/still-products/{expected-product}/preview", root.GetProperty("previewRoute").GetString());
        Assert.AreEqual(30_000, root.GetProperty("failure").GetProperty("timeoutMs").GetInt32());
        Assert.IsFalse(root.GetProperty("failure").TryGetProperty("messageLines", out _));
        Assert.AreEqual("UnavailableProductImage", root.GetProperty("categories")[0].GetString());
        Assert.AreEqual(503, root.GetProperty("preview").GetProperty("browserRequests")[0].GetProperty("status").GetInt32());
        Assert.IsTrue(root.GetProperty("preview").GetProperty("tileFailureMarker").GetBoolean());
        Assert.AreEqual("<article></article>", root.GetProperty("atFailure").GetProperty("markup").GetProperty("dayTile").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("atFailure").GetProperty("markup").GetProperty("calendarCell").ValueKind);
        Assert.AreEqual(1, root.GetProperty("consoleLabels").GetProperty("console:resource-load-failed").GetInt32());
        Assert.AreEqual("HVO.SkyMonitor.CameraAgent", root.GetProperty("hostLogContext")[0].GetProperty("category").GetString());
        StringAssert.Contains(report.Summary("file.json"), "categories=UnavailableProductImage", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task NaturalSuccessPublishesOneCompleteFile()
    {
        var root = RetentionRoot();
        var attached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{\"schema\":\"complete\"}"), static _ => "case.evidence.json", static value => value,
                Store(root, attach: attached.Enqueue), TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.Published, result.Outcome);
            Assert.AreEqual("attached", result.AttachmentNote());
            CollectionAssert.AreEqual(new[] { result.Path }, attached.ToArray());
            Assert.IsFalse(result.Expired);
            CollectionAssert.AreEqual(new[] { result.Path }, Directory.GetFiles(root));
            Assert.AreEqual("{\"schema\":\"complete\"}", await File.ReadAllTextAsync(result.Path!).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("capture", DisplayName = "capture that stalls synchronously before returning")]
    [DataRow("writer", DisplayName = "writer that stalls synchronously before returning")]
    [DataRow("async-writer", DisplayName = "writer that stalls asynchronously")]
    public async Task StalledAttemptsNeverPublishLate(string stall) =>
        _ = await VerifyStalledAttemptsAsync(stall).ConfigureAwait(false);

    internal static async Task<TimeSpan> VerifyStalledAttemptsAsync(string stall)
    {
        var root = RetentionRoot();
        try
        {
            var capture = stall == "capture" ? SynchronouslyStalledCapture() : static _ => Task.FromResult("{}");
            var writer = stall switch
            {
                "writer" => SynchronouslyStalledWriter(),
                "async-writer" => AsynchronouslyStalledWriter(),
                _ => null
            };
            var started = System.Diagnostics.Stopwatch.StartNew();

            var result = await EvidenceRetention.RunAsync(capture, static _ => "case.evidence.json", static value => value,
                Store(root, writer), Budget).ConfigureAwait(false);

            var elapsed = started.Elapsed;
            Assert.AreEqual(EvidenceRetentionOutcome.NotPublished, result.Outcome);
            Assert.IsTrue(result.Expired);
            StringAssert.Contains(result.Describe("row", Budget), "cannot publish or attach", StringComparison.Ordinal);
            await Completion(result.Operation).ConfigureAwait(false);
            Assert.IsEmpty(Directory.Exists(root) ? Directory.GetFiles(root) : [], "the late attempt published nothing and removed its temporary file");
            return elapsed;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TimeoutDuringTheDurableWriteNeverPublishesAHalfFile()
    {
        var root = RetentionRoot();
        var halfWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult(new string('x', 4096)), static _ => "case.evidence.json", static value => value,
                Store(root, async (path, content, _) =>
                {
                    var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
                    await using (stream.ConfigureAwait(false))
                    {
                        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content[..2048]), CancellationToken.None).ConfigureAwait(false);
                        await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                        halfWritten.TrySetResult();
                        await Task.Delay(Stall, CancellationToken.None).ConfigureAwait(false);
                        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(content[2048..]), CancellationToken.None).ConfigureAwait(false);
                    }
                }), Budget).ConfigureAwait(false);

            await halfWritten.Task.ConfigureAwait(false);
            Assert.AreEqual(EvidenceRetentionOutcome.NotPublished, result.Outcome);
            Assert.IsTrue(result.Expired);
            Assert.IsFalse(Directory.GetFiles(root).Any(static file => file.EndsWith(".evidence.json", StringComparison.Ordinal)),
                "a half-written file is never published under its final name");
            await Completion(result.Operation).ConfigureAwait(false);
            Assert.IsEmpty(Directory.GetFiles(root), "the completed late write is refused and its temporary file removed");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task TimeoutDuringPublicationIsReportedAsUnconfirmed()
    {
        var root = RetentionRoot();
        var attached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{\"schema\":\"complete\"}"), static _ => "case.evidence.json", static value => value,
                Store(root, publish: static (temporary, path) =>
                {
                    Thread.Sleep(Stall);
                    File.Move(temporary, path);
                }, attach: attached.Enqueue), Budget).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.PublicationUnconfirmed, result.Outcome);
            Assert.AreEqual("case.evidence.json", Path.GetFileName(result.PendingPath));
            StringAssert.Contains(result.Describe("row", Budget), "may appear but is never attached", StringComparison.Ordinal);
            await Completion(result.Operation).ConfigureAwait(false);
            Assert.IsEmpty(attached, "an attachment that had not started by the deadline never starts");
            CollectionAssert.AreEqual(new[] { result.PendingPath }, Directory.GetFiles(root), "the rename that was in progress completed with the whole file");
            Assert.AreEqual("{\"schema\":\"complete\"}", await File.ReadAllTextAsync(result.PendingPath!).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false, DisplayName = "throwing cancellation callback, writer fails immediately")]
    [DataRow(true, DisplayName = "throwing cancellation callback, writer stalls")]
    public async Task ThrowingCancellationCallbacksStayOffTheCallersPath(bool stalls) =>
        _ = await VerifyThrowingCancellationCallbacksAsync(stalls).ConfigureAwait(false);

    internal static async Task<TimeSpan> VerifyThrowingCancellationCallbacksAsync(bool stalls)
    {
        var root = RetentionRoot();
        var unobserved = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception.ToString().Contains("FAULT_1916", StringComparison.Ordinal)) unobserved.Enqueue(args.Exception);
        };
        TaskScheduler.UnobservedTaskException += observe;
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();

            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root, async (_, _, token) =>
                {
                    token.Register(static () => throw new InvalidOperationException("CALLBACK_FAULT_1916"));
                    if (stalls) await Task.Delay(Stall, CancellationToken.None).ConfigureAwait(false);
                    throw new IOException("WRITE_FAULT_1916");
                }), Budget).ConfigureAwait(false);

            var elapsed = started.Elapsed;
            Assert.AreEqual(EvidenceRetentionOutcome.NotPublished, result.Outcome);
            Assert.AreEqual(stalls, result.Expired);
            Assert.AreEqual(stalls ? null : typeof(IOException).FullName, result.FailureType);
            await Completion(result.Operation).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsEmpty(unobserved, "a callback or write fault went unobserved");
            Assert.IsEmpty(Directory.Exists(root) ? Directory.GetFiles(root) : []);
            return elapsed;
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= observe;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SynchronousAttachmentThatBlocksAfterTheRenameIsUnconfirmed() =>
        _ = await VerifyBlockedAttachmentAsync().ConfigureAwait(false);

    internal static async Task<TimeSpan> VerifyBlockedAttachmentAsync()
    {
        var root = RetentionRoot();
        var attached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();

            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{\"schema\":\"complete\"}"), static _ => "case.evidence.json", static value => value,
                Store(root, attach: path =>
                {
                    Thread.Sleep(Stall);
                    attached.Enqueue(path);
                }), Budget).ConfigureAwait(false);

            var elapsed = started.Elapsed;
            Assert.AreEqual(EvidenceRetentionOutcome.AttachmentUnconfirmed, result.Outcome);
            StringAssert.Contains(result.AttachmentNote(), "not confirmed by the deadline; it may still complete", StringComparison.Ordinal);
            Assert.AreEqual("{\"schema\":\"complete\"}", await File.ReadAllTextAsync(result.Path!).ConfigureAwait(false),
                "the file was complete before attaching started");
            await Completion(result.Operation).ConfigureAwait(false);
            CollectionAssert.AreEqual(new[] { result.Path }, attached.ToArray(), "the attachment that had started completes late");
            return elapsed;
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AttachmentThatThrowsImmediatelyLeavesTheCompleteFileLocal()
    {
        var root = RetentionRoot();
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{\"schema\":\"complete\"}"), static _ => "case.evidence.json", static value => value,
                Store(root, attach: static _ => throw new IOException("attach failed")), TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.PublishedNotAttached, result.Outcome);
            Assert.IsFalse(result.Expired);
            Assert.AreEqual(typeof(IOException).FullName, result.FailureType);
            StringAssert.Contains(result.AttachmentNote(), "complete local file only", StringComparison.Ordinal);
            CollectionAssert.AreEqual(new[] { result.Path }, Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AttachmentThatFaultsLateIsUnconfirmedAndObserved()
    {
        var root = RetentionRoot();
        var unobserved = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception.ToString().Contains("FAULT_1916", StringComparison.Ordinal)) unobserved.Enqueue(args.Exception);
        };
        TaskScheduler.UnobservedTaskException += observe;
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root, attach: static _ =>
                {
                    Thread.Sleep(Stall);
                    throw new IOException("LATE_ATTACH_FAULT_1916");
                }), Budget).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.AttachmentUnconfirmed, result.Outcome);
            Assert.IsTrue(result.Expired);
            await Completion(result.Operation).ConfigureAwait(false);
            await Task.Delay(200).ConfigureAwait(false);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.IsEmpty(unobserved, "the late attachment fault went unobserved");
            CollectionAssert.AreEqual(new[] { result.Path }, Directory.GetFiles(root), "the published file stays complete");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= observe;
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task WriteThatCrossesTheCutoffIsNeverPublishedWhenTheTimerIsLate()
    {
        var root = RetentionRoot();
        var clock = new ManualTimeProvider();
        var attached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            // The caller's timer never fires; only the operation's completion wakes it, after the real cutoff.
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root, async (path, content, _) =>
                {
                    clock.Advance(CrossedBy);
                    await CalendarEvidenceStore.DurableWriteAsync(path, content, CancellationToken.None).ConfigureAwait(false);
                }, attach: attached.Enqueue), CutoffBudget, clock).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.NotPublished, result.Outcome);
            Assert.IsTrue(result.Expired);
            Assert.IsEmpty(attached);
            Assert.IsEmpty(Directory.Exists(root) ? Directory.GetFiles(root) : [], "a write that crossed the cutoff is never published");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RenameThatCrossesTheCutoffIsUnconfirmedAndNeverAttached()
    {
        var root = RetentionRoot();
        var clock = new ManualTimeProvider();
        var attached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{\"schema\":\"complete\"}"), static _ => "case.evidence.json", static value => value,
                Store(root, publish: (temporary, path) =>
                {
                    File.Move(temporary, path);
                    clock.Advance(CrossedBy);
                }, attach: attached.Enqueue), CutoffBudget, clock).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.PublicationUnconfirmed, result.Outcome);
            Assert.IsTrue(result.Expired);
            Assert.IsEmpty(attached, "no attachment starts after the cutoff");
            Assert.AreEqual("{\"schema\":\"complete\"}", await File.ReadAllTextAsync(Path.Combine(root, "case.evidence.json")).ConfigureAwait(false),
                "the rename that started in time completed with the whole file");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PublicationAndAttachmentBeforeTheCutoffSucceedWhenTheTimerIsLate()
    {
        var root = RetentionRoot();
        var clock = new ManualTimeProvider();
        var attached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root, attach: attached.Enqueue), CutoffBudget, clock).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.Published, result.Outcome);
            Assert.IsFalse(result.Expired);
            CollectionAssert.AreEqual(new[] { result.Path }, attached.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task AttachmentInFlightAtTheCutoffStaysUnconfirmed()
    {
        var root = RetentionRoot();
        var clock = new ManualTimeProvider();
        var attached = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        try
        {
            var running = EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root, attach: path =>
                {
                    started.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                    attached.Enqueue(path);
                }), CutoffBudget, clock);
            Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)));
            clock.Advance(CrossedBy);
            clock.FireTimers();

            var result = await running.ConfigureAwait(false);
            release.Set();
            await Completion(result.Operation).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.AttachmentUnconfirmed, result.Outcome);
            Assert.IsTrue(result.Expired);
            CollectionAssert.AreEqual(new[] { result.Path }, attached.ToArray(), "the attachment that had started completed late");
        }
        finally
        {
            release.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("write", "before", DisplayName = "write fault before the cutoff")]
    [DataRow("write", "fault-first", DisplayName = "write fault after the cutoff, fault completes first")]
    [DataRow("write", "timer-first", DisplayName = "write fault after the cutoff, timer runs first")]
    [DataRow("rename", "before", DisplayName = "rename fault before the cutoff")]
    [DataRow("rename", "fault-first", DisplayName = "rename fault after the cutoff, fault completes first")]
    [DataRow("rename", "timer-first", DisplayName = "rename fault after the cutoff, timer runs first")]
    [DataRow("attach", "before", DisplayName = "attachment fault before the cutoff")]
    [DataRow("attach", "fault-first", DisplayName = "attachment fault after the cutoff, fault completes first")]
    [DataRow("attach", "timer-first", DisplayName = "attachment fault after the cutoff, timer runs first")]
    public async Task PhaseFaultsAreJudgedByWhenThePhaseEnded(string phase, string timing)
    {
        var root = RetentionRoot();
        var clock = new ManualTimeProvider();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        void Fault()
        {
            if (timing == "fault-first")
            {
                clock.Advance(CrossedBy);
            }
            else if (timing == "timer-first")
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
            throw new IOException("PHASE_FAULT_1916");
        }
        try
        {
            var running = EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{\"schema\":\"complete\"}"), static _ => "case.evidence.json", static value => value,
                Store(root,
                    writer: phase == "write" ? (_, _, _) => Task.Run(Fault, CancellationToken.None) : null,
                    publish: phase == "rename" ? (_, _) => Fault() : null,
                    attach: phase == "attach" ? _ => Fault() : null),
                CutoffBudget, clock);
            if (timing == "timer-first")
            {
                Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(30)));
                clock.Advance(CrossedBy);
                clock.FireTimers();
            }
            var result = await running.ConfigureAwait(false);
            release.Set();
            await Completion(result.Operation).ConfigureAwait(false);

            var late = timing != "before";
            var expected = (phase, late) switch
            {
                ("write", _) => EvidenceRetentionOutcome.NotPublished,
                ("rename", false) => EvidenceRetentionOutcome.NotPublished,
                ("rename", true) => EvidenceRetentionOutcome.PublicationUnconfirmed,
                (_, false) => EvidenceRetentionOutcome.PublishedNotAttached,
                _ => EvidenceRetentionOutcome.AttachmentUnconfirmed
            };
            Assert.AreEqual(expected, result.Outcome);
            Assert.AreEqual(late, result.Expired);
            Assert.AreEqual(late ? null : typeof(IOException).FullName, result.FailureType,
                "a phase that ended after the cutoff is judged like one still running, whether it succeeded or threw");
            Assert.IsFalse(Directory.GetFiles(root).Any(static file => file.EndsWith(".partial", StringComparison.Ordinal)));
            Assert.AreEqual(phase == "attach", File.Exists(Path.Combine(root, "case.evidence.json")),
                "only a completed rename leaves a published file");
        }
        finally
        {
            release.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("rename", 0L, DisplayName = "rename ending exactly at the cutoff")]
    [DataRow("attach", 0L, DisplayName = "attachment ending exactly at the cutoff")]
    [DataRow("attach", -1L, DisplayName = "attachment ending one tick before the cutoff")]
    public async Task APhaseEndingAtTheCutoffIsLate(string phase, long ticksFromCutoff)
    {
        var root = RetentionRoot();
        var clock = new ManualTimeProvider();
        var reachCutoff = TimeSpan.FromTicks(CutoffBudget.Ticks + ticksFromCutoff);
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root,
                    publish: phase == "rename" ? (temporary, path) =>
                    {
                        File.Move(temporary, path);
                        clock.Advance(reachCutoff);
                    }
            : null,
                    attach: phase == "attach" ? _ => clock.Advance(reachCutoff) : null),
                CutoffBudget, clock).ConfigureAwait(false);

            var expected = ticksFromCutoff < 0 ? EvidenceRetentionOutcome.Published
                : phase == "rename" ? EvidenceRetentionOutcome.PublicationUnconfirmed
                : EvidenceRetentionOutcome.AttachmentUnconfirmed;
            Assert.AreEqual(expected, result.Outcome);
            Assert.AreEqual(ticksFromCutoff >= 0, result.Expired);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("rename", "held-timer-first", DisplayName = "rename gate check held across the cutoff, timer seals first")]
    [DataRow("rename", "held-refusal-first", DisplayName = "rename gate check held across the cutoff, refusal first")]
    [DataRow("rename", "admitted", DisplayName = "rename admitted in time, then held across the cutoff")]
    [DataRow("rename", "success", DisplayName = "rename admitted after a paused check, ordinary success")]
    [DataRow("attach", "held-timer-first", DisplayName = "attachment gate check held across the cutoff, timer seals first")]
    [DataRow("attach", "held-refusal-first", DisplayName = "attachment gate check held across the cutoff, refusal first")]
    [DataRow("attach", "admitted", DisplayName = "attachment admitted in time, then held across the cutoff")]
    [DataRow("attach", "success", DisplayName = "attachment admitted after a paused check, ordinary success")]
    [SuppressMessage("Usage", "CA1849:Call async methods when in an async method",
        Justification = "The writer stays synchronous so the gate's next clock read happens on the same worker thread and can be held.")]
    public async Task OnlyAnAdmittedPhaseIsSeenAsStarted(string phase, string scenario)
    {
        var root = RetentionRoot();
        var clock = new ManualTimeProvider();
        var invocations = 0;
        using var invoked = new ManualResetEventSlim();
        using var releasePhase = new ManualResetEventSlim();
        void RunPhase(Action work)
        {
            Interlocked.Increment(ref invocations);
            invoked.Set();
            if (scenario == "admitted") releasePhase.Wait(TimeSpan.FromSeconds(30));
            work();
        }
        try
        {
            var running = EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root,
                    writer: (path, content, _) =>
                    {
                        File.WriteAllText(path, content);
                        if (phase == "rename") clock.HoldNextRead(skip: 0);
                        return Task.CompletedTask;
                    },
                    publish: (temporary, path) =>
                    {
                        if (phase == "rename") RunPhase(() => File.Move(temporary, path));
                        else File.Move(temporary, path);
                        if (phase == "attach") clock.HoldNextRead(skip: 1);
                    },
                    attach: phase == "attach" ? _ => RunPhase(static () => { }) : null),
                CutoffBudget, clock);
            Assert.IsTrue(clock.Held.Wait(TimeSpan.FromSeconds(30)), "the gate's cutoff check was reached and held");
            EvidenceRetentionResult<string> result;
            switch (scenario)
            {
                case "held-timer-first":
                    clock.Advance(CrossedBy);
                    clock.FireTimers();
                    result = await running.ConfigureAwait(false);
                    clock.ReleaseHeld.Set();
                    break;
                case "held-refusal-first":
                    clock.Advance(CrossedBy);
                    clock.ReleaseHeld.Set();
                    result = await running.ConfigureAwait(false);
                    break;
                case "admitted":
                    clock.ReleaseHeld.Set();
                    Assert.IsTrue(invoked.Wait(TimeSpan.FromSeconds(30)));
                    clock.Advance(CrossedBy);
                    clock.FireTimers();
                    result = await running.ConfigureAwait(false);
                    releasePhase.Set();
                    break;
                default:
                    clock.ReleaseHeld.Set();
                    result = await running.ConfigureAwait(false);
                    break;
            }
            await Completion(result.Operation).ConfigureAwait(false);

            var (expected, expired, calls) = (phase, scenario) switch
            {
                (_, "success") => (EvidenceRetentionOutcome.Published, false, 1),
                ("rename", "admitted") => (EvidenceRetentionOutcome.PublicationUnconfirmed, true, 1),
                ("attach", "admitted") => (EvidenceRetentionOutcome.AttachmentUnconfirmed, true, 1),
                ("rename", _) => (EvidenceRetentionOutcome.NotPublished, true, 0),
                _ => (EvidenceRetentionOutcome.PublishedNotAttached, true, 0)
            };
            Assert.AreEqual(expected, result.Outcome, "only an admitted start can be reported as started");
            Assert.AreEqual(expired, result.Expired);
            Assert.AreEqual(calls, Volatile.Read(ref invocations), "a phase whose gate was sealed or refused is never invoked");
        }
        finally
        {
            clock.ReleaseHeld.Set();
            releasePhase.Set();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task StatusLineWaitsOnlyForTheRemainingBudget()
    {
        using var release = new ManualResetEventSlim();
        var written = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Blocked(string line)
        {
            release.Wait(TimeSpan.FromSeconds(30));
            written.Enqueue(line);
        }
        var clock = new ManualTimeProvider();

        var nothingLeft = EvidenceRetention.WriteStatusWithinAsync(Blocked, "late", TimeSpan.Zero, clock);
        var someLeft = EvidenceRetention.WriteStatusWithinAsync(Blocked, "in time", TimeSpan.FromMilliseconds(100), clock);

        Assert.IsTrue(nothingLeft.IsCompletedSuccessfully, "with no budget left the caller does not wait for the line");
        Assert.IsFalse(someLeft.IsCompleted, "otherwise it waits for the line up to the remaining budget");
        clock.FireTimers();
        await someLeft.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        release.Set();
        await Task.Run(async () =>
        {
            while (written.Count < 2) await Task.Delay(10).ConfigureAwait(false);
        }).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        CollectionAssert.AreEquivalent(ExpectedStatusLines, written.ToArray(), "both lines are still written off the caller's path");
    }

    [TestMethod]
    public async Task FailingWriteLeavesNoFile()
    {
        var root = RetentionRoot();
        try
        {
            var result = await EvidenceRetention.RunAsync(
                static _ => Task.FromResult("{}"), static _ => "case.evidence.json", static value => value,
                Store(root, static async (path, _, _) =>
                {
                    await File.WriteAllTextAsync(path, "{", CancellationToken.None).ConfigureAwait(false);
                    throw new IOException("partial write failed");
                }), TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            Assert.AreEqual(EvidenceRetentionOutcome.NotPublished, result.Outcome);
            Assert.IsFalse(result.Expired);
            Assert.AreEqual(typeof(IOException).FullName, result.FailureType);
            StringAssert.Contains(result.Describe("row", Budget), "nothing was published", StringComparison.Ordinal);
            Assert.IsEmpty(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Func<CancellationToken, Task<string>> SynchronouslyStalledCapture() => static _ =>
    {
        Thread.Sleep(Stall);
        return Task.FromResult("{}");
    };

    private static Func<string, string, CancellationToken, Task> SynchronouslyStalledWriter() => static (path, content, _) =>
    {
        Thread.Sleep(Stall);
        return CalendarEvidenceStore.DurableWriteAsync(path, content, CancellationToken.None);
    };

    private static Func<string, string, CancellationToken, Task> AsynchronouslyStalledWriter() => static async (path, content, _) =>
    {
        await Task.Delay(Stall, CancellationToken.None).ConfigureAwait(false);
        await CalendarEvidenceStore.DurableWriteAsync(path, content, CancellationToken.None).ConfigureAwait(false);
    };

    private static readonly string[] ExpectedStatusLines = ["late", "in time"];
    private static readonly TimeSpan CutoffBudget = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan CrossedBy = TimeSpan.FromMilliseconds(200);

    /// <summary>A clock that moves only when told to, and whose timers fire only when released: a late timer continuation.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<(TimerCallback Callback, object? State)> timers = new();
        private long now;
        private int heldThread;
        private int readsBeforeHold;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        /// <summary>Signalled when a read is held; the held read returns the clock as it is when released.</summary>
        internal ManualResetEventSlim Held { get; } = new();

        internal ManualResetEventSlim ReleaseHeld { get; } = new();

        /// <summary>Holds this thread's read after skipping <paramref name="skip"/> reads: a paused cutoff check.</summary>
        internal void HoldNextRead(int skip)
        {
            readsBeforeHold = skip;
            Volatile.Write(ref heldThread, Environment.CurrentManagedThreadId);
        }

        public override long GetTimestamp()
        {
            if (Volatile.Read(ref heldThread) == Environment.CurrentManagedThreadId)
            {
                if (readsBeforeHold-- <= 0)
                {
                    Volatile.Write(ref heldThread, 0);
                    Held.Set();
                    ReleaseHeld.Wait(TimeSpan.FromSeconds(30));
                }
            }
            return Interlocked.Read(ref now);
        }

        internal void Advance(TimeSpan by) => Interlocked.Add(ref now, by.Ticks);

        internal void FireTimers()
        {
            while (timers.TryDequeue(out var timer)) timer.Callback(timer.State);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            timers.Enqueue((callback, state));
            return new HeldTimer();
        }

        private sealed class HeldTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(3);

    private static string RetentionRoot() => Path.Combine(
        Path.GetTempPath(), "hvo-1916-retention-" + Guid.NewGuid().ToString("N"), "calendar-preview-failures");

    private static CalendarEvidenceStore Store(
        string root, Func<string, string, CancellationToken, Task>? writer = null, Action<string, string>? publish = null,
        Action<string>? attach = null) =>
        new(root, attach ?? (static _ => { }), static _ => { }, writer, publish);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The abandoned operation's own fault is the subject under test, observed separately.")]
    private static async Task Completion(Task operation)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Only completion matters here.
        }
    }

    private static void AssertCategories(CalendarDayPreviewObservation observation, params Category[] expected) =>
        CollectionAssert.AreEqual(expected, CalendarDayPreviewClassifier.Classify(observation).ToArray());

    private static CalendarDayPreviewObservation Observation(
        bool onDayAtFailure = true,
        bool onDayAfterSettle = true,
        bool failedMarker = false,
        IReadOnlyList<CalendarDayPreviewRequest>? requests = null,
        CalendarDayPreviewProbe? probe = null,
        IReadOnlyList<string>? signals = null,
        bool imageVisibleAtFailure = false,
        long? visibleAfterSettle = null,
        bool dayLoadingAfterSettle = false,
        bool assertionMismatch = false) =>
        new(onDayAtFailure, onDayAfterSettle, failedMarker, requests ?? [], probe ?? AvailableProbe, signals ?? [],
            imageVisibleAtFailure, visibleAfterSettle, dayLoadingAfterSettle, assertionMismatch);
}
