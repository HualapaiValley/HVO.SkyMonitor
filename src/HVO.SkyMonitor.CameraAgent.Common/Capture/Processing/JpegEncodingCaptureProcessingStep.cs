using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class JpegEncodingCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    JpegEncodingProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter)
    : ConfigurableCaptureProcessingStep<JpegEncodingProcessingStepOptions>(metadata, options),
      ICaptureProcessingGraphStep
{
    public bool Enabled => true;

    public string RecipeName => BuiltInProcessingRecipes.JpegEncoding;

    public FrameArtifactRole OutputRole => Options.SourceRole ?? FrameArtifactRole.AnnotatedPreview;

    public string OutputVariant => Options.OutputVariant;

    public string? OutputMediaType => "image/jpeg";

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { options.SourceRole ?? FrameArtifactRole.AnnotatedPreview };

    public override async ValueTask ProcessAsync(
        CaptureProcessingContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var source = context.GetDependencyArtifacts().SingleOrDefault(
            artifact => artifact.Role == OutputRole);
        if (source is null)
        {
            return;
        }

        var sourceProduct = context.GetProcessingProduct(source.ArtifactId)
            ?? throw new InvalidOperationException("JPEG encoding requires a canonical packed processing product.");
        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config,
            source,
            sourceProduct.Variant,
            context.AcquisitionTiming,
            context.ReconstructionDescriptor,
            sourceProduct) with
        {
            RecipeIdentitySha256 = sourceProduct.Recipe.IdentitySha256
        };
        var outcome = await adapter.ExecuteAsync(context, new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.JpegEncoding,
            JsonSerializer.SerializeToElement(new JpegEncodingOptions(
                Options.JpegQuality,
                Options.MaximumDimension)),
            ProcessingInputSelector.RecipeResult(
                source.Role,
                sourceProduct.Variant,
                sourceProduct.Recipe.IdentitySha256),
            [input],
            Options.OutputVariant,
            InputArtifactId: input.ArtifactId), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
    }
}

public sealed class JpegEncodingProcessingStepOptions : IValidatableObject
{
    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "annotated-final-jpeg";

    [Range(1, 100)]
    public int JpegQuality { get; init; } = JpegImageCodec.DefaultQuality;

    [Range(1, 16384)]
    public int? MaximumDimension { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FrameArtifactRole? SourceRole { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SourceRole is not (null or FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview))
            yield return new ValidationResult("JPEG encoding requires a Preview or AnnotatedPreview source.", [nameof(SourceRole)]);
    }
}
