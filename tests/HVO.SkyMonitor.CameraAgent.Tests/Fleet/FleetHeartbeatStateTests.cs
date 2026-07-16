using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.Fleet.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Fleet;

[TestClass]
[TestCategory("Unit")]
public sealed class FleetHeartbeatStateTests
{
    [TestMethod]
    public void ResolveAvailability_PreservesBlockedCredentialsUntilAcknowledged()
    {
        var now = DateTimeOffset.UtcNow;
        var outbox = new FleetStatusOutboxSnapshot(1, 100, 0, 1, 0, 0, 1, now, now);
        var current = new FleetHeartbeatStateSnapshot(
            FleetAvailability.Unavailable, "credentials-blocked", outbox, null);

        var result = FleetHeartbeatService.ResolveAvailability(outbox, current);

        Assert.AreEqual(FleetAvailability.Unavailable, result.Availability);
        Assert.AreEqual("credentials-blocked", result.Reason);
    }

    [TestMethod]
    public void ResolveAvailability_HistoricalOverflowIsDegradedNotQuarantined()
    {
        var now = DateTimeOffset.UtcNow;
        var outbox = new FleetStatusOutboxSnapshot(0, 0, 0, 0, 0, 1, 0, null, now);
        var current = new FleetHeartbeatStateSnapshot(FleetAvailability.Available, "ready", outbox, now);

        var result = FleetHeartbeatService.ResolveAvailability(outbox, current);

        Assert.AreEqual(FleetAvailability.Unavailable, result.Availability);
        Assert.AreEqual("overflow-evidence", result.Reason);
    }
}
