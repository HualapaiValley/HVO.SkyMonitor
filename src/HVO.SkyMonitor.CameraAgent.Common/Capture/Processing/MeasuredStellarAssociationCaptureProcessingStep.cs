using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>
/// Measures the linear raw capture inside the windows predicted by its own committed projected scene. The scene product
/// is bound as canonical JSON exactly as LogicHost binds its resolved scene, so both hosts derive the same identity.
/// </summary>
internal sealed class MeasuredStellarAssociationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    MeasuredStellarAssociationProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter)
    : ConfigurableCaptureProcessingStep<MeasuredStellarAssociationProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep, IRequiredCaptureProcessingDependencies
{
    public bool Enabled => Options.Enabled;
    public string RecipeName => BuiltInProcessingRecipes.MeasuredStellarAssociations;
    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;
    public string OutputVariant => Options.OutputVariant;
    public string? OutputSchemaVersion => MeasuredStellarAssociationsV1.CurrentSchemaVersion;
    public string? OutputMediaType => StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType;
    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata };
    public IReadOnlyList<CaptureProcessingDependencyRequirement> DependencyRequirements =>
    [
        new(new HashSet<FrameArtifactRole> { FrameArtifactRole.Metadata },
            new HashSet<string>(StringComparer.Ordinal) { BuiltInProcessingRecipes.ProjectedScene },
            new HashSet<string>(StringComparer.Ordinal) { ProjectedSceneV1.CurrentSchemaVersion })
    ];

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts?.Raw is not { } raw)
        {
            return;
        }
        var sceneProduct = context.GetDependencyProducts().SingleOrDefault(static product =>
            product.Kind == ProcessingProductKind.Metadata &&
            string.Equals(product.SchemaVersion, ProjectedSceneV1.CurrentSchemaVersion, StringComparison.Ordinal));
        if (sceneProduct is null)
        {
            context.AddProcessingOutcome(ProcessingOutcome.Skipped(ProcessingReasonCodes.MissingProjectedScene));
            return;
        }
        var descriptor = context.ReconstructionDescriptor;
        var source = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            raw,
            descriptor?.Artifact.Variant ?? "source",
            context.AcquisitionTiming,
            descriptor);
        var payload = sceneProduct.Payload.ToArray();
        var auxiliary = new ProcessingAuxiliaryInput(
            "scene",
            ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: ProjectedSceneV1.CurrentSchemaVersion,
            IdentitySha256: sceneProduct.ContentIdentitySha256,
            Payload: payload)
        {
            ChecksumSha256 = ProcessingIdentity.ComputePayloadSha256(payload)
        };
        var outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.MeasuredStellarAssociations,
            JsonSerializer.SerializeToElement(new MeasuredStellarAssociationRecipeOptions(
                Options.Association, Options.LabelPolicy)),
            ProcessingInputSelector.Raw(source.Variant),
            [source],
            Options.OutputVariant,
            AuxiliaryInputs: [auxiliary],
            InputArtifactId: source.ArtifactId), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
    }
}

public sealed class MeasuredStellarAssociationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Required(AllowEmptyStrings = false), MaxLength(128)]
    public string OutputVariant { get; init; } = "measured-stellar-associations-v1";

    public MeasuredStellarAssociationSettingsV1? Association { get; init; }

    public StellarLabelPolicySettingsV1? LabelPolicy { get; init; }
}
