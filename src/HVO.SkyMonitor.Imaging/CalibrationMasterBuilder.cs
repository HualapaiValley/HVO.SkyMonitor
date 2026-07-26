using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Imaging;

public static class CalibrationMasterAlgorithms
{
    public const string MedianV1 = "calibration-median-v1";
    public const string BitwiseOrV1 = "calibration-bitwise-or-v1";
}

public sealed record CalibrationSourceFrame(
    FrameLayoutDescriptor Layout,
    ReadOnlyMemory<byte> PixelData);

public sealed record CalibrationMasterResult(
    FrameLayoutDescriptor Layout,
    ReadOnlyMemory<byte> PixelData,
    int SourceCount,
    string AlgorithmVersion);

public sealed record NormalizedCalibrationFrame(
    FrameLayoutDescriptor Layout,
    ReadOnlyMemory<byte> PixelData);

/// <summary>Builds deterministic normalized Linear16 calibration masters from native 16-bit containers.</summary>
public static class CalibrationMasterBuilder
{
    public const int RequiredSourceCount = 3;
    public const string NormalizationAlgorithmVersion = "calibration-normalize-linear16-v1";

    public static CalibrationMasterResult BuildMedian(
        IReadOnlyList<CalibrationSourceFrame> sources,
        CancellationToken cancellationToken = default)
        => Build(sources, defectMask: false, cancellationToken);

    public static CalibrationMasterResult BuildDefectMask(
        IReadOnlyList<CalibrationSourceFrame> sources,
        CancellationToken cancellationToken = default)
        => Build(sources, defectMask: true, cancellationToken);

