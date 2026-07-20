using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>A row-major, LSB-first detector-pixel exclusion mask.</summary>
public sealed record Linear16PixelMask(
    int Width,
    int Height,
    ReadOnlyMemory<byte> Bits);

/// <summary>One circular detector-space exclusion support.</summary>
public readonly record struct Linear16CircularMaskRegion(
    double CenterX,
    double CenterY,
    double RadiusPixels);

/// <summary>One masked linear frame and its exact rational normalization to the target response.</summary>
public sealed record Linear16TemporalFrame(
    Linear16Frame Frame,
    Linear16PixelMask ExclusionMask,
    ushort BlackLevel,
    uint NormalizationNumerator = 1,
    uint NormalizationDenominator = 1);

/// <summary>An owned packed background and a mask whose set pixels had no usable source samples.</summary>
public sealed record Linear16TemporalBackgroundResult(
    int Width,
    int Height,
    int StrideBytes,
    ReadOnlyMemory<byte> PixelData,
    Linear16PixelMask NoSupportMask,
    int SourceCount,
    long IncludedSamples,
    long MaskedSamples,
    string AlgorithmVersion);

/// <summary>Creates and combines deterministic detector-pixel masks.</summary>
public static class Linear16MaskOperations
{
    public const string AlgorithmVersion = "linear16-exclusion-mask-lsb-v1";
    public const string RggbReductionAlgorithmVersion = "linear16-rggb-any-excluded-cell-mask-v1";
    public const string CircularSupportAlgorithmVersion = "linear16-circular-support-mask-v1";

    /// <summary>Returns the exact byte count for a bit-packed mask.</summary>
    public static int RequiredByteLength(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        var pixelCount = checked((long)width * height);
        return checked((int)((pixelCount + 7) / 8));
    }

    /// <summary>Creates an empty exclusion mask.</summary>
    public static Linear16PixelMask Empty(int width, int height)
        => new(width, height, new byte[RequiredByteLength(width, height)]);

    /// <summary>Combines compatible masks with bitwise OR without retaining their buffers.</summary>
    public static Linear16PixelMask Combine(
        IReadOnlyList<Linear16PixelMask> masks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(masks);
        cancellationToken.ThrowIfCancellationRequested();
        if (masks.Count == 0)
        {
            throw new ArgumentException("At least one mask is required.", nameof(masks));
        }

        var first = masks[0] ?? throw new ArgumentException("Masks must not contain null entries.", nameof(masks));
        Validate(first, nameof(masks));
        var output = new byte[first.Bits.Length];
        for (var maskIndex = 0; maskIndex < masks.Count; maskIndex++)
        {
            var mask = masks[maskIndex] ?? throw new ArgumentException("Masks must not contain null entries.", nameof(masks));
            Validate(mask, nameof(masks));
            if (mask.Width != first.Width || mask.Height != first.Height)
            {
                throw new ArgumentException("All masks must have compatible dimensions.", nameof(masks));
            }

            var source = mask.Bits.Span;
            for (var index = 0; index < output.Length; index++)
            {
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
                output[index] |= source[index];
            }
        }

        ClearPaddingBits(output, checked(first.Width * first.Height));
        return new Linear16PixelMask(first.Width, first.Height, output);
    }

    /// <summary>Creates a detector-space mask from circular supports, including projected star paths.</summary>
    public static Linear16PixelMask CreateCircularSupportMask(
        int width,
        int height,
        IReadOnlyList<Linear16CircularMaskRegion> regions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regions);
        cancellationToken.ThrowIfCancellationRequested();
        var output = new byte[RequiredByteLength(width, height)];
        for (var regionIndex = 0; regionIndex < regions.Count; regionIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var region = regions[regionIndex];
            if (!double.IsFinite(region.CenterX) || !double.IsFinite(region.CenterY) ||
                !double.IsFinite(region.RadiusPixels) || region.RadiusPixels <= 0 || region.RadiusPixels > 256)
            {
                throw new ArgumentOutOfRangeException(nameof(regions));
            }

            var minimumX = Math.Max(0, (int)Math.Floor(region.CenterX - region.RadiusPixels));
            var maximumX = Math.Min(width - 1, (int)Math.Ceiling(region.CenterX + region.RadiusPixels));
            var minimumY = Math.Max(0, (int)Math.Floor(region.CenterY - region.RadiusPixels));
            var maximumY = Math.Min(height - 1, (int)Math.Ceiling(region.CenterY + region.RadiusPixels));
            var radiusSquared = region.RadiusPixels * region.RadiusPixels;
            for (var y = minimumY; y <= maximumY; y++)
            {
                for (var x = minimumX; x <= maximumX; x++)
                {
                    var dx = x + 0.5 - region.CenterX;
                    var dy = y + 0.5 - region.CenterY;
                    if (dx * dx + dy * dy <= radiusSquared)
                    {
                        Set(output, checked(y * width + x));
                    }
                }
            }
        }

