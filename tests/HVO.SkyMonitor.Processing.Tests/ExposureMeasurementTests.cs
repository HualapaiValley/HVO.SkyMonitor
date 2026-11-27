using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "MSTest handles its async tests.")]
public sealed class ExposureMeasurementTests
{
    private static readonly DateTimeOffset ExposureTime = new(2026, 11, 19, 4, 0, 0, TimeSpan.Zero);
    private static readonly ProcessingCompatibilityIdentity Compatibility =
        new("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");
    private static readonly RecipeIdentityDescriptor RawRecipe =
        RecipeIdentityDescriptor.Create("raw", "1.0.0", "fixture-v1", JsonSerializer.SerializeToElement(new { }));
    private static readonly JsonSerializerOptions MeasurementJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    [TestMethod]
    public async Task RawStatisticsUseStoredWhiteExcludeStrideAndRetainCalibrationFailures()
    {
        var layout = Layout(2, 2) with { ByteLength = 12, StrideBytes = 6, ByteOrder = FrameByteOrder.BigEndian };
        var raw = CreateRaw(layout, new byte[] { 0x0f, 0xfe, 0x0f, 0xff, 0xff, 0xff, 0x10, 0, 0, 0, 0xff, 0xff });
        foreach (var disposition in new[]
                 {
                     ExposureCalibrationDisposition.Missing, ExposureCalibrationDisposition.Failed,
                     ExposureCalibrationDisposition.Incompatible
                 })
        {
            var input = Bind(raw, null, disposition, "test-" + disposition);
            var result = await Run(raw, null, input);
            Assert.AreEqual(disposition, result.Input.Calibration);
            Assert.AreEqual("test-" + disposition, result.Input.CalibrationReason);
            Assert.AreEqual(ExposureMeasurementStatus.Unavailable, result.Sources.Status);
            Assert.IsNull(result.Hfd.ValidCount);
            var brightness = result.RawBrightness!;
            Assert.AreEqual(4L, brightness.SampleCount);
            Assert.AreEqual((ushort)0, brightness.Minimum);
            Assert.AreEqual((ushort)4096, brightness.Maximum);
            Assert.AreEqual(12285UL, brightness.Sum);
            Assert.AreEqual(3071.25, brightness.Mean);
            Assert.AreEqual(1L, brightness.ZeroCount);
            Assert.AreEqual((ushort)4095, brightness.SensorClipping.StoredWhite);
            Assert.AreEqual(2L, brightness.SensorClipping.Count);
        }
    }

    [TestMethod]
    [DataRow(2800d)]
    [DataRow(2799.75d)]
    public async Task NativeWhiteUsesTheDownRoundedFullRangeEncoding(double white)
    {
        var layout = Layout(3, 1) with
        {
            StoredCodeTransform = FrameStoredCodeTransform.FullRangeScaledV1,
            WhiteLevel = white
        };
        // These are the independently quantized stored codes of native 2799, 2800 and 2801.
        ushort[] codes = [44794, 44810, 44826];
        var raw = CreateRaw(layout, Generate(layout, (x, _) => codes[x]));
        var result = await Run(raw, null, Bind(raw, null, ExposureCalibrationDisposition.Missing, "calibration-missing"));
        Assert.AreEqual((ushort)44810, result.RawBrightness!.SensorClipping.StoredWhite);
        Assert.AreEqual(2L, result.RawBrightness.SensorClipping.Count);
    }

    [TestMethod]
    public async Task OpaqueAdcRetainsBrightnessAndWithholdsThresholdDependentScience()
    {
        var layout = Layout(96, 96) with { StoredCodeTransform = FrameStoredCodeTransform.OpaqueContainerV1 };
        var raw = CreateRaw(layout, Generate(layout, static (_, _) => 93));
        var result = await Run(raw, null, Bind(raw, null, ExposureCalibrationDisposition.Incompatible,
            FrameWhiteThreshold.OpaqueAdcReason));
        Assert.AreEqual(93, result.RawBrightness!.Mean);
        Assert.IsNull(result.RawBrightness.SensorClipping.Count);
        Assert.AreEqual(FrameWhiteThreshold.OpaqueAdcReason, result.RawBrightness.SensorClipping.Reason);
        Assert.AreEqual(ExposureMeasurementStatus.Unavailable, result.Sources.Status);
        Assert.AreEqual(FrameWhiteThreshold.OpaqueAdcReason, result.Sources.Reason);
        Assert.IsNull(result.Hfd.ValidCount);
    }

