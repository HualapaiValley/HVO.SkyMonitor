using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal interface ICaptureProcessingGraphStep
{
    bool Enabled { get; }

    string RecipeName { get; }

    FrameArtifactRole OutputRole { get; }

    string OutputVariant { get; }

    IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; }
}

internal interface IWindowCaptureProcessingGraphStep
{
    int MaximumInputCount { get; }
}

public sealed record CaptureProcessingGraphNode(
    string Id,
    ICaptureProcessingStep Step,
    IReadOnlyList<string> Dependencies,
    bool Required,
    string? RecipeName,
    FrameArtifactRole? OutputRole,
    string? OutputVariant,
    string PlanSha256 = "");

public sealed class CaptureProcessingGraph
{
    internal CaptureProcessingGraph(IReadOnlyList<CaptureProcessingGraphNode> nodes)
    {
        Nodes = nodes;
    }

    public IReadOnlyList<CaptureProcessingGraphNode> Nodes { get; }

    public void DisposeSteps()
    {
        foreach (var disposable in Nodes.Select(static node => node.Step).OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }
}
