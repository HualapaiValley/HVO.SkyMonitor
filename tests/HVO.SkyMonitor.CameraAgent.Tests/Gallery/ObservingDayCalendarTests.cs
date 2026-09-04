using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Tests.Gallery;

[TestClass]
[TestCategory("Unit")]
public sealed class ObservingDayCalendarTests
{
    private static readonly ObservingDayCalendar Phoenix = ObservingDayCalendar.Create("America/Phoenix");
    private static readonly ObservingDayCalendar Denver = ObservingDayCalendar.Create("America/Denver");

    [TestMethod]
    public void Resolve_SplitsTheCalendarDateAtLocalNoon()
    {
        // 11:59 and 12:01 local on 2026-09-04 in Phoenix (UTC-7 all year).
        var beforeNoon = new DateTimeOffset(2026, 9, 4, 18, 59, 0, TimeSpan.Zero);
        var afterNoon = new DateTimeOffset(2026, 9, 4, 19, 1, 0, TimeSpan.Zero);

        Assert.AreEqual(new DateOnly(2026, 9, 3), Phoenix.Resolve(beforeNoon).Date);
        Assert.AreEqual(new DateOnly(2026, 9, 4), Phoenix.Resolve(afterNoon).Date);
    }

    [TestMethod]
    public void Resolve_KeepsUtcBoundariesAndIdentityForANightThatCrossesMidnight()
    {
        var lateNight = new DateTimeOffset(2026, 9, 4, 6, 30, 0, TimeSpan.Zero); // 23:30 local on 2026-09-03

        var day = Phoenix.Resolve(lateNight);

        Assert.AreEqual(new DateOnly(2026, 9, 3), day.Date);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 3, 19, 0, 0, TimeSpan.Zero), day.StartUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 9, 4, 19, 0, 0, TimeSpan.Zero), day.EndUtc);
        Assert.AreEqual(TimeSpan.FromHours(24), day.Duration);
        Assert.IsTrue(day.Contains(lateNight));
        Assert.IsFalse(day.Contains(day.EndUtc));
        Assert.AreEqual("America/Phoenix", day.TimeZoneId);
        Assert.IsFalse(day.TimeZoneFallback);
    }

    [TestMethod]
    public void Resolve_ShortensTheSpringForwardDayAndLengthensTheFallBackDay()
    {
        // United States daylight saving in 2026 starts 8 March and ends 1 November.
        var springForward = Denver.Resolve(new DateOnly(2026, 3, 7));
        var fallBack = Denver.Resolve(new DateOnly(2026, 10, 31));
        var ordinary = Denver.Resolve(new DateOnly(2026, 7, 1));

        Assert.AreEqual(TimeSpan.FromHours(23), springForward.Duration);
        Assert.AreEqual(TimeSpan.FromHours(25), fallBack.Duration);
        Assert.AreEqual(TimeSpan.FromHours(24), ordinary.Duration);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 7, 19, 0, 0, TimeSpan.Zero), springForward.StartUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 3, 8, 18, 0, 0, TimeSpan.Zero), springForward.EndUtc);
    }

    [TestMethod]
    public void Resolve_NeverAssignsAnInstantToADayThatDoesNotContainIt()
    {
        var calendar = Denver;
        var instant = new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.Zero);
        for (var step = 0; step < 24 * 4; step++)
        {
            var probe = instant.AddMinutes(step * 15);
            var day = calendar.Resolve(probe);
            Assert.IsTrue(day.Contains(probe), $"{probe:O} resolved to {day.Date} [{day.StartUtc:O}, {day.EndUtc:O})");
            Assert.AreEqual(day, calendar.Resolve(day.Date));
        }
    }

    [TestMethod]
    public void Create_FallsBackToUtcForAMissingOrUnknownTimeZone()
    {
        var missing = ObservingDayCalendar.Create(" ");
        var unknown = ObservingDayCalendar.Create("Mars/Olympus_Mons");

        Assert.IsTrue(missing.TimeZoneFallback);
        Assert.IsTrue(unknown.TimeZoneFallback);
        Assert.AreEqual(TimeZoneInfo.Utc.Id, unknown.TimeZoneId);
        Assert.IsFalse(ObservingDayCalendar.Utc.TimeZoneFallback);
        var day = unknown.Resolve(new DateTimeOffset(2026, 9, 4, 11, 59, 0, TimeSpan.Zero));
        Assert.AreEqual(new DateOnly(2026, 9, 3), day.Date);
        Assert.IsTrue(day.TimeZoneFallback);
    }

    [TestMethod]
    public void Range_ReturnsConsecutiveDaysAndRejectsAnUnboundedRange()
    {
        var days = Phoenix.Range(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 3));

        Assert.AreEqual(3, days.Count);
        Assert.AreEqual(new DateOnly(2026, 9, 1), days[0].Date);
        Assert.AreEqual(days[0].EndUtc, days[1].StartUtc);
        Assert.AreEqual(days[1].EndUtc, days[2].StartUtc);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Phoenix.Range(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1).AddDays(ObservingDayCalendar.MaximumRangeDays)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Phoenix.Range(new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 1)));
        Assert.AreEqual(ObservingDayCalendar.MaximumRangeDays, Phoenix.Range(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 1).AddDays(ObservingDayCalendar.MaximumRangeDays - 1)).Count);
    }
}
