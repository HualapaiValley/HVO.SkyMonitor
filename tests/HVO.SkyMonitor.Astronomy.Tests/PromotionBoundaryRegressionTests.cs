namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PromotionBoundaryRegressionTests
{
    [TestMethod]
    [DataRow(0d, 1d)]
    [DataRow(-1d, 1d)]
    [DataRow(double.Epsilon, double.MaxValue)]
    public void UnavailableConditionNumber_PreservesReasonCodedSerializableRejection(double smallest, double largest)
    {
        var condition = OpticalCalibrationSession.SpectralConditionNumber(smallest, largest);
        var diagnostics = new OpticalCalibrationDiagnostics(20, 3, 40, 12, 0, 0, 4, 6, condition, 1, true, []);
        var hash = AstrometricTestFixture.Hash("conditioning-rejection");
        var result = new OpticalCalibrationResult(OpticalCalibrationStatus.Rejected, "ill-conditioned", "Optical parameters are not independently constrained.",
            ["ill-conditioned"], AstrometricTestFixture.Truth(), null, [], [], [], diagnostics,
            hash, hash, hash, hash, hash, new(0, 0, 0, 0));

        Assert.AreEqual(OpticalCalibrationStatus.Rejected, result.Status);
        Assert.Contains("ill-conditioned", result.Rejections);
        Assert.IsNull(result.Diagnostics.ConditionNumber);
        Assert.AreEqual(64, result.IdentitySha256.Length);
    }

    [TestMethod]
    public void FiniteConditionNumber_RetainsItsValue()
    {
        Assert.AreEqual(2d, OpticalCalibrationSession.SpectralConditionNumber(2, 4));
    }

    [TestMethod]
    [DataRow(1d)]
    [DataRow(1.0001d)]
    public void ResidualDiagnostics_PhysicalFocalBoundaryRetainsMappingAndReportsConditioning(double boundaryFactor)
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective, 1700, 1700, 1000, 1000, 3400, 3400,
            ProjectionAperture.Rectangular, null, BoresightAltitudeDegrees: 90, BoresightAzimuthDegrees: 0,
            RollDegrees: 0, RadialDistortionK1: -.02);
        var scale = AstrometricSolverCore.MinimumPhysicalScale(SolverOptics.From(projection)) * boundaryFactor;
        var calibrated = new AstrometricCalibration(projection, "physical-boundary", AstrometricTestFixture.Hash("readout"));
        var actual = projection with { FocalLengthXPixels = 1000 * scale, FocalLengthYPixels = 1000 * scale };
        actual.Validate();
        var projector = ProjectorFactory.Create(actual);
        var catalog = AstrometricTestFixture.Catalog();
        var stars = catalog.Stars.Select(star => (Star: star, Horizontal: AstrometricTestFixture.Horizontal(star, AstrometricTestFixture.Utc)))
            .Where(p => p.Horizontal.AltitudeDegrees > 0).Select(p => (p.Star, Pixel: projector.Project(p.Horizontal)))
            .Where(p => p.Pixel is { } pixel && actual.ContainsSample(pixel.X, pixel.Y)).Take(16).ToArray();
        Assert.HasCount(16, stars);
        var detections = stars.Select((p, i) => new AstrometricDetection(i, p.Pixel!.Value, 1)).ToArray();
        var associations = stars.Select((p, i) => new AstrometricAssociation(p.Star.Id, i, 0, i >= 12)).ToArray();
        var frame = AstrometricTestFixture.Frame(AstrometricTestFixture.Utc);
        var direction = CoordinateTransforms.HorizontalToEquatorial(new(90, 0), frame.MidpointUtc,
            frame.Observer.LatitudeDegrees, frame.Observer.LongitudeDegrees);
        var settings = new AstrometricSolverOptions();
        var assessment = new AstrometricFrameAssessment(AstrometricFrameAssessment.CurrentSchemaVersion, string.Empty,
            frame, calibrated.IdentitySha256, catalog.IdentitySha256, catalog.SelectionIdentitySha256,
            AstrometricConventions.SolverVersion, settings.IdentitySha256, AstrometricConventions.CoordinateModel,
            AstrometricConventions.Refraction, AstrometricConventions.PixelCoordinates, AstrometricSolveMode.Blind,
            AstrometricAssessmentStatus.Accepted, "accepted", "Supported distorted-perspective mapping.",
            new(90, 0, 0, scale, direction.RightAscensionHours, direction.DeclinationDegrees),
            new(86, 12, 4, 12, 0, 0, .8, .8), AstrometricIdentity.Hash(associations), null);
        assessment = assessment with { IdentitySha256 = AstrometricIdentity.Hash(assessment) };
        var solved = new AstrometricSolveResult(assessment, associations, new(0, 0, 0, 0, 0, 0, false));

        var diagnostics = AstrometricResidualAnalyzer.Analyze(calibrated, catalog, settings, solved, detections);

        Assert.IsTrue(diagnostics.HasMeasuredMapping);
        Assert.HasCount(16, diagnostics.Residuals);
        Assert.IsNotNull(diagnostics.Conditioning);
        if (boundaryFactor == 1)
        {
            Assert.AreEqual("unavailable-derivative-boundary", diagnostics.Conditioning.Status);
            Assert.IsNull(diagnostics.Conditioning.ConditionNumber);
        }
        else
        {
            Assert.AreEqual("well-conditioned", diagnostics.Conditioning.Status);
            Assert.IsNotNull(diagnostics.Conditioning.ConditionNumber);
            Assert.IsTrue(double.IsFinite(diagnostics.Conditioning.ConditionNumber.Value));
        }
        Assert.AreEqual(64, diagnostics.IdentitySha256.Length);
    }
}
