using HVO.SkyMonitor.CameraAgent.Common.Operations;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Pages;

public sealed partial class OperationsQueueFacts : ComponentBase
{
    [Parameter, EditorRequired] public OperationsQueueState Queue { get; set; } = default!;
}
