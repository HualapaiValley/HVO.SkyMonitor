using Bunit;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Components;

[TestClass]
[TestCategory("Unit")]
public sealed class AgentClockTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 12, 0, 0, 250, TimeSpan.Zero);

    [TestMethod]
    public void Clock_ShowsTheAgentsTimeAtTheSiteAndPreservesUtcAttribute()
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<AgentClock>(parameters => parameters.Add(clock => clock.SiteTimeZoneId, "America/Phoenix"));

        Assert.AreEqual("23 Jul 2026 05:00:00 -07:00 (America/Phoenix)", cut.Find("#time-site time").TextContent);
        Assert.AreEqual(Now.ToUniversalTime().ToString("O"), cut.Find("#time-site time").GetAttribute("datetime"));
    }

    [TestMethod]
    [DataRow(null, "UTC (site time zone unavailable)")]
    [DataRow("Mars/Olympus_Mons", "UTC (site time zone unavailable)")]
    public void Clock_WithoutAKnownZone_SaysWhy(string? zone, string expected)
    {
        using var context = new BunitContext();
        Configure(context);

        var cut = context.Render<AgentClock>(parameters => parameters.Add(clock => clock.SiteTimeZoneId, zone));

        StringAssert.Contains(cut.Find("#time-site").TextContent, expected, StringComparison.Ordinal);
        StringAssert.Contains(cut.Find("#time-site").TextContent, "12:00:00 +00:00", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Clock_TicksOnTheNextWholeSecondAndThenEverySecond()
    {
        using var context = new BunitContext();
        var clock = Configure(context);
        var cut = context.Render<AgentClock>(parameters => parameters.Add(component => component.SiteTimeZoneId, "UTC"));

        Assert.AreEqual(TimeSpan.FromMilliseconds(750), clock.DueTime);
        Assert.AreEqual(TimeSpan.FromSeconds(1), clock.Period);

        clock.Now = Now.AddSeconds(1);
        clock.Tick();

        cut.WaitForAssertion(() =>
        {
            Assert.AreEqual("23 Jul 2026 12:00:01 +00:00 (UTC)", cut.Find("#time-site time").TextContent);
        });
    }

    [TestMethod]
    public void Clock_ChangingTheZone_ConvertsAgain()
    {
        using var context = new BunitContext();
        Configure(context);
        var cut = context.Render<AgentClock>(parameters => parameters.Add(clock => clock.SiteTimeZoneId, "UTC"));

        cut.Render(parameters => parameters.Add(clock => clock.SiteTimeZoneId, "Asia/Kolkata"));

        Assert.AreEqual("23 Jul 2026 17:30:00 +05:30 (Asia/Kolkata)", cut.Find("#time-site time").TextContent);
    }

    [TestMethod]
    public void Dispose_StopsTheTimerAndIgnoresALateTick()
    {
        using var context = new BunitContext();
        var clock = Configure(context);
        var cut = context.Render<AgentClock>();

        cut.Instance.Dispose();
        clock.Now = Now.AddSeconds(1);
        clock.Tick();

        Assert.IsTrue(clock.TimerDisposed);
        StringAssert.Contains(cut.Find("#time-site time").TextContent, "12:00:00 +00:00", StringComparison.Ordinal);
    }

    private static ManualClock Configure(BunitContext context)
    {
        var clock = new ManualClock { Now = Now };
        context.Services.AddSingleton<TimeProvider>(clock);
        return clock;
    }

    /// <summary>A clock whose timer fires only when the test says so.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        internal DateTimeOffset Now { get; set; }

        internal TimeSpan DueTime { get; private set; }

        internal TimeSpan Period { get; private set; }

        internal bool TimerDisposed { get; set; }

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            Period = period;
            return new CapturedTimer(this);
        }

        internal void Tick() => _callback!(_state);
    }

    private sealed class CapturedTimer(ManualClock owner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => owner.TimerDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
