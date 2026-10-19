using System.Globalization;
using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// An image upgrade or rollback is a swap that never copies bulk capture data (#1314). An unchanged state contract
/// takes no snapshot at all. A declared migration snapshots only the small SQLite stores, while the instance is
/// stopped and before the candidate starts; a failed candidate is restored onto that snapshot, and a rollback of the
/// committed upgrade puts it back, each with a warning that later database changes are lost. Every step is resumable
/// after a power loss at any durable point.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string RawSentinelTable = "sentinel";
    private const string CandidateMigrationTable = "candidate_migration";
    private static readonly string[] SnapshotEntries =
        ["00-cameraagent_identity.db", "01-raw-ingress.db", "02-artifact-outbox.db", "manifest.json"];
    private static readonly string[] DurableSwapSteps =
    [
        "journal:Prepared", "journal:CandidateValidated", "journal:Drained", "snapshot:identity/cameraagent_identity.db",
        "snapshot:raw/journal/raw-ingress.db", "snapshot:raw/outbox/artifact-outbox.db", "journal:StateSnapshotRecorded",
        "journal:Mutating", "journal:CandidateVerified", "journal:Committed"
    ];

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_UnchangedStateContract_SwapsWithoutCopyingOrSnapshottingAnything()
    {
        using var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        SeedStores(fixture.Paths);
        var bulk = SeedBulk(fixture.Paths);

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(0, fixture.Runner.BackupCount);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsFalse(journal.StateSnapshotRequired);
        Assert.IsNull(journal.StateSnapshot);
        Assert.IsFalse(Directory.Exists(SnapshotsRoot(fixture, journal.OperationId)));
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.IsNull(manifest.PreviousStateSnapshot);
        AssertBulkUntouched(bulk);
        AssertNothingBulkCopied(fixture, bulk);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_DeclaredMigration_SnapshotsOnlyTheSmallDatabasesBeforeTheCandidateStarts()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var before = ReadStoreFacts(fixture.Paths);
        var bulk = SeedBulk(fixture.Paths);
        LifecycleOperationState? atCandidateStart = null;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            atCandidateStart = CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
                .GetAwaiter().GetResult();
            MigrateAsCandidate(fixture.Paths);
        };

        var result = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);

        // No --migration-backward-compatible assertion is needed: the snapshot is the restore path.
        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(0, fixture.Runner.BackupCount);
        // The snapshot was durable and recorded before the candidate's Compose up, and the record that the candidate
        // may start followed it.
        Assert.IsNotNull(atCandidateStart);
        Assert.AreEqual(LifecycleOperationPhase.Mutating, atCandidateStart.Phase);
        Assert.IsTrue(atCandidateStart.StateSnapshotRequired);
        var snapshot = atCandidateStart.StateSnapshot;
        Assert.IsNotNull(snapshot);
        var snapshotManifest = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, snapshot, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image, snapshotManifest.Image);
        // Only the small stores are in it: the three present ones and the manifest, nothing from a bulk tree.
        var root = CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, snapshot.OperationId, snapshot.SnapshotId);
        CollectionAssert.AreEquivalent(SnapshotEntries, Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToArray());
        CollectionAssert.AreEqual(
            CameraAgentStateLayout.SqliteStores.ToArray(),
            snapshotManifest.Databases.Select(static database => database.RelativePath).ToArray());
        Assert.AreEqual(3, snapshotManifest.Databases.Count(static database => database.Present));
        // The snapshot is the pre-upgrade state, not what the candidate migrated.
        CollectionAssert.AreEqual(before, ReadStoreFacts(SnapshotFile(root, "01-raw-ingress.db")));
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(snapshot, manifest.PreviousStateSnapshot);
        Assert.AreEqual(CandidateImageId, manifest.Image.ImageId);
        AssertBulkUntouched(bulk);
        AssertNothingBulkCopied(fixture, bulk);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UpgradeAsync_FailedMigratingCandidate_RestoresTheSnapshotBeforeThePreviousImageStartsAndWarns(bool json)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var before = ReadStoreFacts(fixture.Paths);
        var bulk = SeedBulk(fixture.Paths);
        List<string>? atOriginalRestart = null;
        var sidecarsAtOriginalRestart = true;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId)
            {
                MigrateAsCandidate(fixture.Paths);
                LeaveCandidateSidecars(fixture.Paths);
                return;
            }
            atOriginalRestart = ReadStoreFacts(fixture.Paths);
            sidecarsAtOriginalRestart = HasCandidateSidecars(fixture.Paths);
        };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false) with { Json = json }, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => owner, fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        // The previous image started only on the restored stores, with the candidate's log and wal-index removed.
        Assert.IsNotNull(atOriginalRestart);
        CollectionAssert.AreEqual(before, atOriginalRestart);
        Assert.IsFalse(sidecarsAtOriginalRestart);
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(InstallationStatus.Failed, journal.Status);
        Assert.IsFalse(journal.MutationStarted);
        Assert.IsNotNull(journal.StateSnapshot);
        Assert.IsNotNull(journal.StateSnapshotRestoredUtc);
        var warning = error.ToString();
        StringAssert.Contains(warning, "Every database change made after that point is lost", StringComparison.Ordinal);
        // CameraAgent records verifiable raw frames written since the snapshot again at startup, so the warning never
        // promises that every later frame stays unindexed.
        StringAssert.Contains(warning, "records again the raw frames it can verify", StringComparison.Ordinal);
        StringAssert.Contains(warning, journal.StateSnapshot.SnapshotId.ToString("D"), StringComparison.Ordinal);
        if (json)
        {
            using var parsed = System.Text.Json.JsonDocument.Parse(SingleLine(warning));
            Assert.AreEqual(LifecycleStateSnapshotRestoreEvent.Name, parsed.RootElement.GetProperty("event").GetString());
        }
        else
        {
            Assert.IsTrue(warning.StartsWith("Warning: ", StringComparison.Ordinal), warning);
        }
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(fixture.Manifest.Image, manifest.Image);
        Assert.IsNull(manifest.PreviousStateSnapshot);
        AssertBulkUntouched(bulk);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_AfterAMigratingUpgrade_RestoresItsSnapshotWithAWarningAndCanRollForwardAgain()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var beforeUpgrade = ReadStoreFacts(fixture.Paths);
        var bulk = SeedBulk(fixture.Paths);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) MigrateAsCandidate(fixture.Paths);
        };
        var lifecycle = new FakeLifecycleClient();
        var upgraded = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        Assert.AreEqual("completed", upgraded.Outcome);
        var upgradeSnapshot = (await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None))
            .PreviousStateSnapshot;
        Assert.IsNotNull(upgradeSnapshot);
        // The upgraded release records more captures before the operator rolls back.
        AddRawRows(fixture.Paths, 7);
        var beforeRollback = ReadStoreFacts(fixture.Paths);
        var pausesBeforeRollback = lifecycle.PauseCount;
        using var error = new ObservingWriter<int>(() => lifecycle.PauseCount);
        fixture.Runner.OnComposeUp = null;

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: error);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        // The warning came before the pause, so before anything about the instance changed.
        Assert.AreEqual(pausesBeforeRollback, error.StateAtFirstWrite);
        StringAssert.Contains(error.ToString(), upgradeSnapshot.SnapshotId.ToString("D"), StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), "Every database change made after that point is lost", StringComparison.Ordinal);
        // The stores are as they were before the upgrade: the later captures are gone, as the warning said.
        CollectionAssert.AreEqual(beforeUpgrade, ReadStoreFacts(fixture.Paths));
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(upgradeSnapshot, journal.RetainedStateSnapshot);
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None);
        Assert.AreEqual(CandidateImageId, manifest.PreviousImage!.ImageId);
        // The rollback kept the stores it replaced, so going forward again puts the upgraded release's state back.
        Assert.AreEqual(journal.StateSnapshot, manifest.PreviousStateSnapshot);
        AssertBulkUntouched(bulk);

        var forward = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null);

        Assert.AreEqual("completed", forward.Outcome);
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        CollectionAssert.AreEqual(beforeRollback, ReadStoreFacts(fixture.Paths));
        AssertBulkUntouched(bulk);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_TamperedRetainedSnapshot_IsRefusedBeforeAnythingChanges()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var lifecycle = new FakeLifecycleClient();
        Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        var snapshot = (await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None))
            .PreviousStateSnapshot!;
        var tampered = SnapshotFile(CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, snapshot.OperationId, snapshot.SnapshotId), "01-raw-ingress.db");
        await using (var stream = new FileStream(tampered, FileMode.Open, FileAccess.Write))
        {
            stream.Seek(-1, SeekOrigin.End);
            stream.WriteByte(0x5a);
        }
        var pauses = lifecycle.PauseCount;
        var stores = ReadStoreFacts(fixture.Paths);

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => lifecycle,
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None));

        StringAssert.Contains(exception.Message, "differs from its manifest", StringComparison.Ordinal);
        Assert.AreEqual(pauses, lifecycle.PauseCount);
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        CollectionAssert.AreEqual(stores, ReadStoreFacts(fixture.Paths));
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.AreEqual(CameraAgentLifecycleManager.RefusedFailureCode, journal.FailureCode);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RestoreOnly_RefusesASnapshotItCannotAuthenticateBeforeAnythingChanges()
    {
        using var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, recoveryState: true);
        var request = await fixture.PrepareRestoreAsync();
        var operation = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        await CameraAgentLifecycleManager.RecordAsync(fixture.Paths, operation with
        {
            StateSnapshotRequired = true,
            StateSnapshot = new LifecycleStateSnapshot(Guid.NewGuid(), operation.OperationId, new string('a', 64), DateTimeOffset.UtcNow)
        }, CancellationToken.None);
        var lifecycle = new FakeLifecycleClient();

        var exception = await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            request, fixture.Runner, _ => lifecycle, _ => new FakeOwnerClient(fixture.ApplicationIdentity),
            fixture.Uid, fixture.Gid, CancellationToken.None));

        // Restore-only recovers onto a recorded snapshot only after authenticating it; one it cannot authenticate stops
        // it before it stops, restores or resumes anything.
        StringAssert.Contains(exception.Message, "snapshot is missing its manifest", StringComparison.Ordinal);
        Assert.AreEqual(0, lifecycle.RecoveryResumeCalls);
        Assert.AreEqual(0, fixture.Runner.ComposeStopCount);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_ResumeAfterTheOriginalWasRestored_NeverRestoresOverItsNewCaptures()
    {
        // The automatic restore put the stores back and restarted the original, but the capture resume was not
        // acknowledged, so the journal still holds the recovery. The original captures more before the operator
        // resumes the upgrade. --resume must not put the snapshot back over those captures: the restore was recorded
        // before the original started on the restored stores.
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) MigrateAsCandidate(fixture.Paths);
        };
        var lifecycle = new FakeLifecycleClient { RejectNextResume = true };
        var owner = new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true };
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => lifecycle, _ => owner,
            fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null));
        var retained = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        Assert.IsTrue(retained.MutationStarted);
        Assert.AreEqual(LifecycleOperationPhase.Restoring, retained.Phase);
        Assert.IsNotNull(retained.StateSnapshotRestoredUtc);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsFalse(HasCandidateMigration(fixture.Paths));
        AddRawRows(fixture.Paths, 5);
        var afterRestore = ReadStoreFacts(fixture.Paths);
        List<string>? atOriginalUp = null;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId)
            {
                MigrateAsCandidate(fixture.Paths);
                return;
            }
            atOriginalUp ??= ReadStoreFacts(fixture.Paths);
        };

        var resumed = await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: TextWriter.Null);

        Assert.AreEqual("completed", resumed.Outcome);
        CollectionAssert.AreEqual(afterRestore, atOriginalUp);
        // The retry snapshotted the state the original had reached, so its snapshot carries those captures.
        var snapshot = (await CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None))
            .PreviousStateSnapshot!;
        CollectionAssert.AreEqual(
            afterRestore,
            ReadStoreFacts(SnapshotFile(CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, snapshot.OperationId, snapshot.SnapshotId), "01-raw-ingress.db")));
        CollectionAssert.AreEqual(afterRestore.Append($"{CandidateMigrationTable}=1").ToArray(), ReadStoreFacts(fixture.Paths).ToArray());
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_PowerLossAtEveryDurableStep_ResumeConvergesOnTheMigratedCandidate()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var before = ReadStoreFacts(fixture.Paths);
        var bulk = SeedBulk(fixture.Paths, filesPerTree: 3);
        var images = new List<CrashImage>();
        var violations = new List<string>();
        using var captured = new TemporaryDirectory();
        InstallCandidateHooks(fixture, violations);
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        // Every durable step of the swap is covered: the journal records and each store's snapshot file.
        CollectionAssert.IsSubsetOf(DurableSwapSteps, images.Select(static image => image.Step).ToArray());

        foreach (var image in images.Where(static image => image.Step != "journal:Completed"))
        {
            await ResumeFromAsync(fixture, image, violations);
            AssertConvergedOnCandidate(fixture, image.Step, before);
            CollectionAssert.AreEqual(
                bulk.Select(static file => (file.Key, file.Value.Sha256)).ToArray(),
                bulk.Select(static file => (file.Key, Sha256(file.Key))).ToArray(),
                image.Step);
        }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_PowerLossDuringTheRestore_ResumeFinishesItBeforeTheOriginalStarts()
    {
        // A power loss after the candidate migrated the stores is resumed by restoring the snapshot. A second power
        // loss at any durable step of that restore (a stale sidecar removed, a store replaced, the directories synced,
        // the restore recorded) is resumed again, and the original still starts only on fully restored stores.
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var before = ReadStoreFacts(fixture.Paths);
        var violations = new List<string>();
        using var captured = new TemporaryDirectory();
        var first = new List<CrashImage>();
        InstallCandidateHooks(fixture, violations, leaveSidecars: true);
        CaptureCrashImages(fixture, captured.Path, first);
        try
        {
            await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None);
        }
        finally
        {
            ResetCrashHooks();
        }
        var afterMigration = first.First(static image => image.Step == "journal:CandidateVerified");

        RestoreCrashImage(fixture, afterMigration);
        // The retry's own candidate start leaves nothing behind, so only the restore has sidecars to remove.
        InstallCandidateHooks(fixture, violations);
        var second = new List<CrashImage>();
        CaptureCrashImages(fixture, Path.Combine(captured.Path, "second"), second);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
                error: TextWriter.Null)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        var restoreSteps = second.Where(static image => image.Step.StartsWith("restore", StringComparison.Ordinal)).ToArray();
        // Two steps for each of the three present stores, and the sync of every restored directory's chain.
        Assert.AreEqual(2 * 3 + 1, restoreSteps.Length, string.Join(", ", second.Select(static image => image.Step)));

        foreach (var image in second.TakeWhile(static image => image.Step != "journal:Completed"))
        {
            await ResumeFromAsync(fixture, image, violations);
            AssertConvergedOnCandidate(fixture, image.Step, before);
        }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }

    /// <summary>What a power loss at one durable step leaves: the product root on disk and the container Docker restarts.</summary>
    private sealed record CrashImage(string Step, string Copy, string? ActiveImageId, bool Running);

    private static async Task<LifecycleFixture> MigratingFixtureAsync()
    {
        var fixture = await LifecycleFixture.CreateAsync(InstanceLifecycleCondition.Installed, installedImage: CurrentStateContract);
        // One boundary label differs, so the candidate declares a migration without changing what the preflight reads.
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        return fixture;
    }

    /// <summary>
    /// The candidate migrates the raw journal when it starts, as a real one would, and the original must only ever
    /// start on stores without that migration.
    /// </summary>
    private static void InstallCandidateHooks(LifecycleFixture fixture, List<string> violations, bool leaveSidecars = false)
        => fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId)
            {
                MigrateAsCandidate(fixture.Paths);
                if (leaveSidecars) LeaveCandidateSidecars(fixture.Paths);
                return;
            }
            if (HasCandidateMigration(fixture.Paths)) violations.Add("the original image started on migrated stores");
            if (HasCandidateSidecars(fixture.Paths)) violations.Add("the original image started beside candidate sidecars");
        };

    private static void CaptureCrashImages(LifecycleFixture fixture, string directory, List<CrashImage> images)
    {
        void Capture(string step)
        {
            var copy = Path.Combine(directory, images.Count.ToString("D3", CultureInfo.InvariantCulture));
            CopyTree(fixture.Root, copy);
            images.Add(new CrashImage(step, copy, fixture.Runner.ActiveImageId, fixture.Runner.Running));
        }
        CameraAgentLifecycleManager.JournalRecorded = state => Capture($"journal:{state.Phase}");
        CameraAgentStateSnapshot.StepCompleted = Capture;
    }

    private static void ResetCrashHooks()
    {
        CameraAgentLifecycleManager.JournalRecorded = static _ => { };
        CameraAgentStateSnapshot.StepCompleted = static _ => { };
    }

    private static void RestoreCrashImage(LifecycleFixture fixture, CrashImage image)
    {
        SafeFileSystem.MakeTreeOwnerWritable(fixture.Root);
        Directory.Delete(fixture.Root, recursive: true);
        CopyTree(image.Copy, fixture.Root);
        fixture.Runner.Reboot(image.ActiveImageId, image.Running);
    }

    private static async Task ResumeFromAsync(LifecycleFixture fixture, CrashImage image, List<string> violations)
    {
        RestoreCrashImage(fixture, image);
        try
        {
            var result = await CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
                error: TextWriter.Null);
            if (result.Outcome != "completed") violations.Add($"{image.Step}: resume ended {result.Outcome}");
        }
        catch (InstallerException exception)
        {
            violations.Add($"{image.Step}: resume failed: {exception.Message} {exception.InnerException?.Message}");
        }
    }

    private static void AssertConvergedOnCandidate(LifecycleFixture fixture, string step, List<string> before)
    {
        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId, step);
        var journal = CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None)
            .GetAwaiter().GetResult()!;
        Assert.AreEqual(InstallationStatus.Completed, journal.Status, step);
        // The original rows are intact and the candidate's migration applied exactly once.
        var facts = ReadStoreFacts(fixture.Paths);
        CollectionAssert.AreEqual(before.Append($"{CandidateMigrationTable}=1").ToArray(), facts.ToArray(), step);
        var manifest = CameraAgentLifecycleManager.ReadManifestAsync(fixture.Paths.ManifestPath, CancellationToken.None)
            .GetAwaiter().GetResult();
        var snapshot = manifest.PreviousStateSnapshot;
        Assert.IsNotNull(snapshot, step);
        _ = CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, snapshot, CancellationToken.None)
            .GetAwaiter().GetResult();
        CollectionAssert.AreEqual(
            before,
            ReadStoreFacts(SnapshotFile(CameraAgentStateSnapshot.SnapshotRoot(fixture.Paths, snapshot.OperationId, snapshot.SnapshotId), "01-raw-ingress.db")),
            step);
    }

    private static string SnapshotsRoot(LifecycleFixture fixture, Guid operationId)
        => Path.Combine(fixture.Paths.OperationsRoot, "lifecycle", operationId.ToString("D"), "state-snapshots");

    private static string SnapshotFile(string root, string name) => Path.Combine(root, name);

    private static string RawIngressPath(InstallationPaths paths) => CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);

    private static IEnumerable<string> RawIngressSidecars(InstallationPaths paths)
        => CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => RawIngressPath(paths) + suffix);

    /// <summary>Creates three of the small stores, each with rows, as a running instance would have them.</summary>
    private static void SeedStores(InstallationPaths paths)
    {
        foreach (var directory in ComposeDeployment.WritableStateDirectories(paths.StateRoot))
            SafeFileSystem.CreateRuntimeDirectory(directory, NativeLinux.getuid(), NativeLinux.getgid());
        Execute(CameraAgentStateLayout.IdentityDatabasePath(paths.StateRoot),
            "CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY, ProductVersion TEXT NOT NULL);" +
            "INSERT INTO __EFMigrationsHistory VALUES('20260827053715_InitialIdentity', '10.0.0');" +
            "CREATE TABLE AspNetUsers(Id TEXT PRIMARY KEY, Email TEXT);" +
            "INSERT INTO AspNetUsers VALUES('1', 'owner@example.test'), ('2', 'operator@example.test');");
        Execute(RawIngressPath(paths),
            "PRAGMA journal_mode=WAL;" +
            $"CREATE TABLE {RawSentinelTable}(id INTEGER PRIMARY KEY, value TEXT NOT NULL);" +
            string.Concat(Enumerable.Range(1, 40).Select(static index => $"INSERT INTO {RawSentinelTable}(value) VALUES('capture-{index}');")) +
            "PRAGMA user_version=12;");
        Execute(CameraAgentStateSnapshot.StorePath(paths.StateRoot, "raw/outbox/artifact-outbox.db"),
            "CREATE TABLE outbox(id INTEGER PRIMARY KEY, artifact TEXT NOT NULL);" +
            "INSERT INTO outbox(artifact) VALUES('a'), ('b'), ('c');");
    }

    private static void MigrateAsCandidate(InstallationPaths paths)
        => Execute(RawIngressPath(paths),
            $"CREATE TABLE IF NOT EXISTS {CandidateMigrationTable}(step INTEGER NOT NULL);" +
            $"INSERT INTO {CandidateMigrationTable} VALUES(1);");

    private static bool HasCandidateMigration(InstallationPaths paths)
        => ReadStoreFacts(paths).Any(static fact => fact.StartsWith(CandidateMigrationTable, StringComparison.Ordinal));

    /// <summary>
    /// A write-ahead log and wal-index the stopped candidate left behind, which a restore must not replay. They carry
    /// a marker so they are told apart from the original image's own sidecars, which are legitimate beside its stores.
    /// </summary>
    private static void LeaveCandidateSidecars(InstallationPaths paths)
    {
        File.WriteAllBytes(RawIngressPath(paths) + "-wal", [.. CandidateSidecarMarker, .. RandomNumberGenerator.GetBytes(4096)]);
        File.WriteAllBytes(RawIngressPath(paths) + "-shm", [.. CandidateSidecarMarker, .. RandomNumberGenerator.GetBytes(32768)]);
    }

    private static readonly byte[] CandidateSidecarMarker = "candidate-sidecar"u8.ToArray();

    private static bool HasCandidateSidecars(InstallationPaths paths)
        => RawIngressSidecars(paths).Any(static path =>
            File.Exists(path) && File.ReadAllBytes(path).AsSpan().StartsWith(CandidateSidecarMarker));

    private static void AddRawRows(InstallationPaths paths, int count)
        => Execute(RawIngressPath(paths), string.Concat(Enumerable.Range(1, count)
            .Select(static index => $"INSERT INTO {RawSentinelTable}(value) VALUES('later-{index}');")));

    private static void Execute(string database, string sql)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-owned statements, not input.
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static List<string> ReadStoreFacts(InstallationPaths paths) => ReadStoreFacts(RawIngressPath(paths));

    /// <summary>The raw journal's user version, each table's row count, and the sentinel rows themselves.</summary>
    private static List<string> ReadStoreFacts(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        var facts = new List<string>();
        using (var version = connection.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version;";
            facts.Add($"user_version={version.ExecuteScalar()}");
        }
        using (var rows = connection.CreateCommand())
        {
            rows.CommandText = $"SELECT group_concat(value, ',') FROM (SELECT value FROM {RawSentinelTable} ORDER BY id);";
            facts.Add($"{RawSentinelTable}={rows.ExecuteScalar()}");
        }
        using (var tables = connection.CreateCommand())
        {
            tables.CommandText = $"SELECT name FROM sqlite_schema WHERE type = 'table' AND name = '{CandidateMigrationTable}';";
            if (tables.ExecuteScalar() is string)
            {
                using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM {CandidateMigrationTable};";
                facts.Add($"{CandidateMigrationTable}={count.ExecuteScalar()}");
            }
        }
        return facts;
    }

    private sealed record BulkFile(ulong Inode, long Length, string Sha256);

    /// <summary>Frames, archived products and derived products in their bulk trees, as a retained instance holds them.</summary>
    private static Dictionary<string, BulkFile> SeedBulk(InstallationPaths paths, int filesPerTree = 12)
    {
        var files = new Dictionary<string, BulkFile>(StringComparer.Ordinal);
        foreach (var tree in new[] { "raw/frames/2026-10-16", "archive/2026/10/16", "raw/derived/2026-10-16" })
        {
            var directory = CameraAgentStateSnapshot.StorePath(paths.StateRoot, tree);
            Directory.CreateDirectory(directory);
            for (var index = 0; index < filesPerTree; index++)
            {
                var path = Path.Combine(directory, $"capture-{index:D4}.fits");
                File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(64 * 1024));
                files[path] = new BulkFile(NativeLinux.GetNodeIdentity(path).Inode, new FileInfo(path).Length, Sha256(path));
            }
        }
        return files;
    }

    /// <summary>Every bulk file is the same file it was: same inode, length and content, so it was neither moved nor copied back.</summary>
    private static void AssertBulkUntouched(Dictionary<string, BulkFile> bulk)
    {
        foreach (var (path, file) in bulk)
        {
            Assert.IsTrue(File.Exists(path), path);
            Assert.AreEqual(file, new BulkFile(NativeLinux.GetNodeIdentity(path).Inode, new FileInfo(path).Length, Sha256(path)), path);
        }
    }

    /// <summary>Nothing outside the state tree holds a copy of a bulk file, and the operations tree stays small.</summary>
    private static void AssertNothingBulkCopied(LifecycleFixture fixture, Dictionary<string, BulkFile> bulk)
    {
        var bulkHashes = bulk.Values.Select(static file => file.Sha256).ToHashSet(StringComparer.Ordinal);
        long operationsBytes = 0;
        foreach (var file in Directory.EnumerateFiles(fixture.Paths.OperationsRoot, "*", SearchOption.AllDirectories))
        {
            operationsBytes += new FileInfo(file).Length;
            Assert.IsFalse(file.EndsWith(".tar.gz", StringComparison.Ordinal), file);
            if (new FileInfo(file).Length == 64 * 1024) Assert.IsFalse(bulkHashes.Contains(Sha256(file)), file);
        }
        Assert.IsLessThan(bulk.Values.Sum(static file => file.Length) / 4, operationsBytes);
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
        foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry.LinkTarget is { } link)
            {
                if (entry is DirectoryInfo) Directory.CreateSymbolicLink(target, link);
                else File.CreateSymbolicLink(target, link);
            }
            else if (entry is DirectoryInfo)
            {
                CopyTree(entry.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
                File.SetUnixFileMode(target, File.GetUnixFileMode(entry.FullName));
            }
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"hvo-crash-images-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            SafeFileSystem.MakeTreeOwnerWritable(Path);
            Directory.Delete(Path, recursive: true);
        }
    }
}
