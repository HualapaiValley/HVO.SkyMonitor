using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates an annotated preview through the shared projector contract.</summary>
internal sealed class AnnotationCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    AnnotationProcessingStepOptions options) : ConfigurableCaptureProcessingStep<AnnotationProcessingStepOptions>(metadata, options)
{
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts is not { } artifacts ||
            !artifacts.Artifacts.TryGetValue(FrameArtifactRole.Preview, out var preview) ||
            preview.Frame.PixelFormat != CameraPixelFormat.Mono8)
        {
            return ValueTask.CompletedTask;
        }

        var frame = preview.Frame;
        var projector = ProjectorFactory.CreatePerspective(new PerspectiveProjectionContext(
            frame.Width / 2d, frame.Height / 2d, Options.FocalLengthPixels, Options.FocalLengthPixels, frame.Width, frame.Height));
        var pixels = AnnotationRenderer.AnnotateMono8(frame.PixelData, frame.Width, frame.Height, projector, [new AltAzPoint(90, 0)]);
        context.AddDerivative(FrameArtifactRole.AnnotatedPreview,
            new CameraFrame(frame.TimestampUtc, frame.Width, frame.Height, CameraPixelFormat.Mono8, pixels,
                frame.Metadata with { SourceId = "AnnotatedPreview" }), Options.RecipeVersion);
        return ValueTask.CompletedTask;
    }
}

public sealed class AnnotationProcessingStepOptions
{
    public bool Enabled { get; init; } = true;

    [Range(1, 100_000)]
    public double FocalLengthPixels { get; init; } = 500;

    [Required(AllowEmptyStrings = false)]
    public string RecipeVersion { get; init; } = "zenith-marker-v1";
}
