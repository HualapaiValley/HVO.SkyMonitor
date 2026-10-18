using System.Formats.Tar;
using System.IO.Compression;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// <c>cameraagent backup</c> and <c>cameraagent restore</c> (#1353): a root-only, self-verifying archive of the whole
/// instance, written while the instance is stopped and proved whole before it is committed, and a restore that refuses
/// anything partial, unrecorded or damaged with nothing changed, puts the databases back through the snapshot restore,
/// revokes again what the live instance revoked, and resumes after a power loss at any durable step.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string LiveKey = "key-live";
    private const string RevokedKey = "key-revoked";
    private static readonly string[] BackupDurableSteps =
    [
        "journal:Prepared", "journal:Drained", "snapshot:identity/cameraagent_identity.db", "journal:StateSnapshotRecorded",
        "archive-written", "journal:BackupRecorded", "backup-committed", "journal:Committed"
    ];
    private static readonly string[] RestoreDurableSteps =
    [
        "journal:Prepared", "restore-staged", "journal:CandidateValidated", "journal:Mutating", "restore-swapped:state",
        "journal:StateRestorePlanRecorded", "journal:CandidateVerified", "journal:Committed"
    ];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_RunningInstance_WritesAWholeArchiveWhileStoppedAndPutsTheInstanceBack()
    {
        using var fixture = await BackupFixtureAsync();
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 3);
        var lifecycle = new FakeLifecycleClient();
        using var error = new StringWriter();

        var result = await BackupAsync(fixture, lifecycle, error: error);

        Assert.AreEqual("completed", result.Outcome);
        Assert.IsNotNull(result.Backup);
        Assert.AreEqual(1, lifecycle.PauseCount, "capture is paused once, for the snapshot and the archive");
        Assert.AreEqual(1, lifecycle.ResumeCount);
        Assert.IsTrue(fixture.Runner.Running, "the instance runs again");
        var archive = Path.Combine(fixture.Root, BackupVault.FolderName, result.Backup.Archive);
        Assert.IsTrue(File.Exists(archive));
        Assert.IsTrue(File.Exists(archive + BackupVaultFiles.RecordSuffix), "a committed archive has its record");
        Assert.IsFalse(File.Exists(archive + BackupVaultFiles.PartialSuffix));
        Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(archive));
        var (manifest, names) = await ReadArchiveAsync(archive);
        Assert.AreEqual(fixture.InstanceId, manifest.InstanceId);
        CollectionAssert.Contains(names, "instance/instance-manifest.json");
        CollectionAssert.Contains(names, "instance/config/lifecycle-control/token");
        CollectionAssert.DoesNotContain(names, "instance/state/identity/cameraagent_identity.db", "a store comes from the snapshot");
        CollectionAssert.Contains(names, BackupInventory.SnapshotPrefix(manifest.StateSnapshot) + "manifest.json");
        foreach (var file in bulk.Keys)
        {
            CollectionAssert.Contains(names, "instance/" + Path.GetRelativePath(fixture.Paths.InstanceRoot, file).Replace('\\', '/'),
                "bulk data is copied by the explicit backup");
        }
        CollectionAssert.Contains(manifest.Omitted.ToArray(), ".deployment.lock");
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(InstallationStatus.Completed, journal.Status);
        Assert.IsFalse(Directory.Exists(SnapshotsRoot(fixture, journal.OperationId)), "the backup's own snapshot is pruned once archived");
        StringAssert.Contains(error.ToString(), "Backed up", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_Plan_ChangesNothingAndNamesTheArchive()
    {
        using var fixture = await BackupFixtureAsync();
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var planned = await BackupAsync(fixture, new FakeLifecycleClient(), plan: true);

        Assert.AreEqual("planned", planned.Outcome);
        Assert.IsTrue(planned.Plan!.Items.Any(static item => item.Kind == "file" && item.Target.EndsWith(".tar.gz", StringComparison.Ordinal)));
        Assert.IsTrue(fixture.Runner.Running);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the backup plan changed the product root");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_NotEnoughSpace_IsRefusedWithNothingChanged()
    {
        using var fixture = await BackupFixtureAsync();
        var lifecycle = new FakeLifecycleClient();
        var before = FileSystemActivity.Snapshot(fixture.Root);
        var original = CameraAgentLifecycleManager.BackupHost;
        CameraAgentLifecycleManager.BackupHost = new SmallDisk(1024 * 1024);
        try
        {
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, lifecycle));

            Assert.IsTrue(refused.IsRefusal, refused.Message);
            StringAssert.StartsWith(refused.Message, "There is not enough free space", StringComparison.Ordinal);
        }
        finally
        {
            CameraAgentLifecycleManager.BackupHost = original;
        }
        Assert.AreEqual(0, lifecycle.PauseCount);
        Assert.IsTrue(fixture.Runner.Running);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "a refused backup changed the product root");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("disk-full")]
    [DataRow("cancelled")]
    public async Task BackupAsync_StoppedWhileWriting_DiscardsThePartialAndPutsTheInstanceBack(string fault)
    {
        using var fixture = await BackupFixtureAsync();
        SeedBulk(fixture.Paths, filesPerTree: 3);
        var lifecycle = new FakeLifecycleClient();
        using var cancellation = new CancellationTokenSource();
        var vault = new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            FailWriteAfter = 64 * 1024,
            WriteFault = fault == "disk-full"
                ? () => throw new IOException("No space left on device")
                : () =>
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
        };
        BackupVault.Override = (_, _) => vault;
        try
        {
            if (fault == "disk-full")
            {
                var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, lifecycle));
                Assert.IsFalse(failure.IsRefusal, failure.Message);
                StringAssert.StartsWith(failure.Message, "The backup failed, and nothing was kept", StringComparison.Ordinal);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(() => BackupAsync(fixture, lifecycle, cancellation: cancellation.Token));
            }
        }
        finally
        {
            BackupVault.Override = null;
        }
        Assert.IsEmpty(Directory.GetFiles(Path.Combine(fixture.Root, BackupVault.FolderName), "hvo-sky-*"), "the partial is discarded");
        Assert.IsTrue(fixture.Runner.Running, "the instance runs again");
        Assert.AreEqual(lifecycle.PauseCount, lifecycle.ResumeCount);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(InstallationStatus.Failed, journal.Status);
        Assert.IsFalse(journal.MutationStarted, "a failed backup blocks no later operation");
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_PowerLossAtEveryDurableStep_ResumeKeepsOnlyAWholeArchive()
    {
        using var fixture = await BackupFixtureAsync();
        var images = new List<CrashImage>();
        using var captured = new TemporaryDirectory();
        CaptureBackupCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await BackupAsync(fixture, new FakeLifecycleClient())).Outcome);
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        CollectionAssert.IsSubsetOf(BackupDurableSteps, images.Select(static image => image.Step).ToArray());

        var violations = new List<string>();
        foreach (var image in images.Where(static image => image.Step != "journal:Completed"))
        {
            RestoreCrashImage(fixture, image);
            var lifecycle = new FakeLifecycleClient();
            // Before the backup marked anything mutated, --resume runs it from the start; after, it keeps the archive
            // only once the archive was complete.
            var interrupted = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
            var kept = interrupted is not { MutationStarted: true } ||
                       interrupted.Phase is LifecycleOperationPhase.BackupRecorded or LifecycleOperationPhase.Committed;
            try
            {
                var result = await BackupAsync(fixture, lifecycle, resume: true);
                if (!kept) violations.Add($"{image.Step}: resume kept an archive written before it was complete");
                if (!File.Exists(Path.Combine(fixture.Root, BackupVault.FolderName, result.Backup!.Archive + BackupVaultFiles.RecordSuffix)))
                    violations.Add($"{image.Step}: the kept archive has no record");
            }
            catch (InstallerException exception) when (!kept)
            {
                if (!exception.Message.StartsWith("The backup was interrupted before its archive was complete", StringComparison.Ordinal))
                    violations.Add($"{image.Step}: {exception.Message}");
                var vaultFolder = Path.Combine(fixture.Root, BackupVault.FolderName);
                if (Directory.Exists(vaultFolder) && Directory.EnumerateFiles(vaultFolder, "hvo-sky-*").Any())
                    violations.Add($"{image.Step}: an incomplete archive was left in the backup folder");
            }
            catch (InstallerException exception)
            {
                violations.Add($"{image.Step}: resume failed: {exception.Message}");
            }
            if (image.Running && !fixture.Runner.Running) violations.Add($"{image.Step}: the instance was left stopped");
            var journal = await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None);
            if (journal!.MutationStarted) violations.Add($"{image.Step}: the journal still demands --resume");
        }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_UninstalledInstance_PutsTheBackupBackAndRevokesAgainWhatWasRevokedSince()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var atBackup = ReadStoreFacts(fixture.Paths);
        // After the backup: more captures, one API key revoked, and the owner's password changed.
        AddRawRows(fixture.Paths, 5);
        Execute(CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot),
            $"DELETE FROM AspNetUserTokens WHERE Name = '{RevokedKey}';" +
            "UPDATE AspNetUsers SET PasswordHash = 'hash-after', SecurityStamp = 'stamp-after' WHERE Id = 'owner';");
        await UninstallAsync(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture.Paths.ConfigRoot, "stray-after-backup.txt"), "not in the backup");
        using var error = new StringWriter();

        var restored = await RestoreAsync(fixture, backup.Backup!.Archive, error: error);

        Assert.AreEqual("completed", restored.Outcome);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, restored.LifecycleCondition);
        StringAssert.Contains(restored.ResumeCommand!, "cameraagent reinstall", StringComparison.Ordinal);
        CollectionAssert.AreEqual(atBackup, ReadStoreFacts(fixture.Paths), "the databases are the backup's");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Paths.ConfigRoot, "stray-after-backup.txt")), "the files are exactly the backup's");
        var (keys, owner) = ReadCredentials(fixture.Paths);
        CollectionAssert.AreEquivalent(new[] { LiveKey }, keys, "the key revoked after the backup is revoked again");
        Assert.AreEqual(("hash-after", "stamp-after"), owner, "the owner keeps the password the live instance had");
        Assert.AreEqual(1, restored.Restore!.ApiKeysRevoked);
        Assert.AreEqual(1, restored.Restore.AccountsReset);
        StringAssert.Contains(error.ToString(), "revoked them again", StringComparison.Ordinal);
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Paths.InstanceRoot, BackupInventory.RestoreFolderName)), "the staging is removed");
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(InstanceLifecycleCondition.Uninstalled, manifest.LifecycleCondition);
        Assert.AreEqual(restored.OperationId, manifest.LastLifecycleOperationId);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(InstallationStatus.Completed, journal.Status);
        Assert.IsNotNull(journal.StateRestorePlan, "the databases went back through the snapshot restore");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_AbsentInstance_WarnsLoudlyThatCredentialsRevokedSinceAreValidAgain()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        SafeFileSystem.MakeTreeOwnerWritable(fixture.Paths.InstanceRoot);
        Directory.Delete(fixture.Paths.InstanceRoot, recursive: true);
        using var error = new StringWriter();

        var restored = await RestoreAsync(fixture, backup.Backup!.Archive, error: error);

        Assert.AreEqual("completed", restored.Outcome);
        Assert.IsTrue(restored.Restore!.NoLiveCredentialStore);
        StringAssert.StartsWith(error.ToString().Split(Environment.NewLine).Single(static line => line.Contains("credential", StringComparison.Ordinal)),
            "WARNING: there was no live credential store to compare", StringComparison.Ordinal);
        var (keys, _) = ReadCredentials(fixture.Paths);
        CollectionAssert.AreEquivalent(new[] { LiveKey, RevokedKey }, keys);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_Plan_ProvesTheArchiveAndChangesNothing()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var planned = await RestoreAsync(fixture, backup.Backup!.Archive, plan: true);

        Assert.AreEqual("planned", planned.Outcome);
        Assert.IsTrue(planned.Plan!.Items.Any(static item => item.Kind == "databases"));
        StringAssert.Contains(
            planned.Plan.Items.Single(static item => item.Kind == "credentials").Detail!, "revoked again", StringComparison.Ordinal);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "the restore plan changed the product root");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_InstalledInstance_IsRefusedWithNothingChanged()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var before = FileSystemActivity.Snapshot(fixture.Root);

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => RestoreAsync(fixture, backup.Backup!.Archive));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), "a refused restore changed the product root");
    }

    /// <summary>A partial, unrecorded or damaged archive is refused before anything changes, plan and command alike.</summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("truncated")]
    [DataRow("flipped-byte")]
    [DataRow("unrecorded")]
    [DataRow("partial-only")]
    [DataRow("record-of-other-bytes")]
    [DataRow("entry-not-in-manifest")]
    [DataRow("manifest-not-last")]
    public async Task RestoreAsync_PartialOrDamagedArchive_IsRefusedWithNothingChanged(string damage)
    {
        ArgumentNullException.ThrowIfNull(damage);
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        var archive = Path.Combine(fixture.Root, BackupVault.FolderName, backup.Backup!.Archive);
        await DamageAsync(archive, damage);
        var before = FileSystemActivity.Snapshot(fixture.Root);

        foreach (var plan in new[] { true, false })
        {
            var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => RestoreAsync(fixture, backup.Backup.Archive, plan: plan));

            Assert.IsTrue(refused.IsRefusal, $"{damage}: {refused.Message}");
            FileSystemActivity.AssertUnchanged(before, FileSystemActivity.Snapshot(fixture.Root), $"{damage}: a refused restore changed the product root");
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("read-fails")]
    [DataRow("cancelled")]
    public async Task RestoreAsync_StoppedWhileStaging_RemovesTheStagingAndReplacesNothing(string fault)
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        var instanceBefore = InstanceContent(fixture);
        using var cancellation = new CancellationTokenSource();
        // The first read proves the archive; the second, which stages it, stops part of the way.
        var vault = new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            FailReadNumber = 2,
            FailReadAfter = 16 * 1024,
            ReadFault = fault == "read-fails"
                ? () => throw new IOException("Input/output error")
                : () =>
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
        };
        BackupVault.Override = (_, _) => vault;
        try
        {
            if (fault == "read-fails")
            {
                var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => RestoreAsync(fixture, backup.Backup!.Archive));
                Assert.IsFalse(failure.IsRefusal, failure.Message);
                StringAssert.StartsWith(failure.Message, "The restore failed before it replaced anything", StringComparison.Ordinal);
            }
            else
            {
                await Assert.ThrowsAsync<OperationCanceledException>(
                    () => RestoreAsync(fixture, backup.Backup!.Archive, cancellation: cancellation.Token));
            }
        }
        finally
        {
            BackupVault.Override = null;
        }
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Paths.InstanceRoot, BackupInventory.RestoreFolderName)));
        FileSystemActivity.AssertUnchanged(instanceBefore, InstanceContent(fixture), "a restore stopped before its swap changed the instance");
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(InstallationStatus.Failed, journal.Status);
        Assert.IsFalse(journal.MutationStarted);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_PowerLossAtEveryDurableStep_ResumeConvergesOnTheBackup()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var atBackup = ReadStoreFacts(fixture.Paths);
        AddRawRows(fixture.Paths, 5);
        await UninstallAsync(fixture);
        var images = new List<CrashImage>();
        using var captured = new TemporaryDirectory();
        CaptureBackupCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup!.Archive)).Outcome);
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        CollectionAssert.IsSubsetOf(RestoreDurableSteps, images.Select(static image => image.Step).ToArray(),
            string.Join(", ", images.Select(static image => image.Step)));

        var violations = new List<string>();
        foreach (var image in images.Where(static image => image.Step != "journal:Completed"))
        {
            RestoreCrashImage(fixture, image);
            try
            {
                var result = await RestoreAsync(fixture, backup.Backup.Archive, resume: true);
                if (result.Outcome != "completed") violations.Add($"{image.Step}: resume ended {result.Outcome}");
            }
            catch (InstallerException exception) when (exception.Message.StartsWith("No incomplete matching lifecycle operation exists", StringComparison.Ordinal))
            {
                // A power loss before the restore's first journal record left nothing to resume; a new restore runs.
                await RestoreAsync(fixture, backup.Backup.Archive);
            }
            catch (InstallerException exception)
            {
                violations.Add($"{image.Step}: resume failed: {exception.Message} {exception.InnerException?.Message}");
                continue;
            }
            if (!ReadStoreFacts(fixture.Paths).SequenceEqual(atBackup)) violations.Add($"{image.Step}: the databases are not the backup's");
            if (Directory.Exists(Path.Combine(fixture.Paths.InstanceRoot, BackupInventory.RestoreFolderName)))
                violations.Add($"{image.Step}: the staging folder was left behind");
        }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    /// <summary>Everything under the instance root but the root itself and its lock, which a restore's lock and staging touch.</summary>
    private static string[] InstanceContent(LifecycleFixture fixture)
        => [.. FileSystemActivity.Snapshot(fixture.Paths.InstanceRoot)
            .Where(static line => !line.StartsWith(". ", StringComparison.Ordinal) && !line.StartsWith(".deployment.lock ", StringComparison.Ordinal))];

    private static async Task<LifecycleFixture> BackupFixtureAsync()
    {
        var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId, fixture.Uid, fixture.Gid);
        SeedStores(fixture.Paths);
        SeedCredentials(fixture.Paths);
        return fixture;
    }

    /// <summary>An owner with a password and two API keys, beside the operator account <see cref="SeedStores"/> wrote.</summary>
    private static void SeedCredentials(InstallationPaths paths)
        => Execute(CameraAgentStateLayout.IdentityDatabasePath(paths.StateRoot),
            "DROP TABLE AspNetUsers;" +
            "CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY, Email TEXT, PasswordHash TEXT, SecurityStamp TEXT, ConcurrencyStamp TEXT, PasswordChangeRequired INTEGER NOT NULL, IsSiteOwner INTEGER NOT NULL);" +
            "INSERT INTO AspNetUsers VALUES('owner', 'owner@example.test', 'hash-before', 'stamp-before', 'c1', 0, 1);" +
            "CREATE TABLE AspNetRoles(Id TEXT PRIMARY KEY, Name TEXT);" +
            "INSERT INTO AspNetRoles VALUES('role-operator', 'Operator');" +
            "CREATE TABLE AspNetUserRoles(UserId TEXT NOT NULL, RoleId TEXT NOT NULL, PRIMARY KEY(UserId, RoleId));" +
            "INSERT INTO AspNetUserRoles VALUES('owner', 'role-operator');" +
            "CREATE TABLE AspNetUserClaims(Id INTEGER PRIMARY KEY AUTOINCREMENT, UserId TEXT NOT NULL, ClaimType TEXT, ClaimValue TEXT);" +
            "INSERT INTO AspNetUserClaims(UserId, ClaimType, ClaimValue) VALUES('owner', 'hvo:authority', 'calibration-admin');" +
            "CREATE TABLE AspNetUserTokens(UserId TEXT NOT NULL, LoginProvider TEXT NOT NULL, Name TEXT NOT NULL, Value TEXT, PRIMARY KEY(UserId, LoginProvider, Name));" +
            $"INSERT INTO AspNetUserTokens VALUES('owner', '{RestoredCredentials.ApiKeyProvider}', '{LiveKey}', 'v1'), ('owner', '{RestoredCredentials.ApiKeyProvider}', '{RevokedKey}', 'v2');");

    private static (string[] Keys, (string, string) Owner) ReadCredentials(InstallationPaths paths)
    {
        using var connection = new SqliteConnection($"Data Source={CameraAgentStateLayout.IdentityDatabasePath(paths.StateRoot)};Mode=ReadOnly;Pooling=False");
        connection.Open();
        var keys = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Name FROM AspNetUserTokens ORDER BY Name;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
        }
        using var owner = connection.CreateCommand();
        owner.CommandText = "SELECT PasswordHash, SecurityStamp FROM AspNetUsers WHERE Id = 'owner';";
        using var row = owner.ExecuteReader();
        Assert.IsTrue(row.Read());
        return ([.. keys], (row.GetString(0), row.GetString(1)));
    }

    private static Task<LifecycleResult> BackupAsync(
        LifecycleFixture fixture,
        FakeLifecycleClient lifecycle,
        bool plan = false,
        bool resume = false,
        TextWriter? error = null,
        CancellationToken cancellation = default)
        => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Backup) with { DryRun = plan, Resume = resume },
            fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid,
            cancellation, error: error ?? TextWriter.Null);

    private static Task<LifecycleResult> RestoreAsync(
        LifecycleFixture fixture,
        string archive,
        bool plan = false,
        bool resume = false,
        TextWriter? error = null,
        CancellationToken cancellation = default)
        => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Restore) with { Archive = archive, DryRun = plan, Resume = resume },
            fixture.Runner, _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid,
            cancellation, error: error ?? TextWriter.Null);

    private static async Task UninstallAsync(LifecycleFixture fixture)
        => Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Uninstall), fixture.Runner, _ => new FakeLifecycleClient(), null,
            fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);

    private static async Task<(BackupArchiveManifest Manifest, string[] Names)> ReadArchiveAsync(string archive)
    {
        var names = new List<string>();
        await using var stream = File.OpenRead(archive);
        var manifest = await BackupArchive.ReadAsync(
            stream, (name, _, _, _, _) =>
            {
                names.Add(name);
                return Task.CompletedTask;
            }, null, CancellationToken.None);
        return (manifest, [.. names]);
    }

    /// <summary>Damages a committed archive the way a failed copy, a bad disk or a hand edit would.</summary>
    private static async Task DamageAsync(string archive, string damage)
    {
        var record = archive + BackupVaultFiles.RecordSuffix;
        switch (damage)
        {
            case "truncated":
                await RewriteAsync(archive, bytes => bytes[..(bytes.Length * 2 / 3)]);
                break;
            case "flipped-byte":
                await RewriteAsync(archive, bytes =>
                {
                    bytes[bytes.Length / 2] ^= 0x40;
                    return bytes;
                });
                break;
            case "unrecorded":
                File.Delete(record);
                break;
            case "partial-only":
                File.Move(archive, archive + BackupVaultFiles.PartialSuffix);
                break;
            case "record-of-other-bytes":
                await SetRecordShaAsync(archive, new string('0', 64));
                break;
            case "entry-not-in-manifest":
            case "manifest-not-last":
                await RepackAsync(archive, damage);
                break;
        }
    }

    private static async Task RewriteAsync(string path, Func<byte[], byte[]> change)
    {
        var bytes = change(await File.ReadAllBytesAsync(path));
        await File.WriteAllBytesAsync(path, bytes);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        // The record follows the new length, so only the content is wrong.
        var record = path + BackupVaultFiles.RecordSuffix;
        var text = await File.ReadAllTextAsync(record);
        var start = text.IndexOf("\"archiveLength\":", StringComparison.Ordinal) + "\"archiveLength\":".Length;
        var end = text.IndexOf(',', start);
        await File.WriteAllTextAsync(record, string.Concat(text.AsSpan(0, start), " ", bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture), text.AsSpan(end)));
    }

    /// <summary>Rewrites an archive with one entry added that its manifest does not name, or with its manifest moved first.</summary>
    private static async Task RepackAsync(string archive, string damage)
    {
        var entries = new List<(string Name, TarEntryType Type, UnixFileMode Mode, byte[]? Data)>();
        await using (var input = File.OpenRead(archive))
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        await using (var tar = new TarReader(gzip))
        {
            while (await tar.GetNextEntryAsync(copyData: true) is { } entry)
            {
                byte[]? data = null;
                if (entry.DataStream is { } stream)
                {
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    data = buffer.ToArray();
                }
                entries.Add((entry.Name, entry.EntryType, entry.Mode, data));
            }
        }
        if (damage == "entry-not-in-manifest")
            entries.Insert(1, ("instance/config/smuggled.txt", TarEntryType.RegularFile, UnixFileMode.UserRead | UnixFileMode.UserWrite, "x"u8.ToArray()));
        else
            entries.Insert(0, entries[^1]);
        if (damage == "manifest-not-last") entries.RemoveAt(entries.Count - 1);
        var temporary = archive + ".repack";
        await using (var output = File.Create(temporary))
        await using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        await using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, type, mode, data) in entries)
            {
                var entry = new PaxTarEntry(type, name) { Mode = mode };
                if (data is not null) entry.DataStream = new MemoryStream(data);
                await tar.WriteEntryAsync(entry);
            }
        }
        File.Move(temporary, archive, overwrite: true);
        await RewriteAsync(archive, static bytes => bytes);
        await SetRecordShaAsync(archive);
    }

    /// <summary>Sets the archive hash a record names; with the archive's own hash, only the archive's own proof can refuse it.</summary>
    private static async Task SetRecordShaAsync(string archive, string? sha256 = null)
    {
        var record = archive + BackupVaultFiles.RecordSuffix;
        sha256 ??= Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(archive)));
        var text = await File.ReadAllTextAsync(record);
        var start = text.IndexOf("\"archiveSha256\":", StringComparison.Ordinal);
        var quote = text.IndexOf('"', start + "\"archiveSha256\":".Length);
        await File.WriteAllTextAsync(record, string.Concat(text.AsSpan(0, quote + 1), sha256, text.AsSpan(quote + 65)));
        File.SetUnixFileMode(record, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void CaptureBackupCrashImages(LifecycleFixture fixture, string directory, List<CrashImage> images)
    {
        CaptureCrashImages(fixture, directory, images);
        var journal = CameraAgentLifecycleManager.JournalRecorded;
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            journal(state);
            if (state.StateRestorePlan is not null && state.StateSnapshotRestoredUtc is null)
                images[^1] = images[^1] with { Step = "journal:StateRestorePlanRecorded" };
        };
        CameraAgentLifecycleManager.BackupStepCompleted = step =>
        {
            var copy = Path.Combine(directory, images.Count.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
            CopyTree(fixture.Root, copy);
            images.Add(new CrashImage(step, copy, fixture.Runner.ActiveImageId, fixture.Runner.Running));
        };
    }

    private static void ResetBackupCrashHooks()
    {
        ResetCrashHooks();
        CameraAgentLifecycleManager.BackupStepCompleted = static _ => { };
    }

    /// <summary>A host whose filesystems have only <paramref name="availableBytes"/> free.</summary>
    private sealed class SmallDisk(long availableBytes) : IHostEnvironment
    {
        public HostFileSystemMeasurement MeasureFileSystem(string path)
            => new(LinuxHostEnvironment.NearestExistingAncestor(path), "test:0", availableBytes);

        public bool HasExecutable(string name) => true;

        public string? ReadCgroupControllers() => null;
    }

    /// <summary>A vault whose writes or reads stop part of the way, as a full disk, a failing disk or a Ctrl+C does.</summary>
    private sealed class FaultingVault(IBackupVault inner) : IBackupVault
    {
        private int reads;

        public long FailWriteAfter { get; init; } = long.MaxValue;
        public Action? WriteFault { get; init; }
        public int FailReadNumber { get; init; } = int.MaxValue;
        public long FailReadAfter { get; init; } = long.MaxValue;
        public Action? ReadFault { get; init; }

        public Action? DiscardFault { get; init; }

        public string Root => inner.Root;

        public async Task<IBackupTransfer> WriteAsync(string name, CancellationToken cancellationToken)
            => new FaultingTransfer(await inner.WriteAsync(name, cancellationToken), FailWriteAfter, WriteFault);

        public async Task<IBackupTransfer> ReadAsync(string name, bool partial, CancellationToken cancellationToken)
        {
            var transfer = await inner.ReadAsync(name, partial, cancellationToken);
            if (++reads != FailReadNumber) return transfer;
            return new FaultingTransfer(transfer, FailReadAfter, ReadFault);
        }

        public Task CommitAsync(BackupRecord record, CancellationToken cancellationToken) => inner.CommitAsync(record, cancellationToken);
        public Task DiscardAsync(string name, CancellationToken cancellationToken)
        {
            DiscardFault?.Invoke();
            return inner.DiscardAsync(name, cancellationToken);
        }
        public Task<BackupRecord?> ReadRecordAsync(string name, CancellationToken cancellationToken) => inner.ReadRecordAsync(name, cancellationToken);
        public Task<IReadOnlyList<BackupPartial>> ListPartialsAsync(Guid instanceId, CancellationToken cancellationToken)
            => inner.ListPartialsAsync(instanceId, cancellationToken);

        private sealed class FaultingTransfer(IBackupTransfer inner, long after, Action? fault) : IBackupTransfer
        {
            private readonly FaultingStream stream = new(inner.Stream, after, fault);

            public Stream Stream => stream;
            public Task CompleteAsync(CancellationToken cancellationToken) => inner.CompleteAsync(cancellationToken);

            public async ValueTask DisposeAsync()
            {
                await stream.DisposeAsync();
                await inner.DisposeAsync();
            }
        }

        private sealed class FaultingStream(Stream inner, long after, Action? fault) : Stream
        {
            private long passed;

            public override bool CanRead => inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => inner.CanWrite;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => passed; set => throw new NotSupportedException(); }

            public override int Read(byte[] buffer, int offset, int count)
            {
                Check(count);
                var read = inner.Read(buffer, offset, count);
                passed += read;
                return read;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Check(buffer.Length);
                var read = await inner.ReadAsync(buffer, cancellationToken);
                passed += read;
                return read;
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                Check(count);
                inner.Write(buffer, offset, count);
                passed += count;
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Check(buffer.Length);
                await inner.WriteAsync(buffer, cancellationToken);
                passed += buffer.Length;
            }

            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            private void Check(int count)
            {
                if (passed + count > after) fault?.Invoke();
            }
        }
    }
}
