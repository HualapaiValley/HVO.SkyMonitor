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
    [DataRow("http://127.0.0.1:5130/archive/day/OPAQUE_SEGMENT_1916", "/archive/day/{invalid-date}")]
    [DataRow("http://127.0.0.1:5130/archive/day/2026-02-30", "/archive/day/{invalid-date}")]
    [DataRow("/api/v1/operations/still-products/00000000-0000-0000-0000-000000001916/preview?download=1", "/api/v1/operations/still-products/{expected-product}/preview?download=1")]
    [DataRow("/api/v1/operations/still-products/11111111-1111-1111-1111-111111111111/preview", "/api/v1/operations/still-products/{other-product}/preview")]
    [DataRow("/api/v1/operations/still-products/OPAQUE_1916/preview?OPAQUE_QUERYNAME_1916=1", "/api/v1/operations/still-products/{invalid-product}/preview?[other-query]")]
    [DataRow("/archive/products/nightly/00000000-0000-0000-0000-000000001916", "/archive/products/nightly/{expected-product}")]
    [DataRow("/api/v1/operations/gallery/OPAQUE_1916/thumbnail", "/api/v1/operations/gallery/{capture}/thumbnail")]
    [DataRow("/archive/products?view=retained", "/archive/products?view=retained")]
    [DataRow("http://127.0.0.1:5130/_blazor?id=opaque-circuit-token", "/_blazor?[other-query]")]
    [DataRow("https://owner:password@opaque-host-1916.example/Account/Login?ReturnUrl=%2Fsecret", "/Account/Login?[other-query]")]
    [DataRow("http://127.0.0.1:5130/OPAQUE_1916/route", "[other-route]")]
    [DataRow("about:blank", "about:blank")]
    [DataRow("data:text/html,OPAQUE_1916", "[unparsed]")]
    [DataRow("", "[empty]")]
    public void RoutesKeepOnlyKnownShapesWithPublicComponents(string address, string expected) =>
        Assert.AreEqual(expected, CalendarEvidenceVocabulary.RouteShape(address, ExpectedProduct));

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
                {"t":"a","a":[["href","/archive/day/OPAQUE_LINK_SEGMENT_1916?OPAQUE_LINK_QUERYNAME_1916=1"]],"c":["open"]},
                {"t":"img","a":[["src","/api/v1/operations/still-products/00000000-0000-0000-0000-000000001916/preview?OPAQUE_IMAGE_QUERYNAME_1916=1"],["alt","OPAQUE_ALT_1916"],["loading","lazy"]],"c":[]},
                {"t":"time","a":[["datetime","OPAQUE_DATE_1916"]],"c":["1"]},
                {"t":"opaque-element-1916","a":[["class","product-slot"]],"c":["OPAQUE_CUSTOM_1916"]},
                {"t":"script","a":[],"c":["OPAQUE_SCRIPT_1916"]}]}]}
            """);

        var markup = CalendarEvidenceVocabulary.RenderMarkup(tree.RootElement, ExpectedProduct, omitOtherProducts: true)!;

        foreach (var opaque in new[]
        {
            "OPAQUE_CLASS_1916", "OPAQUE_ATTRIBUTE_1916", "OPAQUE_STYLE_1916", "OPAQUE_LABEL_1916", "OPAQUE_OTHER_TILE_1916",
            "OPAQUE_STATE_1916", "OPAQUE_ID_1916", "OPAQUE_TITLE_1916", "OPAQUE_HANDLER_1916", "OPAQUE_TEXT_1916",
            "OPAQUE_CHIP_TITLE_1916", "OPAQUE_LINK_SEGMENT_1916", "OPAQUE_LINK_QUERYNAME_1916", "OPAQUE_IMAGE_QUERYNAME_1916", "OPAQUE_ALT_1916",
            "OPAQUE_DATE_1916", "opaque-element-1916", "OPAQUE_CUSTOM_1916", "OPAQUE_SCRIPT_1916", "00000000-0000-0000-0000-000000001916"
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
                      "src":"/archive/OPAQUE_SRC_1916","currentSrc":"http://opaque-host-1916.example/x?OPAQUE_QUERYNAME_1916=1",
                      "loading":"OPAQUE_LOADING_1916","width":10,"height":10,"visible":true},
             "blazorErrorUi":false,"errorBoundary":false,
             "reconnect":{"open":true,"classes":["components-reconnect-show","OPAQUE_RECONNECT_1916"]},
             "notices":[{"classes":["page-state","page-state--error","OPAQUE_NOTICE_CLASS_1916"],"title":"OPAQUE_NOTICE_TITLE_1916"},
                        {"classes":["page-state"],"title":"Loading observing day"}],
             "trees":{"calendarCell":null,"dayTile":{"t":"article","a":[["data-product-kind","daily summary"]],"c":["OPAQUE_TILE_TEXT_1916"]},
                      "dailyProducts":null,"pageStates":[{"t":"section","a":[["class","page-state OPAQUE_PAGE_STATE_1916"]],"c":["OPAQUE_PAGE_STATE_TEXT_1916"]}]}}
            """);

        var snapshot = PageSnapshot.From(raw.RootElement, ExpectedProduct);
        var retained = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("OPAQUE", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-host-1916", retained, StringComparison.Ordinal);
        Assert.AreEqual("other", snapshot.Interactive);
        Assert.AreEqual("other", snapshot.ReadyState);
        Assert.AreEqual("other", snapshot.TileState);
        Assert.AreEqual("[other-route]", snapshot.Image.Source);
        Assert.AreEqual("[other-route]", snapshot.Image.CurrentSource);
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
    public async Task BoundedAwaitGivesUpAtItsCap()
    {
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => CalendarDayPreviewEvidence.Bounded(never.Task, TimeSpan.FromMilliseconds(100), CancellationToken.None)).ConfigureAwait(false);

        Assert.IsLessThan(TimeSpan.FromSeconds(5), started.Elapsed);
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
    public async Task FileSinkAttachesOneCompleteFileAndLeavesNoPartialFile()
    {
        var directory = SinkDirectory();
        var attached = new List<string>();
        try
        {
            var path = await new FileEvidenceSink(directory, attached.Add, static _ => { })
                .PersistAsync("case.evidence.json", "{}", CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(new[] { path }, attached);
            CollectionAssert.AreEqual(new[] { path }, Directory.GetFiles(directory));
            Assert.AreEqual("{}", await File.ReadAllTextAsync(path).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSinkLeavesNothingWhenAttachmentFails()
    {
        var directory = SinkDirectory();
        try
        {
            var sink = new FileEvidenceSink(directory, static _ => throw new IOException("attach failed"), static _ => { });

            await Assert.ThrowsExactlyAsync<IOException>(() => sink.PersistAsync("case.evidence.json", "{}", CancellationToken.None)).ConfigureAwait(false);

            Assert.IsEmpty(Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory) : []);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task FileSinkLeavesNothingWhenCancelled()
    {
        var directory = SinkDirectory();
        var attached = new List<string>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        try
        {
            var sink = new FileEvidenceSink(directory, attached.Add, static _ => { });

            await Assert.ThrowsAsync<OperationCanceledException>(() => sink.PersistAsync("case.evidence.json", "{}", cancelled.Token)).ConfigureAwait(false);

            Assert.IsEmpty(attached);
            Assert.IsEmpty(Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory) : []);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string SinkDirectory() => Path.Combine(Path.GetTempPath(), "hvo-1916-sink-" + Guid.NewGuid().ToString("N"));

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
