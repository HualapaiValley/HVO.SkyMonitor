using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class ArchiveCalendarPage : ComponentBase, IAsyncDisposable
{
    internal const int RangeDays = 31;
    private CancellationTokenSource? _loadCancellation;
    private CameraAgentGalleryCalendar? _calendar;
    private string? _errorMessage;
    private bool _isLoading = true;
    private long _generation;

    [Inject] internal ICameraAgentOperatorUiService OperatorService { get; set; } = default!;
    [Inject] internal NavigationManager NavigationManager { get; set; } = default!;
    [Inject] internal TimeProvider TimeProvider { get; set; } = default!;
    [Parameter, SupplyParameterFromQuery(Name = "to")] public string? To { get; set; }

    private DateOnly ToDate => DateOnly.TryParseExact(To, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
        ? parsed
        : DateOnly.FromDateTime(TimeProvider.GetUtcNow().UtcDateTime);

    private DateOnly FromDate => ToDate.AddDays(-(RangeDays - 1));

    private string RangeLabel => FormattableString.Invariant($"{FromDate:yyyy-MM-dd} to {ToDate:yyyy-MM-dd}");

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
                new CameraAgentGalleryCalendarQuery(FromDate, ToDate), cancellation.Token);
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

    private string RangeUrl(int direction) => NavigationManager.GetUriWithQueryParameter(
        "to", ToDate.AddDays(direction * RangeDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    // Day links hand the night's UTC boundaries to the filter-backed pages so
    // the same evidence is selected there; the archive's inclusive upper bound
    // excludes the next night's first millisecond.
    internal static string CapturesUrl(ObservingDay day) => NavigationUrl("/gallery", day);

    internal static string CandidatesUrl(ObservingDay day) => NavigationUrl("/transients", day);

    private static string NavigationUrl(string path, ObservingDay day) => FormattableString.Invariant(
        $"{path}?from={day.StartUtc.UtcDateTime:yyyy-MM-ddTHH:mm:ss}&to={day.EndUtc.AddMilliseconds(-1).UtcDateTime:yyyy-MM-ddTHH:mm:ss}");

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
