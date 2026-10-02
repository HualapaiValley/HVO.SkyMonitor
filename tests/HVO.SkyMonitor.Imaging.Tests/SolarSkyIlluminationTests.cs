using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;

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
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.Rgb24)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task IncidentSkyAndDiskRespondToSensorExposureAndValidateProjection(CameraPixelFormat format)
    {
        var scene = await SceneTestFactory.CreateEmptyAsync(64, 64, 31).ConfigureAwait(false);
        var projection = scene.Request.Projection;
        var layout = new ImageLayout(64, 64, format, 64 * ImageLayout.BytesPerPixel(format));
        var sky = new SolarSkyIllumination(projection, new(60, 180));
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, scene.Request.Utc,
            new(90, 0), .25, 0, 1, 0, 149600000);
        var disks = new SolarDiskRenderPlan(projection, [disk], 1000);
        SceneRenderResult Render(double exposure, SolarSkyIllumination? illumination, SolarDiskRenderPlan? bodies)
            => format switch
            {
                CameraPixelFormat.Mono16 => Mono16SceneRenderer.Render(scene, layout, new()
                { ExposureSeconds = exposure, BackgroundElectronsPerSecond = 20, SkyIllumination = illumination, SolarDisks = bodies }),
                CameraPixelFormat.Rgb24 => Rgb24CompatibilityRenderer.Render(scene, layout, new()
                { ExposureSeconds = exposure, BackgroundElectronsPerSecond = 20, SkyIllumination = illumination, SolarDisks = bodies }),
                _ => BayerRggb16Renderer.Render(scene, layout, new()
                { ChannelResponse = new(1, 1, 1), ExposureSeconds = exposure, BackgroundElectronsPerSecond = 20, SkyIllumination = illumination, SolarDisks = bodies })
            };
        var normal = Render(.1, sky, disks);
        var excessive = Render(10000, sky, disks);
        Assert.AreEqual(0, normal.Statistics.ClippedHigh);
        Assert.IsTrue(excessive.Statistics.ClippedHigh > 0);
        Assert.IsTrue(excessive.Statistics.Mean > normal.Statistics.Mean);
        Assert.IsTrue(Render(.1, null, disks).Statistics.Mean > Render(.1, null, null).Statistics.Mean);
        CollectionAssert.AreEqual(Render(.1, null, null).Pixels.ToArray(),
            Render(.1, new SolarSkyIllumination(projection, new(-30, 0)), null).Pixels.ToArray());
        Assert.Throws<ArgumentException>(() => Render(.1,
            new SolarSkyIllumination(projection with { HorizontalFlip = true }, new(60, 180)), disks));
        Assert.Throws<ArgumentException>(() => Render(.1, sky,
            new SolarDiskRenderPlan(projection with { HorizontalFlip = true }, [disk], 1000)));
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
            SkyIllumination = sky,
            BackgroundElectronsPerSecond = 1e12
        }.Validate());
    }
}
