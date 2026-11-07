using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsync_AbortDiscardFails_StillRestartsAndKeepsRecoveryIntent(bool restartFails)
    {
        using var fixture = await BackupFixtureAsync();
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 2);
        var owner = QuarantinedOwner(fixture);
        var lifecycle = new FakeLifecycleClient();
        await FailRestartAfterTheArchiveIsWrittenAsync(fixture, lifecycle, owner, discardPartial: false);
        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        var before = await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath);
        var resumes = lifecycle.ResumeCount;
        BackupVault.Override = _ => new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            DiscardFault = static () => throw new IOException("read-only file system")
        };
        InstallerException failure;
        try
        {
            failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(
                fixture, lifecycle, abort: true, allowQuarantinedReadiness: !restartFails, owner: owner));
        }
        finally
        {
            BackupVault.Override = null;
        }

        Assert.IsTrue(fixture.Runner.Running, "the discard failure skipped the required start");
        Assert.AreEqual(resumes + (restartFails ? 0 : 1), lifecycle.ResumeCount);
        StringAssert.Contains(failure.Message, "read-only file system", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, "--resume", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, "--abort instead of --resume", StringComparison.Ordinal);
        if (restartFails) StringAssert.Contains(failure.Message, "restarting the instance also failed", StringComparison.Ordinal);
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(fixture.Paths.LifecycleStatePath));
        Assert.HasCount(1, Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "*.partial"));
        AssertBulkUntouched(bulk);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_AbortAtPublishedArchiveCrashBoundary_RefusesAndResumeCompletes()
    {
        using var fixture = await BackupFixtureAsync();
        var images = new List<CrashImage>();
        using var captured = new TemporaryDirectory();
        CaptureBackupCrashImages(fixture, captured.Path, images);
        try
        {
            await BackupAsync(fixture, new FakeLifecycleClient());
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        RestoreCrashImage(fixture, images.Single(static image => image.Step == "backup-committed"));
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(LifecycleOperationPhase.BackupRecorded, journal.Phase);
        var archive = Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "*.tar.gz").Single();
        var bytes = await File.ReadAllBytesAsync(archive);
        Assert.IsTrue(File.Exists(archive + BackupVaultFiles.RecordSuffix));
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, new FakeLifecycleClient(), abort: true));

        Assert.IsTrue(refused.IsRefusal);
        StringAssert.Contains(refused.Message, "--resume", StringComparison.Ordinal);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "abort changed a published backup or its recovery journal");
        var resumed = await BackupAsync(fixture, new FakeLifecycleClient(), resume: true);
        Assert.AreEqual("completed", resumed.Outcome);
        Assert.IsNotNull(resumed.Backup);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(archive));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsync_AbortUnmutatedRunningInstance_DoesNotAdmitOrWarnAboutAStart(bool noCpuQuota)
    {
        using var fixture = await BackupFixtureAsync();
        SafeFileSystem.CreateOwnerDirectory(fixture.Paths.OperationsRoot);
        var operation = await CameraAgentLifecycleManager.BeginAsync(
            fixture.Request(LifecycleOperationKind.Backup), fixture.Paths, LifecycleOperationKind.Backup, fixture.Manifest, CancellationToken.None);
        Assert.IsNull(operation.PreMutationContinuity);
        Assert.IsFalse(operation.MutationStarted);
        if (noCpuQuota) fixture.Runner.CpuCfsQuota = false;
        else fixture.Runner.Cpus = 0;
        var lifecycle = new FakeLifecycleClient();
        var (ups, stops) = (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount);
        using var error = new StringWriter();

        var result = await BackupAsync(fixture, lifecycle, abort: true, error: error);

        Assert.AreEqual("aborted", result.Outcome);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.AreEqual((ups, stops), (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount));
        Assert.AreEqual(0, lifecycle.ResumeCount);
        Assert.IsFalse(File.Exists(InstallLogPath(fixture, operation.OperationId)));
        Assert.DoesNotContain("THE BACKUP GOES AHEAD", error.ToString());
        Assert.IsFalse(result.Warnings!.Any(static warning => warning.Code == CpuQuotaUnsupported));
    }
}
