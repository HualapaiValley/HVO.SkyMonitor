using HVO.SkyMonitor.CameraAgent.Common.TimeSync;
using HVO.SkyMonitor.CameraAgent.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class ClockAssessmentTests
{
    private const string Server = "time.private.example";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Evaluate_WhenCheckingIsOff_IsDisabledWhateverWasMeasured()
    {
        var assessment = ClockAssessment.Evaluate(
            TimeSyncSettings.Default with { Enabled = false }, Snapshot(TimeSpan.FromSeconds(30)));

        Assert.AreEqual(ClockSyncStatus.Disabled, assessment.Status);
        Assert.IsTrue(assessment.IsHealthy);
    }

    [TestMethod]
    public void Evaluate_BeforeTheFirstRound_IsNotMeasured()
    {
        Assert.AreEqual(ClockSyncStatus.NotMeasured, ClockAssessment.Evaluate(TimeSyncSettings.Default, null).Status);
        // A round recorded while checking was off does not count once it is turned back on.
        Assert.AreEqual(
            ClockSyncStatus.NotMeasured,
            ClockAssessment.Evaluate(TimeSyncSettings.Default, Snapshot(TimeSpan.Zero) with { Enabled = false }).Status);
    }

    [TestMethod]
    [DataRow(-499d, KernelClockStatus.Synchronized, ClockSyncStatus.InTolerance, true)]
    [DataRow(500d, KernelClockStatus.Unknown, ClockSyncStatus.InTolerance, true)]
    [DataRow(12d, KernelClockStatus.Unsynchronized, ClockSyncStatus.Unsynchronized, false)]
    [DataRow(501d, KernelClockStatus.Synchronized, ClockSyncStatus.Drifting, false)]
    [DataRow(-2_000d, KernelClockStatus.Unsynchronized, ClockSyncStatus.Drifting, false)]
    public void Evaluate_AMeasuredOffset_IsJudgedAgainstTheTolerance(
        double offsetMilliseconds, KernelClockStatus kernel, ClockSyncStatus expected, bool healthy)
    {
        var assessment = ClockAssessment.Evaluate(
            TimeSyncSettings.Default, Snapshot(TimeSpan.FromMilliseconds(offsetMilliseconds), kernel));

        Assert.AreEqual(expected, assessment.Status);
        Assert.AreEqual(healthy, assessment.IsHealthy);
    }

    [TestMethod]
    [DataRow(KernelClockStatus.Synchronized, ClockSyncStatus.HostSynchronized, true)]
    [DataRow(KernelClockStatus.Unsynchronized, ClockSyncStatus.Unverified, false)]
    [DataRow(KernelClockStatus.Unknown, ClockSyncStatus.Unverified, false)]
    public void Evaluate_WhenNoServerAnswered_FallsBackToTheHostTimeService(
        KernelClockStatus kernel, ClockSyncStatus expected, bool healthy)
    {
        var assessment = ClockAssessment.Evaluate(TimeSyncSettings.Default, Snapshot(null, kernel));

        Assert.AreEqual(expected, assessment.Status);
        Assert.AreEqual(healthy, assessment.IsHealthy);
    }

    [TestMethod]
    public void Evaluate_ADrift_SaysWhichWayTheClockIsOutInWholeMilliseconds()
    {
        // A server ahead of this host (positive offset) means this host's clock is behind.
        Assert.AreEqual(
            "The clock is 1250 ms behind network time, beyond the 500 ms tolerance.",
            ClockAssessment.Evaluate(TimeSyncSettings.Default, Snapshot(TimeSpan.FromMilliseconds(1250.4))).Description);
        Assert.AreEqual(
            "The clock is 750 ms ahead of network time, beyond the 500 ms tolerance.",
            ClockAssessment.Evaluate(TimeSyncSettings.Default, Snapshot(TimeSpan.FromMilliseconds(-750))).Description);
    }

    [TestMethod]
    public async Task HealthCheck_IsNeverUnhealthyAndNamesNoServerAsync()
    {
        foreach (var (settings, snapshot) in new (TimeSyncSettings, ClockSyncSnapshot?)[]
        {
            (TimeSyncSettings.Default with { Enabled = false }, null),
            (TimeSyncSettings.Default, null),
            (TimeSyncSettings.Default, Snapshot(TimeSpan.FromMilliseconds(3))),
            (TimeSyncSettings.Default, Snapshot(TimeSpan.FromHours(3))),
            (TimeSyncSettings.Default, Snapshot(TimeSpan.Zero, KernelClockStatus.Unsynchronized)),
            (TimeSyncSettings.Default, Snapshot(null, KernelClockStatus.Synchronized)),
            (TimeSyncSettings.Default, Snapshot(null, KernelClockStatus.Unknown)),
        })
        {
            var result = await new ClockHealthCheck(new StaticMonitor(settings, snapshot))
                .CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

            Assert.AreNotEqual(HealthStatus.Unhealthy, result.Status);
            Assert.DoesNotContain(Server, result.Description!, StringComparison.OrdinalIgnoreCase);
            Assert.IsFalse(
                result.Data.Values.Any(static value => value.ToString()!.Contains(Server, StringComparison.OrdinalIgnoreCase)),
                "No data value names the server.");
            string[] expectedKeys = ["Availability", "Status"];
            CollectionAssert.AreEquivalent(expectedKeys, result.Data.Keys.ToArray());
        }
    }

    [TestMethod]
    [DataRow(false, 0d, "Healthy", "Disabled", "Disabled")]
    [DataRow(true, 12d, "Healthy", "Enabled", "InTolerance")]
    [DataRow(true, 900d, "Degraded", "Enabled", "Drifting")]
    public async Task HealthCheck_ReportsItsStatusAndAvailabilityAsync(
        bool enabled, double offsetMilliseconds, string status, string availability, string clockStatus)
    {
        var monitor = new StaticMonitor(
            TimeSyncSettings.Default with { Enabled = enabled }, Snapshot(TimeSpan.FromMilliseconds(offsetMilliseconds)));

        var result = await new ClockHealthCheck(monitor).CheckHealthAsync(new HealthCheckContext()).ConfigureAwait(false);

        Assert.AreEqual(Enum.Parse<HealthStatus>(status), result.Status);
        Assert.AreEqual(availability, result.Data["Availability"]);
        Assert.AreEqual(clockStatus, result.Data["Status"]);
    }

    internal static ClockSyncSnapshot Snapshot(TimeSpan? offset, KernelClockStatus kernel = KernelClockStatus.Synchronized)
    {
        TimeServerResult server = offset is { } value
            ? new(Server, null, value, TimeSpan.FromMilliseconds(20), 2)
            : TimeServerResult.Failed(Server, SntpFailure.Timeout);
        return new ClockSyncSnapshot(
            Now, true, server.Succeeded ? server : null, [server], 0, new KernelClockState(kernel, null, null));
    }

    private sealed class StaticMonitor(TimeSyncSettings settings, ClockSyncSnapshot? latest) : IClockSyncMonitor
    {
        public TimeSyncSettings Settings => settings;

        public ClockSyncSnapshot? Latest => latest;

        public Task<ClockCheckResult> CheckNowAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
