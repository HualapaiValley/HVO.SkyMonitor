using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal interface ICaptureProcessingGraphStep
{
    bool Enabled { get; }

    string RecipeName { get; }

    FrameArtifactRole OutputRole { get; }

    string OutputVariant { get; }

    IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; }

    string? OutputSchemaVersion => null;

    string? SharedStepVersion => null;

    ProcessingOperationKind? SharedOperationKind => null;

    ProcessingRecipeDefinition? SharedOutputRecipe => null;

    IReadOnlyList<ProcessingAlgorithmIdentity> SharedOutputAlgorithms => [];
}

internal sealed record CaptureProcessingOutputDescriptor(
    FrameArtifactRole Role,
    string Variant,
    string RecipeName,
    string? SchemaVersion = null,
    ProcessingRecipeDefinition? SharedRecipe = null,
    IReadOnlyList<ProcessingAlgorithmIdentity>? SharedAlgorithms = null);

internal interface IMultiOutputCaptureProcessingGraphStep
{
    IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs { get; }
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

internal sealed record CaptureProcessingDependencyRequirement(
    IReadOnlySet<FrameArtifactRole> Roles,
    IReadOnlySet<string>? RecipeNames = null,
    IReadOnlySet<string>? SchemaVersions = null,
    string? Variant = null,
    bool Required = true);

internal interface IRequiredCaptureProcessingDependencies
{
    IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements { get; }
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
    IReadOnlyList<string>? DeclaredDependencies = null,
    CaptureProcessingPublicationPolicy? Publication = null,
    IReadOnlySet<string>? OptionalDependencies = null,
    string SharedPlanNodeIdentitySha256 = "");

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
    string? OutputVariant,
    [property: System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    CaptureProcessingPublicationPolicy? Publication = null);

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
        : this(nodes, null)
    {
    }

    internal CaptureProcessingGraph(
        IReadOnlyList<CaptureProcessingGraphNode> nodes,
        ProcessingGraphExecutionPlan? sharedPlan)
    {
        Nodes = nodes;
        SharedPlan = sharedPlan;
    }

    public IReadOnlyList<CaptureProcessingGraphNode> Nodes { get; }

    public ProcessingGraphExecutionPlan? SharedPlan { get; }

    public void DisposeSteps()
    {
        foreach (var disposable in Nodes.Select(static node => node.Step).OfType<IDisposable>())
        {
            disposable.Dispose();
        }
    }
}
