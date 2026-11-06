using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Correction round 2 of #1314 (PR #1340), R9: a snapshot restore never lets a version or sequence a client holds be
/// handed out again for different state. Each store here is built from the previous-release image's own DDL, so the
/// counters are the real columns the CameraAgent reads and writes.
/// </summary>
public sealed partial class LifecycleContractTests
{
    private const string AutomationStore = "raw/.automation/local-automations.db";

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("capture_schedule_state", "version",
        ScheduleRevision + "INSERT INTO capture_schedule_state(state_key, active_revision_id, version, updated_unix_ms) VALUES(1, 'profile-00000001-abcdefabcdef', 4, 0);",
        DisplayName = "schedule")]
    [DataRow("named_rig_selection", "version", "UPDATE named_rig_selection SET version = 4;", DisplayName = "rig selection")]
    [DataRow("calibration_library_state", "version", "UPDATE calibration_library_state SET version = 4;", DisplayName = "calibration library")]
    [DataRow("processing_graph_registry_state", "state_version",
        "INSERT INTO processing_graph_revisions(revision_id, graph_name, revision_name, lifecycle, definition_identity_sha256, " +
        "shared_plan_identity_sha256, local_plan_identity_sha256, pipeline_json, definition_json, frozen_plan_json, nodes_json, created_unix_ms) " +
        "VALUES('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'basic', 'configured', 'Active', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', CAST('{}' AS BLOB), CAST('{}' AS BLOB), CAST('{}' AS BLOB), CAST('[]' AS BLOB), 0);" +
        "INSERT INTO processing_graph_registry_state(state_key, selection_mode, active_revision_id, configured_basic_revision_id, state_version, updated_unix_ms) " +
        "VALUES(1, 'ConfiguredBasic', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 4, 0);",
        DisplayName = "processing graph registry")]
    public async Task RollbackAsync_AVersionAClientWasGivenAfterTheSnapshot_NeverMatchesTheRestoredState(string table, string column, string seed)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedRawIngressImage(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths), seed);
        await UpgradeAndCommitAsync(fixture);
        // The upgraded release takes three edits, handing out versions 5, 6 and 7 for states the snapshot never had.
        ExecuteSql(RawIngressPath(fixture.Paths), $"UPDATE {table} SET {column} = 7;");

        await RollBackAsync(fixture);

        // A client still holding 7 (or 5, or 6) is stale against the restored state, and the next edit issues 9.
        Assert.AreEqual(8, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", $"SELECT {column} FROM {table} WHERE state_key = 1;"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task RollbackAsync_AutomationDefinitionVersionsHandedOutAfterTheSnapshot_AreNeverIssuedAgain()
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var automation = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, AutomationStore);
        ExecuteSql(automation, FixtureDump(AutomationStore));
        SaveDefinition(automation, "kept", 1, 2, 3);
        SaveDefinition(automation, "removed-later", 1, 2);
        await UpgradeAndCommitAsync(fixture);
        // After the upgrade: one definition is edited to version 6, one is created and edited to 2, one is removed (the
        // removal itself takes version 3).
        SaveDefinition(automation, "kept", 4, 5, 6);
        SaveDefinition(automation, "created", 1, 2);
        RemoveDefinition(automation, "removed-later", 3);

        await RollBackAsync(fixture);

        Assert.AreEqual(7, ReadCounter(fixture.Paths, AutomationStore, "SELECT version FROM automation_definitions WHERE definition_id = 'kept';"));
        Assert.AreEqual(4, ReadCounter(fixture.Paths, AutomationStore, "SELECT version FROM automation_definitions WHERE definition_id = 'removed-later';"));
        // The definition the snapshot never had stays gone, recorded as removed above version 2, so recreating it
        // (the store continues a removed definition's line) issues 4, not 1.
        Assert.AreEqual(0, ReadCounter(fixture.Paths, AutomationStore, "SELECT COUNT(*) FROM automation_definitions WHERE definition_id = 'created';"));
        Assert.AreEqual(3, ReadCounter(fixture.Paths, AutomationStore, "SELECT MAX(version) FROM automation_definition_revisions WHERE definition_id = 'created';"));
        Assert.AreEqual(1, ReadCounter(fixture.Paths, AutomationStore,
            "SELECT removed FROM automation_definition_revisions WHERE definition_id = 'created' AND version = 3;"));
        Assert.AreEqual(1, ReadCounter(fixture.Paths, AutomationStore,
            $"SELECT COUNT(*) FROM automation_definition_revisions WHERE definition_id = 'created' AND version = 3 AND definition_json = '{DefinitionJson("created", 2)}';"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow("raw/.environment/environmental-observation-outbox.db", "environmental_observation_outbox", DisplayName = "environmental outbox")]
    [DataRow("raw/.environment/environmental-observation-outbox.db", "environmental_observation_journal", DisplayName = "environmental journal")]
    [DataRow("raw/evidence/execution-evidence-outbox.db", "execution_evidence_units", DisplayName = "execution evidence")]
    public async Task RollbackAsync_ARowIdInAClientActionTokenIsNeverReissued(string store, string table)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        var path = CameraAgentStateSnapshot.StorePath(fixture.Paths.StateRoot, store);
        ExecuteSql(path, FixtureDump(store) + $"INSERT INTO sqlite_sequence(name, seq) VALUES('{table}', 10);");
        await UpgradeAndCommitAsync(fixture);
        // Rows 11 to 25 are created after the upgrade, and an operator is shown action tokens naming them.
        ExecuteSql(path, $"UPDATE sqlite_sequence SET seq = 25 WHERE name = '{table}';");

        await RollBackAsync(fixture);

        Assert.AreEqual(25, ReadCounter(fixture.Paths, store, $"SELECT seq FROM sqlite_sequence WHERE name = '{table}';"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(1, true, DisplayName = "created at version 1, as the previous release would recreate it")]
    [DataRow(3, false, DisplayName = "advanced to version 3")]
    public async Task RollbackAsync_ScheduleStateTheSnapshotLacks_IsCarriedOnlyWhenTheLazyRowWouldMatch(int liveVersion, bool matches)
    {
        using var fixture = await MigratingFixtureAsync();
        SeedStores(fixture.Paths);
        SeedRawIngressImage(fixture.Paths);
        ExecuteSql(RawIngressPath(fixture.Paths), ScheduleRevision);
        await UpgradeAndCommitAsync(fixture);
        ExecuteSql(RawIngressPath(fixture.Paths), string.Create(CultureInfo.InvariantCulture,
            $"INSERT INTO capture_schedule_state(state_key, active_revision_id, version, updated_unix_ms) VALUES(1, 'profile-00000001-abcdefabcdef', {liveVersion}, 0);"));

        using var error = new StringWriter();
        Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: error)).Outcome);

        // The previous release recreates the row it lacks. A version of 1 is the state that row describes, so nothing is
        // lost; a higher version cannot be carried into a row the snapshot lacks, and since carrying is best effort the
        // rollback completes and says so (#1314 R17–R20) rather than refusing.
        Assert.AreEqual(0, ReadCounter(fixture.Paths, "raw/journal/raw-ingress.db", "SELECT COUNT(*) FROM capture_schedule_state;"));
        Assert.AreEqual(fixture.Manifest.Image.ImageId, fixture.Runner.ActiveImageId);
        if (matches)
            Assert.IsFalse(error.ToString().Contains("could not carry", StringComparison.Ordinal), error.ToString());
        else
            StringAssert.Contains(error.ToString(),
                "could not carry the capture-schedule-version counter of 'raw/journal/raw-ingress.db' with key \"1\" (the snapshot has no row to carry it into)",
                StringComparison.Ordinal);
    }

    private const string ScheduleRevision =
        "INSERT INTO capture_schedule_revisions(revision_id, revision_number, profile_json, profile_sha256, schedule_sha256, source, actor, created_unix_ms) " +
        "VALUES('profile-00000001-abcdefabcdef', 1, CAST('{}' AS BLOB), 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'file-bootstrap', 'system', 0);";

    /// <summary>
    /// Adds every table of the previous-release raw-ingress image to the seeded journal, so a counter's row has the
    /// parent tables its foreign keys name. The image's schema version is left out, so the journal keeps the one the seed set.
    /// </summary>
    private static void SeedRawIngressImage(InstallationPaths paths)
        => ExecuteSql(RawIngressPath(paths), Regex.Replace(
            FixtureDump("raw/journal/raw-ingress.db"), "^PRAGMA user_version = \\d+;$", string.Empty,
            RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)));

    private static async Task UpgradeAndCommitAsync(LifecycleFixture fixture)
        => Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            UpgradeRequest(fixture, assertMigration: false), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None)).Outcome);

    private static async Task RollBackAsync(LifecycleFixture fixture)
        => Assert.AreEqual("completed", (await CameraAgentLifecycleManager.ExecuteAsync(
            fixture.Request(LifecycleOperationKind.Rollback), fixture.Runner, _ => new FakeLifecycleClient(),
            _ => new FakeOwnerClient(fixture.ApplicationIdentity), fixture.Uid, fixture.Gid, CancellationToken.None,
            error: TextWriter.Null)).Outcome);

    /// <summary>Saves an automation definition at each of <paramref name="versions"/> in turn, as the store does.</summary>
    private static void SaveDefinition(string automation, string id, params int[] versions)
    {
        foreach (var version in versions)
        {
            ExecuteSql(automation, string.Create(CultureInfo.InvariantCulture, $"""
                INSERT INTO automation_definitions(definition_id, name, enabled, task_kind, task_target, trigger_kind,
                    trigger_interval, trigger_epoch_unix_ms, version, revision_sha256, updated_unix_ms, actor)
                VALUES('{id}', '{id}', 1, 'time-lapse', 'night', 'interval', 60, 0, {version}, '{Sha(id, version)}', 0, 'owner')
                ON CONFLICT(definition_id) DO UPDATE SET version = excluded.version, revision_sha256 = excluded.revision_sha256;
                INSERT INTO automation_definition_revisions(definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor)
                VALUES('{id}', {version}, '{Sha(id, version)}', '{DefinitionJson(id, version)}', 0, 0, 'owner');
                """));
        }
    }

    private static void RemoveDefinition(string automation, string id, int version)
        => ExecuteSql(automation, string.Create(CultureInfo.InvariantCulture, $"""
            DELETE FROM automation_definitions WHERE definition_id = '{id}';
            INSERT INTO automation_definition_revisions(definition_id, version, revision_sha256, definition_json, removed, recorded_unix_ms, actor)
            VALUES('{id}', {version}, '{Sha(id, version - 1)}', '{DefinitionJson(id, version - 1)}', 1, 0, 'owner');
            """));

    private static string DefinitionJson(string id, int version)
        => string.Create(CultureInfo.InvariantCulture, $"{{\"definitionId\":\"{id}\",\"edit\":{version}}}");

    private static string Sha(string id, int version)
        => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(DefinitionJson(id, version))));

    /// <summary>The previous-release image of the store at <paramref name="location"/>: every table, metadata row and index.</summary>
    private static string FixtureDump(string location)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "previous-release");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        var dump = manifest.RootElement.GetProperty("stores").EnumerateArray()
            .Single(store => store.GetProperty("location").GetString() == location).GetProperty("dump").GetString()!;
        return File.ReadAllText(Path.Combine(directory, dump));
    }
}
