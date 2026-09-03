using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
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

    [TestMethod]
    [DataRow(0.001, -89d, 1900, 1, 1)]
    [DataRow(23.999, 89d, 2100, 12, 31)]
    [DataRow(6.752477, -16.716116, 2026, 7, 12)]
    public void PrecessToJ2000_RoundTripsIau1976Rotation(
        double rightAscensionHours,
        double declinationDegrees,
        int year,
        int month,
        int day)
    {
        var utc = new DateTimeOffset(year, month, day, 12, 0, 0, TimeSpan.Zero);
        var input = new EquatorialPoint(rightAscensionHours, declinationDegrees);

        var actual = EquatorialPrecession.PrecessToJ2000(
            EquatorialPrecession.PrecessJ2000(input, utc), utc);

        Assert.AreEqual(input.RightAscensionHours, actual.RightAscensionHours, 1e-10);
        Assert.AreEqual(input.DeclinationDegrees, actual.DeclinationDegrees, 1e-10);
    }

    [TestMethod]
    public void PrecessToJ2000_InvalidCoordinate_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            EquatorialPrecession.PrecessToJ2000(new EquatorialPoint(double.NaN, 0), DateTimeOffset.UnixEpoch));
    }
}
