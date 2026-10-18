using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// PR-1369-R2 (#1353). Backups are owner-only (owner decision 2026-10-18): a backup or restore runs as the instance's
/// service account and is refused, before anything changes, run as root or as anyone else (F12, F13). The live owner
/// settings win over the archived ones (F14). A carried store moves as one unit with its sidecars (F15). A resumed swap
/// syncs both folders of every rename again (F16). A resumed restore reserves room only for what is left (F17). The
/// backup folder is checked before anything is written (F18). The host's limits apply to a backup only when it starts the
/// instance (F19).
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string AdminEmailSetting = "LocalIdentity__AdminEmail";
    private const string NewOwnerEmail = "new-owner@example.test";

    /// <summary>
    /// A backup runs as the instance's service account. Run as root, which is what <c>sudo</c> gives it, or as another
    /// user, it is refused before anything changes, naming the account to run it as; run as that account it records the
    /// account's own verified IDs.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("root")]
    [DataRow("another-user")]
    [DataRow("service-account")]
    public async Task BackupAsync_RunAs_IsTheServiceAccountsAlone(string runAs)
    {
        using var fixture = await BackupFixtureAsync();
        var (uid, gid) = runAs switch
        {
            "root" => (0u, 0u),
            "another-user" => (fixture.Uid + 1, fixture.Gid + 1),
            _ => (fixture.Uid, fixture.Gid)
        };
        var lifecycle = new FakeLifecycleClient();
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var invocations = fixture.Runner.InvocationCount;

        Task<LifecycleResult> Backup() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Backup), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), uid, gid, CancellationToken.None, error: TextWriter.Null);

        if (runAs == "service-account")
        {
            var backup = await Backup();
            Assert.AreEqual("completed", backup.Outcome);
            var archive = Path.Combine(fixture.Root, BackupVault.FolderName, backup.Backup!.Archive);
            var (manifest, _) = await ReadArchiveAsync(archive);
            Assert.AreEqual((fixture.Uid, fixture.Gid), (manifest.RuntimeUid, manifest.RuntimeGid));
            using var record = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(archive + BackupVaultFiles.RecordSuffix));
            Assert.AreEqual(fixture.Uid, record.RootElement.GetProperty("runtimeUid").GetUInt32());
            Assert.AreEqual(fixture.Gid, record.RootElement.GetProperty("runtimeGid").GetUInt32());
            return;
        }
        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(Backup);

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        StringAssert.Contains(refused.Message, Environment.UserName, StringComparison.Ordinal);
        StringAssert.Contains(refused.Message, $"uid {fixture.Uid}", StringComparison.Ordinal);
        StringAssert.Contains(refused.Message, "not with sudo or as root", StringComparison.Ordinal);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the refused backup changed files");
        Assert.AreEqual(invocations, fixture.Runner.InvocationCount, "the refused backup ran Docker");
        Assert.AreEqual(0, lifecycle.PauseCount);
    }

    /// <summary>
    /// A restore runs as the service account too: over a preserved instance, as the account that owns it; refused run as
    /// root or as another user, before anything changes, naming that account.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("root")]
    [DataRow("another-user")]
    public async Task RestoreAsync_NotTheServiceAccount_IsRefusedBeforeAnythingChangesNamingIt(string runAs)
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        var (uid, gid) = runAs == "root" ? (0u, 0u) : (fixture.Uid + 1, fixture.Gid + 1);
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Restore) with { Archive = backup.Backup!.Archive }, fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), uid, gid, CancellationToken.None,
            error: TextWriter.Null));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        StringAssert.Contains(refused.Message, Environment.UserName, StringComparison.Ordinal);
        StringAssert.Contains(refused.Message, $"uid {fixture.Uid}", StringComparison.Ordinal);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the refused restore changed files");
    }

    /// <summary>
    /// An absent instance belongs to the account its backup records. A backup whose record names another account is
    /// refused before anything is created: the restore never makes files for an owner it did not run as.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_AbsentInstanceWhoseBackupRecordsAnotherOwner_IsRefusedBeforeAnythingChanges()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        SafeFileSystem.MakeTreeOwnerWritable(fixture.Paths.InstanceRoot);
        Directory.Delete(fixture.Paths.InstanceRoot, recursive: true);
        var recordPath = Path.Combine(fixture.Root, BackupVault.FolderName, backup.Backup!.Archive) + BackupVaultFiles.RecordSuffix;
        var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!;
        record["runtimeUid"] = fixture.Uid + 1;
        SafeFileSystem.WriteTextAtomic(recordPath, record.ToJsonString());
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => RestoreAsync(fixture, backup.Backup.Archive));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        StringAssert.Contains(refused.Message, $"uid {fixture.Uid + 1}", StringComparison.Ordinal);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the refused restore changed files");
        Assert.IsFalse(Directory.Exists(fixture.Paths.InstanceRoot), "the refused restore created the instance root");
    }

    /// <summary>
    /// F14: the startup seeder makes the account <c>LocalIdentity__AdminEmail</c> names the site owner, creating it from the
    /// temporary password when it is missing. When those settings changed after the backup, as a purge and a fresh install
    /// with a new owner leaves them, the restore keeps the live ones and the owner email the live records name, so the
    /// demoted owner stays demoted, and it says so. No password reaches the report.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_LiveOwnerSettingsChangedSinceTheBackup_KeepsTheLiveOnesAndReportsThem()
    {
        using var fixture = await BackupFixtureAsync();
        var secrets = Path.Combine(fixture.Paths.ConfigRoot, "secrets");
        var temporaryPassword = Path.Combine(fixture.Paths.ConfigRoot, "owner-bootstrap", "temporary-password");
        WriteOwnerSettings(fixture, fixture.Manifest.OwnerEmail, "archived-temporary-password", passwordFile: true);
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        WriteOwnerSettings(fixture, NewOwnerEmail, "live-temporary-password", passwordFile: false);
        await SetOwnerEmailAsync(fixture, NewOwnerEmail);
        Execute(CameraAgentLayoutIdentity(fixture), "UPDATE AspNetUsers SET IsSiteOwner = 0 WHERE Id = 'owner';");
        await UninstallAsync(fixture);
        using var error = new StringWriter();

        var restored = await RestoreAsync(fixture, backup.Backup!.Archive, error: error);

        Assert.AreEqual("completed", restored.Outcome);
        Assert.AreEqual(NewOwnerEmail, await File.ReadAllTextAsync(Path.Combine(secrets, AdminEmailSetting)));
        Assert.AreEqual("true", await File.ReadAllTextAsync(Path.Combine(secrets, "LocalIdentity__AllowMissingAdminPassword")));
        Assert.IsFalse(File.Exists(Path.Combine(secrets, "LocalIdentity__AdminPasswordFile")), "the archived password setting came back");
        Assert.AreEqual("live-temporary-password", await File.ReadAllTextAsync(temporaryPassword));
        Assert.AreEqual(NewOwnerEmail, (await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None)).OwnerEmail);
        Assert.AreEqual(NewOwnerEmail, (await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None)).OwnerEmail);
        Assert.AreEqual(0L, Scalar(CameraAgentLayoutIdentity(fixture), "SELECT IsSiteOwner FROM AspNetUsers WHERE Id = 'owner';"));
        var kept = restored.Warnings?.SingleOrDefault(static warning => warning.Code == "restore-live-owner-settings-kept");
        Assert.IsNotNull(kept, "the restore did not report that it kept the live owner settings");
        foreach (var name in new[] { AdminEmailSetting, "LocalIdentity__AdminPasswordFile", "LocalIdentity__AllowMissingAdminPassword", "temporary-password", "ownerEmail" })
        {
            StringAssert.Contains(kept.Message, name, StringComparison.Ordinal);
        }
        StringAssert.Contains(error.ToString(), AdminEmailSetting, StringComparison.Ordinal);
        foreach (var secret in new[] { "archived-temporary-password", "live-temporary-password" })
        {
            Assert.DoesNotContain(secret, error.ToString());
            Assert.DoesNotContain(secret, kept.Message);
        }
    }

    /// <summary>
    /// F14: with no live instance to compare, the restore says loudly that the backup's owner settings are in force again.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_AbsentInstance_WarnsLoudlyThatTheBackupsOwnerSettingsAreInForceAgain()
    {
        using var fixture = await BackupFixtureAsync();
        WriteOwnerSettings(fixture, fixture.Manifest.OwnerEmail, "archived-temporary-password", passwordFile: true);
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        SafeFileSystem.MakeTreeOwnerWritable(fixture.Paths.InstanceRoot);
        Directory.Delete(fixture.Paths.InstanceRoot, recursive: true);
        using var error = new StringWriter();

        var restored = await RestoreAsync(fixture, backup.Backup!.Archive, error: error);

        Assert.AreEqual("completed", restored.Outcome);
        var warning = error.ToString().Split(Environment.NewLine).SingleOrDefault(static line => line.Contains(AdminEmailSetting, StringComparison.Ordinal));
        Assert.IsNotNull(warning, error.ToString());
        StringAssert.StartsWith(warning, "WARNING:", StringComparison.Ordinal);
        Assert.IsTrue(restored.Warnings?.Any(static entry => entry.Code == "restore-backup-owner-settings") ?? false);
    }

    /// <summary>
    /// F15: a store carried from the live instance moves with its WAL, journal and shared-memory files as one unit. However
    /// an interruption splits them, the resumed restore moves what is left, so no committed transaction is lost.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_InterruptedBetweenTheFilesOfACarriedStore_ResumesTheWholeStore()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var fleet = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore);
        SeedWalStore(fleet);
        var unit = new[] { string.Empty }.Concat(CameraAgentStateLayout.SqliteSidecarSuffixes).Where(suffix => File.Exists(fleet + suffix)).ToArray();
        CollectionAssert.Contains(unit, "-wal", "the store under test has no WAL");
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
        Assert.AreEqual("completed", first.Outcome);
        var carried = images.Single(static image => image.Step == $"restore-carried:{FleetStore}");
        var previousFleet = CameraAgentStateSnapshot.StorePath(
            Path.Combine(fixture.Paths.InstanceRoot, BackupInventory.RestoreFolderName, first.OperationId!.Value.ToString("D"), "previous", "state"), FleetStore);

        var violations = new List<string>();
        // Every way an interruption can split the unit: some of its files moved, the rest still kept aside.
        for (var mask = 1; mask < (1 << unit.Length) - 1; mask++)
        {
            var moved = unit.Where((_, index) => (mask & (1 << index)) != 0).ToArray();
            RestoreCrashImage(fixture, carried);
            foreach (var suffix in unit.Except(moved)) File.Move(fleet + suffix, previousFleet + suffix);
            var name = $"moved [{string.Join(",", moved.Select(static suffix => suffix.Length == 0 ? "db" : suffix))}]";
            try
            {
                Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup.Archive, resume: true)).Outcome, name);
            }
            catch (InstallerException exception)
            {
                violations.Add($"{name}: resume failed: {exception.Message}");
                continue;
            }
            foreach (var suffix in unit.Where(suffix => suffix != "-shm"))
            {
                if (!File.Exists(fleet + suffix)) violations.Add($"{name}: '{FleetStore}{suffix}' was not carried");
            }
            if (ReadWalStore(fleet) != 60) violations.Add($"{name}: the store lost the transaction its WAL held");
        }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// F16: an interruption after a rename of the swap and before it is synced leaves it undurable, so every resumed pass
    /// syncs both folders of every rename again, those it finds already done included.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_ResumedSwap_SyncsBothFoldersOfEveryRenameAgain()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
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
        var operationRoot = Path.Combine(fixture.Paths.InstanceRoot, BackupInventory.RestoreFolderName, first.OperationId!.Value.ToString("D"));
        var folders = new[] { fixture.Paths.InstanceRoot, Path.Combine(operationRoot, "staged"), Path.Combine(operationRoot, "previous") };
        // A rename and its sync are one step apart: the image after an entry's swap, or after a rename before its sync.
        var swapping = images.Where(static image => image.Step.StartsWith("restore-swapped:", StringComparison.Ordinal) ||
                                                    image.Step.StartsWith("restore-renamed:", StringComparison.Ordinal) &&
                                                    !image.Step.Contains("/.fleet/", StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(swapping);

        var violations = new List<string>();
        foreach (var image in swapping)
        {
            RestoreCrashImage(fixture, image);
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
                Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup.Archive, resume: true)).Outcome, image.Step);
            }
            finally
            {
                NativeLinux.DirectoryFlushed = flushed;
                ResetCrashHooks();
            }
            // Before the resumed restore records anything, the swap it resumed is durable in every folder it renames in.
            var beforeJournal = events.TakeWhile(static entry => entry != "journal").ToArray();
            foreach (var folder in folders.Where(folder => !beforeJournal.Contains(folder)))
            {
                violations.Add($"{image.Step}: the resumed swap did not sync {folder}");
            }
        }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    /// <summary>
    /// F17: a restore resumed after its databases were restored reserves no room for their copies: that phase is done.
    /// Resumed before it, it still does.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_ResumedAfterTheDatabaseRestore_ReservesNoRoomForItsCopies()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
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
        try
        {
            Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup!.Archive)).Outcome);
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        var original = CameraAgentLifecycleManager.BackupHost;
        // The headroom, and nothing for the databases' copies.
        CameraAgentLifecycleManager.BackupHost = new SmallDisk(256L * 1024 * 1024 + 1);
        try
        {
            RestoreCrashImage(fixture, images.Single(static image => image.Step == "journal:StateSnapshotRestored"));
            Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup.Archive, resume: true)).Outcome);

            RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:StateRestorePlanRecorded"));
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => RestoreAsync(fixture, backup.Backup.Archive, resume: true));
            StringAssert.StartsWith(refused.Message, "There is not enough free space", StringComparison.Ordinal);
        }
        finally
        {
            CameraAgentLifecycleManager.BackupHost = original;
        }
    }

    /// <summary>
    /// F18: the backup folder is checked before anything is written, even when it does not exist yet, and nothing is
    /// created: it must be the service account's own, mode 0700, in folders no one else can change.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("ancestor-group-writable")]
    [DataRow("folder-group-readable")]
    public async Task BackupAsync_UnsafeBackupFolder_IsRefusedBeforeAnythingIsWritten(string shape)
    {
        using var fixture = await BackupFixtureAsync();
        var folder = Path.Combine(fixture.Root, BackupVault.FolderName);
        var rootMode = File.GetUnixFileMode(fixture.Root);
        if (shape == "ancestor-group-writable")
        {
            File.SetUnixFileMode(fixture.Root, rootMode | UnixFileMode.GroupWrite);
        }
        else
        {
            Directory.CreateDirectory(folder);
            File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        }
        var lifecycle = new FakeLifecycleClient();
        var before = FileSystemActivity.Snapshot(fixture.Root);
        try
        {
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, lifecycle));

            Assert.IsTrue(refused.IsRefusal, refused.Message);
            StringAssert.Contains(refused.Message, shape == "ancestor-group-writable" ? fixture.Root : folder, StringComparison.Ordinal);
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the refused backup changed files");
            Assert.AreEqual(0, lifecycle.PauseCount);
            Assert.AreEqual(shape != "ancestor-group-writable", Directory.Exists(folder));
        }
        finally
        {
            File.SetUnixFileMode(fixture.Root, rootMode);
        }
    }

    /// <summary>F18: an ancestor others can write is safe when its sticky bit keeps them from renaming what is not theirs.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_StickyAncestor_IsAccepted()
    {
        using var fixture = await BackupFixtureAsync();
        var rootMode = File.GetUnixFileMode(fixture.Root);
        File.SetUnixFileMode(fixture.Root, rootMode | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite | UnixFileMode.StickyBit);
        try
        {
            var backup = await BackupAsync(fixture, new FakeLifecycleClient());

            Assert.AreEqual("completed", backup.Outcome);
            Assert.AreEqual(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.Combine(fixture.Root, BackupVault.FolderName)));
        }
        finally
        {
            File.SetUnixFileMode(fixture.Root, rootMode);
        }
    }

    /// <summary>
    /// F19: a backup of a stopped instance starts nothing, so a host that could not run the instance does not stop it: it
    /// goes ahead with no warning, fresh or resumed. A running instance is still held to the host's limits.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_StoppedInstanceOnAHostWithTooFewCpus_GoesAheadWithoutTheHostLimits()
    {
        using var fixture = await BackupFixtureAsync();
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, fixture.Manifest with { Resources = CameraAgentContainerResources.Default },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        fixture.Runner.Cpus = 1;

        await Assert.ThrowsExactlyAsync<HostPrerequisiteException>(() => BackupAsync(fixture, new FakeLifecycleClient()));

        fixture.Runner.Reboot(fixture.Runner.ActiveImageId, wasRunning: false);
        var images = new List<CrashImage>();
        using var captured = new TemporaryDirectory();
        CaptureBackupCrashImages(fixture, captured.Path, images);
        using var error = new StringWriter();
        LifecycleResult stopped;
        try
        {
            stopped = await BackupAsync(fixture, new FakeLifecycleClient(), error: error);
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        Assert.AreEqual("completed", stopped.Outcome);
        Assert.IsFalse(fixture.Runner.Running, "the backup started an instance that was stopped");
        Assert.DoesNotContain("GOES AHEAD", error.ToString());
        Assert.IsFalse(stopped.Warnings?.Any(static warning => warning.Code.StartsWith("docker-", StringComparison.Ordinal)) ?? false);

        // Resumed once its archive is complete, the journal says the instance was not running, so it is not held to them either.
        var image = images.First(static image => image.Step == "journal:BackupRecorded");
        RestoreCrashImage(fixture, image);
        fixture.Runner.Reboot(image.ActiveImageId, wasRunning: false);
        Assert.AreEqual("completed", (await BackupAsync(fixture, new FakeLifecycleClient(), resume: true)).Outcome);
    }

    /// <summary>Writes the owner settings the startup seeder reads: the owner's email, its temporary password, and the password settings.</summary>
    private static void WriteOwnerSettings(LifecycleFixture fixture, string ownerEmail, string temporaryPassword, bool passwordFile)
    {
        var secrets = Path.Combine(fixture.Paths.ConfigRoot, "secrets");
        var bootstrap = Path.Combine(fixture.Paths.ConfigRoot, "owner-bootstrap");
        foreach (var directory in new[] { secrets, bootstrap })
        {
            if (!Directory.Exists(directory)) SafeFileSystem.CreateOwnerDirectory(directory);
        }
        SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, AdminEmailSetting), ownerEmail);
        SafeFileSystem.WriteTextAtomic(Path.Combine(secrets, "LocalIdentity__AllowMissingAdminPassword"), passwordFile ? "false" : "true");
        var passwordSetting = Path.Combine(secrets, "LocalIdentity__AdminPasswordFile");
        if (passwordFile) SafeFileSystem.WriteTextAtomic(passwordSetting, "/run/hvo-private/owner-password");
        else File.Delete(passwordSetting);
        SafeFileSystem.WriteTextAtomic(Path.Combine(bootstrap, "temporary-password"), temporaryPassword);
    }

    /// <summary>Gives the instance's records another owner email, as a fresh install with a new owner writes them.</summary>
    private static async Task SetOwnerEmailAsync(LifecycleFixture fixture, string ownerEmail)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        var result = await CameraAgentLifecycleManager.ReadResultAsync(fixture.Paths.ResultPath, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, manifest with { OwnerEmail = ownerEmail }, DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ResultPath, result with { OwnerEmail = ownerEmail }, DeploymentJsonContext.Default.InstallationResult, CancellationToken.None);
    }

    /// <summary>
    /// A fleet-status store in WAL mode whose last transaction, which takes its counter to 60, is only in its WAL: the
    /// files are copied while the connection that wrote them is still open, so nothing has checkpointed it.
    /// </summary>
    private static void SeedWalStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var scratch = path + ".scratch";
        using (var connection = new SqliteConnection($"Data Source={scratch};Pooling=False"))
        {
            connection.Open();
            void Run(string sql)
            {
                using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned statements, not input.
                command.CommandText = sql;
#pragma warning restore CA2100
                command.ExecuteNonQuery();
            }
            Run("CREATE TABLE fleet_status_metadata(metadata_key INTEGER PRIMARY KEY, next_sequence INTEGER NOT NULL, schema_version INTEGER NOT NULL);" +
                "INSERT INTO fleet_status_metadata VALUES(1, 1, 1);");
            Run("PRAGMA journal_mode=WAL;");
            Run("PRAGMA wal_autocheckpoint=0;");
            Run("UPDATE fleet_status_metadata SET next_sequence = 60 WHERE metadata_key = 1;");
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                File.Copy(scratch + suffix, path + suffix);
                File.SetUnixFileMode(path + suffix, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" }) File.Delete(scratch + suffix);
    }

    /// <summary>The store's counter as SQLite reads it, its WAL applied.</summary>
    private static long ReadWalStore(string path)
    {
        var copy = Path.Combine(Path.GetTempPath(), $"hvo-wal-{Guid.NewGuid():N}.db");
        try
        {
            foreach (var suffix in new[] { string.Empty, "-wal" })
            {
                if (File.Exists(path + suffix)) File.Copy(path + suffix, copy + suffix);
            }
            using var connection = new SqliteConnection($"Data Source={copy};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" }) File.Delete(copy + suffix);
        }
    }
}
