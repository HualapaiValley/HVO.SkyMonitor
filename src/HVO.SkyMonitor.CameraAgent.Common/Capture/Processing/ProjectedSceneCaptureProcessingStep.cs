using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ProjectedSceneCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    ProjectedSceneCaptureProcessingStepOptions options,
    IProjectedSceneStagingStore stagingStore,
    IProjectedSceneStagingReader stagingReader,
    CameraAgentRecipeExecutionAdapter adapter,
    IServiceProvider serviceProvider,
    ICelestialCatalog? catalog = null,
    IConstellationTopology? topology = null,
    IPlanetEphemeris? ephemeris = null)
    : ConfigurableCaptureProcessingStep<ProjectedSceneCaptureProcessingStepOptions>(metadata, options),
      IDescriptorOnlyCaptureProcessingStep, ICaptureProcessingGraphStep, IDurableCaptureProcessingPostCommit
{
    private const string StageInputName = "virtual-render-scene";
    private const string PredictionInputName = "predicted-scene-facts";

    public bool Enabled => true;
    public string RecipeName => BuiltInProcessingRecipes.ProjectedScene;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
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
        if (string.Equals(descriptor.Artifact.SourceId, "VirtualSky", StringComparison.Ordinal))
        {
            if (provenance is not
                {
                    ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                    ProjectedSceneStageKey: { Length: 64 } stageKey,
                    SceneId: { Length: 64 } sceneId
                })
            {
                context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingProjectedScene));
                return;
            }
            var staged = await stagingReader.ReadAsync(stageKey, cancellationToken).ConfigureAwait(false);
            if (staged is null)
            {
                context.AddProcessingOutcome(ProcessingOutcome.RetryableFailure(ProcessingReasonCodes.MissingProjectedScene));
                return;
            }
            if (!string.Equals(staged.SceneId, sceneId, StringComparison.Ordinal))
                throw new InvalidDataException("Projected-scene stage does not match capture scene evidence.");
            scene = staged.Bind(source, ProjectedSceneKind.VirtualRenderAuthoritative);
            context.RecordCanonicalInput(StageInputName, StagedProjectedSceneDocument.CurrentSchemaVersion,
                staged.StageIdentitySha256);
        }
        else
        {
            scene = await BuildPredictedAsync(context.Config, descriptor, source, cancellationToken).ConfigureAwait(false);
            if (scene is null)
            {
                context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingProjectedScene));
                return;
            }
            context.RecordCanonicalInput(PredictionInputName, "projected-scene-prediction-facts-v1",
                scene.SceneIdentitySha256);
        }

        var payload = ProjectedSceneJson.Serialize(scene);
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
        var auxiliary = new ProcessingAuxiliaryInput(
            "scene", ProcessingAuxiliaryInputKind.CanonicalJson, SchemaVersion: ProjectedSceneV1.CurrentSchemaVersion,
            IdentitySha256: scene.SceneIdentitySha256, Payload: payload)
        {
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
        };
        var outcome = await context.ExecuteAsync(adapter, new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ProjectedScene,
            JsonSerializer.SerializeToElement(new Dictionary<string, object>()),
            ProcessingInputSelector.Raw(descriptor.Artifact.Variant), [raw], Options.OutputVariant,
            AuxiliaryInputs: [auxiliary], InputArtifactId: raw.ArtifactId), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
    }

    public async ValueTask OnCommittedAsync(CaptureDescriptorProcessingContext context, CancellationToken cancellationToken)
    {
        if (context.SceneProvenance is not
            {
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: { Length: 64 } stageKey
            }) return;
        await stagingStore.MarkCompletedAsync(stageKey, cancellationToken).ConfigureAwait(false);
        await stagingStore.DeleteCompletedAsync(stageKey, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ProjectedSceneV1?> BuildPredictedAsync(
        CameraModuleConfig config,
        ReconstructionDescriptor descriptor,
        ProjectedSceneSource source,
        CancellationToken cancellationToken)
    {
        if (descriptor.Location is not { } location || config.DeploymentLocationRedacted is false ||
            !string.Equals(CameraRigProfileIdentity.ComputeSha256(config.Rig), descriptor.Profiles.Rig.Sha256,
                StringComparison.OrdinalIgnoreCase))
            return null;
        var locationStore = serviceProvider.GetService(typeof(IDeploymentLocationStore)) as IDeploymentLocationStore;
        if (locationStore is null) return null;
        DeploymentLocationSnapshot resolved;
        try
        {
            resolved = locationStore.Resolve(location, descriptor.Timing.ExposureStartedUtc);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        var projection = RigProjectionContextFactory.Create(config.Rig);
        if (projection.WidthPixels != descriptor.Layout.Width || projection.HeightPixels != descriptor.Layout.Height)
            return null;
        var effectiveUtc = descriptor.Timing.ExposureStartedUtc + TimeSpan.FromTicks(descriptor.Controls.EffectiveExposure.Ticks / 2);
        if (catalog is null) return null;
        var metadata = (catalog as ICelestialCatalogMetadataSource)?.Metadata;
        if (metadata is null) return null;
        var request = new VisibleSceneRequest(
            effectiveUtc, new ObserverLocation(resolved.LatitudeDegrees, resolved.LongitudeDegrees, resolved.ElevationMeters),
            projection, new CatalogQuery(Options.MaximumMagnitude, Options.MaximumResults), metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion,
            algorithmVersion: Options.AstronomyAlgorithmVersion,
            constellationIds: Options.ConstellationIds,
            solarSystemBodies: Options.SolarSystemBodies,
            includeConstellationEndpointStars: Options.IncludeConstellationEndpointStars);
        var visible = await new VisibleSceneBuilder(catalog, topology, ephemeris)
            .BuildAsync(request, cancellationToken).ConfigureAwait(false);
        return ProjectedSceneJson.Create(
            ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(descriptor.Layout.Width, descriptor.Layout.Height), source,
            config.Rig.Optics.CalibrationVersion, config.Rig.Optics.CalibrationVersion);
    }
}

internal sealed class ProjectedSceneCaptureProcessingStepOptions : IValidatableObject
{
    [Required, MinLength(1), MaxLength(128)]
    public string OutputVariant { get; init; } = "projected-scene-v1";

    [Range(-30, 30)]
    public double MaximumMagnitude { get; init; } = 6.5;

    [Range(1, ProjectedSceneJson.MaximumObjectCount)]
    public int MaximumResults { get; init; } = 2000;

    [Required, MinLength(1), MaxLength(128)]
    public string AstronomyAlgorithmVersion { get; init; } = "visible-scene-iau1976-constellation-v2";

    public IReadOnlyList<string> ConstellationIds { get; init; } = [];
    public IReadOnlyList<SolarSystemBody> SolarSystemBodies { get; init; } = [];
    public bool IncludeConstellationEndpointStars { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (ConstellationIds.Count > 128 || ConstellationIds.Any(static value => string.IsNullOrWhiteSpace(value) || value.Length > 16))
            yield return new ValidationResult("Constellation selections exceed their bounds.", [nameof(ConstellationIds)]);
        if (SolarSystemBodies.Count > Enum.GetValues<SolarSystemBody>().Length || SolarSystemBodies.Any(static value => !Enum.IsDefined(value)))
            yield return new ValidationResult("Solar-system selections are invalid.", [nameof(SolarSystemBodies)]);
    }
}
