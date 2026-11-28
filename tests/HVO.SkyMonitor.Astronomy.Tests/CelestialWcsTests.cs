namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class CelestialWcsTests
{
    [TestMethod]
    [DataRow(ProjectionModel.Perspective, "TAN", 0d)]
    [DataRow(ProjectionModel.EquidistantFisheye, "ARC", 0d)]
    [DataRow(ProjectionModel.EquidistantFisheye, "ZPN", 0.05)]
    [DataRow(ProjectionModel.EquisolidFisheye, "ZEA", 0d)]
    [DataRow(ProjectionModel.OrthographicFisheye, "SIN", 0d)]
    [DataRow(ProjectionModel.StereographicFisheye, "STG", 0d)]
    public void WcsReproducesNativeProjectionAtOffAxisPixels(ProjectionModel model, string code, double k1)
    {
        var projection = new ProjectionContext(model, 140.8, 158.3, 200,
            model == ProjectionModel.Perspective ? 190 : 200, 300, 320,
            model == ProjectionModel.Perspective ? ProjectionAperture.Rectangular : ProjectionAperture.Circular,
            model == ProjectionModel.Perspective ? null : 130, 67, 233, 31, HorizontalFlip: true, RadialDistortionK1: k1);
        var rotation = new AstrometricRotation(new(1, 0, 0), new(0, 1, 0), new(0, 0, 1));
        var wcs = CelestialWcs.FromMeasuredProjection(projection, rotation);
        Assert.IsNotNull(wcs);
        Assert.IsTrue(wcs.IsValid());
        Assert.AreEqual(code, wcs.Projection);
        Assert.AreEqual(141.3, wcs.ReferenceX, 1e-12);
        Assert.AreEqual(158.8, wcs.ReferenceY, 1e-12);

        var projector = ProjectorFactory.Create(projection);
        foreach (var pixel in new PixelPoint[] { new(140.8, 158.3), new(150.5, 199.5), new(37.5, 144.5), new(220.5, 120.5) })
        {
            var direction = CameraBasis.FromHorizontal(projector.Unproject(pixel)!.Value);
            var x = pixel.X + .5 - wcs.ReferenceX;
            var y = pixel.Y + .5 - wcs.ReferenceY;
            var east = (wcs.Cd11 * x + wcs.Cd12 * y) * Math.PI / 180;
            var north = (wcs.Cd21 * x + wcs.Cd22 * y) * Math.PI / 180;
            var radius = Math.Sqrt(east * east + north * north);
            var ra0 = wcs.RightAscensionDegrees * Math.PI / 180;
            var dec0 = wcs.DeclinationDegrees * Math.PI / 180;
            var sinDec = Math.Sin(dec0);
            var cosDec = Math.Cos(dec0);
            var delta = code switch
            {
                "TAN" => Math.Atan(radius),
                "ARC" or "ZPN" => radius,
                "ZEA" => 2 * Math.Asin(radius / 2),
                "SIN" => Math.Asin(radius),
                "STG" => 2 * Math.Atan(radius / 2),
                _ => throw new AssertFailedException("Unexpected projection.")
            };
            if (wcs.ZenithalCubic is { } cubic)
            {
                for (var iteration = 0; iteration < 10; iteration++)
                    delta -= (delta + cubic * delta * delta * delta - radius) / (1 + 3 * cubic * delta * delta);
            }
            var factor = radius == 0 ? 1 : Math.Sin(delta) / radius;
            var ra = ra0 + Math.Atan2(east * factor, cosDec * Math.Cos(delta) - north * factor * sinDec);
            var dec = Math.Asin(sinDec * Math.Cos(delta) + north * factor * cosDec);
            Assert.AreEqual(direction.East, Math.Cos(dec) * Math.Cos(ra), 3e-9);
            Assert.AreEqual(direction.North, Math.Cos(dec) * Math.Sin(ra), 3e-9);
            Assert.AreEqual(direction.Up, Math.Sin(dec), 3e-9);
        }
    }

    [TestMethod]
    public void WcsDeclinesAnUnrepresentableRadialModel()
    {
        var distorted = new ProjectionContext(ProjectionModel.Perspective, 150, 150, 300, 300,
            300, 300, ProjectionAperture.Rectangular, RadialDistortionK1: 0.2);
        Assert.IsNull(CelestialWcs.FromMeasuredProjection(distorted, default));
    }
}
