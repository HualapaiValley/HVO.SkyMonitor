using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class LargeImageViewer : ComponentBase, IAsyncDisposable
{
    private ElementReference _dialog;
    private ElementReference _closeButton;
    private IJSObjectReference? _module;
    private DotNetObjectReference<LargeImageViewer>? _self;
    private bool _connected;
    private bool _shown;
    private bool _nativeSize;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public Uri? Source { get; set; }
    [Parameter] public string Title { get; set; } = "Large sky image";
    [Parameter] public string Alt { get; set; } = "Sky capture";
    [Parameter] public string? TriggerId { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Open && !_shown)
        {
            _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Presentation/LargeImageViewer.razor.js");
            if (!_connected)
            {
                _self = DotNetObjectReference.Create(this);
                await _module.InvokeVoidAsync("connect", _dialog, _self);
                _connected = true;
            }
            await _module.InvokeVoidAsync("show", _dialog, _closeButton);
            _shown = true;
        }
        else if (!Open && _shown && _module is not null)
        {
            await _module.InvokeVoidAsync("close", _dialog, TriggerId);
            _shown = false;
            _nativeSize = false;
        }
    }

    private async Task CloseAsync()
    {
        if (_module is not null && _shown)
        {
            await _module.InvokeVoidAsync("close", _dialog, TriggerId);
        }
        _shown = false;
        _nativeSize = false;
        await OpenChanged.InvokeAsync(false);
    }

    [JSInvokable]
    public Task CloseFromJavaScriptAsync() => InvokeAsync(CloseAsync);

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_module is not null)
            {
                if (_connected)
                {
                    await _module.InvokeVoidAsync("disconnect", _dialog);
                }
                await _module.DisposeAsync();
            }
        }
        catch (JSDisconnectedException)
        {
        }
        _self?.Dispose();
        GC.SuppressFinalize(this);
    }
}
