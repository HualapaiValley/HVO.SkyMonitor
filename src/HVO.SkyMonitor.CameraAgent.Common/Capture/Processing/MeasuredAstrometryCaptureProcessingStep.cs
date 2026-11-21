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
internal class MeasuredAstrometryCaptureProcessingStep(CaptureProcessingStepMetadata metadata,
    MeasuredAstrometryCaptureOptions options, CameraAgentRecipeExecutionAdapter adapter, CaptureAstrometryExecutionGate gate,
    CaptureProcessingPersistence? persistence = null)
    : ConfigurableCaptureProcessingStep<MeasuredAstrometryCaptureOptions>(metadata, options), ICaptureProcessingGraphStep,
      IRequiredCaptureProcessingDependencies, IMultiOutputCaptureProcessingGraphStep, IFrozenAuxiliaryInputCaptureProcessingStep
{
    internal const string Alias = "MeasuredAstrometry";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected virtual string AssociationRecipe => BuiltInProcessingRecipes.MeasuredStellarAssociations;
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
            new HashSet<string> { AssociationRecipe },
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
        if (AssociationRecipe == BuiltInProcessingRecipes.MeasuredStellarAssociations &&
            (context.Config.CenteredExposure is not null || context.RawCapture?.Manifest.CenteredExposure is not null))
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped("astrometry.centered-measurement-required"));
            return;
        }

        var descriptor = context.ReconstructionDescriptor ?? throw new InvalidDataException("Astrometry requires durable raw admission.");
        if (context.Config.Pipeline.SchemaVersion != CapturePipelineSchemaVersions.ExplicitV2)
            throw new InvalidOperationException("Astrometry requires an explicit v2 graph.");
        if (!context.IsReplayExecution && (CaptureOrdinal(context, descriptor) - 1) % Options.CaptureStride != 0)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped("astrometry.capture-cadence"));
            return;
        }
        var raw = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, context.Artifacts!.Raw,
            descriptor.Artifact.Variant, context.AcquisitionTiming, descriptor);
        var dependencies = context.GetDependencyProducts();
        var scene = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, dependencies.Single(p => ProjectedSceneV1.IsSupportedSchemaVersion(p.SchemaVersion)));
        var measured = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, dependencies.Single(p => p.SchemaVersion == MeasuredStellarAssociationsV1.CurrentSchemaVersion));
        await ValidateMeasuredInputAsync(context, raw, measured, scene, cancellationToken).ConfigureAwait(false);
        if (context.IsReplayExecution)
        {
            ReplayRetainedAssessment(context, raw, measured, scene, cancellationToken);
            return;
        }
        var input = RetainedAstrometryProducts.CreateContext(raw, measured, scene, Options.Catalog!, Options.Clock, Options.PreviousAccepted);
        var newContext = RetainedAstrometryProducts.CreateContextProduct(input, raw, measured, scene, OutputVariant + "-context");
        var frozen = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, newContext);
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

    private void ReplayRetainedAssessment(CaptureProcessingContext context, ProcessingArtifact raw, ProcessingArtifact measured,
        ProcessingArtifact scene, CancellationToken cancellationToken)
    {
        if (context.FrozenAuxiliaryInputFailure == FrozenAuxiliaryInputFailure.Altered)
        {
            context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure("astrometry.frozen-evidence-invalid"));
            return;
        }
        var inputs = context.GetFrozenAuxiliaryInputs();
        var frozen = inputs.SingleOrDefault(p => p.SchemaVersion == MeasuredAstrometryContextV1.CurrentSchemaVersion);
        var retained = inputs.SingleOrDefault(p => p.SchemaVersion == MeasuredAstrometrySolutionV1.CurrentSchemaVersion);
        if (frozen is null || retained is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped("astrometry.frozen-evidence-unavailable"));
            return;
        }

        var contextProduct = RetainedAstrometryProducts.CreateContextProduct(MeasuredAstrometryJson.ParseContext(frozen.Payload),
            raw, measured, scene, OutputVariant + "-context");
        var assessment = RetainedAstrometryProducts.Product(retained);
        if (frozen.Variant != contextProduct.Variant || frozen.ArtifactId != ProcessingIdentity.CreateArtifactId(contextProduct.OutputIdentitySha256) ||
            frozen.RecipeIdentitySha256 != contextProduct.Recipe.IdentitySha256 ||
            frozen.ContentIdentitySha256 != contextProduct.ContentIdentitySha256 ||
            frozen.SourceArtifactIds is not { } lineage || !lineage.SequenceEqual(contextProduct.SourceArtifactIds) ||
            assessment.Variant != OutputVariant || retained.ArtifactId != ProcessingIdentity.CreateArtifactId(assessment.OutputIdentitySha256))
        {
            context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure("astrometry.frozen-evidence-invalid"));
            return;
        }
        var originalOptions = assessment.Recipe.Descriptor.Options.GetProperty("parameters")
            .Deserialize<MeasuredAstrometryOptions>(Json)!;
        var request = RetainedAstrometryProducts.Request(raw, measured, scene, frozen, originalOptions, OutputVariant);
        _ = RetainedAstrometryProducts.Verify(request, assessment, cancellationToken);
        context.RecordExecutionRequest(request);
        context.RecordExecutionRoute(ProcessingNodeExecutionRoute.InProcess);
        var outcome = ProcessingOutcome.Produced(contextProduct, assessment);
        context.RecordExecutionOutcome(outcome);
        context.AddProcessingOutcome(outcome);
    }

    protected virtual ValueTask ValidateMeasuredInputAsync(CaptureProcessingContext context, ProcessingArtifact raw,
        ProcessingArtifact association, ProcessingArtifact scene, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected virtual long CaptureOrdinal(CaptureProcessingContext context, ReconstructionDescriptor descriptor) =>
        descriptor.Capture.CaptureSequence;
}

internal sealed class ExposureAstrometryCaptureProcessingStep(CaptureProcessingStepMetadata metadata,
    MeasuredAstrometryCaptureOptions options, CameraAgentRecipeExecutionAdapter adapter, CaptureAstrometryExecutionGate gate,
    CaptureProcessingPersistence persistence, CaptureExposureMeasurementReader reader)
    : MeasuredAstrometryCaptureProcessingStep(metadata, options, adapter, gate, persistence)
{
    internal const string CenteredAlias = CaptureAnalysisOptionDescriptors.CenteredAstrometryAlias;
    protected override string AssociationRecipe => ExposureAssociationProducts.RecipeName;

    protected override long CaptureOrdinal(CaptureProcessingContext context, ReconstructionDescriptor descriptor) =>
        context.RawCapture?.Manifest.CenteredExposure?.Source.Ordinal ??
        throw new InvalidDataException("Centered astrometry requires an admitted attempt ordinal.");

    protected override async ValueTask ValidateMeasuredInputAsync(CaptureProcessingContext context, ProcessingArtifact raw,
        ProcessingArtifact association, ProcessingArtifact scene, CancellationToken cancellationToken)
    {
        if (context.GetDependencyProducerStepId(scene.ArtifactId) != ExposureCatalogPredictionProducts.StepAlias)
            throw new InvalidDataException("Centered astrometry requires the fixed exposure catalog prediction.");
        if (association.SourceArtifactIds is not { Count: 3 } lineage)
            throw new InvalidDataException("Centered astrometry requires retained measurement lineage.");
        var (retained, measurement) = await reader.ReadDependencyAsync(context, lineage[1], cancellationToken).ConfigureAwait(false);
        ExposureAssociationProducts.VerifyMeasurement(association, raw, retained, measurement, scene);
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
