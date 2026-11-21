using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal class ProjectedSceneCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    ProjectedSceneCaptureProcessingStepOptions options,
    IProjectedSceneStagingStore stagingStore,
    IProjectedSceneStagingReader stagingReader,
    CameraAgentRecipeExecutionAdapter adapter,
    CaptureMaintenanceGate? maintenance = null,
    IServiceProvider? services = null,
    CenteredExposureSceneProvider? centeredScenes = null)
    : ConfigurableCaptureProcessingStep<ProjectedSceneCaptureProcessingStepOptions>(metadata, options),
       IDescriptorOnlyCaptureProcessingStep, ICaptureProcessingGraphStep, IDurableCaptureProcessingPostCommit,
       IFrozenAuxiliaryInputCaptureProcessingStep
{
    internal const string ExposurePredictionAlias = ExposureCatalogPredictionProducts.StepAlias;
    private const string StageInputName = "virtual-render-scene";
    private const string FrozenInputName = "frozen-projected-scene";
    internal virtual bool IsExposurePrediction => false;

    public bool Enabled => true;
    internal virtual bool IsAstrometryPrediction => false;
    public string RecipeName => IsAstrometryPrediction
        ? BuiltInProcessingRecipes.AstrometryPrediction : BuiltInProcessingRecipes.ProjectedScene;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => IsExposurePrediction ? ExposureCatalogPredictionProducts.Variant : Options.OutputVariant;
    // The producer declaration keeps its v1 family root; expanded consumer capabilities can change plan identity.
    // Actual products carry v3 for a curated collection, v2 for resolved footprints, otherwise v1.
    public string? OutputSchemaVersion => ProjectedSceneV1.CurrentSchemaVersion;
    public string? OutputMediaType => StructuredProcessingProductContracts.ProjectedSceneMediaType;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
        => ((IDescriptorOnlyCaptureProcessingStep)this).ProcessAsync(
            new CaptureDescriptorProcessingContext(context), cancellationToken);

    public async ValueTask ProcessAsync(CaptureDescriptorProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ReconstructionDescriptor is not { } descriptor)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingProjectedScene));
            return;
        }

        var descriptorSha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor);
        var source = new ProjectedSceneSource(
            descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId, descriptorSha256);
        ProjectedSceneV1? scene;
        var provenance = context.SceneProvenance;
        var virtualStage = string.Equals(descriptor.Artifact.SourceId, "VirtualSky", StringComparison.Ordinal) &&
            (provenance?.RequiresProjectedScene == true || provenance?.ProjectedSceneStageKey is not null);
        var independentPrediction = IsAstrometryPrediction || Options.IndependentPrediction && !virtualStage;
        if (context.IsReplayExecution)
        {
            // Archived replay never consults, recreates, or recomputes the transient stage: it consumes the committed
            // projected-scene product that submission pinned, or fails closed without dispatching the recipe.
            if (!TryReadFrozenScene(context, source, out scene, out var failureReason))
            {
                context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure(failureReason));
                return;
            }
            // The raw manifest declares the canonical render scene schema, not the supplemental prediction schema.
            if (!IsAstrometryPrediction && !IsExposurePrediction && provenance?.ProjectedSceneSchemaVersion is { } declaredSchema &&
                !string.Equals(declaredSchema, scene.SchemaVersion, StringComparison.Ordinal))
            {
                context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidProjectedScene));
                return;
            }
            if ((IsAstrometryPrediction && scene.Kind != ProjectedSceneKind.Predicted) ||
                (!IsAstrometryPrediction && !IsExposurePrediction && virtualStage && scene.Kind != ProjectedSceneKind.VirtualRenderAuthoritative))
            {
                context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidProjectedScene));
                return;
            }
            context.RecordCanonicalInput(FrozenInputName, scene.SchemaVersion, scene.SceneIdentitySha256);
        }
        else if (IsExposurePrediction)
        {
            var facts = context.CenteredExposure is { } binding && centeredScenes is not null
                ? await centeredScenes.ReadPredictionFactsAsync(new(binding.Source.Camera, binding.Source.RunId,
                    descriptor.Capture.CaptureId), cancellationToken).ConfigureAwait(false) : null;
            scene = facts is not null
                ? await centeredScenes!.ResolvePredictionsAsync(facts, cancellationToken).ConfigureAwait(false) : null;
            if (scene is null)
            {
                context.AddProcessingOutcome(ProcessingOutcome.Skipped("scene.frozen-prediction-input-unavailable"));
                return;
            }
            context.RecordCanonicalInput("centered-catalog-prediction", scene.SchemaVersion, scene.SceneIdentitySha256);
        }
        else if (independentPrediction && context.CenteredExposure is null)
        {
            var catalog = services?.GetService<ICelestialCatalog>();
            var locations = services?.GetService<IDeploymentLocationStore>();
            // Predict independently from capture-time rig/site and installed catalog. Never relabel virtual truth.
            if (catalog is not ICelestialCatalogMetadataSource metadata || locations is null || descriptor.Location is not { } location)
                throw new InvalidDataException("Independent prediction requires a verified catalog and capture location history.");
            var site = locations.Resolve(location, descriptor.Timing.ExposureStartedUtc);
            var projection = RigProjectionContextFactory.Create(context.Config.Rig);
            if (projection.WidthPixels != descriptor.Layout.Width || projection.HeightPixels != descriptor.Layout.Height)
                throw new InvalidDataException("Independent prediction requires the source output geometry.");
            var start = descriptor.Timing.ExposureStartedUtc;
            var end = ProcessingArtifact.ResolveObservationEndedUtc(start, descriptor.Timing.ExposureEndedUtc, descriptor.Controls.EffectiveExposure);
            var predictionRequest = new VisibleSceneRequest(start + TimeSpan.FromTicks((end - start).Ticks / 2),
                new ObserverLocation(site.LatitudeDegrees, site.LongitudeDegrees, site.ElevationMeters), projection,
                new CatalogQuery(Options.MaximumMagnitude, Options.MaximumResults), metadata.Metadata,
                horizonPolicy: HorizonPolicy.GeometricHorizon, projectionVersion: context.Config.Rig.Optics.CalibrationVersion,
                algorithmVersion: Options.AstronomyAlgorithmVersion);
            var predicted = await new VisibleSceneBuilder(catalog).BuildAsync(predictionRequest, cancellationToken).ConfigureAwait(false);
            scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, predicted,
                ProjectedSceneImageTransformV1.Identity(descriptor.Layout.Width, descriptor.Layout.Height), source,
                context.Config.Rig.Optics.CalibrationVersion, context.Config.Rig.Optics.CalibrationVersion);
        }
        else if (!independentPrediction && provenance is
        {
            ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
            ProjectedSceneStageKey: { Length: 64 } stageKey,
            SceneId: { Length: 64 } sceneId
        })
        {
            var staged = await stagingReader.ReadAsync(stageKey, cancellationToken).ConfigureAwait(false);
            if (staged is null)
            {
                context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(ProcessingReasonCodes.MissingProjectedScene));
                return;
            }
            if (!string.Equals(staged.SceneId, sceneId, StringComparison.Ordinal))
                throw new InvalidDataException("Projected-scene stage does not match capture scene evidence.");
            if (staged.StageSceneIdentitySha256 is { } stagedIdentity &&
                !string.Equals(stagedIdentity, provenance.ProjectedSceneStageIdentitySha256, StringComparison.Ordinal))
                throw new InvalidDataException("Projected-scene stage semantic identity does not match capture evidence.");
            if (staged.Layout is { } layout && staged.Layout != descriptor.Layout ||
                staged.RigProfileSha256 is { } rigSha256 && !string.Equals(
                    rigSha256, descriptor.Profiles.Rig.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Projected-scene stage does not match capture layout or rig evidence.");
            var kind = staged.IntendedKind ??
                (string.Equals(descriptor.Artifact.SourceId, "VirtualSky", StringComparison.Ordinal)
                    ? ProjectedSceneKind.VirtualRenderAuthoritative
                    : ProjectedSceneKind.Predicted);
            if (virtualStage && kind != ProjectedSceneKind.VirtualRenderAuthoritative)
                throw new InvalidDataException("VirtualSky capture evidence requires its authoritative staged scene.");
            scene = staged.Bind(source, kind);
            if (provenance.ProjectedSceneSchemaVersion is { } declaredSchema &&
                !string.Equals(declaredSchema, scene.SchemaVersion, StringComparison.Ordinal))
                throw new InvalidDataException("Projected-scene stage schema does not match capture scene evidence.");
            context.RecordCanonicalInput(StageInputName, StagedProjectedSceneDocument.CurrentSchemaVersion,
                staged.StageIdentitySha256);
        }
        else if (context.CenteredExposure is { } centered)
        {
            var sceneProvider = centeredScenes ?? services?.GetService<CenteredExposureSceneProvider>();
            scene = sceneProvider is null ? null : await sceneProvider.ResolvePresentationAsync(
                centered, descriptor, Options,
                independentPrediction ? null : provenance?.VirtualExposure?.CelestialMidpointUtc,
                cancellationToken).ConfigureAwait(false);
            if (scene is null)
            {
                context.AddProcessingOutcome(ProcessingOutcome.Skipped("scene.frozen-input-unavailable"));
                return;
            }

            context.RecordCanonicalInput("centered-predicted-scene", scene.SchemaVersion, scene.SceneIdentitySha256);
        }
        else
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingProjectedScene));
            return;
        }

        var raw = new ProcessingArtifact(
            descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
            ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
            descriptor.Artifact.MediaType, descriptor.Layout, ReadOnlyMemory<byte>.Empty,
            descriptor.Artifact.CreatedUtc, descriptor.Controls.EffectiveExposure,
            CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor),
            CaptureSequence: descriptor.Capture.CaptureSequence,
            ObservationStartedUtc: descriptor.Timing.ExposureStartedUtc,
            ObservationEndedUtc: descriptor.Timing.ExposureEndedUtc)
        {
            CaptureId = descriptor.Capture.CaptureId,
            DescriptorIdentitySha256 = descriptorSha256
        };
        var request = ExposureCatalogPredictionProducts.Request(raw, scene) with { RecipeName = RecipeName, OutputVariant = OutputVariant };
        var outcome = await context.ExecuteAsync(adapter, request, cancellationToken).ConfigureAwait(false);
        if (context.IsReplayExecution && (IsExposurePrediction || IsAstrometryPrediction || provenance?.RequiresProjectedScene == true) &&
            outcome.Products.Any(product => !context.FrozenAuxiliaryInputs.Any(input =>
                input.ArtifactId == ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256))))
        {
            // The frozen geometry artifact is immutable across replay and configuration changes.
            context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure(ProcessingReasonCodes.InvalidProjectedScene));
            return;
        }
        context.AddProcessingOutcome(outcome);
    }

    private bool TryReadFrozenScene(
        CaptureDescriptorProcessingContext context,
        ProjectedSceneSource source,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ProjectedSceneV1? scene,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? failureReason)
    {
        scene = null;
        failureReason = null;
        if (context.FrozenAuxiliaryInputFailure is { } failure)
        {
            failureReason = failure == FrozenAuxiliaryInputFailure.Missing
                ? ProcessingReasonCodes.MissingProjectedScene
                : ProcessingReasonCodes.InvalidProjectedScene;
            return false;
        }
        var candidates = context.FrozenAuxiliaryInputs.Where(static input =>
            input.Role == FrameArtifactRole.Metadata &&
            input.ProductKind == ProcessingProductKind.Metadata &&
            ProjectedSceneV1.IsSupportedSchemaVersion(input.SchemaVersion) &&
            string.Equals(input.MediaType, StructuredProcessingProductContracts.ProjectedSceneMediaType, StringComparison.Ordinal)).ToArray();
        if (candidates.Length == 0)
        {
            failureReason = ProcessingReasonCodes.MissingProjectedScene;
            return false;
        }
        if (candidates.Length > 1)
        {
            failureReason = ProcessingReasonCodes.InvalidProjectedScene;
            return false;
        }
        var frozen = candidates[0];
        if ((IsExposurePrediction || IsAstrometryPrediction || context.SceneProvenance?.RequiresProjectedScene == true) &&
            !string.Equals(frozen.Variant, OutputVariant, StringComparison.Ordinal))
        {
            failureReason = ProcessingReasonCodes.InvalidProjectedScene;
            return false;
        }
        var parsed = ProjectedSceneJson.Parse(frozen.Payload);
        if (!parsed.IsValid || parsed.Scene is not { } candidate ||
            !string.Equals(candidate.SchemaVersion, frozen.SchemaVersion, StringComparison.Ordinal))
        {
            failureReason = ProcessingReasonCodes.InvalidProjectedScene;
            return false;
        }
        if (candidate.Source != source || IsExposurePrediction && candidate.Kind != ProjectedSceneKind.Predicted)
        {
            failureReason = ProcessingReasonCodes.ProjectedSceneSourceMismatch;
            return false;
        }
        if (frozen.ContentIdentitySha256 is { } contentIdentity &&
            !string.Equals(contentIdentity, candidate.SceneIdentitySha256, StringComparison.Ordinal))
        {
            failureReason = ProcessingReasonCodes.InvalidProjectedScene;
            return false;
        }
        scene = candidate;
        return true;
    }

    public async ValueTask OnCommittedAsync(CaptureDescriptorProcessingContext context, CancellationToken cancellationToken)
    {
        if (IsAstrometryPrediction || IsExposurePrediction || context.SceneProvenance is not
            {
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: { Length: 64 } stageKey
            }) return;
        // A snapshot restore can rewind this node, and its re-run reads the stage again, so the stage is removed only
        // under the capture maintenance barrier; a held removal runs once capture admission opens, and if a restart
        // forgets it, the node's durable completion releases the stage to stage reconciliation instead.
        using var lease = maintenance?.TryEnterIfInitialized();
        if (maintenance is not null && lease is null)
        {
            maintenance.DeferUntilRunning(token => RemoveStageAsync(stageKey, token));
            return;
        }
        await RemoveStageAsync(stageKey, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask RemoveStageAsync(string stageKey, CancellationToken cancellationToken)
    {
        await stagingStore.MarkCompletedAsync(stageKey, cancellationToken).ConfigureAwait(false);
        await stagingStore.DeleteCompletedAsync(stageKey, cancellationToken).ConfigureAwait(false);
    }

}

internal sealed class ExposureCatalogPredictionCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata, ProjectedSceneCaptureProcessingStepOptions options,
    IProjectedSceneStagingStore stagingStore, IProjectedSceneStagingReader stagingReader,
    CameraAgentRecipeExecutionAdapter adapter, CenteredExposureSceneProvider centeredScenes)
    : ProjectedSceneCaptureProcessingStep(metadata, options, stagingStore, stagingReader, adapter, centeredScenes: centeredScenes)
{
    internal override bool IsExposurePrediction => true;
}

