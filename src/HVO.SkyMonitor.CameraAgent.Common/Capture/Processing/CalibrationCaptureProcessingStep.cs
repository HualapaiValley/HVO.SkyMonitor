using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed class CalibrationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    CalibrationProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter) : ConfigurableCaptureProcessingStep<CalibrationProcessingStepOptions>(metadata, options), ICaptureProcessingGraphStep
{
    public bool Enabled => Options.Enabled;

    public string RecipeName => BuiltInProcessingRecipes.LinearNormalization;

    public FrameArtifactRole OutputRole => FrameArtifactRole.Calibrated;

    public string OutputVariant => Options.OutputVariant;

    public IReadOnlySet<FrameArtifactRole> AcceptedInputRoles { get; } =
        new HashSet<FrameArtifactRole> { FrameArtifactRole.Raw };

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!Options.Enabled)
        {
            return;
        }

        var raw = context.Artifacts?.Raw;
        if (raw is null)
        {
            return;
        }

        var input = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source");
        var outcome = await adapter.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.LinearNormalization,
            JsonSerializer.SerializeToElement(new LinearNormalizationOptions(Options.Strategy)),
            ProcessingInputSelector.Raw("source"),
            [input],
            Options.OutputVariant), cancellationToken).ConfigureAwait(false);
        context.AddProcessingOutcome(outcome);
        if (outcome.Status == ProcessingOutcomeStatus.Produced)
        {
            var product = outcome.Products[0];
            var artifact = context.AddDerivative(
                FrameArtifactRole.Calibrated,
                CameraAgentRecipeExecutionAdapter.CreateFrame(product, raw.Frame, "Calibration"),
                product.Recipe.Descriptor.ImplementationVersion,
                product.SourceArtifactIds,
                CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
            context.AssociateProcessingProduct(artifact, product);
        }
    }
}

public sealed class CalibrationProcessingStepOptions : IValidatableObject
{
    public bool Enabled { get; init; } = true;

    [Range(1, 10)]
    public int CalibrationPasses { get; init; } = 1;

    [Range(1, 600)]
    public int MaxCalibrationSeconds { get; init; } = 30;

    [Required(AllowEmptyStrings = false)]
    public string Strategy { get; init; } = "None";

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "none";

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.Equals(Strategy, "None", StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "Only the explicit None calibration strategy is currently supported.",
                [nameof(Strategy)]);
        }
    }
}
