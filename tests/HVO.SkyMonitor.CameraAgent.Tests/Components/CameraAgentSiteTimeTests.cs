using Bunit;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Components.Layout;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSiteTimeTests
{
    [TestMethod]
    public void SiteDateAndNonWholeHourOffset_ConvertBothInputAndDisplay()
    {
        var clock = new CameraAgentSiteTime(ObservingDayCalendar.Create("Asia/Kolkata"));
        var instant = new DateTimeOffset(2026, 7, 23, 20, 0, 0, TimeSpan.Zero);
        Assert.AreEqual("2026-07-24T01:30", clock.Input(instant));
        Assert.AreEqual("24 Jul 2026 01:30:00 +05:30 (Asia/Kolkata)", clock.Format(instant));
        Assert.IsTrue(clock.TryInput("2026-07-24T01:30:00", out var utc, out var error));
        Assert.AreEqual(instant, utc);
        Assert.IsNull(error);
        Assert.AreEqual(TimeSpan.Zero, utc!.Value.Offset);
    }

    [TestMethod]
    [DataRow(1L, "2026-07-24T01:30")]
    [DataRow(1234567L, "2026-07-24T01:30:00.123")]
    public void Input_UsesBrowserRepresentablePrecision(long ticks, string expected)
    {
        var clock = new CameraAgentSiteTime(ObservingDayCalendar.Create("Asia/Kolkata"));
        Assert.AreEqual(expected, clock.Input(new DateTimeOffset(2026, 7, 23, 20, 0, 0, TimeSpan.Zero).AddTicks(ticks)));
    }

    [TestMethod]
    public void Fold_DisplaysBothOffsetsAndRejectsUnqualifiedLocalInput()
    {
        var clock = new CameraAgentSiteTime(ObservingDayCalendar.Create("America/New_York"));
        var early = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero);
        var late = early.AddHours(1);
        Assert.AreEqual("1 Nov 2026 01:30:00 -04:00 (America/New_York)", clock.Format(early));
        Assert.AreEqual("1 Nov 2026 01:30:00 -05:00 (America/New_York)", clock.Format(late));
        Assert.IsFalse(clock.TryInput("2026-11-01T01:30:00", out var utc, out var error));
        Assert.IsNull(utc);
        StringAssert.Contains(error!, "occurs twice", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Gap_RejectsMissingTimeAndConvertsBothSides()
    {
        var clock = new CameraAgentSiteTime(ObservingDayCalendar.Create("America/New_York"));
        Assert.IsFalse(clock.TryInput("2026-03-08T02:30", out _, out var error));
        StringAssert.Contains(error!, "does not exist", StringComparison.Ordinal);
        Assert.IsTrue(clock.TryInput("2026-03-08T01:30", out var before, out _));
        Assert.IsTrue(clock.TryInput("2026-03-08T03:30", out var after, out _));
        Assert.AreEqual(TimeSpan.FromHours(1), after - before);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("Mars/Olympus_Mons")]
    public void UnavailableZone_UsesExplicitUtcFallbackForInputAndDisplay(string? zone)
    {
        var clock = new CameraAgentSiteTime(ObservingDayCalendar.Create(zone));
        Assert.AreEqual("UTC (site time zone unavailable)", clock.Label);
        Assert.IsTrue(clock.TryInput("2026-07-23T20:00", out var utc, out _));
        Assert.AreEqual(new DateTimeOffset(2026, 7, 23, 20, 0, 0, TimeSpan.Zero), utc);
        StringAssert.Contains(clock.Format(utc), "+00:00 (UTC (site time zone unavailable))", StringComparison.Ordinal);
        Assert.IsFalse(clock.TryInput("2026-07-23T20:00Z", out _, out _));
    }

    [TestMethod]
    public void Footer_UsesDeploymentClockAndFollowsChangedDeploymentZone()
    {
        using var context = new BunitContext();
        var provider = new MutableCalendarProvider { Current = ObservingDayCalendar.Create("America/Phoenix") };
        context.Services.AddSingleton<IObservingDayCalendarProvider>(provider);
        context.Services.AddSingleton<TimeProvider>(new FixedClock());
        context.Services.AddLogging();
        var cut = context.Render<MainLayoutFooter>();
        Assert.AreEqual("2026-07-24T06:00:00.0000000+00:00", cut.Find("time").GetAttribute("datetime"));
        StringAssert.Contains(cut.Markup, "America/Phoenix", StringComparison.Ordinal);
        StringAssert.Contains(cut.Markup, "23:00:00 -07:00", StringComparison.Ordinal);
        provider.Current = ObservingDayCalendar.Create("Asia/Kolkata");
        cut.WaitForAssertion(() => StringAssert.Contains(cut.Markup, "11:30:00 +05:30", StringComparison.Ordinal), TimeSpan.FromSeconds(3));
    }

    private sealed class MutableCalendarProvider : IObservingDayCalendarProvider
    {
        public ObservingDayCalendar Current { get; set; } = ObservingDayCalendar.Utc;
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 7, 24, 6, 0, 0, TimeSpan.Zero);
    }
}
