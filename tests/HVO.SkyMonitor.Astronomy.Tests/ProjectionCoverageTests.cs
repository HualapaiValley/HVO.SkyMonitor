using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProjectionCoverageTests
{
    [TestMethod]
    public void UnifiedContext_RejectsEveryInvalidModelIntrinsicApertureAndOrientationBranch()
    {
        var validCircle = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 50, 40, 30, 30, 100, 80,
            ProjectionAperture.Circular, 35);
        ProjectionContext[] invalid =
        [
            validCircle with { Model = (ProjectionModel)int.MaxValue },
            validCircle with { Aperture = (ProjectionAperture)int.MaxValue },
            validCircle with { PrincipalPointX = double.NaN },
            validCircle with { PrincipalPointY = double.PositiveInfinity },
            validCircle with { FocalLengthXPixels = double.NaN },
            validCircle with { FocalLengthXPixels = 0 },
            validCircle with { FocalLengthYPixels = double.PositiveInfinity },
            validCircle with { FocalLengthYPixels = 0 },
            validCircle with { WidthPixels = 0 },
            validCircle with { HeightPixels = 0 },
            validCircle with { BoresightAltitudeDegrees = -91 },
            validCircle with { BoresightAltitudeDegrees = 91 },
            validCircle with { BoresightAzimuthDegrees = double.NaN },
            validCircle with { RollDegrees = double.NaN },
            validCircle with { ImageCircleRadiusPixels = null },
            validCircle with { ImageCircleRadiusPixels = double.NaN },
            validCircle with { ImageCircleRadiusPixels = 0 },
            validCircle with { Aperture = ProjectionAperture.Rectangular, ImageCircleRadiusPixels = null },
            new(ProjectionModel.Perspective, 50, 40, 30, 30, 100, 80,
                ProjectionAperture.Rectangular, 35),
            new(ProjectionModel.Perspective, 50, 40, 30, 30, 100, 80,
                ProjectionAperture.Circular, 35),
            validCircle with { FocalLengthYPixels = 31 },
            validCircle with { ImageCircleRadiusPixels = 100 },
            validCircle with { Model = ProjectionModel.EquisolidFisheye, ImageCircleRadiusPixels = 61 },
            validCircle with { Model = ProjectionModel.OrthographicFisheye, ImageCircleRadiusPixels = 31 }
        ];

        validCircle.Validate();
        foreach (var context in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(context.Validate);
        }
    }

    [TestMethod]
    public void UnifiedContext_ApertureContainsAndNormalizesCircularAndRectangularSamples()
    {
        var circle = new ProjectionContext(
            ProjectionModel.EquidistantFisheye, 50, 40, 30, 30, 100, 80,
            ProjectionAperture.Circular, 35);
        var rectangle = new ProjectionContext(
            ProjectionModel.Perspective, 40, 30, 30, 30, 100, 80,
            ProjectionAperture.Rectangular);

        Assert.IsTrue(circle.ContainsSample(50, 40));
        Assert.IsFalse(circle.ContainsSample(90, 40));
        Assert.IsFalse(circle.ContainsSample(-1, 40));
        Assert.IsFalse(circle.ContainsSample(101, 40));
        Assert.IsFalse(circle.ContainsSample(50, -1));
        Assert.IsFalse(circle.ContainsSample(50, 81));
        Assert.AreEqual(0, circle.NormalizedRadiusSquared(50, 40));
        Assert.AreEqual(1, circle.NormalizedRadiusSquared(85, 40), 1e-12);
        Assert.IsTrue(rectangle.ContainsSample(0, 0));
        Assert.IsTrue(rectangle.ContainsSample(100, 80));
        Assert.IsTrue(rectangle.NormalizedRadiusSquared(100, 80) <= 1);
    }

    [TestMethod]
    public void CoordinateTransform_RejectsEveryInvalidInputDomain()
    {
        EquatorialPoint[] invalidEquatorial =
        [
            new(double.NaN, 0), new(-1, 0), new(24, 0),
            new(0, double.NaN), new(0, -91), new(0, 91)
        ];

        foreach (var equatorial in invalidEquatorial)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateTransforms.EquatorialToHorizontal(
                equatorial, DateTimeOffset.UnixEpoch, 0, 0));
        }

        foreach (var latitude in new[] { double.NaN, -91, 91 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateTransforms.EquatorialToHorizontal(
                new EquatorialPoint(0, 0), DateTimeOffset.UnixEpoch, latitude, 0));
        }

        foreach (var longitude in new[] { double.NaN, -181, 181 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => CoordinateTransforms.EquatorialToHorizontal(
                new EquatorialPoint(0, 0), DateTimeOffset.UnixEpoch, 0, longitude));
        }
    }

    [TestMethod]
    public void EquidistantContext_RejectsInvalidFiniteRangesAndSensorCombinations()
    {
        EquidistantProjectionContext[] invalid =
        [
            new(double.NaN, 1, 1, 1), new(1, double.NaN, 1, 1),
            new(1, 1, double.NaN, 1), new(1, 1, 1, double.NaN),
            new(1, 1, 1, 1, double.NaN), new(1, 1, 1, 1, -91), new(1, 1, 1, 1, 91),
            new(1, 1, 1, 1, BoresightAzimuthDegrees: double.NaN),
            new(1, 1, 1, 1, RollDegrees: double.NaN),
            new(1, 1, 0, 1), new(1, 1, 1, 0),
            new(1, 1, 1, 1, WidthPixels: -1), new(1, 1, 1, 1, HeightPixels: -1),
            new(1, 1, 1, 1, WidthPixels: 10), new(1, 1, 1, 1, HeightPixels: 10)
        ];

        foreach (var context in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(context.Validate);
        }
    }

    [TestMethod]
    public void EquidistantContext_AllowsOffSensorPrincipalPointForCroppedReadout()
    {
        var context = new EquidistantProjectionContext(
            142, 60, 94.83075, 148.96, WidthPixels: 120, HeightPixels: 120);

        context.Validate();
        var projector = new EquidistantFisheyeProjector(context);
        var direction = projector.Unproject(new PixelPoint(119, 60));

        Assert.IsNotNull(direction);
        var roundTrip = projector.Project(direction.Value);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(119, roundTrip.Value.X, 1e-12);
        Assert.AreEqual(60, roundTrip.Value.Y, 1e-12);
    }

    [TestMethod]
    public void EquidistantProjector_RejectsOffSensorAndInvalidInverseDomains()
    {
        var sensorProjector = new EquidistantFisheyeProjector(new(
            50, 50, 100, 400, WidthPixels: 100, HeightPixels: 100));
        Assert.IsNull(sensorProjector.Project(new AltAzPoint(0, 90)));

        var circleProjector = new EquidistantFisheyeProjector(new(0, 0, 1, 5));
        Assert.IsNull(circleProjector.Unproject(new PixelPoint(6, 0)));
        Assert.IsNull(circleProjector.Unproject(new PixelPoint(0, double.NegativeInfinity)));

        var excessiveAngleProjector = new EquidistantFisheyeProjector(new(0, 0, 1, 10));
        Assert.IsNull(excessiveAngleProjector.Unproject(new PixelPoint(4, 0)));
    }

    [TestMethod]
    public void Factory_CreatesEveryModelAndRejectsNonFisheyeModel()
    {
        var context = new EquidistantProjectionContext(10, 10, 10, 20);

        Assert.IsInstanceOfType<EquidistantFisheyeProjector>(
            ProjectorFactory.CreateFisheye(ProjectionModel.EquidistantFisheye, context));
        Assert.IsNotNull(ProjectorFactory.CreateFisheye(ProjectionModel.EquisolidFisheye, context));
        Assert.IsNotNull(ProjectorFactory.CreateFisheye(ProjectionModel.OrthographicFisheye, context));
        Assert.IsNotNull(ProjectorFactory.CreateFisheye(ProjectionModel.StereographicFisheye, context));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectorFactory.CreateFisheye(ProjectionModel.Perspective, context));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProjectorFactory.CreateFisheye((ProjectionModel)int.MaxValue, context));
    }

    [TestMethod]
    public void PerspectiveContext_RejectsInvalidIntrinsicsAndDimensions()
    {
        PerspectiveProjectionContext[] invalid =
        [
            new(double.NaN, 1, 1, 1, 1, 1), new(1, double.NaN, 1, 1, 1, 1),
            new(1, 1, double.NaN, 1, 1, 1), new(1, 1, 1, double.NaN, 1, 1),
            new(1, 1, 0, 1, 1, 1), new(1, 1, 1, 0, 1, 1),
            new(1, 1, 1, 1, 0, 1), new(1, 1, 1, 1, 1, 0)
        ];

        foreach (var context in invalid)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ProjectorFactory.CreatePerspective(context));
        }
    }

    [TestMethod]
    public void PerspectiveProjector_HandlesCenterCardinalsHorizonAndSensorRejection()
    {
        var projector = ProjectorFactory.CreatePerspective(new(50, 50, 25, 25, 100, 100));

        var zenith = projector.Project(new AltAzPoint(90, -720));
        Assert.AreEqual(new PixelPoint(50, 50), zenith);
        Assert.AreEqual(new AltAzPoint(90, 0), projector.Unproject(zenith!.Value));

        Assert.IsNull(projector.Project(new AltAzPoint(double.NaN, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(0, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(-1, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(10, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(10, 90)));
        Assert.IsNull(projector.Project(new AltAzPoint(10, 180)));
        Assert.IsNull(projector.Project(new AltAzPoint(10, 270)));

        var north = projector.Project(new AltAzPoint(60, 0));
        Assert.IsNotNull(north);
        var roundTrip = projector.Unproject(north.Value);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(60, roundTrip.Value.AltitudeDegrees, 1e-10);
        Assert.AreEqual(0, roundTrip.Value.AzimuthDegrees, 1e-10);
    }

    [TestMethod]
    public void PerspectiveProjector_UnprojectRejectsEveryInvalidSensorDomain()
    {
        var projector = ProjectorFactory.CreatePerspective(new(50, 50, 25, 25, 100, 100));
        PixelPoint[] invalid =
        [
            new(double.NaN, 0), new(0, double.PositiveInfinity),
            new(-double.Epsilon, 0), new(100, 0), new(0, -double.Epsilon), new(0, 100)
        ];

        foreach (var pixel in invalid)
        {
            Assert.IsNull(projector.Unproject(pixel));
        }
    }

    [TestMethod]
    [DataRow(ProjectionModel.EquisolidFisheye)]
    [DataRow(ProjectionModel.OrthographicFisheye)]
    [DataRow(ProjectionModel.StereographicFisheye)]
    public void RadialProjector_HandlesCenterInvalidDomainsAndCircleRejection(ProjectionModel model)
    {
        var projector = ProjectorFactory.CreateFisheye(model, new(50, 50, 20, 15));

        Assert.AreEqual(new PixelPoint(50, 50), projector.Project(new AltAzPoint(90, 0)));
        Assert.AreEqual(new AltAzPoint(90, 0), projector.Unproject(new PixelPoint(50, 50)));
        Assert.IsNull(projector.Project(new AltAzPoint(91, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(0, double.NaN)));
        Assert.IsNull(projector.Project(new AltAzPoint(0, 0)));
        Assert.IsNull(projector.Unproject(new PixelPoint(double.NaN, 0)));
        Assert.IsNull(projector.Unproject(new PixelPoint(0, double.PositiveInfinity)));
        Assert.IsNull(projector.Unproject(new PixelPoint(66, 50)));
    }

    [TestMethod]
    public void OrthographicProjector_RejectsInverseRadiusOutsideOpticalDomain()
    {
        var projector = ProjectorFactory.CreateFisheye(
            ProjectionModel.OrthographicFisheye, new EquidistantProjectionContext(0, 0, 10, 20));

        Assert.IsNull(projector.Unproject(new PixelPoint(15, 0)));
    }
}
