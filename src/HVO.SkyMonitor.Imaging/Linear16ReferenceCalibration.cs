using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Exposure facts and flat normalization used by deterministic linear reference correction.</summary>
public sealed record Linear16CalibrationParameters(
    TimeSpan LightExposure,
    TimeSpan DarkExposure,
    TimeSpan FlatExposure,
    ushort FlatNormalizationAdu);

/// <summary>A tightly packed corrected linear frame.</summary>
public sealed record Linear16CalibrationResult(
    int Width,
    int Height,
    int StrideBytes,
    CameraPixelFormat PixelFormat,
    ReadOnlyMemory<byte> PixelData,
    int CorrectedDefectCount,
    string AlgorithmVersion);

public sealed class UnrepairableCalibrationDefectException : Exception
{
    public UnrepairableCalibrationDefectException()
    {
    }

    public UnrepairableCalibrationDefectException(string message) : base(message)
    {
    }

    public UnrepairableCalibrationDefectException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Corrects compatible Mono16 or RGGB16 samples from explicit bias, dark, flat, and defect references.</summary>
public static class Linear16ReferenceCalibration
{
    public const string AlgorithmVersion = "linear16-reference-calibration-v1";

    /// <summary>
    /// Subtracts bias and exposure-scaled dark signal, divides by the bias/dark-corrected flat response with
    /// midpoint-away-from-zero integer rounding, clamps to UInt16, then replaces marked defects from same-lane
    /// orthogonal neighbors. Source buffers are borrowed and never modified.
    /// </summary>
    public static Linear16CalibrationResult Correct(
        Linear16Frame light,
        Linear16Frame bias,
        Linear16Frame dark,
        Linear16Frame flat,
        Linear16Frame defectMask,
        Linear16CalibrationParameters parameters,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(light);
        ArgumentNullException.ThrowIfNull(bias);
        ArgumentNullException.ThrowIfNull(dark);
        ArgumentNullException.ThrowIfNull(flat);
        ArgumentNullException.ThrowIfNull(defectMask);
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateParameters(parameters);
        ValidateCompatible(light, bias, nameof(bias));
        ValidateCompatible(light, dark, nameof(dark));
        ValidateCompatible(light, flat, nameof(flat));
        ValidateCompatible(light, defectMask, nameof(defectMask));
        var lightExposureTicks = (ulong)parameters.LightExposure.Ticks;
        var flatExposureTicks = (ulong)parameters.FlatExposure.Ticks;
        var darkExposureTicks = (ulong)parameters.DarkExposure.Ticks;
        var maximumRoundedNumerator = ulong.MaxValue - darkExposureTicks / 2;
        var lightScaleFitsUInt64 = lightExposureTicks <= maximumRoundedNumerator / ushort.MaxValue;
        var flatScaleFitsUInt64 = flatExposureTicks <= maximumRoundedNumerator / ushort.MaxValue;

        var output = new byte[checked(light.Width * light.Height * 2)];
        var defectCount = 0;
        for (var y = 0; y < light.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < light.Width; x++)
            {
                var outputOffset = checked((y * light.Width + x) * 2);
                if (Read(defectMask, x, y) != 0)
                {
                    defectCount++;
                    continue;
                }

                var biasValue = Read(bias, x, y);
                var darkSignal = Math.Max(0L, Read(dark, x, y) - biasValue);
                var scaledDark = ScaleRounded(
                    (ulong)darkSignal,
                    lightExposureTicks,
                    darkExposureTicks,
                    lightScaleFitsUInt64);
                var flatDark = ScaleRounded(
                    (ulong)darkSignal,
                    flatExposureTicks,
                    darkExposureTicks,
                    flatScaleFitsUInt64);
                var flatAfterBias = Math.Max(0L, (long)Read(flat, x, y) - biasValue);
                var flatSignal = flatDark >= (ulong)flatAfterBias ? 0 : flatAfterBias - (long)flatDark;
                if (flatSignal <= 0)
                {
                    throw new InvalidDataException("The corrected flat response must be positive at every usable sample.");
                }

                var lightAfterBias = Math.Max(0L, (long)Read(light, x, y) - biasValue);
                var lightSignal = scaledDark >= (ulong)lightAfterBias ? 0 : lightAfterBias - (long)scaledDark;
                var corrected = DivideRoundedFast(
                    checked((ulong)lightSignal * parameters.FlatNormalizationAdu),
                    (ulong)flatSignal);
                Write(output, outputOffset, (ushort)Math.Min(corrected, ushort.MaxValue));
            }
        }

