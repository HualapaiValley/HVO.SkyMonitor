using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// One observing day (#988, prototype <c>day.html</c>): representative capture, night facts,
/// a night timeline of schedule / captures / candidates on one local axis, the recorded nightly
/// products of a sunrise period (#1138; the time-lapse stays "not yet generated" until #1130),
/// candidates and automation runs. Product cards read only evaluations retained under the displayed
/// period; another period of the same report date, such as one resolved for an earlier site, is
/// listed separately under its own site and boundaries. Each product is an equal-sized thumbnail tile
/// that opens one shared viewer with the larger image or player beside its details (#1148), so more
/// artifact kinds can join the row without widening it.
/// </summary>
public sealed partial class ObservingDayPage : ComponentBase, IAsyncDisposable
{
    /// <summary>
    /// One product kind of the period: the calendar's badge for it, so the two pages cannot disagree, the chosen
    /// whole-period evaluation, and one evaluation per completed civil hour.
    /// </summary>
    internal sealed record ProductCard(
        NightlyProductKind Kind,
        ArchiveCalendarPage.NightlyBadge Badge,
        NightlyProductWindowRecord? Daily,
        IReadOnlyList<NightlyProductWindowRecord> Hours)
    {
        internal NightlyProductSummary? Product => Daily?.FinalProduct;
    }

    private static readonly NightlyProductKind[] ProductKinds = [NightlyProductKind.StarTrail, NightlyProductKind.Keogram];

    internal sealed record CaptureSegment(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Count);

    internal sealed record HourTick(double Percent, string Label);

    private CancellationTokenSource? _loadCancellation;
    private CameraAgentObservingDayView? _view;
    private CameraAgentNightlyDayView? _nightly;
    private string? _nightlyMessage;
    private bool _nightlyLoading;
    private readonly HashSet<Guid> _failedPreviews = [];
    // The tile whose viewer is open: a nightly product kind, or the time-lapse sample when no kind is set.
    private NightlyProductKind? _viewerKind;
    private bool _viewerOpen;
    private string? _errorMessage;
    private bool _isLoading = true;
    private bool _invalidDate;
    private long _generation;

    [Inject] internal ICameraAgentObservingDayUiService DayService { get; set; } = default!;
    [Inject] internal ICameraAgentNightlyProductUiService NightlyProducts { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider Clock { get; set; } = default!;
    [Parameter] public string DateText { get; set; } = string.Empty;
    [Parameter, SupplyParameterFromQuery(Name = "calendar")] public string? CalendarVersion { get; set; }

    private DateOnly Date { get; set; }

    private static string CoverageBinMinutes => CameraAgentObservingDayUiService.CoverageBin.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);

    private string PageTitleText => _invalidDate ? "Observing day" : $"Observing day {DateText}";

    private string DayTitle => Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private string DayLead => _view is { } view
        ? $"{(view.Day.Day.SunrisePeriod is null ? "Legacy noon association" : "Starting-sunrise report date")}: " +
          $"{CivilBoundary(view.Day.Day.StartUtc)} through {CivilBoundary(view.Day.Day.EndUtc)} ({TimeZoneId})."
        : "The report date names the starting sunrise; the source period ends at the following sunrise.";

