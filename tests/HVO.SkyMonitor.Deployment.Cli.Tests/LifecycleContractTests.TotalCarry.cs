using System.Globalization;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Correction round 4 of #1314 (PR #1340): carrying counters is best effort and total. Every carry is planned and proved
/// on a scratch copy of the target store before any store is replaced, and a floor or store that cannot be carried, for
/// any reason, is put back as the snapshot recorded it and named on standard error, so a value a candidate or a snapshot
/// holds never stops a restore. R17 is a rollback restoring its own snapshot of values the installer cannot interpret;
/// R18 a state token at the edge of what a carry can write and verify; R19 an automation ID the target's CHECK refuses;
/// R20 a store in a UTF-16 encoding; and a fault at each carry step.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string FleetStore = "raw/.fleet/fleet-status.db";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("blob-revision")]
    [DataRow("text-version")]
    [DataRow("utf16-store")]
    public async Task RollbackAsync_FailedRollbackRestoresItsOwnSnapshotWhoseValuesTheInstallerCannotInterpret(string shape)
    {
        using var fixture = await UpgradedReleaseFixtureAsync(shape);
        using var error = new StringWriter();

        // The rollback's image fails its verification, so recovery puts the rollback's own snapshot of the upgraded
        // release back.
        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(CandidateImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        AssertUpgradedReleaseStores(fixture.Paths, shape);
        AssertSaidWhatItCouldNotCarry(error.ToString(), shape);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("blob-revision")]
    [DataRow("text-version")]
    [DataRow("utf16-store")]
    public async Task RollbackAsync_InterruptedRollbackResumesOntoItsOwnSnapshotWhoseValuesTheInstallerCannotInterpret(string shape)
    {
        using var fixture = await UpgradedReleaseFixtureAsync(shape);
        using var captured = new TemporaryDirectory();
        var images = new List<CrashImage>();
        CaptureCrashImages(fixture, captured.Path, images);
        try
        {
            Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
                fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
                error: TextWriter.Null)).Outcome);
        }
        finally
        {
            ResetCrashHooks();
        }
        // Power is lost once the retained snapshot is back, before the rollback's image starts.
        RestoreCrashImage(fixture, images.First(static image => image.Step == "journal:RetainedStateApplied"));
        var ownRestored = false;
        CameraAgentStateSnapshot.StepCompleted = step =>
        {
            if (step != "restore-directories-synced" || ownRestored) return;
            ownRestored = true;
            AssertUpgradedReleaseStores(fixture.Paths, shape);
        };
        using var error = new StringWriter();
        LifecycleResult result;
        try
        {
            result = await CameraAgentLifecycleManager.ExecuteAsync(
                fixture.Request(LifecycleOperationKind.Rollback) with { Resume = true }, fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None, error: error);
        }
        finally
        {
            ResetCrashHooks();
        }

        Assert.IsTrue(ownRestored, "The resume never put the rollback's own snapshot back.");
        Assert.AreEqual("completed", result.Outcome);
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        AssertSaidWhatItCouldNotCarry(error.ToString(), shape);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("capture_schedule_state", "version", "state_key = 1", false, DisplayName = "a state token, which writes one above its floor")]
    [DataRow("raw_capture_sequences", "last_sequence", "agent_id = 'agent-1'", true, DisplayName = "a high-water mark, which writes its floor")]
    public async Task UpgradeAsync_FailedCandidateThatLeftACounterAtTheEdgeOfTheRange_RecoversAndCarriesOnlyWhatItCanVerify(
        string table, string column, string where, bool carried)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedRawIngressImage(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths), ScheduleRevision +
            "INSERT INTO capture_schedule_state(state_key, active_revision_id, version, updated_unix_ms) VALUES(1, 'profile-00000001-abcdefabcdef', 2, 0);" +
            "INSERT INTO raw_capture_sequences(agent_id, last_sequence) VALUES('agent-1', 5);");
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            ExecuteSql(RawIngressPath(fixture.Paths), string.Create(CultureInfo.InvariantCulture,
                $"UPDATE {table} SET {column} = {long.MaxValue - 1} WHERE {where};"));
            // Another counter in the same store is still carried.
            ExecuteSql(RawIngressPath(fixture.Paths), table == "raw_capture_sequences"
                ? "UPDATE capture_schedule_state SET version = 7 WHERE state_key = 1;"
                : "UPDATE raw_capture_sequences SET last_sequence = 9 WHERE agent_id = 'agent-1';");
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        var restored = ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", $"SELECT {column} FROM {table} WHERE {where};");
        if (carried)
        {
            Assert.AreEqual(long.MaxValue - 1, restored);
            Assert.AreEqual(8, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", "SELECT version FROM capture_schedule_state;"));
            Assert.IsFalse(error.ToString().Contains("could not carry", StringComparison.Ordinal), error.ToString());
        }
        else
        {
            Assert.AreEqual(2, restored);
            Assert.AreEqual(9, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", "SELECT last_sequence FROM raw_capture_sequences;"));
            StringAssert.Contains(error.ToString(),
                "could not carry the capture-schedule-version counter of 'raw/journal/raw-ingress.db' (its value is outside the range a carry can write and verify)",
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task UpgradeAsync_FailedCandidateThatRecordedADefinitionWithANulId_RecoversAndCarriesTheOtherDefinitions()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var automation = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(automation, FixtureDump(AutomationStore));
        SaveDefinition(automation, "kept", 1);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            // The candidate's revision table no longer checks IDs, and it records a definition whose ID is a lone NUL,
            // which the previous release's CHECK (length(definition_id) BETWEEN 1 AND 64) refuses.
            ExecuteSql(automation, RelaxedRevisionsTable +
                $"INSERT INTO automation_definition_revisions(definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor) VALUES(char(0), 1, '{new string('c', 64)}', '{{}}', 1, 0, 'owner');");
            SaveDefinition(automation, "kept", 2);
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        // The definition the candidate saved at version 2 continues above it; the NUL definition is not recorded.
        Assert.AreEqual(3, ReadCounter(fixture.Paths, AutomationStore, "SELECT version FROM automation_definitions WHERE definition_id = 'kept';"));
        Assert.AreEqual(0, ReadCounter(fixture.Paths, AutomationStore, "SELECT COUNT(*) FROM automation_definition_revisions WHERE definition_id = char(0);"));
        StringAssert.Contains(error.ToString(),
            $"could not carry the automation-definition-version counter of '{AutomationStore}' (its key contains a NUL character)",
            StringComparison.Ordinal);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("UTF-16le")]
    [DataRow("UTF-16be")]
    public async Task UpgradeAsync_FailedCandidateThatRewroteAStoreInUtf16_RecoversAndSaysWhatItCouldNotRead(string encoding)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image == CandidateImageId) RewriteFleetStore(fixture.Paths, encoding, nextSequence: 20);
        };
        using var error = new StringWriter();

        await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
            fixture.Uid, fixture.Gid, CancellationToken.None, error: error));

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        Assert.IsTrue(fixture.Runner.Running);
        Assert.AreEqual("UTF-8", DatabaseEncoding(CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, FleetStore)));
        Assert.AreEqual(10, ReadCounter(fixture.Paths, FleetStore, "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;"));
        StringAssert.Contains(error.ToString(), $"could not read the counters of '{FleetStore}'", StringComparison.Ordinal);
        StringAssert.Contains(error.ToString(), $"is encoded as {encoding}, not UTF-8", StringComparison.Ordinal);
    }

    [TestMethod]
    [DoNotParallelize]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("read:" + FleetStore, false)]
    [DataRow("trim", true)]
    [DataRow("screen:" + FleetStore, false)]
    [DataRow("trial:" + FleetStore, false)]
    [DataRow("trial-verify:" + FleetStore, false)]
    [DataRow("apply:" + FleetStore, false)]
    [DataRow("apply-verify:" + FleetStore, false)]
    public async Task UpgradeAsync_AFaultAtAnyCarryStep_DegradesOnlyThatStoreAndCompletesTheRestore(string step, bool fleetCarried)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        fixture.Runner.OnComposeUp = image =>
        {
            if (image != CandidateImageId) return;
            AdvanceFleet(fixture.Paths, toNextSequence: 20);
            ExecuteSql(RawIngressPath(fixture.Paths), $"UPDATE raw_capture_sequences SET last_sequence = 9 WHERE agent_id = '{AgentId}';");
        };
        using var error = new StringWriter();
        // An exception of a type no step expects, thrown at exactly one step.
        CameraAgentStateCounters.CarryStep = current =>
        {
            if (current == step) throw new TimeZoneNotFoundException("injected carry fault");
        };
        try
        {
            await Assert.ThrowsExactlyAsync<InstallerException>(() => CameraAgentLifecycleManager.ExecuteAsync(
                UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
                _ => new FakeOwnerClient(fixture.ApplicationIdentity) { RejectNextVerification = true },
                fixture.Uid, fixture.Gid, CancellationToken.None, error: error));
        }
        finally
        {
            CameraAgentStateCounters.CarryStep = static _ => { };
        }

        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId, error.ToString());
        Assert.IsTrue(fixture.Runner.Running);
        Assert.AreEqual(fleetCarried ? 20 : 10,
            ReadCounter(fixture.Paths, FleetStore, "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;"));
        // The other store's carry is untouched by the fault.
        Assert.AreEqual(9, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", $"SELECT last_sequence FROM raw_capture_sequences WHERE agent_id = '{AgentId}';"));
        if (!fleetCarried)
        {
            StringAssert.Contains(error.ToString(), $"the counters of '{FleetStore}' (injected carry fault)", StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A committed migrating upgrade whose release then left a store the rollback's own snapshot will hold in a shape the
    /// installer cannot interpret: an automation revision stored as a blob, a definition version stored as text, or the
    /// fleet store rewritten in UTF-16.
    /// </summary>
    private static async Task<LifecycleFixture> UpgradedReleaseFixtureAsync(string shape)
    {
        var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedCounters(fixture.Paths, captureSequence: 5, fleetNextSequence: 10);
        var automation = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(automation, FixtureDump(AutomationStore));
        SaveDefinition(automation, "kept", 1);
        await UpgradeAndCommitAsync(fixture);
        switch (shape)
        {
            case "blob-revision":
                ExecuteSql(automation, RelaxedRevisionsTable +
                    "INSERT INTO automation_definition_revisions(definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor) " +
                    $"VALUES('kept', 2, '{new string('d', 64)}', CAST('{{\"definitionId\":\"kept\",\"edit\":2}}' AS BLOB), 0, 0, 'owner');" +
                    "UPDATE automation_definitions SET version = 2 WHERE definition_id = 'kept';");
                break;
            case "text-version":
                ExecuteSql(automation,
                    "CREATE TABLE d2(definition_id TEXT PRIMARY KEY, name TEXT, enabled INTEGER, task_kind TEXT, task_target TEXT, trigger_kind TEXT," +
                    " trigger_interval INTEGER, trigger_epoch_unix_ms INTEGER, version, revision_sha256 TEXT, updated_unix_ms INTEGER, actor TEXT," +
                    " reason TEXT, source_window_json TEXT);" +
                    "INSERT INTO d2 SELECT definition_id, name, enabled, task_kind, task_target, trigger_kind, trigger_interval, trigger_epoch_unix_ms," +
                    " 'two', revision_sha256, updated_unix_ms, actor, reason, source_window_json FROM automation_definitions;" +
                    "DROP TABLE automation_definitions; ALTER TABLE d2 RENAME TO automation_definitions;");
                break;
            default:
                RewriteFleetStore(fixture.Paths, "UTF-16le", nextSequence: 15);
                break;
        }
        return fixture;
    }

    /// <summary>The upgraded release's own stores, as its snapshot holds them, are back.</summary>
    private static void AssertUpgradedReleaseStores(InstallationPaths paths, string shape)
    {
        switch (shape)
        {
            case "blob-revision":
                Assert.AreEqual(1, ReadCounter(paths, AutomationStore,
                    "SELECT COUNT(*) FROM automation_definition_revisions WHERE definition_id = 'kept' AND typeof(definition_json) = 'blob';"));
                break;
            case "text-version":
                Assert.AreEqual(1, ReadCounter(paths, AutomationStore, "SELECT COUNT(*) FROM automation_definitions WHERE version = 'two';"));
                break;
            default:
                Assert.AreEqual("UTF-16le", DatabaseEncoding(CameraAgentStateSnapshot.StorePath(paths.StateRoot, FleetStore)));
                Assert.AreEqual(15, ReadCounter(paths, FleetStore, "SELECT next_sequence FROM fleet_status_metadata WHERE metadata_key = 1;"));
                break;
        }
    }

    /// <summary>
    /// A blob revision is never needed for the carry, so nothing is reported for it; a version or store the installer
    /// cannot read is put back without its counters and named.
    /// </summary>
    private static void AssertSaidWhatItCouldNotCarry(string error, string shape)
    {
        switch (shape)
        {
            case "blob-revision":
                return;
            case "text-version":
                StringAssert.Contains(error, $"could not carry the counters of '{AutomationStore}'", StringComparison.Ordinal);
                break;
            default:
                StringAssert.Contains(error, $"could not carry the counters of '{FleetStore}'", StringComparison.Ordinal);
                StringAssert.Contains(error, "is encoded as UTF-16le, not UTF-8", StringComparison.Ordinal);
                break;
        }
    }

    /// <summary>The automation revision table rebuilt without its STRICT types and CHECKs, as a later release might.</summary>
    private const string RelaxedRevisionsTable =
        "CREATE TABLE r2(sequence INTEGER PRIMARY KEY AUTOINCREMENT, definition_id TEXT NOT NULL, version INTEGER NOT NULL," +
        " revision_sha256 TEXT NOT NULL, definition_json NOT NULL, removed INTEGER NOT NULL, recorded_unix_ms INTEGER NOT NULL," +
        " actor TEXT NOT NULL, reason TEXT);" +
        "INSERT INTO r2 SELECT sequence, definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor, reason" +
        " FROM automation_definition_revisions;" +
        "DROP TABLE automation_definition_revisions; ALTER TABLE r2 RENAME TO automation_definition_revisions;";

    /// <summary>Replaces the fleet store with one in <paramref name="encoding"/>, holding the same schema and one row.</summary>
    private static void RewriteFleetStore(InstallationPaths paths, string encoding, long nextSequence)
    {
        var path = CameraAgentStateSnapshot.StorePath(paths.StateRoot, FleetStore);
        foreach (var file in CameraAgentStateLayout.SqliteSidecarSuffixes.Select(suffix => path + suffix).Prepend(path))
            File.Delete(file);
        ExecuteSql(path,
            FormattableString.Invariant($"PRAGMA encoding = '{encoding}';") +
            "CREATE TABLE fleet_status_metadata(metadata_key INTEGER NOT NULL PRIMARY KEY CHECK(metadata_key = 1)," +
            " schema_version INTEGER NOT NULL, agent_instance_id TEXT NULL, next_sequence INTEGER NOT NULL CHECK(next_sequence > 0)," +
            " overflow_count INTEGER NOT NULL DEFAULT 0) STRICT;" +
            FormattableString.Invariant($"INSERT INTO fleet_status_metadata VALUES(1, 1, NULL, {nextSequence}, 0);"));
        Assert.AreEqual(encoding, DatabaseEncoding(path));
    }

    private static string DatabaseEncoding(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA encoding;";
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }
}
