using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SolarDiskRenderPlanTests
{
    private static readonly ProjectionContext Projection = new(ProjectionModel.Perspective,
        32, 32, 4000, 4000, 64, 64, ProjectionAperture.Rectangular);

    [TestMethod]
    public void DiskConservesFluxAndLunarPhaseMirrorsWithCameraOrientation()
    {
        var moon = new SolarDiskAppearance(SolarSystemBody.Moon, DateTimeOffset.UnixEpoch,
            new(90, 0), .25, 0, .5, 90, 384400);
        var plan = new SolarDiskRenderPlan(Projection, [moon], 1000);
        var flipped = new SolarDiskRenderPlan(Projection with { HorizontalFlip = true }, [moon], 1000);
        double flux = 0, weightedX = 0;
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                var rate = plan.ElectronRate(x, y);
                flux += rate;
                weightedX += (x + .5) * rate;
                Assert.AreEqual(rate, flipped.ElectronRate(63 - x, y), 1e-8);
            }
        Assert.AreEqual(1000, flux, 1e-8);
        Assert.IsTrue(weightedX / flux > 36, "The bright hemisphere must face local east before camera mirroring.");
    }

    [TestMethod]
    public void HorizonClippingNeverRestoresLostDiskFlux()
    {
        var disk = new SolarDiskAppearance(SolarSystemBody.Sun, DateTimeOffset.UnixEpoch,
            new(0, 180), .25, 0, 1, 0, 149600000);
        var horizon = Projection with { BoresightAltitudeDegrees = 0, BoresightAzimuthDegrees = 180 };
        var plan = new SolarDiskRenderPlan(horizon, [disk], 1000);
        var total = Enumerable.Range(0, 64 * 64).Sum(i => plan.ElectronRate(i % 64, i / 64));
        Assert.AreEqual(500, total, 2);
        var hidden = new SolarDiskRenderPlan(horizon, [disk with { Direction = new(-1, 180) }], 1000);
        Assert.AreEqual(0, hidden.MaximumElectronRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolarDiskRenderPlan(Projection,
            [disk with { IlluminatedFraction = double.NaN }], 1000));
    }
}
