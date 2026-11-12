extern alias InstallerRuntime;

using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Tests.Deployment;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SnapshotApi = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateSnapshot;
using InstallerPaths = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallationPaths;
using InstallerLayout = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateLayout;
using InstallerLabels = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentImageLabels;
using InstallerMigration = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateMigration;
using InstallerFiles = InstallerRuntime::HVO.SkyMonitor.Deployment.SafeFileSystem;
using InstallerNative = InstallerRuntime::HVO.SkyMonitor.Deployment.NativeLinux;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class LocalAutomationCheckpointRollbackTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task CurrentBaseUpgradeCheckpointsBeforeAutomationMigrationAndRestoresSchemaThree()
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-automation-rollback-" + Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            var paths = InstallerPaths.Create(root, id, "synthetic");
            foreach (var directory in new[] { paths.ProductRoot, paths.InstanceRoot, paths.StateRoot, paths.OperationsRoot })
                InstallerFiles.CreateOwnerDirectory(directory);
            using var labelsFixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "automation-schema3-processing9-image-labels.json")).ConfigureAwait(false));
            Assert.AreEqual("38320d3a4745327b7d5c1d28b6ffcf3485e084a7", labelsFixture.RootElement.GetProperty("sourceRevision").GetString());
            var previousLabels = labelsFixture.RootElement.GetProperty("imageLabels");
            var installed = InstallerLabels.Identity("registry", "base", $"sha256:{new string('1', 64)}", "amd64", null, previousLabels);
            var candidate = InstallerLabels.Identity("registry", "candidate", $"sha256:{new string('2', 64)}", "amd64", null,
                JsonSerializer.SerializeToElement(CameraAgentStateCompatibilityLabelTests.ReadImageLabels()));
            var changes = InstallerMigration.DeclaredChanges(paths, installed, installed.UpgradeCompatibility, candidate,
                Path.Combine(paths.StateRoot, "absent", "raw-ingress.db"));
            Assert.HasCount(1, changes, "A current-base upgrade must select the checkpoint before opening the schema-3 automation store.");

            var raw = Path.Combine(paths.StateRoot, "raw");
            var database = Path.Combine(raw, ".automation", "local-automations.db");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            var schemaThree = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory,
                "Fixtures", "previous-release", "raw__.automation__local-automations.db.sql")).ConfigureAwait(false);
            await ExecuteAsync(database, schemaThree + """
                INSERT INTO automation_commands VALUES ('baseline-command', 'save',
                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', 'retained', 1);
                """).ConfigureAwait(false);
            var before = await FactsAsync(database).ConfigureAwait(false);
            Assert.AreEqual(3L, before.Version);
            Assert.AreEqual(0L, before.StopTables);
            Assert.AreEqual(1L, before.Commands);
            var snapshot = await SnapshotApi.CreateAsync(paths, id, Guid.NewGuid(), installed,
                InstallerLayout.SqliteStores, default).ConfigureAwait(false);

            using (var store = new SqliteLocalAutomationStore(new ScriptControlsFixture.ScriptRegistry(), new StubCaptureSequenceSource(),
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = raw }), TimeProvider.System,
                NullLogger<SqliteLocalAutomationStore>.Instance))
                await store.InitializeAsync(default).ConfigureAwait(false);
            var migrated = await FactsAsync(database).ConfigureAwait(false);
            Assert.AreEqual(4L, migrated.Version);
            Assert.AreEqual(1L, migrated.StopTables);
            Assert.AreEqual(before.Commands, migrated.Commands);
            // Candidate-only state must disappear with the schema-4 audit table on rollback.
            await ExecuteAsync(database, "UPDATE automation_commands SET recorded_unix_ms = 2;").ConfigureAwait(false);
            var plan = await SnapshotApi.PlanRestoreAsync(paths, id, snapshot, [], default).ConfigureAwait(false);
            _ = await SnapshotApi.RestoreAsync(paths, id, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                [], plan, default).ConfigureAwait(false);
            Assert.AreEqual(before, await FactsAsync(database).ConfigureAwait(false),
                "The schema-3 reader receives its exact schema, retained command and pre-upgrade timestamp.");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only the checked-in frozen schema fixture and fixed SQL literals are executed; no user input is accepted.")]
    private static async Task ExecuteAsync(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<(long Version, long StopTables, long Commands, long Timestamp, string Schema)> FactsAsync(string database)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT user_version FROM pragma_user_version),
                (SELECT COUNT(*) FROM sqlite_schema WHERE name = 'automation_script_stops'),
                (SELECT COUNT(*) FROM automation_commands WHERE idempotency_key = 'baseline-command'),
                (SELECT recorded_unix_ms FROM automation_commands WHERE idempotency_key = 'baseline-command'),
                (SELECT group_concat(sql, char(10)) FROM (SELECT sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name));
            """;
        using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4));
    }
}
