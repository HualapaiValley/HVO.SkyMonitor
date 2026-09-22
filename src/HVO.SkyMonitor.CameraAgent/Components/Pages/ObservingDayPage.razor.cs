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

    private string DayUrl(int direction) => ArchiveCalendarPage.DayUrl(Date.AddDays(direction));

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

    private static string StateLabel(CameraAgentObservingDayView view) => view.Day.CaptureCount switch
    {
        0 => "No archived session",
        _ when view.Schedule is { ExpectedDuration.Ticks: > 0 } schedule && schedule.CoveredDuration >= schedule.ExpectedDuration * 0.9 => "Complete",
        _ when view.Schedule is { ExpectedDuration.Ticks: > 0 } => "Partial",
        _ => "Archived session"
    };

    private static string StateChipClass(CameraAgentObservingDayView view) => StateLabel(view) switch
    {
        "Complete" => "hvo-chip--success",
        "Partial" => "hvo-chip--warning",
        "Archived session" => "hvo-chip--info",
        _ => "hvo-chip--neutral"
    };

    private string StateDetail(CameraAgentObservingDayView view) => view.Day.CaptureCount == 0
        ? "Nothing was retained inside this observing day."
        : view.Day is { FirstExposureUtc: { } first, LastExposureUtc: { } last }
            ? $"Captures from {LocalTime(first)} to {LocalTime(last)}"
            : "Captures retained.";

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

    private static string RepresentativeCaption(CameraAgentObservingDayView view)
        => view.Day.LastExposureUtc is { } last
            ? FormattableString.Invariant($"Newest displayable capture, {last:yyyy-MM-dd HH:mm:ss} UTC")
            : "Newest displayable capture";

    internal static string FormatDuration(TimeSpan value) => value switch
    {
        { TotalSeconds: < 1 } => "0s",
        { TotalMinutes: < 1 } => FormattableString.Invariant($"{value.TotalSeconds:0}s"),
        { TotalHours: < 1 } => FormattableString.Invariant($"{(int)value.TotalMinutes}m {value.Seconds:00}s"),
        _ => FormattableString.Invariant($"{(int)value.TotalHours}h {value.Minutes:00}m")
    };

    private string TimelineRange(CameraAgentObservingDayView view)
        => $"{LocalTime(view.Day.Day.StartUtc)} – {LocalTime(view.Day.Day.EndUtc)} {(view.Day.Day.TimeZoneFallback ? "UTC" : "local")}";

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

    /// <summary>Captures collapsed into runs where consecutive exposures are within one coverage bin.</summary>
    internal static IReadOnlyList<CaptureSegment> CaptureSegments(CameraAgentObservingDayView view)
    {
        var segments = new List<CaptureSegment>();
        CaptureSegment? current = null;
        foreach (var capture in view.Captures.OrderBy(static capture => capture.ExposureStartedUtc))
        {
            var start = capture.ExposureStartedUtc;
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
        // One label every two hours keeps the axis legible at typical widths.
        for (var utc = start; utc <= end; utc = utc.AddHours(2))
        {
            ticks.Add(new HourTick(Percent(view, utc), LocalTime(utc)));
        }
        return ticks;
    }

    protected override Task OnParametersSetAsync()
    {
        if (!DateOnly.TryParseExact(DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
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
