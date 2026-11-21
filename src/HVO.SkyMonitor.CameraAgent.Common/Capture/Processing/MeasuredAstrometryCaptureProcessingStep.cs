using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureAstrometryExecutionGate : IDisposable
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}

/// <summary>Explicit sampled background work on admitted raw. Inputs come from the frozen graph and its dependencies.</summary>
internal sealed class MeasuredAstrometryCaptureProcessingStep(CaptureProcessingStepMetadata metadata,
    MeasuredAstrometryCaptureOptions options, CameraAgentRecipeExecutionAdapter adapter, CaptureAstrometryExecutionGate gate,
    CaptureProcessingPersistence? persistence = null, IServiceProvider? services = null)
    : ConfigurableCaptureProcessingStep<MeasuredAstrometryCaptureOptions>(metadata, options), ICaptureProcessingGraphStep,
      IRequiredCaptureProcessingDependencies, IMultiOutputCaptureProcessingGraphStep, IFrozenAuxiliaryInputCaptureProcessingStep
{
    internal const string Alias = "MeasuredAstrometry";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Enabled => true;
    public string RecipeName => BuiltInProcessingRecipes.MeasuredAstrometry;
    internal bool UsesAstrometryPrediction => Options.UseAstrometryPrediction;
    internal bool HasCrossedSceneDependencies { get; set; }
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => MeasuredAstrometrySolutionV1.CurrentSchemaVersion;
    public string? OutputMediaType => MeasuredAstrometryJson.MediaType;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string> { Options.UseAstrometryPrediction
                ? BuiltInProcessingRecipes.AstrometryPrediction : BuiltInProcessingRecipes.ProjectedScene },
            new HashSet<string>(ProjectedSceneV1.SupportedSchemaVersions)),
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string> { BuiltInProcessingRecipes.MeasuredStellarAssociations },
            new HashSet<string> { MeasuredStellarAssociationsV1.CurrentSchemaVersion })
    ];
    public IReadOnlyList<CaptureProcessingOutputDescriptor> Outputs =>
    [
        new(OutputRole, OutputVariant, RecipeName, OutputSchemaVersion, MediaType: OutputMediaType),
        new(OutputRole, OutputVariant + "-context", RetainedAstrometryProducts.ContextRecipe,
            MeasuredAstrometryContextV1.CurrentSchemaVersion,
            new(RetainedAstrometryProducts.ContextRecipe, "1.0.0", RetainedAstrometryProducts.Version, ProcessingOperationKind.Analyzer),
            MediaType: MeasuredAstrometryJson.ContextMediaType)
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var descriptor = context.ReconstructionDescriptor ?? throw new InvalidDataException("Astrometry requires durable raw admission.");
        if (context.Config.Pipeline.SchemaVersion != CapturePipelineSchemaVersions.ExplicitV2)
            throw new InvalidOperationException("Astrometry requires an explicit v2 graph.");
        if (!context.IsReplayExecution && HasCrossedSceneDependencies)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped("astrometry.crossed-scene-dependencies"));
            return;
        }
        if (!context.IsReplayExecution && (descriptor.Capture.CaptureSequence - 1) % Options.CaptureStride != 0)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped("astrometry.capture-cadence"));
            return;
        }
        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, context.Artifacts!.Raw,
            descriptor.Artifact.Variant, context.AcquisitionTiming, descriptor);
        var dependencies = context.GetDependencyProducts();
        var scene = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, dependencies.Single(p => ProjectedSceneV1.IsSupportedSchemaVersion(p.SchemaVersion)));
        var measured = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, dependencies.Single(p => p.SchemaVersion == MeasuredStellarAssociationsV1.CurrentSchemaVersion));
        ProcessingProduct? newContext = null;
        ProcessingArtifact frozen;
        if (context.IsReplayExecution)
        {
            if (context.FrozenAuxiliaryInputFailure is not null)
                throw new InvalidDataException("The original astrometry context is missing or altered.");
            frozen = context.GetFrozenAuxiliaryInputs().Single(p => p.SchemaVersion == MeasuredAstrometryContextV1.CurrentSchemaVersion);
        }
        else
        {
            var catalog = Options.Catalog ?? await (Options.CatalogSelection
                ?? throw new InvalidDataException("A pinned astrometry catalog is required."))
                .ResolveAsync(services?.GetService<IAstrometricCatalogSource>(), cancellationToken).ConfigureAwait(false);
            var input = RetainedAstrometryProducts.CreateContext(raw, measured, scene, catalog, Options.Clock, Options.PreviousAccepted);
            newContext = RetainedAstrometryProducts.CreateContextProduct(input, raw, measured, scene, OutputVariant + "-context");
            frozen = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, newContext);
        }
        if (context.IsReplayExecution)
        {
            var retained = context.GetFrozenAuxiliaryInputs().Single(p => p.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
            var product = RetainedAstrometryProducts.Product(retained);
            if (product.Variant != OutputVariant) throw new InvalidDataException("Replay cannot rename an immutable assessment.");
            var originalOptions = product.Recipe.Descriptor.Options.GetProperty("parameters")
                .Deserialize<MeasuredAstrometryOptions>(Json)!;
            var originalRequest = RetainedAstrometryProducts.Request(raw, measured, scene, frozen, originalOptions, OutputVariant);
            _ = RetainedAstrometryProducts.Verify(originalRequest, product, cancellationToken);
            context.RecordExecutionRequest(originalRequest);
            context.RecordExecutionRoute(ProcessingNodeExecutionRoute.InProcess);
            LayeredPresentationCaptureProcessing.Add(context, RetainedAstrometryProducts.Product(frozen), product);
            return;
        }
        var request = RetainedAstrometryProducts.Request(raw, measured, scene, frozen, Options.Recipe, OutputVariant);
        await gate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (outcome, restored) = await (persistence ?? throw new InvalidOperationException("Astrometry requires durable publication."))
                .ExecuteRetainedAstrometryAsync(context, request, newContext!,
                    () => adapter.ExecuteAsync(context, request, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (restored)
            {
                context.RecordExecutionRequest(request);
                context.RecordExecutionRoute(ProcessingNodeExecutionRoute.InProcess);
                context.RecordExecutionOutcome(outcome);
            }
            if (outcome.Status != ProcessingOutcomeStatus.Produced)
            {
                context.AddProcessingOutcome(outcome);
                return;
            }
            var solution = outcome.Products.Single();
            _ = RetainedAstrometryProducts.Verify(request, solution, cancellationToken);
            LayeredPresentationCaptureProcessing.Add(context, newContext is null ? [solution] : [newContext, solution]);
        }
        finally { gate.Semaphore.Release(); }
    }
}

