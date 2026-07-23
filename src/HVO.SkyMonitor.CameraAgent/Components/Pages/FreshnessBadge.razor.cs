using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class FreshnessBadge : ComponentBase
{
    [Parameter, EditorRequired] public string Value { get; set; } = string.Empty;
}
