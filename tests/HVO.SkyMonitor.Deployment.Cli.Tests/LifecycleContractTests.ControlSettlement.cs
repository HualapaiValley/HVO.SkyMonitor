using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [DataRow(LifecycleOperationKind.Upgrade, "active")]
    [DataRow(LifecycleOperationKind.Rollback, "active")]
    [DataRow(LifecycleOperationKind.CatalogSelect, "active")]
    [DataRow(LifecycleOperationKind.CatalogRollback, "active")]
    [DataRow(LifecycleOperationKind.Uninstall, "active")]
    [DataRow(LifecycleOperationKind.Upgrade, "unpublished")]
    [DataRow(LifecycleOperationKind.Upgrade, "refused")]
    public async Task DeploymentJournalCannotBeginUntilRuntimeControlSettlementIsAcknowledged(LifecycleOperationKind kind, string state)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var now = DateTimeOffset.UtcNow;
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "owner", "maintenance", 0, now);
        var phase = state == "active" ? CameraAgentControlPhase.Draining : CameraAgentControlPhase.Failed;
        var receipt = new CameraAgentControlReceipt(request, phase,
            [new(CameraAgentControlPhase.Queued, now, "lifecycle.queued"), new(phase, now, "lifecycle.interrupted")]);
        var path = Path.Combine(fixture.Paths.OperationsRoot, $"control-worker-{fixture.InstanceId:D}.json");
        var journal = new InstallerControlJournal(path);
        await journal.SaveAsync(receipt);
        if (state == "refused") await journal.RefuseAsync(request.OperationId, 409, now);
        var before = await File.ReadAllBytesAsync(path);
        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.BeginAsync(
            fixture.Request(kind), fixture.Paths, kind, fixture.Manifest, CancellationToken.None));
        StringAssert.Contains(exception.Message, "lifecycle-worker", StringComparison.Ordinal);
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath), "A refused deployment must not strand a new operation journal.");
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        if (!receipt.IsTerminal)
            await journal.SaveAsync(receipt with { Phase = CameraAgentControlPhase.Failed,
                Events = [.. receipt.Events, new(CameraAgentControlPhase.Failed, now, "lifecycle.installer-interrupted")] });
        await journal.AcknowledgeAsync(request.OperationId);
        await CameraAgentLifecycleManager.BeginAsync(fixture.Request(kind), fixture.Paths, kind, fixture.Manifest, CancellationToken.None);
        Assert.IsTrue(File.Exists(fixture.Paths.LifecycleStatePath));
    }
}
