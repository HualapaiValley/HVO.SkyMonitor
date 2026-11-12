using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Reinstall, false, false)]
    [DataRow(LifecycleOperationKind.Reinstall, true, false)]
    [DataRow(LifecycleOperationKind.Rollback, false, false)]
    [DataRow(LifecycleOperationKind.Reinstall, false, true)]
    [DataRow(LifecycleOperationKind.Rollback, false, true)]
    public async Task CommittedOfflineRecoveryHandsRetainedControlToTheWorkerThenCompletesResume(
        LifecycleOperationKind kind, bool uninstallFirst, bool terminal)
    {
        using var fixture = await LifecycleFixture.CreateAsync(kind == LifecycleOperationKind.Reinstall && !uninstallFirst
            ? InstanceLifecycleCondition.Uninstalled : InstanceLifecycleCondition.Installed);
        var candidate = "sha256:" + new string('d', 64);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            kind == LifecycleOperationKind.Rollback ? candidate : fixture.Manifest.Image.ImmutableReference,
            kind == LifecycleOperationKind.Rollback ? candidate : fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid,
            exists: kind != LifecycleOperationKind.Reinstall || uninstallFirst);
        var ordinary = new FakeLifecycleClient();
        if (kind == LifecycleOperationKind.Rollback)
            await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(LifecycleOperationKind.Upgrade) with
            { ImageReference = candidate, NoDownload = true, MigrationBackwardCompatible = true }, fixture.Runner,
                _ => ordinary, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        var (journal, path, original) = await PendingControlAsync(fixture, terminal: false);
        original = original with { OriginalProcessId = Guid.NewGuid(), ResumeAfterVerification = true };
        await journal.SaveAsync(original);
        if (terminal) await journal.SaveAsync(original with
        {
            Phase = CameraAgentControlPhase.Failed,
            Events = [.. original.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "restart.verification-timeout")]
        });
        var preserved = await File.ReadAllBytesAsync(path);
        var returned = new RetainedControlProjection(original);
        var lifecycle = new FencedDeploymentLifecycle(ordinary, returned);
        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        if (uninstallFirst)
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => lifecycle, null,
                fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        IProcessRunner runner = kind == LifecycleOperationKind.Rollback ? new ReturnAfterControlAdmission(fixture.Runner) : fixture.Runner;
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(kind), runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));
        StringAssert.Contains(failure.ToString(), "409", StringComparison.Ordinal);
        var committed = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
        Assert.AreEqual(LifecycleOperationPhase.Committed, committed!.Phase);
        Assert.AreEqual(InstallationStatus.Running, committed.Status);
        Assert.IsTrue(committed.MutationStarted);
        CollectionAssert.AreEqual(preserved, await File.ReadAllBytesAsync(path));
        var busy = new List<string>();
        await new InstallerControlWorker(returned, journal,
            token => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, token),
            TimeProvider.System, busy.Add,
            acquireInterruptedRecovery: token => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(
                fixture.Paths, token, settleCommittedRecovery: true)).PollAsync(CancellationToken.None);
        Assert.IsTrue(journal.Receipts.Single().IsTerminal, string.Join("; ", busy));
        Assert.IsEmpty(journal.Unpublished);
        Assert.IsEmpty(journal.Refused);
        Assert.AreEqual(original.Request, journal.Receipts.Single().Request);
        Assert.IsFalse(returned.Held);
        var starts = fixture.Runner.ComposeUpCount;
        var result = await CameraAgentLifecycleManager.ExecuteAsync(fixture.Request(kind) with { Resume = true },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(starts, fixture.Runner.ComposeUpCount, "Committed retry must not restart the image again.");
        Assert.AreEqual(InstallationStatus.Completed,
            (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!.Status);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Reinstall, LifecycleOperationPhase.Committed, "Paused", true)]
    [DataRow(LifecycleOperationKind.Rollback, LifecycleOperationPhase.Committed, "Paused", true)]
    [DataRow(LifecycleOperationKind.Reinstall, LifecycleOperationPhase.CandidateVerified, "Paused", false)]
    [DataRow(LifecycleOperationKind.Rollback, LifecycleOperationPhase.CandidateVerified, "Paused", false)]
    [DataRow(LifecycleOperationKind.Upgrade, LifecycleOperationPhase.Committed, "Paused", false)]
    [DataRow(LifecycleOperationKind.Uninstall, LifecycleOperationPhase.Committed, "Paused", false)]
    [DataRow(LifecycleOperationKind.Restore, LifecycleOperationPhase.Committed, "Paused", false)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Committed, "Paused", false)]
    [DataRow(LifecycleOperationKind.Reinstall, LifecycleOperationPhase.Committed, "Running", false)]
    [DataRow(LifecycleOperationKind.Rollback, LifecycleOperationPhase.Committed, "Running", false)]
    public async Task OnlyInterruptedSettlementMayEnterTheVerifiedCommittedRecoveryBoundary(
        LifecycleOperationKind kind, LifecycleOperationPhase phase, string captureState, bool allowed)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(kind), fixture.Paths, kind, fixture.Manifest, CancellationToken.None);
        operation = operation with
        {
            Phase = phase,
            MutationStarted = true,
            CandidateImage = kind is LifecycleOperationKind.Rollback or LifecycleOperationKind.Upgrade ? fixture.Manifest.Image : null,
            PostMutationContinuity = new(captureState, 1, 42, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow)
        };
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, CancellationToken.None));
        Task<IDisposable> AcquireRecovery() => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(
            fixture.Paths, CancellationToken.None, settleCommittedRecovery: true);
        if (allowed)
        {
            using var lease = await AcquireRecovery();
        }
        else await Assert.ThrowsExactlyAsync<InstallerException>(AcquireRecovery);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CommittedRecoverySettlementCannotStartAnotherQueuedRuntimeLifecycle()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Reinstall),
            fixture.Paths, LifecycleOperationKind.Reinstall, fixture.Manifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            Phase = LifecycleOperationPhase.Committed,
            MutationStarted = true,
            PostMutationContinuity = new("Paused", 1, 42, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        var (journal, _, retained) = await PendingControlAsync(fixture, terminal: false);
        var next = retained with
        {
            Request = retained.Request with { OperationId = Guid.NewGuid() },
            Phase = CameraAgentControlPhase.Queued,
            Events = [new(CameraAgentControlPhase.Queued, DateTimeOffset.UtcNow, "lifecycle.queued")]
        };
        var returned = new RetainedControlProjection(retained) { NextQueued = next };
        await new InstallerControlWorker(returned, journal,
            token => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, token), TimeProvider.System, _ => { },
            acquireInterruptedRecovery: token => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(
                fixture.Paths, token, settleCommittedRecovery: true)).PollAsync(CancellationToken.None);
        Assert.IsTrue(journal.Receipts.Single(item => item.Request.OperationId == retained.Request.OperationId).IsTerminal);
        var rejected = journal.Receipts.Single(item => item.Request.OperationId == next.Request.OperationId);
        Assert.AreEqual(CameraAgentControlPhase.Failed, rejected.Phase);
        Assert.AreEqual("lifecycle.installer-busy", rejected.Events[^1].Code);
        Assert.IsEmpty(journal.Unpublished);
    }

    private sealed class FencedDeploymentLifecycle(FakeLifecycleClient inner, RetainedControlProjection projection) : ICameraAgentLifecycleClient
    {
        public Task<LifecycleContinuity> PauseAndDrainAsync(Guid id, string token, CancellationToken ct) => inner.PauseAndDrainAsync(id, token, ct);
        public Task<LifecycleContinuity> ConfirmDrainedAsync(string token, CancellationToken ct) => inner.ConfirmDrainedAsync(token, ct);
        public Task<LifecycleContinuity> ReadContinuityAsync(string token, CancellationToken ct) => inner.ReadContinuityAsync(token, ct);
        public Task<LifecycleResumeReceipt> ResumeRecoveryAsync(Guid id, Guid command, long version, string token, CancellationToken ct) =>
            inner.ResumeRecoveryAsync(id, command, version, token, ct);
        public Task ResumeAsync(Guid id, string token, CancellationToken ct) => projection.Held
            ? throw new InstallerException("CameraAgent rejected the lifecycle resume command with status 409.")
            : inner.ResumeAsync(id, token, ct);
    }

    private sealed class RetainedControlProjection(CameraAgentControlReceipt original) : IInstallerControlClient
    {
        internal bool Held = true;
        internal CameraAgentControlReceipt? NextQueued;
        private readonly Guid _returnedProcess = Guid.NewGuid();
        public Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken ct)
        {
            if (receipt?.IsTerminal == true)
            {
                if (receipt.Request.OperationId == original.Request.OperationId) Held = false;
                else if (NextQueued?.Request.OperationId == receipt.Request.OperationId) NextQueued = receipt;
            }
            return Task.FromResult(new CameraAgentControlSnapshot(Held ? original : NextQueued, false, _returnedProcess,
                "Paused", 2, true, true, true));
        }
        public Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken ct) => throw new AssertFailedException("Settlement must not drain.");
        public Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken ct) => throw new AssertFailedException("Settlement must not restart.");
        public Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken ct) => throw new AssertFailedException("Settlement must not verify a new restart.");
        public Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken ct) => throw new AssertFailedException("Returned-process settlement must not resume.");
    }
}
