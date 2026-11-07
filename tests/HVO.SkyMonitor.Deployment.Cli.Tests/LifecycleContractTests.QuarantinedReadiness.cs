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