public sealed class MeasuredAstrometryCaptureOptions : IValidatableObject
{
    [Required, MinLength(1), MaxLength(96)]
    public string OutputVariant { get; init; } = "measured-astrometry-v1";
    [Range(60, 1_000_000)]
    public int CaptureStride { get; init; } = 60;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool UseAstrometryPrediction { get; init; }
    public AstrometryCatalogSnapshotV1? Catalog { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MeasuredAstrometryCatalogSelection? CatalogSelection { get; init; }
    public AstrometricClockFacts Clock { get; init; } = AstrometricClockFacts.NotSupplied;
    public AstrometricFrameAssessment? PreviousAccepted { get; init; }
    public MeasuredAstrometryOptions Recipe { get; init; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((Catalog is null) == (CatalogSelection is null))
            yield return new ValidationResult("Supply exactly one frozen catalog or pinned installed catalog selection.");
        if (CatalogSelection is { } selection) selection.Validate();
        if (Catalog is not null)
        {
            if (Catalog.Stars.Count > AstrometricCatalogData.MaximumEntries)
                yield return new ValidationResult("The astrometry snapshot exceeds the 2500-entry capture bound.");
            _ = new AstrometricCatalogData(Catalog.Metadata, Catalog.Stars, Catalog.Complete,
                Catalog.CompletenessMagnitudeLimit, Catalog.CoordinateModel, Catalog.Package);
        }
        Clock.Validate();
        _ = BuiltInProcessingRecipes.NormalizeOptions(BuiltInProcessingRecipes.MeasuredAstrometry,
            System.Text.Json.JsonSerializer.SerializeToElement(Recipe));
        if (PreviousAccepted is { } prior)
        {
            AstrometricEvidenceJson.Validate(prior);
            if (!prior.HasMeasuredMapping) yield return new ValidationResult("The explicit prior must be accepted.");
        }
    }
}

/// <summary>Compact immutable pin; selected catalog rows are frozen once in the retained execution context.</summary>
public sealed record MeasuredAstrometryCatalogSelection(string CatalogIdentitySha256, string SelectionIdentitySha256,
    double MaximumMagnitude, int MaximumEntries = 2500)
{
    internal void Validate()
    {
        if (!double.IsFinite(MaximumMagnitude) || MaximumMagnitude is < -30 or > 30 || MaximumEntries is < 1 or > 2500 ||
            !IsHash(CatalogIdentitySha256) || !IsHash(SelectionIdentitySha256))
            throw new ValidationException("Invalid pinned astrometry catalog selection.");
    }

    internal async ValueTask<AstrometryCatalogSnapshotV1> ResolveAsync(IAstrometricCatalogSource? source, CancellationToken cancellationToken)
    {
        Validate();
        var data = await (source ?? throw new InvalidDataException("The installed astrometry catalog adapter is unavailable."))
            .ReadAsync(MaximumMagnitude, MaximumEntries, cancellationToken).ConfigureAwait(false);
        if (!data.IsCompleteForRequestedMagnitude || data.Provenance is null ||
            data.IdentitySha256 != CatalogIdentitySha256 || data.SelectionIdentitySha256 != SelectionIdentitySha256 ||
            data.Stars.Count > MaximumEntries || data.CompletenessMagnitudeLimit < MaximumMagnitude)
            throw new InvalidDataException("The installed astrometry catalog does not match its complete pinned selection.");
        return new(data.Metadata, data.Stars, data.Provenance, true, data.CompletenessMagnitudeLimit, data.CoordinateModel);
    }

    private static bool IsHash(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
