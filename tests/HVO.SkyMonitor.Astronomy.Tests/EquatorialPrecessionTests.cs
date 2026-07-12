using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
public sealed class EquatorialPrecessionTests
{
    [TestMethod]
    public void PrecessJ2000_AtJ2000_IsIdentity()
    {
        var input = new EquatorialPoint(12.25, -31.5);
        var actual = EquatorialPrecession.PrecessJ2000(input, new DateTimeOffset(2000, 1, 1, 12, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(input.RightAscensionHours, actual.RightAscensionHours, 1e-12);
        Assert.AreEqual(input.DeclinationDegrees, actual.DeclinationDegrees, 1e-12);
    }

    [TestMethod]
    public void PrecessJ2000_NorthPole_MatchesIau1976AnalyticRotation()
    {
        var utc = new DateTimeOffset(2100, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var actual = EquatorialPrecession.PrecessJ2000(new EquatorialPoint(0, 90), utc);

        // For the pole the Lieske rotation reduces exactly to RA = 12h + z and Dec = 90 - theta.
        Assert.AreEqual(12.0427283515, actual.RightAscensionHours, 2e-9);
        Assert.AreEqual(89.4433771064, actual.DeclinationDegrees, 2e-9);
    }

    [TestMethod]
    public void PrecessJ2000_InvalidCoordinate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            EquatorialPrecession.PrecessJ2000(new EquatorialPoint(24, 0), DateTimeOffset.UnixEpoch));
    }
}
