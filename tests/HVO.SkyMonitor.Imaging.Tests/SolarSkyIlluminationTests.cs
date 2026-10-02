using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SolarSkyIlluminationTests
{
    private static readonly ProjectionContext Projection = new(ProjectionModel.EquidistantFisheye,
        32, 32, 20, 20, 64, 64, ProjectionAperture.Circular, 31);

    [TestMethod]
    public void SkyColorIsContinuousBoundedAndNeutralAtNight()
    {
        foreach (var altitude in new[] { -90d, -18, -12, -6, 0, 6, 90 })
        {
            var sky = new SolarSkyIllumination(Projection, new(altitude, 180));
            var nearby = new SolarSkyIllumination(Projection, new(Math.Min(90, altitude + 1e-7), 180));
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                    for (var channel = -1; channel < 3; channel++)
                    {
                        var value = sky.Multiplier(x, y, channel);
                        Assert.IsTrue(value >= 0 && value <= SolarSkyIllumination.MaximumMultiplier);
                        Assert.AreEqual(value, nearby.Multiplier(x, y, channel), 1e-6);
                    }
            if (altitude <= -18)
                for (var channel = -1; channel < 3; channel++) Assert.AreEqual(1, sky.Multiplier(32, 32, channel), 1e-12);
        }
        var noon = new SolarSkyIllumination(Projection, new(60, 180));
        Assert.IsTrue(noon.Multiplier(32, 32, 2) > noon.Multiplier(32, 32, 0));
    }

    [TestMethod]
    public void BoundsAndProjectionBindingAreValidated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolarSkyIllumination(Projection, new(double.NaN, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolarSkyIllumination(Projection, new(0, 360)));
        var sky = new SolarSkyIllumination(Projection, new(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => sky.Multiplier(32, 32, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Mono16SceneRenderOptions
        {
            SkyIllumination = sky, BackgroundElectronsPerSecond = 1e12
        }.Validate());
    }
}
