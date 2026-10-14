namespace HVO.SkyMonitor.Imaging;

/// <summary>Color filter arrangement in the top-left 2-by-2 cell of the supplied image.</summary>
public enum BayerPattern
{
    Rggb,
    Bggr,
    Grbg,
    Gbrg
}

/// <summary>Owned full-resolution floating-point linear RGB planes and a true-means-valid mask.</summary>
/// <remarks>No white balance, color calibration, gamma, stretch, clamping, or integer quantization is applied.</remarks>
public sealed class LinearStellarRgbImage
{
    internal LinearStellarRgbImage(int width, int height, double[] red, double[] green, double[] blue, bool[] validMask)
    {
        Width = width;
        Height = height;
        Red = red;
        Green = green;
        Blue = blue;
        ValidMask = validMask;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<double> Red { get; }
    public ReadOnlyMemory<double> Green { get; }
    public ReadOnlyMemory<double> Blue { get; }
    public ReadOnlyMemory<bool> ValidMask { get; }
    public string AlgorithmVersion { get; } = LinearBayerReconstruction.AlgorithmVersion;

    /// <summary>Creates the linear proxy 0.2126 R + 0.7152 G + 0.0722 B after reconstruction.</summary>
    /// <remarks>The fixed Rec.709 weights are a detection proxy, not a calibrated sensor color or photometry model.</remarks>
    public LinearStellarLuminanceImage ToLuminance(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pixels = new double[Red.Length];
        var red = Red.Span;
        var green = Green.Span;
        var blue = Blue.Span;
        var valid = ValidMask.Span;
        for (var index = 0; index < pixels.Length; index++)
        {
            if ((index & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (valid[index])
            {
                pixels[index] = 0.2126 * red[index] + 0.7152 * green[index] + 0.0722 * blue[index];
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Width, Height, pixels, ValidMask);
    }
}

/// <summary>Owned full-resolution linear luminance samples with the reconstruction's read-only validity mask.</summary>
public sealed class LinearStellarLuminanceImage
{
    internal LinearStellarLuminanceImage(int width, int height, double[] pixels, ReadOnlyMemory<bool> validMask)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
        ValidMask = validMask;
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<double> Pixels { get; }
    public ReadOnlyMemory<bool> ValidMask { get; }
}

/// <summary>Scientific linear bilinear reconstruction of Bayer mosaics without a display transform.</summary>
/// <remarks>
/// The declared CFA phase is relative to the supplied image origin. The one-pixel border and every output whose
/// 3-by-3 input neighborhood contains an invalid sample remain invalid and zero. Rectangular and odd-size inputs
/// are supported. Known samples are preserved exactly. Callers must not modify input buffers during a call.
/// </remarks>
public static class LinearBayerReconstruction
{
    public const string AlgorithmVersion = "linear-bayer-bilinear-v1";
    public const int MaximumSupportedPixels = StellarDetector.MaximumSupportedPixels;

    public static LinearStellarRgbImage Reconstruct(
        ReadOnlySpan<double> raw,
        ReadOnlySpan<bool> validMask,
        int width,
        int height,
        BayerPattern pattern = BayerPattern.Rggb,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(pattern))
        {
            throw new ArgumentOutOfRangeException(nameof(pattern));
        }
        LinearStellarInput.Validate(raw, validMask, width, height, 3, MaximumSupportedPixels, cancellationToken);

        var red = new double[raw.Length];
        var green = new double[raw.Length];
        var blue = new double[raw.Length];
        var outputMask = new bool[raw.Length];
        var planes = new[] { red, green, blue };
        for (var y = 1; y < height - 1; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 1; x < width - 1; x++)
            {
                if ((x & 1023) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                if (!HasValidNeighborhood(validMask, width, x, y))
                {
                    continue;
                }
                var index = y * width + x;
                var knownChannel = ChannelAt(x, y, pattern);
                for (var channel = 0; channel < 3; channel++)
                {
                    if (knownChannel == channel)
                    {
                        planes[channel][index] = raw[index];
                        continue;
                    }
                    // There are four contributors at R/B sites and two at green sites. Scale before summing
                    // to preserve finite constant fields near the floating-point limit.
                    var count = knownChannel == 1 ? 2 : 4;
                    double value = 0;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (ChannelAt(x + dx, y + dy, pattern) == channel)
                            {
                                value += raw[(y + dy) * width + x + dx] / count;
                            }
                        }
                    }
                    planes[channel][index] = value;
                }
                outputMask[index] = true;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(width, height, red, green, blue, outputMask);
    }

    private static bool HasValidNeighborhood(ReadOnlySpan<bool> mask, int width, int x, int y)
    {
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!mask[(y + dy) * width + x + dx])
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static int ChannelAt(int x, int y, BayerPattern pattern)
    {
        var phaseX = pattern is BayerPattern.Bggr or BayerPattern.Grbg ? 1 : 0;
        var phaseY = pattern is BayerPattern.Bggr or BayerPattern.Gbrg ? 1 : 0;
        return ((x + phaseX) & 1, (y + phaseY) & 1) switch
        {
            (0, 0) => 0,
            (1, 1) => 2,
            _ => 1
        };
    }
}