        return new Linear16PixelMask(width, height, output);
    }

    /// <summary>Marks detector pixels at or above an inclusive saturation threshold.</summary>
    public static Linear16PixelMask CreateSaturationMask(
        Linear16Frame frame,
        ushort saturationLevel,
        CancellationToken cancellationToken = default)
    {
        ValidateFrame(frame, nameof(frame));
        cancellationToken.ThrowIfCancellationRequested();
        var output = new byte[RequiredByteLength(frame.Width, frame.Height)];
        var source = frame.PixelData.Span;
        for (var y = 0; y < frame.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = y * frame.StrideBytes;
            for (var x = 0; x < frame.Width; x++)
            {
                if (Read(source, row + x * 2) >= saturationLevel)
                {
                    Set(output, checked(y * frame.Width + x));
                }
            }
        }

        return new Linear16PixelMask(frame.Width, frame.Height, output);
    }

    /// <summary>Reduces a source RGGB mask; any excluded photosite excludes its derived detector cell.</summary>
    public static Linear16PixelMask ReduceRggb16ToDetector(
        Linear16PixelMask sourceMask,
        CancellationToken cancellationToken = default)
    {
        Validate(sourceMask, nameof(sourceMask));
        cancellationToken.ThrowIfCancellationRequested();
        if ((sourceMask.Width & 1) != 0 || (sourceMask.Height & 1) != 0)
        {
            throw new ArgumentException("RGGB mask dimensions must be even.", nameof(sourceMask));
        }

        var outputWidth = sourceMask.Width / 2;
        var outputHeight = sourceMask.Height / 2;
        var output = new byte[RequiredByteLength(outputWidth, outputHeight)];
        var source = sourceMask.Bits.Span;
        for (var y = 0; y < outputHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < outputWidth; x++)
            {
                var sourceX = x * 2;
                var sourceY = y * 2;
                if (Contains(source, sourceY * sourceMask.Width + sourceX) ||
                    Contains(source, sourceY * sourceMask.Width + sourceX + 1) ||
                    Contains(source, (sourceY + 1) * sourceMask.Width + sourceX) ||
                    Contains(source, (sourceY + 1) * sourceMask.Width + sourceX + 1))
                {
                    Set(output, y * outputWidth + x);
                }
            }
        }

        return new Linear16PixelMask(outputWidth, outputHeight, output);
    }

    /// <summary>Returns whether the specified detector pixel is excluded.</summary>
    public static bool IsExcluded(Linear16PixelMask mask, int x, int y)
    {
        Validate(mask, nameof(mask));
        if ((uint)x >= (uint)mask.Width || (uint)y >= (uint)mask.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }
        return Contains(mask.Bits.Span, checked(y * mask.Width + x));
    }

    internal static void Validate(Linear16PixelMask mask, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(mask, parameterName);
        int required;
        try
        {
            required = RequiredByteLength(mask.Width, mask.Height);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Mask dimensions are invalid.", parameterName, exception);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("Mask dimensions are invalid.", parameterName, exception);
        }
        if (mask.Bits.Length != required || HasSetPaddingBits(mask.Bits.Span, checked(mask.Width * mask.Height)))
        {
            throw new ArgumentException("Mask storage or padding bits are invalid.", parameterName);
        }
    }

    private static void ValidateFrame(Linear16Frame frame, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(frame, parameterName);
        if (frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException("Mask creation supports only linear 16-bit frames.", parameterName);
        }
        try
        {
            var layout = new ImageLayout(frame.Width, frame.Height, frame.PixelFormat, frame.StrideBytes);
            layout.Validate();
            if (frame.PixelData.Length != layout.RequiredByteLength)
            {
                throw new ArgumentException("Frame storage does not exactly match its layout.", parameterName);
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Frame layout is invalid.", parameterName, exception);
        }
    }

    private static bool Contains(ReadOnlySpan<byte> bits, int index)
        => (bits[index >> 3] & 1 << (index & 7)) != 0;

    private static void Set(Span<byte> bits, int index)
        => bits[index >> 3] |= (byte)(1 << (index & 7));

    private static ushort Read(ReadOnlySpan<byte> source, int offset)
        => (ushort)(source[offset] | source[offset + 1] << 8);

    private static bool HasSetPaddingBits(ReadOnlySpan<byte> bits, int pixelCount)
    {
        var usedBits = pixelCount & 7;
        return usedBits != 0 && (bits[^1] & ~((1 << usedBits) - 1)) != 0;
    }

    private static void ClearPaddingBits(Span<byte> bits, int pixelCount)
    {
        var usedBits = pixelCount & 7;
        if (usedBits != 0)
        {
            bits[^1] &= (byte)((1 << usedBits) - 1);
        }
    }
}

/// <summary>Computes deterministic masked and normalized temporal backgrounds in one source scan.</summary>
public static class Linear16TemporalBackground
{
    public const string AlgorithmVersion = "linear16-masked-normalized-temporal-mean-v1";

