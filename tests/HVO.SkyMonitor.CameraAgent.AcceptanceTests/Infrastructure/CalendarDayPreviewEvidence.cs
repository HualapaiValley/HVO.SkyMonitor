using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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

    /// <summary>A script error, a Blazor circuit or connection failure, an error boundary, or a product error notice on the page.</summary>
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
/// The strict positive allowlist every retained value passes through. The browser only collects raw structure; this
/// decides what survives. Anything not named here is dropped: unknown elements with their subtrees, attributes, class
/// tokens and text, and URLs other than the product's own route shapes.
/// </summary>
internal static partial class CalendarEvidenceVocabulary
{
    internal const string ExpectedProduct = "{expected-product}";
    internal const string OtherProduct = "{other-product}";
    private const int MarkupCharacters = 16_384;

    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "article", "section", "header", "div", "span", "p", "small", "strong", "i", "h2", "h3", "a", "button", "img",
        "time", "ol", "ul", "li", "dl", "dt", "dd"
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "img" };

    private static readonly HashSet<string> ClassTokens = new(StringComparer.Ordinal)
    {
        "calendar-day", "calendar-day--outside", "calendar-day--empty", "calendar-day--today", "calendar-day__link",
        "calendar-day__date", "calendar-day__details", "calendar-products", "calendar-thumb", "calendar-thumb--missing",
        "day-quality", "day-quality--event", "available", "partial", "unavailable", "not-produced", "event",
        "daily-products", "daily-products__note", "daily-products__note--warning", "product-slots", "product-slot",
        "product-slot--pending", "product-slot--sample", "product-slot__media", "product-slot__failed", "product-slot__open",
        "product-slot__chips", "product-slot__hourly", "product-slot__play", "hvo-chip", "hvo-chip--success",
        "hvo-chip--warning", "hvo-chip--info", "hvo-chip--neutral", "page-state", "page-state--error", "page-state--warning",
        "page-state--info", "page-state__actions", "spinner-border", "spinner-border-sm", "eyebrow", "bi", "bi-image",
        "bi-arrows-angle-expand", "bi-stars", "bi-journal-text", "bi-bar-chart-steps", "bi-film", "bi-play-circle"
    };

    private static readonly HashSet<string> ProductKinds = new(StringComparer.Ordinal)
    {
        "daily summary", "star trail", "keogram", "time-lapse"
    };

    private static readonly HashSet<string> NightlyStates = new(StringComparer.Ordinal)
    {
        "Produced", "Partial", "NotProduced", "OtherPeriod", "Pending", "NotGenerated", "Unavailable",
        "available", "partial", "unavailable", "unavailable not-produced"
    };

    private static readonly HashSet<string> TileIds = new(StringComparer.Ordinal)
    {
        "product-tile-daily-summary", "product-tile-star-trail", "product-tile-keogram", "product-tile-time-lapse"
    };

    private static readonly HashSet<string> NoticeTitles = new(StringComparer.Ordinal)
    {
        "Loading observing calendar", "Observing calendar unavailable", "Legacy noon view uses UTC", "Nightly products hidden",
        "Nightly product status unavailable", "Loading observing day", "Observing day unavailable", "Observing day not recognised"
    };

    private static readonly HashSet<string> Labels = new(NoticeTitles, StringComparer.Ordinal)
    {
        "T", "S", "K", "D", "!", "Produced", "Partial", "Not produced", "Other period only", "Pending", "Not generated",
        "Unavailable", "Preview unavailable", "Star trail", "Keogram", "Daily summary", "Nightly products",
        "Generated products", "Loading nightly products…", "Nightly product status unavailable.", "Night time-lapse",
        "Play sample", "Sample"
    };

    private static readonly HashSet<string> ReconnectStates = new(StringComparer.Ordinal)
    {
        "components-reconnect-show", "components-reconnect-hide", "components-reconnect-failed", "components-reconnect-rejected",
        "components-reconnect-retrying", "components-reconnect-paused", "components-reconnect-resume-failed"
    };

    private static readonly HashSet<string> CalendarVersions = new(StringComparer.Ordinal)
    {
        SunriseReportingPeriod.CurrentVersion, ObservingDayCalendar.LegacyNoonVersion
    };

    private static readonly Uri RelativeBase = new("http://evidence.local/");

    /// <summary>
    /// The product route a URL names, with only public components kept: dates that parse, product identities reduced
    /// to whether they are the expected product, and the product's own query keys with valid values. Any other path,
    /// query key, value, scheme or origin is replaced by a fixed marker.
    /// </summary>
    internal static string RouteShape(string? value, Guid? expectedProduct)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "[empty]";
        }
        if (string.Equals(value, "about:blank", StringComparison.Ordinal))
        {
            return value;
        }
        if (!Uri.TryCreate(RelativeBase, value, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "[unparsed]";
        }
        var path = PathShape(uri.AbsolutePath, expectedProduct);
        if (path == "[other-route]")
        {
            return path;
        }
        var parts = new List<string>();
        var otherKeys = false;
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(split[0]);
            var raw = split.Length == 2 ? Uri.UnescapeDataString(split[1].Replace('+', ' ')) : string.Empty;
            var part = key switch
            {
                "month" => "month=" + (MonthValue().IsMatch(raw) ? raw : "[other]"),
                "calendar" => "calendar=" + (CalendarVersions.Contains(raw) ? raw : "[other]"),
                "view" => "view=" + (raw == "retained" ? raw : "[other]"),
                "download" => "download=" + (raw == "1" ? raw : "[other]"),
                _ => null
            };
            if (part is null)
            {
                otherKeys = true;
            }
            else if (!parts.Contains(part, StringComparer.Ordinal))
            {
                parts.Add(part);
            }
        }
        if (otherKeys)
        {
            parts.Add("[other-query]");
        }
        return parts.Count == 0 ? path : path + "?" + string.Join('&', parts);
    }

    private static string PathShape(string path, Guid? expectedProduct)
    {
        if (path is "/" or "/archive/calendar" or "/archive/products" or "/Account/Login" or "/_blazor")
        {
            return path;
        }
        if (DayRoute().Match(path) is { Success: true } day)
        {
            return DateOnly.TryParseExact(day.Groups["date"].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? path
                : "/archive/day/{invalid-date}";
        }
        if (PreviewRoute().Match(path) is { Success: true } preview)
        {
            return "/api/v1/operations/still-products/" + Product(preview.Groups["id"].Value, expectedProduct) + "/preview";
        }
        if (DetailRoute().Match(path) is { Success: true } detail)
        {
            return "/archive/products/nightly/" + Product(detail.Groups["id"].Value, expectedProduct);
        }
        if (ThumbnailRoute().IsMatch(path))
        {
            return "/api/v1/operations/gallery/{capture}/thumbnail";
        }
        return CaptureRoute().IsMatch(path) ? "/gallery/{capture}" : "[other-route]";
    }

    private static string Product(string value, Guid? expectedProduct) =>
        !Guid.TryParseExact(value, "D", out var id) ? "{invalid-product}"
        : id == expectedProduct ? ExpectedProduct
        : OtherProduct;

    /// <summary>The expected product of a preview route, so retained routes can say whether they name it.</summary>
    internal static Guid? ProductOf(string previewPath)
    {
        var match = PreviewRoute().Match(previewPath);
        return match.Success && Guid.TryParseExact(match.Groups["id"].Value, "D", out var id) ? id : null;
    }

    /// <summary>Renders a collected element tree through the allowlist, or null when there is nothing to render.</summary>
    internal static string? RenderMarkup(JsonElement node, Guid? expectedProduct, bool omitOtherProducts = false)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var builder = new StringBuilder();
        Render(node, builder, expectedProduct, omitOtherProducts);
        return builder.Length == 0 ? null
            : builder.Length > MarkupCharacters ? builder.ToString(0, MarkupCharacters) + "…[truncated]"
            : builder.ToString();
    }

    private static void Render(JsonElement node, StringBuilder builder, Guid? expectedProduct, bool omitOtherProducts)
    {
        if (builder.Length > MarkupCharacters)
        {
            return;
        }
        if (node.ValueKind == JsonValueKind.String)
        {
            var text = node.GetString()!.Trim();
            if (Labels.Contains(text) || DayNumber().IsMatch(text))
            {
                builder.Append(WebUtility.HtmlEncode(text));
            }
            return;
        }
        if (node.ValueKind != JsonValueKind.Object ||
            !node.TryGetProperty("t", out var tagElement) || tagElement.GetString() is not { } tag || !Tags.Contains(tag))
        {
            return;
        }
        var attributes = new List<(string Name, string Value)>();
        if (node.TryGetProperty("a", out var raw) && raw.ValueKind == JsonValueKind.Array)
        {
            foreach (var pair in raw.EnumerateArray())
            {
                if (pair.ValueKind == JsonValueKind.Array && pair.GetArrayLength() == 2 &&
                    Attribute(pair[0].GetString(), pair[1].GetString(), expectedProduct) is { } attribute)
                {
                    attributes.Add(attribute);
                }
            }
        }
        var kind = attributes.FirstOrDefault(static attribute => attribute.Name == "data-product-kind").Value;
        if (omitOtherProducts && tag == "article" && kind is not null && kind != "daily summary")
        {
            builder.Append("<article data-product-kind=\"").Append(WebUtility.HtmlEncode(kind)).Append("\" data-evidence=\"omitted\"></article>");
            return;
        }
        builder.Append('<').Append(tag);
        foreach (var (name, value) in attributes)
        {
            builder.Append(' ').Append(name).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
        }
        builder.Append('>');
        if (VoidTags.Contains(tag))
        {
            return;
        }
        if (node.TryGetProperty("c", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in children.EnumerateArray())
            {
                Render(child, builder, expectedProduct, omitOtherProducts);
            }
        }
        builder.Append("</").Append(tag).Append('>');
    }

    private static (string Name, string Value)? Attribute(string? name, string? value, Guid? expectedProduct)
    {
        if (name is null || value is null)
        {
            return null;
        }
        string? kept = name switch
        {
            "class" => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(ClassTokens.Contains).Distinct(StringComparer.Ordinal)),
            "id" => TileIds.Contains(value) ? value : null,
            "data-product-kind" => ProductKinds.Contains(value) ? value : null,
            "data-nightly-state" or "data-video-state" => NightlyStates.Contains(value) ? value : null,
            "aria-busy" or "aria-hidden" => value is "true" or "false" ? value : null,
            "aria-haspopup" => value == "dialog" ? value : null,
            "aria-current" => value == "date" ? value : null,
            "role" => value is "status" or "alert" or "note" ? value : null,
            "type" => value == "button" ? value : null,
            "datetime" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? value : null,
            "loading" => value is "lazy" or "eager" ? value : null,
            "decoding" => value is "async" or "sync" or "auto" ? value : null,
            "src" or "href" => RouteShape(value, expectedProduct),
            _ => null
        };
        return string.IsNullOrEmpty(kept) ? null : (name, kept);
    }

    internal static string Allowed(string? value, params string[] allowed) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : "other";

    internal static string NoticeTitle(string? title) => title?.Trim() is { } trimmed && NoticeTitles.Contains(trimmed) ? trimmed : "other";

    internal static string NightlyState(string? value) => value is not null && NightlyStates.Contains(value) ? value : "other";

    internal static IReadOnlyList<string> ReconnectClasses(IEnumerable<string?> classes) =>
        [.. classes.Where(static name => name is not null && ReconnectStates.Contains(name)).Select(static name => name!).Distinct(StringComparer.Ordinal)];

    internal static string NoticeKind(IEnumerable<string?> classes)
    {
        var tokens = classes.ToHashSet(StringComparer.Ordinal);
        return tokens.Contains("page-state--error") ? "page-state--error"
            : tokens.Contains("page-state--warning") ? "page-state--warning"
            : tokens.Contains("page-state--info") ? "page-state--info"
            : "page-state";
    }

    /// <summary>A code-defined exception type name, or "other".</summary>
    internal static string TypeName(Type? type) =>
        type?.FullName is { } name && TypeNamePattern().IsMatch(name) ? name : "other";

    /// <summary>The coarse host component a log category belongs to.</summary>
    internal static string CoarseCategory(string? category) =>
        category is null ? "other"
        : CoarseCategories.FirstOrDefault(prefix => category == prefix || category.StartsWith(prefix + ".", StringComparison.Ordinal)) ?? "other";

    private static readonly string[] CoarseCategories =
    [
        "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.SignalR", "Microsoft.AspNetCore.Http.Connections",
        "Microsoft.AspNetCore", "Microsoft.Extensions", "HVO.SkyMonitor.CameraAgent", "HVO.SkyMonitor"
    ];

    [GeneratedRegex(@"^\d{4}-(0[1-9]|1[0-2])$", RegexOptions.CultureInvariant)]
    private static partial Regex MonthValue();

    [GeneratedRegex("^([1-9]|[12][0-9]|3[01])$", RegexOptions.CultureInvariant)]
    private static partial Regex DayNumber();

    [GeneratedRegex("^/archive/day/(?<date>[^/]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DayRoute();

    [GeneratedRegex("^/api/v1/operations/still-products/(?<id>[^/]*)/preview$", RegexOptions.CultureInvariant)]
    private static partial Regex PreviewRoute();

    [GeneratedRegex("^/archive/products/nightly/(?<id>[^/]*)$", RegexOptions.CultureInvariant)]
    private static partial Regex DetailRoute();

    [GeneratedRegex("^/api/v1/operations/gallery/[^/]*/thumbnail$", RegexOptions.CultureInvariant)]
    private static partial Regex ThumbnailRoute();

    [GeneratedRegex("^/gallery/[^/]*$", RegexOptions.CultureInvariant)]
    private static partial Regex CaptureRoute();

    [GeneratedRegex(@"^(System|Microsoft|HVO\.SkyMonitor)(\.[A-Za-z_][A-Za-z0-9_]{0,80}){1,12}$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNamePattern();
}

/// <summary>
/// Retains credential-free evidence when the Calendar day link does not lead to the Daily Summary preview (#1916):
/// the selected day, known route shapes, allowlisted markup of the Calendar cell and Daily Summary tile, bounded
/// browser and circuit signal labels, preview request outcomes and an authenticated re-read of the preview. Coarse
/// host log metadata is kept as context only. One deadline bounds capture, writing and attachment.
/// </summary>
internal sealed partial class CalendarDayPreviewEvidence : IDisposable
{
    internal const string EvidenceDirectoryName = "calendar-preview-failures";
    internal const string Schema = "hvo-calendar-day-preview-failure-v2";
    private const int TimelineCapacity = 256;
    private static readonly TimeSpan BrowserCallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(60);

    private readonly IPage page;
    private readonly TestContext testContext;
    private readonly ICalendarEvidenceSink sink;
    private readonly TimeSpan budget;
    private readonly ServerLogEvidence? serverLog;
    private readonly string selectedDay;
    private readonly string selectedDayPath;
    private readonly string previewPath;
    private readonly Guid? expectedProduct;
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
    private string routeAtClick = "not-recorded";
    private bool detached;

    private CalendarDayPreviewEvidence(
        IPage page, TestContext testContext, ServerLogEvidence? serverLog, DateOnly selectedDay, string previewPath, string rowLabel,
        ICalendarEvidenceSink sink, TimeSpan budget)
    {
        this.page = page;
        this.testContext = testContext;
        this.sink = sink;
        this.budget = budget;
        this.serverLog = serverLog;
        this.selectedDay = selectedDay.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        selectedDayPath = "/archive/day/" + this.selectedDay;
        this.previewPath = previewPath;
        expectedProduct = CalendarEvidenceVocabulary.ProductOf(previewPath);
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
        IPage page, TestContext testContext, DateOnly selectedDay, string previewPath, string rowLabel, ServerLogEvidence? serverLog = null,
        ICalendarEvidenceSink? sink = null, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(testContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(previewPath);
        return new CalendarDayPreviewEvidence(page, testContext, serverLog, selectedDay, previewPath, rowLabel,
            sink ?? FileEvidenceSink.ForTestContext(testContext), budget ?? DefaultBudget);
    }

    /// <summary>Records the route and whether the shell had become interactive immediately before the day link click.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Recording pre-click state is diagnostic and must never change the outcome of the click under test.")]
    internal async Task BeforeDayLinkClickAsync()
    {
        Step = "click day link";
        routeAtClick = Route(page.Url);
        try
        {
            var raw = await Bounded(page.EvaluateAsync<string>(
                    "() => document.querySelector('.app-frame')?.getAttribute('data-interactive') ?? 'absent'"),
                BrowserCallTimeout, CancellationToken.None).ConfigureAwait(false);
            interactiveAtClick = CalendarEvidenceVocabulary.Allowed(raw, "true", "false", "absent");
        }
        catch (Exception)
        {
            interactiveAtClick = "unreadable";
        }
        clickAtMilliseconds = clock.ElapsedMilliseconds;
        Record("click", $"interactive={interactiveAtClick} route={routeAtClick}");
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
            sink.WriteLine(string.Create(CultureInfo.InvariantCulture,
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
    /// Captures, classifies, writes and attaches the evidence for <paramref name="failure"/> within one deadline. It never
    /// throws, so the caller rethrows its original failure object unchanged. Work abandoned at the deadline has its
    /// faults observed, and the sink attaches only a complete file.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Evidence capture runs while the original assertion unwinds and must never replace it.")]
    internal async Task<CalendarDayPreviewReport?> RetainAsync(Exception failure, TimeSpan settleWindow)
    {
        ArgumentNullException.ThrowIfNull(failure);
        using var deadline = new CancellationTokenSource(budget);
        try
        {
            var report = await Bounded(CaptureAsync(failure, settleWindow, deadline.Token), budget, deadline.Token).ConfigureAwait(false);
            var path = await Bounded(sink.PersistAsync(FileName(report), report.ToJson(), deadline.Token), budget, deadline.Token)
                .ConfigureAwait(false);
            LastReport = report with { EvidencePath = path };
            TryWriteLine(report.Summary(Path.GetFileName(path)));
            return LastReport;
        }
        catch (Exception exception)
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            TryWriteLine("Calendar day preview evidence was not retained: " +
                         (exception is OperationCanceledException ? "evidence budget exhausted" : CalendarEvidenceVocabulary.TypeName(exception.GetType())));
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

    /// <summary>Awaits <paramref name="task"/> until <paramref name="cap"/> or <paramref name="token"/>; an abandoned task's fault is observed.</summary>
    internal static async Task<T> Bounded<T>(Task<T> task, TimeSpan cap, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObserveFaults(task);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
        call.CancelAfter(cap);
        return await task.WaitAsync(call.Token).ConfigureAwait(false);
    }

    /// <summary>Awaits <paramref name="task"/> until <paramref name="cap"/> or <paramref name="token"/>; an abandoned task's fault is observed.</summary>
    internal static async Task Bounded(Task task, TimeSpan cap, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(task);
        ObserveFaults(task);
        using var call = CancellationTokenSource.CreateLinkedTokenSource(token);
        call.CancelAfter(cap);
        await task.WaitAsync(call.Token).ConfigureAwait(false);
    }

    private static void ObserveFaults(Task task) => _ = task.ContinueWith(
        static completed => _ = completed.Exception,
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A summary line is diagnostic; a failing sink must not replace the original assertion.")]
    private void TryWriteLine(string line)
    {
        try
        {
            sink.WriteLine(line);
        }
        catch (Exception)
        {
            // The original assertion remains the reported failure.
        }
    }

    private async Task<CalendarDayPreviewReport> CaptureAsync(Exception failure, TimeSpan settleWindow, CancellationToken token)
    {
        var failureAt = clock.ElapsedMilliseconds;
        var step = Step;
        var routeAtFailure = Route(page.Url);
        var onDayAtFailure = IsSelectedDay(page.Url);
        Record("failure", $"step={step} type={CalendarEvidenceVocabulary.TypeName(failure.GetType())}");
        var atFailure = await SnapshotAsync(token).ConfigureAwait(false);
        var probe = await ProbeAsync(token).ConfigureAwait(false);

        long? visibleAfterSettle = null;
        var settle = Stopwatch.StartNew();
        try
        {
            await Bounded(page.Locator("[data-product-kind='daily summary'] img")
                    .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = (float)settleWindow.TotalMilliseconds }),
                settleWindow + TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            visibleAfterSettle = settle.ElapsedMilliseconds;
            Record("settle", "preview image visible after " + visibleAfterSettle.Value.ToString(CultureInfo.InvariantCulture) + " ms");
        }
        catch (Exception exception) when (exception is TimeoutException or PlaywrightException ||
                                          (exception is OperationCanceledException && !token.IsCancellationRequested))
        {
            Record("settle", "preview image not visible within " + ((long)settleWindow.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + " ms");
        }
        var routeAfterSettle = Route(page.Url);
        var onDayAfterSettle = IsSelectedDay(page.Url);
        var afterSettle = await SnapshotAsync(token).ConfigureAwait(false);

        var observation = new CalendarDayPreviewObservation(
            onDayAtFailure,
            onDayAfterSettle,
            atFailure.PreviewFailedMarker || afterSettle.PreviewFailedMarker,
            previewRequests.ToArray(),
            probe,
            ComposePageErrorSignals(eventSignals, atFailure, afterSettle),
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
            CalendarEvidenceVocabulary.RouteShape(previewPath, expectedProduct),
            step,
            CalendarEvidenceVocabulary.TypeName(failure.GetType()),
            TimeoutMilliseconds(failure.Message),
            CalendarDayPreviewClassifier.Classify(observation),
            observation,
            interactiveAtClick,
            routeAtClick,
            routeAtFailure,
            routeAfterSettle,
            clickAtMilliseconds,
            failureAt,
            (long)settleWindow.TotalMilliseconds,
            atFailure,
            afterSettle,
            consoleLabels.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new KeyValuePair<string, int>(pair.Key, pair.Value)).ToArray(),
            ServerContext(),
            timeline.OrderBy(static entry => entry.Sequence).ToArray(),
            null);
    }

    /// <summary>
    /// The page and circuit signals that can attribute a failure. Host log events are deliberately absent: a host
    /// error is not shown to be the page's error, so it is retained as context and never decides the category.
    /// </summary>
    internal static IReadOnlyList<string> ComposePageErrorSignals(IEnumerable<string> pageEventSignals, params PageSnapshot[] snapshots)
    {
        var signals = new SortedSet<string>(pageEventSignals, StringComparer.Ordinal);
        foreach (var snapshot in snapshots)
        {
            signals.UnionWith(snapshot.Signals);
        }
        return [.. signals];
    }

    private ServerLogContext[] ServerContext()
    {
        var clickUtc = clickAtMilliseconds is { } click ? startedUtc.AddMilliseconds(click) : startedUtc;
        return serverLog?.Snapshot()
            .Where(entry => entry.AtUtc >= startedUtc)
            .Select(entry => new ServerLogContext(
                (long)(entry.AtUtc - clickUtc).TotalMilliseconds, entry.Level, entry.Category, entry.EventId, entry.ExceptionType, entry.InnerExceptionType))
            .ToArray() ?? [];
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "A page that cannot be inspected is itself evidence; the snapshot records that instead of failing.")]
    private async Task<PageSnapshot> SnapshotAsync(CancellationToken token)
    {
        try
        {
            var json = await Bounded(page.EvaluateAsync<JsonElement>(SnapshotScript, new { day = selectedDay }), BrowserCallTimeout, token)
                .ConfigureAwait(false);
            return PageSnapshot.From(json, expectedProduct);
        }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            return PageSnapshot.FromUnreadable(exception is OperationCanceledException ? "timeout" : "unreadable");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated re-read is diagnostic; its own failure is recorded as its outcome.")]
    private async Task<CalendarDayPreviewProbe> ProbeAsync(CancellationToken token)
    {
        IAPIResponse? response = null;
        try
        {
            response = await Bounded(page.Context.APIRequest.GetAsync(
                    new Uri(new Uri(page.Url), previewPath).AbsoluteUri,
                    new() { MaxRedirects = 0, Timeout = (float)BrowserCallTimeout.TotalMilliseconds }),
                BrowserCallTimeout + TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            var body = await Bounded(response.BodyAsync(), BrowserCallTimeout, token).ConfigureAwait(false);
            response.Headers.TryGetValue("content-type", out var contentType);
            return new CalendarDayPreviewProbe(response.Status, BoundedContentType(contentType), body.LongLength, HasImageSignature(body), null);
        }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            return new CalendarDayPreviewProbe(null, null, 0, false,
                exception is OperationCanceledException ? "timeout" : CalendarEvidenceVocabulary.TypeName(exception.GetType()));
        }
        finally
        {
            if (response is not null)
            {
                ObserveFaults(DisposeResponseAsync(response));
            }
        }
    }

    private static async Task DisposeResponseAsync(IAPIResponse response) => await response.DisposeAsync().ConfigureAwait(false);

    private static string FileName(CalendarDayPreviewReport report) => string.Create(CultureInfo.InvariantCulture,
        $"{PlaywrightDiagnosticContext.SafeName(report.TestClass)}.{PlaywrightDiagnosticContext.SafeName(report.TestName)}.{report.Row}.{Guid.NewGuid():N}.evidence.json");

    private string Route(string? url) => CalendarEvidenceVocabulary.RouteShape(url, expectedProduct);

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
            Record("navigated", Route(frame.Url));
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
                Record("preview-request", "method=" + BoundedMethod(request.Method));
            }
            else if (request.IsNavigationRequest && request.Frame == page.MainFrame)
            {
                Record("document-request", Route(request.Url));
            }
            else if (request.ResourceType is "fetch" or "xhr" &&
                     Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) &&
                     uri.AbsolutePath.StartsWith("/archive/", StringComparison.Ordinal))
            {
                // Blazor enhanced navigation fetches the next page instead of navigating the document.
                Record("enhanced-fetch", Route(request.Url));
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
                Record("preview-response", string.Create(CultureInfo.InvariantCulture, $"status={response.Status} type={bounded}"));
            }
            else if (response.Request.IsNavigationRequest && response.Request.Frame == page.MainFrame)
            {
                Record("document-response", string.Create(CultureInfo.InvariantCulture, $"status={response.Status} {Route(response.Url)}"));
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
                Record("document-failed", failure + " " + Route(request.Url));
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

    internal static string BoundedNetworkError(string? failure) =>
        failure is not null && NetworkErrors.Contains(failure) ? failure : "OTHER";

    private static readonly HashSet<string> NetworkErrors = new(StringComparer.Ordinal)
    {
        "net::ERR_ABORTED", "net::ERR_FAILED", "net::ERR_CONNECTION_REFUSED", "net::ERR_CONNECTION_RESET",
        "net::ERR_CONNECTION_CLOSED", "net::ERR_CONNECTION_FAILED", "net::ERR_EMPTY_RESPONSE", "net::ERR_TIMED_OUT",
        "net::ERR_NAME_NOT_RESOLVED", "net::ERR_INTERNET_DISCONNECTED", "net::ERR_ADDRESS_UNREACHABLE",
        "net::ERR_INVALID_RESPONSE", "net::ERR_HTTP2_PROTOCOL_ERROR", "net::ERR_INCOMPLETE_CHUNKED_ENCODING",
        "net::ERR_BLOCKED_BY_CLIENT", "net::ERR_CACHE_MISS"
    };

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

    /// <summary>The wait a Playwright timeout reports, the only part of a failure message that is retained.</summary>
    internal static int? TimeoutMilliseconds(string? message)
    {
        var match = PlaywrightTimeout().Match(message ?? string.Empty);
        return match.Success && int.TryParse(match.Groups["ms"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string BoundedMethod(string value) => value.ToUpperInvariant() switch
    {
        "GET" or "HEAD" or "POST" => value.ToUpperInvariant(),
        _ => "OTHER"
    };

    [GeneratedRegex(@"\ATimeout (?<ms>\d{1,7})ms exceeded", RegexOptions.CultureInvariant)]
    private static partial Regex PlaywrightTimeout();

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

    /// <summary>
    /// Collects raw facts and element trees for the selected Calendar cell, the Daily Summary tile, the products
    /// section and page notices. It decides nothing about retention: every value is filtered by
    /// <see cref="CalendarEvidenceVocabulary"/> before it can be serialized.
    /// </summary>
    private const string SnapshotScript = """
        ({ day }) => {
          const collect = element => {
            if (!element) return null;
            let count = 0;
            const walk = (node, depth) => {
              if (count++ > 600 || depth > 16) return null;
              if (node.nodeType === Node.TEXT_NODE) {
                const text = node.textContent.trim();
                return text ? text.slice(0, 120) : null;
              }
              if (node.nodeType !== Node.ELEMENT_NODE) return null;
              const children = [];
              for (const child of node.childNodes) {
                const value = walk(child, depth + 1);
                if (value !== null) children.push(value);
              }
              return { t: node.localName, a: [...node.attributes].slice(0, 32).map(attribute => [attribute.name, attribute.value.slice(0, 300)]), c: children };
            };
            return walk(element, 0);
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
          const notices = [...document.querySelectorAll('.content-container .page-state')];
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
              src: image.getAttribute('src'),
              currentSrc: image.currentSrc,
              loading: image.getAttribute('loading'),
              width: Math.round(rect.width),
              height: Math.round(rect.height),
              visible: visible(image)
            },
            blazorErrorUi: errorUi !== null && getComputedStyle(errorUi).display !== 'none',
            errorBoundary: document.querySelector('.error-display') !== null,
            reconnect: reconnect === null ? null : { open: reconnect.open === true, classes: [...reconnect.classList] },
            notices: notices.map(notice => ({ classes: [...notice.classList], title: notice.querySelector('strong')?.textContent ?? '' })),
            trees: {
              calendarCell: collect(cell),
              dayTile: collect(tile),
              dailyProducts: collect(products),
              pageStates: notices.map(collect)
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

/// <summary>Allowlisted facts read from the page at one instant.</summary>
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
        new(CalendarEvidenceVocabulary.Allowed(reason, "timeout", "unreadable"), "unreadable", "unreadable", false, false, false, false, null,
            NoImage, false, false, false, [], [],
            new Dictionary<string, string?>(StringComparer.Ordinal) { ["calendarCell"] = null, ["dayTile"] = null, ["dailyProducts"] = null }, []);

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

    internal static PageSnapshot From(JsonElement json, Guid? expectedProduct)
    {
        var image = json.GetProperty("image");
        var imageState = image.GetProperty("present").GetBoolean()
            ? new PageImageState(
                true,
                image.GetProperty("connected").GetBoolean(),
                image.GetProperty("complete").GetBoolean(),
                image.GetProperty("naturalWidth").GetInt32(),
                image.GetProperty("naturalHeight").GetInt32(),
                CalendarEvidenceVocabulary.RouteShape(OptionalString(image, "src"), expectedProduct),
                CalendarEvidenceVocabulary.RouteShape(OptionalString(image, "currentSrc"), expectedProduct),
                OptionalString(image, "loading") is { } loading ? CalendarEvidenceVocabulary.Allowed(loading, "lazy", "eager") : null,
                image.GetProperty("width").GetInt32(),
                image.GetProperty("height").GetInt32(),
                image.GetProperty("visible").GetBoolean())
            : NoImage;
        var reconnect = json.GetProperty("reconnect");
        var trees = json.GetProperty("trees");
        return new PageSnapshot(
            null,
            CalendarEvidenceVocabulary.Allowed(json.GetProperty("interactive").GetString(), "true", "false", "absent"),
            CalendarEvidenceVocabulary.Allowed(json.GetProperty("readyState").GetString(), "loading", "interactive", "complete"),
            json.GetProperty("dayPage").GetBoolean(),
            json.GetProperty("dayLoading").GetBoolean(),
            json.GetProperty("nightlyUnavailable").GetBoolean(),
            json.GetProperty("previewFailedMarker").GetBoolean(),
            OptionalString(json, "tileState") is { } state ? CalendarEvidenceVocabulary.NightlyState(state) : null,
            imageState,
            json.GetProperty("blazorErrorUi").GetBoolean(),
            json.GetProperty("errorBoundary").GetBoolean(),
            reconnect.ValueKind == JsonValueKind.Object && reconnect.GetProperty("open").GetBoolean(),
            reconnect.ValueKind == JsonValueKind.Object
                ? CalendarEvidenceVocabulary.ReconnectClasses(reconnect.GetProperty("classes").EnumerateArray().Select(static name => name.GetString()))
                : [],
            [.. json.GetProperty("notices").EnumerateArray().Select(static notice => new PageNotice(
                CalendarEvidenceVocabulary.NoticeKind(notice.GetProperty("classes").EnumerateArray().Select(static name => name.GetString())),
                CalendarEvidenceVocabulary.NoticeTitle(notice.GetProperty("title").GetString())))],
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["calendarCell"] = CalendarEvidenceVocabulary.RenderMarkup(trees.GetProperty("calendarCell"), expectedProduct),
                ["dayTile"] = CalendarEvidenceVocabulary.RenderMarkup(trees.GetProperty("dayTile"), expectedProduct),
                ["dailyProducts"] = CalendarEvidenceVocabulary.RenderMarkup(trees.GetProperty("dailyProducts"), expectedProduct, omitOtherProducts: true)
            },
            [.. trees.GetProperty("pageStates").EnumerateArray()
                .Select(state => CalendarEvidenceVocabulary.RenderMarkup(state, expectedProduct))
                .Where(static markup => markup is not null)
                .Select(static markup => markup!)]);
    }

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>A host log event retained as coarse context: it never decides a failure category.</summary>
internal sealed record ServerLogContext(
    long AtMillisecondsAfterClick, LogLevel Level, string Category, int EventId, string? ExceptionType, string? InnerExceptionType);

internal sealed record CalendarDayPreviewReport(
    string TestClass,
    string TestName,
    string Row,
    string SelectedDay,
    string SelectedDayPath,
    string PreviewRoute,
    string FailedStep,
    string ExceptionType,
    int? TimeoutMilliseconds,
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
    IReadOnlyList<ServerLogContext> ServerLog,
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
            $"route@click={UrlAtClick}",
            $"route@failure={UrlAtFailure}",
            $"route@settle={UrlAfterSettle}",
            $"previewRequests={previews}",
            $"previewProbe={probe}",
            $"image@failure={image}",
            $"image@settle={settled}",
            $"dayLoading@settle={AfterSettle.DayLoading}",
            $"signals={(Observation.PageErrorSignals.Count == 0 ? "none" : string.Join(',', Observation.PageErrorSignals))}",
            FormattableString.Invariant($"hostLogContext={ServerLog.Count}"),
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
            writer.WriteString("previewRoute", PreviewRoute);
            writer.WriteStartObject("failure");
            writer.WriteString("step", FailedStep);
            writer.WriteString("exceptionType", ExceptionType);
            if (TimeoutMilliseconds is { } timeout) writer.WriteNumber("timeoutMs", timeout);
            writer.WriteEndObject();
            WriteStrings(writer, "categories", Categories.Select(static category => category.ToString()));
            WriteStrings(writer, "pageErrorSignals", Observation.PageErrorSignals);
            writer.WriteStartObject("navigation");
            writer.WriteString("interactiveAtClick", InteractiveAtClick);
            writer.WriteString("routeAtClick", UrlAtClick);
            writer.WriteString("routeAtFailure", UrlAtFailure);
            writer.WriteString("routeAfterSettle", UrlAfterSettle);
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
            writer.WriteStartArray("hostLogContext");
            foreach (var entry in ServerLog)
            {
                writer.WriteStartObject();
                writer.WriteNumber("atMsAfterClick", entry.AtMillisecondsAfterClick);
                writer.WriteString("level", entry.Level.ToString());
                writer.WriteString("category", entry.Category);
                writer.WriteNumber("eventId", entry.EventId);
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
                writer.WriteString("detail", entry.Detail);
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

/// <summary>Where retained evidence goes: one complete file, attached to the run, and one summary line.</summary>
internal interface ICalendarEvidenceSink
{
    Task<string> PersistAsync(string fileName, string content, CancellationToken cancellationToken);

    void WriteLine(string line);
}

/// <summary>
/// Writes evidence under a directory and attaches it to the test run. The file is written under a temporary name and
/// published by rename, then attached; a cancelled or failed write, publication or attachment leaves no file behind,
/// so only complete evidence is ever attached.
/// </summary>
internal sealed class FileEvidenceSink(string directory, Action<string> attach, Action<string> writeLine) : ICalendarEvidenceSink
{
    internal static FileEvidenceSink ForTestContext(TestContext testContext) => new(
        Path.Combine(testContext.ResultsDirectory ?? Path.GetTempPath(), CalendarDayPreviewEvidence.EvidenceDirectoryName),
        testContext.AddResultFile,
        testContext.WriteLine);

    public async Task<string> PersistAsync(string fileName, string content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        var partial = path + ".partial";
        var published = false;
        try
        {
            await File.WriteAllTextAsync(partial, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partial, path, overwrite: false);
            published = true;
            cancellationToken.ThrowIfCancellationRequested();
            attach(path);
            return path;
        }
        catch
        {
            TryDelete(partial);
            if (published)
            {
                TryDelete(path);
            }
            throw;
        }
    }

    public void WriteLine(string line) => writeLine(line);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Cleanup after a failed write is best-effort; the write's own failure is what propagates.")]
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // The write's failure propagates; a cleanup failure must not replace it.
        }
    }
}

internal sealed record ServerLogEntry(
    DateTimeOffset AtUtc, LogLevel Level, string Category, int EventId, string? ExceptionType, string? InnerExceptionType);

/// <summary>
/// Captures Warning-and-above host log events as coarse component, level, numeric event id and exception type only.
/// Messages, structured state, scopes, event names and full category names are never read or kept.
/// </summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes",
    Justification = "Instantiated by the Calendar browser tests and registered as a host logger provider.")]
internal sealed class ServerLogEvidence : ILoggerProvider
{
    private const int Capacity = 512;
    private readonly ConcurrentQueue<ServerLogEntry> entries = new();
    private int count;

    public ILogger CreateLogger(string categoryName) => new EvidenceLogger(this, CalendarEvidenceVocabulary.CoarseCategory(categoryName));

    internal IReadOnlyList<ServerLogEntry> Snapshot() => entries.ToArray();

    public void Dispose()
    {
        // Entries stay readable after the host disposes its providers.
    }

    private void Add(ServerLogEntry entry)
    {
        if (Interlocked.Increment(ref count) <= Capacity)
        {
            entries.Enqueue(entry);
        }
    }

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
                exception is null ? null : CalendarEvidenceVocabulary.TypeName(exception.GetType()),
                exception?.InnerException is null ? null : CalendarEvidenceVocabulary.TypeName(exception.InnerException.GetType())));
        }
    }
}
