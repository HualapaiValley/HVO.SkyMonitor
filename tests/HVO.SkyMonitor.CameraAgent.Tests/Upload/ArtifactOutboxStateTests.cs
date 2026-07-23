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

    [TestMethod]
    public void Snapshot_IgnoresDrainedRootsWhenSelectingOldestPendingTime()
    {
        var state = new ArtifactOutboxState();
        var oldest = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);
        state.Update("/tmp/outbox-drained", new ArtifactOutboxSnapshot(0, 0, null, 0, 0, 0, 4, 0, 0));
        state.Update("/tmp/outbox-pending", new ArtifactOutboxSnapshot(2, 512, oldest, 2, 0, 0, 0, 0, 0));

        Assert.AreEqual(oldest, state.Snapshot.OldestPendingUtc);
    }
}
