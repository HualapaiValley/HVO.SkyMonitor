using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>An explicitly laid-out linear 16-bit source frame.</summary>
public sealed record Linear16Frame(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData);

/// <summary>A tightly packed linear 16-bit arithmetic mean.</summary>
public sealed record Linear16MeanResult(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    int SourceCount,
    string AlgorithmVersion);

/// <summary>Computes a stateless arithmetic mean over compatible linear 16-bit frames.</summary>
public static class Linear16ArithmeticMean
{
    public const string AlgorithmVersion = "linear16-arithmetic-mean-v1";

    /// <summary>Computes a packed little-endian mean without retaining source buffers.</summary>
    public static Linear16MeanResult Compute(
        IReadOnlyList<Linear16Frame> frames,
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
        var sampleCount = checked(first.Width * first.Height);
        var totals = new ulong[sampleCount];
        for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
        {
            var frame = frames[frameIndex]
                ?? throw new ArgumentException("Source frames must not contain null entries.", nameof(frames));
            ValidateFrame(frame, nameof(frames));
            if (frame.Width != first.Width || frame.Height != first.Height || frame.PixelFormat != first.PixelFormat)
            {
                throw new ArgumentException("All source frames must have compatible dimensions and pixel format.", nameof(frames));
            }

            var source = frame.PixelData.Span;
            for (var y = 0; y < frame.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceRow = y * frame.StrideBytes;
                var destinationRow = y * frame.Width;
                for (var x = 0; x < frame.Width; x++)
                {
                    var offset = sourceRow + x * 2;
                    totals[destinationRow + x] += (ushort)(source[offset] | source[offset + 1] << 8);
                }
            }
        }

        var output = new byte[checked(sampleCount * 2)];
        for (var y = 0; y < first.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < first.Width; x++)
            {
                var pixel = y * first.Width + x;
                var average = (ushort)(totals[pixel] / (ulong)frames.Count);
                output[pixel * 2] = (byte)average;
                output[pixel * 2 + 1] = (byte)(average >> 8);
            }
        }

        return new Linear16MeanResult(
            first.Width,
            first.Height,
            checked(first.Width * 2),
            first.PixelFormat,
            output,
            frames.Count,
            AlgorithmVersion);
    }

    private static void ValidateFrame(Linear16Frame frame, string parameterName)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException("Linear arithmetic mean supports only Mono16 and BayerRggb16 frames.", parameterName);
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
