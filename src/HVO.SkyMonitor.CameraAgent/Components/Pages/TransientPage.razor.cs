using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Security;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class TransientPage : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentTransientOperatorPage? _page;
    private string? _errorMessage;
    private bool _isLoading;
    private long _generation;
    private Dictionary<Guid, CameraAgentEventEvidenceView> _evidence = [];
    private ObservingDayCalendar _calendar = ObservingDayCalendar.Create(null);

    [Inject] internal ICameraAgentEventEvidenceUiService EventEvidence { get; set; } = default!;
    [Inject] internal IObservingDayCalendarProvider ObservingDays { get; set; } = default!;
    [Parameter, SupplyParameterFromQuery(Name = "classification")] public string? Classification { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "date")] public string? Date { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "month")] public string? Month { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "view")] public string? View { get; set; }

    private bool CalendarView => string.Equals(View, "calendar", StringComparison.Ordinal);
    private string ClassificationFilter => Classification ?? "all";
    private CameraAgentTransientOperatorCandidate? Latest => _page is { Items.Count: > 0 } ? _page.Items[0] : null;
    private string TimeZoneLabel => _calendar.TimeZoneFallback ? "UTC (site time zone unavailable)" : _calendar.TimeZoneId;
    private CameraAgentEventEvidenceView? Evidence(Guid id) => _evidence.GetValueOrDefault(id);
    private DateTimeOffset RecordedUtc(CameraAgentTransientOperatorCandidate candidate)
        => Evidence(candidate.CandidateId)?.RecordedUtc ?? candidate.CreatedUtc;
    private DateOnly ObservingDate(CameraAgentTransientOperatorCandidate candidate)
        => _calendar.Resolve(RecordedUtc(candidate)).Date;
    private string LocalTime(DateTimeOffset value)
        => TimeZoneInfo.ConvertTime(value, _calendar.TimeZone).ToString("d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    private static string UtcTime(DateTimeOffset value) => value.UtcDateTime.ToString("d MMM yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    private IReadOnlyList<CameraAgentTransientOperatorCandidate> VisibleItems => _page?.Items.Where(candidate =>
        (ClassificationFilter == "all" || string.Equals(ClassificationFilter,
            CameraAgentEventFacts.ClassificationKey(Evidence(candidate.CandidateId)?.Detail.AssessmentEvidence), StringComparison.Ordinal)) &&
        (!DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) || ObservingDate(candidate) == date)).ToArray() ?? [];
    private DateOnly CalendarMonth
    {
        get
        {
            if (DateOnly.TryParseExact(Month + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
            {
                return month;
            }
            var date = DateOnly.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var selected)
                ? selected : Latest is { } first ? ObservingDate(first) : _calendar.Resolve(DateTimeOffset.UtcNow).Date;
            return new DateOnly(date.Year, date.Month, 1);
        }
    }
    private IEnumerable<DateOnly> CalendarCells
    {
        get
        {
            var month = CalendarMonth;
            var start = month.AddDays(-(int)month.DayOfWeek);
            var count = ((int)month.DayOfWeek + DateTime.DaysInMonth(month.Year, month.Month) + 6) / 7 * 7;
            return Enumerable.Range(0, count).Select(start.AddDays);
        }
    }
    private void SetClassification(ChangeEventArgs args) => NavigateFilter("classification", args.Value?.ToString());
    private void SetDate(ChangeEventArgs args) => NavigateFilter("date", args.Value?.ToString());
    private void SetView(bool calendar) => NavigateFilter("view", calendar ? "calendar" : null);
    private void ChangeMonth(int delta) => NavigateFilter("month", CalendarMonth.AddMonths(delta).ToString("yyyy-MM", CultureInfo.InvariantCulture));
    private void NavigateFilter(string key, string? value) => NavigationManager.NavigateTo(
        NavigationManager.GetUriWithQueryParameter(key, string.IsNullOrWhiteSpace(value) ? null : value));

    [Inject] internal ICameraAgentTransientUiService TransientService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Parameter, SupplyParameterFromQuery(Name = "cursor")] public string? Cursor { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "pageSize")] public int? PageSize { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "from")] public string? From { get; set; }
    [Parameter, SupplyParameterFromQuery(Name = "to")] public string? To { get; set; }

    private bool HasRange => TryDate(From, out _) || TryDate(To, out _);

    private string RangeLabel => $"{(TryDate(From, out var from) && from is { } f ? FormatTime(f) : "the beginning")} and {(TryDate(To, out var to) && to is { } t ? FormatTime(t) : "now")}";

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
        _page = null;
        _evidence = [];
        _calendar = ObservingDays.Current;
        try
        {
            if (ClassificationFilter is not ("all" or "fireball" or "meteor" or "satellite" or "aircraft" or "sensor-artifact" or "environmental-artifact" or "unresolved") ||
                !ValidFilterDate(Date, "yyyy-MM-dd") ||
                !ValidFilterDate(string.IsNullOrWhiteSpace(Month) ? null : Month + "-01", "yyyy-MM-dd"))
            {
                _page = null;
                _errorMessage = "The event filter is invalid.";
                return;
            }
            if (!TryDate(From, out var fromUtc) || !TryDate(To, out var toUtc))
            {
                _page = null;
                _errorMessage = "The candidate time range is not a valid UTC date or time.";
                return;
            }
            var result = await TransientService.GetPageAsync(
                new CameraAgentTransientOperatorQuery(PageSize, Cursor, fromUtc, toUtc), cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                _page = null;
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                var evidence = new Dictionary<Guid, CameraAgentEventEvidenceView>();
                foreach (var candidate in result.Value.Items)
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    var candidateResult = await EventEvidence.GetAsync(candidate.CandidateId, includeContext: false, cancellation.Token);
                    if (generation != Volatile.Read(ref _generation))
                    {
                        return;
                    }
                    if (candidateResult.Kind == OperatorUiResultKind.Unauthorized)
                    {
                        _page = null;
                        NavigationManager.NavigateTo("/Account/AccessDenied");
                        return;
                    }
                    if (candidateResult.IsSuccess && candidateResult.Value is { } view && view.Detail.Candidate.CandidateId == candidate.CandidateId)
                    {
                        evidence[candidate.CandidateId] = view;
                    }
                }
                if (generation == Volatile.Read(ref _generation))
                {
                    _evidence = evidence;
                    _page = result.Value;
                }
            }
            else
            {
                _page = null;
                _errorMessage = result.Message ?? "Transient evidence is unavailable.";
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

    private void ShowNewest() => NavigateToCursor(null);
    private void ShowOlder() => NavigateToCursor(_page?.NextCursor);
    private void NavigateToCursor(string? cursor) => NavigationManager.NavigateTo(
        NavigationManager.GetUriWithQueryParameter("cursor", cursor));

    private string DetailUrl(Guid candidateId)
    {
        var relative = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);
        var returnUrl = ReturnUrlHelper.NormalizeReturnUrl($"/{relative}");
        if (!string.Equals(returnUrl, "/transients", StringComparison.Ordinal) &&
            !returnUrl.StartsWith("/transients?", StringComparison.Ordinal))
        {
            returnUrl = "/transients";
        }
        return QueryHelpers.AddQueryString(
            FormattableString.Invariant($"/transients/{candidateId:D}"), "returnUrl", returnUrl);
    }

    private static bool ValidFilterDate(string? value, string format) => string.IsNullOrWhiteSpace(value) ||
        DateOnly.TryParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year is >= 2 and <= 9998;

    private static bool TryDate(string? value, out DateTimeOffset? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result))
        {
            return false;
        }
        parsed = result;
        return true;
    }

    internal static string FormatTime(DateTimeOffset value)
        => value.UtcDateTime.ToString("MMM d, yyyy HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    internal static string SplitWords(string value)
        => OperationsPage.SplitWords(value.Replace('_', ' '));

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
