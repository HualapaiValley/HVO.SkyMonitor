using HVO.SkyMonitor.LogicHost.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.LogicHost.Components.Pages.Operations;

public sealed partial class OperationsCurrentSky : ComponentBase
{
    private readonly CurrentSkyPosition position = new();
    [Parameter] public Guid ObservatoryId { get; set; }
    [Parameter] public Guid CameraId { get; set; }
}
