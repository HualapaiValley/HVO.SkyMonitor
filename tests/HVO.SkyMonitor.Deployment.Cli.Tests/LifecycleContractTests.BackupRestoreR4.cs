using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// PR-1369-R4 (#1353). Putting the live owner in place is idempotent: a restored account that collides with the live
/// owner and that the live store still has, as one an earlier restore of the same archive renamed and locked out, takes
/// its live state, so restoring an archive again changes nothing more (F26). An account that held only the live owner's
/// email gets a replacement name too (F27). The helper's backup-folder form creates nothing above the folder (F28).
/// </summary>
public sealed partial class LifecycleContractTests
{
    /// <summary>
    /// F26: the same archive restored a second time, over the instance the first restore left, and that second restore
    /// resumed after an interruption at its database step, each end as the first did: the live owner in place with its
    /// live credentials, and the backup's owner renamed, without its email and locked out.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestoreAsync_SameArchiveRestoredAgain_PutsTheLiveOwnerInPlaceAgain(bool resumed)
    {
        using var fixture = await BackupFixtureAsync();
        var identity = CameraAgentLayoutIdentity(fixture);
        SeedAccounts(identity, ("owner", "admin", fixture.Manifest.OwnerEmail, true));
        WriteOwnerSettings(fixture, fixture.Manifest.OwnerEmail, "archived-temporary-password", passwordFile: false);
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        SeedAccounts(identity, ("new-owner", "admin", NewOwnerEmail, true));
        WriteOwnerSettings(fixture, NewOwnerEmail, "live-temporary-password", passwordFile: false);
        await SetOwnerEmailAsync(fixture, NewOwnerEmail);
        await UninstallAsync(fixture);
        Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup!.Archive)).Outcome);
        AssertLiveOwnerInPlace(identity);
        var images = new List<CrashImage>();
        using var captured = new TemporaryDirectory();
        CaptureBackupCrashImages(fixture, captured.Path, images);
        var journal = CameraAgentLifecycleManager.JournalRecorded;
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            journal(state);
            if (state.StateSnapshotRestoredUtc is not null && state.Phase == LifecycleOperationPhase.Mutating)
                images[^1] = images[^1] with { Step = "journal:StateSnapshotRestored" };
        };
        LifecycleResult? again = null;
        InstallerException? failed = null;
        try
        {
            again = await RestoreAsync(fixture, backup.Backup.Archive);
        }
        catch (InstallerException exception)
        {
            failed = exception;
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        if (resumed)
        {
            RestoreCrashImage(fixture, images.Single(static image => image.Step == "journal:StateSnapshotRestored"));
            again = await RestoreAsync(fixture, backup.Backup.Archive, resume: true);
        }
        else if (failed is not null)
        {
            Assert.Fail($"{failed.Message} {failed.InnerException?.Message}");
        }

        Assert.AreEqual("completed", again!.Outcome);
        AssertLiveOwnerInPlace(identity);
    }

    /// <summary>
    /// F27: an account the backup holds with the live owner's email, under another sign-in name, gives up the email and
    /// takes a replacement name too, as every account that gives way to the live owner does.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_RestoredAccountWithOnlyTheLiveOwnersEmail_TakesAReplacementName()
    {
        using var fixture = await BackupFixtureAsync();
        var identity = CameraAgentLayoutIdentity(fixture);
        SeedAccounts(identity, ("owner", "admin", fixture.Manifest.OwnerEmail, true), ("helper", "helper", NewOwnerEmail, false));
        WriteOwnerSettings(fixture, fixture.Manifest.OwnerEmail, "archived-temporary-password", passwordFile: false);
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        SeedAccounts(identity, ("new-owner", "admin", NewOwnerEmail, true));
        WriteOwnerSettings(fixture, NewOwnerEmail, "live-temporary-password", passwordFile: false);
        await SetOwnerEmailAsync(fixture, NewOwnerEmail);
        await UninstallAsync(fixture);

        Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup!.Archive)).Outcome);

        Assert.AreEqual(1L, Scalar(identity,
            "SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'helper' AND UserName = 'removed-helper' AND NormalizedUserName = 'REMOVED-HELPER' AND NormalizedEmail IS NULL AND PasswordHash IS NULL;"),
            "the account that held only the live owner's email kept its sign-in name");
        AssertLiveOwnerInPlace(identity);
    }

    /// <summary>
    /// F28: the helper's backup-folder form needs the product root to exist already, and creates nothing when it does not.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void PrepareBackupFolder_ProductRootAbsent_IsRefusedAndCreatesNothing()
    {
        using var scratch = new TemporaryDirectory();
        var productRoot = Path.Combine(scratch.Path, "product");

        var refused = Assert.ThrowsExactly<InstallerException>(() =>
            PrivilegedPreparation.PrepareBackupFolder(productRoot, NativeLinux.getuid(), NativeLinux.getgid()));

        StringAssert.Contains(refused.Message, productRoot, StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(productRoot), "the helper created the product root");
    }

    /// <summary>
    /// The live owner's account with its live password, the only site owner and the only account with its email, and the
    /// backup's owner, which held its sign-in name, renamed, without a password and locked out.
    /// </summary>
    private static void AssertLiveOwnerInPlace(string identity)
    {
        Assert.AreEqual(1L, Scalar(identity,
            $"SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'new-owner' AND NormalizedEmail = '{NewOwnerEmail.ToUpperInvariant()}' AND UserName = 'admin' AND PasswordHash = 'hash-new-owner' AND IsSiteOwner = 1;"),
            "the live owner is not in place with its live password");
        Assert.AreEqual(1L, Scalar(identity,
            "SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'owner' AND UserName = 'removed-owner' AND PasswordHash IS NULL AND IsSiteOwner = 0 AND LockoutEnd = '9999-12-31 23:59:59+00:00';"),
            "the backup's owner is not renamed, without a password and locked out");
        Assert.AreEqual(1L, Scalar(identity, $"SELECT COUNT(*) FROM AspNetUsers WHERE NormalizedEmail = '{NewOwnerEmail.ToUpperInvariant()}';"));
        Assert.AreEqual(1L, Scalar(identity, "SELECT COUNT(*) FROM AspNetUsers WHERE IsSiteOwner = 1;"));
    }
}
