using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Layout;

public sealed partial class ResponsiveNavigation : ComponentBase, IAsyncDisposable
{
    [Parameter, EditorRequired] public string Id { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string Label { get; set; } = string.Empty;
    [Parameter] public int Breakpoint { get; set; } = 780;
    [Parameter] public RenderFragment? ToggleContent { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Inject] public IJSRuntime JS { get; set; } = default!;
    [Inject] public NavigationManager NavigationManager { get; set; } = default!;

    private ElementReference _panel;
    private ElementReference _toggle;
    private bool _expanded;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ResponsiveNavigation>? _reference;

    protected override void OnInitialized() => NavigationManager.LocationChanged += LocationChanged;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _reference = DotNetObjectReference.Create(this);
        _module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Layout/ResponsiveNavigation.razor.js");
        await _module.InvokeVoidAsync("initialize", _panel, _toggle, Breakpoint, _reference);
    }

    private async Task OpenAsync()
    {
        if (_module is not null) await _module.InvokeVoidAsync("open", _panel);
    }

    private async Task CloseAsync()
    {
        if (_module is not null) await _module.InvokeVoidAsync("close", _panel);
    }

    [JSInvokable]
    public Task SetExpanded(bool expanded) => InvokeAsync(() => { _expanded = expanded; StateHasChanged(); });

    private async void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        try { await CloseAsync(); }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        NavigationManager.LocationChanged -= LocationChanged;
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose", _panel);
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException) { }
            catch (TaskCanceledException) { }
        }
        _reference?.Dispose();
    }
}
