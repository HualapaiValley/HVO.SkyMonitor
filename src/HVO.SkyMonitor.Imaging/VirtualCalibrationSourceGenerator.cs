using System.Text.Json;
using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

public enum VirtualCalibrationSourceKind
{
    Bias,
    Dark,
    Flat,
    Defect
}

/// <summary>
/// Versioned arbitrary software effects for deterministic calibration workflow evidence. Values are not measurements
/// or predictions of a physical camera, sensor, or illumination source.
/// </summary>
public sealed record VirtualCalibrationSourceModelV1
{
    public const string CurrentSchemaVersion = "virtual-calibration-source-model-v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public int Seed { get; init; } = 676;
    public double BiasLevelFraction { get; init; } = 0.06;
    public double BiasSpatialAmplitudeFraction { get; init; } = 0.008;
    public double DarkExposureSignalFractionPerSecond { get; init; } = 0.0005;
    public double DarkSpatialVariationFraction { get; init; } = 0.25;
    public double FlatSignalFraction { get; init; } = 0.55;
    public double FlatSpatialVariationFraction { get; init; } = 0.08;
    public double FlatEdgeFalloffFraction { get; init; } = 0.20;
    public double SourceNoiseAmplitudeFraction { get; init; } = 0.001;
    public double GainInfluenceFraction { get; init; } = 0.10;
    public double OffsetInfluenceFraction { get; init; } = 0.02;
    public double TemperatureInfluenceFraction { get; init; } = 0.10;
    public int PersistentDefectCount { get; init; } = 1;
    public int SourceSpecificDefectCount { get; init; } = 1;

    public void Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal) ||
            !Fraction(BiasLevelFraction) || !Fraction(BiasSpatialAmplitudeFraction) ||
            !Fraction(DarkExposureSignalFractionPerSecond) || !Fraction(DarkSpatialVariationFraction) ||
            !Fraction(FlatSignalFraction) || !Fraction(FlatSpatialVariationFraction) ||
            !Fraction(FlatEdgeFalloffFraction) || !Fraction(SourceNoiseAmplitudeFraction) ||
            SourceNoiseAmplitudeFraction <= 0 || !Fraction(GainInfluenceFraction) ||
            !Fraction(OffsetInfluenceFraction) || !Fraction(TemperatureInfluenceFraction) ||
            PersistentDefectCount is < 0 or > 1024 || SourceSpecificDefectCount is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(VirtualCalibrationSourceModelV1),
                "The virtual calibration source model is invalid.");
        }
    }

    private static bool Fraction(double value)
        => double.IsFinite(value) && value is >= 0 and <= 1;
}

public sealed record VirtualCalibrationLightParameters(
    TimeSpan BiasExposure,
    TimeSpan DarkExposure,
    TimeSpan FlatExposure,
    TimeSpan DefectExposure,
    TimeSpan LightExposure,
    double Gain,
    double Offset,
    double TemperatureC);

public sealed record VirtualCalibrationLightResult(
    ReadOnlyMemory<byte> PixelData,
    RenderStatistics Statistics,
    string AlgorithmVersion);

/// <summary>Immutable reusable masters for deterministic virtual calibration light corruption.</summary>
public sealed class PreparedVirtualCalibration
{
    private readonly ReadOnlyMemory<byte> _biasPixels;
    private readonly ReadOnlyMemory<byte> _darkPixels;
    private readonly ReadOnlyMemory<byte> _flatPixels;
    private readonly ReadOnlyMemory<byte> _defectPixels;

    internal PreparedVirtualCalibration(
        FrameLayoutDescriptor layout,
        TimeSpan biasExposure,
        TimeSpan darkExposure,
        TimeSpan flatExposure,
        TimeSpan defectExposure,
        double gain,
        double offset,
        double temperatureC,
        string modelIdentitySha256,
        CalibrationMasterResult bias,
        CalibrationMasterResult dark,
        CalibrationMasterResult flat,
        CalibrationMasterResult defect,
        ushort flatNormalization)
    {
        Layout = layout;
        BiasExposure = biasExposure;
        DarkExposure = darkExposure;
        FlatExposure = flatExposure;
        DefectExposure = defectExposure;
        Gain = gain;
        Offset = offset;
        TemperatureC = temperatureC;
        ModelIdentitySha256 = modelIdentitySha256;
        _biasPixels = bias.PixelData;
        _darkPixels = dark.PixelData;
        _flatPixels = flat.PixelData;
        _defectPixels = defect.PixelData;
        FlatNormalization = flatNormalization;
    }

