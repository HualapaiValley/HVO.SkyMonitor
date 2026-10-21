using System.Buffers;
using System.Buffers.Binary;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>
/// One input to <see cref="Linear16GainNormalizedMean"/>: packed row-major planes with one value per photosite, and
/// the input's own saturation code and coefficient.
/// </summary>
/// <param name="Light">Stored light codes.</param>
/// <param name="Bias">Bias in stored codes. It is the only bias subtracted from the light.</param>
/// <param name="Dark">Bias-subtracted dark signal at the light exposure, in stored codes, within ±65535.</param>
/// <param name="Flat">
/// Unity-normalized flat response as a count of 10^-6 (<see cref="Linear16GainNormalizedMean.FlatRepresentationVersion"/>).
/// </param>
/// <param name="DefectMask">Empty for no mask; otherwise one byte per photosite, and non-zero marks a defect.</param>
/// <param name="SaturationCode">The lowest stored light code that is saturated, tested before any correction.</param>
/// <param name="ElectronsPerAduScaled">The input's coefficient in <see cref="ElectronsPerAduFixedPoint"/> form.</param>
public sealed record GainNormalizedMeanInput(
    ReadOnlyMemory<ushort> Light,
    ReadOnlyMemory<ushort> Bias,
    ReadOnlyMemory<int> Dark,
    ReadOnlyMemory<int> Flat,
    ReadOnlyMemory<byte> DefectMask,
    ushort SaturationCode,
    long ElectronsPerAduScaled);

/// <summary>
/// A packed little-endian linear 16-bit mean at the reference coefficient, with the number of valid samples behind
/// each photosite and a mask of photosites that had none.
/// </summary>
public sealed record GainNormalizedMeanResult(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    ReadOnlyMemory<ushort> ValidCounts,
    ReadOnlyMemory<byte> NoCoverageMask,
    int SourceCount,
    int NoCoverageCount,
    int ClippedLowCount,
    int ClippedHighCount,
    string AlgorithmVersion);

/// <summary>
/// Calibrates each input at its own gain, converts it to electrons, and averages the valid samples of each photosite
/// with equal weights. The mean is encoded once, at a fixed reference coefficient.
/// </summary>
/// <remarks>
/// <para>
/// For input <c>i</c> and photosite <c>p</c>, <c>E_i(p) = k_i × (L_i(p) − B_i(p) − D_i(p)) / F_i(p)</c>, and the
/// output code is <c>Σ E_i(p) / (n(p) × k_reference)</c> over the <c>n(p)</c> valid samples. A sample contributes
/// neither signal nor weight when it is marked defective, when its stored light code is at or above its saturation
/// code, or when its flat is not between 1 and <see cref="MaximumFlatScaledValue"/> (10^-6 to 16.0).
/// </para>
/// <para>
/// The arithmetic is integer only. The corrected signal stays signed, within ±131070 codes, so negative noise reaches
/// the mean. Each sample's electrons are rounded once to a 10^-12 e⁻ grid, ties to even, and stay below 1.32 × 10^29
/// grid units in magnitude. Up to <see cref="MaximumInputCount"/> of them sum below 8.7 × 10^33, so an <see cref="Int128"/> sum
/// cannot overflow for any legal input; the arithmetic is checked anyway. The mean is rounded once to an output code,
/// ties to even, then clamped to UInt16, and each clamp is counted. Integer sums are exact, so the result does not
/// depend on reduction order or working size; inputs are summed in their given order.
/// </para>
/// <para>
/// A photosite with no valid sample is marked in the no-coverage mask and encoded as 0; that 0 is not a zero-flux
/// measurement. Photosites never borrow from neighbours, so a CFA frame is processed per photosite, before debayering.
/// </para>
/// </remarks>
public static class Linear16GainNormalizedMean
{
    public const string AlgorithmVersion = "linear16-gain-normalized-mean-v1";
    public const string FlatRepresentationVersion = "unity-flat-fixed-e6-v1";
    public const int FlatScale = 1_000_000;
    public const int MaximumFlatScaledValue = 16 * FlatScale;
    public const int MaximumDarkMagnitude = ushort.MaxValue;
    public const int MaximumInputCount = ushort.MaxValue;
    public const int DefaultWorkingPhotosites = 65_536;

