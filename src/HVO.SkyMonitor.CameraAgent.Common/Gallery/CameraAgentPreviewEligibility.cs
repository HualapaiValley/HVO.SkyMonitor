using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal enum CameraAgentPreviewEligibility
{
    Available,
    UnsupportedMediaType,
    TooLarge,
    Invalid
}

internal static class CameraAgentPreviewEligibilityPolicy
{
    private const int AbsoluteMaximumPreviewDimension = 2_048;
    private const int AbsoluteMaximumPreviewEncodedBytes = 16 * 1024 * 1024;
    internal const int MinimumGeneratedPreviewEncodedBytes = 4 * 1024;

    internal static CameraAgentPreviewEligibility Evaluate(
        FrameArtifactRole role,
        string? mediaType,
        long? byteLength,
        CameraPixelFormat? pixelFormat,
        bool reconstructableLayout,
        int? encodedWidth,
        int? encodedHeight,
        ArtifactReadOptions options)
    {
        if (IsEncodedJpeg(role, mediaType))
        {
            if (byteLength is null or < 1 || encodedWidth is null or < 1 || encodedHeight is null or < 1)
            {
                return CameraAgentPreviewEligibility.Invalid;
            }
            return byteLength > Math.Min(options.MaximumPreviewEncodedBytes, AbsoluteMaximumPreviewEncodedBytes) ||
                   encodedWidth > Math.Min(options.MaximumPreviewDimension, AbsoluteMaximumPreviewDimension) ||
                   encodedHeight > Math.Min(options.MaximumPreviewDimension, AbsoluteMaximumPreviewDimension)
                ? CameraAgentPreviewEligibility.TooLarge
                : CameraAgentPreviewEligibility.Available;
        }

        if (!IsReconstructableRole(role) || !reconstructableLayout ||
            !IsSupportedReconstructableMediaType(mediaType, pixelFormat))
        {
            return CameraAgentPreviewEligibility.UnsupportedMediaType;
        }
        if (byteLength is null or < 1)
        {
            return CameraAgentPreviewEligibility.Invalid;
        }
        return byteLength > options.MaximumPreviewSourceBytes || byteLength > int.MaxValue ||
               options.MaximumPreviewEncodedBytes < MinimumGeneratedPreviewEncodedBytes
            ? CameraAgentPreviewEligibility.TooLarge
            : CameraAgentPreviewEligibility.Available;
    }

    internal static bool IsEncodedJpeg(FrameArtifactRole role, string? mediaType)
        => role is FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview &&
           string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupportedLayout(FrameLayoutDescriptor layout)
    {
        try
        {
            return layout.StrideBytes == checked(layout.Width * ImageLayout.BytesPerPixel(layout.PixelFormat)) &&
                   layout.ByteLength == checked((long)layout.StrideBytes * layout.Height) &&
                   (layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16
                       ? layout.ByteOrder == FrameByteOrder.LittleEndian
                       : layout.ByteOrder == FrameByteOrder.NotApplicable);
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            return false;
        }
    }

    private static bool IsReconstructableRole(FrameArtifactRole role)
        => role is FrameArtifactRole.Raw or FrameArtifactRole.Calibrated or FrameArtifactRole.Combined or
             FrameArtifactRole.Preview or FrameArtifactRole.AnnotatedPreview;

    private static bool IsSupportedReconstructableMediaType(string? mediaType, CameraPixelFormat? pixelFormat)
    {
        if (pixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 or
            CameraPixelFormat.Rgb24 or CameraPixelFormat.BayerRggb16))
        {
            return false;
        }
        return string.Equals(mediaType, "application/x-hvo-packed-image", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mediaType, "application/x-hvo-linear-frame", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mediaType, pixelFormat switch
               {
                   CameraPixelFormat.Mono8 => "application/x-skymonitor-mono8",
                   CameraPixelFormat.Mono16 => "application/x-skymonitor-mono16",
                   CameraPixelFormat.Rgb24 => "application/x-skymonitor-rgb24",
                   CameraPixelFormat.BayerRggb16 => "application/x-skymonitor-bayer-rggb16",
                   _ => string.Empty
               }, StringComparison.OrdinalIgnoreCase);
    }
}
