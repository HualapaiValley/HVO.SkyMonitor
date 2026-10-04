using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Components.Shared;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

/// <summary>
/// The generated products library: one card per recorded daily final of a whole sunrise-to-sunrise period, newest
/// report date first. Every filter and page position is URL state, so links, back and reload keep them. Hourly products
/// and periods still in progress belong to the observing day page, and nothing is shown for product types or states
/// CameraAgent does not record.
/// </summary>
public sealed partial class GeneratedProductsView : ComponentBase, IAsyncDisposable
{
    /// <summary>The report dates one library page presents.</summary>
    internal const int PageDates = 12;

    internal const string LibraryPath = "/archive/products";

    internal sealed record FilterOption(string Value, string Label, bool Recorded);

    // The prototype's choices, with the ones CameraAgent cannot answer truthfully kept visible but disabled.
    internal static readonly FilterOption[] TypeOptions =
    [
        new("time-lapse", "Timelapses", false),
        new(NightlyProductContract.StarTrailTarget, "Star trails", true),
        new(NightlyProductContract.KeogramTarget, "Keograms", true),
        new("daily-summary", "Daily summaries", false)
    ];

    internal static readonly FilterOption[] StatusOptions =
    [
        new("produced", "Produced", true),
        new("partial", "Partial", true),
        new("not-produced", "Not produced", true),
        new("running", "Running", false)
    ];

    private readonly HashSet<Guid> _failedPreviews = [];
    private Dictionary<DateOnly, string?> _calendarPeriods = [];
    private CancellationTokenSource? _loadCancellation;
    private NightlyProductLibraryQuery? _query;
    private NightlyProductLibraryPage? _page;
    private string? _notice;
    private string? _errorMessage;
    private PageStateNotice.PageStateKind _errorKind = PageStateNotice.PageStateKind.Error;
    private bool _isLoading = true;
    private long _generation;
    private string _draftType = string.Empty;
    private string _draftStatus = string.Empty;
    private string _draftDay = string.Empty;

