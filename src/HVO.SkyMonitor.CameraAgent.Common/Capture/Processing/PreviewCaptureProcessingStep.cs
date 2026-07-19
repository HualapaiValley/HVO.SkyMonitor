using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates a deterministic Mono8 display preview while retaining the source raw artifact.</summary>
internal sealed class PreviewCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    PreviewProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter) : ConfigurableCaptureProcessingStep<PreviewProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;

    public string RecipeName => BuiltInProcessingRecipes.EncodedPreview;

    public FrameArtifactRole OutputRole => FrameArtifactRole.Preview;

    public string OutputVariant => Options.OutputVariant;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw, FrameArtifactRole.Calibrated, FrameArtifactRole.Combined };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var sourceArtifact = context.GetDependencyArtifacts()
            .SingleOrDefault(artifact => AcceptedInputRoles.Contains(artifact.Role));
        if (sourceArtifact is null && !context.HasDeclaredDependencies)
        {
            sourceArtifact = context.Artifacts?.Raw;
        }
        if (!Options.Enabled || sourceArtifact is null ||
            sourceArtifact.Frame is not { } source ||
            source.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 or CameraPixelFormat.Rgb24))
        {
            return;
        }

        var sourceProduct = context.GetProcessingProduct(sourceArtifact.ArtifactId);
        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(
            context.Config, sourceArtifact, sourceProduct?.Variant ?? "source", context.AcquisitionTiming,
            context.ReconstructionDescriptor, sourceProduct);
        if (sourceProduct is not null)
        {
            input = input with { RecipeIdentitySha256 = sourceProduct.Recipe.IdentitySha256 };
        }
        var recipeOptions = JsonSerializer.SerializeToElement(new EncodedPreviewOptions(
            Options.BlackPercentile,
            Options.WhitePercentile,
            Options.AsinhStrength,
            OutputEncoding: "Packed"));
        var outcome = await adapter.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            recipeOptions,
            CameraAgentRecipeExecutionAdapter.CreateSelector(sourceArtifact, sourceProduct, "source"),
            [input],
            Options.OutputVariant), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
        {
            return;
        }
        var product = outcome.Products[0];
        var artifact = context.AddDerivative(FrameArtifactRole.Preview,
            CameraAgentRecipeExecutionAdapter.CreateFrame(product, source, "Preview"),
            Options.RecipeVersion,
            product.SourceArtifactIds,
            CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
        context.AssociateProcessingProduct(artifact, product);
    }
}

public sealed class PreviewProcessingStepOptions : IValidatableObject
{
    public bool Enabled { get; init; } = true;

    [Required(AllowEmptyStrings = false)]
    public string RecipeVersion { get; init; } = "mono16-asinh-v2";

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "default";

    [Range(0, 0.999999)]
    public double BlackPercentile { get; init; } = 0.5;

    [Range(0.000001, 1)]
    public double WhitePercentile { get; init; } = 0.9999;

    [Range(0.01, 1000)]
    public double AsinhStrength { get; init; } = 4;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (BlackPercentile >= WhitePercentile)
        {
            yield return new ValidationResult(
                "BlackPercentile must be less than WhitePercentile.",
                [nameof(BlackPercentile), nameof(WhitePercentile)]);
        }
    }
}
