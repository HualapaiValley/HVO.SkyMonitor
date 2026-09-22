using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Components;

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
    public void CoveredDuration_UnionsOneMinuteBinsInsideTheWindowsOnly()
    {
        var windows = new[] { (NightStart.AddHours(1), NightStart.AddHours(2)) };
        var captures = new[]
        {
            // Outside every window.
            OperatorUiTestData.Capture(Guid.NewGuid()) with { ExposureStartedUtc = NightStart },
            // Two captures 30 s apart share half a bin: 1.5 minutes, not 2.
            OperatorUiTestData.Capture(Guid.NewGuid()) with { ExposureStartedUtc = NightStart.AddHours(1).AddMinutes(10) },
            OperatorUiTestData.Capture(Guid.NewGuid()) with { ExposureStartedUtc = NightStart.AddHours(1).AddMinutes(10).AddSeconds(30) },
            // Straddles the window end: only the inside 30 s count.
            OperatorUiTestData.Capture(Guid.NewGuid()) with { ExposureStartedUtc = NightStart.AddHours(2).AddSeconds(-30) }
        };

        var covered = CameraAgentObservingDayUiService.CoveredDuration(windows, captures);

        Assert.AreEqual(TimeSpan.FromSeconds(120), covered);
        Assert.AreEqual(TimeSpan.Zero, CameraAgentObservingDayUiService.CoveredDuration([], captures));
        Assert.AreEqual(TimeSpan.Zero, CameraAgentObservingDayUiService.CoveredDuration(windows, []));
    }

    private static CaptureSchedulePreview Preview(params ExpandedScheduleInterval[] intervals)
        => new("rev", "exp", CaptureScheduleIntervalExpander.AlgorithmVersion, "tz", "solar", NightStart.AddDays(-1), NightEnd.AddDays(1), intervals, []);

    private static ExpandedScheduleInterval Open(string id, DateTimeOffset start, DateTimeOffset end)
        => new(id, CaptureScheduleIntervalSource.WeeklyWindow, ExpandedScheduleDisposition.Open, start, end, null, "night");

    private static ExpandedScheduleInterval Closed(string id, DateTimeOffset start, DateTimeOffset end)
        => new(id, CaptureScheduleIntervalSource.Blackout, ExpandedScheduleDisposition.Closed, start, end, null, null);
}
