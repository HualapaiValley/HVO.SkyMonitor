using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ProjectedSceneCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    ProjectedSceneCaptureProcessingStepOptions options,
    IProjectedSceneStagingStore stagingStore,
    IProjectedSceneStagingReader stagingReader,
    CameraAgentRecipeExecutionAdapter adapter)
    : ConfigurableCaptureProcessingStep<ProjectedSceneCaptureProcessingStepOptions>(metadata, options),
       IDescriptorOnlyCaptureProcessingStep, ICaptureProcessingGraphStep, IDurableCaptureProcessingPostCommit,
       IFrozenAuxiliaryInputCaptureProcessingStep
{
    private const string StageInputName = "virtual-render-scene";
    private const string FrozenInputName = "frozen-projected-scene";

    public bool Enabled => true;
    public string RecipeName => BuiltInProcessingRecipes.ProjectedScene;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
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
        if (context.IsReplayExecution)
        {
            // Archived replay never consults, recreates, or recomputes the transient stage: it consumes the committed
            // projected-scene product that submission pinned, or fails closed without dispatching the recipe.
            if (TryReadFrozenScene(context, source, out scene, out var failureReason))
            {
                context.RecordCanonicalInput(FrozenInputName, ProjectedSceneV1.CurrentSchemaVersion, scene.SceneIdentitySha256);
            }
            else
            {
                context.AddProcessingOutcome(ProcessingOutcome.TerminalFailure(failureReason));
                return;
            }
        }
        else if (provenance is
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
            scene = staged.Bind(source, kind);
            context.RecordCanonicalInput(StageInputName, StagedProjectedSceneDocument.CurrentSchemaVersion,
                staged.StageIdentitySha256);
        }
        else
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingProjectedScene));
            return;
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

    private static bool TryReadFrozenScene(
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
            string.Equals(input.SchemaVersion, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal) &&
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
        var parsed = ProjectedSceneJson.Parse(frozen.Payload);
        if (!parsed.IsValid || parsed.Scene is not { } candidate)
        {
            failureReason = ProcessingReasonCodes.InvalidProjectedScene;
            return false;
        }
        if (candidate.Source != source)
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
        if (context.SceneProvenance is not
            {
                ProjectedSceneStageSchemaVersion: StagedProjectedSceneDocument.CurrentSchemaVersion,
                ProjectedSceneStageKey: { Length: 64 } stageKey
            }) return;
        await stagingStore.MarkCompletedAsync(stageKey, cancellationToken).ConfigureAwait(false);
        await stagingStore.DeleteCompletedAsync(stageKey, cancellationToken).ConfigureAwait(false);
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
