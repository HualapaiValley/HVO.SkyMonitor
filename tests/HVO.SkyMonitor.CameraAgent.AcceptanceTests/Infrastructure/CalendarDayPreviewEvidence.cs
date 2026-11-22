using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

namespace HVO.SkyMonitor.CameraAgent.AcceptanceTests.Infrastructure;

/// <summary>
/// What a failed Calendar-to-day Daily Summary preview assertion is attributed to (#1916). A failure can carry more
/// than one category; <see cref="Unclassified"/> is reported only when the evidence supports none of the others.
/// </summary>
internal enum CalendarDayPreviewFailureCategory
{
    /// <summary>The main frame never reached the selected day, at the failure or after the settle window.</summary>
    LostNavigation,

    /// <summary>The preview failed in the browser, the tile showed its failure state, or the authenticated re-read failed.</summary>
    UnavailableProductImage,

    /// <summary>A script error, a Blazor circuit or connection failure, an error boundary, or a product error notice.</summary>
    PageError,

    /// <summary>The selected day was reached without errors and the preview image appeared, but not within the wait.</summary>
    TestSynchronization,

    /// <summary>The selected day was reached without errors, but was still loading when the settle window ended.</summary>
    RenderIncomplete,

    /// <summary>An assertion failed on a reached day with a displayed preview, so the product state differs from the expectation.</summary>
    ProductStateMismatch,

    Unclassified
}

internal sealed record CalendarDayPreviewRequest(long AtMilliseconds, string Outcome, int? Status, string? ContentType, string? Failure);

internal sealed record CalendarDayPreviewProbe(int? Status, string? ContentType, long Bytes, bool ImageSignature, string? Error)
{
    internal bool Available => Status is >= 200 and < 300 && ImageSignature;
}

/// <summary>The facts the classifier needs, separated from Playwright so every category can be verified directly.</summary>
internal sealed record CalendarDayPreviewObservation(
    bool OnSelectedDayAtFailure,
    bool OnSelectedDayAfterSettle,
    bool PreviewFailedMarker,
    IReadOnlyList<CalendarDayPreviewRequest> PreviewRequests,
    CalendarDayPreviewProbe? Probe,
    IReadOnlyList<string> PageErrorSignals,
    bool ImageVisibleAtFailure,
    long? ImageVisibleAfterSettleMilliseconds,
    bool DayLoadingAfterSettle,
    bool AssertionMismatch);

internal static class CalendarDayPreviewClassifier
{
    internal static IReadOnlyList<CalendarDayPreviewFailureCategory> Classify(CalendarDayPreviewObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var categories = new List<CalendarDayPreviewFailureCategory>();
        var reachedDay = observation.OnSelectedDayAtFailure || observation.OnSelectedDayAfterSettle;
        if (!reachedDay)
        {
            categories.Add(CalendarDayPreviewFailureCategory.LostNavigation);
        }
        if (PreviewUnavailable(observation))
        {
            categories.Add(CalendarDayPreviewFailureCategory.UnavailableProductImage);
        }
        if (observation.PageErrorSignals.Count > 0)
        {
            categories.Add(CalendarDayPreviewFailureCategory.PageError);
        }
        if (categories.Count == 0 && reachedDay)
        {
            var imageSeen = observation.ImageVisibleAtFailure || observation.ImageVisibleAfterSettleMilliseconds is not null;
            if (observation.AssertionMismatch && imageSeen)
            {
                categories.Add(CalendarDayPreviewFailureCategory.ProductStateMismatch);
            }
            else if (imageSeen)
            {
                categories.Add(CalendarDayPreviewFailureCategory.TestSynchronization);
            }
            else if (observation.DayLoadingAfterSettle)
            {
                categories.Add(CalendarDayPreviewFailureCategory.RenderIncomplete);
            }
        }
        if (categories.Count == 0)
        {
            categories.Add(CalendarDayPreviewFailureCategory.Unclassified);
        }
        return categories;
    }

    private static bool PreviewUnavailable(CalendarDayPreviewObservation observation)
    {
        if (observation.PreviewFailedMarker || observation.Probe is { Available: false })
        {
            return true;
        }
        var requests = observation.PreviewRequests;
        if (requests.Any(static request => request.Status >= 400 ||
                                           (request.Status is >= 200 and < 300 && !IsImageContentType(request.ContentType))))
        {
            return true;
        }
        // Replacing a prerendered image with its interactive copy aborts the first request. Only a failure without any
        // successful image response shows that the browser could not obtain the preview.
        return requests.Any(static request => request.Outcome == "failed") &&
               !requests.Any(static request => request.Status is >= 200 and < 300);
    }

    internal static bool IsImageContentType(string? contentType) =>
        contentType is not null && contentType.StartsWith("image/", StringComparison.Ordinal);
}

/// <summary>
/// Retains credential-free evidence when the Calendar day link does not lead to the Daily Summary preview (#1916):
/// selected day, URLs reduced to origin-relative paths with query keys only, scoped day-card markup with comments,
/// scripts, values and styles removed, bounded browser and circuit signal labels, preview request outcomes and an
/// authenticated re-read of the preview. Free-text console, page-error and server-log messages are never retained.
/// </summary>
internal sealed partial class CalendarDayPreviewEvidence : IDisposable
{
    internal const string EvidenceDirectoryName = "calendar-preview-failures";
    internal const string Schema = "hvo-calendar-day-preview-failure-v1";
    private const int TimelineCapacity = 256;
    private const int MessageLineCapacity = 12;
    private const int MessageLineCharacters = 300;
    private static readonly TimeSpan BrowserCallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CaptureBudget = TimeSpan.FromSeconds(90);

    private readonly IPage page;
    private readonly TestContext testContext;
    private readonly ServerLogEvidence? serverLog;
    private readonly string selectedDay;
    private readonly string selectedDayPath;
    private readonly string previewPath;
    private readonly string rowLabel;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
    private readonly ConcurrentQueue<TimelineEntry> timeline = new();
    private readonly ConcurrentQueue<CalendarDayPreviewRequest> previewRequests = new();
    private readonly ConcurrentQueue<string> eventSignals = new();
    private readonly ConcurrentDictionary<string, int> consoleLabels = new(StringComparer.Ordinal);
    private int timelineCount;
    private long? clickAtMilliseconds;
    private string interactiveAtClick = "not-recorded";
    private string urlAtClick = "not-recorded";
    private bool detached;