    /// <summary>
    /// Produces one tightly packed background. Normalization is applied to black-subtracted samples using each
    /// source's exact rational factor; midpoint rounding is away from zero and the final mean rounds to nearest.
    /// </summary>
    public static Linear16TemporalBackgroundResult Compute(
        IReadOnlyList<Linear16TemporalFrame> frames,
        ushort outputBlackLevel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        cancellationToken.ThrowIfCancellationRequested();
        if (frames.Count == 0)
        {
            throw new ArgumentException("At least one temporal source is required.", nameof(frames));
        }

        var first = frames[0] ?? throw new ArgumentException("Temporal sources must not contain null entries.", nameof(frames));
        Validate(first, nameof(frames));
        var width = first.Frame.Width;
        var height = first.Frame.Height;
        var output = GC.AllocateUninitializedArray<byte>(checked(width * height * 2));
        var noSupport = new byte[Linear16MaskOperations.RequiredByteLength(width, height)];
        long includedSamples = 0;
        long maskedSamples = 0;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                ulong sum = 0;
                var support = 0;
                var pixelIndex = y * width + x;
                for (var frameIndex = 0; frameIndex < frames.Count; frameIndex++)
                {
                    var source = frames[frameIndex]
                        ?? throw new ArgumentException("Temporal sources must not contain null entries.", nameof(frames));
                    if (y == 0 && x == 0)
                    {
                        Validate(source, nameof(frames));
                        if (source.Frame.Width != width || source.Frame.Height != height ||
                            source.Frame.StrideBytes != first.Frame.StrideBytes)
                        {
                            throw new ArgumentException("All temporal sources must have compatible dimensions.", nameof(frames));
                        }
                    }
                    if ((source.ExclusionMask.Bits.Span[pixelIndex >> 3] & 1 << (pixelIndex & 7)) != 0)
                    {
                        maskedSamples++;
                        continue;
                    }

                    var sourceOffset = y * source.Frame.StrideBytes + x * 2;
                    var pixels = source.Frame.PixelData.Span;
                    var sample = (ushort)(pixels[sourceOffset] | pixels[sourceOffset + 1] << 8);
                    if (!TryNormalize(
                        sample,
                        source.BlackLevel,
                        outputBlackLevel,
                        source.NormalizationNumerator,
                        source.NormalizationDenominator,
                        out var normalized))
                    {
                        maskedSamples++;
                        continue;
                    }
                    sum += normalized;
                    support++;
                    includedSamples++;
                }

                var outputOffset = pixelIndex * 2;
                if (support == 0)
                {
                    noSupport[pixelIndex >> 3] |= (byte)(1 << (pixelIndex & 7));
                    output[outputOffset] = (byte)outputBlackLevel;
                    output[outputOffset + 1] = (byte)(outputBlackLevel >> 8);
                    continue;
                }

                var mean = (ushort)((sum + (ulong)support / 2) / (ulong)support);
                output[outputOffset] = (byte)mean;
                output[outputOffset + 1] = (byte)(mean >> 8);
            }
        }

        return new Linear16TemporalBackgroundResult(
            width,
            height,
            checked(width * 2),
            output,
            new Linear16PixelMask(width, height, noSupport),
            frames.Count,
            includedSamples,
            maskedSamples,
            AlgorithmVersion);
    }

    private static void Validate(Linear16TemporalFrame source, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        ArgumentNullException.ThrowIfNull(source.Frame, parameterName);
        if (source.Frame.PixelFormat != CameraPixelFormat.Mono16 || source.NormalizationNumerator == 0 ||
            source.NormalizationDenominator == 0)
        {
            throw new ArgumentException("Temporal sources require Mono16 pixels and a positive normalization factor.", parameterName);
        }
        try
        {
            var layout = new ImageLayout(source.Frame.Width, source.Frame.Height, source.Frame.PixelFormat, source.Frame.StrideBytes);
            layout.Validate();
            if (source.Frame.PixelData.Length != layout.RequiredByteLength)
            {
                throw new ArgumentException("Temporal source storage must exactly match its layout.", parameterName);
            }
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Temporal source layout is invalid.", parameterName, exception);
        }
        Linear16MaskOperations.Validate(source.ExclusionMask, parameterName);
        if (source.ExclusionMask.Width != source.Frame.Width || source.ExclusionMask.Height != source.Frame.Height)
        {
            throw new ArgumentException("Temporal source mask dimensions do not match its frame.", parameterName);
        }
    }

    private static bool TryNormalize(
        ushort sample,
        ushort sourceBlackLevel,
        ushort outputBlackLevel,
        uint numerator,
        uint denominator,
        out ushort output)
    {
        var signal = (long)sample - sourceBlackLevel;
        var scaledMagnitude = DivideRound((ulong)Math.Abs(signal) * numerator, denominator);
        if (scaledMagnitude > long.MaxValue)
        {
            output = default;
            return false;
        }
        var normalized = signal < 0
            ? (long)outputBlackLevel - (long)scaledMagnitude
            : (long)outputBlackLevel + (long)scaledMagnitude;
        if (normalized is < ushort.MinValue or > ushort.MaxValue)
        {
            output = default;
            return false;
        }
        output = (ushort)normalized;
        return true;
    }

    private static ulong DivideRound(ulong numerator, uint denominator)
        => (numerator + denominator / 2UL) / denominator;
}
