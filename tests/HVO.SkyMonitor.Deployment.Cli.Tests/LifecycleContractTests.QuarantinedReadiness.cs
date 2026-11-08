using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// An incomplete backup whose restart fails only because of a processing-lane quarantine can be resumed, committing
/// the archive only when its checksum still matches, or aborted without restoring instance data.
/// </summary>
public sealed partial class LifecycleContractTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_QuarantineBlocksRestart_ResumeWithTheFlagCommitsTheSameArchive()
    {
        using var fixture = await BackupFixtureAsync();
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 2);
        var owner = QuarantinedOwner(fixture);
        var lifecycle = new FakeLifecycleClient();
        await FailRestartAfterTheArchiveIsWrittenAsync(fixture, lifecycle, owner, discardPartial: false);

        var blocked = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            BackupAsync(fixture, lifecycle, resume: true, owner: owner));
        StringAssert.Contains(blocked.Message, "did not become ready", StringComparison.Ordinal);
        var stillBlocked = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsTrue(stillBlocked.MutationStarted);
        Assert.AreEqual(LifecycleOperationPhase.BackupRecorded, stillBlocked.Phase);

        var resumed = await BackupAsync(fixture, lifecycle, resume: true, allowQuarantinedReadiness: true, owner: owner);

        Assert.AreEqual("completed", resumed.Outcome);
        Assert.IsNotNull(resumed.Backup);
        var bytes = await File.ReadAllBytesAsync(resumed.Backup.Path);
        Assert.AreEqual(resumed.Backup.ArchiveSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.AreEqual(stillBlocked.BackupManifestSha256, resumed.Backup.ArchiveSha256);
        _ = await ReadArchiveAsync(resumed.Backup.Path);
        AssertBulkUntouched(bulk);
        var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(InstallationStatus.Completed, settled.Status);
        Assert.IsFalse(settled.MutationStarted);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_QuarantineAndDiscardedPartial_ResumeSettlesWithoutABackup()
    {
        using var fixture = await BackupFixtureAsync();
        var owner = QuarantinedOwner(fixture);
        var lifecycle = new FakeLifecycleClient();
        await FailRestartAfterTheArchiveIsWrittenAsync(fixture, lifecycle, owner, discardPartial: true);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "*.partial"));

        var settled = await BackupAsync(fixture, lifecycle, resume: true, allowQuarantinedReadiness: true, owner: owner);

        Assert.AreEqual("aborted", settled.Outcome);
        Assert.IsNull(settled.Backup);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsFalse(journal.MutationStarted);
        Assert.AreEqual(CameraAgentLifecycleManager.MissingPartialBackupCode, journal.FailureCode);
        var again = await BackupAsync(fixture, lifecycle, allowQuarantinedReadiness: true, owner: owner);
        Assert.AreEqual("completed", again.Outcome);
        Assert.AreEqual(
            Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(again.Backup!.Path))),
            again.Backup.ArchiveSha256);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_DamagedPartial_IsNotCommitted_AndAbortLeavesTheInstanceData()
    {
        using var fixture = await BackupFixtureAsync();
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 2);
        var owner = QuarantinedOwner(fixture);
        var lifecycle = new FakeLifecycleClient();
        await FailRestartAfterTheArchiveIsWrittenAsync(fixture, lifecycle, owner, discardPartial: false);
        var partial = Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "*.partial").Single();
        var damaged = await File.ReadAllBytesAsync(partial);
        damaged[damaged.Length / 2] ^= 0x40;
        await File.WriteAllBytesAsync(partial, damaged);

        var mismatch = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            BackupAsync(fixture, lifecycle, resume: true, allowQuarantinedReadiness: true, owner: owner));
        Assert.IsFalse(string.IsNullOrWhiteSpace(mismatch.Message));
        var still = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsTrue(still.MutationStarted);
        Assert.IsFalse(File.Exists(partial[..^BackupVaultFiles.PartialSuffix.Length] + BackupVaultFiles.RecordSuffix));

        var aborted = await BackupAsync(fixture, lifecycle, abort: true, allowQuarantinedReadiness: true, owner: owner);

        Assert.AreEqual("aborted", aborted.Outcome);
        Assert.IsNull(aborted.Backup);
        Assert.IsFalse(File.Exists(partial));
        AssertBulkUntouched(bulk);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsFalse(journal.MutationStarted);
        Assert.AreEqual(CameraAgentLifecycleManager.AbandonedBackupCode, journal.FailureCode);
        var replacement = await BackupAsync(fixture, lifecycle, allowQuarantinedReadiness: true, owner: owner);
        Assert.AreEqual("completed", replacement.Outcome);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_AbortStoppedInstanceOnAHostWithTooFewCpus_LeavesThePartialAndJournalUntouched()
    {
        using var fixture = await BackupFixtureAsync();
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var compose = (await File.ReadAllTextAsync(composePath))
            .Replace("cpus: \"1\"", "cpus: \"2\"", StringComparison.Ordinal)
            .Replace("mem_limit: 1G", "mem_limit: 4G", StringComparison.Ordinal);
        SafeFileSystem.WriteTextAtomic(composePath, compose);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath,
            fixture.Manifest with
            {
                Resources = CameraAgentContainerResources.Default,
                ComposeModelSha256 = ComposeDeployment.ComputeSha256(compose)
            },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        var installation = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath, installation with { ComposeModelSha256 = ComposeDeployment.ComputeSha256(compose) },
            DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 2);
        var owner = QuarantinedOwner(fixture);
        var lifecycle = new FakeLifecycleClient();
        await FailRestartAfterTheArchiveIsWrittenAsync(fixture, lifecycle, owner, discardPartial: false);
        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        fixture.Runner.Cpus = 1;
        var partial = Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "*.partial").Single();
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var (ups, stops, resumes) = (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount, lifecycle.ResumeCount);

        var refused = await Assert.ThrowsExactlyAsync<HostPrerequisiteException>(() =>
            BackupAsync(fixture, lifecycle, abort: true, allowQuarantinedReadiness: true, owner: owner));

        AssertRefusedFindings(refused, [HostCpusInsufficient], "the aborted backup's restart");
        Assert.IsTrue(File.Exists(partial));
        Assert.IsFalse(fixture.Runner.Running);
        Assert.AreEqual((ups, stops, resumes), (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount, lifecycle.ResumeCount));
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "abort admission changed the partial, journal or instance data");
        AssertBulkUntouched(bulk);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsync_AbortStoppedInstanceWithoutCpuQuota_RestartsWithoutCpuLimitsAndWarns(bool existingLog)
    {
        using var fixture = await BackupFixtureAsync();
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 2);
        var owner = QuarantinedOwner(fixture);
        var lifecycle = new FakeLifecycleClient();
        await FailRestartAfterTheArchiveIsWrittenAsync(fixture, lifecycle, owner, discardPartial: false);
        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        fixture.Runner.CpuCfsQuota = false;
        var composePath = Path.Combine(fixture.Paths.ConfigRoot, "compose", "compose.yml");
        var compose = await File.ReadAllTextAsync(composePath);
        var started = fixture.Runner.StartedModels.Count;
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var logPath = InstallLogPath(fixture, operation.OperationId);
        const string earlierEvidence = "Earlier backup evidence.\n";
        if (existingLog)
        {
            SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(logPath)!);
            SafeFileSystem.WriteTextAtomic(logPath, earlierEvidence);
        }
        using var error = new StringWriter();
        var loggedBeforeDiscard = false;
        BackupVault.Override = _ => new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            DiscardFault = () =>
            {
                AssertRecoveryWarning(File.ReadAllText(logPath), [CpuQuotaUnsupported], "the log before partial discard", "BACKUP");
                loggedBeforeDiscard = true;
            }
        };
        LifecycleResult result;
        try
        {
            result = await BackupAsync(fixture, lifecycle, abort: true, allowQuarantinedReadiness: true, owner: owner, error: error);
        }
        finally
        {
            BackupVault.Override = null;
        }

        Assert.AreEqual("aborted", result.Outcome);
        Assert.IsNull(result.Backup);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "*.partial"));
        Assert.AreEqual(CameraAgentContainerResources.WithoutCpuLimits(compose), fixture.Runner.StartedModels.Skip(started).Single());
        Assert.AreEqual(compose, await File.ReadAllTextAsync(composePath));
        AssertRecoveryWarning(error.ToString(), [CpuQuotaUnsupported], "standard error", "BACKUP");
        Assert.IsTrue(loggedBeforeDiscard);
        var log = await File.ReadAllTextAsync(logPath);
        AssertRecoveryWarning(log, [CpuQuotaUnsupported], "the retained install log", "BACKUP");
        if (existingLog) StringAssert.StartsWith(log, earlierEvidence, StringComparison.Ordinal);
        Assert.IsTrue(result.Warnings?.Any(static warning => warning.Code == CpuQuotaUnsupported) ?? false);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsFalse(journal.MutationStarted);
        Assert.AreEqual(CameraAgentLifecycleManager.AbandonedBackupCode, journal.FailureCode);
        AssertBulkUntouched(bulk);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BackupAsync_RestartBoundaryChangesAfterAdmission_LeavesThePartialAndInstanceUntouched(bool abort)
    {
        using var fixture = await BackupFixtureAsync();
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 2);
        var owner = QuarantinedOwner(fixture);
        var lifecycle = new FakeLifecycleClient();
        await FailRestartAfterTheArchiveIsWrittenAsync(fixture, lifecycle, owner, discardPartial: false);
        var retained = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        fixture.Runner.CpuCfsQuota = false;
        // Admission sees an unmutated preparation that needs no restart. Another backup with the same request can
        // supersede that preparation while this invocation waits for the locks, retaining a required restart instead.
        await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.LifecycleStatePath, retained with
        {
            OperationId = Guid.NewGuid(),
            Phase = LifecycleOperationPhase.Prepared,
            Status = InstallationStatus.Running,
            MutationStarted = false,
            PreMutationContinuity = null,
            BackupManifestSha256 = null
        }, DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
        var beforeLocked = Array.Empty<string>();
        var runner = new BeforeLockedBackupReadRunner(fixture.Runner, async () =>
        {
            await SafeFileSystem.WriteJsonAtomicAsync(fixture.Paths.LifecycleStatePath, retained,
                DeploymentJsonContext.Default.LifecycleOperationState, CancellationToken.None);
            beforeLocked = FileSystemActivity.Snapshot(fixture.Root);
        });
        var partial = Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "*.partial").Single();
        var (ups, stops, resumes) = (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount, lifecycle.ResumeCount);

        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Backup) with
            {
                Abort = abort,
                Resume = !abort,
                AllowQuarantinedReadiness = true
            }, runner, _ => lifecycle, _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        StringAssert.Contains(failure.Message, "restart that was not checked", StringComparison.Ordinal);
        Assert.IsNotEmpty(beforeLocked, "the journal interleaving was not exercised");
        Assert.IsTrue(File.Exists(partial));
        Assert.IsFalse(fixture.Runner.Running);
        Assert.AreEqual((ups, stops, resumes), (fixture.Runner.ComposeUpCount, fixture.Runner.ComposeStopCount, lifecycle.ResumeCount));
        FileSystemActivity.AssertUnchanged(beforeLocked, FileSystemActivity.Snapshot(fixture.Root), "recovery mutated before its restart was admitted");
        AssertBulkUntouched(bulk);
    }

    /// <summary>Changes the journal after admission, immediately before the locked journal reread.</summary>
    private sealed class BeforeLockedBackupReadRunner(IProcessRunner inner, Func<Task> beforeLockedRead) : IProcessRunner
    {
        private int infoCalls;

        public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            IReadOnlyList<string> command = arguments;
            if (command is ["--host", _, ..]) command = command.Skip(2).ToArray();
            if (fileName == "docker" && command is ["info", ..] && ++infoCalls == 2)
            {
                await beforeLockedRead().ConfigureAwait(false);
            }
            return await inner.RunAsync(fileName, arguments, cancellationToken).ConfigureAwait(false);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_QuarantinedReadiness_ReachesHealthVerification()
    {
        var daemonArchitecture = ReleaseAcquirer.HostImageArchitecture() == "amd64" ? "arm64" : "amd64";
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed,
            HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner,
            daemonArchitecture: daemonArchitecture);
        using var release = CandidateRelease(fixture, out var candidateImageId, platforms: [$"linux/{daemonArchitecture}"]);
        fixture.Runner.CandidateRawIngressSchema = "13";
        var owner = QuarantinedOwner(fixture);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with
            {
                Release = release.Folder,
                MigrationBackwardCompatible = true,
                AllowQuarantinedReadiness = true
            },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(candidateImageId, result.Image!.ImageId);
        Assert.IsTrue(owner.QuarantinedReadinessSeen);
        Assert.IsGreaterThan(0, owner.QuarantinedReadinessCalls);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_QuarantinedReadiness_VerifiesAndRestoresThePreviousImage()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed,
            CameraAgentReplayProfile.LocalRunner);
        using var release = CandidateRelease(fixture, out var candidateImageId);
        fixture.Runner.CandidateRawIngressSchema = "13";
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Upgrade) with { Release = release.Folder, MigrationBackwardCompatible = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual(candidateImageId, upgraded.Image!.ImageId);
        var owner = QuarantinedOwner(fixture);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback) with { AllowQuarantinedReadiness = true },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, result.Image!.ImageId);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.IsTrue(owner.QuarantinedReadinessSeen);
        Assert.IsGreaterThan(0, owner.QuarantinedReadinessCalls);
    }

    private static FakeOwnerClient QuarantinedOwner(LifecycleFixture fixture)
        => new(fixture.ApplicationIdentity) { RequireQuarantinedReadiness = true };

    /// <summary>
    /// Runs a backup until the archive is durable, then fails the restart the way a quarantined <c>/health</c> does.
    /// <paramref name="discardPartial"/> false leaves the partial in place, as a discard that itself failed would.
    /// </summary>
    private static async Task FailRestartAfterTheArchiveIsWrittenAsync(
        LifecycleFixture fixture,
        FakeLifecycleClient lifecycle,
        FakeOwnerClient owner,
        bool discardPartial)
    {
        BackupVault.Override = _ => new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            DiscardFault = discardPartial ? null : static () => throw new IOException("read-only file system")
        };
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, lifecycle, owner: owner));
            StringAssert.Contains(failure.Message, "did not become ready", StringComparison.Ordinal);
            StringAssert.Contains(failure.Message, "--abort", StringComparison.Ordinal);
        }
        finally
        {
            BackupVault.Override = null;
        }
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsTrue(journal.MutationStarted);
        Assert.AreEqual(LifecycleOperationPhase.BackupRecorded, journal.Phase);
        Assert.IsFalse(string.IsNullOrEmpty(journal.BackupManifestSha256));
    }
}