    private CalendarDayPreviewEvidence(
        IPage page, TestContext testContext, ServerLogEvidence? serverLog, DateOnly selectedDay, string previewPath, string rowLabel)
    {
        this.page = page;
        this.testContext = testContext;
        this.serverLog = serverLog;
        this.selectedDay = selectedDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        selectedDayPath = "/archive/day/" + this.selectedDay;
        this.previewPath = previewPath;
        this.rowLabel = PlaywrightDiagnosticContext.SafeName(rowLabel);
        page.FrameNavigated += OnFrameNavigated;
        page.Request += OnRequest;
        page.Response += OnResponse;
        page.RequestFailed += OnRequestFailed;
        page.Console += OnConsole;
        page.PageError += OnPageError;
        page.WebSocket += OnWebSocket;
    }

    /// <summary>The assertion step in progress; recorded with the failure so the timeline names what was awaited.</summary>
    internal string Step { get; set; } = "arrange";

    internal string SelectedDayPath => selectedDayPath;

    /// <summary>The most recently retained report, or null when no failure has been retained.</summary>
    internal CalendarDayPreviewReport? LastReport { get; private set; }

    internal static CalendarDayPreviewEvidence Observe(
        IPage page, TestContext testContext, DateOnly selectedDay, string previewPath, string rowLabel, ServerLogEvidence? serverLog = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(testContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewPath);
        return new CalendarDayPreviewEvidence(page, testContext, serverLog, selectedDay, previewPath, rowLabel);
    }

    /// <summary>Records the URL and whether the shell had become interactive immediately before the day link click.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Recording pre-click state is diagnostic and must never change the outcome of the click under test.")]
    internal async Task BeforeDayLinkClickAsync()
    {
        Step = "click day link";
        urlAtClick = SafeUrl(page.Url);
        try
        {
            interactiveAtClick = await page.EvaluateAsync<string>(
                    "() => document.querySelector('.app-frame')?.getAttribute('data-interactive') ?? 'absent'")
                .WaitAsync(BrowserCallTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            interactiveAtClick = "unreadable";
        }
        clickAtMilliseconds = clock.ElapsedMilliseconds;
        Record("click", $"interactive={interactiveAtClick} url={urlAtClick}");
    }

    /// <summary>
    /// Records one line for a passing run: how long the preview took after the click and how the day was reached, so
    /// passing controls show their margin against the wait. No file is retained for a pass.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A passing run's timing line is diagnostic and must never fail the run.")]
    internal void ReportPassed()
    {
        try
        {
            var entries = timeline.ToArray();
            var navigation = entries.Any(static entry => entry.Kind == "document-request") ? "document"
                : entries.Any(static entry => entry.Kind == "enhanced-fetch") ? "enhanced"
                : entries.Any(static entry => entry.Kind == "navigated") ? "in-circuit" : "none-observed";
            var previews = previewRequests.ToArray();
            testContext.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Calendar day preview passed ({rowLabel}): assertions complete {Elapsed()} ms after the click; interactiveAtClick={interactiveAtClick}; navigation={navigation}; previewResponses={previews.Count(static request => request.Outcome == "response")}; previewFailures={previews.Count(static request => request.Outcome == "failed")}"));
        }
        catch (Exception)
        {
            // Timing is diagnostic only.
        }
    }

    /// <summary>Exceptions a Calendar-to-day preview assertion can fail with; evidence is retained for exactly these.</summary>
    internal static bool IsAssertionFailure(Exception exception) =>
        exception is TimeoutException or PlaywrightException or AssertFailedException;

    /// <summary>
    /// Captures, classifies and retains the evidence for <paramref name="failure"/>. Capture is bounded and best-effort:
    /// it never throws, so the caller's original failure remains the reported outcome.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Evidence capture runs while the original assertion unwinds and must never replace it.")]
    internal async Task<CalendarDayPreviewReport?> RetainAsync(Exception failure, TimeSpan settleWindow)
    {
        ArgumentNullException.ThrowIfNull(failure);
        try
        {
            var report = await CaptureAsync(failure, settleWindow).WaitAsync(CaptureBudget).ConfigureAwait(false);
            var path = await WriteAsync(report).ConfigureAwait(false);
            testContext.WriteLine(report.Summary(Path.GetFileName(path)));
            LastReport = report with { EvidencePath = path };
            return LastReport;
        }
        catch (Exception exception)
        {
            try
            {
                testContext.WriteLine($"Calendar day preview evidence capture failed: {exception.GetType().Name}");
            }
            catch (Exception)
            {
                // The original assertion remains the reported failure.
            }
            return null;
        }
    }

    public void Dispose()
    {
        if (detached)
        {
            return;
        }
        detached = true;
        page.FrameNavigated -= OnFrameNavigated;
        page.Request -= OnRequest;
        page.Response -= OnResponse;
        page.RequestFailed -= OnRequestFailed;
        page.Console -= OnConsole;
        page.PageError -= OnPageError;
        page.WebSocket -= OnWebSocket;
    }

    private async Task<CalendarDayPreviewReport> CaptureAsync(Exception failure, TimeSpan settleWindow)
    {
        var failureAt = clock.ElapsedMilliseconds;
        var step = Step;
        var urlAtFailure = SafeUrl(page.Url);
        var onDayAtFailure = IsSelectedDay(page.Url);
        Record("failure", $"step={step} type={failure.GetType().Name}");
        var atFailure = await SnapshotAsync().ConfigureAwait(false);
        var probe = await ProbeAsync().ConfigureAwait(false);

        long? visibleAfterSettle = null;
        var settle = Stopwatch.StartNew();
        try
        {
            await page.Locator("[data-product-kind='daily summary'] img")
                .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = (float)settleWindow.TotalMilliseconds })
                .ConfigureAwait(false);
            visibleAfterSettle = settle.ElapsedMilliseconds;
            Record("settle", $"preview image visible after {visibleAfterSettle} ms");
        }
        catch (Exception exception) when (exception is TimeoutException or PlaywrightException)
        {
            Record("settle", $"preview image not visible within {(long)settleWindow.TotalMilliseconds} ms");
        }
        var urlAfterSettle = SafeUrl(page.Url);
        var onDayAfterSettle = IsSelectedDay(page.Url);
        var afterSettle = await SnapshotAsync().ConfigureAwait(false);

