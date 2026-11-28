using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
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

    [TestMethod]
    public void LegacyV2ReplayOmitsOccupancyAndPreservesDistinctV2AlgorithmIdentity()
    {
        const int size = 128;
        var pixels = Field(size, size);
        AddGaussian(pixels, size, 64.5, 64.5, 1.2, 1.2, 2000);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = double.Hypot(x + 0.5 - 64.5, y + 0.5 - 64.5);
                if (r is >= 14.0 and <= 30.0)
                    pixels[y * size + x] += 1800.0;
            }
        }
        var valid = Valid(pixels.Length);
        var current = StellarSourceMeasurer.Measure(pixels, valid, [], size, size);
        var legacy = StellarSourceMeasurer.MeasureLegacyV2(pixels, valid, [], size, size);

        Assert.AreEqual(StellarSourceMeasurer.AlgorithmVersion, current.AlgorithmVersion);
        Assert.AreEqual("linear-stellar-local-v3", current.AlgorithmVersion);
        Assert.AreEqual(StellarSourceMeasurer.V2AlgorithmVersion, legacy.AlgorithmVersion);
        Assert.AreEqual("linear-stellar-local-v2", legacy.AlgorithmVersion);
        Assert.AreNotEqual(current.SettingsIdentitySha256, legacy.SettingsIdentitySha256);
        Assert.AreEqual(StellarSourceMeasurer.SettingsIdentity(new(), StellarSourceMeasurer.V2AlgorithmVersion),
            legacy.SettingsIdentitySha256);
        CollectionAssert.AreEqual(current.Detections.ToArray(), legacy.Detections.ToArray());
        var currentRing = current.Exclusions.Single(e => e.ReasonCode == StellarExclusionReasons.ExtendedRegion);
        var legacyRing = legacy.Exclusions.Single(e => e.ReasonCode == StellarExclusionReasons.ExtendedRegion);
        Assert.IsNotNull(currentRing.Occupancy);
        Assert.IsNotNull(currentRing.Occupancy.Subgrid, "61x61 ring coarse 2x2 cells should refine to a 1x1 subgrid.");
        Assert.IsTrue(currentRing.Occupancy.IsValid(currentRing.Footprint!.Value, currentRing.Peak, currentRing.SampleCount));
        Assert.IsNull(legacyRing.Occupancy);
        Assert.AreEqual(currentRing.Footprint, legacyRing.Footprint);
        Assert.AreEqual(currentRing.Peak, legacyRing.Peak);
        Assert.AreEqual(currentRing.SampleCount, legacyRing.SampleCount);
    }

    [TestMethod]
    public void TwoLevelOccupancyRefinesMegapixelDiagonalStreakAndEnforcesIntegrity()
    {
        var fp = new StellarFootprintBounds(100, 100, 1899, 1899);
        // 1,800-px diagonal streak y == x +/- 2 inside a 1800x1800 box; coarse cells are 40x40, subgrid refines to <= 4x4.
        var occupancy = StellarFootprintOccupancy.Create(fp, static (x, y) => Math.Abs(x - y) <= 2);
        Assert.IsGreaterThanOrEqualTo(30, occupancy.CellWidth);
        Assert.IsNotNull(occupancy.Subgrid);
        Assert.IsLessThanOrEqualTo(4, occupancy.Subgrid.CellWidth);
        Assert.IsLessThanOrEqualTo(4, occupancy.Subgrid.CellHeight);

        var onStreak = new PixelPoint(1000.5, 1000.5);
        var nearby = new PixelPoint(1000.5 + 18.0 * Math.Sqrt(0.5), 1000.5 - 18.0 * Math.Sqrt(0.5));
        Assert.AreEqual(0.0, occupancy.DistanceSquaredToOccupied(fp, onStreak, 1.0));
        Assert.IsTrue(double.IsPositiveInfinity(occupancy.DistanceSquaredToOccupied(fp, nearby, 10.0)),
            "Subgrid must keep a point 18 px off the diagonal streak well outside a 10 px search radius.");
        Assert.IsTrue(occupancy.IsValid(fp, onStreak, sampleCount: 9000));

        // Integrity rejections (M7):
        var smallFp = new StellarFootprintBounds(10, 10, 13, 13);
        var twoCells = StellarFootprintOccupancy.Create(smallFp, static (x, y) => (x == 10 && y == 10) || (x == 13 && y == 13));
        Assert.IsTrue(twoCells.IsValid(smallFp, new PixelPoint(10.5, 10.5), sampleCount: 2));
        Assert.IsFalse(twoCells.IsValid(smallFp, new PixelPoint(10.5, 10.5), sampleCount: 1),
            "1 segmented sample cannot occupy 2 distinct cells.");
        Assert.IsFalse(twoCells.IsValid(smallFp, new PixelPoint(11.5, 11.5), sampleCount: 2),
            "Recorded peak outside the occupied mask must be rejected.");
        Assert.IsFalse(twoCells.IsValid(smallFp, new PixelPoint(10.5, 10.5), sampleCount: 3),
            "3 samples cannot fit inside 2 occupied 1x1 cells.");

        // Subgrid integrity rejections:
        Assert.IsFalse(occupancy.IsValid(fp, nearby, sampleCount: 9000),
            "Recorded peak in an unoccupied sub-cell (even inside an occupied coarse cell) must be rejected.");
        Assert.IsFalse(occupancy.IsValid(fp, onStreak, sampleCount: 1),
            "1 segmented sample cannot occupy multiple subgrid cells.");
        // Tamper a subgrid coarse cell to all zeros while keeping coarse bit set:
        var zeroSub = occupancy with
        {
            Subgrid = occupancy.Subgrid with
            {
                BitsBase64 = Convert.ToBase64String(new byte[Convert.FromBase64String(occupancy.Subgrid.BitsBase64).Length])
            }
        };
        Assert.IsFalse(zeroSub.IsValid(fp), "An occupied coarse cell with zero occupied sub-cells must be rejected.");

        // D8-M3 + D8-H1 + D10-M2: standalone 1-arg/3-arg IsValid accepts any physically valid budgeted coarse mask
        // (including coarse-only masks where whole-product subgrid budget was exhausted), while 4-arg and 5-arg
        // product-budget validation deterministically enforces exact Subgrid presence/absence against the budget.
        var strippedSub = occupancy with { Subgrid = null };
        Assert.IsTrue(strippedSub.IsValid(fp),
            "Standalone 1-arg validation must accept a physically valid coarse-only budgeted mask.");
        Assert.IsTrue(strippedSub.IsValid(fp, onStreak, sampleCount: 9000),
            "Standalone 3-arg validation must accept a physically valid coarse-only budgeted mask.");
        Assert.IsTrue(occupancy.IsValid(fp, onStreak, sampleCount: 9000, StellarFootprintOccupancy.MaximumSubgridBits));
        Assert.IsFalse(strippedSub.IsValid(fp, onStreak, sampleCount: 9000, StellarFootprintOccupancy.MaximumSubgridBits),
            "Product-budget validation must reject stripping Subgrid when refinement fits in budget.");
        Assert.IsFalse(strippedSub.IsValid(fp, onStreak, sampleCount: 9000,
            StellarFootprintOccupancy.MaximumCells, StellarFootprintOccupancy.MaximumSubgridBits),
            "5-arg product-budget validation must reject stripping Subgrid when refinement fits in budget.");
        Assert.IsTrue(strippedSub.IsValid(fp, onStreak, sampleCount: 9000, subgridBitBudget: 1),
            "When whole-product subgrid budget is legitimately exhausted (ChooseSubgrid == null), Subgrid == null must validate.");
        Assert.IsFalse(occupancy.IsValid(fp, onStreak, sampleCount: 9000, subgridBitBudget: 1),
            "When whole-product subgrid budget is exhausted, an unexpected Subgrid must be rejected.");
    }

    [TestMethod]
    public void ProductOccupancyBudgetsAccountForWorstCaseJsonEscapingAndEnforceDeterministicCoarsening()
    {
        // 1. WorstCaseSerializedBase64Utf8ByteCount must strictly bound actual System.Text.Json default UTF-8 output
        //    on the worst-case repeating [0xFB, 0xEF, 0xBE] pattern (which encodes to 100% '+' -> 6-byte \u002B).
        byte[] allPlusPattern = [0xFB, 0xEF, 0xBE];
        for (var bitCount = 1; bitCount <= 320; bitCount++)
        {
            var rawLength = (bitCount + 7) / 8;
            var raw = new byte[rawLength];
            for (var i = 0; i < rawLength; i++)
                raw[i] = allPlusPattern[i % 3];
            var serializedLength = JsonSerializer.SerializeToUtf8Bytes(Convert.ToBase64String(raw)).Length - 2; // exclude surrounding quotes
            var bound = StellarFootprintOccupancy.WorstCaseSerializedBase64Utf8ByteCount(bitCount);
            Assert.IsGreaterThanOrEqualTo(serializedLength, bound,
                $"Worst-case Base64 UTF-8 bound must cover actual \\u002B-escaped length for {bitCount} bits.");
        }

        // 1b. Worst-case per-record serialized UTF-8 byte constants must strictly bound canonical System.Text.Json output.
        var webJson = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
        };
        var worstDetection = new StellarDetection(
            StellarDetector.MaximumSupportedCandidates - 1,
            new PixelPoint(16777215.123456789, 16777215.123456789),
            1.7976931348623157E+308, 65535.123456789012, 1.2345678901234567E+100, 1.2345678901234567E+100,
            65535.123456789012, 1.2345678901234567E-100)
        {
            CentroidCovariance = new StellarCentroidCovariance(1.2345678901234567E+100, -1.2345678901234567E+100, 1.2345678901234567E+100),
            CentroidCovarianceStatus = "propagated-noise-trail-moment-saturation-inflated-unvalidated",
            SignalToNoise = 1.2345678901234567E+100,
            Conditions = StellarSourceConditions.Saturated | StellarSourceConditions.Trailed | StellarSourceConditions.CovarianceUnavailable,
            SaturatedSampleCount = int.MaxValue,
            TrailLengthPixels = 1.2345678901234567E+100,
            TrailAngleDegrees = -89.999999999999986
        };
        Assert.IsLessThanOrEqualTo(StellarFootprintOccupancy.WorstCaseBytesPerDetection,
            JsonSerializer.SerializeToUtf8Bytes(worstDetection, webJson).Length + 1);
        var worstExclusion = new StellarMeasurementExclusion(
            new PixelPoint(16777215.5, 16777215.5), 65535.123456789012, StellarDetector.MaximumSupportedPixels,
            StellarExclusionReasons.BackgroundUnavailable)
        {
            Footprint = new StellarFootprintBounds(16777215, 16777215, 16777215, 16777215)
        };
        Assert.IsLessThanOrEqualTo(StellarFootprintOccupancy.WorstCaseBytesPerExclusionFixed,
            JsonSerializer.SerializeToUtf8Bytes(worstExclusion, webJson).Length + 1);

        // 2. Across worst-case (detections, exclusions) pairs up to MaximumSupportedCandidates, projected worst-case
        //    payload (including all-'+' coarse masks and all-'+' subgrids) must stay <= MaximumProductPayloadBytes
        //    and retain positive CoarseCellBudget at DefaultReservedEnvelopeBytes.
        var tileBounds = new StellarFootprintBounds(8, 8, 55, 49); // 48x42
        var streakBounds = new StellarFootprintBounds(16, 16, 3055, 3055); // 3040x3040
        (int Detections, int Exclusions, StellarFootprintBounds Shape)[] regimes =
        [
            (0, 4096, tileBounds),
            (0, 4096, streakBounds),
            (2048, 2048, tileBounds),
            (2048, 2048, streakBounds),
            (3840, 256, streakBounds),
            (4088, 8, streakBounds),
            (4095, 1, streakBounds)
        ];
        foreach (var (detections, exclusions, shape) in regimes)
        {
            var footprints = Enumerable.Repeat(shape, exclusions).ToArray();
            var budget = StellarFootprintOccupancy.ComputeProductBudgets(detections, exclusions, footprints);
            Assert.IsGreaterThan(0, budget.CoarseCellBudget);

            var sampleOccupancy = StellarFootprintOccupancy.Create(
                shape,
                (x, y) => x == shape.MinX && y == shape.MinY,
                budget.CoarseCellBudget,
                budget.SubgridBitBudget);
            var worstCoarse = (long)exclusions * (StellarFootprintOccupancy.WorstCaseCoarseOccupancyWrapperBytes +
                StellarFootprintOccupancy.WorstCaseSerializedBase64Utf8ByteCount(sampleOccupancy.Columns * sampleOccupancy.Rows));
            var refinable = sampleOccupancy.CellWidth > 1 || sampleOccupancy.CellHeight > 1 ? exclusions : 0;
            var worstSubgrid = refinable > 0 && budget.SubgridBitBudget > 0
                ? (long)refinable * (StellarFootprintOccupancy.WorstCaseSubgridWrapperBytes +
                    StellarFootprintOccupancy.WorstCaseSerializedBase64Utf8ByteCount(budget.SubgridBitBudget))
                : 0L;
            var projectedTotal = StellarFootprintOccupancy.DefaultReservedEnvelopeBytes
                + (long)detections * StellarFootprintOccupancy.WorstCaseBytesPerDetection
                + (long)exclusions * StellarFootprintOccupancy.WorstCaseBytesPerExclusionFixed
                + worstCoarse
                + worstSubgrid;
            Assert.IsLessThanOrEqualTo(StellarFootprintOccupancy.MaximumProductPayloadBytes, projectedTotal,
                $"Worst-case all-'+' serialized total must stay <= 4 MiB for D={detections}, E={exclusions} (got {projectedTotal}).");
        }

        // 3. D10-M2 + D8-M3: Producer-generated budgeted 16x14 (3x3) coarse masks pass standalone 1-arg IsValid(footprint),
        //    standalone 3-arg IsValid(footprint, peak, sampleCount), AND exact 5-arg IsValid, while corrupt standalone
        //    masks still fail and 5-arg IsValid rejects artificially coarsened grids on small products.
        var budget4096 = StellarFootprintOccupancy.ComputeProductBudgets(
            0, 4096, Enumerable.Repeat(tileBounds, 4096).ToArray());
        Assert.IsLessThan(StellarFootprintOccupancy.MaximumCells, budget4096.CoarseCellBudget);
        var coarsened = StellarFootprintOccupancy.Create(
            tileBounds,
            static (x, y) => x == 8 || x == 55 || y == 8 || y == 49 || (x - 8) % 10 != 6,
            budget4096.CoarseCellBudget,
            budget4096.SubgridBitBudget);
        Assert.AreEqual(16, coarsened.Columns);
        Assert.AreEqual(14, coarsened.Rows);
        Assert.AreEqual(3, coarsened.CellWidth);
        Assert.AreEqual(3, coarsened.CellHeight);
        Assert.IsNull(coarsened.Subgrid);
        Assert.IsTrue(coarsened.IsValid(tileBounds),
            "Standalone 1-arg IsValid must accept a producer-generated budgeted 16x14 coarse mask.");
        Assert.IsTrue(coarsened.IsValid(tileBounds, new PixelPoint(8.5, 8.5), 1856),
            "Standalone 3-arg IsValid must accept a producer-generated budgeted 16x14 coarse mask.");
        Assert.IsTrue(coarsened.IsValid(tileBounds, new PixelPoint(8.5, 8.5), 1856,
            budget4096.CoarseCellBudget, budget4096.SubgridBitBudget));
        Assert.IsFalse(coarsened.IsValid(tileBounds, new PixelPoint(8.5, 8.5), 1856,
            StellarFootprintOccupancy.MaximumCells, StellarFootprintOccupancy.MaximumSubgridBits),
            "An artificially coarsened grid must be rejected when full resolution fits in budget.");

        // Corrupt standalone masks must still fail both 1-arg and 3-arg IsValid:
        var tooFewCols = new StellarFootprintOccupancy(15, 14, 3, 3, Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 27).ToArray()));
        var tooManyRows = new StellarFootprintOccupancy(16, 15, 3, 3, Convert.ToBase64String(Enumerable.Repeat((byte)0xFF, 30).ToArray()));
        var oversizedCell = new StellarFootprintOccupancy(1, 14, 49, 3, Convert.ToBase64String([0xFF, 0x3F]));
        var overMaxCellsFp = new StellarFootprintBounds(0, 0, 48, 42); // 49*43 = 2107 > 2048
        var overMaxCells = new StellarFootprintOccupancy(49, 43, 1, 1, Convert.ToBase64String(new byte[(2107 + 7) / 8]));
        var pixelGridWithSubgrid = StellarFootprintOccupancy.Create(new StellarFootprintBounds(0, 0, 3, 3), static (x, y) => x == 0 && y == 0) with
        {
            Subgrid = new StellarFootprintSubgridOccupancy(1, 1, Convert.ToBase64String([1]))
        };
        Assert.IsFalse(tooFewCols.IsValid(tileBounds));
        Assert.IsFalse(tooFewCols.IsValid(tileBounds, new PixelPoint(8.5, 8.5), 1856));
        Assert.IsFalse(tooManyRows.IsValid(tileBounds));
        Assert.IsFalse(tooManyRows.IsValid(tileBounds, new PixelPoint(8.5, 8.5), 1856));
        Assert.IsFalse(oversizedCell.IsValid(tileBounds));
        Assert.IsFalse(oversizedCell.IsValid(tileBounds, new PixelPoint(8.5, 8.5), 1856));
        Assert.IsFalse(overMaxCells.IsValid(overMaxCellsFp));
        Assert.IsFalse(pixelGridWithSubgrid.IsValid(new StellarFootprintBounds(0, 0, 3, 3)));
        Assert.IsFalse(coarsened.IsValid(tileBounds, new PixelPoint(8.5, 8.5), sampleCount: 1),
            "1 sample cannot occupy 224 coarse cells.");
        Assert.IsFalse(coarsened.IsValid(tileBounds, new PixelPoint(8.5, 8.5), sampleCount: 48 * 42 + 1),
            "SampleCount exceeding bounding-box area must fail standalone validation.");

        // 4. Exhausted envelope reserve omits Occupancy cleanly (CoarseCellBudget == 0) in StellarSourceMeasurer.Measure.
        const int size = 128;
        var pixels = Field(size, size);
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var r = double.Hypot(x + 0.5 - 64.5, y + 0.5 - 64.5);
                if (r is >= 14.0 and <= 30.0)
                    pixels[y * size + x] += 1800.0;
            }
        }
        var exhausted = StellarSourceMeasurer.Measure(
            pixels, Valid(pixels.Length), [], size, size, options: null,
            reservedEnvelopeBytes: StellarFootprintOccupancy.MaximumProductPayloadBytes - 64);
        var omittedRing = exhausted.Exclusions.Single(e => e.ReasonCode == StellarExclusionReasons.ExtendedRegion);
        Assert.IsNull(omittedRing.Occupancy, "Exhausted envelope reserve must omit Occupancy cleanly.");
        Assert.IsNotNull(omittedRing.Footprint);
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