    [TestMethod]
    public async Task SeparatesValidEmptyRejectedAndUnsupportedBackground()
    {
        var layout = Layout(128, 128);
        var blank = CreateRaw(layout, Generate(layout, static (_, _) => 91));
        var blankLinear = await Normalize(blank);
        var empty = await Run(blank, blankLinear, Bind(blank, blankLinear));
        Assert.AreEqual(ExposureMeasurementStatus.Empty, empty.Sources.Status);
        Assert.AreEqual(0, empty.Sources.CandidateCount);
        Assert.AreEqual(0, empty.Hfd.ValidCount);
        Assert.IsNull(empty.Hfd.MeanPixels);

        var hot = CreateRaw(layout, Generate(layout, static (x, y) => (ushort)(x == 63 && y == 63 ? 4050 : 91)));
        var hotLinear = await Normalize(hot);
        var rejected = await Run(hot, hotLinear, Bind(hot, hotLinear));
        Assert.AreEqual(ExposureMeasurementStatus.Unsuitable, rejected.Sources.Status);
        Assert.AreEqual("all-candidates-rejected", rejected.Sources.Reason);
        Assert.AreEqual(1, rejected.Sources.Exclusions.Count);
        Assert.AreEqual(StellarExclusionReasons.HotPixelOrCosmicRay, rejected.Sources.Exclusions[0].ReasonCode);
        Assert.AreEqual(0, rejected.Hfd.ValidCount);

        var clipped = CreateRaw(layout, Generate(layout, static (_, _) => 4095));
        var clippedLinear = await Normalize(clipped);
        var unsuitable = await Run(clipped, clippedLinear, Bind(clipped, clippedLinear));
        Assert.AreEqual(ExposureMeasurementStatus.Unsuitable, unsuitable.Sources.Status);
        Assert.AreEqual("insufficient-background", unsuitable.Sources.Reason);
        Assert.AreEqual(ExposureMeasurementStatus.Unavailable, unsuitable.Hfd.Status);
        Assert.IsNull(unsuitable.Hfd.ValidCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NormalizedPositionsAndValidHfdMatchIndependentGaussianAndReaderChecksBindings(bool bayer)
    {
        var layout = Layout(176, 160, bayer);
        var first = new PixelPoint(48.2, 52.65);
        var second = new PixelPoint(118.7, 101.25);
        var raw = CreateRaw(layout, Generate(layout, (x, y) =>
            (ushort)Math.Round(91 + Gaussian(x, y, first, 1.35, 2700) + Gaussian(x, y, second, 1.55, 2100))));
        var linear = await Normalize(raw);
        var input = Bind(raw, linear);
        var request = ExposureMeasurementProducts.Request(input, raw, linear);
        var executor = new ProcessingRecipeExecutor();
        var product = await Produce(request);
        var retained = ProductArtifact(product, raw);
        var result = ExposureMeasurementProducts.Read(retained, input);
        ProcessingRecipeTests.AssertProductMatchesContract(request, product);
        Assert.AreEqual(ExposureCalibrationDisposition.NormalizationOnly, result.Input.Calibration);
        Assert.AreEqual(ExposureMeasurementStatus.Usable, result.Sources.Status);
        Assert.AreEqual(2, result.Sources.Detections.Count);
        AssertNear(result.Sources.Detections, first, bayer ? 0.4 : 0.12);
        AssertNear(result.Sources.Detections, second, bayer ? 0.4 : 0.12);
        Assert.AreEqual(2, result.Hfd.ValidCount, string.Join(", ", result.Hfd.Samples.Select(sample => sample.Reason)));
        const double gaussianHfdPerSigma = 2.354820045;
        Assert.AreEqual(1.45 * gaussianHfdPerSigma, result.Hfd.MeanPixels!.Value, bayer ? 0.8 : 0.35);
        Assert.AreEqual(result.Hfd.MeanPixels, result.Hfd.MedianPixels);
        Assert.AreEqual(result.Sources.Detections[0].Pixel,
            ExposureMeasurementConsumers.RegistrationDetections(retained, input)[0].Pixel);

        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(retained,
            input with { Linear = input.Linear! with { PayloadSha256 = new string('B', 64) } }));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(retained,
            input with { Raw = input.Raw with { DescriptorSha256 = new string('B', 64) } }));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(retained with
        { ContentIdentitySha256 = new string('A', 64) }, input));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(retained with
        { SourceArtifactIds = [raw.ArtifactId] }, input));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(retained with
        { CaptureId = Guid.NewGuid() }, input));

        var ambiguous = await executor.ExecuteAsync(request with { Inputs = [raw, linear with { Payload = new byte[linear.Payload.Length] }] });
        Assert.AreEqual("measurement.linear-binding-mismatch", ambiguous.ReasonCode);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () => await executor.ExecuteAsync(request, cancelled.Token));
        var retry = await Produce(request);
        Assert.AreEqual(product.OutputIdentitySha256, retry.OutputIdentitySha256);
        CollectionAssert.AreEqual(product.Payload.ToArray(), retry.Payload.ToArray());

        await AssertFocusBudgetPrefersEligibleDetections(bayer, eligibleCount: 36);
        await AssertFocusBudgetPrefersEligibleDetections(bayer, eligibleCount: 4);
    }

    [TestMethod]
    public async Task PinnedOpticalApertureAppliesToScienceWithoutMaskingRawBrightness()
    {
        var layout = Layout(128, 128);
        var onAxis = new PixelPoint(65.5, 68.5);
        var offAxis = new PixelPoint(110.5, 110.5);
        var raw = CreateRaw(layout, Generate(layout, (x, y) => (ushort)Math.Round(91 +
            Gaussian(x, y, onAxis, 1.5, 2100) + Gaussian(x, y, offAxis, 1.4, 2500))));
        var linear = await Normalize(raw);
        var input = Bind(raw, linear);
        var full = await Run(raw, linear, input);
        var masked = await Run(raw, linear, input with
        {
            ApertureProjection = new(ProjectionModel.EquidistantFisheye, 64, 64, 42, 42, 128, 128,
                ProjectionAperture.Circular, 54, 90)
        });
        Assert.AreEqual(full.RawBrightness, masked.RawBrightness);
        Assert.HasCount(2, full.Sources.Detections);
        Assert.HasCount(1, masked.Sources.Detections);
        AssertNear(masked.Sources.Detections, onAxis, 0.15);
        Assert.AreEqual(1, masked.Hfd.ValidCount);
    }

    [TestMethod]
    public async Task ReferenceCalibrationKeepsSensorAndNewOutputClippingSeparateAndWithholdsBothFromHfd()
    {
        var layout = Layout(192, 192);
        var rawClipped = new PixelPoint(46.5, 53.5);
        var outputClipped = new PixelPoint(142.5, 55.5);
        var usable = new PixelPoint(89.5, 132.5);
        var raw = CreateRaw(layout, Generate(layout, (x, y) => (ushort)Math.Min(4095, Math.Round(180 +
            Gaussian(x, y, rawClipped, 1.2, 4600) + Gaussian(x, y, outputClipped, 1.4, 3100) +
            Gaussian(x, y, usable, 1.5, 2400))))) with
        { Conditions = new(10, 0, -10) };
        var product = await Produce(ReferenceCalibration(raw));
        var linear = ProductArtifact(product, raw);
        var result = await Run(raw, linear, Bind(raw, linear, ExposureCalibrationDisposition.ReferenceCalibrated));
        var rawCodes = ReadCodes(raw.Payload);
        var linearCodes = ReadCodes(linear.Payload);
        Assert.AreEqual((ushort)4095, rawCodes[53 * layout.Width + 46]);
        Assert.IsLessThan(ushort.MaxValue, linearCodes[53 * layout.Width + 46]);
        Assert.IsLessThan((ushort)4095, rawCodes[55 * layout.Width + 142]);
        Assert.AreEqual(ushort.MaxValue, linearCodes[55 * layout.Width + 142]);
        Assert.AreEqual(rawCodes.LongCount(code => code >= 4095), result.RawBrightness!.SensorClipping.Count);
        Assert.AreEqual((ushort)4095, result.RawBrightness.SensorClipping.StoredWhite);
        Assert.AreEqual(linearCodes.LongCount(code => code == ushort.MaxValue), result.LinearOutputClipping!.Count);
        Assert.AreEqual(ushort.MaxValue, result.LinearOutputClipping.StoredWhite);
        Assert.AreEqual(1, result.Hfd.ValidCount, string.Join(", ", result.Hfd.Samples.Select(sample => sample.Reason)));
        var focus = result.Sources.Detections[result.Hfd.Samples.Single(sample => sample.DiameterPixels.HasValue).DetectionIndex];
        Assert.AreEqual(usable.X, focus.Pixel.X, 0.2);
        Assert.AreEqual(usable.Y, focus.Pixel.Y, 0.2);
    }

    [TestMethod]
    [DataRow(false, 78)]
    [DataRow(true, 77)]
    public async Task RepairedRawWingAndCfaNeighborClippingRemainIneligibleForHfd(bool bayer, int clippedX)
    {
        var layout = Layout(128, 128, bayer);
        var star = new PixelPoint(64.5, 64.5);
        const int clippedY = 64;
        var raw = CreateRaw(layout, Generate(layout, (x, y) =>
        {
            if (x == clippedX && y == clippedY)
                return 4095;
            var wing = !bayer && y == clippedY && x is >= 65 and <= 79 ? 8 : 0;
            return (ushort)(Math.Round(180 + Gaussian(x, y, star, 1.5, bayer ? 2400 : 3000)) + wing);
        })) with
        { Conditions = new(10, 0, -10) };
        var linear = ProductArtifact(await Produce(ReferenceCalibration(raw,
            (x, y) => x == clippedX && y == clippedY ? (ushort)1 : (ushort)0)), raw);
        var result = await Run(raw, linear, Bind(raw, linear, ExposureCalibrationDisposition.ReferenceCalibrated));
        Assert.AreEqual(1L, result.RawBrightness!.SensorClipping.Count);
        Assert.AreEqual(0L, result.LinearOutputClipping!.Count);
        Assert.IsLessThan(ushort.MaxValue, ReadCodes(linear.Payload)[clippedY * layout.Width + clippedX]);
        var detection = Assert.ContainsSingle(result.Sources.Detections);
        AssertNear(result.Sources.Detections, star, bayer ? 0.5 : 0.2);
        if (!bayer)
        {
            Assert.IsTrue(detection.Conditions.HasFlag(StellarSourceConditions.Saturated));
            Assert.AreEqual(1, detection.SaturatedSampleCount);
        }
        var sample = Assert.ContainsSingle(result.Hfd.Samples);
        Assert.AreEqual(detection.Index, sample.DetectionIndex);
        Assert.IsNull(sample.DiameterPixels, $"Clipped candidate contributed {sample.DiameterPixels} px.");
        Assert.AreEqual(FocusStarReasonCodes.Saturated, sample.Reason);
        Assert.AreEqual(ExposureMeasurementStatus.Unsuitable, result.Hfd.Status);
        Assert.AreEqual(0, result.Hfd.ValidCount);
        Assert.IsNull(result.Hfd.MeanPixels);
        Assert.IsNull(result.Hfd.MedianPixels);
    }

    [TestMethod]
    [DataRow(1, 1)]
    [DataRow(2, 2)]
    [DataRow(2, 1)]
    public async Task ScalarHfdRequiresIsotropicDeliveredPixels(int binX, int binY)
    {
        var layout = Layout(128, 128) with
        {
            Readout = new(128 * binX, 128 * binY, 0, 0, 128 * binX, 128 * binY, binX, binY,
                binX == 1 ? FrameBinningAlgorithm.IdentityV1 : FrameBinningAlgorithm.DigitalAverageV1, null, null)
        };
        var star = new PixelPoint(64.5, 64.5);
        var raw = CreateRaw(layout, Generate(layout, (x, y) => (ushort)Math.Round(180 + Gaussian(x, y, star, 1.5, 2400))));
        var linear = await Normalize(raw);
        var result = await Run(raw, linear, Bind(raw, linear));
        Assert.HasCount(1, result.Sources.Detections);
        AssertNear(result.Sources.Detections, star, 0.2);
        Assert.AreEqual(128L * 128, result.RawBrightness!.SampleCount);
        if (binX == binY)
        {
            Assert.AreEqual(1, result.Hfd.ValidCount);
            Assert.AreEqual(3.53, result.Hfd.MeanPixels!.Value, 0.2);
        }
        else
        {
            Assert.AreEqual(ExposureMeasurementStatus.Unavailable, result.Hfd.Status);
            Assert.AreEqual("anisotropic-readout", result.Hfd.Reason);
            Assert.IsNull(result.Hfd.ValidCount);
            Assert.IsNull(result.Hfd.MeanPixels);
            Assert.IsNull(result.Hfd.MedianPixels);
            Assert.IsEmpty(result.Hfd.Samples);
        }
    }

    private static async Task AssertFocusBudgetPrefersEligibleDetections(bool bayer, int eligibleCount)
    {
        const int pitch = 56;
        const int columns = 8;
        const int sourceCount = 48;
        var layout = Layout(pitch * columns, pitch * (sourceCount / columns), bayer);
        var light = CreateRaw(layout, Generate(layout, (x, y) =>
        {
            var index = y / pitch * columns + x / pitch;
            var truncated = index == sourceCount - 1;
            var saturated = index >= eligibleCount && index % 2 == 0;
            var trailed = index >= eligibleCount && !saturated && !truncated;
            var dx = (x % pitch - pitch / 2) / (trailed ? 3.7 : 1.5);
            var dy = (y % pitch - (truncated ? pitch - 12 : pitch / 2)) / 1.5;
            var amplitude = saturated ? 4800 : trailed ? 2300 : truncated ? 3300 : 1250;
            return (ushort)Math.Min(4095, Math.Round(180 + amplitude * Math.Exp(-(dx * dx + dy * dy) / 2)));
        }));
        var (lookup, raw, borrowed) = await BindToAdmission(light);
        var request = ExposureMeasurementBindings.Request(lookup, raw, borrowed);
        var product = await Produce(request);
        var result = ExposureMeasurementProducts.Read(product);

        Assert.HasCount(sourceCount, result.Sources.Detections);
        var eligible = new List<int>();
        var rejected = new Dictionary<int, string>();
        foreach (var detection in result.Sources.Detections)
        {
            var position = (int)detection.Pixel.Y / pitch * columns + (int)detection.Pixel.X / pitch;
            if (position < eligibleCount)
            {
                Assert.AreEqual(StellarSourceConditions.None, detection.Conditions);
                eligible.Add(detection.Index);
            }
            else if (position == sourceCount - 1)
            {
                Assert.AreEqual(StellarSourceConditions.None, detection.Conditions);
                rejected.Add(detection.Index, FocusStarReasonCodes.ApertureTruncated);
            }
            else
            {
                var saturated = position % 2 == 0;
                Assert.IsTrue(detection.Conditions.HasFlag(saturated
                    ? StellarSourceConditions.Saturated : StellarSourceConditions.Trailed));
                rejected.Add(detection.Index, saturated ? FocusStarReasonCodes.Saturated : "trailed-source");
            }
        }
        Assert.HasCount(eligibleCount, eligible);
        var cap = ExposureMeasurementProducts.MaximumHfdSamples;
        Assert.AreEqual(Math.Min(cap, rejected.Count), rejected.Keys.Count(index => index < cap),
            "The brighter ineligible detections must displace eligible detections in the old sampler.");
        Assert.AreEqual(Math.Min(cap, eligibleCount), result.Hfd.ValidCount);
        Assert.AreEqual("focus-budget-limited", result.Hfd.Reason);
        Assert.AreEqual(sourceCount - cap, result.Hfd.OmittedCount);
        CollectionAssert.AreEqual(eligible.Concat(rejected.Keys.Order()).Take(cap).Order().ToArray(),
            result.Hfd.Samples.Select(sample => sample.DetectionIndex).ToArray());
        foreach (var sample in result.Hfd.Samples)
        {
            Assert.AreEqual(rejected.GetValueOrDefault(sample.DetectionIndex, FocusStarReasonCodes.Valid), sample.Reason);
            Assert.AreEqual(!rejected.ContainsKey(sample.DetectionIndex), sample.DiameterPixels is > 0);
        }
        CollectionAssert.AreEqual(product.Payload.ToArray(), (await Produce(request)).Payload.ToArray());

        bool[] historicalPolicies = bayer ? [false, true] : [false];
        foreach (var bayerCoreCheck in historicalPolicies)
        {
            var historical = await Produce(request with { Options = HistoricalFocusOptions(result.Input, bayerCoreCheck) });
            var previous = ExposureMeasurementProducts.Read(historical);
            Assert.AreNotEqual(historical.OutputIdentitySha256, product.OutputIdentitySha256);
            CollectionAssert.AreEqual(previous.Sources.Detections.ToArray(), result.Sources.Detections.ToArray());
            CollectionAssert.AreEqual(Enumerable.Range(0, cap).ToArray(),
                previous.Hfd.Samples.Select(sample => sample.DetectionIndex).ToArray());
            Assert.AreEqual(eligible.Count(index => index < cap), previous.Hfd.ValidCount);
            Assert.AreEqual(result.Hfd.OmittedCount, previous.Hfd.OmittedCount);
            await AssertRetainedReplay(lookup, raw, borrowed, historical);
            Reject(result with { Hfd = previous.Hfd });
        }
        Reject(result with { Hfd = result.Hfd with { OmittedCount = sourceCount } });
        var samples = result.Hfd.Samples.ToArray();
        (samples[0], samples[1]) = (samples[1], samples[0]);
        Reject(result with { Hfd = result.Hfd with { Samples = samples } });
        samples[0] = samples[1];
        Reject(result with { Hfd = result.Hfd with { Samples = samples } });

        void Reject(ExposureMeasurementV1 measurement)
            => Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(WithMeasurement(product, measurement)));
    }

    [TestMethod]
    public async Task RetainedMeasurementRejectsIncompleteAccountingAndInconsistentSaturationFlags()
    {
        var layout = Layout(384, 320);
        var stars = Enumerable.Range(0, 36).Select(index => new PixelPoint(40.5 + 58 * (index % 6), 38.5 + 48 * (index / 6))).ToArray();
        var raw = CreateRaw(layout, Generate(layout, (x, y) =>
            (ushort)Math.Round(180 + stars.Sum(star => Gaussian(x, y, star, 1.5, 2400)))));
        var linear = await Normalize(raw);
        var input = Bind(raw, linear);
        var request = ExposureMeasurementProducts.Request(input, raw, linear);
        AssertRejectsTamperedMeasurement(raw, input, await Produce(request));
        AssertRejectsTamperedMeasurement(raw, input, await Produce(request with
        {
            Options = HistoricalFocusOptions(input, bayerCoreCheck: false)
        }));
    }

    private static void AssertRejectsTamperedMeasurement(ProcessingArtifact raw, ExposureMeasurementInputV1 input,
        ProcessingProduct product)
    {
        var result = ExposureMeasurementProducts.Read(product);
        Assert.HasCount(36, result.Sources.Detections);
        Assert.AreEqual(32, result.Hfd.ValidCount);
        Assert.AreEqual(4, result.Hfd.OmittedCount);
        Assert.AreEqual("focus-budget-limited", result.Hfd.Reason);
        CollectionAssert.AreEqual(Enumerable.Range(0, 32).ToArray(), result.Hfd.Samples.Select(sample => sample.DetectionIndex).ToArray());
        var falseEmpty = result with { Hfd = new(ExposureMeasurementStatus.Empty, "no-candidates", 0, null, null, 0, null, []) };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(Rehash(falseEmpty)));
        var shortBudget = result with { Hfd = result.Hfd with { OmittedCount = 3 } };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(Rehash(shortBudget)));
        var detections = result.Sources.Detections.ToArray();
        detections[0] = detections[0] with { Conditions = detections[0].Conditions & ~StellarSourceConditions.Saturated, SaturatedSampleCount = 1 };
        var unflagged = Rehash(result with { Sources = result.Sources with { Detections = detections } });
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementConsumers.RegistrationDetections(ProductArtifact(unflagged, raw), input));
        detections[0] = detections[0] with { Conditions = detections[0].Conditions | StellarSourceConditions.Saturated };
        var falseWidth = result with { Sources = result.Sources with { Detections = detections } };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(Rehash(falseWidth)));

        var original = result.Sources.Detections[0];
        detections[0] = original with { Conditions = original.Conditions | StellarSourceConditions.Trailed };
        // Both policies can retain the brightest rejection when the omitted sources are also ineligible.
        for (var index = result.Hfd.Samples.Count; index < detections.Length; index++)
            detections[index] = detections[index] with { Conditions = detections[index].Conditions | StellarSourceConditions.Trailed };
        var trailed = result with { Sources = result.Sources with { Detections = detections } };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(Rehash(trailed)),
            "A trailed detection cannot contribute an HFD diameter.");

        var samples = result.Hfd.Samples.ToArray();
        samples[0] = new(original.Index, "trailed-source", null);
        var widths = samples.Skip(1).Select(sample => sample.DiameterPixels!.Value).Order().ToArray();
        var focus = result.Hfd with
        {
            Samples = samples,
            ValidCount = widths.Length,
            MeanPixels = samples.Skip(1).Average(sample => sample.DiameterPixels!.Value),
            MedianPixels = widths[widths.Length / 2]
        };
        var accepted = ExposureMeasurementProducts.Read(Rehash(trailed with { Hfd = focus }));
        Assert.AreEqual(detections[0], accepted.Sources.Detections[0]);
        Assert.AreEqual(samples[0], accepted.Hfd.Samples[0]);
        Assert.AreEqual(31, accepted.Hfd.ValidCount);
        Assert.AreEqual(4, accepted.Hfd.OmittedCount);

        detections[0] = detections[0] with
        {
            Conditions = detections[0].Conditions | StellarSourceConditions.Saturated,
            SaturatedSampleCount = 1
        };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(Rehash(trailed with { Hfd = focus })),
            "Saturation takes precedence over trailing.");
        samples[0] = new(original.Index, FocusStarReasonCodes.Saturated, null);
        accepted = ExposureMeasurementProducts.Read(Rehash(trailed with { Hfd = focus }));
        Assert.AreEqual(samples[0], accepted.Hfd.Samples[0]);

        ProcessingProduct Rehash(ExposureMeasurementV1 measurement) => WithMeasurement(product, measurement);
    }

    [TestMethod]
    public async Task CommittedReaderVerifiesIndependentAdmissionAndExactProductSlot()
    {
        await AssertCommittedReaderAndReplay(bayer: false);
        await AssertCommittedReaderAndReplay(bayer: true);
    }

    private static async Task AssertCommittedReaderAndReplay(bool bayer)
    {
        var layout = Layout(96, 96, bayer);
        var light = CreateRaw(layout, Generate(layout, static (_, _) => 91));
        var (lookup, raw, borrowed) = await BindToAdmission(light);
        var key = lookup.Exposure.Key;
        var linearRef = lookup.Product!;
        var execution = linearRef.Slot.ExecutionId;
        var request = ExposureMeasurementBindings.Request(lookup, raw, borrowed);
        var current = await Produce(request);
        var retained = ProductArtifact(current, raw);
        var slot = new ExposureProductSlot(execution, ExposureMeasurementProducts.StepAlias,
            ExposureMeasurementProducts.SchemaVersion, ExposureMeasurementProducts.Variant);
        var exact = new ExposureProductRef(key, slot, retained.ArtifactId,
            ProcessingIdentity.CreateOutputIdentity(retained.Role, retained.Variant, retained.RecipeIdentitySha256, retained.SourceArtifactIds!),
            retained.ContentIdentitySha256!, retained.ContentIdentitySha256);
        var reader = new RetainedReader(lookup, new(key, slot, ExposureProductAvailability.Available, 2, exact, null));
        var read = await ExposureMeasurementBindings.ReadCommittedAsync(reader, exact, retained, CancellationToken.None);
        Assert.AreEqual(key, read.Input.Exposure);
        Assert.AreEqual(1L, read.Input.Step3Revision);
        Assert.AreEqual(1320, read.Input.PixelScaleArcseconds);
        Assert.AreEqual(ExposureMeasurementStatus.Empty, read.Sources.Status);
        Assert.AreEqual(0, read.Hfd.ValidCount);
        await AssertRetainedReplay(lookup, raw, borrowed, current);
        var legacyEligibleFirst = await Produce(request with { Options = HistoricalEligibleFirstOptions(read.Input) });
        Assert.AreNotEqual(current.OutputIdentitySha256, legacyEligibleFirst.OutputIdentitySha256);
        await AssertRetainedReplay(lookup, raw, borrowed, legacyEligibleFirst);
        var legacy = await Produce(request with { Options = HistoricalFocusOptions(read.Input, bayerCoreCheck: false) });
        Assert.AreNotEqual(current.OutputIdentitySha256, legacy.OutputIdentitySha256);
        Assert.AreNotEqual(legacyEligibleFirst.OutputIdentitySha256, legacy.OutputIdentitySha256);
        await AssertRetainedReplay(lookup, raw, borrowed, legacy);
        if (bayer)
        {
            var formerBayer = await Produce(request with { Options = HistoricalFocusOptions(read.Input, bayerCoreCheck: true) });
            Assert.AreNotEqual(current.OutputIdentitySha256, formerBayer.OutputIdentitySha256);
            Assert.AreNotEqual(legacy.OutputIdentitySha256, formerBayer.OutputIdentitySha256);
            await AssertRetainedReplay(lookup, raw, borrowed, formerBayer);
        }
        var byExecution = await ExposureMeasurementBindings.ReadCommittedAsync(reader, key, execution, retained, CancellationToken.None);
        Assert.AreEqual(read.Input, byExecution.Input);
        var expired = lookup with { Disposition = LinearExposureDisposition.Expired, Reason = "retention-expired" };
        var readAfterExpiry = await ExposureMeasurementBindings.ReadCommittedWithSourceAsync(
            reader with { Linear = expired }, key, execution, retained, CancellationToken.None);
        Assert.AreEqual(read.Input, readAfterExpiry.Measurement.Input);
        Assert.AreEqual("retention-expired", readAfterExpiry.LinearExpiryReason);
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementBindings.Request(expired, raw, null));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await ExposureMeasurementBindings.ReadCommittedAsync(
            reader with { Linear = expired with { Product = null, Provenance = null } }, exact, retained, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await ExposureMeasurementBindings.ReadCommittedAsync(
            reader, exact with { Slot = slot with { ExecutionId = Guid.NewGuid() } }, retained, CancellationToken.None));
        var wrongLookup = lookup with { Provenance = borrowed.Provenance with { LinearDescriptorSha256 = new string('F', 64) } };
        var wrongReader = reader with { Linear = wrongLookup };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await ExposureMeasurementBindings.ReadCommittedAsync(
            wrongReader, exact, retained, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await ExposureMeasurementBindings.ReadCommittedAsync(
            reader with { Linear = lookup with { Revision = 3 } }, exact, retained, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => await ExposureMeasurementBindings.ReadCommittedAsync(
            reader with { Linear = lookup with { Product = linearRef with { Slot = linearRef.Slot with { ExecutionId = Guid.NewGuid() } } } },
            exact, retained, CancellationToken.None));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementBindings.FromAdmitted(
            lookup with { Disposition = LinearExposureDisposition.Pending }));
    }

    private static async Task<(LinearExposureLookup Lookup, ProcessingArtifact Raw, LinearExposureInputV1 Borrowed)>
        BindToAdmission(ProcessingArtifact light, Guid? runId = null, Guid? linearExecutionId = null)
    {
        var layout = light.Layout!;
        var checksum = Convert.ToHexString(SHA256.HashData(light.Payload.Span));
        var identity = new ProfileIdentityDescriptor("fixture", "1", new string('E', 64));
        var descriptor = new ReconstructionDescriptor(new("agent", "rig", 1, light.CaptureId!.Value),
            new(ExposureTime, ExposureTime, ExposureTime, ExposureTime, ExposureTime),
            new(light.Integration, light.Integration, 10, 10, 0, 0, null, null),
            new(identity, identity, identity, identity, identity), layout,
            new(light.ArtifactId, light.Role, "raw", light.Variant, ExposureTime, [], RawRecipe, light.MediaType, checksum));
        var raw = light with { DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor) };
        var key = new ExposureKey(new("agent", "virtual"), runId ?? Guid.NewGuid(), descriptor.Capture.CaptureId);
        var exposure = new ExposureInputV1(key, descriptor, new(layout, new(90, 0, 0), identity.Sha256, "optics-1", 1320));
        var linear = await Normalize(raw);
        var linearRef = new ExposureProductRef(key, new(linearExecutionId ?? Guid.NewGuid(), "linear", "linear-v1", linear.Variant),
            linear.ArtifactId, linear.ContentIdentitySha256!, Convert.ToHexString(SHA256.HashData(linear.Payload.Span)), null);
        var provenance = new LinearInputProvenanceV1(LinearExposureDisposition.QualifiedNormalizationOnly,
            raw.ArtifactId, raw.DescriptorIdentitySha256, checksum, linear.ArtifactId, linear.DescriptorIdentitySha256!,
            linearRef.PayloadSha256, linearRef.OutputIdentitySha256, ProcessingIdentity.CreateRecipeIdentity(linear.RecipeDescriptor!),
            [], new(raw.ArtifactId, 4095), new(linear.ArtifactId, 4095));
        return (new(exposure, provenance.Disposition, 1, linearRef, provenance, null), raw, new(exposure, linear, provenance));
    }

    private static async Task AssertRetainedReplay(LinearExposureLookup lookup, ProcessingArtifact raw,
        LinearExposureInputV1 borrowed, ProcessingProduct expected)
    {
        var replay = await Produce(ExposureMeasurementBindings.Request(lookup, raw, borrowed, null, null,
            expected.OutputIdentitySha256));
        Assert.AreEqual(expected.OutputIdentitySha256, replay.OutputIdentitySha256);
        Assert.AreEqual(expected.ChecksumSha256, replay.ChecksumSha256);
        CollectionAssert.AreEqual(expected.Payload.ToArray(), replay.Payload.ToArray());
    }

    [TestMethod]
    public async Task AssociationFittingAndRegistrationReuseTheRetainedPositionsWithoutPixels()
    {
        var fixtureScene = await MeasuredStellarAssociationFixtures.StarFieldAsync(("catalog:zenith", 0, 0, 2));
        var scene = WithProjection(fixtureScene, fixtureScene.Projection with { AlgorithmVersion = fixtureScene.Projection.CalibrationVersion });
        var predicted = MeasuredStellarAssociationFixtures.Pixel(scene, "catalog:zenith");
        var matched = new PixelPoint(predicted.X + 0.2, predicted.Y + 0.3);
        var unassociated = new PixelPoint(61.25, 86.4);
        var layout = Layout(MeasuredStellarAssociationFixtures.Width, MeasuredStellarAssociationFixtures.Height);
        var background = MeasuredStellarAssociationFixtures.Field();
        MeasuredStellarAssociationFixtures.AddNoise(background, 1920, 2.5);
        var raw = CreateRaw(layout, Generate(layout, (x, y) => (ushort)Math.Round(background[y * layout.Width + x] +
            Gaussian(x, y, matched, 1.35, 2500) + Gaussian(x, y, unassociated, 1.35, 1800))),
            scene.Source.CaptureId) with
        { ArtifactId = scene.Source.ArtifactId, DescriptorIdentitySha256 = scene.Source.ArtifactIdentitySha256 };
        var linear = await Normalize(raw);
        var input = Bind(raw, linear) with
        {
            ApertureProjection = ProjectedSceneJson.ToProjectionContext(scene.Projection),
            OpticalCalibrationVersion = scene.Projection.CalibrationVersion
        };
        var artifact = ProductArtifact(await Produce(ExposureMeasurementProducts.Request(input, raw, linear)), raw);
        var associations = ExposureMeasurementConsumers.Associate(artifact, input, scene, new(), new());
        var association = Assert.ContainsSingle(associations.Entries.Associations);
        Assert.IsTrue(association.LabelEligible, association.LabelRejectionReason);
        Assert.AreEqual("catalog:zenith", association.CatalogId);
        Assert.HasCount(1, associations.Entries.UnassociatedDetections);
        var fit = Assert.ContainsSingle(ExposureMeasurementConsumers.AstrometryDetections(artifact, input, associations));
        Assert.AreEqual(association.DetectionIndex, fit.Index);
        Assert.AreEqual(matched.X, fit.Pixel.X, 0.15);
        Assert.AreEqual(matched.Y, fit.Pixel.Y, 0.15);
        var registration = ExposureMeasurementConsumers.RegistrationDetections(artifact, input);
        Assert.HasCount(2, registration);
        AssertNear(registration, unassociated, 0.15);
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementConsumers.AstrometryDetections(artifact, input,
            associations with { MeasurementArtifactId = Guid.NewGuid() }));

        var request = ExposureCatalogPredictionProducts.Request(raw, scene);
        var prediction = ProductArtifact(await Produce(request), raw);
        var canonical = ExposureAssociationProducts.Associate(raw, artifact, input, prediction, ExposureAssociationProducts.Variant);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, canonical.Status, canonical.ReasonCode);
        var retained = ProductArtifact(Assert.ContainsSingle(canonical.Products), raw);
        var measured = ExposureMeasurementProducts.Read(artifact, input);
        var associationMember = Assert.ContainsSingle(MeasuredStellarAssociationJson.Parse(retained.Payload).Associations!.Associations);
        Assert.AreEqual(association.DetectionIndex, associationMember.DetectionIndex);
        ExposureAssociationProducts.VerifyMeasurement(retained, raw, artifact, measured, prediction);

        var display = ProductArtifact(await Produce(request with { OutputVariant = "projected-scene-v1" }), raw);
        Reject(display);
        var unrelatedId = prediction with { ArtifactId = Guid.NewGuid() };
        Reject(unrelatedId);
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureAssociationProducts.VerifyMeasurement(retained, raw, artifact, measured, unrelatedId));
        var geometry = scene.Projection;
        foreach (var projection in new[]
        {
            geometry with { HorizontalFlip = !geometry.HorizontalFlip },
            geometry with { PrincipalPointX = geometry.PrincipalPointX + 1, PrincipalPointY = geometry.PrincipalPointY - 1 },
            geometry with { FocalLengthXPixels = geometry.FocalLengthXPixels + 10 },
            geometry with { RollDegrees = geometry.RollDegrees + 3 },
            geometry with { EnforceSensorBounds = !geometry.EnforceSensorBounds },
            geometry with { AlgorithmVersion = "other-projection" }
        })
        {
            var altered = WithProjection(scene, projection);
            var substitute = ProductArtifact(await Produce(ExposureCatalogPredictionProducts.Request(raw, altered)), raw);
            Assert.AreNotEqual(prediction.ArtifactId, substitute.ArtifactId);
            Reject(substitute);
        }

        void Reject(ProcessingArtifact candidate) => Assert.ThrowsExactly<InvalidDataException>(() =>
            ExposureAssociationProducts.Associate(raw, artifact, input, candidate, ExposureAssociationProducts.Variant));
    }

    [TestMethod]
    public async Task OrdinaryAstrometryRejectsSparseRetainedAssociationAndKeepsItsMeasurement()
    {
        var fixture = await MeasuredAstrometryFixture.CreateAsync();
        var isolated = fixture.Associations.Associations
            .Where(item => item.LabelEligible && !item.Trailed)
            .MinBy(item => Math.Abs(item.ExpectedX - 256) + Math.Abs(item.ExpectedY - 256))!;
        var picked = fixture.Scene.Objects.Single(item => item.Id == isolated.CatalogId);
        var rawLayout = fixture.Raw.Layout! with
        {
            BlackLevel = 0,
            StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
            LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
        };
        var background = MeasuredStellarAssociationFixtures.Field(rawLayout.Width, rawLayout.Height);
        MeasuredStellarAssociationFixtures.AddNoise(background, 1920, 2.5);
        var raw = fixture.Raw with
        {
            Layout = rawLayout,
            Payload = Generate(rawLayout, (x, y) => (ushort)Math.Round(background[y * rawLayout.Width + x] +
                Gaussian(x, y, picked.Pixel, 1.35, 4500)))
        };
        var linear = await Normalize(raw);
        var prediction = WithProjection(fixture.Scene, fixture.Scene.Projection with { AlgorithmVersion = fixture.Scene.Projection.CalibrationVersion });
        var expected = Bind(raw, linear) with
        {
            ApertureProjection = ProjectedSceneJson.ToProjectionContext(prediction.Projection),
            OpticalCalibrationVersion = prediction.Projection.CalibrationVersion
        };
        var product = await Produce(ExposureMeasurementProducts.Request(expected, raw, linear));
        var retained = ProductArtifact(product, raw);
        var scene = ProductArtifact(await Produce(ExposureCatalogPredictionProducts.Request(raw, prediction)), raw);
        var matching = ExposureAssociationProducts.Associate(raw with { Payload = default }, retained, expected,
            scene, "exposure-stellar-associations-v1");
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, matching.Status, matching.ReasonCode);
        var associated = ProductArtifact(matching.Products.Single(), raw);
        var evidence = MeasuredStellarAssociationJson.Parse(associated.Payload).Associations!;
        Assert.AreEqual(picked.Id, Assert.ContainsSingle(evidence.Associations).CatalogId);
        var verified = ExposureMeasurementProducts.Read(retained, expected);
        ExposureAssociationProducts.VerifyMeasurement(associated, raw, retained, verified, scene);
        var wrongSource = verified.Sources.Detections[0] with { Flux = verified.Sources.Detections[0].Flux * 2 };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureAssociationProducts.VerifyMeasurement(associated, raw, retained,
            verified with { Sources = verified.Sources with { Detections = [wrongSource] } }, scene));
        var context = fixture.Context with
        {
            MeasuredArtifactId = associated.ArtifactId,
            MeasuredIdentitySha256 = associated.ContentIdentitySha256!,
            MeasuredRecipeIdentitySha256 = associated.RecipeIdentitySha256,
            ReadoutIdentitySha256 = MeasuredAstrometryJson.ComputeReadoutIdentity(rawLayout),
            Frame = fixture.Context.Frame with
            {
                SourcePayloadSha256 = ProcessingIdentity.ComputePayloadSha256(raw.Payload),
                DetectionAlgorithmVersion = evidence.Measurement.AlgorithmVersion,
                DetectionSettingsIdentitySha256 = evidence.Measurement.SettingsIdentitySha256
            }
        };

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync((fixture with { Scene = prediction }).Request(context, raw, associated));
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var solution = MeasuredAstrometryJson.Parse(outcome.Products.Single().Payload);
        Assert.AreEqual(AstrometricAssessmentStatus.Rejected, solution.Assessment.Status, solution.Assessment.Reason);
        Assert.AreEqual(associated.ArtifactId, solution.MeasuredArtifactId);
        var reread = ExposureMeasurementProducts.Read(retained, expected);
        Assert.HasCount(1, reread.Sources.Detections);
        Assert.AreEqual(1, reread.Hfd.ValidCount);
    }

    [TestMethod]
    public async Task OccupancyUpgradeHasDistinctIdentityReplaysLegacyV2BitForBitAndRejectsImpossibleOccupancy()
    {
        // H1 + M7 + D8-M4: verify that (a) new runs emit linear-stellar-local-v3 with occupancy and a distinct output
        // identity, (b) replaying the frozen delta6 (commit 6a631e5e9) legacy linear-stellar-local-v2 EligibleFirst
        // identity reproduces the exact independently anchored delta6 golden bytes, SHA-256, and algorithms bit-for-bit,
        // and (c) ExposureMeasurementProducts.Read rejects internally impossible or stripped occupancy/subgrids.
        var frozenCaptureId = Guid.Parse("52600001-0000-4000-8000-000000000001");
        var frozenRawArtifactId = Guid.Parse("52600002-0000-4000-8000-000000000002");
        var frozenRunId = Guid.Parse("52600003-0000-4000-8000-000000000003");
        var frozenLinearExecutionId = Guid.Parse("52600004-0000-4000-8000-000000000004");
        // Golden legacy v2 constants independently produced from delta6 commit 6a631e5e9b20be79abc57c581bce053c5e642904:
        const string frozenDelta6OutputIdentitySha256 = "DB85B1889BE4E40D96C543EEBCA500CE7ACDC39FC8279F6C38B08630C102894A";
        const string frozenDelta6PayloadSha256 = "8D3F47589477D7CB3143A5365404A8C55770008DCCCC0EA7AF0E45A3B7514A61";
        const int frozenDelta6PayloadByteLength = 3577;
        const string frozenDelta6SettingsSha256 = "CF2E401B3525B134C66F5BB170D3053675156E9864D0F76A53E024220F5ECBB7";

        const int size = 160;
        var layout = Layout(size, size);
        var center = new PixelPoint(80.5, 80.5);
        var light = CreateRaw(layout, Generate(layout, (x, y) =>
        {
            var r = double.Hypot(x + 0.5 - center.X, y + 0.5 - center.Y);
            double value = 180 + Gaussian(x, y, center, 1.25, 2200);
            if (r is >= 14.0 and <= 30.0)
                value += 1400.0;
            return (ushort)Math.Clamp(Math.Round(value), 0, 4095);
        }), frozenCaptureId) with
        {
            ArtifactId = frozenRawArtifactId
        };
        var (lookup, raw, borrowed) = await BindToAdmission(light, frozenRunId, frozenLinearExecutionId);
        var currentRequest = ExposureMeasurementBindings.Request(lookup, raw, borrowed);
        var currentProduct = await Produce(currentRequest);
        var currentMeasurement = ExposureMeasurementProducts.Read(currentProduct);
        var input = currentMeasurement.Input;

        // Produce the exact frozen legacy v2 (EligibleFirst) product for the same exposure & inputs.
        var legacyV2Request = currentRequest with { Options = HistoricalEligibleFirstOptions(input) };
        var legacyV2Product = await Produce(legacyV2Request);
        var legacyV2Measurement = ExposureMeasurementProducts.Read(legacyV2Product);

        // (a) Distinct output identity, algorithm identity, settings identity, and occupancy presence:
        Assert.AreNotEqual(frozenDelta6OutputIdentitySha256, currentProduct.OutputIdentitySha256);
        Assert.AreNotEqual(frozenDelta6PayloadSha256, currentProduct.ChecksumSha256);
        Assert.AreEqual(frozenDelta6OutputIdentitySha256, legacyV2Product.OutputIdentitySha256);
        Assert.AreEqual(frozenDelta6PayloadSha256, legacyV2Product.ChecksumSha256);
        Assert.AreEqual(frozenDelta6PayloadByteLength, legacyV2Product.Payload.Length);
        Assert.IsTrue(currentProduct.Algorithms.Contains(
            new ProcessingAlgorithmIdentity("stellar-source-measurer", StellarSourceMeasurer.AlgorithmVersion)));
        Assert.IsTrue(legacyV2Product.Algorithms.Contains(
            new ProcessingAlgorithmIdentity("stellar-source-measurer", StellarSourceMeasurer.V2AlgorithmVersion)));
        Assert.AreEqual(
            StellarSourceMeasurer.SettingsIdentity(MeasuredStellarAssociationRecipe.MeasurementOptions(CameraPixelFormat.Mono16),
                StellarSourceMeasurer.AlgorithmVersion).ToUpperInvariant(),
            currentMeasurement.Sources.SettingsSha256);
        Assert.AreEqual(frozenDelta6SettingsSha256, legacyV2Measurement.Sources.SettingsSha256);
        var v3Ring = currentMeasurement.Sources.Exclusions.Single(e => e.ReasonCode == StellarExclusionReasons.ExtendedRegion);
        var v2Ring = legacyV2Measurement.Sources.Exclusions.Single(e => e.ReasonCode == StellarExclusionReasons.ExtendedRegion);
        Assert.IsNotNull(v3Ring.Occupancy);
        Assert.IsNotNull(v3Ring.Occupancy.Subgrid);
        Assert.IsNull(v2Ring.Occupancy);
        ProcessingRecipeTests.AssertProductMatchesContract(currentRequest, currentProduct);
        ProcessingRecipeTests.AssertProductMatchesContract(legacyV2Request, legacyV2Product);

        // (b) Replaying using the checked-in frozen delta6 output identity reproduces exact delta6 bytes bit-for-bit:
        var replayRequest = ExposureMeasurementBindings.Request(
            lookup, raw, borrowed, null, null, frozenDelta6OutputIdentitySha256);
        var replayed = await Produce(replayRequest);
        Assert.AreEqual(frozenDelta6OutputIdentitySha256, replayed.OutputIdentitySha256);
        Assert.AreEqual(frozenDelta6PayloadSha256, replayed.ChecksumSha256);
        Assert.AreEqual(frozenDelta6PayloadByteLength, replayed.Payload.Length);
        CollectionAssert.AreEqual(legacyV2Product.Payload.ToArray(), replayed.Payload.ToArray());
        CollectionAssert.AreEqual(legacyV2Product.Algorithms.ToArray(), replayed.Algorithms.ToArray());

        // (c) M7 + D8-M3 negative tests on canonical, rehashed ExposureMeasurementV1 products:
        // 1. SampleCount = 1 with multiple occupied cells (coarse 1x1 grid):
        var smallFp = new StellarFootprintBounds(10, 10, 13, 13);
        var multiCellOcc = StellarFootprintOccupancy.Create(smallFp, static (x, y) => (x == 10 && y == 10) || (x == 13 && y == 13));
        var oneSampleManyCells = new StellarMeasurementExclusion(new PixelPoint(10.5, 10.5), 500, 1, StellarExclusionReasons.HotPixelOrCosmicRay)
        {
            Footprint = smallFp,
            Occupancy = multiCellOcc
        };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with { Exclusions = [oneSampleManyCells] }
            })));

        // 2. SampleCount = 1 with multi-cell two-level subgrid (ring occupancy):
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with { Exclusions = [v3Ring with { SampleCount = 1 }] }
            })));

        // 3. Recorded peak outside the occupied mask (coarse cell = 0, inside footprint):
        var peakOutsideMask = new StellarMeasurementExclusion(new PixelPoint(11.5, 11.5), 500, 2, StellarExclusionReasons.TooBroad)
        {
            Footprint = smallFp,
            Occupancy = multiCellOcc
        };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with { Exclusions = [peakOutsideMask] }
            })));

        // 4. Recorded peak in hollow interior of the ring (subgrid cell = 0, inside ring footprint):
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with { Exclusions = [v3Ring with { Peak = center }] }
            })));

        // 5. Recorded peak outside footprint bounds:
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with
                {
                    Exclusions = [new StellarMeasurementExclusion(new PixelPoint(5.5, 5.5), 500, 2, StellarExclusionReasons.TooBroad)
                    {
                        Footprint = smallFp,
                        Occupancy = multiCellOcc
                    }]
                }
            })));

        // 6. SampleCount exceeding total capacity of occupied 1x1 cells:
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with { Exclusions = [oneSampleManyCells with { SampleCount = 3 }] }
            })));

        // 7. Legacy v2 product tampered to include Occupancy:
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(legacyV2Product, legacyV2Measurement with
            {
                Sources = legacyV2Measurement.Sources with { Exclusions = [v2Ring with { Occupancy = v3Ring.Occupancy }] }
            })));

        // 8. v3 product tampered to strip Subgrid or Occupancy from the non-solid ring:
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with
                {
                    Exclusions = [v3Ring with { Occupancy = v3Ring.Occupancy! with { Subgrid = null } }]
                }
            })));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(currentProduct, currentMeasurement with
            {
                Sources = currentMeasurement.Sources with { Exclusions = [v3Ring with { Occupancy = null }] }
            })));
    }

    [TestMethod]
    public async Task ParallelDiagonalBandsOnThreeThousandPixelFrameStayWithinEnvelopeAndRoundTrip()
    {
        // D8-H1: A supported 3072x3072 Mono16 frame with >= 1,000 parallel diagonal-band exclusions must enforce the
        // whole-product subgrid budget so Step 4 never emits an unreadable > 4 MiB product, and ExposureMeasurementProducts.Read
        // round-trips it cleanly while still rejecting tampered stripped subgrids.
        const int size = 3072;
        var layout = Layout(size, size);
        var rawBytes = new byte[checked((int)layout.ByteLength)];
        for (var y = 0; y < size; y++)
        {
            var rowSpan = rawBytes.AsSpan(y * layout.StrideBytes, size * 2);
            var inY = y is >= 16 and < size - 16;
            for (var x = 0; x < size; x++)
            {
                ushort code = 180;
                if (inY && x is >= 16 and < size - 16 && ((x + y) % 6) <= 1)
                    code = 2200;
                BinaryPrimitives.WriteUInt16LittleEndian(rowSpan[(x * 2)..], code);
            }
        }

        var raw = CreateRaw(layout, rawBytes);
        var linear = await Normalize(raw);
        var input = Bind(raw, linear);
        var request = ExposureMeasurementProducts.Request(input, raw, linear);
        var product = await Produce(request);

        Assert.IsLessThanOrEqualTo(StructuredProcessingProductDescriptorV1.MaximumPayloadBytes, product.Payload.Length,
            $"3072x3072 parallel diagonal bands must stay within the 4 MiB envelope (got {product.Payload.Length} bytes).");

        var read = ExposureMeasurementProducts.Read(product);
        var artifact = ProductArtifact(product, raw);
        var verified = ExposureMeasurementProducts.Read(artifact, input);
        Assert.AreEqual(read.Sources.Exclusions.Count, verified.Sources.Exclusions.Count);
        Assert.IsGreaterThanOrEqualTo(1000, read.Sources.Exclusions.Count,
            $"Expected >= 1,000 parallel diagonal-band exclusions (got {read.Sources.Exclusions.Count}).");

        // Verify whole-product subgrid raw byte budget is respected and refined exclusions still reject subgrid stripping.
        long totalSubgridBytes = 0;
        var firstRefinedIndex = -1;
        for (var i = 0; i < read.Sources.Exclusions.Count; i++)
        {
            if (read.Sources.Exclusions[i].Occupancy?.Subgrid is { } sub)
            {
                totalSubgridBytes += Convert.FromBase64String(sub.BitsBase64).Length;
                if (firstRefinedIndex < 0)
                    firstRefinedIndex = i;
            }
        }
        Assert.IsGreaterThanOrEqualTo(0, firstRefinedIndex, "Expected refined subgrids under the shared whole-product budget.");
        Assert.IsLessThanOrEqualTo(
            StellarFootprintOccupancy.MaximumTotalSubgridBytes + read.Sources.Exclusions.Count,
            totalSubgridBytes);

        var stripped = read.Sources.Exclusions.ToArray();
        stripped[firstRefinedIndex] = stripped[firstRefinedIndex] with
        {
            Occupancy = stripped[firstRefinedIndex].Occupancy! with { Subgrid = null }
        };
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(
            WithMeasurement(product, read with { Sources = read.Sources with { Exclusions = stripped } })));
    }

    [TestMethod]
    public async Task FourThousandNinetySixPlusHeavyCoarseMasksOn3968FrameStayWithinEnvelopeAndRoundTrip()
    {
        // D8-H1 / Delta10 exact regression probe: a supported 3968x3968 Mono16 frame with 4,096 separate exclusions
        // (64x64 grid of 62x62 tiles, each containing a 48x42 bright patch at offset (8, 8) with bright perimeter and
        // every tenth interior column dark x % 10 == 6; background=180, bright=2,200). Previously generated 655,360
        // Base64 '+' chars escaped as 6-byte \u002B (5,689,059 bytes > 4,194,304) and failed Step 4 with
        // TerminalFailure (measurement.payload-budget-exceeded).
        const int tileCountPerAxis = 64;
        const int tileSize = 62;
        const int size = tileCountPerAxis * tileSize; // 3968
        const int patchOffset = 8;
        const int patchWidth = 48;
        const int patchHeight = 42;
        var layout = Layout(size, size);
        var rawBytes = new byte[checked((int)layout.ByteLength)];
        for (var y = 0; y < size; y++)
        {
            var rowSpan = rawBytes.AsSpan(y * layout.StrideBytes, size * 2);
            var ly = y % tileSize;
            var inPatchY = ly >= patchOffset && ly < patchOffset + patchHeight;
            var onBorderY = ly == patchOffset || ly == patchOffset + patchHeight - 1;
            for (var x = 0; x < size; x++)
            {
                ushort code = 180;
                if (inPatchY)
                {
                    var lx = x % tileSize;
                    if (lx >= patchOffset && lx < patchOffset + patchWidth)
                    {
                        var onBorderX = lx == patchOffset || lx == patchOffset + patchWidth - 1;
                        var localPatchX = lx - patchOffset;
                        if (onBorderY || onBorderX || localPatchX % 10 != 6)
                            code = 2200;
                    }
                }
                BinaryPrimitives.WriteUInt16LittleEndian(rowSpan[(x * 2)..], code);
            }
        }

        var raw = CreateRaw(layout, rawBytes);
        var linear = await Normalize(raw);
        var input = Bind(raw, linear);
        var request = ExposureMeasurementProducts.Request(input, raw, linear);
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();

        Assert.IsLessThanOrEqualTo(StructuredProcessingProductDescriptorV1.MaximumPayloadBytes, product.Payload.Length,
            $"3968x3968 4,096-exclusion frame must stay within 4 MiB envelope (got {product.Payload.Length} bytes).");

        var readFromProduct = ExposureMeasurementProducts.Read(product);
        var artifact = ProductArtifact(product, raw);
        var readFromArtifact = ExposureMeasurementProducts.Read(artifact, input);
        Assert.HasCount(4096, readFromProduct.Sources.Exclusions);
        Assert.HasCount(4096, readFromArtifact.Sources.Exclusions);
        Assert.IsTrue(readFromProduct.Sources.Exclusions.All(static e => e.Occupancy is not null),
            "All 4,096 non-solid exclusions should retain deterministic budgeted coarse occupancy.");

        // Stripping Occupancy from any exclusion on the 4,096-exclusion product must still be rejected by both readers.
        var tamperedExclusions = readFromProduct.Sources.Exclusions.ToArray();
        tamperedExclusions[0] = tamperedExclusions[0] with { Occupancy = null };
        var tamperedProduct = WithMeasurement(product, readFromProduct with
        {
            Sources = readFromProduct.Sources with { Exclusions = tamperedExclusions }
        });
        var tamperedArtifact = ProductArtifact(tamperedProduct, raw);
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(tamperedProduct));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(tamperedArtifact, input));
    }

    [TestMethod]
    public async Task WorstCasePostBudgetPlusEscapingAndEnvelopeOmissionStayWithinBudget()
    {
        // 1. Real 3968x3968 Mono16 frame with 4,096 non-solid 48x42 tiles specifically patterned so that every
        //    post-budget 16x14 (3x3) coarse mask repeats bytes [0xFB, 0xEF, 0xBE] -> 37 '+' chars out of 40 Base64
        //    chars (each JSON-escaped as 6-byte \u002B!). Step 4 must still emit Produced <= 4 MiB and round-trip.
        const int tileCountPerAxis = 64;
        const int tileSize = 62;
        const int size = tileCountPerAxis * tileSize; // 3968
        const int patchOffset = 8;
        const int patchWidth = 48;
        const int patchHeight = 42;
        byte[] allPlusPattern = [0xFB, 0xEF, 0xBE];

        var sampleTile = new StellarFootprintBounds(patchOffset, patchOffset, patchOffset + patchWidth - 1, patchOffset + patchHeight - 1);
        var expectedBudget = StellarFootprintOccupancy.ComputeProductBudgets(
            0, 4096, Enumerable.Repeat(sampleTile, 4096).ToArray());
        var probeOccupancy = StellarFootprintOccupancy.Create(
            sampleTile, static (x, y) => x == patchOffset && y == patchOffset,
            expectedBudget.CoarseCellBudget, expectedBudget.SubgridBitBudget);
        var coarseCols = probeOccupancy.Columns;
        var cellW = probeOccupancy.CellWidth;
        var cellH = probeOccupancy.CellHeight;
        Assert.AreEqual(16, coarseCols);
        Assert.AreEqual(14, probeOccupancy.Rows);
        Assert.AreEqual(3, cellW);
        Assert.AreEqual(3, cellH);

        var layout = Layout(size, size);
        var rawBytes = new byte[checked((int)layout.ByteLength)];
        for (var y = 0; y < size; y++)
        {
            var rowSpan = rawBytes.AsSpan(y * layout.StrideBytes, size * 2);
            var ly = y % tileSize;
            var inPatchY = ly >= patchOffset && ly < patchOffset + patchHeight;
            var coarseRow = inPatchY ? (ly - patchOffset) / cellH : -1;
            for (var x = 0; x < size; x++)
            {
                ushort code = 180;
                if (inPatchY)
                {
                    var lx = x % tileSize;
                    if (lx >= patchOffset && lx < patchOffset + patchWidth)
                    {
                        var coarseCol = (lx - patchOffset) / cellW;
                        var bit = coarseRow * coarseCols + coarseCol;
                        var patternByte = allPlusPattern[(bit >> 3) % 3];
                        if ((patternByte & (1 << (bit & 7))) != 0)
                            code = 2200;
                    }
                }
                BinaryPrimitives.WriteUInt16LittleEndian(rowSpan[(x * 2)..], code);
            }
        }

        var raw = CreateRaw(layout, rawBytes);
        var linear = await Normalize(raw);
        var input = Bind(raw, linear);
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(ExposureMeasurementProducts.Request(input, raw, linear));
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        Assert.IsLessThanOrEqualTo(StructuredProcessingProductDescriptorV1.MaximumPayloadBytes, product.Payload.Length,
            $"Worst-case post-budget '+' escaping payload must stay <= 4 MiB (got {product.Payload.Length} bytes).");

        var escapedPlusCount = 0;
        var jsonText = Encoding.UTF8.GetString(product.Payload.Span);
        for (var idx = 0; (idx = jsonText.IndexOf("\\u002B", idx, StringComparison.Ordinal)) >= 0; idx += 6)
            escapedPlusCount++;
        Assert.IsGreaterThanOrEqualTo(4096 * 35, escapedPlusCount,
            $"Expected heavy post-budget \\u002B escaping across all 4,096 coarse masks (got {escapedPlusCount}).");

        var readProduct = ExposureMeasurementProducts.Read(product);
        var readArtifact = ExposureMeasurementProducts.Read(ProductArtifact(product, raw), input);
        Assert.HasCount(4096, readProduct.Sources.Exclusions);
        Assert.HasCount(4096, readArtifact.Sources.Exclusions);
        Assert.IsTrue(readProduct.Sources.Exclusions.All(static e =>
            e.Occupancy is { Columns: 16, Rows: 14, CellWidth: 3, CellHeight: 3 } occ &&
            occ.BitsBase64.Count(static ch => ch == '+') >= 35));

        // 2. When non-source envelope fields consume almost the entire 4 MiB envelope so even 1-cell coarse masks do
        //    not fit, Step 4 omits Occupancy cleanly (bounding-box fallback), both Read overloads verify it, and
        //    injecting a non-null Occupancy is rejected by both Read overloads.
        const int smallSize = 160;
        var smallLayout = Layout(smallSize, smallSize);
        var smallCenter = new PixelPoint(80.5, 80.5);
        var smallRaw = CreateRaw(smallLayout, Generate(smallLayout, (x, y) =>
        {
            var r = double.Hypot(x + 0.5 - smallCenter.X, y + 0.5 - smallCenter.Y);
            double value = 180 + Gaussian(x, y, smallCenter, 1.25, 2200);
            if (r is >= 14.0 and <= 30.0)
                value += 1400.0;
            return (ushort)Math.Clamp(Math.Round(value), 0, 4095);
        }));
        var smallLinear = await Normalize(smallRaw);
        var oversizedInput = Bind(smallRaw, smallLinear) with
        {
            OpticalCalibrationVersion = new string('v', StructuredProcessingProductDescriptorV1.MaximumPayloadBytes - 10_000)
        };
        var omittedOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            ExposureMeasurementProducts.Request(oversizedInput, smallRaw, smallLinear));
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, omittedOutcome.Status, omittedOutcome.ReasonCode);
        var omittedProduct = omittedOutcome.Products.Single();
        Assert.IsLessThanOrEqualTo(StructuredProcessingProductDescriptorV1.MaximumPayloadBytes, omittedProduct.Payload.Length);

        var omittedRead = ExposureMeasurementProducts.Read(omittedProduct);
        var omittedArtifact = ProductArtifact(omittedProduct, smallRaw);
        var omittedVerified = ExposureMeasurementProducts.Read(omittedArtifact, oversizedInput);
        var omittedRing = omittedRead.Sources.Exclusions.Single(e => e.ReasonCode == StellarExclusionReasons.ExtendedRegion);
        Assert.IsNull(omittedRing.Occupancy, "Exhausted whole-product envelope must fall back to bounding-box exclusion.");
        Assert.HasCount(omittedRead.Sources.Exclusions.Count, omittedVerified.Sources.Exclusions);

        // Tampering to inject a non-null Occupancy when CoarseCellBudget == 0 must be rejected.
        var injectedRing = omittedRing with
        {
            Occupancy = StellarFootprintOccupancy.Create(omittedRing.Footprint!.Value, static (_, _) => true)
        };
        var injectedProduct = WithMeasurement(omittedProduct, omittedRead with
        {
            Sources = omittedRead.Sources with { Exclusions = [injectedRing] }
        });
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(injectedProduct));
        Assert.ThrowsExactly<InvalidDataException>(() =>
            ExposureMeasurementProducts.Read(ProductArtifact(injectedProduct, smallRaw), oversizedInput));
    }

    private static ProjectedSceneV1 WithProjection(ProjectedSceneV1 scene, ProjectedSceneProjection projection)
    {
        var projector = ProjectorFactory.Create(ProjectedSceneJson.ToProjectionContext(projection));
        var basis = CameraBasis.Create(projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees,
            projection.RollDegrees, projection.HorizontalFlip);
        var projected = scene with
        {
            Projection = projection,
            Objects = scene.Objects.Select(item => item with
            {
                Pixel = projector.Project(item.ApparentHorizontal)!.Value,
                CameraDirection = basis.ToCamera(CameraBasis.FromHorizontal(item.ApparentHorizontal)),
                ProjectionVersion = projection.AlgorithmVersion
            }).ToArray()
        };
        return projected with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(projected) };
    }

    [TestMethod]
    public async Task BayerWithoutPhaseProofWithholdsSources()
    {
        var layout = Layout(128, 128, bayer: true) with { Readout = null };
        var raw = CreateRaw(layout, Generate(layout, static (_, _) => 4095));
        var linear = await Normalize(raw);
        var result = await Run(raw, linear, Bind(raw, linear));
        Assert.AreEqual((long)layout.Width * layout.Height, result.RawBrightness!.SensorClipping.Count);
        Assert.AreEqual("unqualified-cfa-phase", result.Sources.Reason);
        Assert.IsNull(result.Sources.LocalBayerPattern);
        Assert.IsNull(result.Hfd.ValidCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GeneratedGeometryUsesDeliveredCoordinatesAndIndependentColorResponse(bool bayer)
    {
        // Positions are delivered pixel-edge coordinates at reference exposure 103.
        (double X, double Y, double Sigma, int Red, int Green, int Blue)[] targets =
        [
            (27.35, 22.55, 1.05, 2900, 300, 120),
            (79.8, 28.25, 1.25, 220, 1480, 310),
            (126.45, 40.65, 1.15, 210, 270, 2800),
            (42.65, 78.1, 0.95, 1200, 1200, 1200),
            (94.1, 91.4, 1.1, 1400, 1400, 1400),
            (125.7, 101.45, 1.35, 1650, 1650, 1650),
            (53.9, 48.15, 1.05, 1190, 1190, 1190),
            (23.2, 102.4, 1.05, 1480, 1480, 1480),
            (112.75, 67.9, 1.05, 950, 950, 950)
        ];
        (double X, double Y)[] shifts = [(-2, 3), (1, -1.5), (0, 0), (3, 1), (-1.5, 2), (2, -2)];
        var layout = Layout(160, 128, bayer);
        foreach (var shift in shifts)
        {
            var expected = targets.Select(target => new PixelPoint(target.X + shift.X, target.Y + shift.Y)).ToArray();
            var bytes = Generate(layout, (x, y) =>
            {
                double sample = 91;
                for (var index = 0; index < targets.Length; index++)
                {
                    var target = targets[index];
                    // The delivered tile is explicitly B G / G R, independent of the decoder being exercised.
                    var amplitude = !bayer ? (target.Red + target.Green + target.Blue) / 3d :
                        (x % 2, y % 2) switch { (0, 0) => target.Blue, (1, 1) => target.Red, _ => target.Green };
                    sample += Gaussian(x, y, expected[index], target.Sigma, amplitude);
                }
                return (ushort)Math.Round(sample);
            });
            await AssertGeometry(layout, bytes, expected, bayer);
        }
    }

    internal static async Task AssertGeometry(FrameLayoutDescriptor layout, byte[] bytes,
        IReadOnlyList<PixelPoint> expected, bool bayer)
    {
        var raw = CreateRaw(layout, bytes);
        var linear = await Normalize(raw);
        var result = await Run(raw, linear, Bind(raw, linear));
        Assert.AreEqual(bayer ? BayerPattern.Bggr : null, result.Sources.LocalBayerPattern);
        Assert.AreEqual(ExposureMeasurementStatus.Usable, result.Sources.Status);
        Assert.HasCount(expected.Count, result.Sources.Detections);
        var matched = new HashSet<int>();
        foreach (var point in expected)
        {
            var detection = Nearest(result.Sources.Detections, point);
            Assert.IsTrue(matched.Add(detection.Index), "Each expected source needs its own detection.");
            AssertNear(result.Sources.Detections, point, bayer ? 0.65 : 0.20);
        }

        if (bayer)
        {
            var red = Nearest(result.Sources.Detections, expected[0]);
            var blue = Nearest(result.Sources.Detections, expected[2]);
            Assert.IsGreaterThan(blue.Flux, red.Flux);

            var wrong = CreateRaw(layout with
            {
                Readout = layout.Readout! with { RoiX = 0, RoiY = 0, CfaOriginX = 0, CfaOriginY = 0 }
            }, bytes);
            var wrongLinear = await Normalize(wrong);
            var wrongPhase = await Run(wrong, wrongLinear, Bind(wrong, wrongLinear));
            Assert.IsLessThan(Nearest(wrongPhase.Sources.Detections, expected[2]).Flux,
                Nearest(wrongPhase.Sources.Detections, expected[0]).Flux);
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness",
        Justification = "Seeded noise is required to reproduce the physical fixture.")]
    public async Task CenteredFocusCorroboratesBayerTrailFlagsWithObservedCoreShape()
    {
        const int segments = 25;
        const double sigma = 1.5;
        const double flux = 5000;
        const double noise = 13;
        var center = new PixelPoint(80.3, 80.7);
        var checkedRetainedEvidence = false;
        foreach (var bayer in new[] { false, true })
            foreach (var trail in bayer ? new[] { 0.0, 3.0, 3.5, 6.0 } : new[] { 0.0, 6.0 })
            {
                var layout = Layout(160, 160, bayer);
                var count = trail is 0 or 6 ? 64 : 8;
                var measured = 0;
                var originalFlags = 0;
                var falseExclusions = 0;
                var corroboratedRound = 0;
                for (var seed = 0; seed < count; seed++)
                {
                    var rng = new Random(7000 + seed);
                    var amplitude = flux / (2 * Math.PI * sigma * sigma) / segments;
                    // A circular optical PSF convolved with the known linear track, then independent sensor
                    // noise. The measurer receives only pixels, including the genuine reconstruction for CFA.
                    var raw = CreateRaw(layout, Generate(layout, (x, y) =>
                    {
                        double signal = 0;
                        for (var k = 0; k < segments; k++)
                        {
                            var offset = (k / (segments - 1.0) - 0.5) * trail;
                            signal += Gaussian(x, y, new PixelPoint(center.X + offset, center.Y), sigma, amplitude);
                        }
                        var u1 = 1.0 - rng.NextDouble();
                        var u2 = rng.NextDouble();
                        var perturbation = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2) * noise;
                        return (ushort)Math.Clamp(Math.Round(300 + signal + perturbation), 0, 4095);
                    }));
                    var linear = await Normalize(raw);
                    var input = Bind(raw, linear);
                    var request = ExposureMeasurementProducts.Request(input, raw, linear);
                    var product = await Produce(request);
                    var result = ExposureMeasurementProducts.Read(product);
                    var detection = result.Sources.Detections.SingleOrDefault(star =>
                        Math.Abs(star.Pixel.X - center.X) < 4 && Math.Abs(star.Pixel.Y - center.Y) < 2);
                    if (detection is null) continue;
                    measured++;
                    Assert.AreEqual(0, detection.SaturatedSampleCount);
                    var focus = result.Hfd.Samples.Single(sample => sample.DetectionIndex == detection.Index);
                    var trailed = detection.Conditions.HasFlag(StellarSourceConditions.Trailed);
                    if (trailed) originalFlags++;

                    if (trail == 0)
                    {
                        if (focus.Reason == "trailed-source") falseExclusions++;
                        if (focus.BayerCoreTrailUpperPixels is { } upper)
                        {
                            Assert.IsTrue(bayer && trailed && upper is >= 0 and < 3);
                            Assert.IsTrue(focus.DiameterPixels is > 0);
                            corroboratedRound++;
                            if (!checkedRetainedEvidence)
                            {
                                await AssertBayerProofRequiresItsRetainedRecipeAsync(request, product, result, focus);
                                checkedRetainedEvidence = true;
                            }
                        }
                    }
                    else if (trail == 6 || trailed)
                    {
                        Assert.AreEqual("trailed-source", focus.Reason);
                        Assert.IsNull(focus.DiameterPixels);
                        Assert.IsNull(focus.BayerCoreTrailUpperPixels);
                    }
                }

                // Report the observed error frequency for this deterministic noise cohort, keeping the
                // single independent focus-aperture exclusion separate from trail misclassification.
                Console.WriteLine(FormattableString.Invariant(
                    $"Bayer={bayer} trueTrail={trail} measured={measured}/{count} oldFlags={originalFlags} falseTrailExclusions={falseExclusions} roundProofs={corroboratedRound}"));
                Assert.AreEqual(bayer && trail == 6 ? 63 : count, measured);
                if (trail == 0)
                {
                    Assert.AreEqual(bayer ? 6 : 1, originalFlags);
                    Assert.AreEqual(bayer ? 0 : 1, falseExclusions);
                    Assert.AreEqual(bayer ? 6 : 0, corroboratedRound);
                }
                else
                {
                    Assert.IsGreaterThan(count / 2, originalFlags);
                }
            }
        Assert.IsTrue(checkedRetainedEvidence);
    }

    private static async Task AssertBayerProofRequiresItsRetainedRecipeAsync(ProcessingExecutionRequest request,
        ProcessingProduct currentProduct, ExposureMeasurementV1 result, ExposureHfdSampleV1 focus)
    {
        var historical = await Produce(request with { Options = HistoricalFocusOptions(result.Input, bayerCoreCheck: false) });
        var original = ExposureMeasurementProducts.Read(historical);
        Assert.AreNotEqual(historical.OutputIdentitySha256, currentProduct.OutputIdentitySha256);
        CollectionAssert.AreEqual(original.Sources.Detections.ToArray(), result.Sources.Detections.ToArray());
        Assert.AreEqual("trailed-source", original.Hfd.Samples[focus.DetectionIndex].Reason);
        var previous = await Produce(request with { Options = HistoricalFocusOptions(result.Input, bayerCoreCheck: true) });
        var previousResult = ExposureMeasurementProducts.Read(previous);
        Assert.AreNotEqual(previous.OutputIdentitySha256, currentProduct.OutputIdentitySha256);
        CollectionAssert.AreEqual(previousResult.Sources.Detections.ToArray(), result.Sources.Detections.ToArray());
        CollectionAssert.AreEqual(previousResult.Hfd.Samples.ToArray(), result.Hfd.Samples.ToArray());
        var previousEligibleFirst = await Produce(request with { Options = HistoricalEligibleFirstOptions(result.Input) });
        Assert.AreNotEqual(previousEligibleFirst.OutputIdentitySha256, currentProduct.OutputIdentitySha256);
        CollectionAssert.AreEqual(previous.Payload.ToArray(), previousEligibleFirst.Payload.ToArray());

        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(ReplaceSample(
            currentProduct, focus with { BayerCoreTrailUpperPixels = 3 })));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(ReplaceSample(
            currentProduct, focus with { BayerCoreTrailUpperPixels = null })));
        Assert.ThrowsExactly<InvalidDataException>(() => ExposureMeasurementProducts.Read(ReplaceSample(historical, focus)));

        ProcessingProduct ReplaceSample(ProcessingProduct product, ExposureHfdSampleV1 replacement)
        {
            var samples = result.Hfd.Samples.ToArray();
            samples[replacement.DetectionIndex] = replacement;
            return WithMeasurement(product, result with { Hfd = result.Hfd with { Samples = samples } });
        }
    }

    private static JsonElement HistoricalFocusOptions(ExposureMeasurementInputV1 input, bool bayerCoreCheck)
        => bayerCoreCheck
            ? JsonSerializer.SerializeToElement(new { BayerFocus = "noise-corrected-bayer-core-v1", Input = input }, MeasurementJson)
            : JsonSerializer.SerializeToElement(input, MeasurementJson);

    private static JsonElement HistoricalEligibleFirstOptions(ExposureMeasurementInputV1 input)
        => JsonSerializer.SerializeToElement(new { HfdAllocation = "eligible-first-v1", Input = input }, MeasurementJson);

    private static ProcessingProduct WithMeasurement(ProcessingProduct product, ExposureMeasurementV1 measurement)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(CaptureContractJson.Canonicalize(
            JsonSerializer.SerializeToElement(measurement, MeasurementJson)));
        var hash = ProcessingIdentity.ComputePayloadSha256(payload);
        return product with { Payload = payload, ChecksumSha256 = hash, ContentIdentitySha256 = hash };
    }

    private static double Gaussian(int x, int y, PixelPoint center, double sigma, double amplitude)
    {
        var dx = x + 0.5 - center.X;
        var dy = y + 0.5 - center.Y;
        return amplitude * Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
    }

    private static void AssertNear(IReadOnlyList<StellarDetection> detections, PixelPoint expected, double tolerance)
    {
        var measured = Nearest(detections, expected);
        Assert.AreEqual(expected.X, measured.Pixel.X, tolerance);
        Assert.AreEqual(expected.Y, measured.Pixel.Y, tolerance);
    }

    private static StellarDetection Nearest(IReadOnlyList<StellarDetection> detections, PixelPoint expected)
        => detections.MinBy(item => Math.Pow(item.Pixel.X - expected.X, 2) + Math.Pow(item.Pixel.Y - expected.Y, 2))!;

    internal static FrameLayoutDescriptor Layout(int width, int height, bool bayer = false)
        => new(width, height, width * 2, bayer ? CameraPixelFormat.BayerRggb16 : CameraPixelFormat.Mono16,
            FrameByteOrder.LittleEndian, 12, 16, FrameSamplePacking.ByteAligned,
            bayer ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None, 64, 4095, width * height * 2L)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample,
            Readout = bayer ? new(width + 2, height + 2, 1, 1, width, height, 1, 1, FrameBinningAlgorithm.IdentityV1, 1, 1) : null
        };

    private static byte[] Generate(FrameLayoutDescriptor layout, Func<int, int, ushort> code)
    {
        var bytes = new byte[checked((int)layout.ByteLength)];
        for (var y = 0; y < layout.Height; y++)
        {
            for (var x = 0; x < layout.Width; x++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(y * layout.StrideBytes + x * 2), code(x, y));
        }
        return bytes;
    }

    internal static ProcessingArtifact CreateRaw(FrameLayoutDescriptor layout, byte[] bytes, Guid? capture = null)
        => new(Guid.NewGuid(), FrameArtifactRole.Raw, "source", ProcessingIdentity.CreateRecipeIdentity(RawRecipe).IdentitySha256,
            "application/x-hvo-frame", layout, bytes, ExposureTime, TimeSpan.FromSeconds(20), Compatibility)
        {
            CaptureId = capture ?? Guid.NewGuid(),
            DescriptorIdentitySha256 = new string('A', 64),
            RecipeDescriptor = RawRecipe
        };

    internal static ExposureMeasurementInputV1 Bind(ProcessingArtifact raw, ProcessingArtifact? linear,
        ExposureCalibrationDisposition disposition = ExposureCalibrationDisposition.NormalizationOnly, string? reason = null)
        => new(Source(raw), linear is null ? null : Source(linear), disposition, reason, Compatibility.Orientation, 2.4);

    private static ExposureMeasurementSourceV1 Source(ProcessingArtifact artifact)
        => new(artifact.CaptureId!.Value, artifact.ArtifactId, artifact.DescriptorIdentitySha256!,
            ProcessingIdentity.ComputePayloadSha256(artifact.Payload), artifact.RecipeIdentitySha256,
            artifact.Role == FrameArtifactRole.Raw ? null : artifact.ContentIdentitySha256, artifact.Layout!);

    internal static async Task<ProcessingArtifact> Normalize(ProcessingArtifact raw)
        => ProductArtifact(await Produce(new(BuiltInProcessingRecipes.LinearNormalization,
            JsonSerializer.SerializeToElement(new LinearNormalizationOptions()), ProcessingInputSelector.Raw(raw.Variant),
            [raw], "qualified-linear-v1")), raw);

    private static ProcessingExecutionRequest ReferenceCalibration(ProcessingArtifact raw, Func<int, int, ushort>? defect = null)
    {
        var layout = CalibrationMasterBuilder.CreateNormalizedLayout(raw.Layout!);
        var references = new ProcessingArtifact[]
        {
            Master(CalibrationReferenceKinds.Bias, static (_, _) => 1000),
            Master(CalibrationReferenceKinds.Dark, static (_, _) => 1000),
            Master(CalibrationReferenceKinds.Flat, static (x, y) =>
                (ushort)(Math.Abs(x - 142) <= 8 && Math.Abs(y - 55) <= 8 ? 6000 : 11000)),
            Master(CalibrationReferenceKinds.Defect, defect ?? (static (_, _) => 0))
        };
        var profile = new ReferenceCalibrationProfileV1(
            ReferenceCalibrationProfileV1.CurrentSchemaVersion, "controlled-reference", "1", "independent clipping test",
            ExposureTime.AddDays(-1), null, layout.Width, layout.Height, layout.PixelFormat,
            10000, 9, 11, -11, -9, references.Select(reference => new CalibrationReferenceDescriptorV1(
                reference.Variant, reference.ArtifactId, Convert.ToHexString(SHA256.HashData(reference.Payload.Span)),
                reference.Integration, reference.Conditions!.Gain, reference.Conditions.TemperatureC)).ToArray());
        var profileInput = new ProcessingAuxiliaryInput("calibration-profile", ProcessingAuxiliaryInputKind.CanonicalJson,
            SchemaVersion: ReferenceCalibrationProfileV1.CurrentSchemaVersion,
            IdentitySha256: ReferenceCalibrationProfileJson.ComputeIdentitySha256(profile),
            Payload: ReferenceCalibrationProfileJson.Serialize(profile));
        return new(BuiltInProcessingRecipes.ReferenceCalibration, JsonSerializer.SerializeToElement(new ReferenceCalibrationOptions()),
            ProcessingInputSelector.Raw(raw.Variant), [raw, .. references], "qualified-linear-v1",
            AuxiliaryInputs: [profileInput, .. references.Select(reference =>
                new ProcessingAuxiliaryInput(reference.Variant + "-reference", ProcessingAuxiliaryInputKind.Artifact,
                    ProcessingInputSelector.Raw(reference.Variant), ArtifactId: reference.ArtifactId))]);

        ProcessingArtifact Master(string kind, Func<int, int, ushort> value) =>
            CreateRaw(layout, Generate(layout, value)) with { Variant = kind, Conditions = raw.Conditions };
    }

    private static ushort[] ReadCodes(ReadOnlyMemory<byte> payload)
        => Enumerable.Range(0, payload.Length / sizeof(ushort))
            .Select(index => BinaryPrimitives.ReadUInt16LittleEndian(payload.Span[(index * sizeof(ushort))..])).ToArray();

    private static ProcessingArtifact ProductArtifact(ProcessingProduct product, ProcessingArtifact raw)
        => new(ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
            product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload, raw.CreatedUtc,
            raw.Integration, product.Compatibility, SourceArtifactIds: product.SourceArtifactIds)
        {
            ProductKind = product.Kind,
            SchemaVersion = product.SchemaVersion,
            ContentIdentitySha256 = product.ContentIdentitySha256 ?? product.OutputIdentitySha256,
            CaptureId = raw.CaptureId,
            DescriptorIdentitySha256 = new string('C', 64),
            RecipeDescriptor = product.Recipe.Descriptor
        };

    private static async Task<ProcessingProduct> Produce(ProcessingExecutionRequest request)
    {
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(request);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        return outcome.Products.Single();
    }

    internal static async Task<ExposureMeasurementV1> Run(ProcessingArtifact raw, ProcessingArtifact? linear,
        ExposureMeasurementInputV1 input, bool historicalFocus = false)
    {
        var request = ExposureMeasurementProducts.Request(input, raw, linear);
        if (historicalFocus)
            request = request with { Options = HistoricalFocusOptions(input, bayerCoreCheck: false) };
        return ExposureMeasurementProducts.Read(await Produce(request));
    }

    private sealed record RetainedReader(LinearExposureLookup Linear, ExposureProductLookup Product) : IExposureProductReader
    {
        public ValueTask<ExposureInputV1?> ReadExposureAsync(ExposureKey key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(key == Linear.Exposure.Key ? Linear.Exposure : null);

        public ValueTask<LinearExposureLookup?> ReadLinearAsync(ExposureKey key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(key == Linear.Exposure.Key ? Linear : null);

        public ValueTask<ExposureProductLookup?> ReadProductAsync(
            ExposureKey key, ExposureProductSlot slot, CancellationToken cancellationToken) =>
            ValueTask.FromResult(key == Product.Exposure && slot == Product.Slot ? Product : null);

        public ValueTask<IReadOnlyList<ExposureProductLookup>> ReadProductsAfterAsync(
            CameraKey camera, long afterRevision, int maximumCount, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("An exact product read must never query the latest revision.");
    }
}
