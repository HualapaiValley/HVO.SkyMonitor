using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

public sealed partial class ArchiveNavigation : ComponentBase, IDisposable
{
    private static readonly (string Href, string Label)[] Links =
        [("/gallery", "Captures"), ("/archive/calendar", "Calendar"), ("/archive/products", "Products")];
    private string _path = "/";

    [Inject] public NavigationManager NavigationManager { get; set; } = default!;

    private bool IsArchive => Matches("/gallery") || Matches("/archive");
    private string? Current => Matches("/gallery") ? "/gallery"
        : Matches("/archive/calendar") || Matches("/archive/day") ? "/archive/calendar"
        : Matches("/archive/products") ? "/archive/products" : null;

    protected override void OnInitialized()
    {
        _path = new Uri(NavigationManager.Uri).AbsolutePath;
        NavigationManager.LocationChanged += LocationChanged;
    }

    private bool Matches(string route) => string.Equals(_path, route, StringComparison.OrdinalIgnoreCase)
        || _path.StartsWith(route + "/", StringComparison.OrdinalIgnoreCase);

    private void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        _path = new Uri(args.Location).AbsolutePath;
        _ = InvokeAsync(StateHasChanged);
    }

    public void Dispose() => NavigationManager.LocationChanged -= LocationChanged;
}
