using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstrometricUncertaintyQualificationIsolationTests
{
    [TestMethod]
    public void LongExposureDiagnostic_IsRejectedByInputValidationAndAsSecondCrossCovarianceOperand()
    {
        var catalog = AstrometricTestFixture.Catalog();
        var truth = AstrometricTestFixture.Truth();
        var calibration = AstrometricTestFixture.Calibration(truth);
        var midpoint = AstrometricTestFixture.Utc;
        var detections = AstrometricTestFixture.Centroids(catalog, truth, midpoint);
        const double centroidVariance = .04 * .04 / 12;
        var covariances = detections.Select(d => new AstrometricPixelCovariance(d.Index, centroidVariance, 0, centroidVariance)).ToArray();
        var readout = new FrameReadoutDescriptor(512, 512, 0, 0, 512, 512, 1, 1, FrameBinningAlgorithm.IdentityV1, null, null);
        var shared = new AstrometricCalibrationCovariance(AstrometricTestFixture.Hash("session"), calibration.Projection,
            new(OpticalCalibrationCovariance.ResidualScaledMarginal, [OpticalCalibrationCovariance.LogFocalScale], [4e-8]));
        var solverOptions = new AstrometricSolverOptions();
        var options = new AstrometricUncertaintyOptions();
        var clock = AstrometricClockFacts.NotSupplied;

        AstrometricSolveResult WithExposure(long ticks) => AstrometricSolver.Solve(
            AstrometricTestFixture.Frame(midpoint, TimeSpan.FromTicks(ticks).TotalSeconds), calibration, catalog, detections, solverOptions);

        var longExposure = WithExposure(TimeSpan.TicksPerSecond * 20);
        var diagnostic = AstrometricUncertaintyEstimator.EstimateForLongExposureQualification(calibration, catalog,
            solverOptions, longExposure, detections, covariances, readout, shared, options);
        Assert.AreEqual(AstrometricUncertaintyEstimator.QualificationSchema, diagnostic.SchemaVersion);
        Assert.IsTrue(diagnostic.IsAvailable, diagnostic.Reason);
        Assert.AreEqual(AstrometricUncertaintyEstimator.Available, diagnostic.SharedCalibration.Status);
        Assert.ThrowsExactly<ArgumentException>(() => ValidateInputs(diagnostic, longExposure));

        var shortExposure = WithExposure(TimeSpan.TicksPerSecond);
        var production = AstrometricUncertaintyEstimator.Estimate(calibration, catalog, solverOptions, shortExposure,
            detections, covariances, clock, readout, shared, options);
        ValidateInputs(production, shortExposure);
        Assert.HasCount(16, AstrometricUncertaintyEstimator.CrossCovariance(production, production, shared));
        Assert.ThrowsExactly<ArgumentException>(() => AstrometricUncertaintyEstimator.CrossCovariance(production, diagnostic, shared));

        // Keep the exposure eligible and the evidence identity valid so only the schema can cause rejection.
        var qualificationSchemaOnly = production with { SchemaVersion = diagnostic.SchemaVersion, IdentitySha256 = string.Empty };
        qualificationSchemaOnly = qualificationSchemaOnly with { IdentitySha256 = AstrometricIdentity.Hash(qualificationSchemaOnly) };
        Assert.ThrowsExactly<ArgumentException>(() => ValidateInputs(qualificationSchemaOnly, shortExposure));
        Assert.ThrowsExactly<ArgumentException>(() =>
            AstrometricUncertaintyEstimator.CrossCovariance(production, qualificationSchemaOnly, shared));

        void ValidateInputs(AstrometricFrameUncertainty uncertainty, AstrometricSolveResult solve) =>
            AstrometricUncertaintyEvidence.ValidateInputs(uncertainty, solve.Assessment, calibration, detections, covariances,
                solve.Associations, clock, readout, solverOptions, options, shared);
    }
}