    public FrameLayoutDescriptor Layout { get; }
    public TimeSpan BiasExposure { get; }
    public TimeSpan DarkExposure { get; }
    public TimeSpan FlatExposure { get; }
    public TimeSpan DefectExposure { get; }
    public double Gain { get; }
    public double Offset { get; }
    public double TemperatureC { get; }
    public string ModelIdentitySha256 { get; }

    internal ReadOnlySpan<byte> BiasPixels => _biasPixels.Span;
    internal ReadOnlySpan<byte> DarkPixels => _darkPixels.Span;
    internal ReadOnlySpan<byte> FlatPixels => _flatPixels.Span;
    internal ReadOnlySpan<byte> DefectPixels => _defectPixels.Span;
    internal ushort FlatNormalization { get; }
}

/// <summary>Generates one deterministic software-only native-code calibration source frame.</summary>
public static class VirtualCalibrationSourceGenerator
{
    public const string AlgorithmVersion = "virtual-calibration-source-generator-v1";
    public const string LightCorruptionAlgorithmVersion = "virtual-calibration-light-corruption-v1";
    public const ushort PersistentDefectBit = 1;
    public const ushort SourceZeroDefectBit = 2;
    public const ushort SourceOneDefectBit = 4;
    public const ushort SourceTwoDefectBit = 8;