        if (defectCount > 0)
        {
            ReplaceDefects(output, defectMask, light.PixelFormat, light.Width, light.Height, cancellationToken);
        }
        return new Linear16CalibrationResult(
            light.Width,
            light.Height,
            checked(light.Width * 2),
            light.PixelFormat,
            output,
            defectCount,
            AlgorithmVersion);
    }

    private static void ReplaceDefects(
        byte[] output,
        Linear16Frame defectMask,
        CameraPixelFormat format,
        int width,
        int height,
        CancellationToken cancellationToken)
    {
        var distance = format == CameraPixelFormat.BayerRggb16 ? 2 : 1;
        ReadOnlySpan<(int X, int Y)> directions = [(distance, 0), (-distance, 0), (0, distance), (0, -distance)];
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                if (Read(defectMask, x, y) == 0)
                {
                    continue;
                }
                uint total = 0;
                uint count = 0;
                foreach (var direction in directions)
                {
                    var neighborX = x + direction.X;
                    var neighborY = y + direction.Y;
                    if (neighborX < 0 || neighborX >= width || neighborY < 0 || neighborY >= height ||
                        Read(defectMask, neighborX, neighborY) != 0)
                    {
                        continue;
                    }
                    total += ReadPacked(output, width, neighborX, neighborY);
                    count++;
                }
                if (count == 0)
                {
                    throw new UnrepairableCalibrationDefectException(
                        "A marked defect has no compatible replacement neighbors.");
                }
                var replacement = (ushort)((total + count / 2) / count);
                Write(output, checked((y * width + x) * 2), replacement);
            }
        }
    }

    private static void ValidateParameters(Linear16CalibrationParameters parameters)
    {
        if (parameters.LightExposure <= TimeSpan.Zero || parameters.DarkExposure <= TimeSpan.Zero ||
            parameters.FlatExposure <= TimeSpan.Zero || parameters.FlatNormalizationAdu == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(parameters));
        }
    }

    private static void ValidateCompatible(Linear16Frame expected, Linear16Frame candidate, string parameterName)
    {
        ValidateFrame(expected, nameof(expected));
        ValidateFrame(candidate, parameterName);
        if (candidate.Width != expected.Width || candidate.Height != expected.Height ||
            candidate.PixelFormat != expected.PixelFormat)
        {
            throw new ArgumentException("Calibration references must match the light dimensions and pixel format.", parameterName);
        }
    }

    private static void ValidateFrame(Linear16Frame frame, string parameterName)
    {
        if (frame.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new ArgumentException("Reference calibration supports only Mono16 and BayerRggb16 frames.", parameterName);
        }
        var layout = new ImageLayout(frame.Width, frame.Height, frame.PixelFormat, frame.StrideBytes);
        try
        {
            layout.Validate();
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new ArgumentException("Calibration frame layout is invalid.", parameterName, exception);
        }
        if (frame.PixelData.Length < layout.RequiredByteLength)
        {
            throw new ArgumentException("Calibration frame buffer is shorter than its declared layout.", parameterName);
        }
    }

    private static ushort Read(Linear16Frame frame, int x, int y)
    {
        var offset = checked(y * frame.StrideBytes + x * 2);
        var span = frame.PixelData.Span;
        return (ushort)(span[offset] | span[offset + 1] << 8);
    }

    private static ushort ReadPacked(byte[] frame, int width, int x, int y)
    {
        var offset = checked((y * width + x) * 2);
        return (ushort)(frame[offset] | frame[offset + 1] << 8);
    }

    private static ulong DivideRoundedFast(ulong numerator, ulong denominator)
        => (numerator + denominator / 2) / denominator;

    private static ulong ScaleRounded(
        ulong value,
        ulong numerator,
        ulong denominator,
        bool fitsUInt64)
        => fitsUInt64
            ? DivideRoundedFast(value * numerator, denominator)
            : DivideRounded((UInt128)value * numerator, denominator);

    private static ulong DivideRounded(UInt128 numerator, ulong denominator)
    {
        var quotient = numerator / denominator;
        var remainder = numerator % denominator;
        if (remainder >= ((UInt128)denominator + 1) / 2)
        {
            quotient++;
        }
        return quotient > ulong.MaxValue ? ulong.MaxValue : (ulong)quotient;
    }

    private static void Write(byte[] destination, int offset, ushort value)
    {
        destination[offset] = (byte)value;
        destination[offset + 1] = (byte)(value >> 8);
    }
}
