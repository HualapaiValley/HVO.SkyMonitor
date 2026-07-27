using HVO.SkyMonitor.AgentCore;
using System.Text.Json;

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

internal interface ICompoundCaptureProcessingGraphStep
{
    IReadOnlyList<IReadOnlySet<FrameArtifactRole>> RequiredDependencyRoleGroups { get; }

    IReadOnlyDictionary<FrameArtifactRole, IReadOnlySet<string>> RequiredDependencyRecipes { get; }
}

internal interface ICaptureProcessingArtifactConsumer
{
    IReadOnlySet<FrameArtifactRole> AcceptedDependencyRoles { get; }
}

internal interface ICaptureProcessingOutcomeConsumer
{
}

public sealed record CaptureProcessingGraphNode(
    string Id,
    ICaptureProcessingStep Step,
    IReadOnlyList<string> Dependencies,
    bool Required,
    string? RecipeName,
    FrameArtifactRole? OutputRole,
    string? OutputVariant,
    string PlanSha256 = "",
    string? Alias = null,
    int? EffectiveOrder = null,
    JsonElement? EffectiveOptions = null,
    IReadOnlyList<string>? DeclaredDependencies = null);

public sealed record CaptureProcessingPlanNode(
    string Id,
    string Alias,
    bool Enabled,
    bool Required,
    int? Order,
    JsonElement? Options,
    IReadOnlyList<string>? Dependencies,
    string? RecipeName,
    FrameArtifactRole? OutputRole,
    string? OutputVariant);

public sealed record CaptureProcessingPlanPreview(
    string SchemaVersion,
    CapturePipelineDependencyPolicy DependencyPolicy,
    string DesiredSha256,
    string EffectiveSha256,
    IReadOnlyList<CaptureProcessingPlanNode> DesiredNodes,
    IReadOnlyList<CaptureProcessingPlanNode> EffectiveNodes);

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
