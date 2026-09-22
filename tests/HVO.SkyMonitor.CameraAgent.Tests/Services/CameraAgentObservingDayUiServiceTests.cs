using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentObservingDayUiServiceTests
{
    private static readonly DateTimeOffset NightStart = new(2026, 7, 21, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NightEnd = NightStart.AddDays(1);

    [TestMethod]
    public void ClipOpenWindows_ClipsToTheNightSubtractsClosedIntervalsAndMerges()
    {
        var preview = Preview(
            Open("w1", NightStart.AddHours(-2), NightStart.AddHours(4)),
            Open("w2", NightStart.AddHours(3), NightStart.AddHours(10)),
            Closed("blackout", NightStart.AddHours(5), NightStart.AddHours(6)),
            Open("w3", NightStart.AddHours(20), NightEnd.AddHours(3)));

        var windows = CameraAgentObservingDayUiService.ClipOpenWindows(preview, NightStart, NightEnd);

        CollectionAssert.AreEqual(
            new[]
            {
                (NightStart, NightStart.AddHours(5)),
                (NightStart.AddHours(6), NightStart.AddHours(10)),
                (NightStart.AddHours(20), NightEnd)
            },
            windows.ToArray());
    }

    [TestMethod]
    public void ClipOpenWindows_KeepsADateExceptionsOwnWindowsWhileClosingItsWeeklyOnes()
    {
        // A closed date-exception day removes the weekly window on that local day but keeps the
        // exception's own window, which the expander emits alongside the whole-day closure; a
        // blackout removes both.
        var localDay = (Start: NightStart.AddHours(-12), End: NightStart.AddHours(12));
        var preview = Preview(
            Open("weekly", NightStart.AddHours(1), NightStart.AddHours(9)),
            new ExpandedScheduleInterval("exception:closed", CaptureScheduleIntervalSource.DateExceptionClosed, ExpandedScheduleDisposition.Closed, localDay.Start, localDay.End, new DateOnly(2026, 7, 21), null),
            new ExpandedScheduleInterval("exception:window", CaptureScheduleIntervalSource.DateExceptionWindow, ExpandedScheduleDisposition.Open, NightStart.AddHours(2), NightStart.AddHours(6), new DateOnly(2026, 7, 21), "night"),
            Closed("blackout", NightStart.AddHours(3), NightStart.AddHours(4)));

        var windows = CameraAgentObservingDayUiService.ClipOpenWindows(preview, NightStart, NightEnd);

        CollectionAssert.AreEqual(
            new[] { (NightStart.AddHours(2), NightStart.AddHours(3)), (NightStart.AddHours(4), NightStart.AddHours(6)) },
            windows.ToArray());
    }

    [TestMethod]
    public void CoveredDuration_UnionsOneMinuteBinsInsideTheWindowsOnly()
    {
        var windows = new[] { (NightStart.AddHours(1), NightStart.AddHours(2)) };
        var exposures = new[]
        {
            // Outside every window.
            NightStart,
            // Two captures 30 s apart share half a bin: 1.5 minutes, not 2.
            NightStart.AddHours(1).AddMinutes(10),
            NightStart.AddHours(1).AddMinutes(10).AddSeconds(30),
            // Straddles the window end: only the inside 30 s count.
            NightStart.AddHours(2).AddSeconds(-30)
        };

        var covered = CameraAgentObservingDayUiService.CoveredDuration(windows, exposures);

        Assert.AreEqual(TimeSpan.FromSeconds(120), covered);
        Assert.AreEqual(TimeSpan.Zero, CameraAgentObservingDayUiService.CoveredDuration([], exposures));
        Assert.AreEqual(TimeSpan.Zero, CameraAgentObservingDayUiService.CoveredDuration(windows, []));
    }

    private static CaptureSchedulePreview Preview(params ExpandedScheduleInterval[] intervals)
        => new("rev", "exp", CaptureScheduleIntervalExpander.AlgorithmVersion, "tz", "solar", NightStart.AddDays(-1), NightEnd.AddDays(1), intervals, []);

    private static ExpandedScheduleInterval Open(string id, DateTimeOffset start, DateTimeOffset end)
        => new(id, CaptureScheduleIntervalSource.WeeklyWindow, ExpandedScheduleDisposition.Open, start, end, null, "night");

    private static ExpandedScheduleInterval Closed(string id, DateTimeOffset start, DateTimeOffset end)
        => new(id, CaptureScheduleIntervalSource.Blackout, ExpandedScheduleDisposition.Closed, start, end, null, null);
}
