using HVO.SkyMonitor.CameraAgent.Common.Capture;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Focus;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraModuleOwnershipTests
{
    [TestMethod]
    public async Task Unpublished_HasNothingToLeaseAndRevokesTrivially()
    {
        using var ownership = new CameraModuleOwnership();

        Assert.IsFalse(ownership.TryAcquire(out var lease));
        Assert.IsNull(lease);
        Assert.IsFalse(ownership.Snapshot.Published);
        Assert.IsTrue(await ownership.RevokeAsync(TimeSpan.Zero).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task Publish_ExposesTheOwnersModuleConfigAndPreviewCapability()
    {
        using var ownership = new CameraModuleOwnership();
        var module = FocusTestModules.Create();
        await using var lifetime = module.ConfigureAwait(false);
        var config = FocusTestModules.Config();
        await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);

        var generation = ownership.Publish(module, config);
        Assert.IsTrue(ownership.TryAcquire(out var lease));
        using (lease)
        {
            Assert.AreEqual(1, generation);
            Assert.AreSame(module, lease.Module);
            Assert.AreSame(config, lease.Config);
            Assert.AreEqual(1, lease.Generation);
            Assert.IsFalse(lease.Revoked.IsCancellationRequested);
        }
        var snapshot = ownership.Snapshot;
        Assert.IsTrue(snapshot.Published);
        Assert.AreEqual("VirtualSky", snapshot.ModuleType);
        Assert.AreEqual("virtual-simulated-defocus", snapshot.FocusPreview!.Kind);
        Assert.AreEqual(module.SimulatedFocus, snapshot.SimulatedFocus);
        Assert.ThrowsExactly<InvalidOperationException>(() => ownership.Publish(new CaptureOnlyModule()));
    }

    [TestMethod]
    public async Task Revoke_CancelsLeasesAndWaitsForTheirRelease()
    {
        using var ownership = new CameraModuleOwnership();
        ownership.Publish(new CaptureOnlyModule());
        Assert.IsTrue(ownership.TryAcquire(out var first));
        Assert.IsTrue(ownership.TryAcquire(out var second));

        var revoke = ownership.RevokeAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(first.Revoked.IsCancellationRequested);
        Assert.IsTrue(second.Revoked.IsCancellationRequested);
        Assert.IsFalse(ownership.TryAcquire(out _), "A revoked module is never leased again.");
        first.Dispose();
        first.Dispose();
        await Task.Delay(50).ConfigureAwait(false);
        Assert.IsFalse(revoke.IsCompleted, "A double release must not count as the other lease's release.");
        second.Dispose();

        Assert.IsTrue(await revoke.ConfigureAwait(false));
        Assert.AreEqual(2, ownership.Publish(new CaptureOnlyModule()), "Every publication has a new generation.");
    }

    [TestMethod]
    public async Task Revoke_ReportsALeaseStillHeldAtTheDeadline()
    {
        using var ownership = new CameraModuleOwnership();
        ownership.Publish(new CaptureOnlyModule());
        Assert.IsTrue(ownership.TryAcquire(out var stuck));

        var drained = await ownership.RevokeAsync(TimeSpan.FromMilliseconds(50)).ConfigureAwait(false);
        var finishingDrain = ownership.WaitForRevokedLeasesAsync();
        Assert.IsFalse(finishingDrain.IsCompleted, "The timed-out publication remains occupied.");
        Assert.ThrowsExactly<InvalidOperationException>(() => ownership.Publish(new CaptureOnlyModule()));
        stuck.Dispose();
        await finishingDrain.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

        Assert.IsFalse(drained);
        Assert.IsFalse(ownership.Snapshot.Published);
        Assert.AreEqual(2, ownership.Publish(new CaptureOnlyModule()), "Replacement starts only after the old lease drains.");
    }

    [TestMethod]
    public void Dispose_CancelsHeldLeasesAndRefusesFurtherUse()
    {
        var ownership = new CameraModuleOwnership();
        ownership.Publish(new CaptureOnlyModule());
        Assert.IsTrue(ownership.TryAcquire(out var held));

        ownership.Dispose();
        ownership.Dispose();

        Assert.IsTrue(held.Revoked.IsCancellationRequested);
        Assert.IsFalse(ownership.TryAcquire(out _));
        Assert.ThrowsExactly<ObjectDisposedException>(() => ownership.Publish(new CaptureOnlyModule()));
        held.Dispose();
    }
}
