using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class ImageQualityCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    ImageQualityProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter)
    : ConfigurableCaptureProcessingStep<ImageQualityProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep,
      ILegacyCaptureProcessingPlanContract
{
    internal const string LegacyPlanContract = "image-quality-plan-v1";
    public string LegacyPlanContractId => LegacyPlanContract;
    public bool Enabled => Options.Enabled;

    public string RecipeName => BuiltInProcessingRecipes.ImageQuality;

    public FrameArtifactRole OutputRole => FrameArtifactRole.Metadata;

    public string OutputVariant => Options.OutputVariant;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } = new HashSet<FrameArtifactRole>
    {
        FrameArtifactRole.Raw,
        FrameArtifactRole.Calibrated,
        FrameArtifactRole.Combined,
        FrameArtifactRole.Preview,
        FrameArtifactRole.AnnotatedPreview
    };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sourceArtifact = context.GetDependencyArtifacts()
            .SingleOrDefault(artifact => AcceptedInputRoles.Contains(artifact.Role));
        if (sourceArtifact is null && !context.HasDeclaredDependencies)
        {
            sourceArtifact = context.Artifacts?.Raw;
        }
        if (!Options.Enabled || sourceArtifact is null)
        {
            return;
        }

        var sourceProduct = context.GetProcessingProduct(sourceArtifact.ArtifactId);
        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            sourceArtifact,
            sourceProduct?.Variant ?? "source",
            context.AcquisitionTiming,
            context.ReconstructionDescriptor,
            sourceProduct);
        var outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.ImageQuality,
            JsonSerializer.SerializeToElement(new { }),
            CameraAgentRecipeExecutionAdapter.CreateSelector(sourceArtifact, sourceProduct, input.Variant),
            [input],
            Options.OutputVariant,
            InputArtifactId: input.ArtifactId), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
    }
}

public sealed class ImageQualityProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "image-quality-v1";
}
