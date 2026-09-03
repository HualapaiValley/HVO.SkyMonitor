using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Processing;

public static class ProcessingGraphSchemaVersions
{
    public const string V1 = "hvo-processing-graph-v1";

    public const string Current = V1;
}

public static class ProcessingGraphHosts
{
    public const string CameraAgent = "camera-agent";

    public const string LogicHost = "logic-host";
}

public enum ProcessingGraphNodeFailurePolicy
{
    Required,
    Optional
}

public enum ProcessingGraphDependencyKind
{
    Artifact,
    CanonicalJson,
    Annotation,
    Outcome,
    Ordering
}

public enum ProcessingGraphInputBindingKind
{
    PrimaryArtifact,
    AuxiliaryArtifact,
    CanonicalJson,
    Annotation
}

public enum ProcessingGraphWindowKind
{
    Trailing,
    Centered
}

public enum ProcessingGraphMissingInputOutcome
{
    Run,
    Skip,
    Fail,
    Quarantine
}

public sealed record ProcessingGraphSourceDefinition(
    string Id,
    ImmutableArray<ProcessingGraphProductContract> Outputs);

public sealed record ProcessingGraphDependencyDefinition(
    string ProducerId,
    ProcessingGraphDependencyKind Kind = ProcessingGraphDependencyKind.Artifact,
    bool Required = true);

public sealed record ProcessingGraphInputContract(
    ImmutableArray<FrameArtifactRole> Roles,
    ImmutableArray<ProcessingProductKind> ProductKinds,
    ImmutableArray<string> Variants,
    ImmutableArray<string> RecipeNames,
    ImmutableArray<string> SchemaVersions,
    bool Required = true,
    string BindingName = "input",
    ProcessingGraphInputBindingKind BindingKind = ProcessingGraphInputBindingKind.PrimaryArtifact);

/// <summary>
/// Output contract for a graph source or node. <c>Algorithms</c> and <c>MediaType</c> are optional in
/// <see cref="ProcessingGraphSchemaVersions.V1"/> JSON so persisted schema-6 CameraAgent revisions that predate
/// <c>mediaType</c> stay readable, and a <c>null</c> media type is omitted from the canonical form so their
/// definition identities remain stable.
/// </summary>
public sealed record ProcessingGraphProductContract
{
    [JsonConstructor]
    public ProcessingGraphProductContract(
        FrameArtifactRole Role,
        string Variant,
        ProcessingProductKind ProductKind,
        ProcessingRecipeDefinition? Recipe = null,
        string? SchemaVersion = null,
        ImmutableArray<ProcessingAlgorithmIdentity> Algorithms = default,
        string? MediaType = null)
    {
        this.Role = Role;
        this.Variant = Variant;
        this.ProductKind = ProductKind;
        this.Recipe = Recipe;
        this.SchemaVersion = SchemaVersion;
        this.Algorithms = Algorithms.IsDefault ? [] : Algorithms;
        this.MediaType = MediaType;
    }

    public FrameArtifactRole Role { get; init; }

    public string Variant { get; init; }

    public ProcessingProductKind ProductKind { get; init; }

    public ProcessingRecipeDefinition? Recipe { get; init; }

    public string? SchemaVersion { get; init; }

    public ImmutableArray<ProcessingAlgorithmIdentity> Algorithms { get; init; }

    public string? MediaType { get; init; }

    public void Deconstruct(
        out FrameArtifactRole Role,
        out string Variant,
        out ProcessingProductKind ProductKind,
        out ProcessingRecipeDefinition? Recipe,
        out string? SchemaVersion,
        out ImmutableArray<ProcessingAlgorithmIdentity> Algorithms,
        out string? MediaType)
    {
        Role = this.Role;
        Variant = this.Variant;
        ProductKind = this.ProductKind;
        Recipe = this.Recipe;
        SchemaVersion = this.SchemaVersion;
        Algorithms = this.Algorithms;
        MediaType = this.MediaType;
    }
}

public sealed record ProcessingGraphWindowRequirement(
    ProcessingGraphWindowKind Kind,
    int MinimumInputCount,
    int MaximumInputCount,
    ImmutableArray<int> RequiredPositions,
    ImmutableArray<string> CompatibilityLabels,
    long TimeoutTicks = 3_000_000_000,
    ProcessingGraphMissingInputOutcome MissingInputOutcome =
        ProcessingGraphMissingInputOutcome.Skip);