    [Inject] internal ICameraAgentNightlyProductUiService NightlyProducts { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal IObservingDayCalendarProvider ObservingDays { get; set; } = default!;

    [Parameter] public string? Type { get; set; }
    [Parameter] public string? Status { get; set; }
    [Parameter] public string? Day { get; set; }
    [Parameter] public string? Before { get; set; }

    /// <summary>
    /// The library query of the URL filters, or a notice explaining why nothing can be listed for them: an unrecorded
    /// product type or state is explained rather than answered with an empty result, and a malformed value is named.
    /// </summary>
    internal static (NightlyProductLibraryQuery? Query, string? Notice) Resolve(string? type, string? status, string? day, string? before)
    {
        NightlyProductKind? kind = null;
        if (!string.IsNullOrEmpty(type))
        {
            if (!NightlyProductContract.TryParseTarget(type, out var parsedKind))
            {
                return (null, Array.Find(TypeOptions, option => string.Equals(option.Value, type, StringComparison.Ordinal)) is { } known
                    ? $"{known.Label} are not yet generated, so none are listed and nothing is substituted for them."
                    : "The product type filter is not a product type CameraAgent generates.");
            }
            kind = parsedKind;
        }
        NightlyProductLibraryState? state = null;
        if (!string.IsNullOrEmpty(status))
        {
            state = status switch
            {
                "produced" => NightlyProductLibraryState.Produced,
                "partial" => NightlyProductLibraryState.Partial,
                "not-produced" => NightlyProductLibraryState.NotProduced,
                _ => null
            };
            if (state is null)
            {
                return (null, string.Equals(status, "running", StringComparison.Ordinal)
                    ? "Generation progress is not recorded, so no product is shown as running. Periods still in progress are on each observing day's page."
                    : "The status filter is not a recorded product state.");
            }
        }
        DateOnly? observingDate = null;
        if (!string.IsNullOrEmpty(day))
        {
            if (!TryParseDate(day, out var parsedDay))
            {
                return (null, "The observing day filter is not a date.");
            }
            observingDate = parsedDay;
        }
        // A single observing day is never paged, so its page position is ignored.
        DateOnly? beforeDate = null;
        if (observingDate is null && !string.IsNullOrEmpty(before))
        {
            if (!TryParseDate(before, out var parsedBefore))
            {
                return (null, "The page position is not a date.");
            }
            beforeDate = parsedBefore;
        }
        return (new NightlyProductLibraryQuery(PageDates, kind, state, observingDate, beforeDate), null);
    }

    private static bool TryParseDate(string value, out DateOnly date)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    protected override Task OnParametersSetAsync()
    {
        _draftType = Type ?? string.Empty;
        _draftStatus = Status ?? string.Empty;
        _draftDay = Day ?? string.Empty;
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
        _page = null;
        _errorMessage = null;
        _errorKind = PageStateNotice.PageStateKind.Error;
        _failedPreviews.Clear();
        (_query, _notice) = Resolve(Type, Status, Day, Before);
        if (_query is null)
        {
            _isLoading = false;
            return;
        }
        _isLoading = true;
        try
        {
            var result = await NightlyProducts.ListLibraryAsync(_query, cancellation.Token);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }
            if (result.Kind == OperatorUiResultKind.Unauthorized)
            {
                NavigationManager.NavigateTo("/Account/AccessDenied");
            }
            else if (result.IsSuccess && result.Value is not null)
            {
                _calendarPeriods = result.Value.Entries.Select(static entry => entry.Summary.ObservingDate).Distinct()
                    .ToDictionary(static date => date, CalendarPeriodIdentity);
                _page = result.Value;
            }
            else
            {
                _errorKind = result.Kind == OperatorUiResultKind.Invalid ? PageStateNotice.PageStateKind.Info : PageStateNotice.PageStateKind.Error;
                _errorMessage = result.Message ?? "Generated products are unavailable.";
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

    private bool IsFiltered => _query is { Kind: not null } or { State: not null } or { ObservingDate: not null };

    /// <summary>The library URL of a filter set; changing a filter always returns to the newest page.</summary>
    internal string FilterUrl(string? type, string? status, string? day, DateOnly? before = null)
        => NavigationManager.GetUriWithQueryParameters(LibraryPath, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = EmptyToNull(type),
            ["status"] = EmptyToNull(status),
            ["day"] = EmptyToNull(day),
            ["before"] = before?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });

    private string NewestUrl => FilterUrl(Type, Status, null);

    private string OlderUrl(DateOnly before) => FilterUrl(Type, Status, null, before);

    private void ApplyType() => NavigationManager.NavigateTo(FilterUrl(_draftType, Status, Day));

    private void ApplyStatus() => NavigationManager.NavigateTo(FilterUrl(Type, _draftStatus, Day));

    // A date input reports an empty value while its date is incomplete; only a whole date is applied.
    private void ApplyDay(ChangeEventArgs args)
    {
        _draftDay = args.Value as string ?? string.Empty;
        NavigationManager.NavigateTo(FilterUrl(Type, Status, TryParseDate(_draftDay, out _) ? _draftDay : null));
    }

    private void ShowAllDays() => NavigationManager.NavigateTo(FilterUrl(Type, Status, null));

    private void MarkPreviewFailed(Guid productId) => _failedPreviews.Add(productId);

    // Each report date reads as the observing day page and calendar read it: star trail, then keogram. The sort is
    // stable, so the store's period order within one date and kind is kept.
    private static IEnumerable<NightlyProductLibraryEntry> Ordered(NightlyProductLibraryPage page) => page.Entries
        .OrderByDescending(static entry => entry.Summary.ObservingDate)
        .ThenBy(static entry => entry.Summary.Kind == NightlyProductKind.StarTrail ? 0 : 1);

    internal static ArchiveCalendarPage.NightlyBadge Badge(NightlyProductLibraryEntry entry)
        => ArchiveCalendarPage.Badge(entry.Summary.Kind, entry.Summary, entry.OtherPeriodRecorded, unavailable: false, pending: false);

    // The sunrise period the calendar resolves a report date to, resolved as the calendar and generator resolve it.
    private string? CalendarPeriodIdentity(DateOnly date)
    {
        try
        {
            return ObservingDays.Current.Resolve(date).SunrisePeriod?.IdentitySha256;
        }
        catch (ReportingPeriodUnavailableException)
        {
            return null;
        }
    }

    private string? PeriodNote(NightlyProductLibraryEntry entry)
        => PeriodNote(entry, _calendarPeriods.GetValueOrDefault(entry.Summary.ObservingDate));

    /// <summary>
    /// A card's source period line. A period other than the one the calendar resolves the date to is always named and
    /// explained, because the calendar and its badges for that date describe another period; the period the calendar
    /// shows is named only when another period of the date is also recorded.
    /// </summary>
    internal static string? PeriodNote(NightlyProductLibraryEntry entry, string? calendarPeriodIdentity)
    {
        var period = entry.Daily.ReportingPeriod;
        return !string.Equals(period.IdentitySha256, calendarPeriodIdentity, StringComparison.Ordinal)
            ? $"Recorded under another source period than the calendar shows for this date (another site or time-zone rules). Source period: {ObservingDayPage.OtherPeriodLabel(period)}"
            : entry.OtherPeriodRecorded ? $"Source period: {ObservingDayPage.OtherPeriodLabel(period)}" : null;
    }

    internal static string NightDate(DateOnly date) => date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    internal static string NightTitle(DateOnly date) => "Night of " + NightDate(date);

    private static string CardLabel(NightlyProductLibraryEntry entry)
        => $"{NightlyProductLinks.KindLabel(entry.Summary.Kind)}, night of {NightDate(entry.Summary.ObservingDate)}";

    private static string TimeZoneOf(NightlyProductLibraryEntry entry) => entry.Daily.ReportingPeriod.Site.TimeZoneId;

    /// <summary>
    /// What a card without an image shows in its place. The recorded reason is the card's description; no image is
    /// ever substituted.
    /// </summary>
    internal static string PlaceholderText(NightlyProductLibraryEntry entry)
        => entry.State == NightlyProductLibraryState.Partial
            ? $"No nightly {NightlyProductLinks.KindNoun(entry.Summary.Kind)}"
            : "Not produced";

    // A final's admitted count is the segment products it composed; frame counts live in the product's lineage.
    internal static string Sources(NightlyProductLibraryEntry entry)
    {
        var admitted = entry.Daily.Status.AdmittedCount;
        return FormattableString.Invariant($"{admitted:N0} segment product{(admitted == 1 ? "" : "s")}");
    }

    internal static (string Label, string Value) Span(NightlyProductLibraryEntry entry) => entry.Product is { } product
        ? ("Frames", ObservingDayPage.LocalSpan(product.FirstObservationUtc, product.LastObservationUtc, TimeZoneOf(entry)))
        : ("Period", ObservingDayPage.LocalSpan(entry.Daily.Status.WindowStartUtc, entry.Daily.Status.WindowEndUtc, TimeZoneOf(entry)));

    internal static string? Hourly(NightlyProductLibraryEntry entry)
    {
        var hours = entry.Summary.HourlyProduced + entry.Summary.HourlyWithoutProduct;
        return hours == 0 ? null : FormattableString.Invariant(
            $"Hourly: {entry.Summary.HourlyProduced} of {hours} completed hours produced");
    }

    internal static string Automation(NightlyProductLibraryEntry entry)
    {
        var occurrence = entry.Daily.Status.Occurrence;
        return FormattableString.Invariant($"{occurrence.Definition.Name} · revision {occurrence.DefinitionVersion}");
    }

    private string PageSummary(NightlyProductLibraryPage page)
    {
        var dates = page.Entries.Select(static entry => entry.Summary.ObservingDate).Distinct().Count();
        return FormattableString.Invariant(
            $"{page.Entries.Count} product card{(page.Entries.Count == 1 ? "" : "s")} from {dates} observing day{(dates == 1 ? "" : "s")}, newest first.");
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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
