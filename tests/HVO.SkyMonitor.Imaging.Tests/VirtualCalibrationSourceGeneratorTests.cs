using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualCalibrationSourceGeneratorTests
{
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(2);
    private static readonly VirtualCalibrationSourceModelV1 Model = new() { Seed = 208 };

    [TestMethod]
    public void Generate_ReplaysExactBytesPreservesInputsAndPadding()
    {
        var layout = CreateLayout(CameraPixelFormat.Mono16);
        var layoutBefore = layout with { };
        var model = Model with { };
        var modelBefore = model with { };

        var first = Generate(VirtualCalibrationSourceKind.Flat, 1, layout, model);
        var replay = Generate(VirtualCalibrationSourceKind.Flat, 1, layout, model);

        Assert.AreSame(layout, first.Layout);
        Assert.AreEqual(layoutBefore, layout);
        Assert.AreEqual(modelBefore, model);
        CollectionAssert.AreEqual(first.PixelData.ToArray(), replay.PixelData.ToArray());
        Assert.AreEqual(layout.ByteLength, first.PixelData.Length);
        AssertPaddingIsZero(first);
    }

    [TestMethod]
    public void Generate_DistinguishesSourceIndicesWithBoundedNoise()
    {
        var layout = CreateLayout(CameraPixelFormat.Mono16);
        var sources = Enumerable.Range(0, CalibrationMasterBuilder.RequiredSourceCount)
            .Select(index => Generate(VirtualCalibrationSourceKind.Bias, index, layout, Model))
            .ToArray();

        Assert.IsFalse(sources[0].PixelData.Span.SequenceEqual(sources[1].PixelData.Span));
        Assert.IsFalse(sources[1].PixelData.Span.SequenceEqual(sources[2].PixelData.Span));
        var values = sources.Select(source => ReadActive(source).Select(static value => (int)value).ToArray()).ToArray();
        var largestDifference = Enumerable.Range(0, values[0].Length)
            .Max(index => values.Max(source => source[index]) - values.Min(source => source[index]));
        Assert.IsLessThanOrEqualTo(12, largestDifference);
    }

    [TestMethod]
    [DataRow(VirtualCalibrationSourceKind.Bias)]
    [DataRow(VirtualCalibrationSourceKind.Dark)]
    [DataRow(VirtualCalibrationSourceKind.Flat)]
    [DataRow(VirtualCalibrationSourceKind.Defect)]
    public void Generate_AllKindsContainRepeatableSpatialValues(VirtualCalibrationSourceKind kind)
    {
        var source = Generate(kind, 0, CreateLayout(CameraPixelFormat.Mono16), Model);
        var values = ReadActive(source);

        Assert.IsGreaterThan(1, values.Distinct().Count());
        CollectionAssert.AreEqual(values, ReadActive(Generate(kind, 0, source.Layout, Model)));
        if (kind == VirtualCalibrationSourceKind.Defect)
        {
            Assert.IsTrue(values.Any(static value => value != 0));
            Assert.IsTrue(values.All(static value => (value & ~0x000F) == 0));
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16, ColorFilterArrayPattern.None)]
    [DataRow(CameraPixelFormat.BayerRggb16, ColorFilterArrayPattern.Rggb)]
    public void Generate_SupportsMonoAndRggbNativeLayouts(
        CameraPixelFormat pixelFormat,
        ColorFilterArrayPattern cfaPattern)
    {
        var layout = CreateLayout(pixelFormat);

        var source = Generate(VirtualCalibrationSourceKind.Flat, 2, layout, Model);

        Assert.AreEqual(pixelFormat, source.Layout.PixelFormat);
        Assert.AreEqual(cfaPattern, source.Layout.CfaPattern);
        Assert.AreEqual(FrameByteOrder.LittleEndian, source.Layout.ByteOrder);
        Assert.AreEqual(FrameStoredCodeTransform.RightAlignedV1, source.Layout.StoredCodeTransform);
        Assert.AreEqual(FrameLevelCodeSpace.NativeSample, source.Layout.LevelCodeSpace);
    }

    [TestMethod]
    public void Generate_RightAligned12In16NeverExceedsNativeMaximum()
    {
        var layout = CreateLayout(CameraPixelFormat.BayerRggb16);

        foreach (var kind in Enum.GetValues<VirtualCalibrationSourceKind>())
        {
            var payloads = new List<byte[]>();
            for (var sourceIndex = 0; sourceIndex < CalibrationMasterBuilder.RequiredSourceCount; sourceIndex++)
            {
                var source = VirtualCalibrationSourceGenerator.Generate(
                    kind,
                    sourceIndex,
                    layout,
                    TimeSpan.MaxValue,
                    double.MaxValue,
                    -double.MaxValue,
                    double.MaxValue,
                    Model);
                Assert.IsTrue(ReadActive(source).All(static value => value <= 4095));
                payloads.Add(source.PixelData.ToArray());
            }
            Assert.AreEqual(payloads.Count, payloads.Select(Convert.ToHexString).Distinct(StringComparer.Ordinal).Count());
        }
    }

    [TestMethod]
    public void Generate_UsesSuppliedAcquisitionControls()
    {
        var layout = CreateLayout(CameraPixelFormat.Mono16);
        var baseline = VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Flat, 1, layout, Exposure, 120, 8, -10, Model).PixelData;
        var changedExposure = VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Flat, 1, layout, TimeSpan.FromSeconds(20), 120, 8, -10, Model).PixelData;
        var changedGain = VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Flat, 1, layout, Exposure, 0, 8, -10, Model).PixelData;
        var changedOffset = VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Flat, 1, layout, Exposure, 120, -100, -10, Model).PixelData;
        var changedTemperature = VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Flat, 1, layout, Exposure, 120, 8, 100, Model).PixelData;

        Assert.IsFalse(baseline.Span.SequenceEqual(changedExposure.Span));
        Assert.IsFalse(baseline.Span.SequenceEqual(changedGain.Span));
        Assert.IsFalse(baseline.Span.SequenceEqual(changedOffset.Span));
        Assert.IsFalse(baseline.Span.SequenceEqual(changedTemperature.Span));
    }

    [TestMethod]
    public void Generate_FailsClosedForInvalidInputs()
    {
        var layout = CreateLayout(CameraPixelFormat.Mono16);
        Assert.ThrowsExactly<ArgumentNullException>(() => Generate(VirtualCalibrationSourceKind.Bias, 0, null!, Model));
        Assert.ThrowsExactly<ArgumentNullException>(() => Generate(VirtualCalibrationSourceKind.Bias, 0, layout, null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Generate((VirtualCalibrationSourceKind)99, 0, layout, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Generate(VirtualCalibrationSourceKind.Bias, -1, layout, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Generate(VirtualCalibrationSourceKind.Bias, 3, layout, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout, TimeSpan.Zero, 10, 12, -5, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout, Exposure, double.NaN, 12, -5, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout, Exposure, 10, double.PositiveInfinity, -5, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout, Exposure, 10, 12, double.NaN, Model));

        Assert.ThrowsExactly<ArgumentException>(() => Generate(
            VirtualCalibrationSourceKind.Bias, 0,
            layout with { StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1 }, Model));
        Assert.ThrowsExactly<ArgumentException>(() => Generate(
            VirtualCalibrationSourceKind.Bias, 0,
            layout with { LevelCodeSpace = FrameLevelCodeSpace.StoredContainer }, Model));
        Assert.ThrowsExactly<ArgumentException>(() => Generate(
            VirtualCalibrationSourceKind.Bias, 0,
            layout with { ByteOrder = FrameByteOrder.BigEndian }, Model));
        Assert.ThrowsExactly<ArgumentException>(() => Generate(
            VirtualCalibrationSourceKind.Bias, 0,
            layout with
            {
                PixelFormat = CameraPixelFormat.Mono8,
                SampleDepthBits = 8,
                ContainerDepthBits = 8,
                StrideBytes = layout.Width,
                ByteLength = layout.Width * layout.Height
            }, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout,
            Model with { SchemaVersion = "future-model" }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout,
            Model with { SourceNoiseAmplitudeFraction = 0 }));
        Assert.ThrowsExactly<ArgumentException>(() => Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout,
            Model with { SourceNoiseAmplitudeFraction = double.Epsilon }));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => VirtualCalibrationSourceGenerator.Generate(
            VirtualCalibrationSourceKind.Bias, 0, layout, Exposure, 10, 12, -5, Model, cancellation.Token));
    }

    [TestMethod]
    public void ComputeModelIdentity_IsStableAndIncludesVersionedModelValues()
    {
        var first = VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(Model);
        var replay = VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(Model with { });
        var changed = VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(Model with { Seed = Model.Seed + 1 });

        Assert.AreEqual(64, first.Length);
        Assert.AreEqual(first, replay);
        Assert.AreNotEqual(first, changed);
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(null!));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(Model with { FlatSignalFraction = double.NaN }));
    }

    [TestMethod]
    public void Generate_ProducesAllKindsAcceptedByCalibrationMasterBuilder()
    {
        var layout = CreateLayout(CameraPixelFormat.BayerRggb16);

        foreach (var kind in Enum.GetValues<VirtualCalibrationSourceKind>())
        {
            var sources = Enumerable.Range(0, CalibrationMasterBuilder.RequiredSourceCount)
                .Select(index => Generate(kind, index, layout, Model))
                .ToArray();
            var master = kind == VirtualCalibrationSourceKind.Defect
                ? CalibrationMasterBuilder.BuildDefectMask(sources)
                : CalibrationMasterBuilder.BuildMedian(sources);

            Assert.AreEqual(CalibrationMasterBuilder.RequiredSourceCount, master.SourceCount);
            Assert.AreEqual(layout.Width * 2, master.Layout.StrideBytes);
            Assert.AreEqual(FrameStoredCodeTransform.IdentityV1, master.Layout.StoredCodeTransform);
            if (kind == VirtualCalibrationSourceKind.Defect)
            {
                var masks = ReadPacked(master.PixelData.Span);
                Assert.IsTrue(masks.Any(static value => value != 0));
                Assert.IsTrue(masks.All(static value => (value & ~0x000F) == 0));
                foreach (var source in sources)
                {
                    Assert.AreEqual(
                        Model.PersistentDefectCount + Model.SourceSpecificDefectCount,
                        ReadActive(source).Count(static value => value != 0));
                }
            }
        }
    }

    private static CalibrationSourceFrame Generate(
        VirtualCalibrationSourceKind kind,
        int sourceIndex,
        FrameLayoutDescriptor layout,
        VirtualCalibrationSourceModelV1 model)
        => VirtualCalibrationSourceGenerator.Generate(kind, sourceIndex, layout, Exposure, 120, 8, -10, model);

    private static FrameLayoutDescriptor CreateLayout(CameraPixelFormat pixelFormat)
    {
        const int width = 8;
        const int height = 6;
        const int stride = width * 2 + 4;
        return new FrameLayoutDescriptor(
            width,
            height,
            stride,
            pixelFormat,
            FrameByteOrder.LittleEndian,
            12,
            16,
            FrameSamplePacking.ByteAligned,
            pixelFormat == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            64,
            4095,
            stride * height)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };
    }

    private static ushort[] ReadActive(CalibrationSourceFrame source)
    {
        var values = new ushort[source.Layout.Width * source.Layout.Height];
        for (var y = 0; y < source.Layout.Height; y++)
        {
            for (var x = 0; x < source.Layout.Width; x++)
            {
                var byteOffset = y * source.Layout.StrideBytes + x * 2;
                values[y * source.Layout.Width + x] = (ushort)(
                    source.PixelData.Span[byteOffset] | source.PixelData.Span[byteOffset + 1] << 8);
            }
        }
        return values;
    }

    private static ushort[] ReadPacked(ReadOnlySpan<byte> pixels)
    {
        var values = new ushort[pixels.Length / 2];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)(pixels[index * 2] | pixels[index * 2 + 1] << 8);
        }
        return values;
    }

    private static void AssertPaddingIsZero(CalibrationSourceFrame source)
    {
        var activeBytes = source.Layout.Width * 2;
        for (var y = 0; y < source.Layout.Height; y++)
        {
            var padding = source.PixelData.Span.Slice(
                y * source.Layout.StrideBytes + activeBytes,
                source.Layout.StrideBytes - activeBytes);
            Assert.IsTrue(padding.SequenceEqual(new byte[padding.Length]));
        }
    }
}
