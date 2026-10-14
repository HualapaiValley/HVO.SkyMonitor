namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SolarDiskEphemerisTests
{
    private static readonly ObserverLocation Site = new(35.347, -113.878, 0);

    [TestMethod]
    public void SolarDiskHasTruthfulSizeAndEastToWestMotion()
    {
        var morning = SolarDiskEphemeris.Get(SolarSystemBody.Sun, new(2026, 10, 12, 16, 0, 0, TimeSpan.Zero), Site);
        var afternoon = SolarDiskEphemeris.Get(SolarSystemBody.Sun, morning.Utc.AddHours(6), Site);
        Assert.IsTrue(morning.AngularRadiusDegrees is > .25 and < .28);
        Assert.IsTrue(morning.Direction.AltitudeDegrees > 0);
        Assert.IsTrue(morning.Direction.AzimuthDegrees is > 90 and < 180);
        Assert.IsTrue(afternoon.Direction.AzimuthDegrees is > 180 and < 270);
        Assert.AreEqual(1, morning.IlluminatedFraction);
        Assert.IsTrue(morning.DistanceKilometers is > 147000000 and < 153000000);
    }

    [TestMethod]
    public void LunarDiskTracksNewAndFullPhasesAndRejectsUnsupportedBodies()
    {
        var newMoon = SolarDiskEphemeris.Get(SolarSystemBody.Moon, new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero), Site);
        var fullMoon = SolarDiskEphemeris.Get(SolarSystemBody.Moon, new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), Site);
        Assert.IsTrue(newMoon.IlluminatedFraction < .02);
        Assert.IsTrue(fullMoon.IlluminatedFraction > .98);
        Assert.IsTrue(newMoon.AngularRadiusDegrees is > .23 and < .30);
        Assert.IsTrue(fullMoon.AngularRadiusDegrees is > .23 and < .30);
        Assert.Throws<ArgumentOutOfRangeException>(() => SolarDiskEphemeris.Get(SolarSystemBody.Mars, newMoon.Utc, Site));
    }
}
