using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Uninstall, false)]
    [DataRow(LifecycleOperationKind.Uninstall, true)]
    [DataRow(LifecycleOperationKind.Reinstall, false)]
    [DataRow(LifecycleOperationKind.Reinstall, true)]
    [DataRow(LifecycleOperationKind.Restore, false)]
    [DataRow(LifecycleOperationKind.Restore, true)]
    public async Task OfflineRecoveryPreservesPendingControlAndWorkerReconcilesAfterwards(LifecycleOperationKind kind, bool terminal)
    {
        using var fixture = kind == LifecycleOperationKind.Restore
            ? await BackupFixtureAsync()
            : await LifecycleFixture.CreateAsync(kind == LifecycleOperationKind.Reinstall
                ? InstanceLifecycleCondition.Uninstalled : InstanceLifecycleCondition.Installed);
        string? archive = null;
        if (kind == LifecycleOperationKind.Restore)
        {
            archive = (await BackupAsync(fixture, new FakeLifecycleClient())).Backup!.Archive;
            await UninstallAsync(fixture);
        }
        else
        {
            fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
                fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
                exists: kind != LifecycleOperationKind.Reinstall);
            fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        }
        var (journal, path, receipt) = await PendingControlAsync(fixture, terminal);
        var before = await File.ReadAllBytesAsync(path);
        var result = kind == LifecycleOperationKind.Restore
            ? await RestoreAsync(fixture, archive!)
            : await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(kind), fixture.Runner,
                _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", result.Outcome);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path), "Recovery must retain pending control evidence byte-for-byte.");
        journal = new InstallerControlJournal(path);
        await journal.LoadAsync(CancellationToken.None);
        Assert.HasCount(1, journal.Unpublished);
        var returned = new ReturnedControlClient();
        await new InstallerControlWorker(returned, journal, _ => Task.FromResult<IDisposable>(new ControlRecoveryLease()),
            TimeProvider.System, _ => { }).PollAsync(CancellationToken.None);
        var reloaded = new InstallerControlJournal(path);
        await reloaded.LoadAsync(CancellationToken.None);
        Assert.IsTrue(reloaded.Receipts.Single().IsTerminal);
        Assert.AreEqual(receipt.Request, reloaded.Receipts.Single().Request);
        Assert.IsEmpty(reloaded.Unpublished);
        Assert.IsEmpty(reloaded.Refused);
        Assert.IsTrue(returned.Publications > 0);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OrdinaryUpgradeWithPendingControlStillNamesTheOfflineRecoveryRoute(bool running)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidate = $"sha256:{new string('d', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidate, candidate, fixture.Uid, fixture.Gid);
        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, running);
        var (_, path, _) = await PendingControlAsync(fixture, terminal: false);
        var before = await File.ReadAllBytesAsync(path);
        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { ImageReference = candidate, NoDownload = true, MigrationBackwardCompatible = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(refused.Message, "lifecycle-worker", StringComparison.Ordinal);
        StringAssert.Contains(refused.Message, "uninstall", StringComparison.Ordinal);
        StringAssert.Contains(refused.Message, "reinstall", StringComparison.Ordinal);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Reinstall, "absent", true)]
    [DataRow(LifecycleOperationKind.Reinstall, "stopped", true)]
    [DataRow(LifecycleOperationKind.Reinstall, "unhealthy", true)]
    [DataRow(LifecycleOperationKind.Reinstall, "healthy", false)]
    [DataRow(LifecycleOperationKind.Reinstall, "starting", false)]
    [DataRow(LifecycleOperationKind.Reinstall, "missing-health", false)]
    [DataRow(LifecycleOperationKind.Reinstall, "inspect-error", false)]
    [DataRow(LifecycleOperationKind.Uninstall, "absent", true)]
    [DataRow(LifecycleOperationKind.Uninstall, "stopped", true)]
    [DataRow(LifecycleOperationKind.Uninstall, "unhealthy", true)]
    [DataRow(LifecycleOperationKind.Uninstall, "healthy", false)]
    [DataRow(LifecycleOperationKind.Uninstall, "starting", false)]
    [DataRow(LifecycleOperationKind.Uninstall, "missing-health", false)]
    [DataRow(LifecycleOperationKind.Uninstall, "inspect-error", false)]
    [DataRow(LifecycleOperationKind.Restore, "absent", true)]
    [DataRow(LifecycleOperationKind.Restore, "stopped", true)]
    [DataRow(LifecycleOperationKind.Restore, "unhealthy", true)]
    [DataRow(LifecycleOperationKind.Restore, "healthy", false)]
    [DataRow(LifecycleOperationKind.Restore, "starting", false)]
    [DataRow(LifecycleOperationKind.Restore, "missing-health", false)]
    [DataRow(LifecycleOperationKind.Restore, "inspect-error", false)]
    [DataRow(LifecycleOperationKind.Rollback, "absent", true)]
    [DataRow(LifecycleOperationKind.Rollback, "stopped", true)]
    [DataRow(LifecycleOperationKind.Rollback, "unhealthy", true)]
    [DataRow(LifecycleOperationKind.Rollback, "healthy", false)]
    [DataRow(LifecycleOperationKind.Rollback, "starting", false)]
    [DataRow(LifecycleOperationKind.Rollback, "missing-health", false)]
    [DataRow(LifecycleOperationKind.Rollback, "inspect-error", false)]
    public async Task RecoveryControlGateUsesConfirmedLivenessAndRetainsEvidence(LifecycleOperationKind kind, string observation, bool allowed)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var (_, path, _) = await PendingControlAsync(fixture, terminal: false);
        var before = await File.ReadAllBytesAsync(path);
        var docker = new DockerClient(new ControlObservationRunner(observation));
        Task Admission() => CameraAgentLifecycleManager.EnsureControlSettledAsync(fixture.Paths, CancellationToken.None, kind, docker);
        if (allowed) await Admission();
        else await Assert.ThrowsExactlyAsync<InstallerException>(Admission);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
        Assert.IsFalse(File.Exists(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Reinstall)]
    [DataRow(LifecycleOperationKind.Uninstall)]
    [DataRow(LifecycleOperationKind.Restore)]
    [DataRow(LifecycleOperationKind.Rollback)]
    public async Task OfflineRecoveryCannotBypassDefinitePublicationRefusal(LifecycleOperationKind kind)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var (journal, path, receipt) = await PendingControlAsync(fixture, terminal: true);
        await journal.RefuseAsync(receipt.Request.OperationId, 409, DateTimeOffset.UtcNow);
        var before = await File.ReadAllBytesAsync(path);
        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.EnsureControlSettledAsync(fixture.Paths, CancellationToken.None,
                kind, new DockerClient(new ControlObservationRunner("absent"))));
        Assert.IsTrue(refused.IsRefusal);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
    }

    private sealed class ControlObservationRunner(string observation) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.AreEqual("docker", fileName);
            if (arguments is ["context", "inspect", ..])
                return Task.FromResult(new ProcessResult(0, "unix:///var/run/docker.sock", ""));
            if (arguments is ["--host", _, ..]) arguments = arguments.Skip(2).ToArray();
            Assert.AreEqual("container", arguments[0]);
            Assert.AreEqual("inspect", arguments[1]);
            if (observation == "absent") return Task.FromResult(new ProcessResult(1, "", $"Error response from daemon: No such container: {arguments[2]}"));
            if (observation == "inspect-error") return Task.FromResult(new ProcessResult(1, "", "Injected daemon outage."));
            var state = new Dictionary<string, object> { ["Running"] = observation != "stopped" };
            if (observation != "missing-health") state["Health"] = new { Status = observation == "stopped" ? "healthy" : observation };
            return Task.FromResult(new ProcessResult(0, JsonSerializer.Serialize(new[] { new { State = state, Image = "sha256:test" } }), ""));
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RollbackAfterOfflineAdmissionStillVerifiesTheReturnedOriginalAndRetainsControl(bool terminal)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var candidateReference = $"ghcr.io/example/cameraagent@sha256:{new string('a', 64)}";
        var candidateImageId = $"sha256:{new string('b', 64)}";
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            candidateReference, candidateImageId, fixture.Uid, fixture.Gid);
        var lifecycle = new FakeLifecycleClient();
        await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Upgrade) with
        {
            ImageReference = candidateReference,
            NoDownload = true,
            MigrationBackwardCompatible = true
        }, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        var (_, path, _) = await PendingControlAsync(fixture, terminal);
        var before = await File.ReadAllBytesAsync(path);
        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        var returning = new ReturnAfterControlAdmission(fixture.Runner);
        var result = await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Rollback), returning,
            _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, result.Image!.ImageId);
        Assert.IsTrue(returning.Inspections >= 3);
        Assert.AreEqual(2, lifecycle.PauseCount, "Rollback must still authenticate and drain the returned original.");
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(path));
    }

    private sealed class ReturnAfterControlAdmission(FakeRunner inner) : IProcessRunner
    {
        internal int Inspections;
        public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            var effective = arguments is ["--host", _, ..] ? arguments.Skip(2).ToArray() : arguments;
            if (fileName == "docker" && effective is ["container", "inspect", _, ..] && ++Inspections == 3)
                inner.Reboot(inner.ActiveImageId, wasRunning: true);
            return inner.RunAsync(fileName, arguments, cancellationToken);
        }
    }

    private static async Task<(InstallerControlJournal Journal, string Path, CameraAgentControlReceipt Receipt)> PendingControlAsync(
        LifecycleFixture fixture, bool terminal)
    {
        var now = DateTimeOffset.UtcNow;
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "owner", "maintenance", 0, now);
        var phase = terminal ? CameraAgentControlPhase.Failed : CameraAgentControlPhase.Draining;
        var receipt = new CameraAgentControlReceipt(request, phase,
            [new(CameraAgentControlPhase.Queued, now, "lifecycle.queued"), new(phase, now, "lifecycle.interrupted")]);
        var path = Path.Combine(fixture.Paths.OperationsRoot, $"control-worker-{fixture.InstanceId:D}.json");
        var journal = new InstallerControlJournal(path);
        await journal.SaveAsync(receipt);
        return (journal, path, receipt);
    }

    private sealed class ControlRecoveryLease : IDisposable { public void Dispose() { } }
    private sealed class ReturnedControlClient : IInstallerControlClient
    {
        internal int Publications;
        public Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken cancellationToken)
        {
            if (receipt?.IsTerminal == true) Publications++;
            return Task.FromResult(new CameraAgentControlSnapshot(null, false, Guid.NewGuid(), "Paused", 0, true, true, true));
        }
        public Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken cancellationToken) => throw new AssertFailedException("Recovery must not drain again.");
        public Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken) => throw new AssertFailedException("Recovery must not repeat the stop.");
        public Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken cancellationToken) => throw new AssertFailedException("No verification was authorized.");
        public Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken cancellationToken) => throw new AssertFailedException("No owned resume was authorized.");
    }
}
