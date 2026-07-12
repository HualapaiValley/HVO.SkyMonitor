using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

/// <summary>Creates a deterministic Mono8 display preview while retaining the source raw artifact.</summary>
internal sealed class PreviewCaptureProcessingStep(
    CaptureProcessingStepMetadata metadata,
    PreviewProcessingStepOptions options) : ConfigurableCaptureProcessingStep<PreviewProcessingStepOptions>(metadata, options)
{
    public override ValueTask ProcessAsync(CaptureProcessingContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Options.Enabled || context.Artifacts?.Raw.Frame is not { } source ||
            source.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16 or CameraPixelFormat.Rgb24))
        {
            return ValueTask.CompletedTask;
        }

        var stretch = new Mono16DisplayStretchOptions(
            Options.BlackPercentile, Options.WhitePercentile, Options.AsinhStrength);
        var preview = source.PixelFormat switch
        {
            CameraPixelFormat.BayerRggb16 => BayerRggb16Demosaicer.DemosaicToRgb24(
                source.Width, source.Height, source.PixelData, source.StrideBytes, stretch),
            CameraPixelFormat.Rgb24 => CopyRgb24(source),
            _ => Mono16DisplayStretch.Apply(
                source.Width, source.Height, source.PixelData, source.StrideBytes, stretch)
        };
        var previewFormat = source.PixelFormat is CameraPixelFormat.BayerRggb16 or CameraPixelFormat.Rgb24
            ? CameraPixelFormat.Rgb24
            : CameraPixelFormat.Mono8;

        context.AddDerivative(FrameArtifactRole.Preview,
            new CameraFrame(source.TimestampUtc, source.Width, source.Height, previewFormat, preview,
                source.Metadata with { SourceId = "Preview" }), Options.RecipeVersion);
        return ValueTask.CompletedTask;
    }

    private static byte[] CopyRgb24(CameraFrame source)
    {
        var packedStride = checked(source.Width * 3);
        var sourceStride = source.StrideBytes ?? packedStride;
        if (sourceStride < packedStride || source.PixelData.Length != checked(sourceStride * source.Height))
        {
            throw new ArgumentException("RGB24 source layout is invalid.", nameof(source));
        }

        var preview = new byte[checked(packedStride * source.Height)];
        for (var y = 0; y < source.Height; y++)
        {
            source.PixelData.Span.Slice(y * sourceStride, packedStride)
                .CopyTo(preview.AsSpan(y * packedStride, packedStride));
        }
        return preview;
    }
}

public sealed class PreviewProcessingStepOptions : IValidatableObject
{
    public bool Enabled { get; init; } = true;

    [Required(AllowEmptyStrings = false)]
    public string RecipeVersion { get; init; } = "mono16-asinh-v2";

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
