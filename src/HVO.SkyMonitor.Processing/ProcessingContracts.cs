using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

public enum ProcessingOperationKind
{
    Transform,
    Analyzer,
    Gate,
    Window
}

public enum ProcessingOutcomeStatus
{
    Produced,
    Skipped,
    RetryableFailure,
    TerminalFailure
}

public enum ProcessingInputKind
{
    Raw,
    Calibrated,
    Combined,
    RecipeResult
}

/// <summary>Selects one explicit processing input without host-specific lookup behavior.</summary>
public sealed record ProcessingInputSelector(
    ProcessingInputKind Kind,
    FrameArtifactRole Role,
    string? Variant = null,
    string? RecipeIdentitySha256 = null)
{
    public static ProcessingInputSelector Raw(string? variant = null) =>
        new(ProcessingInputKind.Raw, FrameArtifactRole.Raw, variant);

    public static ProcessingInputSelector Calibrated(string? variant = null) =>
        new(ProcessingInputKind.Calibrated, FrameArtifactRole.Calibrated, variant);

    public static ProcessingInputSelector Combined(string? variant = null) =>
        new(ProcessingInputKind.Combined, FrameArtifactRole.Combined, variant);

    public static ProcessingInputSelector RecipeResult(
        FrameArtifactRole role,
        string variant,
        string recipeIdentitySha256) =>
        new(ProcessingInputKind.RecipeResult, role, variant, recipeIdentitySha256);
}

/// <summary>Capture-time compatibility axes required by linear rolling windows.</summary>
public sealed record ProcessingCompatibilityIdentity(
    string Rig,
    string Orientation,
    string Calibration,
    string Mask,
    string Sensor,
    string SetpointRegime,
    string ProcessingProfile,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? LocationIdentitySha256 = null);

/// <summary>
/// A borrowed immutable input. Its payload must remain valid until execution completes. Observation bounds describe
/// sensor integration independently of artifact creation and are required by detector-input construction.
/// </summary>
public sealed record ProcessingArtifact(
    Guid ArtifactId,
    FrameArtifactRole Role,
    string Variant,
    string RecipeIdentitySha256,
    string MediaType,
    FrameLayoutDescriptor? Layout,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset CreatedUtc,
    TimeSpan Integration,
    ProcessingCompatibilityIdentity Compatibility,
    long? CaptureSequence = null,
    IReadOnlyList<Guid>? SourceArtifactIds = null,
    DateTimeOffset? ObservationStartedUtc = null,
    DateTimeOffset? ObservationEndedUtc = null)
{
    /// <summary>
    /// Resolves the observation end. Accelerated captures may report an instantaneous acquisition while retaining a
    /// positive modeled integration; all execution locations expand that interval identically.
    /// </summary>
    public static DateTimeOffset ResolveObservationEndedUtc(
        DateTimeOffset observationStartedUtc,
        DateTimeOffset observationEndedUtc,
        TimeSpan integration)
        => observationEndedUtc == observationStartedUtc && integration > TimeSpan.Zero
            ? observationStartedUtc.Add(integration)
            : observationEndedUtc;
}

public sealed record ProcessingAlgorithmIdentity(string Name, string Version);

/// <summary>Canonical descriptive identity plus a hash that covers recipe and implementation versions.</summary>
public sealed record ProcessingRecipeIdentity(
    RecipeIdentityDescriptor Descriptor,
    string IdentitySha256);

/// <summary>An owned recipe output with complete immediate lineage and implementation provenance.</summary>
public sealed record ProcessingProduct(
    FrameArtifactRole Role,
    string Variant,
    string OutputIdentitySha256,
    string MediaType,
    FrameLayoutDescriptor? Layout,
    ReadOnlyMemory<byte> Payload,
    string ChecksumSha256,
    ProcessingRecipeIdentity Recipe,
    IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms,
    IReadOnlyList<Guid> SourceArtifactIds,
    TimeSpan TotalIntegration,
    ProcessingCompatibilityIdentity Compatibility);

/// <summary>Explicit projected geometry; hosts remain responsible for catalog and scene acquisition.</summary>
public sealed record ProcessingAnnotationInput(
    IReadOnlyList<ProjectedAnnotationObject> Objects,
    IReadOnlyList<ProjectedAnnotationSegment> Segments,
    PreviewTransform Transform,
    ProjectedAnnotationOverlay? ProjectionOverlay,
    string ProvenanceSha256);

public enum ProcessingAuxiliaryInputKind
{
    Artifact,
    CanonicalJson
}

/// <summary>A named artifact selector or immutable canonical JSON context supplied explicitly by a host.</summary>
public sealed record ProcessingAuxiliaryInput(
    string Name,
    ProcessingAuxiliaryInputKind Kind,
    ProcessingInputSelector? Selector = null,
    string? SchemaVersion = null,
    string? IdentitySha256 = null,
    ReadOnlyMemory<byte> Payload = default,
    Guid? ArtifactId = null);

public sealed record ProcessingExecutionRequest(
    string RecipeName,
    JsonElement Options,
    ProcessingInputSelector Input,
    IReadOnlyList<ProcessingArtifact> Inputs,
    string OutputVariant,
    ProcessingAnnotationInput? Annotation = null,
    IReadOnlyList<ProcessingAuxiliaryInput>? AuxiliaryInputs = null,
    Guid? InputArtifactId = null);

public sealed record ProcessingOutcome(
    ProcessingOutcomeStatus Status,
    string? ReasonCode,
    string? Field,
    IReadOnlyList<ProcessingProduct> Products)
{
    public static ProcessingOutcome Produced(params ProcessingProduct[] products) =>
        new(ProcessingOutcomeStatus.Produced, null, null, products);

    public static ProcessingOutcome Skipped(string reasonCode, string? field = null) =>
        new(ProcessingOutcomeStatus.Skipped, reasonCode, field, []);

    public static ProcessingOutcome RetryableFailure(string reasonCode, string? field = null) =>
        new(ProcessingOutcomeStatus.RetryableFailure, reasonCode, field, []);

    public static ProcessingOutcome TerminalFailure(string reasonCode, string? field = null) =>
        new(ProcessingOutcomeStatus.TerminalFailure, reasonCode, field, []);
}

public static class ProcessingReasonCodes
{
    public const string UnknownRecipe = "processing.unknown-recipe";
    public const string InvalidOptions = "processing.invalid-options";
    public const string InvalidInput = "processing.invalid-input";
    public const string InvalidSelector = "processing.invalid-selector";
    public const string MissingInput = "processing.missing-input";
    public const string AmbiguousInput = "processing.ambiguous-input";
    public const string UnsupportedFormat = "processing.unsupported-format";
    public const string InvalidLayout = "processing.invalid-layout";
    public const string IncompatibleInput = "processing.incompatible-input";
    public const string InvalidLineage = "processing.invalid-lineage";
    public const string MissingAnnotation = "processing.missing-annotation";
    public const string InvalidAnnotation = "processing.invalid-annotation";
    public const string ExecutionFailed = "processing.execution-failed";
}

public sealed record ProcessingRecipeDefinition(
    string Name,
    string SemanticVersion,
    string ImplementationVersion,
    ProcessingOperationKind OperationKind);

public interface IProcessingRecipe
{
    ProcessingRecipeDefinition Definition { get; }

    JsonElement NormalizeOptions(JsonElement options);

    ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        ProcessingRecipeIdentity identity,
        CancellationToken cancellationToken);
}

public interface IProcessingRecipeExecutor
{
    ValueTask<ProcessingOutcome> ExecuteAsync(
        ProcessingExecutionRequest request,
        CancellationToken cancellationToken = default);
}
