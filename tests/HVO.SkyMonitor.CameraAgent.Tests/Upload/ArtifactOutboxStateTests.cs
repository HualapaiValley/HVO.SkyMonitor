using HVO.SkyMonitor.CameraAgent.Common.Upload;

namespace HVO.SkyMonitor.CameraAgent.Tests.Upload;

[TestClass]
[TestCategory("Unit")]
public sealed class ArtifactOutboxStateTests
{
    [TestMethod]
    public void Snapshot_WhenDisabledOrFullyDrained_IsHealthyAndEmpty()
    {
        var state = new ArtifactOutboxState();
        Assert.AreEqual(ArtifactOutboxAvailability.Initializing, state.Snapshot.Availability);

        state.MarkInitialized();
        Assert.AreEqual(ArtifactOutboxAvailability.Healthy, state.Snapshot.Availability);
        Assert.IsNull(state.Snapshot.OldestPendingUtc);

        state.Update("/tmp/outbox-state", new ArtifactOutboxSnapshot(0, 0, null, 0, 0, 0, 1, 0, 0));
        Assert.AreEqual(ArtifactOutboxAvailability.Healthy, state.Snapshot.Availability);
        Assert.AreEqual(0, state.Snapshot.PendingCount);
        Assert.IsNull(state.Snapshot.OldestPendingUtc);
    }

    [TestMethod]
    public void Snapshot_PreservesUnavailableRootUntilThatRootRecovers()
    {
        var state = new ArtifactOutboxState();
        state.ReportUnavailable("/tmp/outbox-failed", "sqlite-failed");
        state.Update("/tmp/outbox-healthy", new ArtifactOutboxSnapshot(0, 0, null, 0, 0, 0, 0, 0, 0));

        Assert.AreEqual(ArtifactOutboxAvailability.Unavailable, state.Snapshot.Availability);

        state.Update("/tmp/outbox-failed", new ArtifactOutboxSnapshot(0, 0, null, 0, 0, 0, 0, 0, 0));
        Assert.AreEqual(ArtifactOutboxAvailability.Healthy, state.Snapshot.Availability);
    }
}
