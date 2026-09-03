using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AstronomyTimeTests
{
    [TestMethod]
    public void ToJulianDate_AtUnixEpoch_ReturnsPublishedEpoch()
    {
        var actual = AstronomyTime.ToJulianDate(DateTimeOffset.UnixEpoch);
        Assert.AreEqual(2440587.5d, actual, 1e-10);
    }

    [TestMethod]
    public void ToJulianDate_NormalizesOffsetToUtc()
    {
        var actual = AstronomyTime.ToJulianDate(new DateTimeOffset(1970, 1, 1, 1, 0, 0, TimeSpan.FromHours(1)));
        Assert.AreEqual(2440587.5d, actual, 1e-10);
    }

    [TestMethod]
    public void GreenwichMeanSiderealDegrees_AtJ2000_ReturnsPublishedReference()
    {
        var actual = AstronomyTime.GreenwichMeanSiderealDegrees(new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.AreEqual(280.46061837d, actual, 1e-8);
    }

    [TestMethod]
    public void LocalMeanSiderealDegrees_AddsEastLongitude()
    {
        var utc = new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.AreEqual(290.46061837d, AstronomyTime.LocalMeanSiderealDegrees(utc, 10), 1e-8);
    }
}
