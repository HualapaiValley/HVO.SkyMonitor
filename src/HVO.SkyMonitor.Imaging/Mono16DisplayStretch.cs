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

        var histogram = new int[ushort.MaxValue + 1];
        var pixels = pixelData.Span;
        var activeCount = 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 2;
                var sample = pixels[offset] | pixels[offset + 1] << 8;
                if (sample > 0)
                {
                    histogram[sample]++;
                    activeCount++;
                }
            }
        }

        var output = new byte[checked(width * height)];
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

        var denominator = Math.Asinh(options.AsinhStrength);
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var offset = y * stride + x * 2;
                var sample = pixels[offset] | pixels[offset + 1] << 8;
                if (sample <= black)
                {
                    continue;
                }

                var normalized = Math.Clamp((sample - black) / (double)(white - black), 0, 1);
                var stretched = Math.Asinh(options.AsinhStrength * normalized) / denominator;
                output[y * width + x] = (byte)Math.Round(stretched * byte.MaxValue);
            }
        }
        return output;
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
