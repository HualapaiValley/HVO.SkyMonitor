using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CaptureAstrometryExecutionGate : IDisposable
{
    internal SemaphoreSlim Semaphore { get; } = new(1, 1);
    public void Dispose() => Semaphore.Dispose();
}

/// <summary>Explicit sampled background work on admitted raw. Inputs come from the frozen graph and its dependencies.</summary>
internal sealed class MeasuredAstrometryCaptureProcessingStep(CaptureProcessingStepMetadata metadata,
    MeasuredAstrometryCaptureOptions options, CameraAgentRecipeExecutionAdapter adapter, CaptureAstrometryExecutionGate gate,
    CaptureProcessingPersistence? persistence = null)
    : ConfigurableCaptureProcessingStep<MeasuredAstrometryCaptureOptions>(metadata, options), ICaptureProcessingGraphStep,
      IRequiredCaptureProcessingDependencies, IMultiOutputCaptureProcessingGraphStep, IFrozenAuxiliaryInputCaptureProcessingStep
{
    internal const string Alias = "MeasuredAstrometry";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public bool Enabled => true;
    public string RecipeName => BuiltInProcessingRecipes.MeasuredAstrometry;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => MeasuredAstrometrySolutionV1.CurrentSchemaVersion;
    public string? OutputMediaType => MeasuredAstrometryJson.MediaType;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string> { BuiltInProcessingRecipes.ProjectedScene }, new HashSet<string>(ProjectedSceneV1.SupportedSchemaVersions)),
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
            var input = RetainedAstrometryProducts.CreateContext(raw, measured, scene, Options.Catalog!, Options.Clock, Options.PreviousAccepted);
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
    [Required]
    public AstrometryCatalogSnapshotV1? Catalog { get; init; }
    public AstrometricClockFacts Clock { get; init; } = AstrometricClockFacts.NotSupplied;
    public AstrometricFrameAssessment? PreviousAccepted { get; init; }
    public MeasuredAstrometryOptions Recipe { get; init; } = new();

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
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
