using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates a rolling Mono16 combined artifact after every compatible capture.</summary>
internal sealed class RollingCombinationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    RollingCombinationProcessingStepOptions options) : ConfigurableCaptureProcessingStep<RollingCombinationProcessingStepOptions>(metadata, options)
{
    private readonly RollingMono16Combiner _combiner = new(options.WindowSize);

    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts?.Raw is not { } raw || raw.Frame.PixelFormat != CameraPixelFormat.Mono16)
        {
            return ValueTask.CompletedTask;
        }

        var result = _combiner.Add(raw);
        context.AddDerivative(FrameArtifactRole.Combined,
            new CameraFrame(raw.Frame.TimestampUtc, raw.Frame.Width, raw.Frame.Height, CameraPixelFormat.Mono16, result.PixelData,
                raw.Frame.Metadata with { SourceId = "RollingCombination" }),
            $"rolling-mean-v1-n{result.SourceArtifactIds.Count}");
        return ValueTask.CompletedTask;
    }
}

public sealed class RollingCombinationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Range(1, 100)]
    public int WindowSize { get; init; } = 5;
}
