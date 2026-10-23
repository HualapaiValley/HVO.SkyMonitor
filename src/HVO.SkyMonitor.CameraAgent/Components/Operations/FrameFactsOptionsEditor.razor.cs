using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class FrameFactsOptionsEditor : ComponentBase
{
    private static readonly (string Key, string Label)[] Corners =
        [("topLeft", "Top left"), ("topRight", "Top right"), ("bottomLeft", "Bottom left"), ("bottomRight", "Bottom right")];
    private static readonly string[] StyleSuffixes = ["Font", "SizeMilliPixels", "Color", "MissingPolicy"];
    private static readonly string[] LayoutKeys = ["widthPixels", "heightPixels", "inset", "lineSpacing"];
    private FrameFactsSnapshotV1 _snapshot = FrameFactsEditorPreview.MissingSnapshot();
    private bool _loaded;
    private string? _image;
    private string? _error;
    private PresentationLayerPayloadV1? _payload;

    [Parameter, EditorRequired] public string IdPrefix { get; set; } = string.Empty;
    [Parameter, EditorRequired] public ProcessingNodeOptionsForm Form { get; set; } = default!;
    [Parameter] public EventCallback Changed { get; set; }

    private async Task ChangeAsync(string key, string value)
    {
        Form.Set(key, value);
        ClearPreview();
        await Changed.InvokeAsync().ConfigureAwait(false);
    }

    private void ClearPreview() { _image = null; _payload = null; _error = null; }

    private async Task LoadFactsAsync(InputFileChangeEventArgs args)
    {
        ClearPreview();
        // A rejected replacement must not leave an older snapshot looking like the newly selected file.
        _snapshot = FrameFactsEditorPreview.MissingSnapshot();
        _loaded = false;
        try
        {
            await using var input = args.File.OpenReadStream(FrameFactsEditorPreview.MaximumInputBytes);
            using var buffer = new MemoryStream();
            await input.CopyToAsync(buffer).ConfigureAwait(false);
            _snapshot = FrameFactsEditorPreview.ReadSnapshot(buffer.ToArray());
            _loaded = true;
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            _error = "Retained facts rejected: " + exception.Message;
        }
    }

    private void Preview()
    {
        ClearPreview();
        try { (_image, _payload) = FrameFactsEditorPreview.Render(Form, _snapshot); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            _error = exception.Message;
        }
    }
}