    private string CivilBoundary(DateTimeOffset utc)
        => TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, TimeZoneId).ToString("d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    private string TimeZoneId => _view?.Day.Day.TimeZoneFallback == false ? _view.Day.Day.TimeZoneId : TimeZoneInfo.Utc.Id;

    private string CapturesUrl => _view is { } view ? ArchiveCalendarPage.CapturesUrl(view.Day.Day) : "/gallery";

    private string CandidatesUrl => _view is { } view ? ArchiveCalendarPage.CandidatesUrl(view.Day.Day) : "/transients";

    private string CalendarUrl
    {
        get
        {
            var url = _invalidDate ? "/archive/calendar" : $"/archive/calendar?month={Date:yyyy-MM}";
            return CalendarVersion is null ? url : url + (_invalidDate ? "?" : "&") +
                "calendar=" + Uri.EscapeDataString(CalendarVersion);
        }
    }

    private string SunriseDayUrl => ArchiveCalendarPage.DayUrl(Date) + "?calendar=" +
        Uri.EscapeDataString(SunriseReportingPeriod.CurrentVersion);

    internal static ProductCard Card(NightlyProductDay day, NightlyProductKind kind, SunriseReportingPeriod period, bool periodOpen)
    {
        var daily = day.Daily(kind, period.IdentitySha256);
        var hours = day.Hours(kind, period.IdentitySha256);
        var otherPeriod = day.OtherPeriods(period.IdentitySha256).Any(other =>
            day.Daily(kind, other.IdentitySha256) is not null || day.Hours(kind, other.IdentitySha256).Count > 0);
        var summary = new NightlyProductDateSummary(day.ObservingDate, kind, period.IdentitySha256,
            daily?.FinalProduct?.ProductId, daily?.Status.Disposition, daily?.Status.ReasonCode,
            hours.Count(static hour => hour.FinalProduct is not null),
            hours.Count(static hour => hour.FinalProduct is null));
        return new ProductCard(kind,
            ArchiveCalendarPage.Badge(kind, summary, otherPeriod, unavailable: false, pending: periodOpen), daily, hours);
    }

    private List<ProductCard> Cards(CameraAgentObservingDayView view, SunriseReportingPeriod period, NightlyProductDay day)
        => ProductKinds.Select(kind => Card(day, kind, period, Clock.GetUtcNow() < view.Day.Day.EndUtc)).ToList();

    internal const string TimeLapseTileId = "product-tile-time-lapse";

    internal static string TileId(NightlyProductKind kind) => "product-tile-" + NightlyProductLinks.KindNoun(kind).Replace(' ', '-');

    private static string KindIcon(NightlyProductKind kind) => kind == NightlyProductKind.StarTrail ? "bi-stars" : "bi-bar-chart-steps";

    // A tile opens only when its viewer adds something: the larger image, or the hours behind a missing nightly product.
    private static bool CanOpen(ProductCard card) => card.Product is not null || card.Hours.Count > 0;

    private static string HourlySummary(ProductCard card) => FormattableString.Invariant(
        $"Hourly: {card.Hours.Count(static hour => hour.FinalProduct is not null)} of {card.Hours.Count} completed hours produced");

    private void OpenViewer(NightlyProductKind kind)
    {
        _viewerKind = kind;
        _viewerOpen = true;
    }

    private void OpenTimeLapse()
    {
        _viewerKind = null;
        _viewerOpen = true;
    }

    private void ViewerOpenChanged(bool open) => _viewerOpen = open;

    private bool ViewerOpen(ProductCard? viewed, CameraAgentTimeLapseSampleView? sample)
        => _viewerOpen && (viewed is not null || (_viewerKind is null && sample is not null));

    private string ViewerTriggerId => _viewerKind is { } kind ? TileId(kind) : TimeLapseTileId;

    private string ViewerTitle(ProductCard? viewed) => viewed is null
        ? "Night time-lapse sample"
        : $"{NightlyProductLinks.KindLabel(viewed.Kind)} · {DayTitle}";

    // The viewer shows the product's own preview, and nothing in its place once that preview has failed.
    private Uri? ViewerSource(ProductCard? viewed) => viewed?.Product is { } product && !_failedPreviews.Contains(product.ProductId)
        ? new Uri(NightlyProductLinks.Preview(product.ProductId), UriKind.Relative)
        : null;

    private void MarkViewedPreviewFailed(Guid? productId)
    {
        if (productId is { } id)
        {
            _failedPreviews.Add(id);
        }
    }

    /// <summary>
    /// What another retained period of the report date holds for one kind, from its own evaluations only: its nightly
    /// final and how many of its completed hours were produced.
    /// </summary>
    internal static string OtherPeriodFacts(NightlyProductDay day, SunriseReportingPeriod period, NightlyProductKind kind)
    {
        var noun = NightlyProductLinks.KindNoun(kind);
        var daily = day.Daily(kind, period.IdentitySha256);
        var hours = day.Hours(kind, period.IdentitySha256);
        var nightly = daily switch
        {
            null => $"no nightly {noun} evaluation",
            { FinalProduct: not null } => $"nightly {noun} produced",
            { Status: var status } => $"nightly {noun} not produced, {Outcome(status)}"
        };
        return hours.Count == 0
            ? nightly
            : FormattableString.Invariant(
                $"{nightly}; {hours.Count(static hour => hour.FinalProduct is not null)} of {hours.Count} hourly {noun}s produced");
    }

    // Another period is described in UTC and its own site, never in this page's time zone.
    internal static string OtherPeriodLabel(SunriseReportingPeriod period) => string.Create(CultureInfo.InvariantCulture,
        $"Site {period.Site.LocationId} version {period.Site.Version} ({period.Site.LatitudeDegrees:0.####}°, {period.Site.LongitudeDegrees:0.####}°, {period.Site.TimeZoneId}), {period.StartUtc.UtcDateTime:yyyy-MM-dd HH:mm}Z to {period.EndUtc.UtcDateTime:yyyy-MM-dd HH:mm}Z");

    internal static string ChipLabel(ArchiveCalendarPage.NightlyBadgeState state) => state switch
    {
        ArchiveCalendarPage.NightlyBadgeState.Produced => "Produced",
        ArchiveCalendarPage.NightlyBadgeState.Partial => "Partial",
        ArchiveCalendarPage.NightlyBadgeState.NotProduced => "Not produced",
        ArchiveCalendarPage.NightlyBadgeState.OtherPeriod => "Other period only",
        ArchiveCalendarPage.NightlyBadgeState.Pending => "Pending",
        ArchiveCalendarPage.NightlyBadgeState.NotGenerated => "Not generated",
        _ => "Unavailable"
    };

    private static string ChipClass(ArchiveCalendarPage.NightlyBadgeState state) => state switch
    {
        ArchiveCalendarPage.NightlyBadgeState.Produced => "hvo-chip--success",
        ArchiveCalendarPage.NightlyBadgeState.Partial => "hvo-chip--warning",
        ArchiveCalendarPage.NightlyBadgeState.Pending => "hvo-chip--info",
        _ => "hvo-chip--neutral"
    };

    /// <summary>
    /// Why a recorded final evaluation has no product, from its retained disposition only. A final composes the
    /// products of its segment windows, so it records no frame counts of its own; a final without sources means no
    /// segment window admitted a frame.
    /// </summary>
    internal static string Outcome(NightlyProductWindowStatus status) => status.Disposition switch
    {
        NightlyProductWindowDisposition.NoSources => "no segment window admitted a frame",
        NightlyProductWindowDisposition.Rejected => $"rejected ({status.ReasonCode ?? "no reason recorded"})",
        _ => "no current product is recorded"
    };

    // A final's admitted count is the segment products it composed; frame counts live in the product's lineage.
    private string ProductFacts(ProductCard card) => card.Product is { } product
        ? $"Frames {LocalSpan(product.FirstObservationUtc, product.LastObservationUtc)} from " +
          FormattableString.Invariant($"{card.Daily!.Status.AdmittedCount:N0} segment products; {product.Width:N0} × {product.Height:N0}.")
        : card.Daily is { Status: var status } && card.Badge.State == ArchiveCalendarPage.NightlyBadgeState.NotProduced
            ? $"The period was evaluated: {Outcome(status)}."
            : card.Badge.Description + ".";

    private string HourTitle(NightlyProductWindowRecord hour)
    {
        var span = LocalSpan(hour.Status.WindowStartUtc, hour.Status.WindowEndUtc);
        return hour.FinalProduct is null
            ? $"{span}: not produced, {Outcome(hour.Status)}"
            : FormattableString.Invariant($"{span}: produced from {hour.Status.AdmittedCount:N0} segment products");
    }

    private static string SampleCaption(CameraAgentTimeLapseSampleView sample) => FormattableString.Invariant(
        $"Sample, not generated from this night · {sample.MediaType} · declared {sample.Width} × {sample.Height}");

    private void MarkPreviewFailed(Guid productId) => _failedPreviews.Add(productId);

    // The calendar's own clamp; a step never leaves it.
    internal static readonly DateOnly MinimumDate = new(1, 2, 1);
    internal static readonly DateOnly MaximumDate = new(9999, 11, 30);

    private string DayUrl(int direction)
    {
        var target = Date.AddDays(direction);
        var url = ArchiveCalendarPage.DayUrl(target < MinimumDate ? MinimumDate : target > MaximumDate ? MaximumDate : target);
        return CalendarVersion is null ? url : url + "?calendar=" + Uri.EscapeDataString(CalendarVersion);
    }

    // A repeated civil hour keeps its offset, so the two hours of a daylight-saving fall-back stay distinct.
    private string HourLabel(ProductCard card, NightlyProductWindowRecord hour)
    {
        var label = LocalTime(hour.Status.WindowStartUtc);
        return card.Hours.Count(other => LocalTime(other.Status.WindowStartUtc) == label) > 1
            ? label + " " + LocalOffset(hour.Status.WindowStartUtc)
            : label;
    }

    /// <summary>
    /// A local span that never reads as reversed or ambiguous: both endpoints carry their dates when the span crosses
    /// a local date, and their UTC offsets when it crosses a daylight-saving change.
    /// </summary>
    internal string LocalSpan(DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (!TryLocal(startUtc, out var start) || !TryLocal(endUtc, out var end))
        {
            return string.Create(CultureInfo.InvariantCulture, $"{startUtc.UtcDateTime:HH:mm d MMM}Z–{endUtc.UtcDateTime:HH:mm d MMM}Z");
        }
        var format = start.Date == end.Date ? "HH:mm" : "HH:mm d MMM";
        return start.Offset == end.Offset
            ? $"{start.ToString(format, CultureInfo.InvariantCulture)}–{end.ToString(format, CultureInfo.InvariantCulture)}"
            : $"{start.ToString(format, CultureInfo.InvariantCulture)} {Offset(start)}–{end.ToString(format, CultureInfo.InvariantCulture)} {Offset(end)}";
    }

    private string LocalOffset(DateTimeOffset utc) => TryLocal(utc, out var local) ? Offset(local) : "UTC";

    private static string Offset(DateTimeOffset local) => "UTC" + local.ToString("zzz", CultureInfo.InvariantCulture);

    private bool TryLocal(DateTimeOffset utc, out DateTimeOffset local)
    {
        try
        {
            local = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, TimeZoneId);
            return true;
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            local = utc;
            return false;
        }
    }

    private string LocalTime(DateTimeOffset utc)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, TimeZoneId).ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return utc.ToString("HH:mm", CultureInfo.InvariantCulture) + "Z";
        }
    }

    // A night is complete when captures cover at least this fraction of the scheduled window; the
    // page states the threshold beside the label so the word carries no hidden meaning.
    internal const double CompleteCoverageFraction = 0.9;

    private static double? CoverageFraction(CameraAgentObservingDayView view) => view.Schedule is { ExpectedDuration.Ticks: > 0 } schedule
        ? Math.Min(1, schedule.CoveredDuration.TotalSeconds / schedule.ExpectedDuration.TotalSeconds)
        : null;

    private string StateLabel(CameraAgentObservingDayView view) => view.Day.Day switch
    {
        var day when day.Contains(Clock.GetUtcNow()) => "In progress",
        var day when day.StartUtc > Clock.GetUtcNow() => "Upcoming day",
        _ when view.Day.CaptureCount == 0 => "No archived session",
        _ when CoverageFraction(view) is { } fraction && fraction >= CompleteCoverageFraction => "Complete",
        _ when CoverageFraction(view) is > 0 => "Partial",
        _ when CoverageFraction(view) is 0 => "Outside window",
        _ => "Archived session"
    };

    private string StateChipClass(CameraAgentObservingDayView view) => StateLabel(view) switch
    {
        "Complete" => "hvo-chip--success",
        "In progress" => "hvo-chip--info",
        "Partial" => "hvo-chip--warning",
        "Outside window" or "Archived session" or "Upcoming day" => "hvo-chip--info",
        _ => "hvo-chip--neutral"
    };

    private string StateDetail(CameraAgentObservingDayView view) => StateLabel(view) switch
    {
        "In progress" when view.Day.CaptureCount == 0 => "This observing day is open; no captures have been retained yet.",
        "In progress" => $"Captures retained from {LocalTime(view.Day.FirstExposureUtc!.Value)} to {LocalTime(view.Day.LastExposureUtc!.Value)} so far. This observing day has not ended.",
        "Upcoming day" => "This observing day has not started; retained captures, if any, may reflect clock skew.",
        "No archived session" => "Nothing was retained inside this observing day.",
        "Outside window" => $"Captures from {LocalTime(view.Day.FirstExposureUtc!.Value)} to {LocalTime(view.Day.LastExposureUtc!.Value)}, none inside the scheduled window.",
        "Complete" => FormattableString.Invariant($"Captures from {LocalTime(view.Day.FirstExposureUtc!.Value)} to {LocalTime(view.Day.LastExposureUtc!.Value)}; complete means at least {CompleteCoverageFraction:P0} of the scheduled window."),
        "Partial" => FormattableString.Invariant($"Captures from {LocalTime(view.Day.FirstExposureUtc!.Value)} to {LocalTime(view.Day.LastExposureUtc!.Value)}; under {CompleteCoverageFraction:P0} of the scheduled window."),
        _ => $"Captures from {LocalTime(view.Day.FirstExposureUtc!.Value)} to {LocalTime(view.Day.LastExposureUtc!.Value)}"
    };

    private string ScheduledWindow(CameraAgentObservingDayView view) => view.Schedule switch
    {
        null => "Schedule unavailable",
        { OpenWindows.Count: 0 } => "No window scheduled",
        { OpenWindows: var windows } => string.Join(", ", windows.Select(window => $"{LocalTime(window.StartUtc)}–{LocalTime(window.EndUtc)}"))
    };

    private static string Coverage(CameraAgentObservingDayView view) => view.Schedule switch
    {
        null => "Unavailable",
        { ExpectedDuration.Ticks: <= 0 } => "No window scheduled",
        var schedule => FormattableString.Invariant($"{Math.Min(100, 100 * schedule.CoveredDuration.TotalSeconds / schedule.ExpectedDuration.TotalSeconds):0}% of {FormatDuration(schedule.ExpectedDuration)}")
    };

    private string RepresentativeCaption(CameraAgentObservingDayView view)
        => view.Day.RepresentativeExposureUtc is { } exposure
            ? $"Newest capture with a published preview, {LocalDateTime(exposure)} ({TimeZoneId})"
            : "Newest capture with a published preview";

    private string LocalDateTime(DateTimeOffset utc)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(utc, TimeZoneId)
                .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return utc.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
        }
    }

    internal static string FormatDuration(TimeSpan value) => value switch
    {
        { TotalSeconds: < 1 } => "0s",
        { TotalMinutes: < 1 } => FormattableString.Invariant($"{value.TotalSeconds:0}s"),
        { TotalHours: < 1 } => FormattableString.Invariant($"{(int)value.TotalMinutes}m {value.Seconds:00}s"),
        _ => FormattableString.Invariant($"{(int)value.TotalHours}h {value.Minutes:00}m")
    };

    private string TimelineRange(CameraAgentObservingDayView view)
        => $"{LocalTime(view.Day.Day.StartUtc)} {view.Day.Day.Date:dd MMM} – {LocalTime(view.Day.Day.EndUtc)} {view.Day.Day.Date.AddDays(1):dd MMM} ({TimeZoneId})";

    private static double NightHours(CameraAgentObservingDayView view) => (view.Day.Day.EndUtc - view.Day.Day.StartUtc).TotalHours;

    private static double Percent(CameraAgentObservingDayView view, DateTimeOffset utc)
    {
        var total = (view.Day.Day.EndUtc - view.Day.Day.StartUtc).TotalSeconds;
        var offset = (utc - view.Day.Day.StartUtc).TotalSeconds;
        return Math.Clamp(100 * offset / total, 0, 100);
    }

    private static string BarStyle(CameraAgentObservingDayView view, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        var left = Percent(view, startUtc);
        var width = Math.Max(0.15, Percent(view, endUtc) - left);
        return FormattableString.Invariant($"left: {left:0.###}%; width: {width:0.###}%");
    }

    private static string MarkerStyle(CameraAgentObservingDayView view, DateTimeOffset utc)
        => FormattableString.Invariant($"left: {Percent(view, utc):0.###}%");

    /// <summary>Exposures collapsed into runs where consecutive exposures are within one coverage bin.</summary>
    internal static IReadOnlyList<CaptureSegment> CaptureSegments(CameraAgentObservingDayView view)
    {
        var segments = new List<CaptureSegment>();
        CaptureSegment? current = null;
        foreach (var exposure in view.ExposureInstantsUtc.Order())
        {
            var start = exposure;
            var end = start + CameraAgentObservingDayUiService.CoverageBin;
            if (current is not null && start <= current.EndUtc)
            {
                current = current with { EndUtc = end > current.EndUtc ? end : current.EndUtc, Count = current.Count + 1 };
                segments[^1] = current;
            }
            else
            {
                current = new CaptureSegment(start, end, 1);
                segments.Add(current);
            }
        }
        return segments;
    }

    private List<HourTick> HourTicks(CameraAgentObservingDayView view)
    {
        var ticks = new List<HourTick>();
        var start = view.Day.Day.StartUtc;
        var end = view.Day.Day.EndUtc;
        // Six evenly spaced ticks fit a phone and follow real elapsed time across DST days.
        for (var index = 0; index <= 6; index++)
        {
            var utc = start + TimeSpan.FromTicks((end - start).Ticks * index / 6);
            ticks.Add(new HourTick(Percent(view, utc), LocalTime(utc)));
        }
        return ticks;
    }

    protected override Task OnParametersSetAsync()
    {
        if (!DateOnly.TryParseExact(DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            parsed < MinimumDate || parsed > MaximumDate)
        {
            _invalidDate = true;
            _isLoading = false;
            return Task.CompletedTask;
        }
        _invalidDate = false;
        Date = parsed;
        return LoadAsync();
    }

    private async Task LoadAsync()
    {
        var generation = Interlocked.Increment(ref _generation);
        var cancellation = new CancellationTokenSource();
        var prior = Interlocked.Exchange(ref _loadCancellation, cancellation);
        if (prior is not null)
        {
            await prior.CancelAsync();
            prior.Dispose();
        }
        _isLoading = true;
        _errorMessage = null;
        _viewerOpen = false;
        _viewerKind = null;
        try
        {
            var result = CalendarVersion is null
                ? await DayService.GetAsync(Date, cancellation.Token)
                : await DayService.GetAsync(Date, CalendarVersion, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _view = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _view = result.Value;
                await LoadNightlyAsync(generation, cancellation.Token);
            }
            else
            {
                _view = null;
                _errorMessage = result.Message ?? "The observing day is unavailable.";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            if (generation == Volatile.Read(ref _generation))
            {
                _isLoading = false;
            }
        }
    }

    // Nightly products are recorded against sunrise report dates, so a legacy noon day reads none. The day itself is
    // shown while they load.
    private async Task LoadNightlyAsync(long generation, CancellationToken cancellationToken)
    {
        _nightly = null;
        _nightlyMessage = null;
        _failedPreviews.Clear();
        if (_view?.Day.Day.SunrisePeriod is null)
        {
            _nightlyLoading = false;
            return;
        }
        _nightlyLoading = true;
        _isLoading = false;
        StateHasChanged();
        var result = await NightlyProducts.GetDayAsync(Date, cancellationToken);
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }
        _nightlyLoading = false;
        if (result.IsSuccess && result.Value is not null)
        {
            _nightly = result.Value;
        }
        else
        {
            _nightlyMessage = result.Message ?? "Nightly products are temporarily unavailable.";
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        var cancellation = Interlocked.Exchange(ref _loadCancellation, null);
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }
}
