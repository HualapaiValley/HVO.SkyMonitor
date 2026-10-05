using System.Collections.Generic;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public interface ICaptureProcessingPipelineFactory
{
    IReadOnlyList<string> StableStepAliases => [];

    CaptureProcessingGraph CreateGraph(CameraModuleConfig config);

    /// <summary>Restores a frozen graph for retained evidence without applying new-acquisition configuration policy.</summary>
    CaptureProcessingGraph CreateRetainedGraph(CameraModuleConfig config) => CreateGraph(config);

    CaptureProcessingGraph CreateGraph(
        CameraModuleConfig config,
        string definitionName,
        string definitionRevision)
        => CreateGraph(config);

    CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config);
}
