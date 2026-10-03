using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class LocalAutomationWindowTests
{
    [TestMethod]
    [DataRow("America/Phoenix", 2026, 10, 12)]
    [DataRow("America/Denver", 2026, 10, 31)]
    [DataRow("America/Denver", 2026, 3, 7)]
    [DataRow("Australia/Lord_Howe", 2026, 10, 3)]
    public void CivilHours_PartitionTheEntireSolarPeriodWithoutGapsOrRepeatedHourCollisions(
        string zone, int year, int month, int day)
    {
        var calendar = ObservingDayCalendar.ForDeployment(Site(zone));
        var period = calendar.Resolve(new DateOnly(year, month, day)).SunrisePeriod!;
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(calendar));
        var policy = Policy(LocalAutomationSourceWindowKind.CompletedCivilHour);
        var windows = planner.ResolveWindows(period, policy);

        Assert.AreEqual(period.StartUtc, windows[0].StartUtc);
        Assert.AreEqual(period.EndUtc, windows[^1].EndUtc);
        Assert.AreEqual(period.Duration.Ticks, windows.Sum(static window => (window.EndUtc - window.StartUtc).Ticks));
        Assert.AreEqual(windows.Count, windows.Select(static window => window.IdentitySha256).Distinct().Count());
        for (var index = 0; index < windows.Count; index++)
        {
            Assert.IsTrue(windows[index].IsValid());
            Assert.AreEqual(period.IdentitySha256, windows[index].ReportingPeriod.IdentitySha256);
            if (index > 0)
            {
                Assert.AreEqual(windows[index - 1].EndUtc, windows[index].StartUtc);
            }
        }
        for (var utc = period.StartUtc; utc < period.EndUtc; utc = utc.AddMinutes(7))
        {
            Assert.AreEqual(1, windows.Count(window => utc >= window.StartUtc && utc < window.EndUtc));
        }
        if (zone == "America/Denver")
        {
            var nextDate = period.ReportDate.AddDays(1);
            var starts = windows.Select(window => TimeZoneInfo.ConvertTime(window.StartUtc, calendar.TimeZone))
                .Where(local => DateOnly.FromDateTime(local.DateTime) == nextDate).ToArray();
            Assert.AreEqual(month == 10 ? 2 : 1, starts.Count(static local => local.Hour == 1 && local.Minute == 0));
            Assert.AreEqual(month == 3 ? 0 : 1, starts.Count(static local => local.Hour == 2 && local.Minute == 0));
        }
    }

    [TestMethod]
    [DataRow(LocalAutomationSourceWindowKind.CompletedCivilHour)]
    [DataRow(LocalAutomationSourceWindowKind.SunriseDay)]
    public void Finality_IsFixedAtWindowClosePlusRetainedAllowance(LocalAutomationSourceWindowKind kind)
    {
        var planner = Planner();
        var policy = Policy(kind) with { ProcessingSettleAllowance = TimeSpan.FromMinutes(15) };
        var window = planner.ResolveWindows(new DateOnly(2026, 10, 12), policy)[0];
        Assert.AreEqual(window.EndUtc.AddMinutes(15), window.EarliestFinalUtc);
        Assert.IsFalse(window.IsEligibleForFinal(window.EarliestFinalUtc.AddTicks(-1)));
        Assert.IsTrue(window.IsEligibleForFinal(window.EarliestFinalUtc));
        Assert.AreEqual(kind == LocalAutomationSourceWindowKind.SunriseDay, window.DailyFinality is not null);
    }

    [TestMethod]
    public void DarkNight_UsesActualSourceTimeAndKeepsTheFullPlannedPeriod()
    {
        var period = Planner().ResolveWindows(new DateOnly(2026, 10, 12),
            Policy(LocalAutomationSourceWindowKind.SunriseDay, LocalAutomationSourceSelection.DarkNightActualSources))[0];
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        Assert.IsFalse(period.AcceptsSource(period.StartUtc.AddHours(4), ephemeris));
        Assert.IsTrue(period.AcceptsSource(period.StartUtc.AddHours(16), ephemeris));
        Assert.IsFalse(period.AcceptsSource(period.EndUtc, ephemeris));
        Assert.AreEqual(period.ReportingPeriod.Duration, period.EndUtc - period.StartUtc);
    }

    [TestMethod]
    public void Occurrence_StrictReadbackRejectsUnknownMissingAndTamperedInputs()
    {
        var window = Planner().ResolveWindows(new DateOnly(2026, 10, 12), Policy(LocalAutomationSourceWindowKind.SunriseDay))[0];
        var definition = Definition("daily", window.Policy);
        var entry = new LocalAutomationRunnerEntry(definition, 2, LocalAutomationContract.ComputeRevisionSha256(definition), null, null);
        var occurrence = LocalAutomationWindowPlanner.CreateOccurrence(entry, window);
        var json = JsonSerializer.Serialize(occurrence);
        Assert.IsTrue(JsonSerializer.Deserialize<LocalAutomationOccurrence>(json)!.IsValid());
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<LocalAutomationOccurrence>(json[..^1] + ",\"unsupported\":true}"));
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize<LocalAutomationOccurrence>("{}"));
        Assert.IsFalse((occurrence with { ScheduledForUtc = occurrence.ScheduledForUtc.AddTicks(1) }).IsValid());
        Assert.IsFalse((occurrence with { Definition = definition with { Name = "Changed" } }).IsValid());
        Assert.IsFalse((occurrence with { Definition = definition with { TriggerEpochUtc = definition.TriggerEpochUtc.AddTicks(1) } }).IsValid());
        Assert.IsFalse((window with { EndUtc = window.EndUtc.AddMinutes(-1) }).IsValid());
    }

    [TestMethod]
    [DataRow("America/Phoenix", 10, 20, LocalAutomationSourceWindowKind.CompletedCivilHour, 0)]
    [DataRow("America/Phoenix", 10, 20, LocalAutomationSourceWindowKind.SunriseDay, 0)]
    [DataRow("America/Phoenix", 10, 20, LocalAutomationSourceWindowKind.CompletedCivilHour, 1440)]
    [DataRow("America/Phoenix", 10, 20, LocalAutomationSourceWindowKind.SunriseDay, 1440)]
    [DataRow("America/Denver", 3, 15, LocalAutomationSourceWindowKind.CompletedCivilHour, 0)]
    [DataRow("America/Denver", 3, 15, LocalAutomationSourceWindowKind.SunriseDay, 0)]
    [DataRow("America/Denver", 3, 15, LocalAutomationSourceWindowKind.CompletedCivilHour, 1440)]
    [DataRow("America/Denver", 3, 15, LocalAutomationSourceWindowKind.SunriseDay, 1440)]
    public void Lookback_CountsEveryEligibleWindowAcrossSunriseSettleAndDstBoundaries(
        string zone, int month, int day, LocalAutomationSourceWindowKind kind, int settleMinutes)
    {
        var now = new DateTimeOffset(2026, month, day, 10, 0, 0, TimeSpan.Zero);
        var calendar = ObservingDayCalendar.ForDeployment(Site(zone));
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(calendar));
        var policy = Policy(kind) with { ProcessingSettleAllowance = TimeSpan.FromMinutes(settleMinutes) };
        var definition = Definition("lookback", policy) with { TriggerEpochUtc = now.AddDays(-30) };
        var entry = new LocalAutomationRunnerEntry(definition, 1,
            LocalAutomationContract.ComputeRevisionSha256(definition), null, null);
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, calendar.TimeZone).DateTime);
        // Enumerate independently beyond the contract's seven UTC days. Eligibility, rather than
        // the report date or current clock's offset, determines which occurrence belongs inside it.
        var expected = Enumerable.Range(-12, 14)
            .SelectMany(offset => planner.ResolveWindows(localDate.AddDays(offset), policy))
            .Where(window => window.EarliestFinalUtc >= now.AddDays(-7) && window.EarliestFinalUtc <= now)
            .OrderBy(static window => window.EarliestFinalUtc).ToArray();
        var plan = planner.Resolve(entry, now);
        Assert.AreEqual(expected.Length, plan.MissedOccurrencesInLookback + 1);
        Assert.AreEqual(expected[^1].IdentitySha256, plan.DueOccurrence!.SourceWindow!.IdentitySha256);
        Assert.IsTrue(plan.EarlierOccurrencesOutsideLookback);
        Assert.IsTrue(plan.NextOccurrence!.ScheduledForUtc > now);
        var advanced = planner.Resolve(entry with { LastOccurrenceUtc = expected[0].EarliestFinalUtc }, now);
        Assert.AreEqual(expected.Length - 1, advanced.MissedOccurrencesInLookback + 1);
        Assert.AreEqual(expected[^1].IdentitySha256, advanced.DueOccurrence!.SourceWindow!.IdentitySha256);
    }

    [TestMethod]
    public void PolarNoEvent_HasNoInventedUpcomingWindow()
    {
        var calendar = ObservingDayCalendar.ForDeployment(Site("Arctic/Longyearbyen", 78.2232, 15.6469));
        var planner = new LocalAutomationWindowPlanner(new FixedObservingDayCalendarProvider(calendar));
        var definition = Definition("polar", Policy(LocalAutomationSourceWindowKind.SunriseDay)) with
        { TriggerEpochUtc = new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero) };
        var plan = planner.Resolve(new(definition, 1, LocalAutomationContract.ComputeRevisionSha256(definition), null, null),
            new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero));
        Assert.IsNull(plan.DueOccurrence);
        Assert.IsNull(plan.NextOccurrence);
        Assert.IsNotNull(plan.UnavailableReasonCode);
        Assert.IsNotEmpty(plan.UnavailableDates);
    }

    internal static DeploymentLocationSnapshot Site(string zone = "America/Phoenix", double latitude = 35.347,
        double longitude = -113.878, long version = 1)
        => DeploymentLocationSnapshot.Create("window-site", version, "test", null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, latitude, longitude, 1000, zone);

    internal static LocalAutomationSourceWindowPolicy Policy(LocalAutomationSourceWindowKind kind,
        LocalAutomationSourceSelection selection = LocalAutomationSourceSelection.AllActualSources)
        => new(LocalAutomationSourceWindowPolicy.CurrentVersion, kind, selection, TimeSpan.Zero);

    internal static LocalAutomationDefinition Definition(string id, LocalAutomationSourceWindowPolicy policy)
        => new(id, id, true, LocalAutomationTaskKind.StillImageGeneration, "test-rig",
            LocalAutomationTriggerKind.SourceWindowClosed, 1,
            new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero), policy);

    private static LocalAutomationWindowPlanner Planner()
        => new(new FixedObservingDayCalendarProvider(ObservingDayCalendar.ForDeployment(Site())));
}
