using System.Globalization;
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
    private static readonly HashSet<string> DimensionKeys = new(StringComparer.Ordinal) { "widthPixels", "heightPixels" };
    private FrameFactsSnapshotV1 _snapshot = FrameFactsEditorPreview.MissingSnapshot();
    private bool _loaded;
    private bool _loadingFacts;
    private long _factsLoadGeneration;
    private string? _image;
    private string? _error;
    private PresentationLayerPayloadV1? _payload;

    [Parameter, EditorRequired] public string IdPrefix { get; set; } = string.Empty;
    [Parameter, EditorRequired] public ProcessingNodeOptionsForm Form { get; set; } = default!;
    [Parameter] public EventCallback Changed { get; set; }
    [Parameter] public bool ShowResetToDefaults { get; set; }

    private bool CanResetToDefaults => Form.HasNonDefaultValuesExcluding(DimensionKeys);

    private async Task ResetAllToDefaultsAsync()
    {
        Form.ResetAllToDefaultsExcluding(DimensionKeys);
        ClearPreview();
        await Changed.InvokeAsync().ConfigureAwait(false);
    }

    private async Task ChangeAsync(string key, string value)
    {
        Form.Set(key, value);
        ClearPreview();
        await Changed.InvokeAsync().ConfigureAwait(false);
    }

    private void ClearPreview() { _image = null; _payload = null; _error = null; }

    private static string? Bounds(ProcessingOptionDescriptor option) => option.Kind switch
    {
        ProcessingOptionKind.WholeNumber or ProcessingOptionKind.NullableWholeNumber => string.Create(CultureInfo.InvariantCulture, $"{option.MinimumWholeNumber}–{option.MaximumWholeNumber} {option.Unit}."),
        ProcessingOptionKind.RealNumber or ProcessingOptionKind.NullableRealNumber => string.Create(CultureInfo.InvariantCulture, $"{option.MinimumRealNumber}–{option.MaximumRealNumber} {option.Unit}."),
        ProcessingOptionKind.Color => "Use #RRGGBB or the colour picker.",
        ProcessingOptionKind.Text when option.MaximumLength.HasValue => $"Maximum {option.MaximumLength} characters.",
        _ => null
    };

    private async Task LoadFactsAsync(InputFileChangeEventArgs args)
    {
        var generation = ++_factsLoadGeneration;
        ClearPreview();
        _loadingFacts = true;
        // A rejected replacement must not leave an older snapshot looking like the newly selected file.
        _snapshot = FrameFactsEditorPreview.MissingSnapshot();
        _loaded = false;
        try
        {
            await using var input = args.File.OpenReadStream(FrameFactsEditorPreview.MaximumInputBytes);
            using var buffer = new MemoryStream();
            // Publish the snapshot and loading state together on the renderer's serialized context.
            await input.CopyToAsync(buffer).ConfigureAwait(true);
            var snapshot = FrameFactsEditorPreview.ReadSnapshot(buffer.ToArray());
            if (generation != _factsLoadGeneration) return;
            _snapshot = snapshot;
            _loaded = true;
        }
        catch (Exception exception) when (exception is ArgumentException or System.Text.Json.JsonException or InvalidDataException or IOException)
        {
            if (generation == _factsLoadGeneration) _error = "Retained facts rejected: " + exception.Message;
        }
        finally
        {
            if (generation == _factsLoadGeneration)
            {
                _loadingFacts = false;
                _image = null;
                _payload = null;
            }
        }
    }

    private void Preview()
    {
        if (_loadingFacts) return;
        ClearPreview();
        try { (_image, _payload) = FrameFactsEditorPreview.Render(Form, _snapshot); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or InvalidOperationException)
        {
            _error = exception.Message;
        }
    }
}
