using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>One timestamped display frame combined into a star trail.</summary>
public sealed record StarTrailFrame(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    DateTimeOffset TimestampUtc);

/// <summary>A tightly packed lighten composite of compatible frames.</summary>
public sealed record StarTrailResult(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    int FrameCount,
    string AlgorithmVersion);

/// <summary>Computes a per-pixel lighten (maximum) composite of compatible frames without retaining source buffers.</summary>
public static class StarTrailComposer
{
    public const string AlgorithmVersion = "star-trail-lighten-v1";

    /// <summary>Composes the lighten composite into a new packed buffer.</summary>
    public static StarTrailResult Compose(
        IReadOnlyList<StarTrailFrame> frames,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        cancellationToken.ThrowIfCancellationRequested();
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one source frame is required.", nameof(frames));
        }

        var first = frames[0] ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
        ValidateFrame(first, nameof(frames));
        var width = first.Width;
        var height = first.Height;
        var pixelFormat = first.PixelFormat;
        var bytesPerPixel = ImageLayout.BytesPerPixel(pixelFormat);
        var outputStride = checked(width * bytesPerPixel);
        var output = new byte[checked(outputStride * height)];

        if (pixelFormat == CameraPixelFormat.Mono16)
        {
            ComposeMono16(frames, width, height, output, outputStride, cancellationToken);
        }
        else
        {
            ComposeBytes(frames, width, height, bytesPerPixel, output, outputStride, cancellationToken);
        }

        return new StarTrailResult(
            width, height, outputStride, pixelFormat, output, frames.Count, AlgorithmVersion);
    }

    private static void ComposeBytes(
        IReadOnlyList<StarTrailFrame> frames,
        int width,
        int height,
        int bytesPerPixel,
        byte[] output,
        int outputStride,
        CancellationToken cancellationToken)
    {
        var rowBytes = checked(width * bytesPerPixel);
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var frame = frames[frameIndex]
                ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
            ValidateFrame(frame, nameof(frames));
            if (frame.Width != width || frame.Height != height || frame.PixelFormat != frames[0].PixelFormat)
            {
                throw new ArgumentException(
                    "All star trail frames must share dimensions and pixel format.", nameof(frames));
            }

            var source = frame.PixelData.Span;
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = y * frame.StrideBytes;
                var destinationRow = y * outputStride;
                for (var x = 0; x < rowBytes; x++)
                {
                    if (source[sourceRow + x] > output[destinationRow + x])
                    {
                        output[destinationRow + x] = source[sourceRow + x];
                    }
                }
            }
        }
    }

    private static void ComposeMono16(
        IReadOnlyList<StarTrailFrame> frames,
        int width,
        int height,
        byte[] output,
        int outputStride,
        CancellationToken cancellationToken)
    {
        var maximum = new ushort[checked(width * height)];
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var frame = frames[frameIndex]
                ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
            ValidateFrame(frame, nameof(frames));
            if (frame.Width != width || frame.Height != height || frame.PixelFormat != frames[0].PixelFormat)
            {
                throw new ArgumentException(
                    "All star trail frames must share dimensions and pixel format.", nameof(frames));
            }

            var source = frame.PixelData.Span;
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = y * frame.StrideBytes;
                var pixelRow = y * width;
                for (var x = 0; x < width; x++)
                {
                    var offset = sourceRow + x * 2;
                    var value = (ushort)(source[offset] | source[offset + 1] << 8);
                    if (value > maximum[pixelRow + x])
                    {
                        maximum[pixelRow + x] = value;
                    }
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationRow = y * outputStride;
            var pixelRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = maximum[pixelRow + x];
                output[destinationRow + x * 2] = (byte)value;
                output[destinationRow + x * 2 + 1] = (byte)(value >> 8);
            }
        }
    }

    private static void ValidateFrame(StarTrailFrame frame, string parameterName)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Mono8 or CameraPixelFormat.Mono16 or CameraPixelFormat.Rgb24))
        {
            throw new ArgumentException(
                "Star trail composition supports Mono8, Mono16, and RGB24 frames.", parameterName);
        }
        if (frame.TimestampUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Source frame timestamps must be UTC.", parameterName);
        }

        var layout = new ImageLayout(frame.Width, frame.Height, frame.PixelFormat, frame.StrideBytes);
        try
        {
            layout.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Source frame layout is invalid.", parameterName, exception);
        }
        if (frame.PixelData.Length < layout.RequiredByteLength)
        {
            throw new ArgumentException("Source frame buffer is shorter than its declared layout.", parameterName);
        }
    }
}
