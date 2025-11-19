using System.Collections.Generic;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public interface ICaptureProcessingPipelineFactory
{
    IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config);
}
