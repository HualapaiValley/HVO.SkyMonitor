using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
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
    [DataRow(ProjectionModel.EquisolidFisheye)]
    [DataRow(ProjectionModel.OrthographicFisheye)]
    [DataRow(ProjectionModel.StereographicFisheye)]
    public void FisheyeMappings_AtFortyFiveDegrees_RoundTrip(ProjectionModel model)
    {
        var projector = ProjectorFactory.CreateFisheye(model, new(100, 100, 100, 200));
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
}
