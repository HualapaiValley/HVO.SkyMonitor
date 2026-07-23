using HVO.SkyMonitor.AgentCore;
using System.Text.Json;

namespace HVO.SkyMonitor.Imaging;

public sealed record SyntheticCalibrationDefect(int X, int Y, ushort FixedValueAdu = ushort.MaxValue);

/// <summary>Versioned deterministic software-only sensor effects used to generate lights and matching references.</summary>
public sealed record SyntheticCalibrationModelV1
{
    public const string CurrentSchemaVersion = "synthetic-calibration-model-v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public int Seed { get; init; } = 195;
    public ushort BiasPedestalAdu { get; init; } = 100;
    public ushort BiasPatternAmplitudeAdu { get; init; } = 8;
    public double DarkCurrentAduPerSecond { get; init; } = 5;
    public double DarkPatternFraction { get; init; } = 0.25;
    public double PixelResponseVariationFraction { get; init; } = 0.1;
    public double VignettingStrength { get; init; } = 0.2;
    public ushort FlatSignalAdu { get; init; } = 20_000;
    public TimeSpan BiasExposure { get; init; } = TimeSpan.FromMilliseconds(1);
    public TimeSpan DarkExposure { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan FlatExposure { get; init; } = TimeSpan.FromSeconds(2);
    public double Gain { get; init; } = 10;
    public double TemperatureC { get; init; } = -10;
    public IReadOnlyList<SyntheticCalibrationDefect> Defects { get; init; } = [];

    public void Validate(int width, int height)
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            width < 1 || height < 1 || BiasPatternAmplitudeAdu > BiasPedestalAdu ||
            !double.IsFinite(DarkCurrentAduPerSecond) || DarkCurrentAduPerSecond < 0 ||
            !double.IsFinite(DarkPatternFraction) || DarkPatternFraction is < 0 or > 1 ||
            !double.IsFinite(PixelResponseVariationFraction) || PixelResponseVariationFraction is < 0 or > 0.5 ||
            !double.IsFinite(VignettingStrength) || VignettingStrength is < 0 or > 0.9 ||
            FlatSignalAdu == 0 || BiasExposure <= TimeSpan.Zero || DarkExposure <= TimeSpan.Zero ||
            FlatExposure <= TimeSpan.Zero || !double.IsFinite(Gain) || Gain < 0 ||
            !double.IsFinite(TemperatureC) || Defects is null || Defects.Any(defect =>
                defect is null || defect.X < 0 || defect.X >= width || defect.Y < 0 || defect.Y >= height) ||
            Defects.Select(static defect => (defect.X, defect.Y)).Distinct().Count() != Defects.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Synthetic calibration model or dimensions are invalid.");
        }
    }
}

public sealed record SyntheticCalibrationReferenceSet(
    Linear16Frame Bias,
    Linear16Frame Dark,
    Linear16Frame Flat,
    Linear16Frame DefectMask,
    ushort FlatNormalizationAdu,
    string AlgorithmVersion);

public sealed record SyntheticCalibrationLightResult(
    ReadOnlyMemory<byte> PixelData,
    RenderStatistics Statistics);

/// <summary>Generates deterministic software references and applies the same hidden sensor effects to virtual lights.</summary>
public static class SyntheticCalibrationReferenceGenerator
{
    public const string AlgorithmVersion = "synthetic-calibration-reference-generator-v1";

