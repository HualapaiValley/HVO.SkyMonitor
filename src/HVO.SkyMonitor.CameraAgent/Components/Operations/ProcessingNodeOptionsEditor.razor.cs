using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class ProcessingNodeOptionsEditor : ComponentBase
{
    [Parameter, EditorRequired] public string IdPrefix { get; set; } = string.Empty;
    [Parameter, EditorRequired] public ProcessingNodeOptionsForm Form { get; set; } = default!;
    [Parameter] public EventCallback Changed { get; set; }

    private async Task ChangedAsync(string key, string value)
    {
        Form.Set(key, value);
        await Changed.InvokeAsync().ConfigureAwait(false);
    }

    private static string? Bounds(ProcessingOptionDescriptor option) => option.Kind switch
    {
        ProcessingOptionKind.WholeNumber => string.Create(CultureInfo.InvariantCulture, $"{option.MinimumWholeNumber}–{option.MaximumWholeNumber} {option.Unit}."),
        ProcessingOptionKind.RealNumber => string.Create(CultureInfo.InvariantCulture, $"{option.MinimumRealNumber}–{option.MaximumRealNumber} {option.Unit}."),
        ProcessingOptionKind.Color => "Use #RRGGBB or the colour picker.",
        _ => null
    };
}
