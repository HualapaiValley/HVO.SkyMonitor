using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Astronomy.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class SolarEventCalculatorTests
{
    [TestMethod]
    public void Find_OrdersPhoenixSolsticeDawnSunriseSunsetAndDusk()
    {
        var calculator = new AstronomyEngineSolarEventCalculator();
        var start = new DateTimeOffset(2025, 6, 21, 7, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);

        var dawn = Find(SolarEventKind.CivilDawn);
        var sunrise = Find(SolarEventKind.Sunrise);
        var sunset = Find(SolarEventKind.Sunset);
        var dusk = Find(SolarEventKind.CivilDusk);

        Assert.IsTrue(dawn < sunrise);
        Assert.IsTrue(sunrise < sunset);
        Assert.IsTrue(sunset < dusk);
        Assert.AreEqual(AstronomyEngineSolarEventCalculator.Version,
            calculator.Find(SolarEventKind.Sunrise, start, end, 35.347, -113.878, 1000).AlgorithmVersion);

        DateTimeOffset Find(SolarEventKind kind)
            => calculator.Find(kind, start, end, 35.347, -113.878, 1000).Utc!.Value;
    }

    [TestMethod]
    public void Find_ReturnsNoRiseOrSetDuringTromsoMidnightSun()
    {
        var calculator = new AstronomyEngineSolarEventCalculator();
        var start = new DateTimeOffset(2025, 6, 20, 22, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(1);

        var sunrise = calculator.Find(SolarEventKind.Sunrise, start, end, 69.6492, 18.9553, 0);
        var sunset = calculator.Find(SolarEventKind.Sunset, start, end, 69.6492, 18.9553, 0);

        Assert.IsFalse(sunrise.Occurs);
        Assert.IsFalse(sunset.Occurs);
    }

    [TestMethod]
    public void Find_RejectsInvalidOrNonUtcQueries()
    {
        var calculator = new AstronomyEngineSolarEventCalculator();
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.Find(
            SolarEventKind.Sunrise, start, start, 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.Find(
            SolarEventKind.Sunrise, start.ToOffset(TimeSpan.FromHours(1)), start.AddDays(1), 0, 0, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => calculator.Find(
            SolarEventKind.Sunrise, start, start.AddDays(1), 91, 0, 0));
    }
}
