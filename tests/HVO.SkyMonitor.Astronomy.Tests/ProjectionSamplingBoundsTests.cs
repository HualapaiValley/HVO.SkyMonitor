namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class ProjectionSamplingBoundsTests
{
    [TestMethod]
    [DataRow(ProjectionModel.Perspective, 0d)]
    [DataRow(ProjectionModel.EquidistantFisheye, 0d)]
    [DataRow(ProjectionModel.EquisolidFisheye, 0d)]
    [DataRow(ProjectionModel.OrthographicFisheye, 0d)]
    [DataRow(ProjectionModel.StereographicFisheye, 0d)]
    [DataRow(ProjectionModel.Perspective, .01)]
    [DataRow(ProjectionModel.Perspective, -.001)]
    [DataRow(ProjectionModel.EquidistantFisheye, .01)]
    [DataRow(ProjectionModel.EquisolidFisheye, .01)]
    [DataRow(ProjectionModel.OrthographicFisheye, .01)]
    [DataRow(ProjectionModel.StereographicFisheye, .01)]
    public void BoundDominatesMeasuredLocalPixelMotion(ProjectionModel model, double distortion)
    {
        var projection = new ProjectionContext(model, 256, 256, 100, 100, 512, 512,
            model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
            model == ProjectionModel.Perspective ? null : model == ProjectionModel.OrthographicFisheye ? 100 : 120, RollDegrees: 23, HorizontalFlip: true,
            RadialDistortionK1: distortion);
        var center = new AltAzPoint(30, 20);
        var bound = ProjectionSamplingBounds.MaximumPixelsPerRadian(projection, center, 2 * Math.PI / 180);
        Assert.IsTrue(double.IsFinite(bound) && bound >= 100);
        var projector = ProjectorFactory.Create(projection);
        foreach (var altitude in new[] { 29d, 30, 31 })
            foreach (var azimuth in new[] { 19d, 20, 21 })
                foreach (var direction in new[] { new AltAzPoint(altitude + 1e-4, azimuth), new AltAzPoint(altitude, azimuth + 1e-4) })
                {
                    var start = new AltAzPoint(altitude, azimuth);
                    var first = projector.Project(start);
                    var last = projector.Project(direction);
                    Assert.IsNotNull(first);
                    Assert.IsNotNull(last);
                    var angularChord = (CameraBasis.FromHorizontal(direction) - CameraBasis.FromHorizontal(start)).Length;
                    var pixelDistance = Math.Sqrt(Math.Pow(last.Value.X - first.Value.X, 2) + Math.Pow(last.Value.Y - first.Value.Y, 2));
                    Assert.IsTrue(pixelDistance / angularChord <= bound * (1 + 1e-6), $"{model}: derivative exceeds bound.");
                }
    }

    [TestMethod]
    public void OutsideCapsSingularitiesAndInvalidRadiiAreExplicit()
    {
        var projection = new ProjectionContext(ProjectionModel.Perspective, 32, 32, 4000, 4000, 64, 64, ProjectionAperture.Rectangular);
        Assert.AreEqual(0, ProjectionSamplingBounds.MaximumPixelsPerRadian(projection, new(20, 0), .01));
        foreach (var radius in new[] { 0, -1, double.NaN, double.PositiveInfinity, Math.PI + .1 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ProjectionSamplingBounds.MaximumPixelsPerRadian(projection, new(90, 0), radius));
        var singular = new ProjectionContext(ProjectionModel.EquidistantFisheye, 400, 400, 100, 100, 800, 800,
            ProjectionAperture.Circular, 100 * Math.PI);
        Assert.AreEqual(double.PositiveInfinity, ProjectionSamplingBounds.MaximumPixelsPerRadian(singular, new(-90, 0), .01));
        Assert.AreEqual(4000, ProjectionSamplingBounds.MaximumPixelsPerRadian(projection, new(90, 0), 1e-14), 1e-8);
    }
}