internal sealed class ProjectedSceneCaptureProcessingStepOptions : IValidatableObject
{
    internal const string DefaultAstronomyAlgorithmVersion = "visible-scene-iau1976-constellation-v2";

    [Required, MinLength(1), MaxLength(128)]
    public string OutputVariant { get; init; } = "projected-scene-v1";

    // Opt-in behavior must not change the effective identity of existing profiles.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool IndependentPrediction { get; init; }

    [Range(-30d, 30d)]
    public double MaximumMagnitude { get; init; } = 6.5;

    [Range(1, ProjectedSceneJson.MaximumObjectCount)]
    public int MaximumResults { get; init; } = 2000;

    [Required, MinLength(1), MaxLength(128)]
    public string AstronomyAlgorithmVersion { get; init; } = DefaultAstronomyAlgorithmVersion;

    public IReadOnlyList<string> ConstellationIds { get; init; } = [];
    public IReadOnlyList<SolarSystemBody> SolarSystemBodies { get; init; } = [];
    public bool IncludeConstellationEndpointStars { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public CuratedDeepSkyOptions? CuratedAnnotations { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool DeepSkyEnabled { get; init; }
    public bool DeepSkyNgc { get; init; } = true;
    public bool DeepSkyMessier { get; init; } = true;
    public bool DeepSkyIc { get; init; } = true;
    [Range(-30d, 30d)] public double DeepSkyMaximumMagnitude { get; init; } = 12;
    public bool DeepSkyIncludeUnknownMagnitude { get; init; } = true;
    [Range(0d, 1200d)] public double DeepSkyMinimumSizeArcminutes { get; init; }
    [Range(0d, 1200d)] public double DeepSkyMaximumSizeArcminutes { get; init; } = 1200;
    public bool DeepSkyIncludeUnknownSize { get; init; } = true;
    public OpenNgcDeepSkyOptions DeepSkySelection() => new(DeepSkyEnabled, DeepSkyNgc, DeepSkyMessier, DeepSkyIc,
        DeepSkyMaximumMagnitude, DeepSkyIncludeUnknownMagnitude, DeepSkyMinimumSizeArcminutes,
        DeepSkyMaximumSizeArcminutes, DeepSkyIncludeUnknownSize);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IndependentPrediction && (ConstellationIds.Count > 0 || SolarSystemBodies.Count > 0 || DeepSkyEnabled || CuratedAnnotations is { Enabled: true }))
            yield return new ValidationResult("Independent astrometry prediction supports catalog stars only.");
        _ = DeepSkySelection().ToSelection();
        if (DeepSkyEnabled && CuratedAnnotations is { Enabled: true })
            yield return new ValidationResult("Choose either the general deep-sky query or the legacy curated preset.");
        if (CuratedAnnotations is { } curated) _ = curated.ToSelection();
        if (ConstellationIds.Count > 128 || ConstellationIds.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 16))
            yield return new ValidationResult("Constellation selections exceed their bounds.", [nameof(ConstellationIds)]);
        if (SolarSystemBodies.Count > Enum.GetValues<SolarSystemBody>().Length || SolarSystemBodies.Any(static value => !Enum.IsDefined(value)))
            yield return new ValidationResult("Solar-system selections are invalid.", [nameof(SolarSystemBodies)]);
    }
}
