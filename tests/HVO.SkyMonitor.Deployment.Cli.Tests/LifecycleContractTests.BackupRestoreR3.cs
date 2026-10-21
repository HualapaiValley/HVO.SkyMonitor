using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// PR-1369-R3 (#1353). Before anything changes, backup and restore may use the installer's folder-preparation helper only
/// to create the backup folder (F20) and an absent instance root (F23), both the service account's; restore leaves the
/// machine install record to <c>reinstall</c> (F23). The live owner's account takes the place of any restored account that
/// holds its email or sign-in name (F25), under a free replacement name (F21). A resumed restore syncs both folders of
/// every carried store again (F22).
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode ReadOnlyFolder = UnixFileMode.UserRead | UnixFileMode.UserExecute;

    /// <summary>
    /// F20: the first backup under a product root the service account cannot write, as the production root is, creates the
    /// backup folder through the installer's preparation helper before anything changes, owned by the account with mode
    /// 0700. A helper that fails refuses the backup, exit 3, with nothing changed.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BackupAsync_FirstBackupUnderAProductRootItCannotWrite_CreatesTheFolderThroughThePreparationHelper(bool helperSucceeds)
    {
        using var fixture = await BackupFixtureAsync();
        var folder = Path.Combine(fixture.Root, BackupVault.FolderName);
        var lifecycle = new FakeLifecycleClient();
        var calls = new List<IReadOnlyList<string>>();
        fixture.Runner.Sudo = (arguments, _) =>
        {
            calls.Add(arguments);
            Assert.AreEqual(0, lifecycle.PauseCount, "the helper ran after the backup changed something");
            if (!helperSucceeds) return Task.FromResult(new ProcessResult(1, string.Empty, "sudo: a terminal is required to read the input"));
            // As the helper does, as root: the folder, the account's, mode 0700, in the root the account cannot write.
            File.SetUnixFileMode(fixture.Root, OwnerOnly);
            Directory.CreateDirectory(folder);
            File.SetUnixFileMode(folder, OwnerOnly);
            File.SetUnixFileMode(fixture.Root, ReadOnlyFolder);
            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        };
        File.SetUnixFileMode(fixture.Root, ReadOnlyFolder);
        var before = FileSystemActivity.Snapshot(fixture.Root);
        try
        {
            if (helperSucceeds)
            {
                Assert.AreEqual("completed", (await BackupAsync(fixture, lifecycle)).Outcome);
                Assert.AreEqual(OwnerOnly, File.GetUnixFileMode(folder));
            }
            else
            {
                var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, lifecycle));
                Assert.IsTrue(refused.IsRefusal, refused.Message);
                StringAssert.Contains(refused.Message, "a terminal is required", StringComparison.Ordinal);
                FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the refused backup changed files");
                Assert.AreEqual(0, lifecycle.PauseCount);
            }
        }
        finally
        {
            File.SetUnixFileMode(fixture.Root, OwnerOnly);
        }
        var call = calls.Single();
        CollectionAssert.IsSubsetOf(
            new[] { "internal-prepare", "--product-root", fixture.Root, "--backups", "--uid", fixture.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            call.ToArray());
        CollectionAssert.DoesNotContain(call.ToArray(), "--instance-id");
    }

    /// <summary>
    /// F23: a restore leaves the machine install record to <c>reinstall</c>, which settles it from the restored manifest.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_LeavesTheMachineInstallRecordToReinstall()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        var location = InstallRecordLocation.For(fixture.Root, InstallRequest.DefaultProductRoot);
        File.Delete(location.Path);

        Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup!.Archive)).Outcome);

        Assert.IsFalse(File.Exists(location.Path), "the restore wrote the machine install record");
        var reinstalled = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Reinstall), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", reinstalled.Outcome);
        var entry = (await InstallRecordStore.ReadAsync(location, CancellationToken.None))!.Instances.Single();
        Assert.AreEqual((fixture.InstanceId, InstanceLifecycleCondition.Installed), (entry.InstanceId, entry.Condition));
    }

    /// <summary>
    /// F25: a fresh install that reused the owner's email made a new Identity account for it. The live owner's account takes
    /// the place of the restored one that holds its email and sign-in name, which is renamed off both and locked out as a
    /// removed account is, so the live owner still signs in with its live password; it is reported.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_LiveOwnerReinstalledWithTheSameEmail_ReplacesTheRestoredAccount()
    {
        using var fixture = await BackupFixtureAsync();
        var identity = CameraAgentLayoutIdentity(fixture);
        var email = fixture.Manifest.OwnerEmail;
        SeedAccounts(identity, ("owner", "admin", email, true));
        WriteOwnerSettings(fixture, email, "archived-temporary-password", passwordFile: false);
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        SeedAccounts(identity, ("owner-2", "admin", email, true));
        await UninstallAsync(fixture);

        var restored = await RestoreAsync(fixture, backup.Backup!.Archive);

        Assert.AreEqual("completed", restored.Outcome);
        Assert.AreEqual(1L, Scalar(identity,
            $"SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'owner-2' AND NormalizedEmail = '{email.ToUpperInvariant()}' AND UserName = 'admin' AND PasswordHash = 'hash-owner-2' AND IsSiteOwner = 1 AND LockoutEnd IS NULL;"),
            "the live owner cannot sign in with its live password");
        Assert.AreEqual(1L, Scalar(identity, $"SELECT COUNT(*) FROM AspNetUsers WHERE NormalizedEmail = '{email.ToUpperInvariant()}';"));
        Assert.AreEqual(1L, Scalar(identity,
            "SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'owner' AND UserName = 'removed-owner' AND NormalizedEmail IS NULL AND PasswordHash IS NULL AND IsSiteOwner = 0 AND LockoutEnd = '9999-12-31 23:59:59+00:00';"),
            "the restored account was not renamed and locked out as a removed account is");
        var kept = restored.Warnings!.Single(static warning => warning.Code == "restore-live-owner-settings-kept");
        StringAssert.Contains(kept.Message, "account owner-2, the live owner", StringComparison.Ordinal);
        StringAssert.Contains(kept.Message,
            System.Text.Json.JsonEncodedText.Encode("account owner held the live owner's email and sign-in name").ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// F21: the name an account gives up for the live owner is one no other account has, chosen in the same transaction, so
    /// a taken <c>removed-&lt;id&gt;</c> fails neither the first run nor a resumed one.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RestoreAsync_ReplacementSignInNameTaken_ChoosesAFreeOne(bool resumed)
    {
        using var fixture = await BackupFixtureAsync();
        var identity = CameraAgentLayoutIdentity(fixture);
        SeedAccounts(identity, ("owner", "admin", fixture.Manifest.OwnerEmail, true), ("squatter", "removed-owner", "squatter@example.test", false));
        WriteOwnerSettings(fixture, fixture.Manifest.OwnerEmail, "archived-temporary-password", passwordFile: false);
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        SeedAccounts(identity, ("new-owner", "admin", NewOwnerEmail, true));
        WriteOwnerSettings(fixture, NewOwnerEmail, "live-temporary-password", passwordFile: false);
        await SetOwnerEmailAsync(fixture, NewOwnerEmail);
        await UninstallAsync(fixture);
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
        LifecycleResult? restored = null;
        InstallerException? failed = null;
        try
        {
            restored = await RestoreAsync(fixture, backup.Backup!.Archive);
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
            // Resumed after the databases were restored, the pass that brings the live owner over runs again.
            RestoreCrashImage(fixture, images.Single(static image => image.Step == "journal:StateSnapshotRestored"));
            restored = await RestoreAsync(fixture, backup.Backup!.Archive, resume: true);
        }
        else if (failed is not null)
        {
            Assert.Fail($"{failed.Message} {failed.InnerException?.Message}");
        }

        Assert.AreEqual("completed", restored!.Outcome);
        Assert.AreEqual(1L, Scalar(identity, "SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'owner' AND UserName = 'removed-owner-2' AND NormalizedUserName = 'REMOVED-OWNER-2';"));
        Assert.AreEqual(1L, Scalar(identity, "SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'squatter' AND UserName = 'removed-owner';"));
        Assert.AreEqual(1L, Scalar(identity, "SELECT COUNT(*) FROM AspNetUsers WHERE Id = 'new-owner' AND UserName = 'admin' AND IsSiteOwner = 1;"));
    }

    /// <summary>
    /// F22: an interruption right after the last rename of a carried store, before its sync, leaves that rename undurable,
    /// so every resumed pass syncs both folders of every carried store again, one already moved whole included, before it
    /// records anything.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_ResumedAfterTheLastCarriedRename_SyncsBothFoldersOfTheCarriedStore()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var fleet = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore);
        Execute(fleet,
            "CREATE TABLE fleet_status_metadata(metadata_key INTEGER PRIMARY KEY, next_sequence INTEGER NOT NULL, schema_version INTEGER NOT NULL);" +
            "INSERT INTO fleet_status_metadata VALUES(1, 60, 1);");
        await UninstallAsync(fixture);
        var images = new List<CrashImage>();
        using var captured = new TemporaryDirectory();
        CaptureBackupCrashImages(fixture, captured.Path, images);
        LifecycleResult first;
        try
        {
            first = await RestoreAsync(fixture, backup.Backup!.Archive);
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        var target = Path.GetDirectoryName(fleet)!;
        var source = Path.GetDirectoryName(CameraAgentStateSnapshot.StorePath(
            Path.Combine(fixture.Paths.InstanceRoot, BackupInventory.RestoreFolderName, first.OperationId!.Value.ToString("D"), "previous", "state"), FleetStore))!;
        var lastCarried = images.Last(image => image.Step.StartsWith("restore-renamed:" + target + "/", StringComparison.Ordinal));
        RestoreCrashImage(fixture, lastCarried);
        var events = new List<string>();
        var flushed = NativeLinux.DirectoryFlushed;
        NativeLinux.DirectoryFlushed = path =>
        {
            lock (events) events.Add(Path.GetFullPath(path).TrimEnd('/'));
        };
        CameraAgentLifecycleManager.JournalRecorded = _ =>
        {
            lock (events) events.Add("journal");
        };
        try
        {
            Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup.Archive, resume: true)).Outcome);
        }
        finally
        {
            NativeLinux.DirectoryFlushed = flushed;
            ResetCrashHooks();
        }

        var beforeJournal = events.TakeWhile(static entry => entry != "journal").ToArray();
        CollectionAssert.Contains(beforeJournal, target, "the resumed restore did not sync the folder the store was carried into");
        CollectionAssert.Contains(beforeJournal, source, "the resumed restore did not sync the folder the store was carried from");
    }

    /// <summary>
    /// Makes the Identity store hold exactly <paramref name="accounts"/>, with Identity's own columns, each with its own
    /// password hash, as an install leaves them.
    /// </summary>
    private static void SeedAccounts(string database, params (string Id, string UserName, string Email, bool Owner)[] accounts)
        => Execute(database,
            "DELETE FROM AspNetUserTokens; DELETE FROM AspNetUserRoles; DELETE FROM AspNetUserClaims; DROP TABLE AspNetUsers;" +
            "CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY, UserName TEXT, NormalizedUserName TEXT UNIQUE, Email TEXT, NormalizedEmail TEXT, " +
            "PasswordHash TEXT, SecurityStamp TEXT, ConcurrencyStamp TEXT, PasswordChangeRequired INTEGER NOT NULL, IsSiteOwner INTEGER NOT NULL, " +
            "LockoutEnabled INTEGER NOT NULL, LockoutEnd TEXT);" +
            string.Concat(accounts.Select(static account =>
                $"INSERT INTO AspNetUsers VALUES('{account.Id}', '{account.UserName}', '{account.UserName.ToUpperInvariant()}', '{account.Email}', " +
                $"'{account.Email.ToUpperInvariant()}', 'hash-{account.Id}', 'stamp-{account.Id}', 'c-{account.Id}', 0, {(account.Owner ? 1 : 0)}, 1, NULL);")));
}
