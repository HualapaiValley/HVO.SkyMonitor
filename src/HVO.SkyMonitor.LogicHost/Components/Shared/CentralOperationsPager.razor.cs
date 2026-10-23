using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Shared;

public sealed partial class CentralOperationsPager : ComponentBase
{
    [Parameter] public string? Current { get; set; }
    [Parameter] public string? Next { get; set; }
    [Parameter, EditorRequired] public string FirstLink { get; set; } = "";
    [Parameter, EditorRequired] public string NextLink { get; set; } = "";
    [Parameter, EditorRequired] public string Label { get; set; } = "";
}
