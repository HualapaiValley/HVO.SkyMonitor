using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public sealed partial class OperationsCurrentSky : ComponentBase
{
    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
}
