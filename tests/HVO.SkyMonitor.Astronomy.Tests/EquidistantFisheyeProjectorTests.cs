using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class EquidistantFisheyeProjectorTests
{
    private static readonly EquidistantFisheyeProjector Projector = new(new(100, 100, 100, Math.PI * 50));

    [TestMethod]
    public void Project_AtZenith_ReturnsPrincipalPoint()
    {
        var pixel = Projector.Project(new AltAzPoint(90, 0));
        Assert.AreEqual(new PixelPoint(100, 100), pixel);
    }

    [TestMethod]
    public void ProjectAndUnproject_AtCardinalDirection_RoundTrips()
    {
        var direction = new AltAzPoint(60, 90);
        var pixel = Projector.Project(direction);
        Assert.IsNotNull(pixel);
        var roundTrip = Projector.Unproject(pixel.Value);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(direction.AltitudeDegrees, roundTrip.Value.AltitudeDegrees, 1e-10);
        Assert.AreEqual(direction.AzimuthDegrees, roundTrip.Value.AzimuthDegrees, 1e-10);
    }

    [TestMethod]
    public void Project_BelowImageCircle_ReturnsNull()
    {
        Assert.IsNull(Projector.Project(new AltAzPoint(-1, 0)));
    }

    [TestMethod]
    public void Asi174Fixture_ProjectsHorizonToExactContinuousEdgeCoordinates()
    {
        const double radius = 595.84;
        var projector = new EquidistantFisheyeProjector(new(
            968, 608, radius / (Math.PI / 2), radius, WidthPixels: 1936, HeightPixels: 1216));
        (AltAzPoint Direction, PixelPoint Pixel)[] fixtures =
        [
            (new(90, 0), new(968, 608)),
            (new(0, 0), new(968, 12.16)),
            (new(0, 90), new(1563.84, 608)),
            (new(0, 180), new(968, 1203.84)),
            (new(0, 270), new(372.16, 608))
        ];

        foreach (var fixture in fixtures)
        {
            var actual = projector.Project(fixture.Direction);
            Assert.IsNotNull(actual);
            Assert.AreEqual(fixture.Pixel.X, actual.Value.X, 1e-9);
            Assert.AreEqual(fixture.Pixel.Y, actual.Value.Y, 1e-9);
            var roundTrip = projector.Unproject(actual.Value);
            Assert.IsNotNull(roundTrip);
            Assert.AreEqual(fixture.Direction.AltitudeDegrees, roundTrip.Value.AltitudeDegrees, 1e-9);
        }
    }

    [TestMethod]
    public void ArbitraryBoresight_ProjectsToPrincipalPointAndRoundTripsOffAxisDirection()
    {
        var projector = new EquidistantFisheyeProjector(new(320, 240, 200, 500, 37, 123, 28));

        Assert.AreEqual(new PixelPoint(320, 240), projector.Project(new AltAzPoint(37, 123)));
        var direction = new AltAzPoint(52, 151);
        var pixel = projector.Project(direction);
        Assert.IsNotNull(pixel);
        var roundTrip = projector.Unproject(pixel.Value);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(direction.AltitudeDegrees, roundTrip.Value.AltitudeDegrees, 1e-10);
        Assert.AreEqual(direction.AzimuthDegrees, roundTrip.Value.AzimuthDegrees, 1e-10);
    }

    [TestMethod]
    public void RollAndHorizontalFlip_ApplyExactImageAxisTransforms()
    {
        var rolled = new EquidistantFisheyeProjector(new(100, 100, 100, 200, RollDegrees: 90));
        var normal = new EquidistantFisheyeProjector(new(100, 100, 100, 200));
        var flipped = new EquidistantFisheyeProjector(new(100, 100, 100, 200, HorizontalFlip: true));

        var northRolled = rolled.Project(new AltAzPoint(60, 0));
        var eastNormal = normal.Project(new AltAzPoint(60, 90));
        var eastFlipped = flipped.Project(new AltAzPoint(60, 90));
        Assert.IsNotNull(northRolled);
        Assert.IsNotNull(eastNormal);
        Assert.IsNotNull(eastFlipped);
        Assert.AreEqual(100 + Math.PI * 100 / 6, northRolled.Value.X, 1e-10);
        Assert.AreEqual(200 - eastNormal.Value.X, eastFlipped.Value.X, 1e-10);
        Assert.AreEqual(eastNormal.Value.Y, eastFlipped.Value.Y, 1e-10);
    }

    [TestMethod]
    public void SensorEdgesAreInclusive_ButOffSensorAndInvalidDomainsReturnNull()
    {
        var projector = new EquidistantFisheyeProjector(new(50, 50, 100 / Math.PI, 100, WidthPixels: 100, HeightPixels: 100));

        Assert.IsNotNull(projector.Unproject(new PixelPoint(0, 50)));
        Assert.IsNotNull(projector.Unproject(new PixelPoint(100, 50)));
        Assert.IsNull(projector.Unproject(new PixelPoint(-double.Epsilon, 50)));
        Assert.IsNull(projector.Unproject(new PixelPoint(double.NaN, 50)));
        Assert.IsNull(projector.Project(new AltAzPoint(91, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(0, double.PositiveInfinity)));
    }

    [TestMethod]
    public void Context_InvalidOrientationOrPartialSensorBounds_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EquidistantFisheyeProjector(new(1, 1, 1, 1, 91)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EquidistantFisheyeProjector(new(1, 1, 1, 1, WidthPixels: 10)));
    }

    [TestMethod]
    [DataRow(ProjectionModel.EquisolidFisheye)]
    [DataRow(ProjectionModel.OrthographicFisheye)]
    [DataRow(ProjectionModel.StereographicFisheye)]
    public void FisheyeMappings_AtFortyFiveDegrees_RoundTrip(ProjectionModel model)
    {
        var projector = ProjectorFactory.CreateFisheye(model, new(100, 100, 100, 100));
        var direction = new AltAzPoint(45, 135);

        var pixel = projector.Project(direction);
        Assert.IsNotNull(pixel);
        var roundTrip = projector.Unproject(pixel.Value);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(direction.AltitudeDegrees, roundTrip.Value.AltitudeDegrees, 1e-10);
        Assert.AreEqual(direction.AzimuthDegrees, roundTrip.Value.AzimuthDegrees, 1e-10);
    }

    [TestMethod]
    public void PerspectiveProjector_AtCardinalDirection_RoundTrips()
    {
        var projector = ProjectorFactory.CreatePerspective(new(100, 100, 100, 100, 200, 200));
        var direction = new AltAzPoint(60, 90);

        var pixel = projector.Project(direction);
        Assert.IsNotNull(pixel);
        var roundTrip = projector.Unproject(pixel.Value);

        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(direction.AltitudeDegrees, roundTrip.Value.AltitudeDegrees, 1e-10);
        Assert.AreEqual(direction.AzimuthDegrees, roundTrip.Value.AzimuthDegrees, 1e-10);
    }

    [TestMethod]
    [DataRow(ProjectionModel.EquidistantFisheye)]
    [DataRow(ProjectionModel.EquisolidFisheye)]
    [DataRow(ProjectionModel.OrthographicFisheye)]
    [DataRow(ProjectionModel.StereographicFisheye)]
    [DataRow(ProjectionModel.Perspective)]
    public void UnifiedProjectionContext_AppliesArbitraryBoresightRollAndFlip(ProjectionModel model)
    {
        var context = model == ProjectionModel.Perspective
            ? new ProjectionContext(model, 320, 240, 220, 220, 640, 480, ProjectionAperture.Rectangular,
                BoresightAltitudeDegrees: 37, BoresightAzimuthDegrees: 123, RollDegrees: 19, HorizontalFlip: true)
            : new ProjectionContext(model, 320, 240, 180, 180, 640, 480, ProjectionAperture.Circular, 150,
                37, 123, 19, true);
        var projector = ProjectorFactory.Create(context);

        Assert.AreEqual(new PixelPoint(320, 240), projector.Project(new AltAzPoint(37, 123)));
        var direction = new AltAzPoint(48, 139);
        var pixel = projector.Project(direction);
        Assert.IsNotNull(pixel);
        var roundTrip = projector.Unproject(pixel.Value);
        Assert.IsNotNull(roundTrip);
        Assert.AreEqual(direction.AltitudeDegrees, roundTrip.Value.AltitudeDegrees, 1e-9);
        Assert.AreEqual(direction.AzimuthDegrees, roundTrip.Value.AzimuthDegrees, 1e-9);
    }

    [TestMethod]
    public void OrthographicProjector_RejectsRearHemisphereWithoutFolding()
    {
        var projector = ProjectorFactory.Create(new ProjectionContext(
            ProjectionModel.OrthographicFisheye, 100, 100, 100, 100, 200, 200,
            ProjectionAperture.Circular, 100));

        Assert.IsNotNull(projector.Project(new AltAzPoint(30, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(-30, 0)));
        Assert.IsNull(projector.Project(new AltAzPoint(-90, 0)));
    }
}
