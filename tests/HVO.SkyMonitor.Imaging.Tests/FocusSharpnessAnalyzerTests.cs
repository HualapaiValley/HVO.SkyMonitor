using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class FocusSharpnessAnalyzerTests
{
    private const double FwhmPerSigma = 2.3548200450309493;

    [TestMethod]
    public void RoundGaussianSources_ReportMedianFwhmAndProvenance()
    {
        const int width = 160, height = 96;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 30.5, 30.5, 1.0, 1.0, 1000);
        AddGaussian(pixels, width, 80.5, 30.5, 1.0, 1.0, 1000);
        AddGaussian(pixels, width, 130.5, 30.5, 1.0, 1.0, 1000);

        var result = FocusSharpnessAnalyzer.Analyze(pixels, Valid(pixels.Length), [], width, height);

        Assert.AreEqual(FocusSharpnessStatus.Valid, result.Status);
        Assert.AreEqual(FocusSharpnessReasonCodes.Valid, result.ReasonCode);
        Assert.IsTrue(result.HasMeasurement);
        Assert.AreEqual(FwhmPerSigma, result.MedianFwhmPixels!.Value, 0.12);
        Assert.AreEqual(3, result.AcceptedSourceCount);
        Assert.AreEqual(3, result.MeasuredSourceCount);
        Assert.AreEqual(0, result.SaturatedSourceCount);
        Assert.AreEqual(FocusSharpnessAnalyzer.AlgorithmVersion, result.AlgorithmVersion);
        Assert.AreEqual(
            FocusSharpnessAnalyzer.SettingsIdentity(new()),
            result.SettingsIdentitySha256);
    }

    [TestMethod]
    public void WiderPsf_IncreasesReportedFwhm()
    {
        const int width = 160, height = 96;
        var sharp = Field(width, height);
        var blurred = Field(width, height);
        foreach (var (x, sigma) in new[] { (30.5, 1.0), (80.5, 1.0), (130.5, 1.0) })
        {
            AddGaussian(sharp, width, x, 30.5, sigma, sigma, 1000);
            AddGaussian(blurred, width, x, 30.5, 1.7, 1.7, 1000);
        }

        var sharpResult = FocusSharpnessAnalyzer.Analyze(sharp, Valid(sharp.Length), [], width, height);
        var blurredResult = FocusSharpnessAnalyzer.Analyze(blurred, Valid(blurred.Length), [], width, height);

        Assert.AreEqual(FocusSharpnessStatus.Valid, sharpResult.Status);
        Assert.AreEqual(FocusSharpnessStatus.Valid, blurredResult.Status);
        Assert.IsTrue(
            blurredResult.MedianFwhmPixels!.Value > sharpResult.MedianFwhmPixels!.Value + 1.0,
            $"blurred {blurredResult.MedianFwhmPixels} should exceed sharp {sharpResult.MedianFwhmPixels}");
    }

    [TestMethod]
    public void StarlessField_ReturnsNoStarsWithoutAValue()
    {
        const int width = 96, height = 96;
        var pixels = Field(width, height);

        var result = FocusSharpnessAnalyzer.Analyze(pixels, Valid(pixels.Length), [], width, height);

        Assert.AreEqual(FocusSharpnessStatus.NoStars, result.Status);
        Assert.AreEqual(FocusSharpnessReasonCodes.NoStars, result.ReasonCode);
        Assert.IsNull(result.MedianFwhmPixels);
        Assert.IsFalse(result.HasMeasurement);
    }

    [TestMethod]
    public void ClippedField_ReturnsSaturatedWithoutAValue()
    {
        const int width = 96, height = 96;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 48.5, 48.5, 1.0, 1.0, 60000);
        var saturated = Enumerable.Repeat(true, pixels.Length).ToArray();

        var result = FocusSharpnessAnalyzer.Analyze(pixels, Valid(pixels.Length), saturated, width, height);

        Assert.AreEqual(FocusSharpnessStatus.Saturated, result.Status);
        Assert.AreEqual(FocusSharpnessReasonCodes.Saturated, result.ReasonCode);
        Assert.IsNull(result.MedianFwhmPixels);
    }

    [TestMethod]
    public void FewerThanMinimumSources_ReturnsInsufficientEvidenceWithoutAValue()
    {
        const int width = 160, height = 96;
        var pixels = Field(width, height);
        AddGaussian(pixels, width, 80.5, 48.5, 1.0, 1.0, 1000);

        var result = FocusSharpnessAnalyzer.Analyze(pixels, Valid(pixels.Length), [], width, height);

        Assert.AreEqual(FocusSharpnessStatus.InsufficientEvidence, result.Status);
        Assert.AreEqual(FocusSharpnessReasonCodes.InsufficientEvidence, result.ReasonCode);
        Assert.IsNull(result.MedianFwhmPixels);
        Assert.AreEqual(1, result.AcceptedSourceCount);
    }

    [TestMethod]
    public void SettingsIdentity_ChangesWithThresholdsAndIsDeterministic()
    {
        var baseline = FocusSharpnessAnalyzer.SettingsIdentity(new());
        Assert.AreEqual(baseline, FocusSharpnessAnalyzer.SettingsIdentity(new()));
        Assert.AreNotEqual(baseline, FocusSharpnessAnalyzer.SettingsIdentity(new() { MinimumAcceptedSources = 4 }));
        Assert.AreNotEqual(baseline, FocusSharpnessAnalyzer.SettingsIdentity(new() { RequireUnsaturatedSources = false }));
        Assert.AreNotEqual(baseline, FocusSharpnessAnalyzer.SettingsIdentity(
            new() { Measurement = new StellarMeasurementOptions(PeakSigma: 6) }));
    }

    [TestMethod]
    public void RejectsInvalidOptions()
    {
        var pixels = Field(32, 32);
        Assert.ThrowsExactly<ArgumentException>(() => FocusSharpnessAnalyzer.Analyze(
            pixels, Valid(pixels.Length), [], 32, 32, new() { MinimumAcceptedSources = 0 }));
        Assert.ThrowsExactly<ArgumentNullException>(() => FocusSharpnessAnalyzer.SettingsIdentity(null!));
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
