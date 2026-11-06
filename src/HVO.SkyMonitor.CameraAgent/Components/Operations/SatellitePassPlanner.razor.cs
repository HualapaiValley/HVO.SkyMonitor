using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>A bounded, explicitly requested planning session over authenticated immutable facade products.</summary>
[SuppressMessage("Usage", "CA2213:Disposable fields should be disposed",
    Justification = "Each request owns and disposes its cancellation source in a local using scope; fields only permit cancellation of that request.")]
public sealed partial class SatellitePassPlanner : ComponentBase, IAsyncDisposable
{
    private const int PageSize = 8;
    private const int MaximumPasses = 128;
    private string _filter = "25544";
    private string _hours = "6";
    private string _minimumElevation = "10";
    private string? _contextIdentity;
    private bool _hasContext;
    private bool _disposed;
    private long _searchGeneration;
    private long _arcGeneration;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _arcCancellation;
    private bool _searching;
    private bool _loadingArc;
    private string? _message;
    private string? _arcMessage;
    private CameraAgentSatellitePassSearchView? _search;
    private CameraAgentSatellitePassRow? _selected;
    private CameraAgentSatellitePassArcView? _arc;
    private int _page;
    private bool _showPath = true;
    private bool _showLabels = true;
    private bool _showRigPreview;

    [Inject] internal IServiceProvider Services { get; set; } = default!;

    [Parameter] public CameraAgentSkyMapObserver? Observer { get; set; }
    [Parameter] public CameraAgentSkyMapGeometry? Rig { get; set; }
    [Parameter] public DateTimeOffset? AtUtc { get; set; }
    [Parameter] public bool ContextLoading { get; set; }

    private ICameraAgentSatellitePlanningUiService? Service => Services.GetService<ICameraAgentSatellitePlanningUiService>();
    private CameraAgentSiteTime Clock => new(ObservingDayCalendar.Create(Observer?.TimeZoneId));
    private bool CanSearch => !ContextLoading && Observer is not null && Rig is not null && AtUtc is not null && Service is not null;
    private int PassCount => Math.Min(_search?.Passes.Count ?? 0, MaximumPasses);
    private int Pages => (PassCount + PageSize - 1) / PageSize;
    private IEnumerable<CameraAgentSatellitePassRow> Rows => _search?.Passes.Take(MaximumPasses).Skip(_page * PageSize).Take(PageSize) ?? [];

    protected override async Task OnParametersSetAsync()
    {
        var identity = string.Create(CultureInfo.InvariantCulture,
            $"{Observer?.CanonicalSha256}|{Observer?.Version}|{Observer?.TimeZoneId}|{Rig?.RigProfileHashSha256}|{Rig?.ProjectionCalibrationVersion}|{Rig?.ProjectionAlgorithmVersion}|{AtUtc:O}|{ContextLoading}");
        var changed = _hasContext && !string.Equals(identity, _contextIdentity, StringComparison.Ordinal);
        _contextIdentity = identity;
        _hasContext = true;
        if (changed)
        {
            await InvalidateAsync("Site, rig or instant changed. Search again for this context.").ConfigureAwait(true);
        }
    }

    private async Task InvalidateAsync(string? message)
    {
        _searchGeneration++;
        _arcGeneration++;
        var searchCancellation = _searchCancellation;
        var arcCancellation = _arcCancellation;
        _searchCancellation = null;
        _arcCancellation = null;
        _searching = false;
        _loadingArc = false;
        _search = null;
        _selected = null;
        _arc = null;
        _page = 0;
        _message = message;
        _arcMessage = null;
        await CancelAsync(searchCancellation).ConfigureAwait(true);
        await CancelAsync(arcCancellation).ConfigureAwait(true);
    }

