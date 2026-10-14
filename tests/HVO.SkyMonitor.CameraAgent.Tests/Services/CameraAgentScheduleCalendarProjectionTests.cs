using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentScheduleCalendarProjectionTests
{
    private static readonly ObservatoryLocation Observer = new(35.347, -113.878, 1000, "America/Phoenix");
    private static readonly TimeZoneInfo Phoenix = TimeZoneInfo.FindSystemTimeZoneById("America/Phoenix");
    private static readonly DateOnly FirstNight = new(2026, 10, 1);

    // Phoenix is UTC-7 all year: local noon is 19:00Z and the 20:00-04:00 window is 03:00Z-11:00Z.
    private static readonly DateTimeOffset NightStart = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void CurrentNight_BelongsToTheDateItBeganOnUntilLocalNoon()
    {
        Assert.AreEqual(
            new DateOnly(2026, 10, 1),
            CameraAgentScheduleCalendarProjection.CurrentNight(NightStart.AddDays(1).AddMinutes(-1), Phoenix));
        Assert.AreEqual(
            new DateOnly(2026, 10, 2),
            CameraAgentScheduleCalendarProjection.CurrentNight(NightStart.AddDays(1), Phoenix));
    }

    [TestMethod]
    public void Create_CoversEachNightNoonToNoonWithContiguousMergedSegments()
    {
        // Two back-to-back windows with the same setpoint read as one open span, not two.
        var definition = Definition(
            [.. Daily("evening", Fixed(20), Fixed(0, dayOffset: 1)),
             .. Daily("morning", Fixed(0, dayOffset: 1), Fixed(4, dayOffset: 1))]);

        var calendar = Create(definition, [], nightCount: 2);

        Assert.AreEqual("America/Phoenix", calendar.TimeZoneId);
        Assert.HasCount(2, calendar.Nights);
        for (var index = 0; index < calendar.Nights.Count; index++)
        {
            var night = calendar.Nights[index];
            var start = NightStart.AddDays(index);
            Assert.AreEqual(FirstNight.AddDays(index), night.LocalDate);
            Assert.AreEqual(start, night.StartUtc);
            Assert.AreEqual(start.AddDays(1), night.EndUtc);
            AssertContiguous(night);
            AssertSegments(
                night,
                (start, start.AddHours(8), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null),
                (start.AddHours(8), start.AddHours(16), true, CaptureScheduleAdmissionReason.WeeklyWindow, "night", null),
                (start.AddHours(16), start.AddDays(1), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null));
        }
    }

    [TestMethod]
    public void Create_AppliesBlackoutsAndOverridesForTheActiveRevisionOnly()
    {
        var blackoutStart = NightStart.AddHours(10);
        var definition = Definition(
            Daily("night", Fixed(20), Fixed(4, dayOffset: 1)),
            [new CaptureScheduleBlackout("maintenance", blackoutStart, blackoutStart.AddHours(1))]);
        var revision = CaptureScheduleContract.ComputeSha256(definition);
        CaptureScheduleOverride[] overrides =
        [
            Override("closed", revision, CaptureScheduleOverrideMode.ForceClosed, NightStart.AddHours(13), NightStart.AddHours(14)),
            Override("open", revision, CaptureScheduleOverrideMode.ForceOpen, NightStart.AddHours(18), NightStart.AddHours(19), "night"),
            Override("stale", new string('0', 64), CaptureScheduleOverrideMode.ForceOpen, NightStart.AddHours(20), NightStart.AddHours(22), "night")
        ];

        var night = Create(definition, overrides, nightCount: 1).Nights.Single();

        AssertContiguous(night);
        AssertSegments(
            night,
            (NightStart, NightStart.AddHours(8), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null),
            (NightStart.AddHours(8), NightStart.AddHours(10), true, CaptureScheduleAdmissionReason.WeeklyWindow, "night", null),
            (NightStart.AddHours(10), NightStart.AddHours(11), false, CaptureScheduleAdmissionReason.Blackout, null, null),
            (NightStart.AddHours(11), NightStart.AddHours(13), true, CaptureScheduleAdmissionReason.WeeklyWindow, "night", null),
            (NightStart.AddHours(13), NightStart.AddHours(14), false, CaptureScheduleAdmissionReason.ForceClosedOverride, null, "closed"),
            (NightStart.AddHours(14), NightStart.AddHours(16), true, CaptureScheduleAdmissionReason.WeeklyWindow, "night", null),
            (NightStart.AddHours(16), NightStart.AddHours(18), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null),
            (NightStart.AddHours(18), NightStart.AddHours(19), true, CaptureScheduleAdmissionReason.ForceOpenOverride, "night", "open"),
            // The override written against another revision neither admits nor splits the closed span.
            (NightStart.AddHours(19), NightStart.AddDays(1), false, CaptureScheduleAdmissionReason.DefaultClosed, null, null));
    }

    [TestMethod]
    public void Create_SpansTheLocalNoonsAcrossADaylightSavingChange()
    {
        var denver = TimeZoneInfo.FindSystemTimeZoneById("America/Denver");
        var definition = Definition(Daily("night", Fixed(20), Fixed(4, dayOffset: 1)));
        var firstNight = new DateOnly(2026, 10, 31);
        var expansion = CaptureScheduleIntervalExpander.Expand(
            definition, firstNight, 2, denver, Observer with { TimeZoneId = "America/Denver" }, new NoSolarEvents());

        var night = CameraAgentScheduleCalendarProjection.Create(
            definition, expansion, [], "America/Denver", denver, firstNight, 1, NightStart).Nights.Single();

        // Noon MDT on the 31st to noon MST on 1 November is 25 hours.
        Assert.AreEqual(new DateTimeOffset(2026, 10, 31, 18, 0, 0, TimeSpan.Zero), night.StartUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 19, 0, 0, TimeSpan.Zero), night.EndUtc);
        AssertContiguous(night);
        var open = night.Segments.Single(segment => segment.Admitted);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 2, 0, 0, TimeSpan.Zero), open.StartUtc);
        Assert.AreEqual(new DateTimeOffset(2026, 11, 1, 11, 0, 0, TimeSpan.Zero), open.EndUtc);
    }

    [TestMethod]
    public void Create_RejectsANightCountOutsideTheDisplayedRange()
    {
        var definition = Definition(Daily("night", Fixed(20), Fixed(4, dayOffset: 1)));

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Create(definition, [], nightCount: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => Create(definition, [], nightCount: CameraAgentScheduleCalendarProjection.MaximumNights + 1));
    }

    private static CameraAgentScheduleCalendar Create(
        CaptureScheduleDefinition definition,
        IReadOnlyList<CaptureScheduleOverride> overrides,
        int nightCount)
    {
        var expansion = CaptureScheduleIntervalExpander.Expand(
            definition, FirstNight, Math.Clamp(nightCount + 1, 1, 31), Phoenix, Observer, new NoSolarEvents());
        return CameraAgentScheduleCalendarProjection.Create(
            definition, expansion, overrides, "America/Phoenix", Phoenix, FirstNight, nightCount, NightStart);
    }

    private static void AssertContiguous(CameraAgentScheduleNight night)
    {
        Assert.AreEqual(night.StartUtc, night.Segments[0].StartUtc);
        Assert.AreEqual(night.EndUtc, night.Segments[^1].EndUtc);
        for (var index = 1; index < night.Segments.Count; index++)
        {
            Assert.AreEqual(night.Segments[index - 1].EndUtc, night.Segments[index].StartUtc);
        }
    }

    private static void AssertSegments(
        CameraAgentScheduleNight night,
        params (DateTimeOffset Start, DateTimeOffset End, bool Admitted, CaptureScheduleAdmissionReason Reason, string? Setpoint, string? OverrideId)[] expected)
    {
        CollectionAssert.AreEqual(
            expected.Select(item => new CameraAgentScheduleSegment(
                item.Start, item.End, item.Admitted, item.Reason, item.Setpoint, item.OverrideId)).ToArray(),
            night.Segments.ToArray());
    }

    private static CaptureScheduleDefinition Definition(
        IReadOnlyList<CaptureWeeklyScheduleWindow> windows,
        IReadOnlyList<CaptureScheduleBlackout>? blackouts = null)
        => new(
            "capture-schedule-v1",
            [new CaptureScheduleSetpointProfile("night", TimeSpan.FromSeconds(5), 1, TimeSpan.FromSeconds(10))],
            windows,
            Blackouts: blackouts);

    private static CaptureWeeklyScheduleWindow[] Daily(
        string id, CaptureScheduleBoundary start, CaptureScheduleBoundary end)
        => Enum.GetValues<DayOfWeek>()
            .Select(day => new CaptureWeeklyScheduleWindow($"{id}-{day}", day, start, end, "night"))
            .ToArray();

    private static CaptureScheduleBoundary Fixed(int hour, int dayOffset = 0)
        => new(CaptureScheduleBoundaryKind.FixedLocalTime, new TimeOnly(hour, 0), DayOffset: dayOffset);

    private static CaptureScheduleOverride Override(
        string id, string revisionSha256, CaptureScheduleOverrideMode mode,
        DateTimeOffset start, DateTimeOffset end, string? setpoint = null)
        => new(id, "revision-1", revisionSha256, mode, start, end, setpoint);

    private sealed class NoSolarEvents : ISolarEventCalculator
    {
        public SolarEventResult Find(
            SolarEventKind kind,
            DateTimeOffset intervalStartUtc,
            DateTimeOffset intervalEndUtc,
            double latitudeDegrees,
            double longitudeDegrees,
            double elevationMeters)
            => new(kind, null, "none");
    }
}