    public static NormalizedCalibrationFrame Normalize(
        CalibrationSourceFrame source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSource(source, nameof(source));
        var layout = source.Layout;
        var output = new byte[checked(layout.Width * layout.Height * 2)];
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var value = Normalize(Read(source, x, y), layout);
                var offset = checked((y * layout.Width + x) * 2);
                output[offset] = (byte)value;
                output[offset + 1] = (byte)(value >> 8);
            }
        }
        return new NormalizedCalibrationFrame(CreateOutputLayout(layout), output);
    }

    public static FrameLayoutDescriptor CreateNormalizedLayout(FrameLayoutDescriptor layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return CreateOutputLayout(layout);
    }

    private static CalibrationMasterResult Build(
        IReadOnlyList<CalibrationSourceFrame> sources,
        bool defectMask,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sources);
        cancellationToken.ThrowIfCancellationRequested();
        if (sources.Count != RequiredSourceCount || sources.Any(static source => source is null))
        {
            throw new ArgumentException("Exactly three non-null calibration sources are required.", nameof(sources));
        }
        var first = sources[0];
        ValidateSource(first, nameof(sources));
        for (var index = 1; index < sources.Count; index++)
        {
            ValidateSource(sources[index], nameof(sources));
            if (sources[index].Layout != first.Layout)
            {
                throw new ArgumentException("Calibration sources must have identical layouts.", nameof(sources));
            }
        }

        var layout = first.Layout;
        var output = new byte[checked(layout.Width * layout.Height * 2)];
        for (var y = 0; y < layout.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < layout.Width; x++)
            {
                var firstValue = Read(sources[0], x, y);
                var secondValue = Read(sources[1], x, y);
                var thirdValue = Read(sources[2], x, y);
                ushort result;
                if (defectMask)
                {
                    result = checked((ushort)(
                        DecodeNativeSample(firstValue, layout) |
                        DecodeNativeSample(secondValue, layout) |
                        DecodeNativeSample(thirdValue, layout)));
                }
                else
                {
                    firstValue = Normalize(firstValue, layout);
                    secondValue = Normalize(secondValue, layout);
                    thirdValue = Normalize(thirdValue, layout);
                    result = Median(firstValue, secondValue, thirdValue);
                }
                var offset = checked((y * layout.Width + x) * 2);
                output[offset] = (byte)result;
                output[offset + 1] = (byte)(result >> 8);
            }
        }
        return new CalibrationMasterResult(
            CreateOutputLayout(layout),
            output,
            RequiredSourceCount,
            defectMask ? CalibrationMasterAlgorithms.BitwiseOrV1 : CalibrationMasterAlgorithms.MedianV1);
    }

    private static void ValidateSource(CalibrationSourceFrame source, string parameterName)
    {
        if (!source.Layout.Validate().IsValid ||
            source.Layout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            source.Layout.ByteOrder != FrameByteOrder.LittleEndian ||
            source.Layout.ContainerDepthBits != 16 ||
            source.Layout.Packing != FrameSamplePacking.ByteAligned ||
            source.Layout.StrideBytes < checked(source.Layout.Width * 2) ||
            source.Layout.BlackLevel is not { } black || source.Layout.WhiteLevel is not { } white ||
            !double.IsInteger(black) || !double.IsInteger(white) || black < 0 || white > ushort.MaxValue ||
            white <= black || source.PixelData.Length != source.Layout.ByteLength)
        {
            throw new ArgumentException("A calibration source layout or payload is invalid.", parameterName);
        }
        _ = ResolveNativeCodeMaximum(source.Layout);
    }

    private static ushort Normalize(ushort stored, FrameLayoutDescriptor layout)
    {
        var levelCode = DecodeLevelCode(stored, layout);
        var black = checked((uint)layout.BlackLevel!.Value);
        var white = checked((uint)layout.WhiteLevel!.Value);
        if (levelCode <= black)
        {
            return 0;
        }
        if (levelCode >= white)
        {
            return ushort.MaxValue;
        }
        var numerator = checked((ulong)(levelCode - black) * ushort.MaxValue);
        var denominator = white - black;
        return (ushort)((numerator + denominator / 2) / denominator);
    }

    private static uint DecodeLevelCode(ushort stored, FrameLayoutDescriptor layout)
    {
        if (layout.SampleDepthBits == layout.ContainerDepthBits &&
            layout.StoredCodeTransform is null && layout.LevelCodeSpace is null)
        {
            return stored;
        }
        if (layout.LevelCodeSpace == FrameLevelCodeSpace.StoredContainer)
        {
            _ = DecodeNativeSample(stored, layout);
            return stored;
        }
        if (layout.LevelCodeSpace != FrameLevelCodeSpace.NativeSample)
        {
            throw new ArgumentException("The calibration source level code space is not explicit.", nameof(layout));
        }
        return DecodeNativeSample(stored, layout);
    }

    private static uint DecodeNativeSample(ushort stored, FrameLayoutDescriptor layout)
    {
        if (layout.SampleDepthBits == layout.ContainerDepthBits && layout.StoredCodeTransform is null)
        {
            return stored;
        }
        var nativeMaximum = ResolveNativeCodeMaximum(layout);
        var shift = layout.ContainerDepthBits - layout.SampleDepthBits;
        return layout.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.RightAlignedV1 when stored <= nativeMaximum => stored,
            FrameStoredCodeTransform.LeftShiftedV1 when shift == 0 || (stored & ((1u << shift) - 1u)) == 0 =>
                (uint)stored >> shift,
            FrameStoredCodeTransform.FullRangeScaledV1 =>
                (uint)(((ulong)stored * nativeMaximum + ushort.MaxValue / 2u) / ushort.MaxValue),
            FrameStoredCodeTransform.IdentityV1 when layout.SampleDepthBits == layout.ContainerDepthBits => stored,
            _ => throw new ArgumentException("The calibration source stored-code transform is incompatible.", nameof(layout))
        };
    }

    private static uint ResolveNativeCodeMaximum(FrameLayoutDescriptor layout)
    {
        if (layout.SampleDepthBits is < 1 or > 16)
        {
            throw new ArgumentException("The calibration source sample depth is invalid.", nameof(layout));
        }
        return (1u << layout.SampleDepthBits) - 1u;
    }

    private static ushort Read(CalibrationSourceFrame source, int x, int y)
    {
        var offset = checked(y * source.Layout.StrideBytes + x * 2);
        var span = source.PixelData.Span;
        return (ushort)(span[offset] | span[offset + 1] << 8);
    }

    private static ushort Median(ushort first, ushort second, ushort third)
        => first > second
            ? second > third ? second : Math.Min(first, third)
            : first > third ? first : Math.Min(second, third);

    private static FrameLayoutDescriptor CreateOutputLayout(FrameLayoutDescriptor input)
        => input with
        {
            StrideBytes = checked(input.Width * 2),
            SampleDepthBits = 16,
            ContainerDepthBits = 16,
            Packing = FrameSamplePacking.ByteAligned,
            BlackLevel = 0,
            WhiteLevel = ushort.MaxValue,
            ByteLength = checked((long)input.Width * input.Height * 2),
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };
}