    private static async Task CancelAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is null) return;
        try { await cancellation.CancelAsync().ConfigureAwait(true); }
        // A request can finish and dispose its own source while another cancellation callback is awaited.
        catch (ObjectDisposedException) { }
    }

    private async Task ChangeFilter(ChangeEventArgs args)
    {
        _filter = args.Value?.ToString() ?? string.Empty;
        await InvalidateAsync("Search inputs changed. Search again to use them.").ConfigureAwait(true);
    }

    private async Task ChangeHours(ChangeEventArgs args)
    {
        _hours = args.Value?.ToString() ?? string.Empty;
        await InvalidateAsync("Search inputs changed. Search again to use them.").ConfigureAwait(true);
    }

    private async Task ChangeMinimumElevation(ChangeEventArgs args)
    {
        _minimumElevation = args.Value?.ToString() ?? string.Empty;
        await InvalidateAsync("Search inputs changed. Search again to use them.").ConfigureAwait(true);
    }

    private async Task SearchAsync()
    {
        if (_disposed) return;
        var generation = _searchGeneration + 1;
        await InvalidateAsync(null).ConfigureAwait(true);
        if (_disposed || generation != _searchGeneration) return;
        if (!CanSearch || Service is not { } service || Observer is null || Rig is null || AtUtc is not { } start)
        {
            _message = "Satellite planning needs an available site, rig, instant and authenticated read service.";
            return;
        }
        if (_filter.Trim().Length is 0 or > 80 || !int.TryParse(_hours, CultureInfo.InvariantCulture, out var hours) ||
            hours is < 1 or > 24 || !double.TryParse(_minimumElevation, CultureInfo.InvariantCulture, out var minimum) ||
            !double.IsFinite(minimum) || minimum is < 0 or >= 90)
        {
            _message = "Enter a name or NORAD number (1–80 characters), 1–24 whole hours and a minimum elevation from 0° to below 90°.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        _searching = true;
        try
        {
            var result = await service.SearchAsync(new(start.ToUniversalTime(), start.AddHours(hours).ToUniversalTime(),
                _filter.Trim(), minimum, Observer.CanonicalSha256, Rig.RigProfileHashSha256), cancellation.Token).ConfigureAwait(true);
            if (_disposed || generation != _searchGeneration) return;
            if (!result.IsSuccess || result.Value is not { } response)
            {
                _message = result.Message ?? "Satellite planning is unavailable.";
                return;
            }
            _message = response.Message;
            if (response.Outcome == CameraAgentSatellitePlanningOutcome.Ready && response.Value is { } view)
            {
                if (!MatchesContext(view.Context) || view.Prediction.RequestedInterval.StartUtc != start.ToUniversalTime() ||
                    view.Prediction.RequestedInterval.EndUtc != start.AddHours(hours).ToUniversalTime())
                {
                    _message = "The planning result belongs to another context. Refresh the site and search again.";
                    return;
                }
                _search = view;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
            if (!_disposed && generation == _searchGeneration) _searching = false;
        }
    }

    private async Task SelectAsync(CameraAgentSatellitePassRow row)
    {
        if (_disposed || _search is not { } search || Service is not { } service) return;
        var generation = ++_arcGeneration;
        var previous = _arcCancellation;
        _arcCancellation = null;
        await CancelAsync(previous).ConfigureAwait(true);
        if (_disposed || generation != _arcGeneration || !ReferenceEquals(search, _search)) return;
        _selected = row;
        _arc = null;
        _arcMessage = null;
        _loadingArc = true;
        using var cancellation = new CancellationTokenSource();
        _arcCancellation = cancellation;
        try
        {
            var result = await service.GetArcAsync(search.SearchIdentitySha256, row.Pass.ProductSha256, cancellation.Token).ConfigureAwait(true);
            if (_disposed || generation != _arcGeneration) return;
            if (!result.IsSuccess || result.Value is not { } response)
            {
                _arcMessage = result.Message ?? "The selected planning arc is unavailable.";
                return;
            }
            if (response.Outcome is CameraAgentSatellitePlanningOutcome.ContextChanged or CameraAgentSatellitePlanningOutcome.NoSnapshot or
                CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable or CameraAgentSatellitePlanningOutcome.Expired)
            {
                await InvalidateAsync(response.Message + " Run a new search before selecting a pass.").ConfigureAwait(true);
                return;
            }
            _arcMessage = response.Message;
            if (response.Outcome == CameraAgentSatellitePlanningOutcome.Ready && response.Value is { } view)
            {
                if (view.SearchIdentitySha256 != search.SearchIdentitySha256 || view.ParentPassSha256 != row.Pass.ProductSha256 ||
                    !MatchesContext(view.Context) || view.Prediction.Products.Any(arc => arc.Pass.ProductSha256 != row.Pass.ProductSha256))
                {
                    _arcMessage = "The returned arc does not match this original search and pass. Search again.";
                    return;
                }
                _arc = view;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_arcCancellation, cancellation)) _arcCancellation = null;
            if (!_disposed && generation == _arcGeneration) _loadingArc = false;
        }
    }

    private bool MatchesContext(CameraAgentSatellitePlanningContext context)
        => context.Observer.CanonicalSha256 == Observer?.CanonicalSha256 &&
           context.Rig.RigProfileHashSha256 == Rig?.RigProfileHashSha256 &&
           context.Rig.ProjectionCalibrationVersion == Rig?.ProjectionCalibrationVersion;

    private void PreviousPage() => _page = Math.Max(0, _page - 1);
    private void NextPage() => _page = Math.Min(Pages - 1, _page + 1);

    private string Timestamp(DateTimeOffset value) => $"{Clock.Format(value)} · {value.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}";
    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
    private static string SignedHours(double value) => value.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture) + " h";
    private static string Boundary(OrbitalPassBoundary boundary, bool start) => boundary switch
    {
        OrbitalPassBoundary.ResolvedCrossing => start ? "Rise" : "Set",
        OrbitalPassBoundary.SearchWindow => start ? "Window start (clipped)" : "Window end (clipped)",
        _ => start ? "Work-limited start" : "Work-limited end"
    };

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await InvalidateAsync(null).ConfigureAwait(true);
    }
}
