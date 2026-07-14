using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates a rolling linear 16-bit combined artifact after every compatible capture.</summary>
internal sealed class RollingCombinationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    RollingCombinationProcessingStepOptions options,
    CameraAgentRecipeExecutionAdapter adapter) : ConfigurableCaptureProcessingStep<RollingCombinationProcessingStepOptions>(metadata, options)
{
    private readonly Queue<ProcessingArtifact> _window = new();

    internal int BufferedFrameCount => _window.Count;

    public override async ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts?.Raw is not { } raw ||
            raw.Frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var current = CameraAgentRecipeExecutionAdapter.CreateArtifact(context.Config, raw, "source") with
        {
            Payload = raw.Frame.PixelData.ToArray()
        };
        var candidateWindow = _window
            .Where(source => source.ArtifactId != current.ArtifactId)
            .Append(current)
            .TakeLast(Options.WindowSize)
            .ToList();

        var outcome = await adapter.ExecuteAsync(CreateRequest(candidateWindow), cancellationToken).ConfigureAwait(false);
        if (outcome.Status == ProcessingOutcomeStatus.TerminalFailure &&
            outcome.ReasonCode == ProcessingReasonCodes.IncompatibleInput)
        {
            candidateWindow = [current];
            outcome = await adapter.ExecuteAsync(CreateRequest(candidateWindow), cancellationToken).ConfigureAwait(false);
        }
        context.AddProcessingOutcome(outcome);
        CameraAgentRecipeExecutionAdapter.ThrowIfFailure(outcome);
        if (outcome.Status != ProcessingOutcomeStatus.Produced)
        {
            return;
        }

        var product = outcome.Products[0];
        _window.Clear();
        var selectedIds = product.SourceArtifactIds.ToHashSet();
        foreach (var source in candidateWindow.Where(source => selectedIds.Contains(source.ArtifactId)))
        {
            _window.Enqueue(source);
        }

        var stackMetadata = raw.Frame.Metadata.Extra is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(raw.Frame.Metadata.Extra, StringComparer.Ordinal);
        stackMetadata["stackCount"] = product.SourceArtifactIds.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        stackMetadata["totalIntegrationMilliseconds"] = product.TotalIntegration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
        var combinedFrame = CameraAgentRecipeExecutionAdapter.CreateFrame(product, raw.Frame, "RollingCombination");
        var artifact = context.AddDerivative(FrameArtifactRole.Combined,
            combinedFrame with { Metadata = combinedFrame.Metadata with { Extra = stackMetadata } },
            $"rolling-mean-v1-n{product.SourceArtifactIds.Count}",
            product.SourceArtifactIds);
        context.AssociateProcessingProduct(artifact, product);

        ProcessingExecutionRequest CreateRequest(IReadOnlyList<ProcessingArtifact> inputs) => new(
            BuiltInProcessingRecipes.RollingMean,
            JsonSerializer.SerializeToElement(new RollingMeanOptions(
                Options.WindowSize,
                Options.MaximumIntegrationMilliseconds,
                Options.MaximumAgeMilliseconds)),
            ProcessingInputSelector.Raw("source"),
            inputs,
            Options.OutputVariant);
    }
}

public sealed class RollingCombinationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Range(1, 100)]
    public int WindowSize { get; init; } = 5;

    [Range(double.Epsilon, double.MaxValue)]
    public double? MaximumIntegrationMilliseconds { get; init; }

    [Range(double.Epsilon, double.MaxValue)]
    public double? MaximumAgeMilliseconds { get; init; }

    [Required(AllowEmptyStrings = false)]
    public string OutputVariant { get; init; } = "rolling-mean";
}
