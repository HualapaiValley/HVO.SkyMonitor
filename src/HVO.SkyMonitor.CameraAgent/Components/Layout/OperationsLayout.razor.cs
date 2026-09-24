using HVO.SkyMonitor.CameraAgent.Components.Operations;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

/// <summary>
/// Nested layout for every Operations workspace page: a grouped section sidebar that
/// collapses behind a toggle on narrow viewports, with the page body beside it.
/// </summary>
public sealed partial class OperationsLayout : LayoutComponentBase, IDisposable
{
    private string _currentPath = OperationsSectionCatalog.OverviewPath;

    [Inject]
    public NavigationManager NavigationManager { get; set; } = default!;

    internal OperationsSection? Current => OperationsSectionCatalog.Resolve(_currentPath);

    protected override void OnInitialized()
    {
        UpdatePath(NavigationManager.Uri);
        NavigationManager.LocationChanged += OnLocationChanged;
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdatePath(e.Location);
        _ = InvokeAsync(StateHasChanged);
    }

    private void UpdatePath(string location)
        => _currentPath = new Uri(location).AbsolutePath;

    private static string GroupId(string group)
        => $"operations-group-{group}";

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
