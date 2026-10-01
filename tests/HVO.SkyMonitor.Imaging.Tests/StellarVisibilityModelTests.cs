namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class StellarVisibilityModelTests
{
    [TestMethod]
    [DataRow(0d, 0d, 1d)]
    [DataRow(0d, 6d, .1d)]
    [DataRow(120d, 2d, 20d)]
    [DataRow(20000d, 6d, 60d)]
    public void AperturePredictionAndMagnitudeLimitAgreeWithIndependentReference(double background, double readNoise, double exposure)
    {
        // Independent numerical bisection, not the production quadratic, tests both limiting regimes.
        var model = new StellarPhotometryModel(60000, exposure, background, readNoise, .02, .95, 25, .8, .7);
        const double magnitude = 4;
        var signal = 60000 * Math.Pow(10, -.4 * magnitude) * exposure * .8 * .7 * .95;
        var variance = signal + 25 * ((background * .7 + .02) * exposure + readNoise * readNoise);
        var actual = StellarVisibilityModel.Predict(magnitude, model);
        Assert.AreEqual(signal, actual.SourceElectrons, signal * 1e-6);
        Assert.AreEqual(signal / Math.Sqrt(variance), actual.SignalToNoise, 1e-6);
        var left = -20d; var right = 30d;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var middle = (left + right) / 2;
            var s = 60000 * Math.Pow(10, -.4 * middle) * exposure * .8 * .7 * .95;
            var snr = s / Math.Sqrt(s + 25 * ((background * .7 + .02) * exposure + readNoise * readNoise));
            if (snr > 5) left = middle; else right = middle;
        }
        Assert.AreEqual((left + right) / 2, StellarVisibilityModel.LimitingMagnitude(model, 5)!.Value, 1e-6);
        Assert.IsTrue(StellarVisibilityModel.BestCaseMagnitudeCeiling(60000, exposure, .8, 5) >=
            StellarVisibilityModel.LimitingMagnitude(model, 5));
    }

    [TestMethod]
    public void LongerExposureIncreasesSignalSnrAndFaintAdmissionWithoutChangingPsf()
    {
        var previousElectrons = -1d; var previousSnr = -1d; var previousLimit = -100d;
        foreach (var exposure in new[] { .1, 1, 5, 20, 60 })
        {
            var model = new StellarPhotometryModel(60000, exposure, 2, 6, .01, .98, 50);
            var prediction = StellarVisibilityModel.Predict(5, model);
            var limit = StellarVisibilityModel.LimitingMagnitude(model, 5)!.Value;
            Assert.IsTrue(prediction.SourceElectrons > previousElectrons);
            Assert.IsTrue(prediction.SignalToNoise > previousSnr);
            Assert.IsTrue(limit > previousLimit);
            previousElectrons = prediction.SourceElectrons; previousSnr = prediction.SignalToNoise; previousLimit = limit;
        }
    }

    [TestMethod]
    public void DigitalAverageChangesExtractionScaleButPreservesNativeNoiseCovariance()
    {
        // Four physical photosites in one digital bin. Dividing their sum by four scales
        // both the signal and its noise standard deviation; it never removes three read noises.
        StellarApertureSample[] native = [new(12, 8, 2, 36), new(30, 8, 2, 36), new(7, 8, 2, 36), new(21, 8, 2, 36)];
        var summed = StellarVisibilityModel.Predict(native);
        var averaged = StellarVisibilityModel.Predict(native.Select(sample => sample with { ExtractionWeight = .25 }).ToArray());
        Assert.AreEqual(70, summed.SourceElectrons, 1e-12);
        Assert.AreEqual(254, summed.NoiseVariance, 1e-12);
        Assert.AreEqual(summed.SignalToNoise, averaged.SignalToNoise, 1e-12);
        Assert.AreEqual(summed.NoiseVariance / 16, averaged.NoiseVariance, 1e-12);
        var incorrectChargeBinSnr = 70 / Math.Sqrt(70 + 4 * 10 + 36);
        Assert.IsTrue(Math.Abs(summed.SignalToNoise - incorrectChargeBinSnr) > .5);
    }

    [TestMethod]
    public void WeightedCfaSamplesUseActualPhaseChannelAndVignettingElectrons()
    {
        StellarApertureSample[] samples = [new(4, 6, 1, 9), new(12, 8, 1, 9), new(3, 2, 1, 9), new(8, 8, 1, 9)];
        var prediction = StellarVisibilityModel.Predict(samples);
        Assert.AreEqual(27, prediction.SourceElectrons, 1e-12);
        Assert.AreEqual(27 / Math.Sqrt(91), prediction.SignalToNoise, 1e-12);
    }

    [TestMethod]
    public void ZeroSensitivityAndCorruptModelsHaveExplicitOutcomes()
    {
        var empty = new StellarPhotometryModel(0, 20, 2, 6, 0, .98, 50);
        Assert.AreEqual(0, StellarVisibilityModel.Predict(5, empty).SignalToNoise);
        Assert.IsNull(StellarVisibilityModel.LimitingMagnitude(empty, 5));
        Assert.IsNull(StellarVisibilityModel.BestCaseMagnitudeCeiling(1000, 0, 1, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarVisibilityModel.Predict(0, empty with { ApertureFraction = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarVisibilityModel.Predict(double.NaN, empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarVisibilityModel.LimitingMagnitude(empty, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarVisibilityModel.BestCaseMagnitudeCeiling(1000, 20, 1, 5, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => StellarVisibilityModel.Predict(new StellarApertureSample[] { new(1, -1, 0, 0) }));
    }
}