    public static string ComputeModelIdentitySha256(VirtualCalibrationSourceModelV1 model)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Validate();
        return CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(model));
    }

    public static CalibrationSourceFrame Generate(
        VirtualCalibrationSourceKind kind,
        int sourceIndex,
        FrameLayoutDescriptor layout,
        TimeSpan exposure,
        double gain,
        double offset,
        double temperatureC,
        VirtualCalibrationSourceModelV1 model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(kind, sourceIndex, layout, exposure, gain, offset, temperatureC, model);

        var pixels = new byte[checked((int)layout.ByteLength)];
        var nativeMaximum = (1u << layout.SampleDepthBits) - 1u;
        var black = layout.BlackLevel!.Value;
        var white = layout.WhiteLevel!.Value;
        var codeRange = white - black;
        var defectBits = kind == VirtualCalibrationSourceKind.Defect
            ? CreateDefectBits(layout, sourceIndex, model)
            : null;

        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var pixelIndex = checked(y * layout.Width + x);
                var value = defectBits is null
                    ? GenerateLevel(kind, sourceIndex, x, y, layout.Width, layout.Height, exposure, gain, offset,
                        temperatureC, black, codeRange, model)
                    : defectBits.GetValueOrDefault(pixelIndex);
                var stored = (ushort)Math.Clamp(
                    Math.Round(value, MidpointRounding.AwayFromZero),
                    0,
                    nativeMaximum);
                if (defectBits is null && x == 0 && y == 0)
                {
                    var markerBase = Math.Min(stored, checked((ushort)(white - 2)));
                    stored = checked((ushort)(markerBase + sourceIndex));
                }
                var byteOffset = checked(y * layout.StrideBytes + x * 2);
                pixels[byteOffset] = (byte)stored;
                pixels[byteOffset + 1] = (byte)(stored >> 8);
            }
        }

        return new CalibrationSourceFrame(layout, pixels);
    }

    public static PreparedVirtualCalibration Prepare(
        FrameLayoutDescriptor layout,
        TimeSpan biasExposure,
        TimeSpan darkExposure,
        TimeSpan flatExposure,
        TimeSpan defectExposure,
        double gain,
        double offset,
        double temperatureC,
        VirtualCalibrationSourceModelV1 model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(
            VirtualCalibrationSourceKind.Bias,
            0,
            layout,
            biasExposure,
            gain,
            offset,
            temperatureC,
            model);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(darkExposure, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(flatExposure, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(defectExposure, TimeSpan.Zero);

        var modelIdentitySha256 = ComputeModelIdentitySha256(model);
        var bias = BuildMaster(VirtualCalibrationSourceKind.Bias, biasExposure, defectMask: false);
        var dark = BuildMaster(VirtualCalibrationSourceKind.Dark, darkExposure, defectMask: false);
        var flat = BuildMaster(VirtualCalibrationSourceKind.Flat, flatExposure, defectMask: false);
        var defect = BuildMaster(VirtualCalibrationSourceKind.Defect, defectExposure, defectMask: true);
        var flatNormalization = CalibrationMasterBuilder.CalculateFlatNormalization(flat.PixelData.Span);
        return new PreparedVirtualCalibration(
            layout,
            biasExposure,
            darkExposure,
            flatExposure,
            defectExposure,
            gain,
            offset,
            temperatureC,
            modelIdentitySha256,
            bias,
            dark,
            flat,
            defect,
            flatNormalization);

        CalibrationMasterResult BuildMaster(
            VirtualCalibrationSourceKind kind,
            TimeSpan exposure,
            bool defectMask)
        {
            var sources = Enumerable.Range(0, CalibrationMasterBuilder.RequiredSourceCount)
                .Select(index => Generate(
                    kind,
                    index,
                    layout,
                    exposure,
                    gain,
                    offset,
                    temperatureC,
                    model,
                    cancellationToken))
                .ToArray();
            return defectMask
                ? CalibrationMasterBuilder.BuildDefectMask(sources, cancellationToken)
                : CalibrationMasterBuilder.BuildMedian(sources, cancellationToken);
        }
    }

    public static VirtualCalibrationLightResult ApplyToLightWithStatistics(
        CalibrationSourceFrame cleanNativeLight,
        VirtualCalibrationLightParameters parameters,
        VirtualCalibrationSourceModelV1 model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanNativeLight);
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(model);
        cancellationToken.ThrowIfCancellationRequested();
        Validate(
            VirtualCalibrationSourceKind.Bias,
            0,
            cleanNativeLight.Layout,
            parameters.LightExposure,
            parameters.Gain,
            parameters.Offset,
            parameters.TemperatureC,
            model);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parameters.BiasExposure, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parameters.DarkExposure, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parameters.FlatExposure, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(parameters.DefectExposure, TimeSpan.Zero);
        if (cleanNativeLight.PixelData.Length != cleanNativeLight.Layout.ByteLength)
        {
            throw new ArgumentException("The clean light payload does not match its layout.", nameof(cleanNativeLight));
        }

        var prepared = Prepare(
            cleanNativeLight.Layout,
            parameters.BiasExposure,
            parameters.DarkExposure,
            parameters.FlatExposure,
            parameters.DefectExposure,
            parameters.Gain,
            parameters.Offset,
            parameters.TemperatureC,
            model,
            cancellationToken);
        return ApplyToLightWithStatistics(
            cleanNativeLight,
            parameters.LightExposure,
            prepared,
            cancellationToken);
    }

    public static VirtualCalibrationLightResult ApplyToLightWithStatistics(
        CalibrationSourceFrame cleanNativeLight,
        TimeSpan lightExposure,
        PreparedVirtualCalibration preparedCalibration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanNativeLight);
        ArgumentNullException.ThrowIfNull(preparedCalibration);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lightExposure, TimeSpan.Zero);
        if (cleanNativeLight.Layout != preparedCalibration.Layout)
        {
            throw new ArgumentException(
                "The clean light layout does not match the prepared calibration.",
                nameof(cleanNativeLight));
        }
        if (cleanNativeLight.PixelData.Length != cleanNativeLight.Layout.ByteLength)
        {
            throw new ArgumentException("The clean light payload does not match its layout.", nameof(cleanNativeLight));
        }

        var clean = CalibrationMasterBuilder.Normalize(cleanNativeLight, cancellationToken);
        var output = new byte[checked((int)cleanNativeLight.Layout.ByteLength)];
        var statistics = new StatisticsAccumulator();
        var black = checked((uint)cleanNativeLight.Layout.BlackLevel!.Value);
        var white = checked((uint)cleanNativeLight.Layout.WhiteLevel!.Value);
        var codeRange = white - black;

        for (var y = 0; y < cleanNativeLight.Layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < cleanNativeLight.Layout.Width; x++)
            {
                var packedOffset = checked((y * cleanNativeLight.Layout.Width + x) * 2);
                var biasValue = ReadPacked(preparedCalibration.BiasPixels, packedOffset);
                var darkSignal = Math.Max(0L, ReadPacked(preparedCalibration.DarkPixels, packedOffset) - biasValue);
                var scaledDark = ScaleRounded(
                    (ulong)darkSignal,
                    (ulong)lightExposure.Ticks,
                    (ulong)preparedCalibration.DarkExposure.Ticks);
                var flatDark = ScaleRounded(
                    (ulong)darkSignal,
                    (ulong)preparedCalibration.FlatExposure.Ticks,
                    (ulong)preparedCalibration.DarkExposure.Ticks);
                var flatSignal = Math.Max(
                    1L,
                    ReadPacked(preparedCalibration.FlatPixels, packedOffset) - biasValue - (long)flatDark);
                var cleanValue = ReadPacked(clean.PixelData.Span, packedOffset);
                var lightSignal = DivideRounded(
                    (ulong)cleanValue * (ulong)flatSignal,
                    preparedCalibration.FlatNormalization);
                var normalized = ReadPacked(preparedCalibration.DefectPixels, packedOffset) == 0
                    ? Math.Min((ulong)ushort.MaxValue, (ulong)biasValue + scaledDark + lightSignal)
                    : ushort.MaxValue;
                var native = normalized >= ushort.MaxValue
                    ? white
                    : black + DivideRounded(normalized * codeRange, ushort.MaxValue);
                var stored = statistics.AddAndQuantize(native, checked((ushort)((1u << cleanNativeLight.Layout.SampleDepthBits) - 1u)));
                var outputOffset = checked(y * cleanNativeLight.Layout.StrideBytes + x * 2);
                output[outputOffset] = (byte)stored;
                output[outputOffset + 1] = (byte)(stored >> 8);
            }
        }

        return new VirtualCalibrationLightResult(output, statistics.Create(), LightCorruptionAlgorithmVersion);
    }

    private static double GenerateLevel(
        VirtualCalibrationSourceKind kind,
        int sourceIndex,
        int x,
        int y,
        int width,
        int height,
        TimeSpan exposure,
        double gain,
        double offset,
        double temperatureC,
        double black,
        double codeRange,
        VirtualCalibrationSourceModelV1 model)
    {
        var kindSalt = (int)kind * 101;
        var rowPattern = SignedUnit(model.Seed, kindSalt + 11, 0, y);
        var columnPattern = SignedUnit(model.Seed, kindSalt + 13, x, 0);
        var bias = black + codeRange * (model.BiasLevelFraction +
            model.BiasSpatialAmplitudeFraction * (rowPattern + columnPattern) / 2);

        var gainControl = 1 + model.GainInfluenceFraction * BoundedPositive(gain);
        var offsetControl = codeRange * model.OffsetInfluenceFraction * BoundedSigned(offset);
        var temperatureControl = 1 + model.TemperatureInfluenceFraction * BoundedSigned(-temperatureC);
        var darkPattern = 1 + model.DarkSpatialVariationFraction * SignedUnit(model.Seed, kindSalt + 17, x, y);
        var darkSignal = codeRange * model.DarkExposureSignalFractionPerSecond * exposure.TotalSeconds *
            gainControl * temperatureControl * darkPattern;

        var centerX = (width - 1) / 2d;
        var centerY = (height - 1) / 2d;
        var radialNormalizer = Math.Max(1, centerX * centerX + centerY * centerY);
        var dx = x - centerX;
        var dy = y - centerY;
        var radial = Math.Min(1, (dx * dx + dy * dy) / radialNormalizer);
        var flatPattern = 1 + model.FlatSpatialVariationFraction * SignedUnit(model.Seed, kindSalt + 19, x, y);
        var flatSignal = codeRange * model.FlatSignalFraction * gainControl * flatPattern *
            (1 - model.FlatEdgeFalloffFraction * radial);

        var sourceCenter = sourceIndex - 1;
        var sourceNoise = codeRange * model.SourceNoiseAmplitudeFraction *
            (sourceCenter + SignedUnit(model.Seed, kindSalt + 31 + sourceIndex, x, y) * 0.25);
        var level = kind switch
        {
            VirtualCalibrationSourceKind.Bias => bias,
            VirtualCalibrationSourceKind.Dark => bias + darkSignal,
            VirtualCalibrationSourceKind.Flat => bias + darkSignal + flatSignal,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return level + offsetControl + sourceNoise;
    }

    private static Dictionary<int, ushort> CreateDefectBits(
        FrameLayoutDescriptor layout,
        int sourceIndex,
        VirtualCalibrationSourceModelV1 model)
    {
        var pixelCount = checked(layout.Width * layout.Height);
        var result = new Dictionary<int, ushort>();
        AddDefects(result, model.Seed, 401, model.PersistentDefectCount, pixelCount, PersistentDefectBit);
        var sourceBit = (ushort)(1 << (sourceIndex + 1));
        AddDefects(result, model.Seed, 409 + sourceIndex, model.SourceSpecificDefectCount, pixelCount, sourceBit);
        return result;
    }

    private static void AddDefects(
        Dictionary<int, ushort> bits,
        int seed,
        int salt,
        int count,
        int pixelCount,
        ushort bit)
    {
        for (var index = 0; index < count; index++)
        {
            var pixelIndex = (int)(Mix(seed, salt, index, 0) % (uint)pixelCount);
            while (bits.ContainsKey(pixelIndex))
            {
                pixelIndex = (pixelIndex + 1) % pixelCount;
            }
            bits[pixelIndex] = bit;
        }
    }

    private static void Validate(
        VirtualCalibrationSourceKind kind,
        int sourceIndex,
        FrameLayoutDescriptor layout,
        TimeSpan exposure,
        double gain,
        double offset,
        double temperatureC,
        VirtualCalibrationSourceModelV1 model)
    {
        model.Validate();
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }
        if (sourceIndex is < 0 or >= CalibrationMasterBuilder.RequiredSourceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceIndex));
        }
        if (!layout.Validate().IsValid ||
            layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            layout.ByteOrder != FrameByteOrder.LittleEndian || layout.ContainerDepthBits != 16 ||
            layout.Packing != FrameSamplePacking.ByteAligned ||
            layout.StoredCodeTransform != FrameStoredCodeTransform.RightAlignedV1 ||
            layout.LevelCodeSpace != FrameLevelCodeSpace.NativeSample ||
            layout.BlackLevel is not { } black || layout.WhiteLevel is not { } white ||
            !double.IsInteger(black) || !double.IsInteger(white) || white <= black ||
            layout.ByteLength > int.MaxValue ||
            model.PersistentDefectCount + model.SourceSpecificDefectCount > checked(layout.Width * layout.Height) ||
            (white - black) * model.SourceNoiseAmplitudeFraction < 2)
        {
            throw new ArgumentException(
                "The layout must describe Mono16 or RGGB16 little-endian, right-aligned native codes in a 16-bit container.",
                nameof(layout));
        }
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(exposure, TimeSpan.Zero);
        if (!double.IsFinite(gain) || gain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gain));
        }
        if (!double.IsFinite(offset))
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }
        if (!double.IsFinite(temperatureC))
        {
            throw new ArgumentOutOfRangeException(nameof(temperatureC));
        }
    }

    private static double BoundedPositive(double value)
        => value / (1 + value);

    private static double BoundedSigned(double value)
        => value / (1 + Math.Abs(value));

    private static ushort ReadPacked(ReadOnlySpan<byte> pixels, int offset)
        => (ushort)(pixels[offset] | pixels[offset + 1] << 8);

    private static ulong ScaleRounded(ulong value, ulong numerator, ulong denominator)
        => DivideRounded((UInt128)value * numerator, denominator);

    private static ulong DivideRounded(ulong numerator, ulong denominator)
        => (numerator + denominator / 2) / denominator;

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

    private static double SignedUnit(int seed, int salt, int x, int y)
        => Mix(seed, salt, x, y) / (double)uint.MaxValue * 2 - 1;

    private static uint Mix(int seed, int salt, int x, int y)
    {
        var value = unchecked((uint)seed * 0x9E3779B9u + (uint)salt * 0x85EBCA6Bu +
            (uint)x * 0xC2B2AE35u + (uint)y * 0x27D4EB2Fu);
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        return value;
    }
}
