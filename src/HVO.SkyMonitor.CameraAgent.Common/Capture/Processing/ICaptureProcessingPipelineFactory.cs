using System.Collections.Generic;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public interface ICaptureProcessingPipelineFactory
{
    IReadOnlyList<string> StableStepAliases => [];

    CaptureProcessingGraph CreateGraph(CameraModuleConfig config);

    /// <summary>Restores an already committed active profile under the current acquisition policy.</summary>
    CaptureProcessingGraph CreateActiveProfileGraph(CameraModuleConfig config) => CreateGraph(config);

    /// <summary>Restores a frozen graph for retained evidence without applying new-acquisition configuration policy.</summary>
    CaptureProcessingGraph CreateRetainedGraph(CameraModuleConfig config) => CreateGraph(config);

    CaptureProcessingGraph CreateGraph(
        CameraModuleConfig config,
        string definitionName,
        string definitionRevision)
        => CreateGraph(config);

    CaptureProcessingPlanPreview PreviewPlan(CameraModuleConfig config);

    /// <summary>Compiles a saved graph revision preview under retained-capture compatibility without relaxing proposal validation.</summary>
    CaptureProcessingPlanPreview PreviewRetainedPlan(CameraModuleConfig config) => PreviewPlan(config);
}

internal enum CaptureProcessingGraphCompilation
{
    Proposal,
    ActiveProfile,
    RetainedCapture
}
