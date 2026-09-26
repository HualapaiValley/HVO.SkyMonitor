using HVO.SkyMonitor.CameraAgent.Common.Operations;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class FreshnessBadge : ComponentBase
{
    [Parameter, EditorRequired] public string Value { get; set; } = string.Empty;

    private string VariantClass => Value switch
    {
        OperationsFreshness.Fresh => "hvo-chip--success",
        OperationsFreshness.Stale => "hvo-chip--warning",
        OperationsFreshness.Disabled or OperationsFreshness.Static => "hvo-chip--neutral",
        _ => "hvo-chip--neutral"
    };
}
