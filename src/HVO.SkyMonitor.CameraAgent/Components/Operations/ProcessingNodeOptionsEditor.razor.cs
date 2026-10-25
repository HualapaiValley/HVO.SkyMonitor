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
    [Parameter] public bool? NodeEnabled { get; set; }
    [Parameter] public EventCallback<bool> NodeEnabledChanged { get; set; }

    private async Task ToggleChangedAsync(string key, bool value)
    {
        if (key == "enabled" && NodeEnabled.HasValue)
        {
            await NodeEnabledChanged.InvokeAsync(value).ConfigureAwait(false);
            await Changed.InvokeAsync().ConfigureAwait(false);
        }
        else
        {
            await ChangedAsync(key, value.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        }
    }

    private async Task ChangedAsync(string key, string value)
    {
        Form.Set(key, value);
        await Changed.InvokeAsync().ConfigureAwait(false);
    }

    private async Task CollectionChangedAsync(Action edit)
    {
        edit();
        await Changed.InvokeAsync().ConfigureAwait(false);
    }

    private static string? Bounds(ProcessingOptionDescriptor option) => option.Kind switch
    {
        ProcessingOptionKind.WholeNumber or ProcessingOptionKind.NullableWholeNumber => string.Create(CultureInfo.InvariantCulture, $"{option.MinimumWholeNumber}–{option.MaximumWholeNumber} {option.Unit}."),
        ProcessingOptionKind.RealNumber or ProcessingOptionKind.NullableRealNumber => string.Create(CultureInfo.InvariantCulture, $"{option.MinimumRealNumber}–{option.MaximumRealNumber} {option.Unit}."),
        ProcessingOptionKind.TextCollection => $"Maximum {option.MaximumItems} items, {option.MaximumLength} characters per item.",
        ProcessingOptionKind.EnumCollection when option.MaximumItems is { } maximum => $"Maximum {maximum} supported selections.",
        ProcessingOptionKind.ChoiceCollection => $"Maximum {option.MaximumItems} unique supported selections.",
        ProcessingOptionKind.Color => "Use #RRGGBB or the colour picker.",
        ProcessingOptionKind.Text when option.MaximumLength.HasValue => $"Maximum {option.MaximumLength} characters.",
        _ => null
    };
}
