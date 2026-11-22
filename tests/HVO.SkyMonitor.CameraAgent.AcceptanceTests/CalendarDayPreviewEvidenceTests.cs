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
    [DataRow("http://127.0.0.1:5130/archive/day/2026-10-01?calendar=sunrise-v1#day-candidates", "/archive/day/2026-10-01?calendar=…")]
    [DataRow("http://127.0.0.1:5130/_blazor?id=opaque-circuit-token&x=1", "/_blazor?id=…&x=…")]
    [DataRow("https://owner:password@example.test/Account/Login?ReturnUrl=%2Fsecret", "/Account/Login?ReturnUrl=…")]
    [DataRow("http://127.0.0.1:5130/archive/a%20b", "[unsafe-path]")]
    [DataRow("http://127.0.0.1:5130/a?bad%20key=value", "/a?key=…")]
    [DataRow("about:blank", "about:blank")]
    [DataRow("data:text/html,secret", "[unparsed]")]
    [DataRow("", "[empty]")]
    public void UrlsKeepOnlyOriginRelativePathsAndQueryKeys(string address, string expected) =>
        Assert.AreEqual(expected, CalendarDayPreviewEvidence.SafeUrl(address));

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
    public void FailureMessagesAreBoundedAndSanitized()
    {
        var message = "Timeout 30000ms exceeded.\r\n" + new string('x', 500)
                      + "\nCall log:\n  - waiting for /home/runner/secret?token=abc password=hunter2\n"
                      + string.Concat(Enumerable.Repeat("line\n", 40));
        var lines = CalendarDayPreviewEvidence.MessageLines(message);

        Assert.HasCount(12, lines);
        Assert.AreEqual("Timeout 30000ms exceeded.", lines[0]);
        Assert.AreEqual(new string('x', 300) + "…", lines[1]);
        var joined = string.Join('\n', lines);
        Assert.DoesNotContain("hunter2", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/runner", joined, StringComparison.Ordinal);
        Assert.DoesNotContain("token=abc", joined, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ServerLogKeepsCategoryEventAndExceptionTypeOnly()
    {
        using var provider = new ServerLogEvidence();
        var logger = provider.CreateLogger("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost");
        Assert.IsFalse(logger.IsEnabled(LogLevel.Information));
        logger.Log(LogLevel.Information, default, "Circuit opaque-circuit-id started for owner@cameraagent.browser", null,
            static (state, _) => state);
        logger.Log(LogLevel.Error, new EventId(111, "CircuitUnhandledException"),
            "Unhandled exception in circuit 'opaque-circuit-id' for owner@cameraagent.browser",
            new InvalidOperationException("Circuit opaque-circuit-id failed for password=hunter2", new TimeoutException("inner secret")),
            static (state, _) => state);
        provider.CreateLogger("Bad category with spaces and secret=1")
            .Log(LogLevel.Warning, new EventId(7, "bad name!"), "warning", null, static (state, _) => state);

        var entries = provider.Snapshot();
        Assert.HasCount(2, entries);
        Assert.AreEqual(LogLevel.Error, entries[0].Level);
        Assert.AreEqual("Microsoft.AspNetCore.Components.Server.Circuits.CircuitHost", entries[0].Category);
        Assert.AreEqual(111, entries[0].EventId);
        Assert.AreEqual("CircuitUnhandledException", entries[0].EventName);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, entries[0].ExceptionType);
        Assert.AreEqual(typeof(TimeoutException).FullName, entries[0].InnerExceptionType);
        Assert.AreEqual("other", entries[1].Category);
        Assert.IsNull(entries[1].EventName);
        var retained = JsonSerializer.Serialize(entries);
        foreach (var secret in new[] { "opaque-circuit-id", "owner@cameraagent.browser", "hunter2", "inner secret", "secret=1" })
        {
            Assert.DoesNotContain(secret, retained, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void ReportJsonCarriesTheCategoriesAndEvidenceSections()
    {
        var observation = Observation(failedMarker: true, requests: [new(40, "response", 503, "text/plain", null)]);
        var categories = CalendarDayPreviewClassifier.Classify(observation);
        var empty = new Dictionary<string, string?>(StringComparer.Ordinal) { ["calendarCell"] = null, ["dayTile"] = "<article></article>", ["dailyProducts"] = null };
        var snapshot = new PageSnapshot(null, "true", "complete", true, false, false, true, "partial",
            new PageImageState(false, false, false, 0, 0, null, null, null, 0, 0, false), false, false, false, [], [], empty, []);
        var report = new CalendarDayPreviewReport(
            "Class", "Method", "1440x900-partial", "2026-10-01", "/archive/day/2026-10-01", "/api/preview", "preview image",
            typeof(TimeoutException).FullName!, ["Timeout 30000ms exceeded."], categories, observation, "true",
            "/archive/calendar?month=…", "/archive/day/2026-10-01", "/archive/day/2026-10-01", 0, 30_000, 10_000,
            snapshot, snapshot, [new("console:resource-load-failed", 1)],
            [new(DateTimeOffset.UnixEpoch, LogLevel.Error, "Category", 1, "Name", "System.Exception", null)],
            [new(1, 0, "click", "interactive=true url=/archive/calendar?month=…")], null);

        using var document = JsonDocument.Parse(report.ToJson());
        var root = document.RootElement;
        Assert.AreEqual(CalendarDayPreviewEvidence.Schema, root.GetProperty("schema").GetString());
        Assert.AreEqual("UnavailableProductImage", root.GetProperty("categories")[0].GetString());
        Assert.AreEqual(503, root.GetProperty("preview").GetProperty("browserRequests")[0].GetProperty("status").GetInt32());
        Assert.IsTrue(root.GetProperty("preview").GetProperty("tileFailureMarker").GetBoolean());
        Assert.AreEqual("<article></article>", root.GetProperty("atFailure").GetProperty("markup").GetProperty("dayTile").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("atFailure").GetProperty("markup").GetProperty("calendarCell").ValueKind);
        Assert.AreEqual(1, root.GetProperty("consoleLabels").GetProperty("console:resource-load-failed").GetInt32());
        Assert.AreEqual("Name", root.GetProperty("serverLog")[0].GetProperty("eventName").GetString());
        StringAssert.Contains(report.Summary("file.json"), "categories=UnavailableProductImage", StringComparison.Ordinal);
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
