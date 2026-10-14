using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.Components.Operations;
using HVO.SkyMonitor.CameraAgent.Services;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class ClockFormatTests
{
    [TestMethod]
    [DataRow(0d, "0 ms")]
    [DataRow(0.4d, "0 ms")]
    [DataRow(-0.4d, "0 ms")]
    [DataRow(0.6d, "+1 ms")]
    [DataRow(12d, "+12 ms")]
    [DataRow(-12d, "−12 ms")]
    [DataRow(999.4d, "+999 ms")]
    [DataRow(-1250d, "−1.25 s")]
    [DataRow(200_000d, "+3m 20s")]
    public void Signed_ShowsTheDirectionUnlessTheDifferenceRoundsToNothing(double milliseconds, string expected)
        => Assert.AreEqual(expected, ClockFormat.Signed(TimeSpan.FromMilliseconds(milliseconds)));

    [TestMethod]
    [DataRow(0d, "0 ms")]
    [DataRow(12.4d, "12 ms")]
    [DataRow(-12.4d, "12 ms")]
    [DataRow(1250d, "1.25 s")]
    [DataRow(30_000d, "30 s")]
    [DataRow(200_000d, "3m 20s")]
    [DataRow(7_500_000d, "2h 5m")]
    [DataRow(363_600_000d, "4d 5h")]
    public void Unsigned_PicksAUnitThatKeepsTheFigureShort(double milliseconds, string expected)
        => Assert.AreEqual(expected, ClockFormat.Unsigned(TimeSpan.FromMilliseconds(milliseconds)));

    [TestMethod]
    [DataRow(12d, "12 ms ahead")]
    [DataRow(-1250d, "1.25 s behind")]
    [DataRow(0.3d, "0 ms")]
    public void Relative_SaysWhichWayTheClockIsOut(double milliseconds, string expected)
        => Assert.AreEqual(expected, ClockFormat.Relative(TimeSpan.FromMilliseconds(milliseconds)));

    [TestMethod]
    public void HealthFact_ShowsTheDriftOrWhyThereIsNone()
    {
        Assert.AreEqual("Unknown", ClockFormat.HealthFact(null));
        Assert.AreEqual(
            "+12 ms", ClockFormat.HealthFact(new SystemClockFact(ClockSyncStatus.InTolerance, TimeSpan.FromMilliseconds(12), DateTimeOffset.UnixEpoch)));
        Assert.AreEqual("Not measured", ClockFormat.HealthFact(new SystemClockFact(ClockSyncStatus.NotMeasured, null, null)));
        Assert.AreEqual("Host synced", ClockFormat.HealthFact(new SystemClockFact(ClockSyncStatus.HostSynchronized, null, DateTimeOffset.UnixEpoch)));
    }

    [TestMethod]
    public void StatusTextAndChip_CoverEveryStatus()
    {
        foreach (var status in Enum.GetValues<ClockSyncStatus>())
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(ClockFormat.StatusText(status)), status.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(ClockFormat.StatusChip(status)), status.ToString());
        }
        Assert.AreEqual("success", ClockFormat.StatusChip(ClockSyncStatus.HostSynchronized));
        Assert.AreEqual("warning", ClockFormat.StatusChip(ClockSyncStatus.Drifting));
        Assert.AreEqual("neutral", ClockFormat.StatusChip(ClockSyncStatus.Disabled));
    }
}
