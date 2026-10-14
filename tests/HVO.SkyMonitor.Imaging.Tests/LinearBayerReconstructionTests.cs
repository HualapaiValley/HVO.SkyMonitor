using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class LinearBayerReconstructionTests
{
    [TestMethod]
    [DataRow(BayerPattern.Rggb)]
    [DataRow(BayerPattern.Bggr)]
    [DataRow(BayerPattern.Grbg)]
    [DataRow(BayerPattern.Gbrg)]
    public void ConstantChannels_ArePreservedWithoutStretchOrQuantization(BayerPattern pattern)
    {
        const int width = 9, height = 7;
        double[] values = [-12.25, 123.75, 100_000.5];
        var raw = Mosaic(width, height, pattern, (channel, _, _) => values[channel]);
        var original = raw.ToArray();
        var result = LinearBayerReconstruction.Reconstruct(raw, Valid(raw.Length), width, height, pattern);
        var luminance = result.ToLuminance();

        Assert.AreEqual((width, height), (result.Width, result.Height));
        Assert.AreEqual(LinearBayerReconstruction.AlgorithmVersion, result.AlgorithmVersion);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var interior = x > 0 && x < width - 1 && y > 0 && y < height - 1;
                Assert.AreEqual(interior, result.ValidMask.Span[index]);
                Assert.AreEqual(interior, luminance.ValidMask.Span[index]);
                Assert.AreEqual(interior ? values[0] : 0, result.Red.Span[index]);
                Assert.AreEqual(interior ? values[1] : 0, result.Green.Span[index]);
                Assert.AreEqual(interior ? values[2] : 0, result.Blue.Span[index]);
                Assert.AreEqual(interior ? 0.2126 * values[0] + 0.7152 * values[1] + 0.0722 * values[2] : 0,
                    luminance.Pixels.Span[index], 1e-10);
            }
        }
        CollectionAssert.AreEqual(original, raw);
    }

    [TestMethod]
    [DataRow(BayerPattern.Rggb)]
    [DataRow(BayerPattern.Bggr)]
    [DataRow(BayerPattern.Grbg)]
    [DataRow(BayerPattern.Gbrg)]
    public void IndependentAffineChannels_AreReconstructedAtEveryInteriorPhase(BayerPattern pattern)
    {
        const int width = 12, height = 9;
        static double Signal(int channel, int x, int y) => (channel + 1) * 31.5 + (channel - 1) * x * 2.25 + (channel + 2) * y * 0.75;
        var raw = Mosaic(width, height, pattern, Signal);
        var result = LinearBayerReconstruction.Reconstruct(raw, Valid(raw.Length), width, height, pattern);
        ReadOnlyMemory<double>[] planes = [result.Red, result.Green, result.Blue];

        for (var channel = 0; channel < 3; channel++)
        {
            for (var y = 1; y < height - 1; y++)
            {
                for (var x = 1; x < width - 1; x++)
                {
                    Assert.AreEqual(Signal(channel, x, y), planes[channel].Span[y * width + x], 1e-12);
                }
            }
        }
    }

    [TestMethod]
    public void RedDelta_HasIndependentBilinearFootprintAndNoOtherChannelSignal()
    {
        const int width = 9, height = 7;
        var raw = new double[width * height];
        raw[4 * width + 4] = 16;
        var result = LinearBayerReconstruction.Reconstruct(raw, Valid(raw.Length), width, height);

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var dx = Math.Abs(x - 4);
                var dy = Math.Abs(y - 4);
                var expected = dx > 1 || dy > 1 ? 0 : 16 / Math.Pow(2, dx + dy);
                Assert.AreEqual(expected, result.Red.Span[y * width + x], 1e-12);
                Assert.AreEqual(0, result.Green.Span[y * width + x]);
                Assert.AreEqual(0, result.Blue.Span[y * width + x]);
            }
        }
    }

    [TestMethod]
    public void ReconstructedColor_DetectsSubpixelCentroidInFullResolutionCoordinates()
    {
        const int width = 63, height = 41;
        const double centerX = 31.23, centerY = 20.71;
        double[] peaks = [1800, 1000, 700];
        var raw = Mosaic(width, height, BayerPattern.Rggb, (channel, x, y) =>
        {
            var dx = (x + 0.5 - centerX) / 1.2;
            var dy = (y + 0.5 - centerY) / 1.2;
            return 100 + peaks[channel] * Math.Exp(-0.5 * (dx * dx + dy * dy));
        });
        var luminance = LinearBayerReconstruction.Reconstruct(raw, Valid(raw.Length), width, height).ToLuminance();
        var result = StellarDetector.Detect(luminance.Pixels.Span, luminance.ValidMask.Span,
            width, height, new(MaximumMajorSigma: 2.1));

        Assert.HasCount(1, result.Detections);
        Assert.AreEqual(centerX, result.Detections[0].Pixel.X, 0.015);
        Assert.AreEqual(centerY, result.Detections[0].Pixel.Y, 0.015);
        Assert.IsNull(result.Detections[0].CentroidCovariance);
    }

    [TestMethod]
    public void InvalidInputSample_ExcludesCompleteNeighborhoodAndDoesNotLeakSignal()
    {
        const int width = 9, height = 7;
        var raw = Enumerable.Repeat(100.0, width * height).ToArray();
        var valid = Valid(raw.Length);
        valid[3 * width + 4] = false;
        raw[3 * width + 4] = 1e20;
        var result = LinearBayerReconstruction.Reconstruct(raw, valid, width, height);

        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < width - 1; x++)
            {
                var expectedValid = Math.Abs(x - 4) > 1 || Math.Abs(y - 3) > 1;
                var index = y * width + x;
                Assert.AreEqual(expectedValid, result.ValidMask.Span[index]);
                Assert.AreEqual(expectedValid ? 100 : 0, result.Red.Span[index]);
                Assert.AreEqual(expectedValid ? 100 : 0, result.Green.Span[index]);
                Assert.AreEqual(expectedValid ? 100 : 0, result.Blue.Span[index]);
            }
        }
        valid[3 * width + 4] = true;
        Assert.IsFalse(result.ValidMask.Span[3 * width + 4], "The result must not borrow the caller's mask.");
    }

    [TestMethod]
    public void FiniteExtremeConstant_RemainsFiniteInEveryChannelAndLuminance()
    {
        var raw = Enumerable.Repeat(double.MaxValue, 25).ToArray();
        var result = LinearBayerReconstruction.Reconstruct(raw, Valid(raw.Length), 5, 5);

        Assert.AreEqual(double.MaxValue, result.Red.Span[12]);
        Assert.AreEqual(double.MaxValue, result.Green.Span[12]);
        Assert.AreEqual(double.MaxValue, result.Blue.Span[12]);
        Assert.IsTrue(double.IsFinite(result.ToLuminance().Pixels.Span[12]));
    }

    [TestMethod]
    public void RejectsNonfiniteSamplesEvenWhenMasked()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var raw = new double[25];
            raw[0] = value;
            Assert.ThrowsExactly<ArgumentException>(() => LinearBayerReconstruction.Reconstruct(raw, new bool[25], 5, 5));
        }
    }

    [TestMethod]
    public void RejectsInvalidLayoutsPatternAndOversizeWithoutAllocatingOutput()
    {
        Assert.ThrowsExactly<ArgumentException>(() => LinearBayerReconstruction.Reconstruct(new double[6], new bool[6], 2, 3));
        Assert.ThrowsExactly<ArgumentException>(() => LinearBayerReconstruction.Reconstruct(new double[9], new bool[8], 3, 3));
        Assert.ThrowsExactly<ArgumentException>(() => LinearBayerReconstruction.Reconstruct(new double[9], new bool[9], 4, 3));
        Assert.ThrowsExactly<ArgumentException>(() => LinearBayerReconstruction.Reconstruct([], [], int.MaxValue, int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => LinearBayerReconstruction.Reconstruct(new double[9], new bool[9], 3, 3, (BayerPattern)99));
    }

    [TestMethod]
    public void Cancellation_IsHonoredByReconstructionAndLuminance()
    {
        var raw = new double[25];
        var mask = Valid(raw.Length);
        var reconstructed = LinearBayerReconstruction.Reconstruct(raw, mask, 5, 5);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() => LinearBayerReconstruction.Reconstruct(
            raw, mask, 5, 5, cancellationToken: cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => reconstructed.ToLuminance(cancellation.Token));
    }

    private static bool[] Valid(int count) => Enumerable.Repeat(true, count).ToArray();

    private static double[] Mosaic(int width, int height, BayerPattern pattern, Func<int, int, int, double> signal)
    {
        // Explicit independent 2-by-2 channel tables, not the reconstruction's phase calculation.
        int[] channels = pattern switch
        {
            BayerPattern.Rggb => [0, 1, 1, 2],
            BayerPattern.Bggr => [2, 1, 1, 0],
            BayerPattern.Grbg => [1, 0, 2, 1],
            BayerPattern.Gbrg => [1, 2, 0, 1],
            _ => throw new ArgumentOutOfRangeException(nameof(pattern))
        };
        var raw = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                raw[y * width + x] = signal(channels[(y & 1) * 2 + (x & 1)], x, y);
            }
        }
        return raw;
    }
}
