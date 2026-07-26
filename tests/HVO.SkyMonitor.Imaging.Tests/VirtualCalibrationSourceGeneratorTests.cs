using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class VirtualCalibrationSourceGeneratorTests
{
    private static readonly TimeSpan Exposure = TimeSpan.FromSeconds(2);
    private static readonly VirtualCalibrationSourceModelV1 Model = new() { Seed = 208 };
    private static readonly VirtualCalibrationLightParameters LightParameters = new(
        TimeSpan.FromMilliseconds(1),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(1),
        Exposure,
        120,
        8,
        -10);

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

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public void ApplyToLightWithStatistics_ReplaysWithoutMutationAndPreservesNativeLayout(
        CameraPixelFormat pixelFormat)
    {
        var layout = CreateLayout(pixelFormat);
        var clean = CreateNativeLight(layout, 1200, 0xA5);
        var cleanBefore = clean.PixelData.ToArray();
        var layoutBefore = layout with { };
        var parameters = LightParameters with { };
        var parametersBefore = parameters with { };
        var model = Model with { };
        var modelBefore = model with { };

        var first = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(clean, parameters, model);
        var replay = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(clean, parameters, model);
        var changedModel = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            clean, parameters, model with { Seed = model.Seed + 1 });

        CollectionAssert.AreEqual(first.PixelData.ToArray(), replay.PixelData.ToArray());
        Assert.AreEqual(first.Statistics, replay.Statistics);
        Assert.AreEqual(VirtualCalibrationSourceGenerator.LightCorruptionAlgorithmVersion, first.AlgorithmVersion);
        Assert.AreEqual(layout.Width * layout.Height, first.Statistics.ActivePixelCount);
        Assert.AreEqual(0, first.Statistics.ClippedLow);
        Assert.AreEqual(0, first.Statistics.ClippedHigh);
        Assert.IsFalse(first.PixelData.Span.SequenceEqual(changedModel.PixelData.Span));
        Assert.AreEqual(layout.ByteLength, first.PixelData.Length);
        Assert.IsTrue(ReadActive(new CalibrationSourceFrame(layout, first.PixelData))
            .All(static value => value <= 4095));
        AssertPaddingIsZero(new CalibrationSourceFrame(layout, first.PixelData));
        CollectionAssert.AreEqual(cleanBefore, clean.PixelData.ToArray());
        Assert.AreSame(layout, clean.Layout);
        Assert.AreEqual(layoutBefore, layout);
        Assert.AreEqual(parametersBefore, parameters);
        Assert.AreEqual(modelBefore, model);
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public void PrepareAndApply_EqualsConveniencePathAndRejectsMismatchedLayout(
        CameraPixelFormat pixelFormat)
    {
        var layout = CreateLayout(pixelFormat);
        var clean = CreateNativeLight(layout, 1200, 0xA5);
        var cleanBefore = clean.PixelData.ToArray();
        var prepared = VirtualCalibrationSourceGenerator.Prepare(
            layout,
            LightParameters.BiasExposure,
            LightParameters.DarkExposure,
            LightParameters.FlatExposure,
            LightParameters.DefectExposure,
            LightParameters.Gain,
            LightParameters.Offset,
            LightParameters.TemperatureC,
            Model);

        var expected = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            clean, LightParameters, Model);
        var first = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            clean, LightParameters.LightExposure, prepared);
        var replay = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            clean, LightParameters.LightExposure, prepared);

        CollectionAssert.AreEqual(expected.PixelData.ToArray(), first.PixelData.ToArray());
        CollectionAssert.AreEqual(first.PixelData.ToArray(), replay.PixelData.ToArray());
        Assert.AreEqual(expected.Statistics, first.Statistics);
        Assert.AreEqual(first.Statistics, replay.Statistics);
        Assert.AreEqual(layout, prepared.Layout);
        Assert.AreEqual(LightParameters.BiasExposure, prepared.BiasExposure);
        Assert.AreEqual(LightParameters.DarkExposure, prepared.DarkExposure);
        Assert.AreEqual(LightParameters.FlatExposure, prepared.FlatExposure);
        Assert.AreEqual(LightParameters.DefectExposure, prepared.DefectExposure);
        Assert.AreEqual(LightParameters.Gain, prepared.Gain);
        Assert.AreEqual(LightParameters.Offset, prepared.Offset);
        Assert.AreEqual(LightParameters.TemperatureC, prepared.TemperatureC);
        Assert.AreEqual(
            VirtualCalibrationSourceGenerator.ComputeModelIdentitySha256(Model),
            prepared.ModelIdentitySha256);
        CollectionAssert.AreEqual(cleanBefore, clean.PixelData.ToArray());

        var mismatchedLayout = layout with { BlackLevel = layout.BlackLevel!.Value + 1 };
        Assert.ThrowsExactly<ArgumentException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                new CalibrationSourceFrame(mismatchedLayout, clean.PixelData),
                LightParameters.LightExposure,
                prepared));
    }

    [TestMethod]
    public void ApplyToLightWithStatistics_RejectsInvalidArgumentsAndCancellation()
    {
        var layout = CreateLayout(CameraPixelFormat.Mono16);
        var clean = CreateNativeLight(layout, 1200);

        Assert.ThrowsExactly<ArgumentNullException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(null!, LightParameters, Model));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(clean, null!, Model));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(clean, LightParameters, null!));
        Assert.ThrowsExactly<ArgumentException>(() => VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            new CalibrationSourceFrame(layout, new byte[checked((int)layout.ByteLength) - 1]),
            LightParameters,
            Model));
        Assert.ThrowsExactly<ArgumentException>(() => VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            new CalibrationSourceFrame(layout with { StoredCodeTransform = FrameStoredCodeTransform.LeftShiftedV1 },
                clean.PixelData),
            LightParameters,
            Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters with { BiasExposure = TimeSpan.Zero }, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters with { DarkExposure = TimeSpan.Zero }, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters with { FlatExposure = TimeSpan.Zero }, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters with { DefectExposure = TimeSpan.Zero }, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters with { LightExposure = TimeSpan.Zero }, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters with { Gain = double.NaN }, Model));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters, Model with { SchemaVersion = "future-model" }));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() =>
            VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
                clean, LightParameters, Model, cancellation.Token));
    }

    [TestMethod]
    public void CalculateFlatNormalization_UsesRoundedLinear16MeanWithoutMutationAndRejectsInvalidPayloads()
    {
        var flat = PackedBytes([100, 101, 102, 103]);
        var flatBefore = flat.ToArray();

        Assert.AreEqual(102, CalibrationMasterBuilder.CalculateFlatNormalization(flat));
        Assert.AreEqual(1, CalibrationMasterBuilder.CalculateFlatNormalization(PackedBytes([0, 0])));
        Assert.AreEqual(ushort.MaxValue,
            CalibrationMasterBuilder.CalculateFlatNormalization(PackedBytes([ushort.MaxValue, ushort.MaxValue])));
        CollectionAssert.AreEqual(flatBefore, flat);
        Assert.ThrowsExactly<ArgumentException>(() => CalibrationMasterBuilder.CalculateFlatNormalization([]));
        Assert.ThrowsExactly<ArgumentException>(() => CalibrationMasterBuilder.CalculateFlatNormalization([0]));
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public void ApplyAndIndependentThreeSourceMasters_CorrectionImprovesMaeWithinTwoNativeAdu(
        CameraPixelFormat pixelFormat)
    {
        var layout = CreateLayout(pixelFormat);
        var cleanNative = CreateNativeLight(layout, 1200);
        var affected = VirtualCalibrationSourceGenerator.ApplyToLightWithStatistics(
            cleanNative, LightParameters, Model);
        var bias = BuildIndependentMaster(VirtualCalibrationSourceKind.Bias, LightParameters.BiasExposure, false);
        var dark = BuildIndependentMaster(VirtualCalibrationSourceKind.Dark, LightParameters.DarkExposure, false);
        var flat = BuildIndependentMaster(VirtualCalibrationSourceKind.Flat, LightParameters.FlatExposure, false);
        var defect = BuildIndependentMaster(VirtualCalibrationSourceKind.Defect, LightParameters.DefectExposure, true);
        var clean = CalibrationMasterBuilder.Normalize(cleanNative);
        var corrupted = CalibrationMasterBuilder.Normalize(new CalibrationSourceFrame(layout, affected.PixelData));
        var flatNormalization = CalibrationMasterBuilder.CalculateFlatNormalization(flat.PixelData.Span);

        var corrected = Linear16ReferenceCalibration.Correct(
            LinearFrame(corrupted.Layout, corrupted.PixelData),
            LinearFrame(bias.Layout, bias.PixelData),
            LinearFrame(dark.Layout, dark.PixelData),
            LinearFrame(flat.Layout, flat.PixelData),
            LinearFrame(defect.Layout, defect.PixelData),
            new(LightParameters.LightExposure, LightParameters.DarkExposure,
                LightParameters.FlatExposure, flatNormalization));

        var expectedValues = ReadPacked(clean.PixelData.Span);
        var corruptedValues = ReadPacked(corrupted.PixelData.Span);
        var correctedValues = ReadPacked(corrected.PixelData.Span);
        var defectValues = ReadPacked(defect.PixelData.Span);
        var usableIndices = Enumerable.Range(0, expectedValues.Length)
            .Where(index => defectValues[index] == 0)
            .ToArray();
        var corruptedMae = usableIndices
            .Average(index => Math.Abs((double)corruptedValues[index] - expectedValues[index]));
        var correctedMae = usableIndices
            .Average(index => Math.Abs((double)correctedValues[index] - expectedValues[index]));
        var maximumNativeResidual = usableIndices.Max(index =>
            Math.Abs((double)correctedValues[index] - expectedValues[index]) *
            (layout.WhiteLevel!.Value - layout.BlackLevel!.Value) / ushort.MaxValue);

        Assert.IsLessThan(corruptedMae, correctedMae);
        Assert.IsLessThanOrEqualTo(2d, maximumNativeResidual);
        Assert.IsTrue(defectValues.Any(static value => value != 0));
        Assert.IsTrue(defectValues.All(static value => (value & ~0x000F) == 0));
        Assert.AreEqual(0x000F, defectValues.Aggregate((ushort)0, static (bits, value) => (ushort)(bits | value)));
        Assert.AreEqual(defectValues.Count(static value => value != 0), corrected.CorrectedDefectCount);
        Assert.IsTrue(Enumerable.Range(0, defectValues.Length)
            .Where(index => defectValues[index] != 0)
            .All(index => correctedValues[index] != ushort.MaxValue));

        CalibrationMasterResult BuildIndependentMaster(
            VirtualCalibrationSourceKind kind,
            TimeSpan exposure,
            bool defectMask)
        {
            var sources = Enumerable.Range(0, CalibrationMasterBuilder.RequiredSourceCount)
                .Select(index => VirtualCalibrationSourceGenerator.Generate(
                    kind,
                    index,
                    layout,
                    exposure,
                    LightParameters.Gain,
                    LightParameters.Offset,
                    LightParameters.TemperatureC,
                    Model))
                .ToArray();
            Assert.HasCount(CalibrationMasterBuilder.RequiredSourceCount, sources);
            return defectMask
                ? CalibrationMasterBuilder.BuildDefectMask(sources)
                : CalibrationMasterBuilder.BuildMedian(sources);
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

    private static CalibrationSourceFrame CreateNativeLight(
        FrameLayoutDescriptor layout,
        ushort value,
        byte padding = 0)
    {
        var pixels = new byte[checked((int)layout.ByteLength)];
        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
            {
                var offset = y * layout.StrideBytes + x * 2;
                pixels[offset] = (byte)value;
                pixels[offset + 1] = (byte)(value >> 8);
            }
            pixels.AsSpan(y * layout.StrideBytes + layout.Width * 2,
                layout.StrideBytes - layout.Width * 2).Fill(padding);
        }
        return new CalibrationSourceFrame(layout, pixels);
    }

    private static Linear16Frame LinearFrame(FrameLayoutDescriptor layout, ReadOnlyMemory<byte> pixels)
        => new(layout.Width, layout.Height, layout.StrideBytes, layout.PixelFormat, pixels);

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

    private static byte[] PackedBytes(IEnumerable<ushort> values)
    {
        var source = values.ToArray();
        var pixels = new byte[source.Length * 2];
        for (var index = 0; index < source.Length; index++)
        {
            pixels[index * 2] = (byte)source[index];
            pixels[index * 2 + 1] = (byte)(source[index] >> 8);
        }
        return pixels;
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
