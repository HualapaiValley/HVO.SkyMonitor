using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

public sealed partial class MixedGainDiagnostics
{
    [Parameter, EditorRequired] public CameraAgentProcessingNodeView Node { get; set; } = default!;
}
