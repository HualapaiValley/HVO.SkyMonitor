using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricUncertaintyEstimatorTests
{
    private sealed record Solved(AstrometricCatalogData Catalog, AstrometricCalibration Calibration, AstrometricDetection[] Detections, AstrometricSolveResult Result);

    // The fixture adds uniform +-0.02 px centroid noise, whose standard deviation is 0.04 / sqrt(12).
    private static readonly double Sigma = .04 / Math.Sqrt(12);
    private static readonly FrameReadoutDescriptor FullFrame = new(512, 512, 0, 0, 512, 512, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null);
    private static readonly Lazy<Solved> Accepted = new(() =>
    {
        var catalog = AstrometricTestFixture.Catalog(); var truth = AstrometricTestFixture.Truth(); var calibration = AstrometricTestFixture.Calibration(truth);
        var detections = AstrometricTestFixture.Centroids(catalog, truth, AstrometricTestFixture.Utc);
        var result = AstrometricSolver.Solve(AstrometricTestFixture.Frame(AstrometricTestFixture.Utc), calibration, catalog, detections);
        Assert.IsTrue(result.Assessment.HasMeasuredMapping, result.Assessment.Reason); return new(catalog, calibration, detections, result);
    });

    [TestMethod]
    public void AcceptedFrame_PublishesSeparateComponentsBoundToEveryIdentity()
    {
        var f = Accepted.Value; var u = Estimate(f, Covariances(f, Sigma));
        Assert.AreEqual(AstrometricUncertaintyEstimator.Available, u.Status, u.Reason); Assert.IsTrue(u.IsAvailable);
        Assert.AreEqual(AstrometricFrameUncertainty.CurrentSchemaVersion, u.SchemaVersion);
        Assert.AreEqual(f.Result.Assessment.IdentitySha256, u.AssessmentIdentitySha256); Assert.AreEqual(f.Calibration.IdentitySha256, u.CalibrationIdentitySha256);
        Assert.AreEqual(f.Catalog.IdentitySha256, u.CatalogIdentitySha256); Assert.AreEqual(f.Catalog.SelectionIdentitySha256, u.CatalogSelectionIdentitySha256);
        Assert.AreEqual(new AstrometricSolverOptions().IdentitySha256, u.SolverSettingsIdentitySha256);
        Assert.AreEqual(new AstrometricUncertaintyOptions().IdentitySha256, u.OptionsIdentitySha256);
        var frame = f.Result.Assessment.Frame;
        Assert.AreEqual(new AstrometricConventionIdentity(frame.ExposureStartUtc, frame.ExposureEndUtc, frame.MidpointUtc, "utc", "J2000.0",
            AstrometricConventions.CoordinateModel, "none-fixed-position-catalog-baseline", "topocentric-horizontal-enu-at-exposure-midpoint",
            AstrometricConventions.Refraction, AstrometricConventions.PixelCoordinates, "enu-rotation-vector-radians-then-log-focal-scale"), u.Conventions);

        Assert.AreEqual(f.Result.Associations.Count(a => !a.Verification), u.Centroid.FittingStars);
        Assert.AreEqual("consistent-with-residuals-unvalidated-probability", u.Centroid.Status);
        Assert.AreEqual(Sigma, u.Centroid.MedianMajorSigmaPixels!.Value, 1e-12);
        Assert.AreEqual("consistent", u.Validity.Status);
        Assert.IsLessThan(u.Validity.ChiSquareLimit!.Value, u.Validity.ChiSquare!.Value); Assert.IsLessThan(u.Validity.HeldOutChiSquareLimit!.Value, u.Validity.HeldOutChiSquare!.Value);
        Assert.IsLessThan(.001, u.Estimate!.OffsetFromAcceptedDegrees);

        Assert.AreEqual(AstrometricUncertaintyEstimator.Available, u.ConditionalFit.Status); Assert.HasCount(16, u.ConditionalFit.Covariance!);
        Assert.IsTrue(u.ConditionalFit.BoresightStandardErrorDegrees is > 0 and < .01 && u.ConditionalFit.RollStandardErrorDegrees is > 0 and < .01);
        Assert.AreEqual("declared-zero-budget", u.Systematic.ReasonCode); Assert.IsTrue(u.Systematic.Covariance!.All(v => v == 0));
        Assert.AreEqual("shared-calibration-covariance-not-supplied", u.SharedCalibration.ReasonCode);
        Assert.AreEqual(AstrometricUncertaintyEstimator.Withheld, u.Total.Status); Assert.IsNull(u.Total.Covariance);
        Assert.AreEqual("clock-facts-not-supplied", u.ClockComponent.ReasonCode);

        Assert.AreEqual(u.IdentitySha256, Estimate(f, Covariances(f, Sigma)).IdentitySha256, "Deterministic for identical inputs.");
        Assert.AreNotEqual(u.IdentitySha256, Estimate(f, Covariances(f, Sigma), options: new(SystematicPixelSigma: .001)).IdentitySha256);
        Assert.AreNotEqual(u.MeasurementInputIdentitySha256, Estimate(f, Covariances(f, Sigma * 1.01)).MeasurementInputIdentitySha256);
    }

    [TestMethod]
    public void SharedCalibration_CompletesTotalAndCrossCovarianceUsesOneSensitivity()
    {
        var f = Accepted.Value; var shared = Shared(f.Calibration.Projection);
        var u = Estimate(f, Covariances(f, Sigma), shared: shared, readout: FullFrame);
        Assert.AreEqual(AstrometricUncertaintyEstimator.Available, u.SharedCalibration.Status, u.SharedCalibration.ReasonCode);
        Assert.AreEqual(shared.IdentitySha256, u.SharedCalibration.CalibrationCovarianceIdentitySha256);
        Assert.AreEqual(AstrometricUncertaintyEstimator.Available, u.Total.Status);
        for (var i = 0; i < 16; i++)
            Assert.AreEqual(u.ConditionalFit.Covariance![i] + u.Systematic.Covariance![i] + u.SharedCalibration.Pose.Covariance![i], u.Total.Covariance![i], 1e-24);
        var cross = AstrometricUncertaintyEstimator.CrossCovariance(u, u, shared);
        for (var i = 0; i < 16; i++) Assert.AreEqual(u.SharedCalibration.Pose.Covariance![i], cross[i], 1e-12 * Math.Abs(u.SharedCalibration.Pose.Covariance[i]) + 1e-30);
        Assert.IsGreaterThan(u.ConditionalFit.FocalScaleStandardError!.Value, u.Total.FocalScaleStandardError!.Value);

        var other = Shared(f.Calibration.Projection, 2);
        Assert.Throws<ArgumentException>(() => AstrometricUncertaintyEstimator.CrossCovariance(u, u, other));
        Assert.Throws<ArgumentException>(() => AstrometricUncertaintyEstimator.CrossCovariance(u, Estimate(f, Covariances(f, Sigma)), shared));
    }

    [TestMethod]
    public void IncompatibleReuse_IsRejectedRatherThanPropagated()
    {
        var f = Accepted.Value; var covariances = Covariances(f, Sigma);
        var moved = Shared(f.Calibration.Projection with { PrincipalPointX = f.Calibration.Projection.PrincipalPointX + .5 });
        Assert.Throws<ArgumentException>(() => Estimate(f, covariances, shared: moved, readout: FullFrame));
        Assert.Throws<ArgumentException>(() => Estimate(f, covariances, shared: Shared(f.Calibration.Projection)));
        Assert.Throws<ArgumentException>(() => AstrometricUncertaintyEstimator.Estimate(f.Calibration, f.Catalog, new(MinimumFocalScale: .95), f.Result, f.Detections,
            covariances, AstrometricClockFacts.NotSupplied));
        Assert.Throws<ArgumentException>(() => Estimate(f, [.. covariances, new(f.Detections.Max(d => d.Index) + 1, 1, 0, 1)]));
        Assert.Throws<ArgumentException>(() => Estimate(f, [.. covariances.Skip(1), covariances[1]]));
        Assert.Throws<ArgumentException>(() => Estimate(f, covariances, clock: new("kernel-adjtimex", AstrometricClockSynchronization.Synchronized, -1, null)));
        Assert.Throws<ArgumentException>(() => new AstrometricCalibrationCovariance(AstrometricTestFixture.Hash("s"), f.Calibration.Projection,
            new(OpticalCalibrationCovariance.ResidualScaledMarginal, [OpticalCalibrationCovariance.RadialK1, OpticalCalibrationCovariance.RadialK1], [1, 0, 0, 1])));
        Assert.Throws<ArgumentException>(() => new AstrometricCalibrationCovariance(AstrometricTestFixture.Hash("s"), f.Calibration.Projection,
            new(OpticalCalibrationCovariance.ResidualScaledMarginal, [OpticalCalibrationCovariance.PrincipalPointX, OpticalCalibrationCovariance.PrincipalPointY], [1, 2, 2, 1])));
    }

    [TestMethod]
    public void UnsupportedCases_WithholdEveryComponentWithActionableReasons()
    {
        var f = Accepted.Value;
        var missing = Estimate(f, []);
        Assert.AreEqual("centroid-covariance-missing", missing.ReasonCode); Assert.IsFalse(missing.IsAvailable);
        Assert.IsTrue(new[] { missing.ConditionalFit, missing.Systematic, missing.SharedCalibration.Pose, missing.Total }.All(c => c.Covariance is null && c.Status == AstrometricUncertaintyEstimator.Withheld));
        Assert.AreEqual("insufficient-fitting-stars", Estimate(f, Covariances(f, Sigma), options: new(MinimumFittingStars: 10000)).ReasonCode);

        // Claimed centroid covariance ten times too small: residuals expose it, so nothing is published.
        var optimistic = Estimate(f, Covariances(f, Sigma / 10));
        Assert.AreEqual("model-invalid-residual-excess", optimistic.ReasonCode); Assert.AreEqual("inconsistent-with-residuals", optimistic.Centroid.Status);
        Assert.IsNotNull(optimistic.Estimate); Assert.IsNull(optimistic.ConditionalFit.Covariance);
        // The same claim with a declared systematic floor of the actual size is consistent again.
        Assert.IsTrue(Estimate(f, Covariances(f, Sigma / 10), options: new(SystematicPixelSigma: Sigma)).IsAvailable);

        var rejected = AstrometricSolver.Solve(f.Result.Assessment.Frame, f.Calibration, f.Catalog, f.Detections.Take(5).ToArray());
        Assert.IsFalse(rejected.Assessment.HasMeasuredMapping);
        var notAccepted = AstrometricUncertaintyEstimator.Estimate(f.Calibration, f.Catalog, new(), rejected, f.Detections.Take(5).ToArray(), [], AstrometricClockFacts.NotSupplied);
        Assert.AreEqual("assessment-not-accepted", notAccepted.ReasonCode);
    }

    [TestMethod]
    public void ClockFacts_BoundHorizontalPoseOnlyAndNeverEnterTheTotal()
    {
        var f = Accepted.Value; var covariances = Covariances(f, Sigma); var shared = Shared(f.Calibration.Projection);
        var none = Estimate(f, covariances, shared: shared, readout: FullFrame);
        var synchronized = Estimate(f, covariances, shared: shared, readout: FullFrame,
            clock: new("kernel-adjtimex", AstrometricClockSynchronization.Synchronized, .2, .05));
        Assert.AreEqual("bounded", synchronized.ClockComponent.Status);
        Assert.AreEqual(.2 * 360.98564736629 / 86400, synchronized.ClockComponent.MaximumHorizontalRotationDegrees!.Value, 1e-15);
        Assert.AreEqual(.05 * 360.98564736629 / 86400, synchronized.ClockComponent.EstimatedHorizontalRotationDegrees!.Value, 1e-15);
        Assert.IsFalse(synchronized.ClockComponent.AffectsEquatorialMapping);
        CollectionAssert.AreEqual(none.Total.Covariance!.ToArray(), synchronized.Total.Covariance!.ToArray());
        Assert.AreNotEqual(none.IdentitySha256, synchronized.IdentitySha256);
        foreach (var (state, code) in new[] { (AstrometricClockSynchronization.Unsynchronized, "clock-unsynchronized"), (AstrometricClockSynchronization.Unknown, "clock-synchronization-unknown") })
        {
            var u = Estimate(f, covariances, clock: new("kernel-adjtimex", state, .2, .05));
            Assert.AreEqual(code, u.ClockComponent.ReasonCode); Assert.IsNull(u.ClockComponent.MaximumHorizontalRotationDegrees); Assert.IsTrue(u.IsAvailable);
        }
        Assert.AreEqual("clock-maximum-error-unreported", Estimate(f, covariances, clock: new("kernel-adjtimex", AstrometricClockSynchronization.Synchronized, null, null)).ClockComponent.ReasonCode);
    }

    private static AstrometricFrameUncertainty Estimate(Solved f, IReadOnlyList<AstrometricPixelCovariance> covariances, AstrometricClockFacts? clock = null,
        AstrometricCalibrationCovariance? shared = null, FrameReadoutDescriptor? readout = null, AstrometricUncertaintyOptions? options = null) =>
        AstrometricUncertaintyEstimator.Estimate(f.Calibration, f.Catalog, new(), f.Result, f.Detections, covariances, clock ?? AstrometricClockFacts.NotSupplied,
            readout, shared, options);

    private static AstrometricPixelCovariance[] Covariances(Solved f, double sigma) => [.. f.Detections.Select(d => new AstrometricPixelCovariance(d.Index, sigma * sigma, 0, sigma * sigma))];

    private static AstrometricCalibrationCovariance Shared(ProjectionContext native, double scale = 1) => new(AstrometricTestFixture.Hash("session"), native,
        new(OpticalCalibrationCovariance.ResidualScaledMarginal,
            [OpticalCalibrationCovariance.LogFocalScale, OpticalCalibrationCovariance.PrincipalPointX, OpticalCalibrationCovariance.PrincipalPointY],
            [scale * 4e-8, 0, 0, 0, scale * .0025, scale * .0005, 0, scale * .0005, scale * .0025]));
}
