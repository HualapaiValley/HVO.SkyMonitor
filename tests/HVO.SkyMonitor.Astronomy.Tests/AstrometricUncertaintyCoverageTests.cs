#pragma warning disable CA5394 // Fixed seeds define reproducible Monte Carlo realizations, never security material.
using HVO.SkyMonitor.AgentCore;
using Observation = HVO.SkyMonitor.Astronomy.AstrometricUncertaintyEstimator.Observation;
using PixelCovariance = HVO.SkyMonitor.Astronomy.AstrometricUncertaintyEstimator.PixelCovariance;

namespace HVO.SkyMonitor.Astronomy.Tests;

/// <summary>
/// Monte Carlo coverage of the fixed-association uncertainty core. Every tolerance is declared before the run: a coverage
/// fraction must lie within 3.5 binomial standard deviations of its nominal level, and a cross-frame correlation within
/// 3.5 Fisher-z standard deviations of its prediction.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricUncertaintyCoverageTests
{
    private const double Tolerance = 3.5;
    private static readonly double[] Levels = [.6827, .95, .99];
    // Chi-square quantiles with four degrees of freedom and two-sided normal quantiles at the same levels.
    private static readonly double[] Ellipsoid = [4.7198, 9.4877, 13.2767];
    private static readonly double[] Interval = [1.0, 1.95996, 2.57583];
    private static readonly AstrometricCatalogData Catalog = AstrometricTestFixture.Catalog();
    private static readonly ProjectionContext Native = AstrometricTestFixture.Truth() with { BoresightAltitudeDegrees = 90, BoresightAzimuthDegrees = 0, RollDegrees = 0 };
    private static readonly FrameReadoutDescriptor FullFrame = new(512, 512, 0, 0, 512, 512, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null);

    private sealed record Star(EnuVector Ray, PixelCovariance Covariance, bool Bright);
    private sealed record Scene(AstrometricRotation Rotation, Star[] Fitting, Star[] Held);
    private sealed record Trial(double[] Error, double[] Covariance, AstrometricUncertaintyEstimator.CoreOutcome Outcome);

    [TestMethod]
    public void IdealModel_EllipsoidAndIntervalsCoverAtNominalLevels()
    {
        var scene = CreateScene(AstrometricTestFixture.Utc, 72, 243, 17, 1105, .05, .3);
        var trials = Run(scene, 400, 20251105, new(), Truth(Native));
        AssertCoverage(trials, "ideal");
        Assert.IsLessThanOrEqualTo(6, trials.Count(t => t.Outcome.ModelFailure is not null), "A valid model withholds at the declared alpha.");
    }

    [TestMethod]
    public void DeclaredSystematicFloor_IsPropagatedAndSumsWithCentroidComponent()
    {
        var scene = CreateScene(AstrometricTestFixture.Utc.AddHours(2), 72, 243, 17, 1106, .03, .12);
        var options = new AstrometricUncertaintyOptions(SystematicPixelSigma: .08);
        var trials = Run(scene, 400, 20251106, options, Truth(Native), systematic: .08);
        AssertCoverage(trials, "systematic-floor");
        var first = trials[0].Outcome;
        Assert.IsTrue(first.Systematic!.Zip(first.Conditional!).Any(p => p.First > .2 * p.Second), "The declared floor must be a material part of the covariance.");
        var none = Run(scene, 1, 20251106, new(), Truth(Native), systematic: .08)[0].Outcome;
        Assert.IsTrue(none.Systematic!.All(v => v == 0));
    }

    [TestMethod]
    [DataRow("omitted-radial-k1", 1e-4)]
    [DataRow("omitted-radial-k1", 3e-4)]
    [DataRow("omitted-radial-k1", 1e-3)]
    [DataRow("omitted-radial-k1", 3e-3)]
    [DataRow("magnitude-dependent-bias", .02)]
    [DataRow("magnitude-dependent-bias", .05)]
    [DataRow("magnitude-dependent-bias", .15)]
    [DataRow("magnitude-dependent-bias", .5)]
    public void MismatchedModel_DeclaredBudget_FalseAcceptanceStaysWithinDeclaredRate(string mismatch, double magnitude)
    {
        // False acceptance: the estimator publishes a total whose 99% ellipsoid excludes the truth. The fitted model omits the
        // mismatch; it is declared only as a one-sigma budget of the same size (calibration k1 prior, or a common-mode offset).
        const int n = 200;
        var (trials, options) = Mismatched(mismatch, magnitude, declared: true, n);
        var accepted = trials.Count(t => t.Outcome.ModelFailure is null);
        var falseAcceptance = trials.Count(t => t.Outcome.ModelFailure is null && Mahalanobis(t) > Ellipsoid[2]);
        var limit = .01 + Tolerance * Math.Sqrt(.01 * .99 / n);
        Assert.IsLessThanOrEqualTo(limit, falseAcceptance / (double)n, $"{mismatch} {magnitude} ({options}): {falseAcceptance} false of {accepted} accepted.");
        // Withholding is the safe direction, so the test is meaningful only where the declared budget explains the mismatch.
        if (mismatch == "omitted-radial-k1" || magnitude <= .05)
            Assert.IsGreaterThanOrEqualTo(.9 * n, accepted, $"{mismatch} {magnitude}: a declared budget must not be withheld as model failure.");
    }

    [TestMethod]
    public void UndeclaredCalibrationError_IsAbsorbedByTheFit_SoOnlyTheConditionalComponentIsPublished()
    {
        // Focal scale absorbs an omitted k1 almost completely: residuals stay consistent while the conditional ellipsoid misses
        // the truth. This is why the total is withheld unless the shared calibration covariance is supplied.
        const int n = 200;
        var (trials, _) = Mismatched("omitted-radial-k1", 1e-3, declared: false, n);
        var accepted = trials.Where(t => t.Outcome.ModelFailure is null).ToArray();
        Assert.IsGreaterThanOrEqualTo(.5 * n, accepted.Length);
        Assert.IsGreaterThanOrEqualTo(.5 * accepted.Length, accepted.Count(t => Mahalanobis(t) > Ellipsoid[2]));
    }

    [TestMethod]
    [DataRow("omitted-radial-k1", 3e-3)]
    [DataRow("magnitude-dependent-bias", .5)]
    public void MismatchedModel_UndeclaredGrossMismatch_IsWithheldAsModelInvalid(string mismatch, double magnitude)
    {
        const int n = 200;
        var (trials, _) = Mismatched(mismatch, magnitude, declared: false, n);
        Assert.IsGreaterThanOrEqualTo(.99 * n, trials.Count(t => t.Outcome.ModelFailure is not null), $"{mismatch} {magnitude}");
    }

    private static (Trial[] Trials, AstrometricUncertaintyOptions Options) Mismatched(string mismatch, double magnitude, bool declared, int n)
    {
        var scene = CreateScene(AstrometricTestFixture.Utc.AddHours(1), 72, 243, 17, 1107, .05, .15);
        var seed = 20251107 + (int)Math.Round(magnitude * 1e5) + (declared ? 1 : 0);
        if (mismatch == "omitted-radial-k1")
        {
            var calibration = declared ? new AstrometricUncertaintyEstimator.CalibrationInput(new(AstrometricTestFixture.Hash("k1-budget"), Native,
                new(OpticalCalibrationCovariance.ResidualScaledMarginal, [OpticalCalibrationCovariance.RadialK1], [magnitude * magnitude])), FullFrame, Native) : null;
            var random = new Random(seed);
            return ([.. Enumerable.Range(0, n).Select(_ => Single(scene, random, new(), Truth(Native with { RadialDistortionK1 = magnitude }), calibration, 0, null))], new());
        }
        var options = new AstrometricUncertaintyOptions(CommonModePixelSigma: declared ? magnitude : 0);
        return (Run(scene, n, seed, options, Truth(Native), bias: (s, p) => s.Bright ? new(p.X + magnitude, p.Y) : p), options);
    }

    [TestMethod]
    public void SharedCalibration_TotalCoversAndCrossFrameCorrelationMatchesPrediction()
    {
        const int n = 400;
        var names = new[] { OpticalCalibrationCovariance.LogFocalScale, OpticalCalibrationCovariance.PrincipalPointX, OpticalCalibrationCovariance.PrincipalPointY, OpticalCalibrationCovariance.RadialK1 };
        double[] sigma = [3e-4, .08, .08, 2e-4];
        var correlation = new double[16]; for (var i = 0; i < 4; i++) correlation[i * 5] = 1;
        correlation[3] = correlation[12] = -.6;
        var values = correlation.Select((r, i) => r * sigma[i / 4] * sigma[i % 4]).ToArray();
        var shared = new AstrometricCalibrationCovariance(AstrometricTestFixture.Hash("session"), Native,
            new(OpticalCalibrationCovariance.ResidualScaledMarginal, names, values));
        var input = new AstrometricUncertaintyEstimator.CalibrationInput(shared, FullFrame, Native);
        var factor = Cholesky(values, 4);
        var scenes = new[] { CreateScene(AstrometricTestFixture.Utc, 72, 243, 17, 1108, .03, .08), CreateScene(AstrometricTestFixture.Utc.AddHours(3), 72, 243, 17, 1109, .03, .08) };
        var random = new Random(20251108);
        var trials = new Trial[2][]; for (var f = 0; f < 2; f++) trials[f] = new Trial[n];
        var predicted = new double[16];
        for (var t = 0; t < n; t++)
        {
            var z = Enumerable.Range(0, 4).Select(_ => Gaussian(random)).ToArray();
            var delta = Enumerable.Range(0, 4).Select(i => Enumerable.Range(0, i + 1).Sum(j => factor[i * 4 + j] * z[j])).ToArray();
            var truth = Truth(Native with
            {
                FocalLengthXPixels = Native.FocalLengthXPixels * Math.Exp(delta[0]), FocalLengthYPixels = Native.FocalLengthYPixels * Math.Exp(delta[0]),
                PrincipalPointX = Native.PrincipalPointX + delta[1], PrincipalPointY = Native.PrincipalPointY + delta[2], RadialDistortionK1 = delta[3]
            });
            for (var f = 0; f < 2; f++) trials[f][t] = Single(scenes[f], random, new(), truth, input, 0, null);
            var sa = trials[0][t].Outcome.Sensitivity!; var sb = trials[1][t].Outcome.Sensitivity!;
            for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++)
                for (var p = 0; p < 4; p++) for (var q = 0; q < 4; q++) predicted[i * 4 + j] += sa[i * 4 + p] * values[p * 4 + q] * sb[j * 4 + q] / n;
        }
        for (var f = 0; f < 2; f++)
        {
            Assert.IsTrue(trials[f].All(t => t.Outcome.Failure is null && t.Outcome.CalibrationFailure is null));
            Assert.IsGreaterThanOrEqualTo(.95 * n, trials[f].Count(t => t.Outcome.ModelFailure is null), "Calibration error within its declared covariance must not look like model failure.");
            AssertCoverage(trials[f], $"frame-{f}");
        }
        var bound = Tolerance / Math.Sqrt(n - 3);
        for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++)
        {
            var va = trials[0].Average(t => t.Covariance[i * 5]); var vb = trials[1].Average(t => t.Covariance[j * 5]);
            var expected = predicted[i * 4 + j] / Math.Sqrt(va * vb);
            var observed = Correlation(trials[0].Select(t => t.Error[i]).ToArray(), trials[1].Select(t => t.Error[j]).ToArray());
            Assert.AreEqual(Math.Atanh(Math.Clamp(expected, -.999, .999)), Math.Atanh(Math.Clamp(observed, -.999, .999)), bound, $"pose {i} x {j}: expected {expected:F3}, observed {observed:F3}");
        }
        Assert.IsGreaterThan(.5, Enumerable.Range(0, 16).Max(k => Math.Abs(predicted[k]) / Math.Sqrt(trials[0].Average(t => t.Covariance[k / 4 * 5]) * trials[1].Average(t => t.Covariance[k % 4 * 5]))),
            "The scenario must exercise a strong shared-calibration correlation.");
    }

    [TestMethod]
    public void Core_IsDeterministicAndRotationVectorInvertsIncrement()
    {
        var scene = CreateScene(AstrometricTestFixture.Utc, 72, 243, 17, 1110, .05, .2);
        var a = Run(scene, 3, 7, new(), Truth(Native)); var b = Run(scene, 3, 7, new(), Truth(Native));
        for (var t = 0; t < 3; t++) CollectionAssert.AreEqual(a[t].Covariance, b[t].Covariance);
        var rotation = AstrometricRotation.FromPose(new(40, 100, -20));
        var w = AstrometricUncertaintyEstimator.RotationVector(rotation, rotation.Increment(1e-3, -2e-3, 5e-4));
        Assert.AreEqual(1e-3, w.East, 1e-12); Assert.AreEqual(-2e-3, w.North, 1e-12); Assert.AreEqual(5e-4, w.Up, 1e-12);
    }

    [TestMethod]
    public void Quantiles_MatchReferenceValues()
    {
        Assert.AreEqual(1.959963985, AstrometricUncertaintyEstimator.NormalQuantile(.975), 1e-8);
        Assert.AreEqual(-3.090232306, AstrometricUncertaintyEstimator.NormalQuantile(1e-3), 1e-8);
        // Reference upper 0.1% points of chi-square with 20 and 100 degrees of freedom.
        Assert.AreEqual(45.315, AstrometricUncertaintyEstimator.ChiSquareQuantile(20, .999), 45.315 * .005);
        Assert.AreEqual(149.449, AstrometricUncertaintyEstimator.ChiSquareQuantile(100, .999), 149.449 * .002);
    }

    private static void AssertCoverage(Trial[] trials, string label)
    {
        var n = trials.Length;
        Assert.IsTrue(trials.All(t => t.Outcome.Failure is null), label);
        for (var level = 0; level < 3; level++)
        {
            var bound = Tolerance * Math.Sqrt(Levels[level] * (1 - Levels[level]) / n);
            var ellipsoid = trials.Count(t => Mahalanobis(t) <= Ellipsoid[level]) / (double)n;
            Assert.AreEqual(Levels[level], ellipsoid, bound, $"{label}: 4-D ellipsoid coverage at {Levels[level]}");
            for (var p = 0; p < 4; p++)
            {
                var interval = trials.Count(t => Math.Abs(t.Error[p]) <= Interval[level] * Math.Sqrt(t.Covariance[p * 5])) / (double)n;
                Assert.AreEqual(Levels[level], interval, bound, $"{label}: parameter {p} interval coverage at {Levels[level]}");
            }
        }
    }

    private static double Mahalanobis(Trial t)
    {
        var inverse = AstrometricLinearAlgebra.SymmetricPositiveDefiniteInverse(t.Covariance, 4)!;
        var sum = 0d; for (var i = 0; i < 4; i++) for (var j = 0; j < 4; j++) sum += t.Error[i] * inverse[i * 4 + j] * t.Error[j];
        return sum;
    }

    private static AstrometricRayCamera Truth(ProjectionContext optics) => new(SolverOptics.From(optics), 1);

    private static Trial[] Run(Scene scene, int n, int seed, AstrometricUncertaintyOptions options, AstrometricRayCamera truth, double systematic = 0,
        Func<Star, PixelPoint, PixelPoint>? bias = null)
    {
        var random = new Random(seed);
        return [.. Enumerable.Range(0, n).Select(_ => Single(scene, random, options, truth, null, systematic, bias))];
    }

    private static Trial Single(Scene scene, Random random, AstrometricUncertaintyOptions options, AstrometricRayCamera truth,
        AstrometricUncertaintyEstimator.CalibrationInput? calibration, double systematic, Func<Star, PixelPoint, PixelPoint>? bias)
    {
        Observation Observe(Star s)
        {
            var p = truth.Pixel(s.Ray, scene.Rotation) ?? throw new InvalidOperationException("Scene star left the truth field.");
            if (bias is not null) p = bias(s, p);
            var c = s.Covariance; var l11 = Math.Sqrt(c.Xx); var l21 = c.Xy / l11; var l22 = Math.Sqrt(c.Yy - l21 * l21);
            var z1 = Gaussian(random); var z2 = Gaussian(random);
            return new(s.Ray, new(p.X + l11 * z1 + systematic * Gaussian(random), p.Y + l21 * z1 + l22 * z2 + systematic * Gaussian(random)), c);
        }
        var fitting = scene.Fitting.Select(Observe).ToArray(); var held = scene.Held.Select(Observe).ToArray();
        // Start a little away from the truth, as an accepted blind solve would, so the refit does real work.
        var start = scene.Rotation.Increment(2e-4 * Gaussian(random), 2e-4 * Gaussian(random), 2e-4 * Gaussian(random));
        var outcome = AstrometricUncertaintyEstimator.Solve(SolverOptics.From(Native), start, 1 + 1e-4 * Gaussian(random), fitting, held, calibration, options);
        Assert.IsNull(outcome.Failure, outcome.Failure?.Reason);
        var w = AstrometricUncertaintyEstimator.RotationVector(scene.Rotation, outcome.Rotation);
        var covariance = outcome.Conditional!.Select((v, i) => v + outcome.Systematic![i] + (outcome.Calibration?[i] ?? 0)).ToArray();
        return new([w.East, w.North, w.Up, Math.Log(outcome.Scale)], covariance, outcome);
    }

    private static Scene CreateScene(DateTimeOffset utc, double altitude, double azimuth, double roll, int seed, double minimumSigma, double maximumSigma)
    {
        var rotation = AstrometricRotation.FromPose(new(altitude, azimuth, roll));
        var camera = new AstrometricRayCamera(SolverOptics.From(Native), 1);
        var site = new CoreSite(AstrometricTestFixture.Observer.LatitudeDegrees, AstrometricTestFixture.Observer.LongitudeDegrees);
        var random = new Random(seed);
        var stars = Catalog.Stars.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.Magnitude, Horizontal: AstrometricMath.Horizontal(s, utc, site)))
            .Where(s => s.Horizontal.AltitudeDegrees > 5).Select(s => (s.Magnitude, Ray: CameraBasis.FromHorizontal(s.Horizontal)))
            .Where(s => camera.Pixel(s.Ray, rotation) is { } p && Math.Sqrt(Math.Pow(p.X - Native.PrincipalPointX, 2) + Math.Pow(p.Y - Native.PrincipalPointY, 2)) < 210)
            .Select(s =>
            {
                var major = minimumSigma + (maximumSigma - minimumSigma) * random.NextDouble(); var minor = major * (.5 + .5 * random.NextDouble());
                var angle = Math.PI * random.NextDouble(); var cos = Math.Cos(angle); var sin = Math.Sin(angle);
                return new Star(s.Ray, new(major * major * cos * cos + minor * minor * sin * sin, (major * major - minor * minor) * cos * sin,
                    major * major * sin * sin + minor * minor * cos * cos), s.Magnitude < 3.5);
            }).ToArray();
        Assert.IsGreaterThanOrEqualTo(64, stars.Length, "The scene needs 48 fitting and 16 held-out stars.");
        return new(rotation, [.. stars.Where((_, i) => i % 4 != 3).Take(48)], [.. stars.Where((_, i) => i % 4 == 3).Take(16)]);
    }

    private static double Gaussian(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());

    private static double[] Cholesky(double[] matrix, int n)
    {
        var l = new double[n * n];
        for (var i = 0; i < n; i++) for (var j = 0; j <= i; j++)
        {
            var sum = matrix[i * n + j]; for (var k = 0; k < j; k++) sum -= l[i * n + k] * l[j * n + k];
            l[i * n + j] = i == j ? Math.Sqrt(sum) : sum / l[j * n + j];
        }
        return l;
    }

    private static double Correlation(double[] a, double[] b)
    {
        var ma = a.Average(); var mb = b.Average();
        return a.Zip(b).Sum(p => (p.First - ma) * (p.Second - mb)) / Math.Sqrt(a.Sum(x => (x - ma) * (x - ma)) * b.Sum(y => (y - mb) * (y - mb)));
    }
}
