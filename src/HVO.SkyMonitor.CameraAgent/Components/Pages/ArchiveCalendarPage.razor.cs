using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// The observing calendar as a month grid (#988, prototype <c>calendar.html</c>). Weeks start on
/// Sunday; the grid always covers whole weeks so the month's leading and trailing days from the
/// neighbouring months are shown muted. Every cell links to the observing-day page.
/// </summary>
public sealed partial class ArchiveCalendarPage : ComponentBase, IAsyncDisposable
{
    internal static readonly string[] Weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryCalendar? _calendar;
    private string? _errorMessage;
    private readonly HashSet<Guid> _failedThumbnails = [];
    private bool _isLoading = true;
    private long _generation;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Inject] internal IObservingDayCalendarProvider ObservingDays { get; set; } = default!;
    [Parameter, SupplyParameterFromQuery(Name = "month")] public string? Month { get; set; }

    internal sealed record CalendarCell(DateOnly Date, bool InMonth, bool IsToday, CameraAgentGalleryCalendarDay? Day);

    private DateOnly LatestObservingDay => ObservingDays.Current.Resolve(TimeProvider.GetUtcNow()).Date;

    // The month is taken from the query when it parses, otherwise the current observing day's
    // month; clamped so grid arithmetic never leaves the calendar.
    private DateOnly MonthStart
    {
        get
        {
            var date = DateOnly.TryParseExact(Month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : new DateOnly(LatestObservingDay.Year, LatestObservingDay.Month, 1);
            var minimum = new DateOnly(1, 2, 1);
            var maximum = new DateOnly(9999, 11, 1);
            return date < minimum ? minimum : date > maximum ? maximum : new DateOnly(date.Year, date.Month, 1);
        }
    }

    private DateOnly GridStart => MonthStart.AddDays(-(int)MonthStart.DayOfWeek);

    // Whole weeks covering the month: 4, 5 or 6 rows.
    private DateOnly GridEnd
    {
        get
        {
            var monthEnd = MonthStart.AddMonths(1).AddDays(-1);
            return monthEnd.AddDays(6 - (int)monthEnd.DayOfWeek);
        }
    }

    private string MonthLabel => MonthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    private IReadOnlyList<CalendarCell> Cells
    {
        get
        {
            var byDate = _calendar?.Days.ToDictionary(static day => day.Day.Date) ?? [];
            var today = LatestObservingDay;
            var cells = new List<CalendarCell>();
            for (var date = GridStart; date <= GridEnd; date = date.AddDays(1))
            {
                byDate.TryGetValue(date, out var day);
                cells.Add(new CalendarCell(date, date.Month == MonthStart.Month, date == today, day));
            }
            return cells;
        }
    }

    private IReadOnlyList<CameraAgentGalleryCalendarDay> MonthDays =>
        _calendar?.Days.Where(day => day.Day.Date.Month == MonthStart.Month && day.Day.Date.Year == MonthStart.Year).ToArray() ?? [];

    private int ObservedNights => MonthDays.Count(static day => day.CaptureCount > 0);

    private long TotalCandidates => MonthDays.Sum(static day => day.CandidateCount);

    private void MarkThumbnailFailed(Guid captureId) => _failedThumbnails.Add(captureId);

    private static string CellClass(CalendarCell cell)
    {
        var classes = "calendar-day";
        if (!cell.InMonth)
        {
            classes += " calendar-day--outside";
        }
        if (cell.Day is null || cell.Day.CaptureCount == 0)
        {
            classes += " calendar-day--empty";
        }
        if (cell.IsToday)
        {
            classes += " calendar-day--today";
        }
        return classes;
    }

    private static string CellDayLabel(CalendarCell cell)
        => cell.InMonth
            ? cell.Date.Day.ToString(CultureInfo.InvariantCulture)
            : cell.Date.ToString("MMM d", CultureInfo.InvariantCulture);

    private static string CellSummary(CalendarCell cell)
        => cell.Day is { CaptureCount: > 0 } day
            ? FormattableString.Invariant($"{day.CaptureCount:N0} capture{(day.CaptureCount == 1 ? "" : "s")}")
            : "No retained captures";

    private static string CellAriaLabel(CalendarCell cell)
        => cell.Day is { CaptureCount: > 0 } day
            ? FormattableString.Invariant($"Observing day {cell.Date:MMMM d yyyy}, {day.CaptureCount:N0} captures, {day.CandidateCount:N0} candidates")
            : FormattableString.Invariant($"Observing day {cell.Date:MMMM d yyyy}, no retained captures");

    internal static string DayUrl(DateOnly date) => FormattableString.Invariant($"/archive/day/{date:yyyy-MM-dd}");

    // Day links hand the night's UTC boundaries to the filter-backed pages so
    // the same evidence is selected there; the archive's inclusive upper bound
    // excludes the next night's first millisecond.
    internal static string CapturesUrl(ObservingDay day) => NavigationUrl("/gallery", day);

    internal static string CandidatesUrl(ObservingDay day) => NavigationUrl("/transients", day);

    private static string NavigationUrl(string path, ObservingDay day) => FormattableString.Invariant(
        $"{path}?from={day.StartUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}&to={day.EndUtc.AddMilliseconds(-1).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}");

    internal static string ThumbnailUrl(Guid captureId) => FormattableString.Invariant($"/api/v1/operations/gallery/{captureId:D}/thumbnail");

    private string MonthUrl(int direction, bool today = false)
    {
        var target = today ? new DateOnly(LatestObservingDay.Year, LatestObservingDay.Month, 1) : MonthStart.AddMonths(direction);
        return NavigationManager.GetUriWithQueryParameter("month", target.ToString("yyyy-MM", CultureInfo.InvariantCulture));
    }

    protected override Task OnParametersSetAsync() => LoadAsync();

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
            var result = await OperatorService.GetArchiveCalendarAsync(
                new CameraAgentGalleryCalendarQuery(GridStart, GridEnd), cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _calendar = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _calendar = result.Value;
            }
            else
            {
                _calendar = null;
                _errorMessage = result.Message ?? "The observing calendar is unavailable.";
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
