using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// PR-1369-R1 (#1353): a restore never lowers an issued counter (F1), a failed backup always restarts the instance (F4),
/// live authority removals win (F5), a restore's swap is durable in both directories of every move (F7), a resumed
/// restore needs only the space its remaining phases use (F8), and a backup's space bound covers every snapshot it holds
/// (F9).
/// </summary>
public sealed partial class LifecycleContractTests
{

    /// <summary>
    /// F1: a counter-bearing store the live instance created after the backup is carried over, so the sequences it handed
    /// out are never handed out again; the restore reports done only once every recorded floor holds.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_CounterStoreCreatedAfterTheBackup_IsCarriedOverSoNoCounterIsLowered()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var fleet = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore);
        Execute(fleet,
            "CREATE TABLE fleet_status_metadata(metadata_key INTEGER PRIMARY KEY, next_sequence INTEGER NOT NULL, schema_version INTEGER NOT NULL);" +
            "INSERT INTO fleet_status_metadata VALUES(1, 60, 1);");
        await UninstallAsync(fixture);

        var restored = await RestoreAsync(fixture, backup.Backup!.Archive);

        Assert.AreEqual("completed", restored.Outcome);
        Assert.IsTrue(File.Exists(fleet), "the store that handed out fleet sequences after the backup was removed");
        Assert.AreEqual(60L, Scalar(fleet, "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;"));
    }

    /// <summary>
    /// F4: when the backup fails and discarding its partial fails too, the instance is still restarted and resumed, both
    /// errors are reported, and the journal stays resumable.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_DiscardFailingAfterTheStop_StillRestartsTheInstanceAndStaysResumable()
    {
        using var fixture = await BackupFixtureAsync();
        SeedBulk(fixture.Paths, filesPerTree: 3);
        var lifecycle = new FakeLifecycleClient();
        BackupVault.Override = (_, _) => new FaultingVault(new OwnerBackupVault(new BackupVaultFiles(Path.Combine(fixture.Root, BackupVault.FolderName))))
        {
            FailWriteAfter = 64 * 1024,
            WriteFault = static () => throw new IOException("No space left on device"),
            DiscardFault = static () => throw new IOException("Read-only file system")
        };
        InstallerException failure;
        try
        {
            failure = await Assert.ThrowsExactlyAsync<InstallerException>(() => BackupAsync(fixture, lifecycle));
        }
        finally
        {
            BackupVault.Override = null;
        }

        Assert.IsTrue(fixture.Runner.Running, "the instance was left stopped because the cleanup failed");
        Assert.AreEqual(lifecycle.PauseCount, lifecycle.ResumeCount, "capture was left paused");
        StringAssert.Contains(failure.Message, "No space left on device", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, "Read-only file system", StringComparison.Ordinal);
        StringAssert.Contains(failure.Message, "--resume", StringComparison.Ordinal);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsTrue(journal.MutationStarted, "the partial was not discarded, so --resume must still be able to finish the cleanup");
    }

    /// <summary>
    /// F5: an owner the live instance demoted after the backup, and a role and an authority claim it removed, stay
    /// removed in the restored store, and each is reported.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_AuthorityTheLiveInstanceRemovedSinceTheBackup_StaysRemovedAndIsReported()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var identity = CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot);
        Execute(identity,
            "UPDATE AspNetUsers SET IsSiteOwner = 0 WHERE Id = 'owner';" +
            "DELETE FROM AspNetUserRoles WHERE UserId = 'owner';" +
            "DELETE FROM AspNetUserClaims WHERE UserId = 'owner';");
        await UninstallAsync(fixture);
        using var error = new StringWriter();

        var restored = await RestoreAsync(fixture, backup.Backup!.Archive, error: error);

        Assert.AreEqual("completed", restored.Outcome);
        Assert.AreEqual(0L, Scalar(identity, "SELECT IsSiteOwner FROM AspNetUsers WHERE Id = 'owner';"), "the site-owner demotion was undone");
        Assert.AreEqual(0L, Scalar(identity, "SELECT COUNT(*) FROM AspNetUserRoles;"), "the removed role came back");
        Assert.AreEqual(0L, Scalar(identity, "SELECT COUNT(*) FROM AspNetUserClaims;"), "the removed authority claim came back");
        var report = error.ToString();
        StringAssert.Contains(report, "site owner", StringComparison.Ordinal);
        StringAssert.Contains(report, "role-operator", StringComparison.Ordinal);
        StringAssert.Contains(report, "hvo:authority", StringComparison.Ordinal);
    }

    /// <summary>
    /// F7: every move of the swap is durable in both directories it touches, and the folder that keeps the replaced files
    /// is durable in its parent, before the swap counts an entry moved.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_Swap_SyncsBothParentsOfEveryMove()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        await UninstallAsync(fixture);
        var events = new List<string>();
        var flushed = NativeLinux.DirectoryFlushed;
        NativeLinux.DirectoryFlushed = path =>
        {
            lock (events) events.Add($"flush:{Path.GetFullPath(path).TrimEnd('/')}");
        };
        CameraAgentLifecycleManager.BackupStepCompleted = step =>
        {
            lock (events) events.Add(step);
        };
        LifecycleResult restored;
        try
        {
            restored = await RestoreAsync(fixture, backup.Backup!.Archive);
        }
        finally
        {
            NativeLinux.DirectoryFlushed = flushed;
            CameraAgentLifecycleManager.BackupStepCompleted = static _ => { };
        }

        Assert.AreEqual("completed", restored.Outcome);
        var operationRoot = Path.Combine(fixture.Paths.InstanceRoot, BackupInventory.RestoreFolderName, restored.OperationId!.Value.ToString("D"));
        var staged = Path.Combine(operationRoot, "staged");
        var previous = Path.Combine(operationRoot, "previous");
        var swaps = events.Select(static (entry, index) => (Entry: entry, Index: index))
            .Where(static item => item.Entry.StartsWith("restore-swapped:", StringComparison.Ordinal)).ToArray();
        Assert.IsNotEmpty(swaps);
        var start = 0;
        foreach (var (entry, index) in swaps)
        {
            var window = events.Skip(start).Take(index - start).ToArray();
            CollectionAssert.Contains(window, $"flush:{fixture.Paths.InstanceRoot}", entry);
            CollectionAssert.Contains(window, $"flush:{staged}", $"{entry}: the staged folder it left was not synced");
            CollectionAssert.Contains(window, $"flush:{previous}", $"{entry}: the folder it moved the replaced entry into was not synced");
            start = index + 1;
        }
        var previousCreated = events.IndexOf($"flush:{operationRoot}");
        Assert.IsTrue(previousCreated >= 0 && previousCreated < swaps[0].Index, "the folder holding 'previous' was not synced before the first move");
    }

    /// <summary>
    /// F8: a restore resumed after its archive was staged needs only the space its remaining phases use: the staged copy is
    /// already there.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreAsync_ResumedAfterStaging_NeedsOnlyTheSpaceOfTheRemainingPhases()
    {
        using var fixture = await BackupFixtureAsync();
        var backup = await BackupAsync(fixture, new FakeLifecycleClient());
        var (archive, _) = await ReadArchiveAsync(Path.Combine(fixture.Root, BackupVault.FolderName, backup.Backup!.Archive));
        await UninstallAsync(fixture);
        var images = new List<CrashImage>();
        using var captured = new TemporaryDirectory();
        CaptureBackupCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup.Archive)).Outcome);
        }
        finally
        {
            ResetBackupCrashHooks();
        }
        RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:CandidateValidated"));
        // Just less than staging the whole archive again would need.
        var staging = archive.TotalBytes + (long)archive.Entries.Count * 4096 + 256L * 1024 * 1024;
        var original = CameraAgentLifecycleManager.BackupHost;
        CameraAgentLifecycleManager.BackupHost = new SmallDisk(staging - 1);
        try
        {
            Assert.AreEqual("completed", (await RestoreAsync(fixture, backup.Backup.Archive, resume: true)).Outcome);
        }
        finally
        {
            CameraAgentLifecycleManager.BackupHost = original;
        }
    }

    /// <summary>
    /// F9: the space a backup needs counts the rollback snapshot the archive will also hold, so a backup whose archive could
    /// not fit is refused before anything changes.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAsync_RollbackSnapshotTheArchiveWouldHold_CountsTowardsTheSpaceItNeeds()
    {
        using var fixture = await BackupFixtureAsync();
        // A rollback snapshot of 64 MiB (sparse, so it costs no real disk), kept by the manifest.
        var rollback = new LifecycleStateSnapshot(Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), DateTimeOffset.UtcNow);
        var rollbackRoot = CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, rollback.OperationId, rollback.SnapshotId);
        Directory.CreateDirectory(rollbackRoot);
        await using (var file = new FileStream(Path.Combine(rollbackRoot, "00-cameraagent_identity.db"), FileMode.CreateNew))
        {
            file.SetLength(64L * 1024 * 1024);
        }
        await SafeFileSystem.WriteJsonAtomicAsync(
            fixture.Paths.ManifestPath, fixture.Manifest with { PreviousStateSnapshot = rollback },
            DeploymentJsonContext.Default.InstanceManifest, CancellationToken.None);
        var lifecycle = new FakeLifecycleClient();
        var original = CameraAgentLifecycleManager.BackupHost;
        // Room for everything but that snapshot.
        CameraAgentLifecycleManager.BackupHost = new SmallDisk(540L * 1024 * 1024);
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
    }

    /// <summary>
    /// F2/F3: under the production product root a backup or restore runs only as root, through sudo, as RoofController's
    /// does; run as the runtime user it is refused before anything is read or written, naming the command to run.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(LifecycleOperationKind.Backup, "sudo hvo-sky-install cameraagent backup")]
    [DataRow(LifecycleOperationKind.Restore, "sudo hvo-sky-install cameraagent restore hvo-sky-")]
    public async Task BackupAndRestore_UnderTheProductionRootWithoutRoot_AreRefusedNamingSudo(LifecycleOperationKind operation, string command)
    {
        if (NativeLinux.getuid() == 0) Assert.Inconclusive("The test runs as the runtime user.");
        var instance = Guid.NewGuid();
        var request = new LifecycleRequest(operation, instance, "/var/lib/hvo-sky", dryRun: false, resume: false, json: false)
        {
            ProductionProductRoot = "/var/lib/hvo-sky",
            Archive = operation == LifecycleOperationKind.Restore
                ? BackupVaultFiles.NameFor(instance, new DateTimeOffset(2026, 10, 18, 7, 30, 0, TimeSpan.Zero), Guid.NewGuid())
                : null
        };
        var runner = new FakeRunner(new DockerDaemonIdentity("daemon", "host", "amd64", "29.7.2"));

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, runner, null, null, NativeLinux.getuid(), NativeLinux.getgid(), CancellationToken.None));

        Assert.IsTrue(refused.IsRefusal, refused.Message);
        StringAssert.Contains(refused.Message, command, StringComparison.Ordinal);
        Assert.AreEqual(0, runner.InvocationCount, "nothing ran before the refusal");
    }

    /// <summary>
    /// With <c>--json</c>, a backup's progress and a restore's credential report are each one JSON event per line on
    /// standard error, so a caller reads them without parsing prose.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task BackupAndRestore_WithJson_ReportOneJsonEventPerLine()
    {
        using var fixture = await BackupFixtureAsync();
        using var backupErrors = new StringWriter();
        var backup = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Backup) with { Json = true }, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: backupErrors);
        Execute(CameraAgentLayoutIdentity(fixture), $"DELETE FROM AspNetUserTokens WHERE Name = '{RevokedKey}';");
        await UninstallAsync(fixture);
        using var restoreErrors = new StringWriter();
        var restored = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Restore) with { Archive = backup.Backup!.Archive, Json = true }, fixture.Runner,
            _ => new FakeLifecycleClient(), _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid,
            CancellationToken.None, error: restoreErrors);

        Assert.AreEqual("completed", restored.Outcome);
        var progress = Events(backupErrors.ToString(), LifecycleBackupProgressEvent.Name);
        Assert.IsNotEmpty(progress);
        Assert.IsTrue(progress.All(static line => line.RootElement.GetProperty("stage").GetString() is "write" or "verify"));
        var credentials = Events(restoreErrors.ToString(), LifecycleCredentialsRestoredEvent.Name).Single();
        StringAssert.Contains(credentials.RootElement.GetProperty("warning").GetString()!, RevokedKey, StringComparison.Ordinal);
        Assert.IsNotEmpty(Events(restoreErrors.ToString(), LifecycleBackupProgressEvent.Name), "the restore's verification and extraction report progress too");
        // The results as --json prints them carry what was backed up and what was restored.
        using var backupResult = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(backup, DeploymentJsonContext.Default.LifecycleResult));
        var archive = backupResult.RootElement.GetProperty("backup");
        Assert.AreEqual(backup.Backup.Archive, archive.GetProperty("archive").GetString());
        StringAssert.EndsWith(archive.GetProperty("path").GetString()!, backup.Backup.Archive, StringComparison.Ordinal);
        Assert.AreEqual(64, archive.GetProperty("archiveSha256").GetString()!.Length);
        Assert.IsGreaterThan(0L, archive.GetProperty("archiveBytes").GetInt64());
        Assert.IsGreaterThan(0L, archive.GetProperty("totalBytes").GetInt64());
        Assert.IsGreaterThan(0, archive.GetProperty("entries").GetInt32());
        Assert.AreEqual(backup.Backup.CreatedUtc, archive.GetProperty("createdUtc").GetDateTimeOffset());
        using var restoreResult = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(restored, DeploymentJsonContext.Default.LifecycleResult));
        var restore = restoreResult.RootElement.GetProperty("restore");
        Assert.AreEqual(backup.Backup.Archive, restore.GetProperty("archive").GetString());
        Assert.AreEqual(backup.Backup.CreatedUtc, restore.GetProperty("backupCreatedUtc").GetDateTimeOffset());
        Assert.AreEqual(backup.OperationId, restore.GetProperty("backupOperationId").GetGuid());
        Assert.AreEqual(1, restore.GetProperty("apiKeysRevoked").GetInt32());
        Assert.AreEqual(0, restore.GetProperty("accountsReset").GetInt32());
        Assert.AreEqual(0, restore.GetProperty("authorityRevoked").GetInt32());
        Assert.IsFalse(restore.GetProperty("noLiveCredentialStore").GetBoolean());

        static List<System.Text.Json.JsonDocument> Events(string text, string name)
            => [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => System.Text.Json.JsonDocument.Parse(line))
                .Where(document => document.RootElement.TryGetProperty("event", out var value) && value.GetString() == name)];
    }

    private static string CameraAgentLayoutIdentity(LifecycleFixture fixture) => CameraAgentStateLayout.IdentityDatabasePath(fixture.Paths.StateRoot);

    private static long Scalar(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned statements, not input.
        command.CommandText = sql;
#pragma warning restore CA2100
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