    public static string ComputeModelIdentitySha256(SyntheticCalibrationModelV1 model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(model));
    }

    public static SyntheticCalibrationReferenceSet Generate(
        int width,
        int height,
        CameraPixelFormat pixelFormat,
        SyntheticCalibrationModelV1 model)
    {
        ArgumentNullException.ThrowIfNull(model);
        ValidateFormat(pixelFormat);
        model.Validate(width, height);
        var length = checked(width * height * 2);
        var bias = new byte[length];
        var dark = new byte[length];
        var flat = new byte[length];
        var defects = new byte[length];
        var defectMap = model.Defects.ToDictionary(static defect => (defect.X, defect.Y));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = checked((y * width + x) * 2);
                var biasValue = Bias(model, x, y);
                var darkRate = DarkRate(model, x, y);
                var response = Response(model, x, y, width, height);
                Write(bias, offset, Quantize(biasValue));
                Write(dark, offset, Quantize(biasValue + darkRate * model.DarkExposure.TotalSeconds));
                Write(flat, offset, Quantize(
                    biasValue + darkRate * model.FlatExposure.TotalSeconds + model.FlatSignalAdu * response));
                Write(defects, offset, defectMap.ContainsKey((x, y)) ? (ushort)1 : (ushort)0);
            }
        }
        return new SyntheticCalibrationReferenceSet(
            new Linear16Frame(width, height, width * 2, pixelFormat, bias),
            new Linear16Frame(width, height, width * 2, pixelFormat, dark),
            new Linear16Frame(width, height, width * 2, pixelFormat, flat),
            new Linear16Frame(width, height, width * 2, pixelFormat, defects),
            model.FlatSignalAdu,
            AlgorithmVersion);
    }

    public static ReadOnlyMemory<byte> ApplyToLight(
        Linear16Frame idealLight,
        TimeSpan exposure,
        SyntheticCalibrationModelV1 model,
        CancellationToken cancellationToken = default)
        => ApplyToLightWithStatistics(idealLight, exposure, model, cancellationToken).PixelData;

    public static SyntheticCalibrationLightResult ApplyToLightWithStatistics(
        Linear16Frame idealLight,
        TimeSpan exposure,
        SyntheticCalibrationModelV1 model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(idealLight);
        ArgumentNullException.ThrowIfNull(model);
        ValidateFormat(idealLight.PixelFormat);
        model.Validate(idealLight.Width, idealLight.Height);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(exposure, TimeSpan.Zero);
        var output = new byte[checked(idealLight.StrideBytes * idealLight.Height)];
        var statistics = new StatisticsAccumulator();
        var defectMap = model.Defects.ToDictionary(static defect => (defect.X, defect.Y));
        for (var y = 0; y < idealLight.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < idealLight.Width; x++)
            {
                var destinationOffset = checked(y * idealLight.StrideBytes + x * 2);
                if (defectMap.TryGetValue((x, y), out var defect))
                {
                    Write(output, destinationOffset, statistics.AddAndQuantize(defect.FixedValueAdu, ushort.MaxValue));
                    continue;
                }
                var sourceOffset = checked(y * idealLight.StrideBytes + x * 2);
                var ideal = Read(idealLight.PixelData.Span, sourceOffset);
                var value = ideal * Response(model, x, y, idealLight.Width, idealLight.Height) +
                    Bias(model, x, y) + DarkRate(model, x, y) * exposure.TotalSeconds;
                Write(output, destinationOffset, statistics.AddAndQuantize(value, ushort.MaxValue));
            }
        }
        return new(output, statistics.Create());
    }

    private static double Bias(SyntheticCalibrationModelV1 model, int x, int y)
    {
        var row = Unit(model.Seed, 11, 0, y);
        var column = Unit(model.Seed, 13, x, 0);
        return model.BiasPedestalAdu + model.BiasPatternAmplitudeAdu * (row + column - 1);
    }

    private static double DarkRate(SyntheticCalibrationModelV1 model, int x, int y)
        => model.DarkCurrentAduPerSecond *
           (1 + model.DarkPatternFraction * (Unit(model.Seed, 17, x, y) * 2 - 1));

    private static double Response(SyntheticCalibrationModelV1 model, int x, int y, int width, int height)
    {
        var pixel = 1 + model.PixelResponseVariationFraction * (Unit(model.Seed, 19, x, y) * 2 - 1);
        var centerX = (width - 1) / 2d;
        var centerY = (height - 1) / 2d;
        var normalizer = Math.Max(1d, centerX * centerX + centerY * centerY);
        var dx = x - centerX;
        var dy = y - centerY;
        var radial = Math.Min(1d, (dx * dx + dy * dy) / normalizer);
        return pixel * (1 - model.VignettingStrength * radial);
    }

    private static double Unit(int seed, int salt, int x, int y)
    {
        var value = unchecked((uint)(seed * 0x9E3779B9 + salt * 0x85EBCA6B + x * 0xC2B2AE35 + y * 0x27D4EB2F));
        value ^= value >> 16;
        value *= 0x7FEB352D;
        value ^= value >> 15;
        value *= 0x846CA68B;
        value ^= value >> 16;
        return value / (double)uint.MaxValue;
    }

    private static void ValidateFormat(CameraPixelFormat pixelFormat)
    {
        if (pixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16))
        {
            throw new NotSupportedException("Synthetic calibration supports only Mono16 and BayerRggb16.");
        }
    }

    private static ushort Quantize(double value)
        => (ushort)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, ushort.MaxValue);

    private static ushort Read(ReadOnlySpan<byte> bytes, int offset)
        => (ushort)(bytes[offset] | bytes[offset + 1] << 8);

    private static void Write(byte[] bytes, int offset, ushort value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
    }
}
