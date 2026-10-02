using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarSourceMeasurerTests
{
    public TestContext TestContext { get; set; } = null!;

    private static readonly int[] ExpectedFluxOrder = [0, 1, 2];
    private static readonly int[] ExpectedFluxOrderColumns = [80, 135, 25];

    [TestMethod]
    public void IsolatedGaussian_ReturnsPixelEdgeCentroidFluxShapeAndIdentity()
    {
        const int width = 96, height = 80;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 47.23, 39.71, 1.1, 0.9, 1000);
        var original = pixels.ToArray();
        var result = Measure(pixels, width, height);

        Assert.AreEqual(StellarMeasurementStatus.Completed, result.Status);
        var star = result.Detections.Single();
        Assert.AreEqual(47.23, star.Pixel.X, 0.002);
        Assert.AreEqual(39.71, star.Pixel.Y, 0.002);
        Assert.AreEqual(1000 * 2 * Math.PI * 1.1 * 0.9, star.Flux, 2);
        Assert.AreEqual(1.1, star.PsfMajorSigma, 0.02);
        Assert.AreEqual(0.9, star.PsfMinorSigma, 0.02);
        Assert.AreEqual(100, star.Background, 1e-9);
        Assert.AreEqual(StellarSourceConditions.None, star.Conditions & ~StellarSourceConditions.CovarianceUnavailable);
        Assert.IsNull(star.TrailAngleDegrees);
        Assert.IsNull(star.SignalToNoise, "A noise-free field has no propagated flux noise.");
        Assert.IsEmpty(result.Exclusions);
        Assert.AreEqual(StellarSourceMeasurer.AlgorithmVersion, result.AlgorithmVersion);
        Assert.AreEqual(StellarSourceMeasurer.SettingsIdentity(new()), result.SettingsIdentitySha256);
        Assert.AreNotEqual(result.SettingsIdentitySha256, StellarSourceMeasurer.SettingsIdentity(new(PeakSigma: 6)));
        CollectionAssert.AreEqual(original, pixels);
    }

    [TestMethod]
    public void PropagatedCovariance_AgreesWithMonteCarloCentroidScatter()
    {
        const int width = 64, height = 64, trials = 400;
        var random = new Random(110301);
        var xs = new List<double>();
        var ys = new List<double>();
        var predicted = new List<StellarCentroidCovariance>();
        var trailed = 0;
        for (var trial = 0; trial < trials; trial++)
        {
            var pixels = Field(width, height);
            AddGaussian(pixels, width, 32.3, 31.8, 1, 1, 120);
            AddNoise(pixels, random, 5);
            var star = Measure(pixels, width, height).Detections.Single();
            xs.Add(star.Pixel.X);
            ys.Add(star.Pixel.Y);
            predicted.Add(star.CentroidCovariance!.Value);
            // Noise elongates some round stars past the trail floor; those take the aperture-moment centroid and covariance.
            trailed += star.Conditions.HasFlag(StellarSourceConditions.Trailed) ? 1 : 0;
            Assert.AreEqual(star.Conditions.HasFlag(StellarSourceConditions.Trailed)
                ? "propagated-noise-trail-moment-unvalidated"
                : "propagated-noise-unvalidated", star.CentroidCovarianceStatus);
            Assert.IsTrue(star.SignalToNoise > 10);
        }
        TestContext.WriteLine($"noise-trailed {trailed}/{trials}");
        Assert.IsTrue(trailed < trials / 4, $"{trailed}");
        var meanX = xs.Average();
        var meanY = ys.Average();
        var varianceX = xs.Sum(x => (x - meanX) * (x - meanX)) / (trials - 1);
        var varianceY = ys.Sum(y => (y - meanY) * (y - meanY)) / (trials - 1);
        Assert.AreEqual(32.3, meanX, 0.01);
        Assert.AreEqual(31.8, meanY, 0.01);
        // A 400-trial sample variance has about 7% relative standard error.
        Assert.AreEqual(1, varianceX / predicted.Average(c => c.XX), 0.25);
        Assert.AreEqual(1, varianceY / predicted.Average(c => c.YY), 0.25);
    }

    [TestMethod]
    public void LocalBackground_FollowsGradientWhereGlobalMedianWouldBias()
    {
        const int width = 256, height = 128;
        var pixels = Field(width, height);
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] += index % width * 0.5;
        }
        AddGaussian(pixels, width, 210.4, 64.6, 1, 1, 300);
        AddNoise(pixels, new Random(5), 3);
        var result = Measure(pixels, width, height);
        var star = result.Detections.Single();

        // The global median is near 164; the local estimate must follow the 205 background under the star.
        Assert.AreEqual(210.4, star.Pixel.X, 0.05);
        Assert.AreEqual(100 + 210.5 * 0.5, star.Background, 1.5);
        Assert.AreEqual(300 * 2 * Math.PI, star.Flux, 300 * 2 * Math.PI * 0.05);
        Assert.AreEqual(3, star.NoiseSigma, 0.3, "Adjacent differences cancel the gradient.");
        Assert.AreEqual(8, result.BackgroundTileCount);
        Assert.AreEqual(8, result.AvailableBackgroundTileCount);
    }

    [TestMethod]
    public void CorrelatedNoise_IsUnderstatedByDifferencesAndMeasuredByClippedSpread()
    {
        const int width = 256, height = 128;
        var white = Field(width, height);
        AddNoise(white, new Random(11), 3);
        // A [1/4, 1/2, 1/4] horizontal kernel models interpolation: sigma 3 * sqrt(3/8), lag-one correlation 2/3.
        var pixels = new double[white.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            var x = index % width;
            pixels[index] = 0.25 * white[x == 0 ? index : index - 1] + 0.5 * white[index] + 0.25 * white[x == width - 1 ? index : index + 1];
        }
        var spread = new StellarMeasurementOptions(NoiseEstimator: StellarNoiseEstimator.ClippedSpread);

        Assert.AreEqual(3 * Math.Sqrt(3.0 / 8) * Math.Sqrt(1.0 / 3), Measure(pixels, width, height).MedianNoiseSigma!.Value, 0.1);
        Assert.AreEqual(3 * Math.Sqrt(3.0 / 8), Measure(pixels, width, height, options: spread).MedianNoiseSigma!.Value, 0.1);
        Assert.AreNotEqual(StellarSourceMeasurer.SettingsIdentity(new()), StellarSourceMeasurer.SettingsIdentity(spread));
    }

    [TestMethod]
    public void UnavailableTiles_AreFilledFromNeighborsAndCounted()
    {
        const int width = 192, height = 64;
        var pixels = Field(width, height);
        var mask = Valid(pixels.Length);
        for (var index = 0; index < pixels.Length; index++)
        {
            mask[index] = index % width < 128;
        }
        var result = Measure(pixels, width, height, mask);

        Assert.AreEqual(3, result.BackgroundTileCount);
        Assert.AreEqual(2, result.AvailableBackgroundTileCount);
        Assert.AreEqual(1, result.FilledBackgroundTileCount);
        Assert.AreEqual(100, result.MedianBackground!.Value, 1e-12);
        Assert.IsNull(Measure(pixels, width, height, new bool[pixels.Length]).MedianBackground);
    }

    [TestMethod]
    public void HotPixel_IsExcludedWithReasonCode()
    {
        const int width = 64, height = 64;
        var pixels = Field(width, height);
        pixels[30 * width + 30] = 5000;
        var result = Measure(pixels, width, height);

        Assert.IsEmpty(result.Detections);
        var exclusion = result.Exclusions.Single();
        Assert.AreEqual(StellarExclusionReasons.HotPixelOrCosmicRay, exclusion.ReasonCode);
        Assert.AreEqual(30.5, exclusion.Peak.X);
        Assert.AreEqual(1, result.ExclusionCounts[StellarExclusionReasons.HotPixelOrCosmicRay]);
    }

    [TestMethod]
    public void SaturatedCore_IsFlaggedWithInflatedCovarianceAndExcessiveSaturationIsExcluded()
    {
        const int width = 64, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 32.37, 31.62, 1.2, 1.2, 4000);
        var saturated = new bool[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            if (pixels[index] >= 2500)
            {
                pixels[index] = 2500;
                saturated[index] = true;
            }
        }
        var star = Measure(pixels, width, height, null, saturated, new(MinimumMinorSigma: 0.2)).Detections.Single();

        Assert.IsTrue(star.Conditions.HasFlag(StellarSourceConditions.Saturated));
        Assert.IsTrue(star.SaturatedSampleCount > 0);
        Assert.AreEqual(32.37, star.Pixel.X, 0.03);
        Assert.AreEqual(31.62, star.Pixel.Y, 0.03);
        Assert.AreEqual("propagated-noise-saturation-inflated-unvalidated", star.CentroidCovarianceStatus);

        var excessive = Measure(pixels, width, height, null, saturated, new(MaximumSaturatedSamples: 1));
        Assert.IsEmpty(excessive.Detections);
        Assert.AreEqual(StellarExclusionReasons.SaturatedExcessive, excessive.Exclusions.Single().ReasonCode);
    }

    [TestMethod]
    public void SaturationDilation_CountsInterpolatedNeighbors()
    {
        const int width = 64, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 32.5, 32.5, 1.2, 1.2, 1000);
        var saturated = new bool[pixels.Length];
        saturated[32 * width + 32] = true;

        Assert.AreEqual(1, Measure(pixels, width, height, null, saturated).Detections.Single().SaturatedSampleCount);
        Assert.AreEqual(9, Measure(pixels, width, height, null, saturated, new(SaturationDilationPixels: 1))
            .Detections.Single().SaturatedSampleCount);
    }

    [TestMethod]
    public void ShortTrail_IsFlaggedWithLengthAngleAndLongTrailIsExcluded()
    {
        const int width = 128, height = 96;
        var pixels = Field(width, height);
        AddTrail(pixels, width, 40.5, 48.5, 8, 30, 1, 20000);
        AddTrail(pixels, width, 95.5, 48.5, 30, -60, 1, 60000);
        var result = Measure(pixels, width, height);

        var star = result.Detections.Single();
        Assert.IsTrue(star.Conditions.HasFlag(StellarSourceConditions.Trailed));
        Assert.AreEqual(8, star.TrailLengthPixels!.Value, 0.5);
        Assert.AreEqual(30, star.TrailAngleDegrees!.Value, 1);
        Assert.AreEqual(40.5, star.Pixel.X, 0.02);
        Assert.AreEqual(1, star.PsfMinorSigma, 0.1);
        Assert.AreEqual(StellarExclusionReasons.TrailTooLong, result.Exclusions.Single().ReasonCode);
    }

    [TestMethod]
    public void NoisyTrail_UsesTheApertureFluxCentroidWithItsOwnCovariance()
    {
        const int width = 96, height = 96, trials = 60;
        var random = new Random(110302);
        var errors = new List<double>();
        for (var trial = 0; trial < trials; trial++)
        {
            var pixels = Field(width, height);
            AddTrail(pixels, width, 48.3, 47.6, 12, 25, 1, 20000);
            AddNoise(pixels, random, 5);
            var star = Measure(pixels, width, height).Detections.Single();
            Assert.IsTrue(star.Conditions.HasFlag(StellarSourceConditions.Trailed));
            StringAssert.Contains(star.CentroidCovarianceStatus, "trail-moment", StringComparison.Ordinal);
            errors.Add(Math.Sqrt(Math.Pow(star.Pixel.X - 48.3, 2) + Math.Pow(star.Pixel.Y - 47.6, 2)));
        }
        Assert.IsTrue(errors.Average() < 0.1, $"mean {errors.Average():F3}");
        Assert.IsTrue(errors.Max() < 0.3, $"max {errors.Max():F3}");
    }

    [TestMethod]
    public void MeasurementWindowAboveTheDeclaredBound_IsAnExtendedRegion()
    {
        const int width = 96, height = 96;
        var pixels = Field(width, height);
        AddTrail(pixels, width, 48.5, 48.5, 12, 45, 1, 20000);

        Assert.HasCount(1, Measure(pixels, width, height).Detections);
        var bounded = Measure(pixels, width, height, options: new(MaximumWindowSamples: 64));
        Assert.IsEmpty(bounded.Detections);
        Assert.AreEqual(StellarExclusionReasons.ExtendedRegion, bounded.Exclusions.Single().ReasonCode);
    }

    [TestMethod]
    public void SeparatedSaddle_IsBlendedButNeighborComponentIsCrowded()
    {
        const int width = 128, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 30.5, 32.5, 1, 1, 1000);
        AddGaussian(pixels, width, 34.5, 32.5, 1, 1, 800);
        AddGaussian(pixels, width, 90.5, 32.5, 1, 1, 1000);
        AddGaussian(pixels, width, 98.5, 32.5, 1, 1, 600);
        var result = Measure(pixels, width, height);

        Assert.IsEmpty(result.Detections);
        Assert.AreEqual(1, result.ExclusionCounts[StellarExclusionReasons.Blended]);
        Assert.AreEqual(2, result.ExclusionCounts[StellarExclusionReasons.Crowded]);
        Assert.HasCount(2, Measure(pixels, width, height, options: new(CrowdingPeakRatio: 2)).Detections);
    }

    [TestMethod]
    public void NoiseArtifactNeighbor_DoesNotCrowdAnIsolatedStar()
    {
        const int width = 64, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 30.5, 32.5, 1, 1, 1000);
        pixels[32 * width + 35] = 400;
        var result = Measure(pixels, width, height);

        Assert.AreEqual(30.5, result.Detections.Single().Pixel.X, 0.01);
        Assert.AreEqual(StellarExclusionReasons.HotPixelOrCosmicRay, result.Exclusions.Single().ReasonCode);
    }

    [TestMethod]
    public void MaskEdgeBroadAndExtendedSources_HaveDistinctReasons()
    {
        const int width = 256, height = 96;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 30.5, 48.5, 1, 1, 1000);
        AddGaussian(pixels, width, 3.5, 48.5, 1, 1, 1000);
        AddGaussian(pixels, width, 100.5, 48.5, 3.5, 3.5, 1000);
        AddGaussian(pixels, width, 190.5, 48.5, 12, 12, 1000);
        var mask = Valid(pixels.Length);
        mask[50 * width + 33] = false;
        var result = Measure(pixels, width, height, mask);

        Assert.IsEmpty(result.Detections);
        CollectionAssert.AreEquivalent(
            new[] { StellarExclusionReasons.MaskedAperture, StellarExclusionReasons.ImageEdge, StellarExclusionReasons.TooBroad,
                StellarExclusionReasons.ExtendedRegion },
            result.Exclusions.Select(exclusion => exclusion.ReasonCode).ToArray());
    }

    [TestMethod]
    public void FaintSource_IsLowSignalToNoiseUnderDeclaredFloor()
    {
        const int width = 64, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 32.5, 32.5, 1, 1, 60);
        AddNoise(pixels, new Random(7), 5);

        Assert.HasCount(1, Measure(pixels, width, height).Detections);
        var strict = Measure(pixels, width, height, options: new(MinimumSignalToNoise: 1000));
        Assert.AreEqual(StellarExclusionReasons.LowSignalToNoise, strict.Exclusions.Single().ReasonCode);
    }

    [TestMethod]
    public void ShotNoiseGain_IncreasesPropagatedUncertainty()
    {
        const int width = 64, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 32.5, 32.5, 1, 1, 400);
        AddNoise(pixels, new Random(11), 4);
        var background = Measure(pixels, width, height).Detections.Single();
        var shot = Measure(pixels, width, height, options: new(ElectronsPerSampleUnit: 0.5)).Detections.Single();

        Assert.IsTrue(shot.CentroidCovariance!.Value.XX > background.CentroidCovariance!.Value.XX);
        Assert.IsTrue(shot.SignalToNoise < background.SignalToNoise);
    }

    [TestMethod]
    public void OrderingIsByFluxAndRepeatedRunsAreDeterministic()
    {
        const int width = 160, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 25.5, 32.5, 1, 1, 500);
        AddGaussian(pixels, width, 80.5, 32.5, 1, 1, 1000);
        AddGaussian(pixels, width, 135.5, 32.5, 1, 1, 750);
        AddNoise(pixels, new Random(3), 2);
        var first = Measure(pixels, width, height);
        var second = Measure(pixels, width, height);

        CollectionAssert.AreEqual(ExpectedFluxOrder, first.Detections.Select(star => star.Index).ToArray());
        CollectionAssert.AreEqual(ExpectedFluxOrderColumns, first.Detections.Select(star => (int)star.Pixel.X).ToArray());
        CollectionAssert.AreEqual(first.Detections.ToArray(), second.Detections.ToArray());
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<StellarDetection>)first.Detections).Clear());
    }

    [TestMethod]
    public void CandidateBudget_ReturnsExplicitStatusWithoutPartialResults()
    {
        const int width = 160, height = 64;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 25.5, 32.5, 1, 1, 500);
        AddGaussian(pixels, width, 80.5, 32.5, 1, 1, 1000);
        var result = Measure(pixels, width, height, options: new(MaximumCandidateCount: 1));

        Assert.AreEqual(StellarMeasurementStatus.CandidateBudgetExceeded, result.Status);
        Assert.AreEqual(2, result.CandidateCount);
        Assert.IsEmpty(result.Detections);
        Assert.IsEmpty(result.Exclusions);
    }

    [TestMethod]
    public void RejectsInvalidInputsOptionsAndCancellation()
    {
        var pixels = Field(32, 32);
        Assert.ThrowsExactly<ArgumentException>(() => StellarSourceMeasurer.Measure(pixels, Valid(pixels.Length), new bool[3], 32, 32));
        Assert.ThrowsExactly<ArgumentException>(() => StellarSourceMeasurer.Measure(pixels, Valid(pixels.Length), [], 31, 32));
        pixels[5] = double.NaN;
        Assert.ThrowsExactly<ArgumentException>(() => StellarSourceMeasurer.Measure(pixels, new bool[pixels.Length], [], 32, 32));
        pixels[5] = 100;
        StellarMeasurementOptions[] invalid =
        [
            new(BackgroundTileSizePixels: 8), new(MinimumBackgroundTileSamples: 64 * 64 + 1), new(PeakSigma: 2),
            new(SegmentationSigma: 0), new(MinimumPeakAboveBackground: double.NaN), new(WindowSigmaPixels: 0),
            new(MaximumMinorSigma: 0.1), new(MaximumSharpness: 1.5), new(MaximumTrailLengthPixels: 1),
            new(MaximumComponentSamples: 0), new(MaximumSaturatedSamples: -1), new(SaturationDilationPixels: 3),
            new(BlendSaddleFraction: 0), new(CrowdingPeakRatio: 0), new(MinimumSignalToNoise: -1),
            new(ElectronsPerSampleUnit: 0), new(MaximumPixelCount: 100), new(MaximumCandidateCount: 0),
            new(NoiseEstimator: (StellarNoiseEstimator)2), new(MaximumWindowSamples: 63), new(MaximumWindowSamples: (1 << 20) + 1)
        ];
        foreach (var options in invalid)
        {
            Assert.ThrowsExactly<ArgumentException>(() => StellarSourceMeasurer.Measure(pixels, Valid(pixels.Length), [], 32, 32, options));
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => StellarSourceMeasurer.Measure(pixels, Valid(pixels.Length), [], 32, 32,
            cancellationToken: cancellation.Token));
    }

    private static StellarMeasurementResult Measure(double[] pixels, int width, int height, bool[]? valid = null,
        bool[]? saturated = null, StellarMeasurementOptions? options = null) =>
        StellarSourceMeasurer.Measure(pixels, valid ?? Valid(pixels.Length), saturated ?? [], width, height, options);

    private static double[] Field(int width, int height) => Enumerable.Repeat(100.0, width * height).ToArray();
    private static bool[] Valid(int count) => Enumerable.Repeat(true, count).ToArray();

    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded, reproducible synthetic noise.")]
    private static void AddNoise(double[] pixels, Random random, double sigma)
    {
        for (var index = 0; index < pixels.Length; index++)
        {
            var u = 1 - random.NextDouble();
            var v = random.NextDouble();
            pixels[index] += sigma * Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
        }
    }

    private static void AddGaussian(double[] pixels, int width, double centerX, double centerY, double sigmaX, double sigmaY, double peak)
    {
        for (var index = 0; index < pixels.Length; index++)
        {
            var dx = (index % width + 0.5 - centerX) / sigmaX;
            var dy = (index / width + 0.5 - centerY) / sigmaY;
            pixels[index] += peak * Math.Exp(-0.5 * (dx * dx + dy * dy));
        }
    }

    private static void AddTrail(double[] pixels, int width, double centerX, double centerY, double length, double angleDegrees,
        double sigma, double flux)
    {
        const int steps = 400;
        var angle = angleDegrees * Math.PI / 180;
        for (var step = 0; step < steps; step++)
        {
            var offset = ((step + 0.5) / steps - 0.5) * length;
            AddGaussian(pixels, width, centerX + offset * Math.Cos(angle), centerY + offset * Math.Sin(angle), sigma, sigma,
                flux / steps / (2 * Math.PI * sigma * sigma));
        }
    }
}
