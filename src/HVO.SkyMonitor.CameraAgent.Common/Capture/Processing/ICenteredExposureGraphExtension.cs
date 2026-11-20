using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Adds concrete registered stages to a centered configured-basic plan before it is frozen.</summary>
/// <remarks>Retained graph reconstruction uses the stored plan and does not invoke extensions.</remarks>
public interface ICenteredExposureGraphExtension
{
    CapturePipelineConfig Configure(CapturePipelineConfig pipeline);
}
