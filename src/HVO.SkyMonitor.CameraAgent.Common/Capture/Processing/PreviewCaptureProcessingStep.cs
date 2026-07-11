using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates a deterministic Mono8 display preview while retaining the source raw artifact.</summary>
internal sealed class PreviewCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    PreviewProcessingStepOptions options) : ConfigurableCaptureProcessingStep<PreviewProcessingStepOptions>(metadata, options)
{
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts?.Raw.Frame is not { } source || source.PixelFormat != CameraPixelFormat.Mono16)
        {
            return ValueTask.CompletedTask;
        }

        var preview = new byte[checked(source.Width * source.Height)];
        var pixels = source.PixelData.Span;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                preview[y * source.Width + x] = pixels[(y * source.Width + x) * 2 + 1];
            }
        }

        context.AddDerivative(FrameArtifactRole.Preview,
            new CameraFrame(source.TimestampUtc, source.Width, source.Height, CameraPixelFormat.Mono8, preview,
                source.Metadata with { SourceId = "Preview" }), "mono16-high-byte-v1");
        return ValueTask.CompletedTask;
    }
}

public sealed class PreviewProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Required(AllowEmptyStrings = false)]
    public string RecipeVersion { get; init; } = "mono16-high-byte-v1";
}
