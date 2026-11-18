using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class InstallerControlRecoveryTests
{
    [TestMethod]
    public async Task InterruptedInstallerRecordsFailureAndRetainsUnpublishedEvidenceAcrossReload()
    {
        var root = Directory.CreateTempSubdirectory("hvo-1711-installer-recovery-");
        try
        {
            var path = Path.Combine(root.FullName, "control.json");
            var now = DateTimeOffset.UtcNow;
            var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "owner", "maintenance", 0, now);
            var receipt = new CameraAgentControlReceipt(request, CameraAgentControlPhase.Restarting,
                [new(CameraAgentControlPhase.Queued, now, "lifecycle.queued"), new(CameraAgentControlPhase.Draining, now, "lifecycle.draining"),
                    new(CameraAgentControlPhase.Restarting, now, "restart.requesting")]);
            await new InstallerControlJournal(path).SaveAsync(receipt);
            var recovered = new InstallerControlJournal(path);
            await recovered.LoadAsync(CancellationToken.None);
            var client = new RecoveryClient();
            await new InstallerControlVerifier(client, recovered.SaveAsync, TimeProvider.System)
                .SettleInterruptedAsync(recovered.Receipts.Single());
            Assert.AreEqual(0, client.Effects);
            Assert.AreEqual(CameraAgentControlPhase.Failed, recovered.Receipts.Single().Phase);
            Assert.AreEqual("lifecycle.installer-interrupted", recovered.Receipts.Single().Events[^1].Code);
            Assert.HasCount(1, recovered.Unpublished.ToArray());
            if (OperatingSystem.IsLinux())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            await recovered.AcknowledgeAsync(request.OperationId);
            var reloaded = new InstallerControlJournal(path);
            await reloaded.LoadAsync(CancellationToken.None);
            Assert.AreEqual(recovered.WorkerId, reloaded.WorkerId, "The process lock fences a durable worker identity across restarts.");
            Assert.IsEmpty(reloaded.Unpublished);
            Assert.IsTrue(reloaded.Receipts.Single().IsTerminal);
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public void WorkerCommandIsExplicitAndInstanceBound()
    {
        var id = Guid.NewGuid();
        var command = LifecycleWorkerRequest.Parse(["cameraagent", "lifecycle-worker", "--instance-id", id.ToString("D"), "--once"], "/tmp/hvo-1711-root", true);
        Assert.AreEqual(id, command.InstanceId);
        Assert.IsTrue(command.Once);
        Assert.AreEqual("/tmp/hvo-1711-root", command.ProductRoot);
    }

    [TestMethod]
    [DataRow("--command")]
    [DataRow("--restart-host")]
    [DataRow("--endpoint")]
    [DataRow("--token")]
    public void WorkerRejectsArbitraryCommandsAddressesPowerAndInlineCredentials(string option)
    {
        Assert.Throws<InstallUsageException>(() => LifecycleWorkerRequest.Parse(
            ["cameraagent", "lifecycle-worker", "--instance-id", Guid.NewGuid().ToString("D"), option, "untrusted"],
            "/tmp/hvo-1711-root", true));
    }
    private sealed class RecoveryClient : IInstallerControlClient
    {
        internal int Effects;
        public Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken cancellationToken) =>
            Task.FromResult(new CameraAgentControlSnapshot(null, false, Guid.NewGuid(), "Paused", 0, true, true, true));
        public Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken cancellationToken)
        { Effects++; throw new AssertFailedException("Recovery must not repeat drain."); }
        public Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
        { Effects++; throw new AssertFailedException("Recovery must not repeat restart."); }
        public Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken cancellationToken) => throw new AssertFailedException();
        public Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken)
        { Effects++; throw new AssertFailedException("Unproven recovery must not resume."); }
    }

}
