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
    CameraAgentRecipeExecutionAdapter adapter) : ConfigurableCaptureProcessingStep<PreviewProcessingStepOptions>(metadata, options)
{
    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts?.Raw is not { } raw ||
            raw.Frame is not { } source ||
            source.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 or CameraPixelFormat.Rgb24))
        {
            return;
        }

        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source");
        var recipeOptions = JsonSerializer.SerializeToElement(new EncodedPreviewOptions(
            Options.BlackPercentile,
            Options.WhitePercentile,
            Options.AsinhStrength,
            OutputEncoding: "Packed"));
        var outcome = await adapter.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.EncodedPreview,
            recipeOptions,
            ProcessingInputSelector.Raw("source"),
            [input],
            Options.OutputVariant), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
        CameraAgentRecipeExecutionAdapter.ThrowIfFailure(outcome);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
        {
            return;
        }
        var product = outcome.Products[0];
        context.AddDerivative(FrameArtifactRole.Preview,
            CameraAgentRecipeExecutionAdapter.CreateFrame(product, source, "Preview"),
            Options.RecipeVersion,
            product.SourceArtifactIds);
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
