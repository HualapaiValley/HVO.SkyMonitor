using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarDetectorTests
{
    [TestMethod]
    public void RectangularGaussian_ReturnsPixelEdgeCentroidAndMeasuredMoments()
    {
        const int width = 77, height = 43;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 54.23, 19.71, 1.1, 0.9, 1000);
        var original = pixels.ToArray();
        var result = StellarDetector.Detect(pixels, Valid(pixels.Length), width, height);

        Assert.HasCount(1, result.Detections);
        var star = result.Detections[0];
        Assert.AreEqual(0, star.Index);
        Assert.AreEqual(54.23, star.Pixel.X, 0.001);
        Assert.AreEqual(19.71, star.Pixel.Y, 0.001);
        Assert.AreEqual(1.1, star.PsfMajorSigma, 0.002);
        Assert.AreEqual(0.9, star.PsfMinorSigma, 0.002);
        Assert.AreEqual(1000 * 2 * Math.PI * 1.1 * 0.9, star.Flux, 1);
        Assert.AreEqual(pixels[19 * width + 54], star.Peak);
        Assert.AreEqual(100, star.Background, 1e-12);
        Assert.AreEqual(0, star.NoiseSigma, 1e-12);
        Assert.IsNull(star.CentroidCovariance);
        Assert.AreEqual(StellarDetector.AlgorithmVersion, result.AlgorithmVersion);
        CollectionAssert.AreEqual(original, pixels);
    }

    [TestMethod]
    public void IndependentDiscreteKernel_HasExactCentroidFluxAndMomentWidths()
    {
        const int width = 35, height = 29;
        var pixels = Field(width, height);
        pixels[14 * width + 17] += 400;
        pixels[14 * width + 16] += 100;
        pixels[14 * width + 18] += 100;
        pixels[13 * width + 17] += 100;
        pixels[15 * width + 17] += 100;
        var star = StellarDetector.Detect(pixels, Valid(pixels.Length), width, height).Detections.Single();

        Assert.AreEqual(17.5, star.Pixel.X, 1e-12);
        Assert.AreEqual(14.5, star.Pixel.Y, 1e-12);
        Assert.AreEqual(800, star.Flux, 1e-12);
        Assert.AreEqual(0.5, star.PsfMajorSigma, 1e-12);
        Assert.AreEqual(0.5, star.PsfMinorSigma, 1e-12);
    }

    [TestMethod]
    public void ConstantEmptyMaskAndAffineFields_ProduceNoDetections()
    {
        const int width = 47, height = 29;
        var pixels = Field(width, height);
        Assert.IsEmpty(StellarDetector.Detect(pixels, Valid(pixels.Length), width, height).Detections);
        Assert.IsEmpty(StellarDetector.Detect(pixels, new bool[pixels.Length], width, height).Detections);
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] += index % width * 20 + index / width * 3;
        }
        Assert.IsEmpty(StellarDetector.Detect(pixels, Valid(pixels.Length), width, height).Detections);
    }

    [TestMethod]
    public void HotPixelAndPlateau_AreRejected()
    {
        const int width = 55, height = 31;
        var pixels = Field(width, height);
        pixels[15 * width + 12] = 10_000;
        pixels[15 * width + 37] = 2000;
        pixels[15 * width + 38] = 2000;
        var result = StellarDetector.Detect(pixels, Valid(pixels.Length), width, height);

        Assert.IsEmpty(result.Detections);
        Assert.AreEqual(1, result.CandidateCount, "The strict-peak hot pixel is measured and rejected; the plateau is not a peak.");
    }

    [TestMethod]
    public void NearbyBlend_RejectsBothRatherThanChoosingBrighterSource()
    {
        const int width = 55, height = 35;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 22.5, 17.5, 0.7, 0.7, 1000);
        AddGaussian(pixels, width, 30.5, 17.5, 0.7, 0.7, 700);

        Assert.IsEmpty(StellarDetector.Detect(pixels, Valid(pixels.Length), width, height).Detections);
        Assert.HasCount(2, StellarDetector.Detect(pixels, Valid(pixels.Length), width, height,
            new(IsolationRadiusPixels: 5)).Detections);
    }

    [TestMethod]
    public void CompleteApertureMaskAndBorderExclusion_AreEnforced()
    {
        const int width = 53, height = 33;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 25.5, 16.5, 1, 1, 1000);
        AddGaussian(pixels, width, 3.5, 16.5, 1, 1, 1000);
        var mask = Valid(pixels.Length);
        mask[20 * width + 29] = false;

        Assert.IsEmpty(StellarDetector.Detect(pixels, mask, width, height).Detections);
    }

    [TestMethod]
    public void ExplicitColorWidthOption_AcceptsWiderPsfAndRejectsLongTrail()
    {
        const int width = 81, height = 43;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 22.5, 21.5, 1.8, 1.4, 1000);
        AddGaussian(pixels, width, 57.5, 21.5, 3.0, 0.7, 1000);

        Assert.IsEmpty(StellarDetector.Detect(pixels, Valid(pixels.Length), width, height).Detections);
        var color = StellarDetector.Detect(pixels, Valid(pixels.Length), width, height, new(MaximumMajorSigma: 2.1));
        Assert.HasCount(1, color.Detections);
        Assert.AreEqual(22.5, color.Detections[0].Pixel.X, 1e-8);
    }

    [TestMethod]
    public void PeakThresholdAndNoiseMultiplier_AreConfigurable()
    {
        const int width = 39, height = 31;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 19.5, 15.5, 1, 1, 30);
        Assert.IsEmpty(StellarDetector.Detect(pixels, Valid(pixels.Length), width, height).Detections);
        Assert.HasCount(1, StellarDetector.Detect(pixels, Valid(pixels.Length), width, height,
            new(MinimumPeakAboveBackground: 20)).Detections);

        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] = index % 2 == 0 ? 90 : 110;
        }
        AddGaussian(pixels, width, 19.5, 15.5, 1, 1, 200);
        var ordinary = StellarDetector.Detect(pixels, Valid(pixels.Length), width, height);
        Assert.IsTrue(ordinary.NoiseSigma is > 10 and < 30);
        Assert.HasCount(1, ordinary.Detections);
        Assert.IsEmpty(StellarDetector.Detect(pixels, Valid(pixels.Length), width, height,
            new(NoiseThresholdMultiplier: 20)).Detections);
    }

    [TestMethod]
    public void MedianAndMad_MatchIndependentThreeLevelDistribution()
    {
        var pixels = Enumerable.Range(0, 169).Select(index => 90.0 + index % 3 * 10).ToArray();
        var result = StellarDetector.Detect(pixels, Valid(pixels.Length), 13, 13);

        Assert.AreEqual(100, result.Background, 1e-12);
        Assert.AreEqual(14.826, result.NoiseSigma, 1e-12);
        Assert.IsEmpty(result.Detections);
    }

    [TestMethod]
    public void FluxOrderingAndIndices_AreStableAndCollectionIsReadOnly()
    {
        const int width = 85, height = 31;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 15.5, 15.5, 1, 1, 500);
        AddGaussian(pixels, width, 42.5, 15.5, 1, 1, 1000);
        AddGaussian(pixels, width, 69.5, 15.5, 1, 1, 750);
        var result = StellarDetector.Detect(pixels, Valid(pixels.Length), width, height);

        int[] expectedIndices = [0, 1, 2];
        double[] expectedPositions = [42.5, 69.5, 15.5];
        CollectionAssert.AreEqual(expectedIndices, result.Detections.Select(star => star.Index).ToArray());
        CollectionAssert.AreEqual(expectedPositions, result.Detections.Select(star => star.Pixel.X).ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StellarDetection>)result.Detections).Clear());
    }

    [TestMethod]
    public void CandidateBudget_FailsExplicitlyWithoutPartialDetections()
    {
        const int width = 55, height = 31;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 15.5, 15.5, 1, 1, 1000);
        AddGaussian(pixels, width, 39.5, 15.5, 1, 1, 1000);

        Assert.ThrowsExactly<InvalidOperationException>(() => StellarDetector.Detect(pixels, Valid(pixels.Length),
            width, height, new(MaximumCandidateCount: 1)));
    }

    [TestMethod]
    public void RejectsNonfiniteSamplesIncludingMaskedPixelsAndOverflowingDynamicRange()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            var pixels = Field(19, 17);
            pixels[0] = value;
            Assert.ThrowsExactly<ArgumentException>(() => StellarDetector.Detect(pixels, new bool[pixels.Length], 19, 17));
        }
        var extremes = Enumerable.Repeat(double.MaxValue, 19 * 17).ToArray();
        extremes[0] = -double.MaxValue;
        Assert.ThrowsExactly<ArgumentException>(() => StellarDetector.Detect(extremes, Valid(extremes.Length), 19, 17));
    }

    [TestMethod]
    public void RejectsInvalidLayoutsAndResourceLimits()
    {
        Assert.ThrowsExactly<ArgumentException>(() => StellarDetector.Detect([], [], int.MaxValue, int.MaxValue));
        Assert.ThrowsExactly<ArgumentException>(() => StellarDetector.Detect(new double[156], new bool[156], 12, 13));
        Assert.ThrowsExactly<ArgumentException>(() => StellarDetector.Detect(new double[169], new bool[168], 13, 13));
        Assert.ThrowsExactly<ArgumentException>(() => StellarDetector.Detect(new double[195], new bool[195], 13, 15, new(MaximumPixelCount: 169)));
        StellarDetectionOptions[] invalid = [
            new(MinimumPeakAboveBackground: 0), new(MinimumPeakAboveBackground: double.NaN),
            new(NoiseThresholdMultiplier: -1), new(NoiseThresholdMultiplier: double.PositiveInfinity),
            new(MinimumMinorSigma: 0), new(MaximumMajorSigma: 0.1), new(MaximumMajorSigma: double.NaN),
            new(MaximumVarianceRatio: 0.9), new(IsolationRadiusPixels: -1), new(IsolationRadiusPixels: double.PositiveInfinity),
            new(MaximumPixelCount: StellarDetector.MaximumSupportedPixels + 1),
            new(MaximumCandidateCount: 0), new(MaximumCandidateCount: StellarDetector.MaximumSupportedCandidates + 1)
        ];
        foreach (var options in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => StellarDetector.Detect(new double[169], new bool[169], 13, 13, options));
        }
    }

    [TestMethod]
    public void Cancellation_IsHonoredBeforeDetection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => StellarDetector.Detect(new double[169], new bool[169], 13, 13,
            cancellationToken: cancellation.Token));
    }

    private static double[] Field(int width, int height) => Enumerable.Repeat(100.0, width * height).ToArray();
    private static bool[] Valid(int count) => Enumerable.Repeat(true, count).ToArray();

    private static void AddGaussian(double[] pixels, int width, double centerX, double centerY, double sigmaX, double sigmaY, double peak)
    {
        for (var index = 0; index < pixels.Length; index++)
        {
            var dx = (index % width + 0.5 - centerX) / sigmaX;
            var dy = (index / width + 0.5 - centerY) / sigmaY;
            pixels[index] += peak * Math.Exp(-0.5 * (dx * dx + dy * dy));
        }
    }
}
