using HVO.SkyMonitor.CameraAgent.Common.RawIngress;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class RawIngressStateCompositionTests
{
    [TestMethod]
    public void QuarantineAndBacklog_DrainPreservesQuarantineDegradation()
    {
        var state = new RawIngressState(TimeProvider.System);
        state.Set(RawIngressAvailability.Degraded, "reconciliation-findings", quarantineCount: 2, quarantineBytes: 10);

        state.SetProjectedSceneBacklog(3);
        Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
        Assert.AreEqual("reconciliation-findings", state.Snapshot.Reason);
        state.SetProjectedSceneBacklog(0);

        Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
        Assert.AreEqual("reconciliation-findings", state.Snapshot.Reason);
        Assert.AreEqual(2, state.Snapshot.QuarantineCount);
    }

    [TestMethod]
    public void IndexFailureAndBacklog_DrainPreservesIndexFailure()
    {
        var state = new RawIngressState(TimeProvider.System);
        state.Set(RawIngressAvailability.Degraded, "index-projection-failed");

        state.SetProjectedSceneBacklog(2);
        state.SetProjectedSceneBacklog(0);

        Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
        Assert.AreEqual("index-projection-failed", state.Snapshot.Reason);
    }

    [TestMethod]
    public void BacklogOnly_DrainsToAccepting()
    {
        var state = new RawIngressState(TimeProvider.System);
        state.Set(RawIngressAvailability.Accepting, "accepting");

        state.SetProjectedSceneBacklog(1);
        Assert.AreEqual(RawIngressAvailability.Degraded, state.Snapshot.Availability);
        Assert.AreEqual("projected-scene-backlog", state.Snapshot.Reason);
        state.SetProjectedSceneBacklog(0);

        Assert.AreEqual(RawIngressAvailability.Accepting, state.Snapshot.Availability);
        Assert.AreEqual("accepting", state.Snapshot.Reason);
    }

    [TestMethod]
    public void RefusalAndBacklog_DrainPreservesRefusal()
    {
        var state = new RawIngressState(TimeProvider.System);
        state.Set(RawIngressAvailability.Unhealthy, "capacity-exhausted");

        state.SetProjectedSceneBacklog(4);
        state.SetProjectedSceneBacklog(0);

        Assert.AreEqual(RawIngressAvailability.Unhealthy, state.Snapshot.Availability);
        Assert.AreEqual("capacity-exhausted", state.Snapshot.Reason);
    }
}
