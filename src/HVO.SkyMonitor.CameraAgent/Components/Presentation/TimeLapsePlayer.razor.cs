using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace HVO.SkyMonitor.CameraAgent.Components.Presentation;

/// <summary>
/// Plays a time-lapse in whatever container it was produced in (#1138). A video is offered only once the browser says it
/// can decode the declared type, and its source is attached only after the circuit is interactive so a decoding failure
/// is never missed. An animated image waits for an explicit request, so nothing moves without the operator asking.
/// A failure is handled on the element that reports it: a source that cannot be fetched or chosen errors on
/// <c>&lt;source&gt;</c>, a file that cannot be decoded errors on <c>&lt;video&gt;</c>, and an animation that cannot be
/// loaded errors on <c>&lt;img&gt;</c>. Each replaces the player with an explicit failure and keeps the download.
/// </summary>
public sealed partial class TimeLapsePlayer : ComponentBase, IDisposable
{
    internal enum PlayerState { Checking, Video, AnimatedImage, Unsupported, Failed }

    private PlayerState _state = PlayerState.Checking;
    private string? _presented;
    private bool _revealed;
    private bool _disposed;

    [Inject] internal IJSRuntime JSRuntime { get; set; } = default!;
    [Parameter, EditorRequired] public string Source { get; set; } = default!;
    [Parameter, EditorRequired] public string MediaType { get; set; } = default!;
    [Parameter, EditorRequired] public int Width { get; set; }
    [Parameter, EditorRequired] public int Height { get; set; }
    [Parameter, EditorRequired] public string Label { get; set; } = default!;
    [Parameter, EditorRequired] public string Caption { get; set; } = default!;

    // The declared dimensions reserve the frame before any byte arrives, so the page does not shift as metadata loads.
    private string FrameStyle => string.Create(CultureInfo.InvariantCulture,
        $"--time-lapse-aspect: {Width} / {Height}; --time-lapse-width-per-height: {(double)Width / Height:0.####}");

    private string StateName => _state switch
    {
        PlayerState.Video => "video",
        PlayerState.AnimatedImage => "animated-image",
        PlayerState.Unsupported => "unsupported",
        PlayerState.Failed => "failed",
        _ => "checking"
    };

    internal static bool IsAnimatedImage(string mediaType) => mediaType is "image/gif" or "image/webp";

    protected override void OnParametersSet()
    {
        var presented = Source + "\n" + MediaType;
        if (presented == _presented) return;
        _presented = presented;
        _revealed = false;
        _state = IsAnimatedImage(MediaType) ? PlayerState.AnimatedImage : PlayerState.Checking;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_state != PlayerState.Checking || _disposed) return;
        var presented = _presented;
        bool playable;
        try
        {
            await using var module = await JSRuntime.InvokeAsync<IJSObjectReference>(
                "import", "./Components/Presentation/TimeLapsePlayer.razor.js");
            playable = await module.InvokeAsync<bool>("canPlay", MediaType);
        }
        catch (JSDisconnectedException)
        {
            return;
        }
        catch (Exception exception) when (exception is JSException or TaskCanceledException)
        {
            // Without the probe the browser still decides; a failed source reports itself through the error handler.
            playable = true;
        }
        if (_disposed || presented != _presented || _state != PlayerState.Checking) return;
        _state = playable ? PlayerState.Video : PlayerState.Unsupported;
        StateHasChanged();
    }

    private void PlaybackFailed()
    {
        if (_state is PlayerState.Video or PlayerState.AnimatedImage) _state = PlayerState.Failed;
    }

    private void Reveal() => _revealed = true;

    public void Dispose()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
