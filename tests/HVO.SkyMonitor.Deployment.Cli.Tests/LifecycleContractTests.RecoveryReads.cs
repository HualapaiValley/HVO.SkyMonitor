using System.Globalization;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Correction round 3 of #1314 (PR #1340), R15: every value a restore reads from stores the release it replaces wrote is
/// checked before it is used, and a value in a shape the installer does not read degrades the restore rather than
/// blocking it. A capture-schedule profile stored as text, as null, as a blob that is not UTF-8 or as malformed JSON
/// narrows the inventory to the configured roots; a counter stored in another shape is carried only as far as the
/// journal recorded it. Automatic recovery, <c>--resume</c>, <c>--restore-only</c> and a rollback each put the snapshot
/// back and say on standard error what they could not read.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string ProductsRoot = "/app/data/archive/products";
    private const string ProductsOutbox = "archive/products/outbox/artifact-outbox.db";
    private const string ProductsProfile =
        "{\"processingSteps\":[{\"type\":\"FileStorage\",\"options\":{\"storageRoot\":\"" + ProductsRoot + "\"}}]}";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("text")]
    [DataRow("null")]
    [DataRow("non-utf8")]
    [DataRow("malformed-json")]
    public async Task UpgradeAsync_FailedCandidateThatRewroteAScheduleProfile_RecoversOntoItsSnapshotAndSaysWhatItCouldNotRead(string shape)
    {
        using var fixture = await CanonicalProfileFixtureAsync();
        RewriteProfileOnFirstCandidateStart(fixture, shape);
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        AssertRecoveredOntoTheSnapshotsProfile(fixture.Paths, canonical: true);
        AssertSaidItCouldNotReadTheProfile(error.ToString(), shape);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("text", false)]
    [DataRow("null", false)]
    [DataRow("non-utf8", false)]
    [DataRow("malformed-json", false)]
    [DataRow("text", true)]
    [DataRow("null", true)]
    [DataRow("non-utf8", true)]
    [DataRow("malformed-json", true)]
    public async Task InterruptedUpgradeWhoseCandidateRewroteAScheduleProfile_RecoversOntoItsSnapshot(string shape, bool restoreOnly)
    {
        using var fixture = await CanonicalProfileFixtureAsync();
        RewriteProfileOnFirstCandidateStart(fixture, shape);
        var request = UpgradeRequest(fixture, assertMigration: false);
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                request, fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        // The installer loses power once the candidate is verified on its rewritten journal.
        RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:CandidateVerified"));
        Assert.IsFalse(ColumnExists(RawIngressPath(fixture.Paths), "capture_schedule_revisions", "profile_sha256"));
        var interrupted = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var restoredJournalOnResume = false;
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (step == "restore-directories-synced") restoredJournalOnResume = ColumnExists(RawIngressPath(fixture.Paths), "capture_schedule_revisions", "profile_sha256");
        };
        using var error = new StringWriter();
        LifecycleResult result;
        try
        {
            result = await CameraAgentLifecycleManager.ExecuteAsync(
                restoreOnly
                    ? request with { Resume = true, RestoreOnly = true, RecoveryOperationId = interrupted.OperationId }
                    : request with { Resume = true },
                fixture.Runner, _ => new StoreBackedLifecycleClient(fixture.Paths), _ => new FakeOwnerClient(fixture.ApplicationIdentity),
                fixture.Uid, fixture.Gid, CancellationToken.None, error: error);
        }
        finally
        {
            ResetCrashHooks();
        }

        Assert.IsTrue(restoredJournalOnResume, "The recovery did not put the snapshot's journal back.");
        AssertSaidItCouldNotReadTheProfile(error.ToString(), shape);
        if (restoreOnly)
        {
            Assert.AreEqual("restored-previous-healthy-admission-resumed", result.Outcome);
            Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
            var settled = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
            Assert.AreEqual(LifecycleOperationPhase.Restored, settled.Phase);
            Assert.IsNotNull(settled.StateSnapshotRestoredUtc);
        }
        else
        {
            // The retry runs the candidate again on the restored stores, and this time it leaves the profile alone.
            Assert.AreEqual("completed", result.Outcome);
            Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        }
        AssertRecoveredOntoTheSnapshotsProfile(fixture.Paths, canonical: true);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("text")]
    [DataRow("null")]
    [DataRow("non-utf8")]
    [DataRow("malformed-json")]
    public async Task RollbackAsync_AfterTheUpgradedReleaseRewroteAScheduleProfile_RestoresTheRetainedSnapshotAndSaysWhatItCouldNotRead(string shape)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedProductsRevision(fixture.Paths);
        await UpgradeAndCommitAsync(fixture);
        // The upgraded release migrated the profile's storage, which the release it rolls back to never reads.
        ExecuteSql(RawIngressPath(fixture.Paths), RewriteProfileSql(shape));
        using var error = new StringWriter();

        var rolledBack = await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: error);

        Assert.AreEqual("completed", rolledBack.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        AssertRecoveredOntoTheSnapshotsProfile(fixture.Paths, canonical: false);
        AssertSaidItCouldNotReadTheProfile(error.ToString(), shape);
        // The rollback could not read the roots, but its own snapshot still holds every store it replaced, so rolling
        // forward again brings the upgraded release's products outbox back.
        var journal = (await CameraAgentLifecycleManager.ReadOperationAsync(fixture.Paths.LifecycleStatePath, CancellationToken.None))!;
        var own = await CameraAgentStateSnapshot.VerifyAsync(fixture.Paths, fixture.InstanceId, journal.StateSnapshot!, CancellationToken.None);
        CollectionAssert.Contains(own.Databases.Where(static database => database.Present).Select(static database => database.RelativePath).ToArray(), ProductsOutbox);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("text-value")]
    [DataRow("null-value")]
    [DataRow("real-value")]
    [DataRow("out-of-range")]
    [DataRow("text-key")]
    public async Task UpgradeAsync_FailedCandidateThatLeftACounterInAnotherShape_RecoversOntoItsSnapshotAndSaysWhatItCouldNotRead(string shape)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            ExecuteSql(RawIngressPath(fixture.Paths), RewriteControlStateSql(shape));
            // A store the candidate left readable is still carried.
            AdvanceFleet(fixture.Paths, toNextSequence: 20);
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        // The snapshot's control state is back in its own shape, carried no further than the journal recorded it.
        Assert.AreEqual(3, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db",
            "SELECT version FROM capture_control_state WHERE state_key = 1 AND typeof(version) = 'integer';"));
        Assert.AreEqual(20, ReadCounter(fixture.Paths, "raw/.fleet/fleet-status.db", "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;"));
        StringAssert.Contains(error.ToString(), "could not read the counters of 'raw/journal/raw-ingress.db'", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), shape switch
        {
            "text-value" => "holds a value stored as text in version",
            "null-value" => "holds a value stored as null in version",
            "real-value" => "holds a value stored as real in version",
            "out-of-range" => "holds a value outside the range a restore can carry in version",
            _ => "holds a key stored as text in state_key"
        }, StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateWhoseLatestAutomationRevisionIsNotJson_NeverCarriesItIntoTheRestoredStore()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var automation = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(automation, FixtureDump(AutomationStore));
        SaveDefinition(automation, "kept", 1);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            // The candidate creates a definition whose revision the previous release could not read back.
            SaveDefinition(automation, "created", 1);
            ExecuteSql(automation, "UPDATE automation_definition_revisions SET definition_json = 'not json' WHERE definition_id = 'created';");
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.AreEqual(1, ReadCounter(fixture.Paths, AutomationStore, "SELECT version FROM automation_definitions WHERE definition_id = 'kept';"));
        Assert.AreEqual(0, ReadCounter(fixture.Paths, AutomationStore,
            "SELECT COUNT(*) FROM automation_definition_revisions WHERE definition_json = 'not json';"));
        StringAssert.Contains(error.ToString(), $"could not read the counters of '{AutomationStore}'", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), "a revision that is not well-formed JSON", StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("long")]
    [DataRow("control")]
    public async Task UpgradeAsync_FailedCandidateThatNamedARootNoSnapshotCanRecord_RecoversOntoItsSnapshot(string shape)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedProductsRevision(fixture.Paths);
        var root = shape == "long" ? "/app/data/archive/" + new string('x', 600) : "/app/data/archive/a\\u0001b";
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId)
                ExecuteSql(RawIngressPath(fixture.Paths),
                    "INSERT INTO capture_schedule_revisions VALUES('r-candidate', 2, CAST('{\"processingSteps\":[{\"options\":{\"storageRoot\":\"" +
                    root + "\"}}]}' AS BLOB));");
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.AreEqual(0, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db",
            "SELECT COUNT(*) FROM capture_schedule_revisions WHERE revision_id = 'r-candidate';"));
        StringAssert.Contains(error.ToString(), "could not read the storage roots in the journal", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), "outbox location a snapshot cannot record", StringComparison.Ordinal);
    }

    /// <summary>
    /// The canonical schema-15 instance the interrupted-recovery tests use, with one schedule revision naming a storage
    /// root the configuration does not, and an outbox under it.
    /// </summary>
    private static async Task<LifecycleFixture> CanonicalProfileFixtureAsync()
    {
        var fixture = await LifecycleFixture.CreateAsync(
            InstanceLifecycleCondition.Installed, recoveryState: true,
            installedImage: static image => CurrentStateContract(image) with { RawIngressSchema = "15" });
        fixture.Runner.CandidateRawIngressSchema = "15";
        fixture.Runner.CandidateLabels["io.hvo.skymonitor.minimum-compatible-revision"] = new string('6', 40);
        fixture.Runner.ConfigureRuntime(fixture.Paths, fixture.Manifest.Image.ImmutableReference, fixture.Manifest.Image.ImageId,
            CandidateReference, CandidateImageId, fixture.Uid, fixture.Gid);
        WriteCanonicalV15RawIngressJournal(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths),
            "INSERT INTO capture_control_state(state_key, state, version, updated_unix_ms) VALUES(1, 'running', 3, 0);" +
            "INSERT INTO capture_schedule_revisions(revision_id, revision_number, profile_json, profile_sha256, schedule_sha256, source, actor, reason, created_unix_ms) " +
            $"VALUES('r1', 1, CAST('{ProductsProfile}' AS BLOB), '{new string('a', 64)}', '{new string('b', 64)}', 'api', 'owner', NULL, 0);");
        WriteIdentityLineage(fixture.Paths);
        SeedProductsOutbox(fixture.Paths);
        // An installed instance's Compose files are owner-only, as the installer writes them; restore-only checks that.
        foreach (var file in ComposeFileNames.Select(name => Path.Combine(fixture.Paths.ConfigRoot, "compose", name)))
            SafeFileSystem.WriteTextAtomic(file, await File.ReadAllTextAsync(file));
        return fixture;
    }

    /// <summary>One schedule revision in the seeded journal's simplified schema, naming the products root.</summary>
    private static void SeedProductsRevision(InstallationPaths paths)
    {
        ExecuteSql(RawIngressPath(paths), ScheduleRevisionsTable +
            $"INSERT INTO capture_schedule_revisions VALUES('r1', 1, CAST('{ProductsProfile}' AS BLOB));");
        SeedProductsOutbox(paths);
    }

    private static void SeedProductsOutbox(InstallationPaths paths)
        => ExecuteSql(CameraAgentStateSnapshot.StorePath(paths.StateRoot, ProductsOutbox),
            "CREATE TABLE outbox(id INTEGER PRIMARY KEY, artifact TEXT NOT NULL); INSERT INTO outbox(artifact) VALUES('before');");

    /// <summary>
    /// The candidate's first start migrates the profile's storage and writes to the products outbox; a later start, the
    /// retry after a restore, leaves both alone.
    /// </summary>
    private static void RewriteProfileOnFirstCandidateStart(LifecycleFixture fixture, string shape)
    {
        var candidateStarts = 0;
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId || candidateStarts++ > 0) return;
            ExecuteSql(RawIngressPath(fixture.Paths), RewriteProfileSql(shape));
            ExecuteSql(CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, ProductsOutbox), "INSERT INTO outbox(artifact) VALUES('candidate');");
        };
    }

    /// <summary>The journal's only schedule revision, rebuilt as a migration that changed the profile's storage would leave it.</summary>
    private static string RewriteProfileSql(string shape)
        => "PRAGMA foreign_keys = OFF; DROP TABLE capture_schedule_revisions;" +
           "CREATE TABLE capture_schedule_revisions(revision_id TEXT PRIMARY KEY, revision_number INTEGER NOT NULL UNIQUE, profile_json);" +
           $"INSERT INTO capture_schedule_revisions VALUES('r1', 1, {ProfileValue(shape)});";

    private static string ProfileValue(string shape)
    {
        byte[] notUtf8 = [.. "{\"processingSteps\":[{\"options\":{\"storageRoot\":\"/app/data/archive/"u8, 0xFF, 0xFE, .. "\"}}]}"u8];
        return shape switch
        {
            "text" => $"'{ProductsProfile}'",
            "null" => "NULL",
            "non-utf8" => $"X'{Convert.ToHexString(notUtf8)}'",
            "malformed-json" => $"CAST('{ProductsProfile[..^3]}' AS BLOB)",
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown profile shape.")
        };
    }

    /// <summary>The capture-control row a candidate left in <paramref name="shape"/>.</summary>
    private static string RewriteControlStateSql(string shape)
    {
        const string loose = "DROP TABLE capture_control_state; CREATE TABLE capture_control_state(state_key {0} PRIMARY KEY, state TEXT, version, updated_unix_ms INTEGER);" +
                             "INSERT INTO capture_control_state VALUES({1}, 'paused', {2}, 0);";
        return shape switch
        {
            "text-value" => string.Format(CultureInfo.InvariantCulture, loose, "INTEGER", "1", "'seven'"),
            "null-value" => string.Format(CultureInfo.InvariantCulture, loose, "INTEGER", "1", "NULL"),
            "real-value" => string.Format(CultureInfo.InvariantCulture, loose, "INTEGER", "1", "7.5"),
            "out-of-range" => "UPDATE capture_control_state SET version = 9223372036854775807 WHERE state_key = 1;",
            "text-key" => string.Format(CultureInfo.InvariantCulture, loose, "TEXT", "'main'", "7"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unknown counter shape.")
        };
    }

    /// <summary>
    /// The journal's revision is the snapshot's again, byte for byte and in the snapshot's own table, and so is the
    /// products outbox the candidate wrote to.
    /// </summary>
    private static void AssertRecoveredOntoTheSnapshotsProfile(InstallationPaths paths, bool canonical)
    {
        Assert.AreEqual(1, ReadCounter(paths, "raw/journal/raw-ingress.db",
            $"SELECT COUNT(*) FROM capture_schedule_revisions WHERE typeof(profile_json) = 'blob' AND CAST(profile_json AS TEXT) = '{ProductsProfile}';"));
        if (canonical) Assert.IsTrue(ColumnExists(RawIngressPath(paths), "capture_schedule_revisions", "profile_sha256"));
        Assert.AreEqual(0, ReadCounter(paths, ProductsOutbox, "SELECT COUNT(*) FROM outbox WHERE artifact = 'candidate';"));
    }

    private static void AssertSaidItCouldNotReadTheProfile(string error, string shape)
    {
        StringAssert.Contains(error, "could not read the storage roots in the journal", StringComparison.Ordinal);
        StringAssert.Contains(error, shape switch
        {
            "text" => "stores its profile as text",
            "null" => "stores its profile as null",
            "non-utf8" => "profile is not valid UTF-8",
            _ => "is not valid JSON"
        }, StringComparison.Ordinal);
    }
}
