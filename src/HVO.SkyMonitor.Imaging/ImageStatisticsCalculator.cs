using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Exact aggregate statistics over the logical samples in an image.</summary>
public sealed record ImageStatisticsResult(
    long PixelCount,
    int ChannelCount,
    long SampleCount,
    ushort Minimum,
    ushort Maximum,
    ulong Sum,
    ulong SumOfSquares,
    long ZeroCount,
    long SaturatedCount,
    string AlgorithmVersion);

/// <summary>Computes deterministic, stride-aware integer image statistics.</summary>
public static class ImageStatisticsCalculator
{
    public const string AlgorithmVersion = "integer-image-statistics-v1";

    /// <summary>Computes aggregate sample statistics for Mono8, linear 16-bit, or RGB24 pixels.</summary>
    public static ImageStatisticsResult Calculate(
        int width,
        int height,
        int strideBytes,
        CameraPixelFormat pixelFormat,
        ReadOnlyMemory<byte> pixelData,
        ushort? saturationLevel = null,
        CancellationToken cancellationToken = default)
        => Calculate(new ImageLayout(width, height, pixelFormat, strideBytes), pixelData, saturationLevel, cancellationToken);

    /// <summary>Computes aggregate sample statistics for a validated image layout.</summary>
    public static ImageStatisticsResult Calculate(
        ImageLayout layout,
        ReadOnlyMemory<byte> pixelData,
        ushort? saturationLevel = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        layout.Validate();
        if (pixelData.Length < layout.RequiredByteLength)
        {
            throw new ArgumentException("Pixel buffer is shorter than its declared image layout.", nameof(pixelData));
        }

        var channelCount = layout.PixelFormat == CameraPixelFormat.Rgb24 ? 3 : 1;
        var formatMaximum = layout.PixelFormat is CameraPixelFormat.Mono8 or CameraPixelFormat.Rgb24
            ? byte.MaxValue
            : ushort.MaxValue;
        var maximumSample = saturationLevel ?? formatMaximum;
        if (maximumSample > formatMaximum)
        {
            throw new ArgumentOutOfRangeException(nameof(saturationLevel));
        }
        var minimum = ushort.MaxValue;
        ushort maximum = 0;
        ulong sum = 0;
        ulong sumOfSquares = 0;
        long zeroCount = 0;
        long saturatedCount = 0;
        var pixels = pixelData.Span;
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = y * layout.StrideBytes;
            if (layout.PixelFormat is CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16)
            {
                for (var x = 0; x < layout.Width; x++)
                {
                    var offset = row + x * 2;
                    AddSample((ushort)(pixels[offset] | pixels[offset + 1] << 8));
                }
            }
            else
            {
                var rowSampleCount = checked(layout.Width * channelCount);
                for (var sample = 0; sample < rowSampleCount; sample++)
                {
                    AddSample(pixels[row + sample]);
                }
            }
        }

        var pixelCount = checked((long)layout.Width * layout.Height);
        return new ImageStatisticsResult(
            pixelCount,
            channelCount,
            checked(pixelCount * channelCount),
            minimum,
            maximum,
            sum,
            sumOfSquares,
            zeroCount,
            saturatedCount,
            AlgorithmVersion);

        void AddSample(ushort value)
        {
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
            sum += value;
            sumOfSquares += (ulong)value * value;
            zeroCount += value == 0 ? 1 : 0;
            saturatedCount += value == maximumSample ? 1 : 0;
        }
    }
}
