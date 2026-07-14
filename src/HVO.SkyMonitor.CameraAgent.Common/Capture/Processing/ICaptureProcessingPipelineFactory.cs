using System.Collections.Generic;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

public interface ICaptureProcessingPipelineFactory
{
    IReadOnlyList<ICaptureProcessingStep> CreatePipeline(CameraModuleConfig config);

    CaptureProcessingGraph CreateGraph(CameraModuleConfig config)
        => new(CreatePipeline(config)
            .OrderBy(static step => step.Order)
            .ThenBy(static step => step.Name, StringComparer.Ordinal)
            .Select(static step => new CaptureProcessingGraphNode(
                step.Name, step, [], true, null, null, null))
            .ToArray());
}
