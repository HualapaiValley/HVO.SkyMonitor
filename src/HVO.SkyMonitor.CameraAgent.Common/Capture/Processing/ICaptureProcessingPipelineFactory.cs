using System.Collections.Generic;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public interface ICaptureProcessingPipelineFactory
{
    IReadOnlyList<string> StableStepAliases => [];

    CaptureProcessingGraph CreateGraph(CameraModuleConfig config);

    CaptureProcessingGraph CreateGraph(
        CameraModuleConfig config,
        string definitionName,
        string definitionRevision)
        => CreateGraph(config);

    CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config);
}
