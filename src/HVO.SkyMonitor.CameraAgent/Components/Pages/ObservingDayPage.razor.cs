using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// One observing day (#988, prototype <c>day.html</c>): representative capture, night facts,
/// a night timeline of schedule / captures / candidates on one local axis, the nightly product
/// slots (honestly "not yet produced" until #993), candidates and automation runs.
/// </summary>
public sealed partial class ObservingDayPage : ComponentBase, IAsyncDisposable
{
    internal sealed record ProductSlot(string Title, string Description, string Icon);

    internal static readonly ProductSlot[] ProductSlots =
    [
        new("Night timelapse", "Processed captures of the observing window in sequence.", "bi-film"),
        new("Star trail", "Lighten composite of the night's quality-approved frames.", "bi-stars"),
        new("North–south keogram", "One north–zenith–south slice per capture along the time axis.", "bi-bar-chart-steps")
    ];

    internal sealed record CaptureSegment(DateTimeOffset StartUtc, DateTimeOffset EndUtc, int Count);

    internal sealed record HourTick(double Percent, string Label);

    private CancellationTokenSource? _loadCancellation;
    private CameraAgentObservingDayView? _view;
    private string? _errorMessage;
    private bool _isLoading = true;
    private bool _invalidDate;
    private long _generation;

    [Inject] internal ICameraAgentObservingDayUiService DayService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider Clock { get; set; } = default!;
    [Parameter] public string DateText { get; set; } = string.Empty;

    private DateOnly Date { get; set; }

    private static string CoverageBinMinutes => CameraAgentObservingDayUiService.CoverageBin.TotalMinutes.ToString("0", CultureInfo.InvariantCulture);

    private string PageTitleText => _invalidDate ? "Observing day" : $"Observing day {DateText}";

    private string DayTitle => Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private string DayLead => _view is { } view
        ? FormattableString.Invariant($"Local noon {view.Day.Day.Date:d MMMM} through local noon {view.Day.Day.Date.AddDays(1):d MMMM}{(view.Day.Day.TimeZoneFallback ? " (UTC days; no deployment time zone)" : "")}.")
        : "Local noon through the next local noon.";

    private string TimeZoneId => _view?.Day.Day.TimeZoneFallback == false ? _view.Day.Day.TimeZoneId : TimeZoneInfo.Utc.Id;

    private string CapturesUrl => _view is { } view ? ArchiveCalendarPage.CapturesUrl(view.Day.Day) : "/gallery";

    private string CandidatesUrl => _view is { } view ? ArchiveCalendarPage.CandidatesUrl(view.Day.Day) : "/transients";

    // The calendar's own clamp; a step never leaves it.
    internal static readonly DateOnly MinimumDate = new(1, 2, 1);
    internal static readonly DateOnly MaximumDate = new(9999, 11, 30);

    private string DayUrl(int direction)
    {
        var target = Date.AddDays(direction);
        return ArchiveCalendarPage.DayUrl(target < MinimumDate ? MinimumDate : target > MaximumDate ? MaximumDate : target);
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
        try
        {
            var result = await DayService.GetAsync(Date, cancellation.Token);
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
