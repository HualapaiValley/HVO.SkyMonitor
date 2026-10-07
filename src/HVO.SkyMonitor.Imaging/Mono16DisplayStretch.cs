using System.Buffers;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Parameters for a robust preview-only nonlinear Mono16 stretch.</summary>
public sealed record Mono16DisplayStretchOptions(
    double BlackPercentile = 0.5,
    double WhitePercentile = 0.9999,
    double AsinhStrength = 4)
{
    /// <summary>Validates percentile ordering and finite positive stretch strength.</summary>
    public void Validate()
    {
        if (!double.IsFinite(BlackPercentile) || BlackPercentile is < 0 or >= 1 ||
            !double.IsFinite(WhitePercentile) || WhitePercentile is <= 0 or > 1 ||
            BlackPercentile >= WhitePercentile || !double.IsFinite(AsinhStrength) || AsinhStrength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Mono16DisplayStretchOptions));
        }
    }
}

/// <summary>Creates an 8-bit display derivative without modifying linear Mono16 source data.</summary>
public static class Mono16DisplayStretch
{
    public const string AlgorithmVersion = "mono16-display-stretch-v1";

    /// <summary>Applies percentile black/white points and an asinh transfer function.</summary>
    public static byte[] Apply(
        int width,
        int height,
        ReadOnlyMemory<byte> pixelData,
        int? strideBytes = null,
        Mono16DisplayStretchOptions? options = null)
        => Apply(width, height, pixelData, CancellationToken.None, strideBytes, options);

    /// <summary>Applies the display stretch while observing cancellation at row boundaries.</summary>
    public static byte[] Apply(
        int width,
        int height,
        ReadOnlyMemory<byte> pixelData,
        CancellationToken cancellationToken,
        int? strideBytes = null,
        Mono16DisplayStretchOptions? options = null)
    {
        options ??= new Mono16DisplayStretchOptions();
        options.Validate();
        var packedStride = checked(width * 2);
        var stride = strideBytes ?? packedStride;
        if (width <= 0 || height <= 0 || stride < packedStride || pixelData.Length < checked(stride * height))
        {
            throw new ArgumentException("Pixel buffer does not contain the requested Mono16 layout.", nameof(pixelData));
        }

        var histogram = ArrayPool<int>.Shared.Rent(ushort.MaxValue + 1);
        byte[]? transfer = null;
        try
        {
            Array.Clear(histogram, 0, ushort.MaxValue + 1);
            var pixels = pixelData.Span;
            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = pixels.Slice(y * stride, packedStride);
                if (BitConverter.IsLittleEndian)
                {
                    foreach (var sample in MemoryMarshal.Cast<byte, ushort>(row))
                    {
                        histogram[sample]++;
                    }
                }
                else
                {
                    for (var x = 0; x < width; x++)
                    {
                        histogram[row[x * 2] | row[x * 2 + 1] << 8]++;
                    }
                }
            }

            var output = new byte[checked(width * height)];
            var activeCount = checked(width * height) - histogram[0];
            if (activeCount == 0)
            {
                return output;
            }

            var black = Percentile(histogram, activeCount, options.BlackPercentile);
            var white = Percentile(histogram, activeCount, options.WhitePercentile);
            if (white <= black)
            {
                white = black;
                black = 0;
            }

            // Every sample above white clamps to the same value as white, so the transfer has white - black + 1
            // distinct inputs. When that is no larger than the frame, each is evaluated once with the same
            // expression as the direct path and looked up; otherwise the direct path is cheaper. Both are exact.
            var denominator = Math.Asinh(options.AsinhStrength);
            var range = white - black;
            if (range < width * height)
            {
                transfer = ArrayPool<byte>.Shared.Rent(range + 1);
                transfer[0] = 0;
                for (var offset = 1; offset <= range; offset++)
                {
                    transfer[offset] = Transfer(black + offset, black, white, options.AsinhStrength, denominator);
                }
            }

            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = pixels.Slice(y * stride, packedStride);
                var destination = output.AsSpan(y * width, width);
                for (var x = 0; x < width; x++)
                {
                    var sample = row[x * 2] | row[x * 2 + 1] << 8;
                    if (sample <= black)
                    {
                        continue;
                    }

                    destination[x] = transfer is null
                        ? Transfer(sample, black, white, options.AsinhStrength, denominator)
                        : transfer[Math.Min(sample, white) - black];
                }
            }
            return output;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(histogram);
            if (transfer is not null)
            {
                ArrayPool<byte>.Shared.Return(transfer);
            }
        }
    }

    private static byte Transfer(int sample, int black, int white, double strength, double denominator)
    {
        var normalized = Math.Clamp((sample - black) / (double)(white - black), 0, 1);
        var stretched = Math.Asinh(strength * normalized) / denominator;
        return (byte)Math.Round(stretched * byte.MaxValue);
    }

    private static int Percentile(int[] histogram, int sampleCount, double percentile)
    {
        var target = Math.Max(1, (int)Math.Ceiling(sampleCount * percentile));
        var cumulative = 0;
        for (var value = 1; value < histogram.Length; value++)
        {
            cumulative += histogram[value];
            if (cumulative >= target)
            {
                return value;
            }
        }
        return ushort.MaxValue;
    }
}