public sealed record ProcessingGraphNodeDefinition
{
    [JsonConstructor]
    public ProcessingGraphNodeDefinition(
        string id,
        string stepAlias,
        string stepVersion,
        ProcessingOperationKind operationKind,
        bool enabled,
        ProcessingGraphNodeFailurePolicy failurePolicy,
        int order,
        JsonElement effectiveOptions,
        ImmutableArray<ProcessingGraphDependencyDefinition> dependencies,
        ImmutableArray<ProcessingGraphInputContract> inputs,
        ImmutableArray<ProcessingGraphProductContract> outputs,
        ProcessingGraphWindowRequirement? window,
        ImmutableArray<string> capabilityLabels,
        ImmutableArray<string> hostApplicability)
    {
        Id = id;
        StepAlias = stepAlias;
        StepVersion = stepVersion;
        OperationKind = operationKind;
        Enabled = enabled;
        FailurePolicy = failurePolicy;
        Order = order;
        EffectiveOptions = effectiveOptions.Clone();
        Dependencies = dependencies;
        Inputs = inputs;
        Outputs = outputs;
        Window = window;
        CapabilityLabels = capabilityLabels;
        HostApplicability = hostApplicability;
    }

    public string Id { get; }

    public string StepAlias { get; }

    public string StepVersion { get; }

    public ProcessingOperationKind OperationKind { get; }

    public bool Enabled { get; }

    public ProcessingGraphNodeFailurePolicy FailurePolicy { get; }

    public int Order { get; }

    public JsonElement EffectiveOptions { get; }

    public ImmutableArray<ProcessingGraphDependencyDefinition> Dependencies { get; }

    public ImmutableArray<ProcessingGraphInputContract> Inputs { get; }

    public ImmutableArray<ProcessingGraphProductContract> Outputs { get; }

    public ProcessingGraphWindowRequirement? Window { get; }

    public ImmutableArray<string> CapabilityLabels { get; }

    public ImmutableArray<string> HostApplicability { get; }
}

public sealed record ProcessingGraphDefinition(
    string SchemaVersion,
    string Name,
    string Revision,
    ImmutableArray<ProcessingGraphSourceDefinition> Sources,
    ImmutableArray<ProcessingGraphNodeDefinition> Nodes);

public sealed record ProcessingGraphCompilationContext(
    string? Host,
    ImmutableArray<string> AvailableCapabilities)
{
    public static ProcessingGraphCompilationContext Portable { get; } = new(null, []);
}

public sealed record ProcessingGraphInputBinding(
    int InputIndex,
    string BindingName,
    ProcessingGraphInputBindingKind BindingKind,
    string ProducerId,
    int OutputIndex,
    bool Required);

public sealed record ProcessingGraphPlanNode(
    ProcessingGraphNodeDefinition Definition,
    string IdentitySha256,
    ImmutableArray<ProcessingGraphInputBinding> InputBindings);

public sealed record ProcessingGraphExecutionPlan(
    string DefinitionIdentitySha256,
    string PlanIdentitySha256,
    JsonElement CanonicalDefinition,
    ImmutableArray<ProcessingGraphSourceDefinition> Sources,
    ImmutableArray<ProcessingGraphPlanNode> Nodes);

public sealed record ProcessingGraphDiagnostic(
    string Code,
    string Path,
    string Message);

public sealed record ProcessingGraphCompilationResult(
    ProcessingGraphExecutionPlan? Plan,
    ImmutableArray<ProcessingGraphDiagnostic> Diagnostics)
{
    [JsonIgnore]
    public bool IsValid => Plan is not null && Diagnostics.IsEmpty;
}

public sealed record ProcessingGraphParseResult(
    ProcessingGraphDefinition? Definition,
    ImmutableArray<ProcessingGraphDiagnostic> Diagnostics)
{
    [JsonIgnore]
    public bool IsValid => Definition is not null && Diagnostics.IsEmpty;
}

public static class ProcessingGraphReasonCodes
{
    public const string InvalidJson = "processing.graph.invalid-json";
    public const string UnsupportedSchema = "processing.graph.unsupported-schema";
    public const string InvalidIdentity = "processing.graph.invalid-identity";
    public const string LimitExceeded = "processing.graph.limit-exceeded";
    public const string DuplicateIdentifier = "processing.graph.duplicate-identifier";
    public const string InvalidNode = "processing.graph.invalid-node";
    public const string MissingProducer = "processing.graph.missing-producer";
    public const string DisabledProducer = "processing.graph.disabled-producer";
    public const string DuplicateOutput = "processing.graph.duplicate-output";
    public const string IncompatibleInput = "processing.graph.incompatible-input";
    public const string AmbiguousInput = "processing.graph.ambiguous-input";
    public const string InvalidWindow = "processing.graph.invalid-window";
    public const string MissingCapability = "processing.graph.missing-capability";
    public const string HostInapplicable = "processing.graph.host-inapplicable";
    public const string Cycle = "processing.graph.cycle";
}