        var serverEntries = ServerEntriesSinceStart();
        var signals = PageErrorSignals(atFailure, afterSettle, serverEntries);
        var requests = previewRequests.ToArray();
        var observation = new CalendarDayPreviewObservation(
            onDayAtFailure,
            onDayAfterSettle,
            atFailure.PreviewFailedMarker || afterSettle.PreviewFailedMarker,
            requests,
            probe,
            signals,
            atFailure.Image.Visible,
            visibleAfterSettle,
            afterSettle.DayLoading,
            failure is AssertFailedException);
        return new CalendarDayPreviewReport(
            testContext.FullyQualifiedTestClassName ?? "unknown",
            testContext.TestName ?? "unknown",
            rowLabel,
            selectedDay,
            selectedDayPath,
            previewPath,
            step,
            failure.GetType().FullName ?? failure.GetType().Name,
            MessageLines(failure.Message),
            CalendarDayPreviewClassifier.Classify(observation),
            observation,
            interactiveAtClick,
            urlAtClick,
            urlAtFailure,
            urlAfterSettle,
            clickAtMilliseconds,
            failureAt,
            (long)settleWindow.TotalMilliseconds,
            atFailure,
            afterSettle,
            consoleLabels.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new KeyValuePair<string, int>(pair.Key, pair.Value)).ToArray(),
            serverEntries,
            timeline.OrderBy(static entry => entry.Sequence).ToArray(),
            null);
    }

    private IReadOnlyList<string> PageErrorSignals(PageSnapshot atFailure, PageSnapshot afterSettle, IReadOnlyList<ServerLogEntry> serverEntries)
    {
        var signals = new SortedSet<string>(eventSignals, StringComparer.Ordinal);
        foreach (var snapshot in new[] { atFailure, afterSettle })
        {
            foreach (var signal in snapshot.Signals)
            {
                signals.Add(signal);
            }
        }
        var clickUtc = clickAtMilliseconds is { } click ? startedUtc.AddMilliseconds(click) : startedUtc;
        foreach (var entry in serverEntries.Where(entry => entry.AtUtc >= clickUtc &&
                                                           (entry.Level >= LogLevel.Error || entry.ExceptionType is not null)))
        {
            signals.Add($"server:{entry.Level}:{entry.EventName ?? entry.EventId.ToString(CultureInfo.InvariantCulture)}:{entry.ExceptionType ?? "none"}");
        }
        return [.. signals];
    }

    private ServerLogEntry[] ServerEntriesSinceStart() =>
        serverLog?.Snapshot().Where(entry => entry.AtUtc >= startedUtc).ToArray() ?? [];

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A page that cannot be inspected is itself evidence; the snapshot records that instead of failing.")]
    private async Task<PageSnapshot> SnapshotAsync()
    {
        try
        {
            var json = await page.EvaluateAsync<JsonElement>(SnapshotScript, new { day = selectedDay })
                .WaitAsync(BrowserCallTimeout).ConfigureAwait(false);
            return PageSnapshot.From(json);
        }
        catch (Exception exception)
        {
            return PageSnapshot.FromUnreadable(exception.GetType().Name);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated re-read is diagnostic; its own failure is recorded as its outcome.")]
    private async Task<CalendarDayPreviewProbe> ProbeAsync()
    {
        IAPIResponse? response = null;
        try
        {
            response = await page.Context.APIRequest.GetAsync(
                    new Uri(new Uri(page.Url), previewPath).AbsoluteUri,
                    new() { MaxRedirects = 0, Timeout = (float)BrowserCallTimeout.TotalMilliseconds })
                .WaitAsync(BrowserCallTimeout + TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            var body = await response.BodyAsync().ConfigureAwait(false);
            response.Headers.TryGetValue("content-type", out var contentType);
            return new CalendarDayPreviewProbe(response.Status, BoundedContentType(contentType), body.LongLength, HasImageSignature(body), null);
        }
        catch (Exception exception)
        {
            return new CalendarDayPreviewProbe(null, null, 0, false, exception.GetType().Name);
        }
        finally
        {
            if (response is not null)
            {
                try
                {
                    await response.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Disposal of a diagnostic response cannot affect the evidence already read.
                }
            }
        }
    }

    private async Task<string> WriteAsync(CalendarDayPreviewReport report)
    {
        var directory = Path.Combine(testContext.ResultsDirectory ?? Path.GetTempPath(), EvidenceDirectoryName);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, string.Create(CultureInfo.InvariantCulture,
            $"{PlaywrightDiagnosticContext.SafeName(report.TestClass)}.{PlaywrightDiagnosticContext.SafeName(report.TestName)}.{report.Row}.{Guid.NewGuid():N}.evidence.json"));
        await File.WriteAllTextAsync(path, report.ToJson(), new UTF8Encoding(false)).ConfigureAwait(false);
        testContext.AddResultFile(path);
        return path;
    }

    private bool IsSelectedDay(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.AbsolutePath, selectedDayPath, StringComparison.Ordinal);

    private bool IsPreview(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.AbsolutePath, previewPath, StringComparison.Ordinal);

    private void Record(string kind, string detail)
    {
        var sequence = Interlocked.Increment(ref timelineCount);
        if (sequence > TimelineCapacity)
        {
            return;
        }
        timeline.Enqueue(new TimelineEntry(sequence, Elapsed(), kind, detail));
    }

    private void OnFrameNavigated(object? sender, IFrame frame)
    {
        if (frame == page.MainFrame)
        {
            Record("navigated", SafeUrl(frame.Url));
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Playwright event handlers must not throw into the driver connection.")]
    private void OnRequest(object? sender, IRequest request)
    {
        try
        {
            if (IsPreview(request.Url))
            {
                Record("preview-request", $"method={BoundedMethod(request.Method)}");
            }
            else if (request.IsNavigationRequest && request.Frame == page.MainFrame)
            {
                Record("document-request", SafeUrl(request.Url));
            }
            else if (request.ResourceType is "fetch" or "xhr" &&
                     Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) &&
                     uri.AbsolutePath.StartsWith("/archive/", StringComparison.Ordinal))
            {
                // Blazor enhanced navigation fetches the next page instead of navigating the document.
                Record("enhanced-fetch", SafeUrl(request.Url));
            }
        }
        catch (Exception)
        {
            Record("request", "unreadable");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Playwright event handlers must not throw into the driver connection.")]
    private void OnResponse(object? sender, IResponse response)
    {
        try
        {
            if (IsPreview(response.Url))
            {
                response.Headers.TryGetValue("content-type", out var contentType);
                var bounded = BoundedContentType(contentType);
                previewRequests.Enqueue(new(Elapsed(), "response", response.Status, bounded, null));
                Record("preview-response", $"status={response.Status} type={bounded}");
            }
            else if (response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame)
            {
                Record("document-response", $"status={response.Status} {SafeUrl(response.Url)}");
            }
        }
        catch (Exception)
        {
            Record("response", "unreadable");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Playwright event handlers must not throw into the driver connection.")]
    private void OnRequestFailed(object? sender, IRequest request)
    {
        try
        {
            var failure = BoundedNetworkError(request.Failure);
            if (IsPreview(request.Url))
            {
                previewRequests.Enqueue(new(Elapsed(), "failed", null, null, failure));
                Record("preview-failed", failure);
            }
            else if (request.IsNavigationRequest && request.Frame == page.MainFrame)
            {
                Record("document-failed", $"{failure} {SafeUrl(request.Url)}");
            }
        }
        catch (Exception)
        {
            Record("request-failed", "unreadable");
        }
    }

    private void OnConsole(object? sender, IConsoleMessage message)
    {
        var label = ClassifyConsole(message.Type, message.Text);
        if (label is null)
        {
            return;
        }
        consoleLabels.AddOrUpdate(label, 1, static (_, count) => count + 1);
        Record("console", label);
        if (IsPageErrorConsoleLabel(label))
        {
            eventSignals.Enqueue(label);
        }
    }

    private void OnPageError(object? sender, string error)
    {
        var label = "script-error:" + ErrorName(error);
        eventSignals.Enqueue(label);
        Record("page-error", label);
    }

    private void OnWebSocket(object? sender, IWebSocket socket)
    {
        if (!Uri.TryCreate(socket.Url, UriKind.Absolute, out var uri) ||
            !uri.AbsolutePath.StartsWith("/_blazor", StringComparison.Ordinal))
        {
            return;
        }
        // The socket URL carries the circuit connection token, so only the event itself is recorded.
        Record("blazor-socket", "opened");
        socket.Close += (_, _) => Record("blazor-socket", "closed");
        socket.SocketError += (_, _) => Record("blazor-socket", "error");
    }

    private long Elapsed() => clock.ElapsedMilliseconds - (clickAtMilliseconds ?? 0);

    /// <summary>An origin-relative path with query keys only; values, fragments and foreign origins are never kept.</summary>
    internal static string SafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "[empty]";
        }
        if (string.Equals(value, "about:blank", StringComparison.Ordinal))
        {
            return value;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "[unparsed]";
        }
        var keys = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static pair => pair.Split('=', 2)[0])
            .Select(static key => SafeQueryKey().IsMatch(key) ? key : "key")
            .Select(static key => key + "=…")
            .ToArray();
        var path = SafePath().IsMatch(uri.AbsolutePath) ? uri.AbsolutePath : "[unsafe-path]";
        return keys.Length == 0 ? path : path + "?" + string.Join('&', keys);
    }

    /// <summary>A bounded label for a console message, or null for message types that carry no failure signal.</summary>
    internal static string? ClassifyConsole(string type, string? text)
    {
        if (!string.Equals(type, "error", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(type, "warning", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var level = string.Equals(type, "error", StringComparison.OrdinalIgnoreCase) ? "error" : "warning";
        text ??= string.Empty;
        if (UnhandledCircuitException().IsMatch(text))
        {
            return "console:circuit-terminated";
        }
        if (CircuitClosed().IsMatch(text))
        {
            return "console:circuit-closed";
        }
        if (ConnectionLost().IsMatch(text))
        {
            return "console:connection-lost";
        }
        if (ResourceLoadFailed().IsMatch(text))
        {
            return "console:resource-load-failed";
        }
        if (BlazorLoggerPrefix().IsMatch(text))
        {
            return $"console:blazor-{level}";
        }
        return $"console:other-{level}";
    }

    internal static bool IsPageErrorConsoleLabel(string label) => label is
        "console:circuit-terminated" or "console:circuit-closed" or "console:connection-lost" or "console:blazor-error";

    /// <summary>The leading error constructor name from a page error, bounded to built-in names.</summary>
    internal static string ErrorName(string? error)
    {
        var match = LeadingErrorName().Match(error ?? string.Empty);
        return match.Success && match.Groups["name"].Value is
            "Error" or "TypeError" or "ReferenceError" or "SyntaxError" or "RangeError" or "EvalError" or "URIError" or
            "AggregateError" or "DOMException" or "AbortError" or "NotFoundError" or "InvalidStateError" or
            "NetworkError" or "SecurityError" or "TimeoutError"
            ? match.Groups["name"].Value
            : "OTHER";
    }

    internal static string BoundedNetworkError(string? failure)
    {
        var match = NetworkErrorCode().Match(failure ?? string.Empty);
        return match.Success ? match.Value : "OTHER";
    }

    internal static string BoundedContentType(string? contentType)
    {
        var media = (contentType ?? string.Empty).Split(';', 2)[0].Trim();
        return media.Length == 0
            ? "none"
            : KnownContentTypes.FirstOrDefault(known => string.Equals(known, media, StringComparison.OrdinalIgnoreCase)) ?? "other";
    }

    private static readonly string[] KnownContentTypes =
    [
        "image/png", "image/jpeg", "image/webp", "image/avif", "image/gif",
        "text/html", "text/plain", "application/json", "application/problem+json"
    ];

    internal static bool HasImageSignature(ReadOnlySpan<byte> body) =>
        body.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ||
        body.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ||
        (body.Length >= 12 && body[..4].SequenceEqual("RIFF"u8) && body.Slice(8, 4).SequenceEqual("WEBP"u8));

    internal static IReadOnlyList<string> MessageLines(string? message) =>
        (message ?? string.Empty).Split('\n')
            .Select(static line => line.TrimEnd('\r'))
            .Where(static line => line.Length > 0)
            .Take(MessageLineCapacity)
            .Select(static line => PlaywrightDiagnosticContext.Sanitize(
                line.Length > MessageLineCharacters ? line[..MessageLineCharacters] + "…" : line))
            .ToArray();

    private static string BoundedMethod(string value) => value.ToUpperInvariant() switch
    {
        "GET" or "HEAD" or "POST" => value.ToUpperInvariant(),
        _ => "OTHER"
    };

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeQueryKey();

    [GeneratedRegex("^/[A-Za-z0-9_./-]{0,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePath();

    [GeneratedRegex("unhandled exception on the current circuit", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UnhandledCircuitException();

    [GeneratedRegex("circuit (?:has )?(?:been )?(?:closed|terminated|disposed)|invocation canceled due to the underlying connection being closed",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CircuitClosed();

    [GeneratedRegex("connection disconnected|websocket closed|failed to start the connection|failed to complete negotiation|connection (?:could not|was) (?:be )?(?:established|closed|stopped)|server timeout elapsed",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionLost();

    [GeneratedRegex("^Failed to load resource", RegexOptions.CultureInvariant)]
    private static partial Regex ResourceLoadFailed();

    [GeneratedRegex(@"^\[\d{4}-\d{2}-\d{2}T[0-9:.]+Z\] (?:Error|Warning|Critical)", RegexOptions.CultureInvariant)]
    private static partial Regex BlazorLoggerPrefix();

    [GeneratedRegex("^(?:Uncaught )?(?<name>[A-Za-z]{1,40})(?::|$)", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingErrorName();

    [GeneratedRegex("^net::ERR_[A-Z_]{1,60}$", RegexOptions.CultureInvariant)]
    private static partial Regex NetworkErrorCode();

    /// <summary>
    /// Returns bounded page facts and scrubbed markup for the selected Calendar cell and the Daily Summary tile. The
    /// markup keeps product-owned element structure and text, and removes comments (Blazor component descriptors),
    /// scripts, templates, dialogs, hidden inputs, form values, inline styles, event attributes and query values.
    /// </summary>
    private const string SnapshotScript = """
        ({ day }) => {
          const maxMarkup = 16384;
          const safeUrl = value => {
            if (value === null || value === undefined || value === '') return null;
            try {
              const url = new URL(value, location.href);
              if (url.origin !== location.origin) return '[external]';
              const keys = [...new Set([...url.searchParams.keys()])].map(key => /^[A-Za-z0-9_.-]{1,40}$/.test(key) ? key : 'key');
              return url.pathname + (keys.length ? '?' + keys.map(key => key + '=…').join('&') : '');
            } catch { return '[unparsed]'; }
          };
          const scrub = (element, prune) => {
            if (!element) return null;
            const clone = element.cloneNode(true);
            const comments = [];
            const walker = document.createTreeWalker(clone, NodeFilter.SHOW_COMMENT);
            while (walker.nextNode()) comments.push(walker.currentNode);
            comments.forEach(node => node.remove());
            clone.querySelectorAll('script, template, dialog, noscript, input[type=hidden]').forEach(node => node.remove());
            if (prune) prune(clone);
            for (const node of [clone, ...clone.querySelectorAll('*')]) {
              for (const attribute of [...node.attributes]) {
                const name = attribute.name.toLowerCase();
                if (name === 'value' || name === 'style' || name === 'srcset' || name === 'nonce' || name.startsWith('on')) {
                  node.removeAttribute(attribute.name);
                } else if (name === 'src' || name === 'href' || name === 'action' || name === 'poster' || name === 'formaction') {
                  node.setAttribute(attribute.name, safeUrl(attribute.value) ?? '');
                }
              }
              if (node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement || node instanceof HTMLSelectElement) {
                node.replaceChildren();
              }
            }
            const html = clone.outerHTML;
            return html.length > maxMarkup ? html.slice(0, maxMarkup) + '…[truncated]' : html;
          };
          const visible = element => {
            if (!element || !element.isConnected) return false;
            const style = getComputedStyle(element);
            const rect = element.getBoundingClientRect();
            return style.visibility !== 'hidden' && style.display !== 'none' && rect.width > 0 && rect.height > 0;
          };
          const cell = [...document.querySelectorAll('.calendar-day')].find(candidate => candidate.querySelector(`time[datetime='${day}']`)) ?? null;
          const tile = document.querySelector("[data-product-kind='daily summary']");
          const image = tile?.querySelector('img') ?? null;
          const rect = image?.getBoundingClientRect();
          const products = document.querySelector('section.daily-products');
          const notices = [...document.querySelectorAll('.content-container .page-state')].map(notice => ({
            kind: [...notice.classList].find(name => name.startsWith('page-state--')) ?? 'page-state',
            title: (notice.querySelector('strong')?.textContent ?? '').trim().slice(0, 160)
          }));
          const errorUi = document.getElementById('blazor-error-ui');
          const reconnect = document.getElementById('components-reconnect-modal');
          const dayPage = document.querySelector('.day-page');
          return {
            interactive: document.querySelector('.app-frame')?.getAttribute('data-interactive') ?? 'absent',
            readyState: document.readyState,
            dayPage: dayPage !== null,
            dayLoading: dayPage !== null && (dayPage.querySelector('.page-state .spinner-border') !== null || products?.getAttribute('aria-busy') === 'true'),
            nightlyUnavailable: document.querySelector('.daily-products__note--warning') !== null,
            previewFailedMarker: tile !== null && tile.querySelector('.product-slot__failed') !== null,
            tileState: tile?.getAttribute('data-nightly-state') ?? null,
            image: image === null ? { present: false } : {
              present: true,
              connected: image.isConnected,
              complete: image.complete,
              naturalWidth: image.naturalWidth,
              naturalHeight: image.naturalHeight,
              src: safeUrl(image.getAttribute('src')),
              currentSrc: safeUrl(image.currentSrc),
              loading: image.getAttribute('loading'),
              width: Math.round(rect.width),
              height: Math.round(rect.height),
              visible: visible(image)
            },
            blazorErrorUi: errorUi !== null && getComputedStyle(errorUi).display !== 'none',
            errorBoundary: document.querySelector('.error-display') !== null,
            reconnect: reconnect === null ? null : {
              open: reconnect.open === true,
              states: [...reconnect.classList].filter(name => /^components-reconnect-[a-z-]{1,40}$/.test(name))
            },
            notices,
            markup: {
              calendarCell: scrub(cell),
              dayTile: scrub(tile),
              dailyProducts: scrub(products, clone => {
                clone.querySelectorAll("[data-product-kind]:not([data-product-kind='daily summary'])").forEach(other => {
                  const placeholder = document.createElement(other.tagName.toLowerCase());
                  placeholder.setAttribute('data-product-kind', other.getAttribute('data-product-kind'));
                  placeholder.setAttribute('data-evidence', 'omitted');
                  other.replaceWith(placeholder);
                });
              }),
              pageStates: [...document.querySelectorAll('.content-container .page-state')].map(notice => scrub(notice))
            }
          };
        }
        """;

    internal sealed record TimelineEntry(int Sequence, long AtMilliseconds, string Kind, string Detail);
}

internal sealed record PageImageState(
    bool Present, bool Connected, bool Complete, int NaturalWidth, int NaturalHeight, string? Source, string? CurrentSource,
    string? Loading, int Width, int Height, bool Visible);

internal sealed record PageNotice(string Kind, string Title);

/// <summary>Bounded facts read from the page at one instant.</summary>
internal sealed record PageSnapshot(
    string? Unreadable,
    string Interactive,
    string ReadyState,
    bool DayPage,
    bool DayLoading,
    bool NightlyUnavailable,
    bool PreviewFailedMarker,
    string? TileState,
    PageImageState Image,
    bool BlazorErrorUi,
    bool ErrorBoundary,
    bool ReconnectOpen,
    IReadOnlyList<string> ReconnectStates,
    IReadOnlyList<PageNotice> Notices,
    IReadOnlyDictionary<string, string?> Markup,
    IReadOnlyList<string> PageStateMarkup)
{
    private static readonly PageImageState NoImage = new(false, false, false, 0, 0, null, null, null, 0, 0, false);

    internal static PageSnapshot FromUnreadable(string reason) =>
        new(reason, "unreadable", "unreadable", false, false, false, false, null, NoImage, false, false, false, [], [],
            new Dictionary<string, string?>(StringComparer.Ordinal), []);

    internal IEnumerable<string> Signals
    {
        get
        {
            if (BlazorErrorUi) yield return "dom:blazor-error-ui";
            if (ErrorBoundary) yield return "dom:error-boundary";
            if (ReconnectOpen) yield return "dom:reconnect-dialog";
            if (NightlyUnavailable) yield return "dom:nightly-products-unavailable";
            foreach (var notice in Notices.Where(static notice => notice.Kind is "page-state--error" or "page-state--warning"))
            {
                yield return "dom:" + notice.Kind["page-state--".Length..] + "-notice";
            }
        }
    }

    internal static PageSnapshot From(JsonElement json)
    {
        var image = json.GetProperty("image");
        var imageState = image.GetProperty("present").GetBoolean()
            ? new PageImageState(
                true,
                image.GetProperty("connected").GetBoolean(),
                image.GetProperty("complete").GetBoolean(),
                image.GetProperty("naturalWidth").GetInt32(),
                image.GetProperty("naturalHeight").GetInt32(),
                OptionalString(image, "src"),
                OptionalString(image, "currentSrc"),
                OptionalString(image, "loading"),
                image.GetProperty("width").GetInt32(),
                image.GetProperty("height").GetInt32(),
                image.GetProperty("visible").GetBoolean())
            : NoImage;
        var reconnect = json.GetProperty("reconnect");
        var markup = json.GetProperty("markup");
        return new PageSnapshot(
            null,
            Bounded(json.GetProperty("interactive").GetString()),
            Bounded(json.GetProperty("readyState").GetString()),
            json.GetProperty("dayPage").GetBoolean(),
            json.GetProperty("dayLoading").GetBoolean(),
            json.GetProperty("nightlyUnavailable").GetBoolean(),
            json.GetProperty("previewFailedMarker").GetBoolean(),
            OptionalString(json, "tileState"),
            imageState,
            json.GetProperty("blazorErrorUi").GetBoolean(),
            json.GetProperty("errorBoundary").GetBoolean(),
            reconnect.ValueKind == JsonValueKind.Object && reconnect.GetProperty("open").GetBoolean(),
            reconnect.ValueKind == JsonValueKind.Object
                ? [.. reconnect.GetProperty("states").EnumerateArray().Select(static state => Bounded(state.GetString()))]
                : [],
            [.. json.GetProperty("notices").EnumerateArray().Select(static notice => new PageNotice(
                Bounded(notice.GetProperty("kind").GetString()),
                PlaywrightDiagnosticContext.Sanitize(notice.GetProperty("title").GetString() ?? string.Empty)))],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["calendarCell"] = SanitizedMarkup(markup, "calendarCell"),
                ["dayTile"] = SanitizedMarkup(markup, "dayTile"),
                ["dailyProducts"] = SanitizedMarkup(markup, "dailyProducts")
            },
            [.. markup.GetProperty("pageStates").EnumerateArray()
                .Select(static state => PlaywrightDiagnosticContext.Sanitize(state.GetString() ?? string.Empty))]);
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? PlaywrightDiagnosticContext.Sanitize(value.GetString()!)
            : null;

    private static string? SanitizedMarkup(JsonElement markup, string name) =>
        markup.GetProperty(name).ValueKind == JsonValueKind.String
            ? PlaywrightDiagnosticContext.Sanitize(markup.GetProperty(name).GetString()!)
            : null;

    private static string Bounded(string? value) =>
        value is { Length: > 0 and <= 60 } && value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            ? value
            : "other";
}

internal sealed record CalendarDayPreviewReport(
    string TestClass,
    string TestName,
    string Row,
    string SelectedDay,
    string SelectedDayPath,
    string PreviewPath,
    string FailedStep,
    string ExceptionType,
    IReadOnlyList<string> MessageLines,
    IReadOnlyList<CalendarDayPreviewFailureCategory> Categories,
    CalendarDayPreviewObservation Observation,
    string InteractiveAtClick,
    string UrlAtClick,
    string UrlAtFailure,
    string UrlAfterSettle,
    long? ClickAtMilliseconds,
    long FailureAtMilliseconds,
    long SettleWindowMilliseconds,
    PageSnapshot AtFailure,
    PageSnapshot AfterSettle,
    IReadOnlyList<KeyValuePair<string, int>> ConsoleLabels,
    IReadOnlyList<ServerLogEntry> ServerLog,
    IReadOnlyList<CalendarDayPreviewEvidence.TimelineEntry> Timeline,
    string? EvidencePath)
{
    internal string Summary(string fileName)
    {
        var previews = Observation.PreviewRequests.Count == 0
            ? "none"
            : string.Join(", ", Observation.PreviewRequests.Select(static request => request.Outcome == "failed"
                ? FormattableString.Invariant($"+{request.AtMilliseconds}ms failed {request.Failure}")
                : FormattableString.Invariant($"+{request.AtMilliseconds}ms {request.Status} {request.ContentType}")));
        var probe = Observation.Probe is { } value
            ? value.Error is null ? FormattableString.Invariant($"{value.Status} {value.ContentType} {value.Bytes}B image={value.ImageSignature}") : "error " + value.Error
            : "none";
        var image = AtFailure.Image.Present
            ? FormattableString.Invariant($"present visible={AtFailure.Image.Visible} complete={AtFailure.Image.Complete} natural={AtFailure.Image.NaturalWidth}x{AtFailure.Image.NaturalHeight}")
            : "absent";
        var settled = Observation.ImageVisibleAfterSettleMilliseconds is { } after
            ? FormattableString.Invariant($"visible after {after} ms")
            : FormattableString.Invariant($"not visible within {SettleWindowMilliseconds} ms");
        return string.Join("; ",
            $"Calendar day preview failure evidence ({Row}): categories={string.Join(',', Categories)}",
            $"step={FailedStep}",
            $"exception={ExceptionType}",
            $"interactiveAtClick={InteractiveAtClick}",
            $"url@click={UrlAtClick}",
            $"url@failure={UrlAtFailure}",
            $"url@settle={UrlAfterSettle}",
            $"previewRequests={previews}",
            $"previewProbe={probe}",
            $"image@failure={image}",
            $"image@settle={settled}",
            $"dayLoading@settle={AfterSettle.DayLoading}",
            $"signals={(Observation.PageErrorSignals.Count == 0 ? "none" : string.Join(',', Observation.PageErrorSignals))}",
            $"file={fileName}");
    }

    internal string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new() { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", CalendarDayPreviewEvidence.Schema);
            writer.WriteString("testClass", TestClass);
            writer.WriteString("testName", TestName);
            writer.WriteString("row", Row);
            writer.WriteString("selectedDay", SelectedDay);
            writer.WriteString("selectedDayPath", SelectedDayPath);
            writer.WriteString("previewPath", PreviewPath);
            writer.WriteStartObject("failure");
            writer.WriteString("step", FailedStep);
            writer.WriteString("exceptionType", ExceptionType);
            WriteStrings(writer, "messageLines", MessageLines);
            writer.WriteEndObject();
            WriteStrings(writer, "categories", Categories.Select(static category => category.ToString()));
            WriteStrings(writer, "pageErrorSignals", Observation.PageErrorSignals);
            writer.WriteStartObject("navigation");
            writer.WriteString("interactiveAtClick", InteractiveAtClick);
            writer.WriteString("urlAtClick", UrlAtClick);
            writer.WriteString("urlAtFailure", UrlAtFailure);
            writer.WriteString("urlAfterSettle", UrlAfterSettle);
            writer.WriteBoolean("onSelectedDayAtFailure", Observation.OnSelectedDayAtFailure);
            writer.WriteBoolean("onSelectedDayAfterSettle", Observation.OnSelectedDayAfterSettle);
            if (ClickAtMilliseconds is { } click) writer.WriteNumber("clickAtMs", click);
            writer.WriteNumber("failureAtMs", FailureAtMilliseconds);
            writer.WriteEndObject();
            writer.WriteStartObject("preview");
            writer.WriteStartArray("browserRequests");
            foreach (var request in Observation.PreviewRequests)
            {
                writer.WriteStartObject();
                writer.WriteNumber("atMsAfterClick", request.AtMilliseconds);
                writer.WriteString("outcome", request.Outcome);
                if (request.Status is { } status) writer.WriteNumber("status", status);
                if (request.ContentType is not null) writer.WriteString("contentType", request.ContentType);
                if (request.Failure is not null) writer.WriteString("failure", request.Failure);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (Observation.Probe is { } probe)
            {
                writer.WriteStartObject("authenticatedReRead");
                if (probe.Status is { } status) writer.WriteNumber("status", status);
                if (probe.ContentType is not null) writer.WriteString("contentType", probe.ContentType);
                writer.WriteNumber("bytes", probe.Bytes);
                writer.WriteBoolean("imageSignature", probe.ImageSignature);
                if (probe.Error is not null) writer.WriteString("error", probe.Error);
                writer.WriteEndObject();
            }
            writer.WriteBoolean("tileFailureMarker", Observation.PreviewFailedMarker);
            if (Observation.ImageVisibleAfterSettleMilliseconds is { } visible) writer.WriteNumber("imageVisibleAfterSettleMs", visible);
            writer.WriteNumber("settleWindowMs", SettleWindowMilliseconds);
            writer.WriteEndObject();
            WriteSnapshot(writer, "atFailure", AtFailure);
            WriteSnapshot(writer, "afterSettle", AfterSettle);
            writer.WriteStartObject("consoleLabels");
            foreach (var (label, count) in ConsoleLabels) writer.WriteNumber(label, count);
            writer.WriteEndObject();
            writer.WriteStartArray("serverLog");
            foreach (var entry in ServerLog)
            {
                writer.WriteStartObject();
                writer.WriteString("atUtc", entry.AtUtc.ToString("O", CultureInfo.InvariantCulture));
                writer.WriteString("level", entry.Level.ToString());
                writer.WriteString("category", entry.Category);
                writer.WriteNumber("eventId", entry.EventId);
                if (entry.EventName is not null) writer.WriteString("eventName", entry.EventName);
                if (entry.ExceptionType is not null) writer.WriteString("exceptionType", entry.ExceptionType);
                if (entry.InnerExceptionType is not null) writer.WriteString("innerExceptionType", entry.InnerExceptionType);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("timeline");
            foreach (var entry in Timeline)
            {
                writer.WriteStartObject();
                writer.WriteNumber("sequence", entry.Sequence);
                writer.WriteNumber("atMsAfterClick", entry.AtMilliseconds);
                writer.WriteString("kind", entry.Kind);
                writer.WriteString("detail", PlaywrightDiagnosticContext.Sanitize(entry.Detail));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteSnapshot(Utf8JsonWriter writer, string name, PageSnapshot snapshot)
    {
        writer.WriteStartObject(name);
        if (snapshot.Unreadable is not null) writer.WriteString("unreadable", snapshot.Unreadable);
        writer.WriteString("interactive", snapshot.Interactive);
        writer.WriteString("readyState", snapshot.ReadyState);
        writer.WriteBoolean("dayPage", snapshot.DayPage);
        writer.WriteBoolean("dayLoading", snapshot.DayLoading);
        writer.WriteBoolean("nightlyUnavailable", snapshot.NightlyUnavailable);
        writer.WriteBoolean("previewFailedMarker", snapshot.PreviewFailedMarker);
        if (snapshot.TileState is not null) writer.WriteString("tileState", snapshot.TileState);
        writer.WriteStartObject("image");
        writer.WriteBoolean("present", snapshot.Image.Present);
        if (snapshot.Image.Present)
        {
            writer.WriteBoolean("visible", snapshot.Image.Visible);
            writer.WriteBoolean("connected", snapshot.Image.Connected);
            writer.WriteBoolean("complete", snapshot.Image.Complete);
            writer.WriteNumber("naturalWidth", snapshot.Image.NaturalWidth);
            writer.WriteNumber("naturalHeight", snapshot.Image.NaturalHeight);
            writer.WriteNumber("width", snapshot.Image.Width);
            writer.WriteNumber("height", snapshot.Image.Height);
            if (snapshot.Image.Source is not null) writer.WriteString("src", snapshot.Image.Source);
            if (snapshot.Image.CurrentSource is not null) writer.WriteString("currentSrc", snapshot.Image.CurrentSource);
            if (snapshot.Image.Loading is not null) writer.WriteString("loading", snapshot.Image.Loading);
        }
        writer.WriteEndObject();
        writer.WriteBoolean("blazorErrorUi", snapshot.BlazorErrorUi);
        writer.WriteBoolean("errorBoundary", snapshot.ErrorBoundary);
        writer.WriteBoolean("reconnectDialogOpen", snapshot.ReconnectOpen);
        WriteStrings(writer, "reconnectStates", snapshot.ReconnectStates);
        writer.WriteStartArray("notices");
        foreach (var notice in snapshot.Notices)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", notice.Kind);
            writer.WriteString("title", notice.Title);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartObject("markup");
        foreach (var (key, value) in snapshot.Markup)
        {
            if (value is null) writer.WriteNull(key);
            else writer.WriteString(key, value);
        }
        WriteStrings(writer, "pageStates", snapshot.PageStateMarkup);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}

internal sealed record ServerLogEntry(
    DateTimeOffset AtUtc, LogLevel Level, string Category, int EventId, string? EventName, string? ExceptionType, string? InnerExceptionType);

/// <summary>
/// Captures Warning-and-above host log events as category, event and exception type only. Messages, structured state
/// and scopes are never read, because circuit diagnostics carry circuit identifiers and request data.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by the Calendar browser tests and registered as a host logger provider.")]
internal sealed partial class ServerLogEvidence : ILoggerProvider
{
    private const int Capacity = 512;
    private readonly ConcurrentQueue<ServerLogEntry> entries = new();
    private int count;

    public ILogger CreateLogger(string categoryName) => new EvidenceLogger(this, BoundedCategory(categoryName));

    internal IReadOnlyList<ServerLogEntry> Snapshot() => entries.ToArray();

    public void Dispose()
    {
        // Entries stay readable after the host disposes its providers.
    }

    internal static string BoundedCategory(string? category) =>
        category is not null && SafeCategory().IsMatch(category) ? category : "other";

    internal static string? BoundedEventName(string? name) =>
        name is not null && SafeEventName().IsMatch(name) ? name : null;

    private void Add(ServerLogEntry entry)
    {
        if (Interlocked.Increment(ref count) <= Capacity)
        {
            entries.Enqueue(entry);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_.+`]{1,200}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCategory();

    [GeneratedRegex("^[A-Za-z0-9_.]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeEventName();

    private sealed class EvidenceLogger(ServerLogEvidence owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Warning and < LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            owner.Add(new ServerLogEntry(
                DateTimeOffset.UtcNow,
                logLevel,
                category,
                eventId.Id,
                BoundedEventName(eventId.Name),
                exception?.GetType().FullName,
                exception?.InnerException?.GetType().FullName));
        }
    }
}
