using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// The observing calendar as a month grid (#988, prototype <c>calendar.html</c>). Weeks start on
/// Sunday; the grid always covers whole weeks so the month's leading and trailing days from the
/// neighbouring months are shown muted. Every cell links to the observing-day page. Nightly product badges (#1138)
/// come from recorded evaluations and are shown only in the sunrise-period view, whose report dates they share. A
/// badge reads only evaluations of the sunrise period the cell resolves to; evaluations retained under another period
/// of the same report date, such as one resolved for an earlier site, are noted and never counted as its products.
/// </summary>
public sealed partial class ArchiveCalendarPage : ComponentBase, IAsyncDisposable
{
    internal static readonly string[] Weekdays = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryCalendar? _calendar;
    private Dictionary<(DateOnly Date, NightlyProductKind Kind), NightlyProductDateSummary> _nightly = [];
    private HashSet<(DateOnly Date, NightlyProductKind Kind)> _otherPeriods = [];
    private bool _nightlyUnavailable;
    private string? _errorMessage;
    private readonly HashSet<Guid> _failedThumbnails = [];
    private bool _isLoading = true;
    private long _generation;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Inject] internal IObservingDayCalendarProvider ObservingDays { get; set; } = default!;
    [Inject] internal ICameraAgentNightlyProductUiService NightlyProducts { get; set; } = default!;
    [Parameter, SupplyParameterFromQuery(Name = "month")] public string? Month { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "calendar")] public string? CalendarVersion { get; set; }

    internal sealed record CalendarCell(DateOnly Date, bool InMonth, bool IsToday, CameraAgentGalleryCalendarDay? Day);

    internal enum NightlyBadgeState { Produced, Partial, NotProduced, OtherPeriod, Pending, NotGenerated, Unavailable }

    internal sealed record NightlyBadge(string Letter, NightlyBadgeState State, string Description)
    {
        internal string CssClass => State switch
        {
            NightlyBadgeState.Produced => "available",
            NightlyBadgeState.Partial => "partial",
            NightlyBadgeState.NotProduced => "unavailable not-produced",
            _ => "unavailable"
        };
    }

    private static readonly NightlyProductKind[] BadgeKinds = [NightlyProductKind.StarTrail, NightlyProductKind.Keogram];

    private const string TimeLapseDescription = "Time-lapse not yet generated";

    private bool ShowsNightlyProducts => _calendar?.CalendarVersion == SunriseReportingPeriod.CurrentVersion;

    private ObservingDayCalendar SelectedCalendar => CalendarVersion == ObservingDayCalendar.LegacyNoonVersion
        ? ObservingDays.Current.LegacyNoon : ObservingDays.Current;

    private bool HasCurrentReportingPeriod =>
        (CalendarVersion is null || CalendarVersion == SelectedCalendar.CalendarVersion) &&
        SelectedCalendar.TryResolve(TimeProvider.GetUtcNow(), out _);

    private string PeriodLabel => (_calendar?.CalendarVersion ?? CalendarVersion ?? ObservingDays.Current.CalendarVersion) switch
    {
        SunriseReportingPeriod.CurrentVersion => "Local reporting period / sunrise-to-sunrise",
        ObservingDayCalendar.LegacyNoonVersion => "Legacy observing day / noon-to-noon",
        _ => "Unsupported reporting calendar"
    };

    private DateOnly LatestObservingDay => SelectedCalendar.TryResolve(TimeProvider.GetUtcNow(), out var day)
        ? day.Date : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(TimeProvider.GetUtcNow(), SelectedCalendar.TimeZone).DateTime);

    private string SelectedDayUrl(DateOnly date) => CalendarVersion is null ? DayUrl(date) :
        DayUrl(date) + "?calendar=" + Uri.EscapeDataString(CalendarVersion);

    private string LegacyCalendarUrl => NavigationManager.GetUriWithQueryParameter("calendar", ObservingDayCalendar.LegacyNoonVersion);

    private string SunriseCalendarUrl => NavigationManager.GetUriWithQueryParameter("calendar", SunriseReportingPeriod.CurrentVersion);

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
            var hasCurrentPeriod = HasCurrentReportingPeriod;
            var cells = new List<CalendarCell>();
            for (var date = GridStart; date <= GridEnd; date = date.AddDays(1))
            {
                byDate.TryGetValue(date, out var day);
                cells.Add(new CalendarCell(date, date.Month == MonthStart.Month, hasCurrentPeriod && date == today, day));
            }
            return cells;
        }
    }

    private IReadOnlyList<CameraAgentGalleryCalendarDay> MonthDays =>
        _calendar?.Days.Where(day => day.Day.Date.Month == MonthStart.Month && day.Day.Date.Year == MonthStart.Year).ToArray() ?? [];

    private int ObservedNights => MonthDays.Count(static day => day.CaptureCount > 0);

    private int NightlyProductNights => _nightly.Values
        .Where(summary => summary.ObservingDate.Month == MonthStart.Month && summary.ObservingDate.Year == MonthStart.Year &&
            summary.DailyProductId is not null)
        .Select(static summary => summary.ObservingDate).Distinct().Count();

    private int NightlyProductCount(NightlyProductKind kind) => _nightly.Values.Count(summary =>
        summary.Kind == kind && summary.DailyProductId is not null &&
        summary.ObservingDate.Month == MonthStart.Month && summary.ObservingDate.Year == MonthStart.Year);

    private int OtherPeriodNights => _otherPeriods
        .Where(record => record.Date.Month == MonthStart.Month && record.Date.Year == MonthStart.Year)
        .Select(static record => record.Date).Distinct().Count();

    private bool HasNightlyRecord(DateOnly date) =>
        BadgeKinds.Any(kind => _nightly.ContainsKey((date, kind)) || _otherPeriods.Contains((date, kind)));

    private IReadOnlyList<NightlyBadge> Badges(CalendarCell cell) => [.. BadgeKinds.Select(kind => Badge(kind,
        _nightly.GetValueOrDefault((cell.Date, kind)), _otherPeriods.Contains((cell.Date, kind)), _nightlyUnavailable,
        IsPending(cell.Date)))];

    private string? PeriodIdentity(DateOnly date)
    {
        try
        {
            return SelectedCalendar.Resolve(date).SunrisePeriod?.IdentitySha256;
        }
        catch (ReportingPeriodUnavailableException)
        {
            return null;
        }
    }

    // The current period cannot have a daily product before its closing sunrise, and later dates have no period yet.
    private bool IsPending(DateOnly date) => HasCurrentReportingPeriod && date >= LatestObservingDay;

    /// <summary>
    /// One kind's badge on one report date, from the summary of the sunrise period the date resolves to. A daily final
    /// lights it; hourly finals alone are partial; a recorded daily evaluation without a product keeps its reason;
    /// evaluations retained only under another period of the date are named as such; anything else is pending or not
    /// generated. Nothing is inferred, and another period's products never light it.
    /// </summary>
    internal static NightlyBadge Badge(
        NightlyProductKind kind,
        NightlyProductDateSummary? summary,
        bool otherPeriod,
        bool unavailable,
        bool pending)
    {
        var label = NightlyProductLinks.KindLabel(kind);
        var noun = NightlyProductLinks.KindNoun(kind);
        var letter = kind == NightlyProductKind.StarTrail ? "S" : "K";
        var hourly = summary is { HourlyProduced: > 0 } produced
            ? FormattableString.Invariant($"{produced.HourlyProduced} hourly {noun}{(produced.HourlyProduced == 1 ? "" : "s")}")
            : null;
        var also = otherPeriod ? "; also recorded under another source period of this date" : "";
        if (unavailable)
        {
            return new(letter, NightlyBadgeState.Unavailable, $"{label} status unavailable");
        }
        if (summary?.DailyProductId is not null)
        {
            return new(letter, NightlyBadgeState.Produced,
                $"Nightly {noun} produced" + (hourly is null ? "" : $", {hourly}") + also);
        }
        if (hourly is not null)
        {
            return new(letter, NightlyBadgeState.Partial, (pending
                ? $"{hourly} so far; the nightly {noun} is due after the period ends"
                : $"Partial: {hourly}, no nightly {noun}") + also);
        }
        return summary?.DailyDisposition switch
        {
            NightlyProductWindowDisposition.NoSources => new(letter, NightlyBadgeState.NotProduced,
                $"{label} not produced: no admitted frames" + also),
            NightlyProductWindowDisposition.Rejected => new(letter, NightlyBadgeState.NotProduced,
                $"{label} not produced: rejected ({summary.DailyReasonCode ?? "no reason recorded"})" + also),
            _ when otherPeriod => new(letter, NightlyBadgeState.OtherPeriod,
                $"{label} recorded only under another source period of this date (another site or time-zone rules)"),
            _ when pending => new(letter, NightlyBadgeState.Pending, $"{label} pending; the period has not ended"),
            _ => new(letter, NightlyBadgeState.NotGenerated, $"{label} not generated")
        };
    }

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

    private string CellAriaLabel(CalendarCell cell, bool missingThumbnail)
    {
        var label = cell.Day is { CaptureCount: > 0 } day
            ? FormattableString.Invariant($"Observing day {cell.Date:MMMM d yyyy}, {day.CaptureCount:N0} captures, {day.CandidateCount:N0} candidates{(missingThumbnail ? ", preview unavailable" : string.Empty)}")
            : FormattableString.Invariant($"Observing day {cell.Date:MMMM d yyyy}, no retained captures");
        return ShowsBadges(cell)
            ? label + ". " + string.Join(". ", Badges(cell).Select(static badge => badge.Description)) + ". " + TimeLapseDescription
            : label;
    }

    private bool ShowsBadges(CalendarCell cell) => ShowsNightlyProducts && (cell.Day is { CaptureCount: > 0 } || HasNightlyRecord(cell.Date));

    internal static string DayUrl(DateOnly date) => FormattableString.Invariant($"/archive/day/{date:yyyy-MM-dd}");

    // Day links hand the night's UTC boundaries to the filter-backed pages so
    // the same evidence is selected there; the archive's inclusive upper bound
    // excludes the next night's first millisecond.
    internal static string CapturesUrl(ObservingDay day) => NavigationUrl("/gallery", day);

    internal static string CandidatesUrl(ObservingDay day) => NavigationUrl("/transients", day);

    private static string NavigationUrl(string path, ObservingDay day) => FormattableString.Invariant(
        $"{path}?from={DateTimeOffset.FromUnixTimeMilliseconds(day.StartUnixMillisecondsInclusive).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}&to={DateTimeOffset.FromUnixTimeMilliseconds(day.EndUnixMillisecondsExclusive - 1).UtcDateTime:yyyy-MM-ddTHH:mm:ss.fff}");

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
            // Only a sunrise calendar shares the products' report dates; any other view neither shows nor reads them.
            var nightlyRead = SelectedCalendar.CalendarVersion != SunriseReportingPeriod.CurrentVersion
                ? null
                : NightlyProducts.SummarizeAsync(GridStart, GridEnd, cancellation.Token).AsTask();
            var result = await OperatorService.GetArchiveCalendarAsync(
                new CameraAgentGalleryCalendarQuery(GridStart, GridEnd, CalendarVersion: CalendarVersion), cancellation.Token);
            var nightly = nightlyRead is null ? null : await nightlyRead;
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            // A summary belongs to a cell only when it was recorded under the very sunrise period the cell resolves to,
            // resolved as the generator resolves it, so a date whose captures have expired still finds its products.
            var summaries = nightly is { IsSuccess: true, Value: { } values } ? values : [];
            var periods = summaries.Select(static summary => summary.ObservingDate).Distinct()
                .ToDictionary(static date => date, PeriodIdentity);
            bool Matches(NightlyProductDateSummary summary) => string.Equals(
                periods[summary.ObservingDate], summary.ReportingPeriodSha256, StringComparison.Ordinal);
            _nightly = summaries.Where(Matches).ToDictionary(static summary => (summary.ObservingDate, summary.Kind));
            _otherPeriods = [.. summaries.Where(summary => !Matches(summary))
                .Select(static summary => (summary.ObservingDate, summary.Kind))];
            _nightlyUnavailable = nightly is { IsSuccess: false };
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
