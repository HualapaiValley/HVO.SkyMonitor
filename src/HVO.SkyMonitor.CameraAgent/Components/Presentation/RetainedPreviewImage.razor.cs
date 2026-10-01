using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

public sealed partial class RetainedPreviewImage : ComponentBase, IDisposable
{
    private ElementReference _image;
    private string? _checkedSource;
    private bool _disposed;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Parameter, EditorRequired] public string Source { get; set; } = default!;
    [Parameter, EditorRequired] public string Alt { get; set; } = default!;
    [Parameter] public EventCallback OnFailure { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        var source = Source;
        if (_checkedSource == source || _disposed) return;
        _checkedSource = source;
        try
        {
            await using var module = await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Presentation/RetainedPreviewImage.razor.js");
            var failed = await module.InvokeAsync<bool>("hasFailed", _image);
            // An image may fail during prerender, before Blazor attaches its error handler.
            // Never apply an older source's delayed check to a replacement image.
            if (failed && !_disposed && source == Source) await NotifyFailureAsync();
        }
        catch (JSDisconnectedException) { }
        catch (JSException) { }
        catch (TaskCanceledException) { }
    }

    private Task NotifyFailureAsync() => _disposed ? Task.CompletedTask : OnFailure.InvokeAsync();

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
