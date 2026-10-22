using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Correction round 2 of #1314 (PR #1340): a restore syncs every directory it relies on before it records that it is
/// complete, even one an interrupted attempt created (R11); every recovery authenticates the snapshot it will restore
/// before it changes any container (R12); and a store under a storage root named after the snapshot is removed, keeping
/// its files, whichever restore puts the snapshot back (R14).
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string ApiRoot = "/app/data/archive/api-added";
    private const string ApiOutbox = "archive/api-added/outbox/artifact-outbox.db";
    private const string ApiBulkFile = "archive/api-added/frames/2026/10/17/Raw/frame.bin";

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ARetriedRestoreSyncsADirectoryAnInterruptedAttemptCreatedBeforeRecordingCompletion()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var automation = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        var automationDirectory = Path.GetDirectoryName(automation)!;
        ExecuteSql(automation, "CREATE TABLE automation_definitions(definition_id TEXT PRIMARY KEY, version INTEGER NOT NULL);");
        // The candidate removes the store's directory, so its recovery has to create it again.
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) Directory.Delete(automationDirectory, recursive: true);
        };
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
                fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));
        }
        finally
        {
            ResetCrashHooks();
        }
        // The power is lost just after a restore attempt recreated the directory and before it synced its parent.
        RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:Restoring"));
        Assert.IsFalse(Directory.Exists(automationDirectory));
        SafeFileSystem.CreateRuntimeDirectory(automationDirectory, fixture.Uid, fixture.Gid);
        fixture.Runner.OnComposeUp = null;
        var flushed = new List<string>();
        string[]? atCompletion = null;
        NativeLinux.DirectoryFlushed = path => flushed.Add(Path.TrimEndingDirectorySeparator(path));
        CameraAgentLifecycleManager.JournalRecorded = state =>
        {
            if (state.StateSnapshotRestoredUtc is not null && atCompletion is null) atCompletion = [.. flushed.Take(flushed.Count - 1)];
        };
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
                error: TextWriter.Null)).Outcome);
        }
        finally
        {
            NativeLinux.DirectoryFlushed = static _ => { };
            CameraAgentLifecycleManager.JournalRecorded = static _ => { };
        }

        Assert.IsNotNull(atCompletion);
        CollectionAssert.IsSubsetOf(Ancestors(automationDirectory, fixture.Paths.ProductRoot), atCompletion,
            $"Synced before the restore was recorded complete: {string.Join(", ", atCompletion)}");
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_ResumeAuthenticatesItsOwnSnapshotBeforeChangingAnyContainer()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        await UpgradeAndCommitAsync(fixture);
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            await RollBackAsync(fixture);
        }
        finally
        {
            ResetCrashHooks();
        }
        // The rollback is interrupted while the previous image runs on the retained snapshot; its own snapshot, the one
        // --resume must restore first, is damaged. The retained snapshot that supplies the preflight view is intact.
        RestoreCrashImage(fixture, images.Last(static image => image.Step == "journal:CandidateVerified"));
        var interrupted = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(LifecycleOperationKind.Rollback, interrupted.Kind);
        Assert.IsNotNull(interrupted.RetainedStateSnapshot);
        File.Delete(Path.Combine(CameraAgentStateSnapshot.SnapshotRoot(
            fixture.Paths, interrupted.OperationId, interrupted.StateSnapshot!.SnapshotId), "manifest.json"));
        var (stops, ups, active) = (fixture.Runner.ComposeStopCount, fixture.Runner.ComposeUpCount, fixture.Runner.ActiveImageId);

        var refused = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null));

        StringAssert.Contains(refused.Message, "missing its manifest", StringComparison.Ordinal);
        Assert.AreEqual(stops, fixture.Runner.ComposeStopCount, "The rollback stopped a container before it found it could not restore.");
        Assert.AreEqual(ups, fixture.Runner.ComposeUpCount);
        Assert.AreEqual(active, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateRecoveryAuthenticatesItsSnapshotBeforeStoppingTheCandidate()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var stopsAtCandidate = -1;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            stopsAtCandidate = fixture.Runner.ComposeStopCount;
            // The snapshot is damaged while the candidate runs, before the candidate fails its verification.
            var journal = CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
                .GetAwaiter().GetResult()!;
            File.Delete(Path.Combine(CameraAgentStateSnapshot.SnapshotRoot(
                fixture.Paths, journal.OperationId, journal.StateSnapshot!.SnapshotId), "manifest.json"));
        };

        var failed = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        StringAssert.Contains(failed.Message + failed.InnerException?.Message, "missing its manifest", StringComparison.Ordinal);
        Assert.AreEqual(stopsAtCandidate, fixture.Runner.ComposeStopCount, "Recovery stopped the candidate before it found it could not restore.");
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidate_RemovesAStoreUnderARootTheCandidateNamedAndKeepsItsFiles()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths), ScheduleRevisionsTable);
        fixture.Runner.OnComposeUp = image =>
        {
            // A schedule created through the API while the candidate runs names a new storage root, and the candidate
            // opens an outbox there in its own (migrated) schema and stores a frame under it.
            if (image == CandidateImageId) NameApiRoot(fixture.Paths);
        };

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));

        AssertApiRootStoreRemoved(fixture.Paths);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        CollectionAssert.Contains(journal.StateRestorePlan!.CurrentLocations.ToArray(), ApiOutbox);
        CollectionAssert.DoesNotContain(journal.StateRestorePlan.TargetLocations.ToArray(), ApiOutbox);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_RemovesAStoreUnderARootNamedThroughTheApiAfterTheUpgradeAndKeepsItsFiles()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths), ScheduleRevisionsTable);
        await UpgradeAndCommitAsync(fixture);
        NameApiRoot(fixture.Paths);

        await RollBackAsync(fixture);

        AssertApiRootStoreRemoved(fixture.Paths);
        // The rollback's own snapshot has the store, so rolling forward again brings it back with the root.
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var own = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, journal.StateSnapshot!, CancellationToken.None);
        CollectionAssert.Contains(own.Databases.Where(static database => database.Present).Select(static database => database.RelativePath).ToArray(), ApiOutbox);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateThatLeftTheJournalUnreadable_StillRecoversAndSaysWhatItCouldNotRead()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            var journal = RawIngressPath(fixture.Paths);
            foreach (var suffix in CameraAgentStateLayout.SqliteSidecarSuffixes) File.Delete(journal + suffix);
            File.WriteAllBytes(journal, new byte[8192]);
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        // The original runs again on its restored journal.
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.IsTrue(TableExists(RawIngressPath(fixture.Paths), RawSentinelTable));
        StringAssert.Contains(error.ToString(), "could not read", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), "raw/journal/raw-ingress.db", StringComparison.Ordinal);
    }

    private const string ScheduleRevisionsTable =
        "CREATE TABLE capture_schedule_revisions(revision_id TEXT PRIMARY KEY, revision_number INTEGER NOT NULL UNIQUE, profile_json BLOB NOT NULL);";

    private static void NameApiRoot(InstallationPaths paths)
    {
        ExecuteSql(RawIngressPath(paths),
            "INSERT INTO capture_schedule_revisions VALUES('r-api', 9, CAST('{\"processingSteps\":[{\"type\":\"FileStorage\",\"options\":{\"storageRoot\":\"" +
            ApiRoot + "\"}}]}' AS BLOB));");
        var outbox = CameraAgentStateSnapshot.StorePath(paths.StateRoot, ApiOutbox);
        Directory.CreateDirectory(Path.GetDirectoryName(outbox)!);
        ExecuteSql(outbox, "PRAGMA journal_mode=WAL; CREATE TABLE outbox(id INTEGER PRIMARY KEY, migrated INTEGER NOT NULL); INSERT INTO outbox(migrated) VALUES(1);");
        var bulk = CameraAgentStateSnapshot.StorePath(paths.StateRoot, ApiBulkFile);
        Directory.CreateDirectory(Path.GetDirectoryName(bulk)!);
        File.WriteAllBytes(bulk, [1, 2, 3]);
    }

    private static void AssertApiRootStoreRemoved(InstallationPaths paths)
    {
        var outbox = CameraAgentStateSnapshot.StorePath(paths.StateRoot, ApiOutbox);
        foreach (var file in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => outbox + suffix).Prepend(outbox))
            Assert.IsFalse(File.Exists(file), $"'{file}' was left in place for the previous release.");
        Assert.IsTrue(File.Exists(CameraAgentStateSnapshot.StorePath(paths.StateRoot, ApiBulkFile)), "A restore removed a bulk file.");
        // The schedule revision that named the root is gone with the rest of the journal's later changes.
        Assert.AreEqual(0, ReadCounter(paths, "raw/journal/raw-ingress.db", "SELECT COUNT(*) FROM capture_schedule_revisions WHERE revision_id = 'r-api';"));
    }
}
