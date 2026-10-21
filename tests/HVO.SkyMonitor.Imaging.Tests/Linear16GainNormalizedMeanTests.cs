using System.Buffers.Binary;
using System.Numerics;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class Linear16GainNormalizedMeanTests
{
    private const long OneElectron = ElectronsPerAduFixedPoint.Scale;
    private const long MaximumCoefficient = ElectronsPerAduFixedPoint.MaximumScaledValue;
    private const int Unity = Linear16GainNormalizedMean.FlatScale;
    private const int MaximumFlat = Linear16GainNormalizedMean.MaximumFlatScaledValue;

    [TestMethod]
    public void Compute_ConvertsEachInputToElectronsAndEncodesAtTheReferenceCoefficient()
    {
        // The design note's fixture: 100 corrected ADU at k = 2 and 200 at k = 1 are 200 e- each, which is 100 ADU at
        // reference k = 2. The plain ADU mean would be 150.
        var result = Mono(
            2 * OneElectron,
            Input(2 * OneElectron, [164], [64]),
            Input(OneElectron, [264], [64]));

        CollectionAssert.AreEqual(new ushort[] { 100 }, Codes(result));
        CollectionAssert.AreEqual(new ushort[] { 2 }, result.ValidCounts.ToArray());
        Assert.AreEqual(Linear16GainNormalizedMean.AlgorithmVersion, result.AlgorithmVersion);
        Assert.AreEqual(2, result.SourceCount);
        Assert.AreEqual(0, result.NoCoverageCount + result.ClippedLowCount + result.ClippedHighCount);
    }

    [TestMethod]
    public void Compute_SubtractsTheBiasExactlyOnce()
    {
        // The dark is already bias-subtracted: 1000 - 100 - 50 = 850.
        var result = Mono(OneElectron, Input(OneElectron, [1000], [100], dark: [50]));

        CollectionAssert.AreEqual(new ushort[] { 850 }, Codes(result));
    }

    [TestMethod]
    public void Compute_KeepsNegativeCorrectedSignalUntilTheFinalEncode()
    {
        var result = Mono(
            OneElectron,
            Input(OneElectron, [70, 70], [100, 100]),
            Input(OneElectron, [150, 110], [100, 100]));

        // (-30 + 50) / 2 = 10, not (0 + 50) / 2; (-30 + 10) / 2 = -10 clips to 0 only at output.
        CollectionAssert.AreEqual(new ushort[] { 10, 0 }, Codes(result));
        CollectionAssert.AreEqual(new ushort[] { 2, 2 }, result.ValidCounts.ToArray());
        Assert.AreEqual(1, result.ClippedLowCount);
        Assert.AreEqual(0, result.NoCoverageCount);
    }

    [TestMethod]
    public void Compute_TestsSaturationOnTheStoredCodeBeforeBias()
    {
        var result = Mono(
            OneElectron,
            Input(OneElectron, [4000, 3999], [200, 200], saturationCode: 4000),
            Input(OneElectron, [1200, 1201], [200, 200], saturationCode: 4000));

        // Code 4000 is saturated although 4000 - 200 is below the threshold; code 3999 is valid.
        CollectionAssert.AreEqual(new ushort[] { 1000, 2400 }, Codes(result));
        CollectionAssert.AreEqual(new ushort[] { 1, 2 }, result.ValidCounts.ToArray());
    }

    [TestMethod]
    public void Compute_ASaturatedHighGainSampleDoesNotSuppressAValidLowGainSample()
    {
        var highGain = Input(OneElectron / 2, [4095, 2064], [64, 64], saturationCode: 4095);
        var lowGain = Input(2 * OneElectron, [1064, 564], [64, 64], saturationCode: 4095);

        var result = Mono(2 * OneElectron, highGain, lowGain);

        // Photosite 0 keeps the low-gain 2000 e- alone; photosite 1 averages 1000 e- from each.
        CollectionAssert.AreEqual(new ushort[] { 1000, 500 }, Codes(result));
        CollectionAssert.AreEqual(new ushort[] { 1, 2 }, result.ValidCounts.ToArray());
        Assert.AreEqual(0, result.NoCoverageCount);
    }

    [TestMethod]
    [DataRow(0, 1600, 1, 300)]
    [DataRow(-1, 1600, 1, 300)]
    [DataRow(int.MinValue, 1600, 1, 300)]
    [DataRow(MaximumFlat + 1, 1600, 1, 300)]
    [DataRow(int.MaxValue, 1600, 1, 300)]
    [DataRow(MaximumFlat, 1600, 2, 200)]
    [DataRow(Unity / 2, 50, 2, 200)]
    [DataRow(1, 0, 2, 150)]
    public void Compute_ExcludesFlatsOutsideTheDeclaredRange(int flat, int light, int expectedCount, int expectedCode)
    {
        var result = Mono(
            OneElectron,
            Input(OneElectron, [(ushort)light], [0], flat: [flat]),
            Input(OneElectron, [300], [0]));

        CollectionAssert.AreEqual(new[] { (ushort)expectedCode }, Codes(result));
        CollectionAssert.AreEqual(new[] { (ushort)expectedCount }, result.ValidCounts.ToArray());
    }

    [TestMethod]
    public void Compute_ExcludesSamplesMarkedDefective()
    {
        var result = Mono(
            OneElectron,
            Input(OneElectron, [500, 500, 500], [0, 0, 0], defects: [0, 1, 255]),
            Input(OneElectron, [100, 100, 100], [0, 0, 0]));

        CollectionAssert.AreEqual(new ushort[] { 300, 100, 100 }, Codes(result));
        CollectionAssert.AreEqual(new ushort[] { 2, 1, 1 }, result.ValidCounts.ToArray());
    }

    [TestMethod]
    public void Compute_MarksAPhotositeWithoutValidSamplesAsNoCoverageRatherThanZeroFlux()
    {
        var result = Mono(
            OneElectron,
            Input(OneElectron, [4000, 100, 300], [100, 100, 100], saturationCode: 4000),
            Input(OneElectron, [100, 100, 500], [100, 100, 100], defects: [1, 0, 0]),
            Input(OneElectron, [100, 100, 700], [100, 100, 100], flat: [0, Unity, Unity]));

        // Photosite 0 is saturated, defective and flat-invalid in turn; photosite 1 is a genuine zero.
        CollectionAssert.AreEqual(new ushort[] { 0, 0, 400 }, Codes(result));
        CollectionAssert.AreEqual(new ushort[] { 0, 3, 3 }, result.ValidCounts.ToArray());
        CollectionAssert.AreEqual(new byte[] { 1, 0, 0 }, result.NoCoverageMask.ToArray());
        Assert.AreEqual(1, result.NoCoverageCount);
        Assert.AreEqual(0, result.ClippedLowCount + result.ClippedHighCount);
    }

    [TestMethod]
    public void Compute_StaysExactAtTheLargestLegalCoefficientCodeAndInputCount()
    {
        // Every input has the largest coefficient. Photosite 1 has the largest positive signal over the smallest
        // flat (1.31069e29 per sample, 65535 of them), photosite 2 the most negative, and photosite 3 averages 32768
        // samples of 65534 with 32767 of 0: 2147418112 / 65535 = 32767.4999924 rounds to 32767.
        var even = Input(
            MaximumCoefficient,
            [65534, 65534, 0, 65534],
            [0, 0, 65535, 0],
            dark: [0, -65535, 65535, 0],
            flat: [Unity, 1, 1, Unity]);
        var odd = even with { Light = new ushort[] { 65534, 65534, 0, 0 } };
        var inputs = Enumerable.Range(0, Linear16GainNormalizedMean.MaximumInputCount)
            .Select(index => index % 2 == 0 ? even : odd)
            .ToArray();

        var result = Linear16GainNormalizedMean.Compute(4, 1, CameraPixelFormat.Mono16, inputs, MaximumCoefficient);

        CollectionAssert.AreEqual(new ushort[] { 65534, 65535, 0, 32767 }, Codes(result));
        CollectionAssert.AreEqual(Enumerable.Repeat(ushort.MaxValue, 4).ToArray(), result.ValidCounts.ToArray());
        Assert.AreEqual(1, result.ClippedHighCount);
        Assert.AreEqual(1, result.ClippedLowCount);
    }

    [TestMethod]
    public void Compute_RejectsInputsOutsideTheLegalRange()
    {
        var valid = Input(OneElectron, [100], [0]);

        Assert.ThrowsExactly<ArgumentException>(
            () => Compute(Enumerable.Repeat(valid, Linear16GainNormalizedMean.MaximumInputCount + 1).ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid with { ElectronsPerAduScaled = 0 }]));
        Assert.ThrowsExactly<ArgumentException>(
            () => Compute([valid with { ElectronsPerAduScaled = MaximumCoefficient + 1 }]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Compute([valid], reference: MaximumCoefficient + 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Compute([valid], reference: 0));
        int[] darkTooHigh = [65536];
        int[] darkTooLow = [-65536];
        byte[] defective = [1];
        Assert.ThrowsExactly<InvalidDataException>(() => Compute([valid with { Dark = darkTooHigh }]));
        Assert.ThrowsExactly<InvalidDataException>(() => Compute([valid with { Dark = darkTooLow }]));
        // A dark outside the range is rejected even where the sample is otherwise excluded.
        Assert.ThrowsExactly<InvalidDataException>(
            () => Compute([valid with { Dark = darkTooHigh, DefectMask = defective }]));
    }

    [TestMethod]
    [DataRow(5L, 1, 1003)]
    [DataRow(3L, 1, 1003)]
    [DataRow(5L, -1, 1001)]
    [DataRow(3L, -1, 1001)]
    public void Compute_RoundsEachSampleToTheElectronGridHalfToEven(long scaledCoefficient, int signal, int expectedCode)
    {
        // With a flat of 2.0 the sample is k × signal / 2 grid units, so ±2.5 and ±1.5 are exact ties that round to
        // ±2. The second input adds 2004 units so that the sign survives the encode, and the mean of the two is exact.
        var tie = Input(scaledCoefficient, [(ushort)(100 + signal)], [100], flat: [2 * Unity]);
        var offset = Input(1, [2104], [100]);

        var result = Mono(1, tie, offset);

        CollectionAssert.AreEqual(new[] { (ushort)expectedCode }, Codes(result));
    }

    [TestMethod]
    [DataRow(1, 2, 2)]
    [DataRow(2, 3, 2)]
    [DataRow(2, 5, 4)]
    [DataRow(0, 1, 0)]
    public void Compute_RoundsTheMeanToAnOutputCodeHalfToEven(int first, int second, int expectedCode)
    {
        var result = Mono(
            OneElectron,
            Input(OneElectron, [(ushort)first], [0]),
            Input(OneElectron, [(ushort)second], [0]));

        CollectionAssert.AreEqual(new[] { (ushort)expectedCode }, Codes(result));
    }

    [TestMethod]
    public void Compute_ReportsOutputClippingSeparatelyFromCoverage()
    {
        var result = Mono(
            OneElectron,
            Input(
                OneElectron,
                [65534, 65534, 0, 0, 100],
                [0, 0, 0, 0, 0],
                dark: [-1, -2, 0, 1, 0],
                defects: [0, 0, 0, 0, 1]),
            Input(
                OneElectron,
                [65534, 65534, 0, 0, 100],
                [0, 0, 1, 2, 0],
                dark: [-1, -2, 0, 0, 0],
                defects: [0, 0, 0, 0, 1]));

        // 65535 fits; 65536 clips high; -0.5 rounds to 0 and is not clipped; -1.5 rounds to -2 and clips low.
        CollectionAssert.AreEqual(new ushort[] { 65535, 65535, 0, 0, 0 }, Codes(result));
        Assert.AreEqual(1, result.ClippedHighCount);
        Assert.AreEqual(1, result.ClippedLowCount);
        Assert.AreEqual(1, result.NoCoverageCount);
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0, 1 }, result.NoCoverageMask.ToArray());
    }

    [TestMethod]
    public void Compute_IsByteIdenticalAcrossRunsAndWorkingSizesAndMatchesExactArithmetic()
    {
        const int width = 37;
        const int height = 11;
        const long reference = 7_900_000_000_000;
        var inputs = RandomFixture(width * height);

        var baseline = Linear16GainNormalizedMean.Compute(width, height, CameraPixelFormat.Mono16, inputs, reference);

        foreach (var workingPhotosites in new[] { 1, 2, 7, 64, 406, 407, Linear16GainNormalizedMean.DefaultWorkingPhotosites, 1 })
        {
            var repeated = Linear16GainNormalizedMean.Compute(
                width,
                height,
                CameraPixelFormat.Mono16,
                inputs,
                reference,
                workingPhotosites);
            CollectionAssert.AreEqual(baseline.PixelData.ToArray(), repeated.PixelData.ToArray());
            CollectionAssert.AreEqual(baseline.ValidCounts.ToArray(), repeated.ValidCounts.ToArray());
            CollectionAssert.AreEqual(baseline.NoCoverageMask.ToArray(), repeated.NoCoverageMask.ToArray());
            Assert.AreEqual(baseline.ClippedLowCount, repeated.ClippedLowCount);
            Assert.AreEqual(baseline.ClippedHighCount, repeated.ClippedHighCount);
            Assert.AreEqual(baseline.NoCoverageCount, repeated.NoCoverageCount);
        }

        var (codes, counts) = ExactOracle(inputs, width * height, reference);
        CollectionAssert.AreEqual(codes, Codes(baseline));
        CollectionAssert.AreEqual(counts, baseline.ValidCounts.ToArray());
        CollectionAssert.AreEqual(counts.Select(count => (byte)(count == 0 ? 1 : 0)).ToArray(), baseline.NoCoverageMask.ToArray());
        Assert.IsGreaterThanOrEqualTo(1, baseline.NoCoverageCount);
        Assert.IsGreaterThanOrEqualTo(1, baseline.ClippedLowCount);
    }

    [TestMethod]
    public void Compute_AppliesTheRulesPerCfaPhotositeWithoutBorrowingFromNeighbours()
    {
        // RGGB 4 × 2. Input A marks green (1, 0) defective and saturates blue (1, 1); input B saturates red (2, 0)
        // and has an invalid flat at blue (3, 1). The defective green keeps only B's sample rather than a repair
        // from the green at (3, 0).
        var a = Input(
            OneElectron,
            [1100, 600, 300, 700, 800, 4095, 500, 200],
            [100, 100, 100, 100, 100, 100, 100, 100],
            defects: [0, 1, 0, 0, 0, 0, 0, 0],
            saturationCode: 4095);
        var b = Input(
            OneElectron,
            [300, 900, 4095, 500, 600, 500, 300, 200],
            [100, 100, 100, 100, 100, 100, 100, 100],
            flat: [Unity, Unity, Unity, Unity, Unity, Unity, Unity, 0],
            saturationCode: 4095);

        var result = Linear16GainNormalizedMean.Compute(4, 2, CameraPixelFormat.BayerRggb16, [a, b], OneElectron);

        CollectionAssert.AreEqual(new ushort[] { 600, 800, 200, 500, 600, 400, 300, 100 }, Codes(result));
        CollectionAssert.AreEqual(new ushort[] { 2, 1, 1, 2, 2, 1, 2, 1 }, result.ValidCounts.ToArray());
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, result.PixelFormat);
        Assert.AreEqual(8, result.StrideBytes);
    }

    [TestMethod]
    [DataRow(79_000_000_000L, 50, 0)]
    [DataRow(79_000_000_000L, 51, 1)]
    [DataRow(79_000_000_000L, 149, 1)]
    [DataRow(79_000_000_000L, 150, 2)]
    [DataRow(79_000_000_000L, 249, 2)]
    [DataRow(79_000_000_000L, 250, 2)]
    [DataRow(79_000_000_000L, 251, 3)]
    [DataRow(790_000_000_000L, 15, 2)]
    public void Compute_QuantizesHighGainStacksToTheReferenceGainZeroStep(long coefficient, int signal, int expectedCode)
    {
        // ASI174 reference gain 0 is k = 7.9 e-/ADU, so one output code is 7.9 e-: 100 ADU at gain 400 (0.079 e-/ADU)
        // and 10 ADU at gain 200 (0.79 e-/ADU). Encoding adds up to ±3.95 e- (σ = 7.9/√12 ≈ 2.28 e-), more than the
        // read noise of a mean of three or more gain-400 frames (3.6/√N e-).
        var result = Mono(7_900_000_000_000, Input(coefficient, [(ushort)(64 + signal)], [64]));

        CollectionAssert.AreEqual(new[] { (ushort)expectedCode }, Codes(result));
    }

    [TestMethod]
    public void Compute_RejectsInvalidArguments()
    {
        var valid = Input(OneElectron, [100, 100], [0, 0]);

        Assert.ThrowsExactly<ArgumentNullException>(
            () => Linear16GainNormalizedMean.Compute(2, 1, CameraPixelFormat.Mono16, null!, OneElectron));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([], width: 2));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid, null!], width: 2));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid], width: 1));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid with { Bias = new ushort[1] }], width: 2));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid with { Dark = new int[1] }], width: 2));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid with { Flat = new int[3] }], width: 2));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid with { DefectMask = new byte[1] }], width: 2));
        Assert.ThrowsExactly<ArgumentException>(() => Compute([valid with { SaturationCode = 0 }], width: 2));
        Assert.ThrowsExactly<ArgumentException>(
            () => Linear16GainNormalizedMean.Compute(2, 1, CameraPixelFormat.Mono8, [valid], OneElectron));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Linear16GainNormalizedMean.Compute(0, 1, CameraPixelFormat.Mono16, [valid], OneElectron));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Linear16GainNormalizedMean.Compute(2, 0, CameraPixelFormat.Mono16, [valid], OneElectron));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Linear16GainNormalizedMean.Compute(int.MaxValue, 2, CameraPixelFormat.Mono16, [valid], OneElectron));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Linear16GainNormalizedMean.Compute(2, 1, CameraPixelFormat.Mono16, [valid], OneElectron, workingPhotosites: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(
            () => Linear16GainNormalizedMean.Compute(
                2,
                1,
                CameraPixelFormat.Mono16,
                [valid],
                OneElectron,
                cancellationToken: cancellation.Token));
    }

    private static GainNormalizedMeanInput Input(
        long electronsPerAdu,
        ushort[] light,
        ushort[] bias,
        int[]? dark = null,
        int[]? flat = null,
        byte[]? defects = null,
        ushort saturationCode = ushort.MaxValue)
        => new(
            light,
            bias,
            dark ?? new int[light.Length],
            flat ?? Enumerable.Repeat(Unity, light.Length).ToArray(),
            defects is null ? ReadOnlyMemory<byte>.Empty : defects,
            saturationCode,
            electronsPerAdu);

    private static GainNormalizedMeanResult Mono(long reference, params GainNormalizedMeanInput[] inputs)
        => Linear16GainNormalizedMean.Compute(inputs[0].Light.Length, 1, CameraPixelFormat.Mono16, inputs, reference);

    private static GainNormalizedMeanResult Compute(
        GainNormalizedMeanInput[] inputs,
        long reference = OneElectron,
        int width = 1)
        => Linear16GainNormalizedMean.Compute(width, 1, CameraPixelFormat.Mono16, inputs, reference);

    private static ushort[] Codes(GainNormalizedMeanResult result)
    {
        var bytes = result.PixelData.Span;
        var codes = new ushort[bytes.Length / 2];
        for (var index = 0; index < codes.Length; index++)
        {
            codes[index] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(index * 2)..]);
        }
        return codes;
    }

    private static GainNormalizedMeanInput[] RandomFixture(int photosites)
    {
        var random = new XorShift(0x1475D);
        var inputs = new GainNormalizedMeanInput[5];
        for (var inputIndex = 0; inputIndex < inputs.Length; inputIndex++)
        {
            var light = new ushort[photosites];
            var bias = new ushort[photosites];
            var dark = new int[photosites];
            var flat = new int[photosites];
            var defects = inputIndex % 2 == 1 ? new byte[photosites] : null;
            for (var photosite = 0; photosite < photosites; photosite++)
            {
                light[photosite] = (ushort)random.Next(0, 4096);
                bias[photosite] = (ushort)random.Next(60, 69);
                dark[photosite] = random.Next(-5, 41);
                flat[photosite] = random.Next(0, 100) switch
                {
                    0 => 0,
                    1 => -Unity,
                    2 => MaximumFlat + 1,
                    _ => random.Next(Unity / 2, Unity * 3 / 2),
                };
                if (defects is not null && random.Next(0, 30) == 0)
                {
                    defects[photosite] = 1;
                }
            }

            // Photosite 5 has no valid sample and photosite 6 only negative signal.
            flat[5] = 0;
            light[6] = 0;
            var coefficient = (random.Next(50_000, 8_000_000) * 1_000_000L) + random.Next(0, 1_000_000);
            inputs[inputIndex] = Input(coefficient, light, bias, dark, flat, defects, 4000);
        }
        return inputs;
    }

    private static (ushort[] Codes, ushort[] Counts) ExactOracle(
        IReadOnlyList<GainNormalizedMeanInput> inputs,
        int photosites,
        long reference)
    {
        var codes = new ushort[photosites];
        var counts = new ushort[photosites];
        for (var photosite = 0; photosite < photosites; photosite++)
        {
            var sum = BigInteger.Zero;
            var count = 0;
            foreach (var input in inputs)
            {
                var flat = input.Flat.Span[photosite];
                var light = input.Light.Span[photosite];
                if (!input.DefectMask.IsEmpty && input.DefectMask.Span[photosite] != 0 ||
                    light >= input.SaturationCode || flat < 1 || flat > MaximumFlat)
                {
                    continue;
                }
                var signal = light - input.Bias.Span[photosite] - input.Dark.Span[photosite];
                sum += RoundHalfToEven(new BigInteger(input.ElectronsPerAduScaled) * signal * Unity, flat);
                count++;
            }
            counts[photosite] = (ushort)count;
            if (count > 0)
            {
                codes[photosite] = (ushort)BigInteger.Clamp(
                    RoundHalfToEven(sum, new BigInteger(reference) * count),
                    0,
                    ushort.MaxValue);
            }
        }
        return (codes, counts);
    }

    private static BigInteger RoundHalfToEven(BigInteger numerator, BigInteger denominator)
    {
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twiceRemainder = BigInteger.Abs(remainder) * 2;
        if (twiceRemainder > denominator || twiceRemainder == denominator && !quotient.IsEven)
        {
            quotient += remainder.Sign;
        }
        return quotient;
    }

    private sealed class XorShift(ulong seed)
    {
        private ulong state = seed;

        public int Next(int minimum, int maximumExclusive)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            return minimum + (int)(state % (ulong)(maximumExclusive - minimum));
        }
    }
}