    /// <summary>
    /// Computes the mean in working blocks of at most <paramref name="workingPhotosites"/> photosites, which bounds
    /// the working memory and never changes the result. Source buffers are borrowed and never modified.
    /// </summary>
    public static GainNormalizedMeanResult Compute(
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        IReadOnlyList<GainNormalizedMeanInput> inputs,
        long referenceElectronsPerAduScaled,
        int workingPhotosites = DefaultWorkingPhotosites,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        cancellationToken.ThrowIfCancellationRequested();
        if (pixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException("The gain-normalized mean supports only Mono16 and BayerRggb16 output.", nameof(pixelFormat));
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if ((long)width * height > Array.MaxLength / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "The frame is too large for one 16-bit buffer.");
        }
        ArgumentOutOfRangeException.ThrowIfLessThan(workingPhotosites, 1);
        if (!ElectronsPerAduFixedPoint.IsValid(referenceElectronsPerAduScaled))
        {
            throw new ArgumentOutOfRangeException(nameof(referenceElectronsPerAduScaled));
        }
        if (inputs.Count is 0 or > MaximumInputCount)
        {
            throw new ArgumentException($"Between 1 and {MaximumInputCount} inputs are required.", nameof(inputs));
        }
        var photosites = width * height;
        for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
        {
            var input = inputs[inputIndex]
                ?? throw new ArgumentException("Inputs must not contain null entries.", nameof(inputs));
            if (input.Light.Length != photosites || input.Bias.Length != photosites || input.Dark.Length != photosites ||
                input.Flat.Length != photosites || input.DefectMask.Length is not 0 && input.DefectMask.Length != photosites)
            {
                throw new ArgumentException("Every input plane must hold exactly one value per photosite.", nameof(inputs));
            }
            if (input.SaturationCode == 0 || !ElectronsPerAduFixedPoint.IsValid(input.ElectronsPerAduScaled))
            {
                throw new ArgumentException(
                    "Every input needs a positive saturation code and a valid electrons-per-ADU coefficient.",
                    nameof(inputs));
            }
        }

        var output = new byte[photosites * 2];
        var validCounts = new ushort[photosites];
        var noCoverage = new byte[photosites];
        var noCoverageCount = 0;
        var clippedLowCount = 0;
        var clippedHighCount = 0;
        var blockLength = Math.Min(workingPhotosites, photosites);
        var rented = ArrayPool<Int128>.Shared.Rent(blockLength);
        try
        {
            for (var start = 0; start < photosites; start += blockLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(blockLength, photosites - start);
                var sums = rented.AsSpan(0, length);
                sums.Clear();
                var counts = validCounts.AsSpan(start, length);
                for (var inputIndex = 0; inputIndex < inputs.Count; inputIndex++)
                {
                    Accumulate(inputs[inputIndex], start, sums, counts);
                }

                for (var index = 0; index < length; index++)
                {
                    var photosite = start + index;
                    ushort code = 0;
                    if (counts[index] == 0)
                    {
                        noCoverage[photosite] = 1;
                        noCoverageCount++;
                    }
                    else
                    {
                        var mean = DivideRoundingToEven(
                            sums[index],
                            checked(counts[index] * (Int128)referenceElectronsPerAduScaled));
                        if (mean < 0)
                        {
                            clippedLowCount++;
                        }
                        else if (mean > ushort.MaxValue)
                        {
                            code = ushort.MaxValue;
                            clippedHighCount++;
                        }
                        else
                        {
                            code = (ushort)mean;
                        }
                    }
                    BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(photosite * 2), code);
                }
            }
        }
        finally
        {
            ArrayPool<Int128>.Shared.Return(rented);
        }

        return new GainNormalizedMeanResult(
            width,
            height,
            width * 2,
            pixelFormat,
            output,
            validCounts,
            noCoverage,
            inputs.Count,
            noCoverageCount,
            clippedLowCount,
            clippedHighCount,
            AlgorithmVersion);
    }

    private static void Accumulate(GainNormalizedMeanInput input, int start, Span<Int128> sums, Span<ushort> counts)
    {
        var length = sums.Length;
        var light = input.Light.Span.Slice(start, length);
        var bias = input.Bias.Span.Slice(start, length);
        var dark = input.Dark.Span.Slice(start, length);
        var flat = input.Flat.Span.Slice(start, length);
        var defects = input.DefectMask.IsEmpty ? default : input.DefectMask.Span.Slice(start, length);
        var coefficient = (Int128)input.ElectronsPerAduScaled * FlatScale;
        for (var index = 0; index < length; index++)
        {
            var darkSignal = dark[index];
            if (darkSignal is < -MaximumDarkMagnitude or > MaximumDarkMagnitude)
            {
                throw new InvalidDataException("A bias-subtracted dark sample is outside ±65535 stored codes.");
            }
            var lightCode = light[index];
            var flatResponse = flat[index];
            if (lightCode >= input.SaturationCode || flatResponse is < 1 or > MaximumFlatScaledValue ||
                !defects.IsEmpty && defects[index] != 0)
            {
                continue;
            }

            var signal = lightCode - bias[index] - darkSignal;
            sums[index] = checked(sums[index] + DivideRoundingToEven(checked(coefficient * signal), flatResponse));
            counts[index]++;
        }
    }

    private static Int128 DivideRoundingToEven(Int128 numerator, Int128 positiveDenominator)
    {
        var (quotient, remainder) = Int128.DivRem(numerator, positiveDenominator);
        var twiceRemainder = Int128.Abs(remainder) << 1;
        if (twiceRemainder > positiveDenominator ||
            twiceRemainder == positiveDenominator && !Int128.IsEvenInteger(quotient))
        {
            quotient += Int128.Sign(remainder);
        }
        return quotient;
    }
}
