using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, false, "backup-aborted", true)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, false, "backup-partial-missing", true)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, false, "lifecycle-failed", true)]
    [DataRow(LifecycleOperationKind.Upgrade, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, false, "lifecycle-refused", true)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Prepared, InstallationStatus.Running, false, null, false)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Prepared, InstallationStatus.Running, true, null, false)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, true, "lifecycle-failed", false)]
    [DataRow(LifecycleOperationKind.Backup, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, true, "backup-aborted", false)]
    [DataRow(LifecycleOperationKind.Upgrade, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, false, "lifecycle-failed", false)]
    [DataRow(LifecycleOperationKind.Upgrade, LifecycleOperationPhase.Prepared, InstallationStatus.Failed, false, "backup-aborted", false)]
    public async Task SettledBackupControlAdmissionPreservesJournalAndExcludesIncompleteWork(
        LifecycleOperationKind kind, LifecycleOperationPhase phase, InstallationStatus status, bool mutated, string? code, bool allowed)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(kind), fixture.Paths, kind, fixture.Manifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            Phase = phase,
            Status = status,
            MutationStarted = mutated,
            FailureCode = code,
            FailureMessage = code is null ? null : "retained evidence"
        }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var probes = 0;
        Task<IDisposable> Acquire() => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, CancellationToken.None,
            returnedProcess: _ => { probes++; return Task.FromResult(true); });
        if (allowed)
        {
            using (await Acquire()) Assert.IsFalse(await LockAvailableInChildAsync(fixture.Paths));
            Assert.IsTrue(await LockAvailableInChildAsync(fixture.Paths));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
            // A subsequent real operation supersedes the settled record and must fence control again.
            await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
            {
                Kind = LifecycleOperationKind.Backup,
                Phase = LifecycleOperationPhase.Prepared,
                Status = InstallationStatus.Running,
                MutationStarted = true,
                FailureCode = null,
                FailureMessage = null
            }, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<InstallerException>(Acquire);
        }
        else
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(Acquire);
            Assert.IsTrue(await LockAvailableInChildAsync(fixture.Paths));
            CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        }
        Assert.AreEqual(0, probes);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task CancelledControlAdmissionReleasesDeploymentLockAndPreservesSettledBackup()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Backup), fixture.Paths,
            LifecycleOperationKind.Backup, fixture.Manifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        { Status = InstallationStatus.Failed, FailureCode = "lifecycle-failed", FailureMessage = "recovered" }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(
            fixture.Paths, CancellationToken.None, readCondition: _ => Task.FromCanceled<InstanceLifecycleCondition>(cancelled.Token)));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.IsTrue(await LockAvailableInChildAsync(fixture.Paths));
        using var lease = await CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, CancellationToken.None);
    }

    private static async Task<bool> LockAvailableInChildAsync(InstallationPaths paths)
    {
        var start = new System.Diagnostics.ProcessStartInfo("python3") { UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import fcntl, sys; f = open(sys.argv[1], 'r+'); fcntl.lockf(f, fcntl.LOCK_EX | fcntl.LOCK_NB, 1)");
        start.ArgumentList.Add(Path.Combine(paths.OperationsRoot, "deployment.lock"));
        start.RedirectStandardError = true;
        using var process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("backup-aborted", true)]
    [DataRow("backup-partial-missing", true)]
    [DataRow("lifecycle-failed", true)]
    [DataRow(null, false)]
    public async Task WorkerAdmitsSettledBackupButFailsNewRequestForActiveBackup(string? code, bool allowed)
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed);
        var operation = await CameraAgentLifecycleManager.BeginAsync(fixture.Request(LifecycleOperationKind.Backup), fixture.Paths,
            LifecycleOperationKind.Backup, fixture.Manifest, CancellationToken.None);
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            Status = allowed ? InstallationStatus.Failed : InstallationStatus.Running,
            FailureCode = code,
            FailureMessage = code is null ? null : "settled"
        }, CancellationToken.None);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var client = new SettledBackupControlClient();
        var journal = new InstallerControlJournal(Path.Combine(fixture.Paths.OperationsRoot, "settled-control-test.json"));
        await new InstallerControlWorker(client, journal,
            token => CameraAgentLifecycleManager.AcquireControlDeploymentAsync(fixture.Paths, token),
            TimeProvider.System, _ => { }).PollAsync(CancellationToken.None);
        var receipt = journal.Receipts.Single();
        Assert.AreEqual(allowed ? CameraAgentControlPhase.Succeeded : CameraAgentControlPhase.Failed, receipt.Phase);
        if (!allowed) Assert.AreEqual("lifecycle.installer-busy", receipt.Events[^1].Code);
        Assert.AreEqual(allowed ? 1 : 0, client.Drains);
        Assert.IsEmpty(journal.Unpublished);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
    }

    private sealed class SettledBackupControlClient : IInstallerControlClient
    {
        private CameraAgentControlReceipt? _current = CreateRequest();
        internal int Drains;
        private static CameraAgentControlReceipt CreateRequest()
        {
            var now = DateTimeOffset.UtcNow;
            var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Drain, "owner", "maintenance", 0, now);
            return new(request, CameraAgentControlPhase.Queued, [new(CameraAgentControlPhase.Queued, now, "lifecycle.queued")]);
        }
        public Task<CameraAgentControlSnapshot> ExchangeAsync(CameraAgentControlReceipt? receipt, CancellationToken token)
        {
            if (receipt is not null) _current = receipt.IsTerminal ? null : receipt;
            return Task.FromResult(new CameraAgentControlSnapshot(_current, false, Guid.NewGuid(), Drains > 0 ? "Paused" : "Running", 0, true, true, true));
        }
        public Task<LifecycleContinuity> DrainAsync(CameraAgentControlRequest request, CancellationToken token)
        { Drains++; return Task.FromResult(new LifecycleContinuity("Paused", 1, 42, 0, 0, 0, 0, 0, 0)); }
        public Task RestartAsync(CameraAgentControlReceipt receipt, CancellationToken token) => throw new InvalidOperationException();
        public Task<LifecycleContinuity> ConfirmDrainedAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task ResumeAsync(CameraAgentControlReceipt receipt, CancellationToken token) => throw new InvalidOperationException();
    }
}
