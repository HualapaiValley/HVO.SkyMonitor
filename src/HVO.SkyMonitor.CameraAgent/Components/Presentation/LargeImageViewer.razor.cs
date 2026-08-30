using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class LargeImageViewer : ComponentBase, IAsyncDisposable
{
    private ElementReference _dialog;
    private ElementReference _closeButton;
    private IJSObjectReference? _module;
    private DotNetObjectReference<LargeImageViewer>? _self;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The managed semaphore remains undisposed so a callback already queued during asynchronous component disposal can observe the disposed flag and exit safely.")]
    private readonly SemaphoreSlim _interopGate = new(1, 1);
    private bool _connected;
    private bool _shown;
    private bool _nativeSize;
    private bool _disposed;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }
    [Parameter] public Uri? Source { get; set; }
    [Parameter] public string Title { get; set; } = "Large sky image";
    [Parameter] public string Alt { get; set; } = "Sky capture";
    [Parameter] public string? TriggerId { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        await _interopGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            if (Open && !_shown)
            {
                _module ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
                    "import", "./Components/Presentation/LargeImageViewer.razor.js");
                if (_disposed || !Open)
                {
                    return;
                }
                if (!_connected)
                {
                    _self = DotNetObjectReference.Create(this);
                    await _module.InvokeVoidAsync("connect", _dialog, _self);
                    _connected = true;
                }
                if (_disposed || !Open)
                {
                    return;
                }
                await _module.InvokeVoidAsync("show", _dialog, _closeButton);
                if (_disposed || !Open)
                {
                    await _module.InvokeVoidAsync("close", _dialog, TriggerId);
                    return;
                }
                _shown = true;
            }
            else if (!Open && _shown && _module is not null)
            {
                await _module.InvokeVoidAsync("close", _dialog, TriggerId);
                _shown = false;
                _nativeSize = false;
            }
        }
        finally
        {
            _interopGate.Release();
        }
    }

    internal async Task CloseAsync()
    {
        await _interopGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }
            if (_module is not null && _shown)
            {
                await _module.InvokeVoidAsync("close", _dialog, TriggerId);
            }
            _shown = false;
            _nativeSize = false;
        }
        finally
        {
            _interopGate.Release();
        }
        await OpenChanged.InvokeAsync(false);
    }

    [JSInvokable]
    public Task CloseFromJavaScriptAsync() => InvokeAsync(CloseAsync);

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _interopGate.WaitAsync();
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
        finally
        {
            _self?.Dispose();
            _interopGate.Release();
            GC.SuppressFinalize(this);
        }
    }
}
