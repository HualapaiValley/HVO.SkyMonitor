using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class FocusStarMeasurerTests
{
    // For a circular Gaussian the half-flux radius is sigma * sqrt(2 ln 2), so HFD equals FWHM.
    private const double HalfFluxRadiusPerSigma = 1.1774100225154747;
    private const double FwhmPerSigma = 2.3548200450309493;
    private const int Size = 160;

    [TestMethod]
    [DataRow(1.5)]
    [DataRow(2.5)]
    [DataRow(4.0)]
    [DataRow(7.0)]
    public void PixelIntegratedGaussian_ReportsAnalyticHalfFluxDiameterAndFwhm(double sigma)
    {
        var pixels = Field(Size, Size, 100);
        AddIntegratedGaussian(pixels, Size, 80.3, 79.6, sigma, 200000);

        var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(78, 81));

        Assert.AreEqual(FocusStarStatus.Valid, result.Status, result.ReasonCode);
        Assert.IsTrue(result.HasMeasurement);
        // The pixel box adds 1/12 px^2 of variance to the sampled profile.
        var effectiveSigma = Math.Sqrt(sigma * sigma + 1.0 / 12);
        Assert.AreEqual(HalfFluxRadiusPerSigma * effectiveSigma, result.HalfFluxRadiusPixels!.Value, 0.02 * effectiveSigma);
        Assert.AreEqual(2 * result.HalfFluxRadiusPixels.Value, result.HalfFluxDiameterPixels!.Value, 1e-12);
        Assert.AreEqual(FwhmPerSigma * sigma, result.FwhmPixels!.Value, 0.03 * FwhmPerSigma * sigma);
        Assert.AreEqual(80.3, result.Centroid!.Value.X, 0.01);
        Assert.AreEqual(79.6, result.Centroid.Value.Y, 0.01);
        // The broadest profiles leave a negligible wing in the annulus.
        Assert.AreEqual(100, result.Background!.Value, 1e-6);
        Assert.AreEqual(200000, result.TotalFlux!.Value, 200000 * 0.01);
        Assert.AreEqual(FocusStarMeasurer.AlgorithmVersion, result.AlgorithmVersion);
        Assert.AreEqual(FocusStarMeasurer.SettingsIdentity(new()), result.SettingsIdentitySha256);
    }

    [TestMethod]
    public void DefocusSweep_HalfFluxDiameterFollowsTheVCurve()
    {
        var sigmas = new[] { 6.0, 4.0, 2.0, 1.0, 2.0, 4.0, 6.0 };
        var diameters = sigmas.Select(sigma =>
        {
            var pixels = Field(Size, Size, 100);
            AddIntegratedGaussian(pixels, Size, 80.5, 80.5, sigma, 200000);
            return FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(80, 80))
                .HalfFluxDiameterPixels!.Value;
        }).ToArray();

        for (var index = 1; index <= 3; index++)
        {
            Assert.IsTrue(diameters[index] < diameters[index - 1], $"HFD must fall toward best focus: {string.Join(", ", diameters)}");
            Assert.IsTrue(diameters[^(index + 1)] < diameters[^index], $"HFD must rise away from best focus: {string.Join(", ", diameters)}");
        }
        Assert.AreEqual(diameters[0], diameters[^1], 1e-9);
    }

    [TestMethod]
    public void NoisyBackground_IsSubtractedAndCentroidStaysAccurate()
    {
        var random = new Random(1017);
        var pixels = Field(Size, Size, 1000);
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] += Gaussian(random) * 5;
        }
        AddIntegratedGaussian(pixels, Size, 81.25, 78.75, 2.5, 150000);

        var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(80, 80));

        Assert.AreEqual(FocusStarStatus.Valid, result.Status, result.ReasonCode);
        Assert.AreEqual(1000, result.Background!.Value, 1);
        Assert.AreEqual(5, result.NoiseSigma!.Value, 1);
        Assert.AreEqual(81.25, result.Centroid!.Value.X, 0.05);
        Assert.AreEqual(78.75, result.Centroid.Value.Y, 0.05);
        var expected = HalfFluxRadiusPerSigma * Math.Sqrt(2.5 * 2.5 + 1.0 / 12);
        Assert.AreEqual(expected, result.HalfFluxRadiusPixels!.Value, expected * 0.05);
        Assert.IsTrue(result.SignalToNoise > 100);
    }

    [TestMethod]
    public void WindowOrigin_ReportsSourceFrameCoordinates()
    {
        var pixels = Field(Size, Size, 50);
        AddIntegratedGaussian(pixels, Size, 70.4, 90.2, 2, 100000);

        var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size,
            new(1270, 590), originX: 1200, originY: 500);

        Assert.AreEqual(FocusStarStatus.Valid, result.Status, result.ReasonCode);
        Assert.AreEqual(1270.4, result.Centroid!.Value.X, 0.01);
        Assert.AreEqual(590.2, result.Centroid.Value.Y, 0.01);
    }

    [TestMethod]
    public void EmptyNoiseField_IsNoStarWithoutAWidth()
    {
        var random = new Random(7);
        var pixels = Field(Size, Size, 200);
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] += Gaussian(random) * 3;
        }

        var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(80, 80));

        Assert.AreEqual(FocusStarStatus.NoStar, result.Status);
        Assert.AreEqual(FocusStarReasonCodes.NoStar, result.ReasonCode);
        Assert.IsNull(result.HalfFluxRadiusPixels);
        Assert.IsNull(result.HalfFluxDiameterPixels);
        Assert.IsNull(result.FwhmPixels);
        Assert.IsFalse(result.HasMeasurement);
    }

    [TestMethod]
    public void ClippedCore_IsSaturatedButStillTracksTheCentroid()
    {
        var pixels = Field(Size, Size, 100);
        AddIntegratedGaussian(pixels, Size, 80.5, 80.5, 1.5, 400000);
        var saturated = new bool[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            if (pixels[index] >= 10000)
            {
                pixels[index] = 10000;
                saturated[index] = true;
            }
        }

        var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), saturated, Size, Size, new(80, 80));

        Assert.AreEqual(FocusStarStatus.Saturated, result.Status);
        Assert.AreEqual(FocusStarReasonCodes.Saturated, result.ReasonCode);
        Assert.IsTrue(result.SaturatedSampleCount > 0);
        Assert.IsNull(result.HalfFluxDiameterPixels);
        Assert.AreEqual(80.5, result.Centroid!.Value.X, 0.05);
    }

    [TestMethod]
    public void StarNearTheEdge_IsApertureTruncated()
    {
        var pixels = Field(Size, Size, 100);
        AddIntegratedGaussian(pixels, Size, 20.5, 80.5, 2, 100000);

        var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(20, 80));

        Assert.AreEqual(FocusStarStatus.ApertureTruncated, result.Status);
        Assert.AreEqual(FocusStarReasonCodes.ApertureTruncated, result.ReasonCode);
        Assert.IsNull(result.HalfFluxDiameterPixels);
    }

    [TestMethod]
    public void MaskedApertureSample_IsApertureTruncated()
    {
        var pixels = Field(Size, Size, 100);
        AddIntegratedGaussian(pixels, Size, 80.5, 80.5, 2, 100000);
        var valid = Valid(pixels.Length);
        valid[82 * Size + 82] = false;

        var result = FocusStarMeasurer.Measure(pixels, valid, [], Size, Size, new(80, 80));

        Assert.AreEqual(FocusStarStatus.ApertureTruncated, result.Status);
        Assert.AreEqual(FocusStarReasonCodes.ApertureMasked, result.ReasonCode);
    }

    [TestMethod]
    public void StarBroaderThanTheAperture_IsNotContained()
    {
        var pixels = Field(Size, Size, 100);
        AddIntegratedGaussian(pixels, Size, 80.5, 80.5, 18, 4000000);

        var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(80, 80));

        Assert.AreEqual(FocusStarStatus.NotContained, result.Status);
        Assert.IsNull(result.HalfFluxDiameterPixels);
    }

    [TestMethod]
    public void MaskedAnnulus_IsBackgroundUnavailable()
    {
        var pixels = Field(Size, Size, 100);
        AddIntegratedGaussian(pixels, Size, 80.5, 80.5, 2, 100000);
        var valid = Valid(pixels.Length);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var distance = Math.Sqrt(Math.Pow(x + 0.5 - 80.5, 2) + Math.Pow(y + 0.5 - 80.5, 2));
                if (distance > 34)
                {
                    valid[y * Size + x] = false;
                }
            }
        }

        var result = FocusStarMeasurer.Measure(pixels, valid, [], Size, Size, new(80.5, 80.5));

        Assert.AreEqual(FocusStarStatus.BackgroundUnavailable, result.Status);
        Assert.IsNull(result.HalfFluxDiameterPixels);
    }

    [TestMethod]
    public void SelectTarget_PicksBrightestIsolatedUnclippedStarAndRejectsHotPixels()
    {
        const int width = 400, height = 300;
        var pixels = Field(width, height, 100);
        AddIntegratedGaussian(pixels, width, 100.5, 100.5, 1.5, 50000);
        AddIntegratedGaussian(pixels, width, 300.5, 150.5, 1.5, 120000);
        AddIntegratedGaussian(pixels, width, 200.5, 220.5, 1.5, 900000);
        pixels[150 * width + 200] = 60000;
        var saturated = new bool[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            if (pixels[index] >= 40000)
            {
                pixels[index] = 40000;
                saturated[index] = true;
            }
        }
        saturated[150 * width + 200] = false;
        pixels[150 * width + 200] = 30000;

        var target = FocusStarMeasurer.SelectTarget(pixels, Valid(pixels.Length), saturated, width, height);

        Assert.IsTrue(target.Selected);
        Assert.AreEqual(300.5, target.Position!.Value.X);
        Assert.AreEqual(150.5, target.Position.Value.Y);
        Assert.AreEqual(FocusStarReasonCodes.Valid, target.ReasonCode);
    }

    [TestMethod]
    public void SelectTarget_NearAPickedPixelRefinesToTheNearestStar()
    {
        const int width = 400, height = 300;
        var pixels = Field(width, height, 100);
        AddIntegratedGaussian(pixels, width, 100.5, 100.5, 1.5, 50000);
        AddIntegratedGaussian(pixels, width, 300.5, 150.5, 1.5, 120000);

        var target = FocusStarMeasurer.SelectTarget(pixels, Valid(pixels.Length), [], width, height,
            near: new PixelPoint(104, 96));
        var missed = FocusStarMeasurer.SelectTarget(pixels, Valid(pixels.Length), [], width, height,
            near: new PixelPoint(200, 200));

        Assert.AreEqual(new PixelPoint(100.5, 100.5), target.Position);
        Assert.IsFalse(missed.Selected);
        Assert.AreEqual(FocusStarReasonCodes.NoCandidateNearSelection, missed.ReasonCode);
    }

    [TestMethod]
    public void SelectTarget_EmptyFieldHasNoCandidate()
    {
        var pixels = Field(Size, Size, 100);

        var target = FocusStarMeasurer.SelectTarget(pixels, Valid(pixels.Length), [], Size, Size);

        Assert.IsFalse(target.Selected);
        Assert.AreEqual(FocusStarReasonCodes.NoCandidate, target.ReasonCode);
    }

    [TestMethod]
    public void VignettedSky_CurvatureIsSubtractedAndNeverMakesAStar()
    {
        // Vignetting brightest at the star: 40 counts of fall-off at 48 px, far above the 1.5-count noise.
        var random = new Random(31);
        var sky = Field(Size, Size, 0);
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var r2 = Math.Pow(x + 0.5 - 80.5, 2) + Math.Pow(y + 0.5 - 80.5, 2);
                sky[y * Size + x] = 1660 - 40 * r2 / (48 * 48) + Gaussian(random) * 1.5;
            }
        }
        var starless = (double[])sky.Clone();
        AddIntegratedGaussian(sky, Size, 80.5, 80.5, 2.5, 100000);

        var measured = FocusStarMeasurer.Measure(sky, Valid(sky.Length), [], Size, Size, new(80, 80));
        var empty = FocusStarMeasurer.Measure(starless, Valid(starless.Length), [], Size, Size, new(80, 80));

        Assert.AreEqual(FocusStarStatus.Valid, measured.Status, measured.ReasonCode);
        Assert.AreEqual(1660, measured.Background!.Value, 0.5);
        Assert.AreEqual(100000, measured.TotalFlux!.Value, 100000 * 0.01);
        var expected = HalfFluxRadiusPerSigma * Math.Sqrt(2.5 * 2.5 + 1.0 / 12);
        Assert.AreEqual(expected, measured.HalfFluxRadiusPixels!.Value, expected * 0.02);
        Assert.AreEqual(FocusStarStatus.NoStar, empty.Status, "Sky curvature alone must not be reported as a star.");
    }

    [TestMethod]
    public void FaintStarOnQuantizedSky_CentroidIsUnbiased()
    {
        // About SNR 15 on an integer sky with 1.5-count noise: the thresholded moment alone drifts toward noise.
        double sumX = 0, sumY = 0;
        const int trials = 12;
        for (var trial = 0; trial < trials; trial++)
        {
            var random = new Random(100 + trial);
            var pixels = Field(Size, Size, 66);
            AddIntegratedGaussian(pixels, Size, 81.3, 79.7, 1.2, 1200);
            for (var index = 0; index < pixels.Length; index++)
            {
                pixels[index] = Math.Round(pixels[index] + Gaussian(random) * 1.5);
            }

            var result = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(80, 80));

            Assert.AreEqual(FocusStarStatus.Valid, result.Status, result.ReasonCode);
            Assert.AreEqual(81.3, result.Centroid!.Value.X, 0.25, $"trial {trial}");
            Assert.AreEqual(79.7, result.Centroid.Value.Y, 0.25, $"trial {trial}");
            sumX += result.Centroid.Value.X;
            sumY += result.Centroid.Value.Y;
        }
        Assert.AreEqual(81.3, sumX / trials, 0.05);
        Assert.AreEqual(79.7, sumY / trials, 0.05);
    }

    [TestMethod]
    public void SelectTarget_BroadNoisyStarIsOneObject()
    {
        // Noise breaks a broad profile into many local maxima; they must merge rather than crowd each other out.
        const int width = 300, height = 300;
        var random = new Random(5);
        var pixels = Field(width, height, 1000);
        AddIntegratedGaussian(pixels, width, 150.5, 150.5, 6, 400000);
        for (var index = 0; index < pixels.Length; index++)
        {
            pixels[index] += Gaussian(random) * 8;
        }

        var target = FocusStarMeasurer.SelectTarget(pixels, Valid(pixels.Length), [], width, height);

        Assert.IsTrue(target.Selected, target.ReasonCode);
        Assert.AreEqual(150.5, target.Position!.Value.X, 3);
        Assert.AreEqual(150.5, target.Position.Value.Y, 3);
        Assert.AreEqual(1, target.CandidateCount);
    }

    [TestMethod]
    public void SelectTarget_OnlyAClippedStar_IsReturnedSoMeasurementReportsSaturation()
    {
        const int width = 300, height = 300;
        var pixels = Field(width, height, 100);
        AddIntegratedGaussian(pixels, width, 150.5, 150.5, 1.2, 900000);
        var saturated = new bool[pixels.Length];
        for (var index = 0; index < pixels.Length; index++)
        {
            if (pixels[index] >= 30000)
            {
                pixels[index] = 30000;
                saturated[index] = true;
            }
        }

        var target = FocusStarMeasurer.SelectTarget(pixels, Valid(pixels.Length), saturated, width, height);
        var measured = FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), saturated, width, height, target.Position!.Value);

        Assert.IsTrue(target.Selected, target.ReasonCode);
        Assert.AreEqual(150.5, target.Position!.Value.X, 1);
        Assert.AreEqual(FocusStarStatus.Saturated, measured.Status);
    }

    [TestMethod]
    public void SelectTarget_StepEdgeIsNotAStarAndTheRealStarWins()
    {
        // A bright band with step edges (like a rim or the edge of an unmasked image circle) on a dark field: every band
        // pixel is significant against global statistics, but none is a star against its own local annulus.
        const int width = 400, height = 300;
        var random = new Random(11);
        var pixels = Field(width, height, 0);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[y * width + x] = (x is >= 60 and < 120 ? 1500 : 100) + Gaussian(random) * 3;
            }
        }
        var starless = (double[])pixels.Clone();
        AddIntegratedGaussian(pixels, width, 300.5, 150.5, 2, 20000);

        var withStar = FocusStarMeasurer.SelectTarget(pixels, Valid(pixels.Length), [], width, height);
        var withoutStar = FocusStarMeasurer.SelectTarget(starless, Valid(starless.Length), [], width, height);

        Assert.AreEqual(new PixelPoint(300.5, 150.5), withStar.Position);
        Assert.IsFalse(withoutStar.Selected, $"{withoutStar}");
    }

    [TestMethod]
    public void SelectTarget_AutomaticSkipsAStarWhoseApertureIsMasked()
    {
        const int width = 400, height = 300;
        var pixels = Field(width, height, 100);
        AddIntegratedGaussian(pixels, width, 100.5, 150.5, 1.5, 200000);
        AddIntegratedGaussian(pixels, width, 300.5, 150.5, 1.5, 50000);
        var valid = Valid(pixels.Length);
        valid[150 * width + 120] = false;

        var automatic = FocusStarMeasurer.SelectTarget(pixels, valid, [], width, height);
        var picked = FocusStarMeasurer.SelectTarget(pixels, valid, [], width, height, near: new PixelPoint(101, 151));

        Assert.AreEqual(new PixelPoint(300.5, 150.5), automatic.Position);
        Assert.AreEqual(new PixelPoint(100.5, 150.5), picked.Position, "An operator pick is kept so measurement reports it.");
    }

    [TestMethod]
    public void InvalidLayoutOrOptions_AreRejected()
    {
        var pixels = Field(Size, Size, 0);
        Assert.ThrowsExactly<ArgumentException>(() =>
            FocusStarMeasurer.Measure(pixels, Valid(pixels.Length - 1), [], Size, Size, new(80, 80)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(80, 80),
                options: new(ApertureRadiusPixels: 40, AnnulusInnerRadiusPixels: 30)));
        Assert.ThrowsExactly<ArgumentException>(() =>
            FocusStarMeasurer.Measure(pixels, Valid(pixels.Length), [], Size, Size, new(double.NaN, 80)));
        Assert.AreNotEqual(
            FocusStarMeasurer.SettingsIdentity(new()),
            FocusStarMeasurer.SettingsIdentity(new(ApertureRadiusPixels: 30)));
    }

    private static double[] Field(int width, int height, double background)
    {
        var pixels = new double[width * height];
        Array.Fill(pixels, background);
        return pixels;
    }

    private static bool[] Valid(int length)
    {
        var valid = new bool[length];
        Array.Fill(valid, true);
        return valid;
    }

    private static void AddIntegratedGaussian(double[] pixels, int width, double cx, double cy, double sigma, double flux)
    {
        var height = pixels.Length / width;
        var reach = (int)Math.Ceiling(6 * sigma) + 1;
        for (var y = Math.Max(0, (int)cy - reach); y < Math.Min(height, (int)cy + reach); y++)
        {
            var fy = Cdf((y + 1 - cy) / sigma) - Cdf((y - cy) / sigma);
            for (var x = Math.Max(0, (int)cx - reach); x < Math.Min(width, (int)cx + reach); x++)
            {
                var fx = Cdf((x + 1 - cx) / sigma) - Cdf((x - cx) / sigma);
                pixels[y * width + x] += flux * fx * fy;
            }
        }
    }

    private static double Cdf(double z) => 0.5 * (1 + Erf(z / Math.Sqrt(2)));

    // Abramowitz and Stegun 7.1.26, absolute error below 1.5e-7.
    private static double Erf(double x)
    {
        var sign = Math.Sign(x);
        x = Math.Abs(x);
        var t = 1 / (1 + 0.3275911 * x);
        var y = 1 - ((((1.061405429 * t - 1.453152027) * t + 1.421413741) * t - 0.284496736) * t + 0.254829592) * t * Math.Exp(-x * x);
        return sign * y;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "Seeded, reproducible synthetic noise.")]
    private static double Gaussian(Random random)
        => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
