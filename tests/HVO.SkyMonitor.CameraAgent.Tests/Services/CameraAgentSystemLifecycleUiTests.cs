using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Services;

public sealed partial class CameraAgentSystemUiServiceTests
{
    [TestMethod]
    public async Task LifecycleReadRightsCannotSubmitOrCancelEvenWhenInstallerIsRegistered()
    {
        using var fixture = new Fixture(lifecycle: true, canMutate: false);
        await fixture.InitializeStoresAsync().ConfigureAwait(false);
        await fixture.Controls!.ExchangeAsync(new(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse((await fixture.Service.GetLifecycleAsync(CancellationToken.None).ConfigureAwait(false)).Value!.Available);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await fixture.Service.RequestDrainAsync(Guid.NewGuid(),
            fixture.Coordinator.Snapshot.Version, "maintenance", CancellationToken.None).ConfigureAwait(false)).Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized,
            (await fixture.Service.CancelLifecycleAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false)).Kind);
        Assert.IsEmpty((await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts);
    }

    [TestMethod]
    public async Task LifecycleOwnerCommandRecordsActualActorAndReason_TerminalReceiptJoinsNativeHistory()
    {
        using var fixture = new Fixture(lifecycle: true);
        await fixture.InitializeStoresAsync().ConfigureAwait(false);
        var worker = Guid.NewGuid();
        await fixture.Controls!.ExchangeAsync(new(worker), CancellationToken.None).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var result = await fixture.Service.RequestDrainAsync(id, fixture.Coordinator.Snapshot.Version,
            "  maintenance  ", CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(result.IsSuccess);
        var queued = result.Value!;
        Assert.AreEqual("owner", queued.Request.Actor);
        Assert.AreEqual("maintenance", queued.Request.Reason);
        await fixture.Controls.ExchangeAsync(new(worker, queued with { Phase = CameraAgentControlPhase.Failed,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Failed, fixture.Clock.GetUtcNow(), "drain.timeout")] }), CancellationToken.None).ConfigureAwait(false);
        var receipt = (await fixture.Service.GetControlReceiptsAsync(25, CancellationToken.None).ConfigureAwait(false)).Value!
            .Single(item => item.Kind == SystemControlReceiptKind.Drain);
        Assert.AreEqual(SystemControlActor.LocalOwner, receipt.Actor);
        Assert.AreEqual(SystemControlOutcome.Failed, receipt.Outcome);
        Assert.AreEqual("maintenance", receipt.Reason);
        StringAssert.Contains(receipt.Detail, "No automatic resume", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task LifecycleInvalidReasonAndStaleVersionNeverQueueAnIntent()
    {
        using var fixture = new Fixture(lifecycle: true);
        await fixture.InitializeStoresAsync().ConfigureAwait(false);
        await fixture.Controls!.ExchangeAsync(new(Guid.NewGuid()), CancellationToken.None).ConfigureAwait(false);
        var version = fixture.Coordinator.Snapshot.Version;
        Assert.AreEqual(OperatorUiResultKind.Invalid, (await fixture.Service.RequestDrainAsync(Guid.NewGuid(), version,
            " ", CancellationToken.None).ConfigureAwait(false)).Kind);
        Assert.AreEqual(OperatorUiResultKind.Conflict, (await fixture.Service.RequestDrainAsync(Guid.NewGuid(), version + 1,
            "reason", CancellationToken.None).ConfigureAwait(false)).Kind);
        Assert.IsEmpty((await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts);
    }
}
