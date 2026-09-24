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
    private bool _disposed;
    private IJSObjectReference? _module;
    private DotNetObjectReference<ResponsiveNavigation>? _reference;

    protected override void OnInitialized() => NavigationManager.LocationChanged += LocationChanged;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _disposed) return;
        IJSObjectReference? module = null;
        DotNetObjectReference<ResponsiveNavigation>? reference = null;
        try
        {
            module = await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Layout/ResponsiveNavigation.razor.js");
            if (_disposed) return;
            reference = DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("initialize", _panel, _toggle, Breakpoint, reference);
            if (_disposed) return;

            // Initialization owns these resources until both awaits complete. Disposal cannot
            // invalidate the callback reference or lose a module that arrives after teardown.
            _module = module;
            _reference = reference;
            module = null;
            reference = null;
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        finally
        {
            if (module is not null) await ReleaseAsync(module, reference);
        }
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
    public Task SetExpanded(bool expanded) => InvokeAsync(() =>
    {
        if (_disposed) return;
        _expanded = expanded;
        StateHasChanged();
    });

    private async void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        try
        {
            if (!_disposed && _module is not null) await _module.InvokeVoidAsync("close", _panel, false);
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        NavigationManager.LocationChanged -= LocationChanged;
        var module = _module;
        var reference = _reference;
        _module = null;
        _reference = null;
        if (module is not null) await ReleaseAsync(module, reference);
    }

    private async Task ReleaseAsync(IJSObjectReference module, DotNetObjectReference<ResponsiveNavigation>? reference)
    {
        try
        {
            try
            {
                if (reference is not null) await module.InvokeVoidAsync("dispose", _panel);
            }
            finally
            {
                await module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException) { }
        catch (TaskCanceledException) { }
        finally { reference?.Dispose(); }
    }
}
