extern alias InstallerRuntime;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Deployment;
using HVO.SkyMonitor.CameraAgent.Tests.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using InstallerFiles = InstallerRuntime::HVO.SkyMonitor.Deployment.SafeFileSystem;
using InstallerLabels = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentImageLabels;
using InstallerInventory = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateInventory;
using InstallerMigration = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateMigration;
using InstallerNative = InstallerRuntime::HVO.SkyMonitor.Deployment.NativeLinux;
using InstallerPaths = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallationPaths;
using ReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;
using SnapshotApi = InstallerRuntime::HVO.SkyMonitor.Deployment.CameraAgentStateSnapshot;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux)]
    [SupportedOSPlatform("linux")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Setup uses the fixed historical schema and a fixed retained journal row.")]
    public async Task CenteredProcessingUpgradeRestoresThePriorRawAndProcessingCheckpoint(bool rehearsalPublisher)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-processing-rollback");
        var token = TestContext.CancellationToken;
        try
        {
            var instance = Guid.NewGuid();
            var paths = InstallerPaths.Create(root, instance, "synthetic");
            foreach (var directory in new[] { paths.ProductRoot, paths.InstanceRoot, paths.StateRoot, paths.OperationsRoot })
                InstallerFiles.CreateOwnerDirectory(directory);
            var raw = Path.Combine(paths.StateRoot, "raw");
            var database = Path.Combine(raw, "journal", "raw-ingress.db");
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = raw, RawIngressSqliteBusyTimeoutSeconds = 1 });
            var journal = new SqliteRawCaptureJournal(database, 1);
            await journal.InitializeAsync(token).ConfigureAwait(false);
            var capture = await journal.ReserveIdentityAsync("checkpoint-agent", Guid.NewGuid(), Guid.NewGuid(), token).ConfigureAwait(false);
            string? environmentDatabase = null;
            if (rehearsalPublisher)
            {
                using var processing = new SqliteCaptureProcessingStore(options);
                await processing.InitializeAsync(token).ConfigureAwait(false);
                environmentDatabase = await SeedRehearsalEnvironmentAsync(raw, token).ConfigureAwait(false);
            }

            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var seed = connection.CreateCommand();
                var legacyProcessingSchema = rehearsalPublisher ? string.Empty :
                    SqliteCaptureProcessingStore.LegacySchema10SqlForTests +
                    "DROP INDEX ix_processing_centered_output_availability_cutoff;\n" +
                    SqliteCaptureProcessingStore.LegacyCenteredAvailabilityCutoffIndexForTests;
                seed.CommandText = legacyProcessingSchema + """
                    INSERT INTO processing_nodes(capture_id, node_id, required, dependencies_json,
                        recipe_name, output_role, output_variant, plan_sha256, status, attempt, completed_unix_ms)
                    VALUES ($capture, 'checkpoint-probe', 1, '[]', 'fixture', 'Preview', 'default',
                        $plan, 'Completed', 1, 12);
                    """;
                seed.Parameters.AddWithValue("$capture", capture.CaptureId.ToString("N"));
                seed.Parameters.AddWithValue("$plan", new string('A', 64));
                await seed.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            const string version = "version:capture_processing_schema.version";
            var before = CameraAgentStateStoreHarness.ReadSchema(database);
            Assert.AreEqual("17", before["pragma:user_version"]);
            Assert.AreEqual(rehearsalPublisher ? "12" : "10", before[version]);
            const string boundary = "io.hvo.skymonitor.minimum-compatible-revision";
            var candidateLabels = CameraAgentStateCompatibilityLabelTests.ReadImageLabels();
            var previousLabels = new Dictionary<string, string>(candidateLabels, StringComparer.Ordinal)
            {
                [boundary] = rehearsalPublisher ? "150886d93ec3cef42c2ebe2bfea29d1a8419f2a3" : "9c233e2ea15c81c0077757927cec3dc6adbbe973"
            };
            var previous = InstallerLabels.Identity("registry", "schema-10", $"sha256:{new string('1', 64)}", "amd64", null,
                JsonSerializer.SerializeToElement(previousLabels)) with
            {
                SourceRevision = rehearsalPublisher ? "eda8cb17a6481dec6cf27469428031f0cf3d9a00" : "5332f010aaa574d13ccfaa67e2201d869c07325e"
            };
            var candidate = InstallerLabels.Identity("registry", "schema-12", $"sha256:{new string('2', 64)}", "amd64", null,
                JsonSerializer.SerializeToElement(candidateLabels));
            var changes = InstallerMigration.DeclaredChanges(paths, previous, previous.UpgradeCompatibility, candidate, database);
            Assert.AreEqual($"{boundary} installed {previousLabels[boundary]}, candidate {candidateLabels[boundary]}",
                Assert.ContainsSingle(changes));
            Assert.IsEmpty(InstallerMigration.DeclaredChanges(paths, candidate, candidate.UpgradeCompatibility, candidate, database));
            var snapshot = await SnapshotApi.CreateAsync(paths, instance, Guid.NewGuid(), previous, InstallerInventory.Stores(paths, ReplayProfile.InProcess), token)
                .ConfigureAwait(false);
            var manifest = await SnapshotApi.VerifyAsync(paths, instance, snapshot, token).ConfigureAwait(false);
            Assert.AreEqual(rehearsalPublisher ? 2 : 1, manifest.Databases.Count(static store => store.Present));
            if (environmentDatabase is not null)
            {
                using var environment = new SqliteEnvironmentalObservationStore();
                var refused = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                    await environment.InitializeAsync(raw, token).ConfigureAwait(false)).ConfigureAwait(false);
                StringAssert.Contains(refused.Message, "state-disposition", StringComparison.Ordinal);
            }
            using (var processing = new SqliteCaptureProcessingStore(options))
                await processing.InitializeAsync(token).ConfigureAwait(false);
            Assert.AreEqual("12", CameraAgentStateStoreHarness.ReadSchema(database)[version]);
            using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
            {
                await connection.OpenAsync(token).ConfigureAwait(false);
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE processing_nodes SET completed_unix_ms = 13 WHERE node_id = 'checkpoint-probe';";
                Assert.AreEqual(1, await update.ExecuteNonQueryAsync(token).ConfigureAwait(false));
            }
            SqliteConnection.ClearAllPools();
            var plan = await SnapshotApi.PlanRestoreAsync(paths, instance, snapshot, [], token).ConfigureAwait(false);
            var remaining = await SnapshotApi.RestoreAsync(paths, instance, snapshot, InstallerNative.getuid(), InstallerNative.getgid(),
                [], plan, token).ConfigureAwait(false);
            Assert.IsEmpty(remaining);
            CollectionAssert.AreEqual(before.ToArray(), CameraAgentStateStoreHarness.ReadSchema(database).ToArray());
            if (environmentDatabase is not null)
            {
                using var restoredEnvironment = new SqliteConnection($"Data Source={environmentDatabase};Mode=ReadOnly;Pooling=False");
                await restoredEnvironment.OpenAsync(token).ConfigureAwait(false);
                Assert.AreEqual(5L, await ScalarAsync(restoredEnvironment, "PRAGMA user_version;").ConfigureAwait(false));
                Assert.AreEqual(0L, await ScalarAsync(restoredEnvironment,
                    "SELECT COUNT(*) FROM pragma_table_info('environmental_location_held_polls') WHERE name='settled';").ConfigureAwait(false));
                Assert.AreEqual(1234L, await ScalarAsync(restoredEnvironment,
                    "SELECT due_utc_ticks FROM environmental_location_held_polls WHERE source_id='sqm';").ConfigureAwait(false));
            }
            using var restored = new SqliteConnection($"Data Source={database};Mode=ReadOnly;Pooling=False");
            await restored.OpenAsync(token).ConfigureAwait(false);
            Assert.AreEqual(12L, await ScalarAsync(restored, "SELECT completed_unix_ms FROM processing_nodes WHERE node_id = 'checkpoint-probe';")
                .ConfigureAwait(false));
            using var assignment = restored.CreateCommand();
            assignment.CommandText = """
                SELECT capture_sequence FROM raw_capture_assignments
                WHERE capture_id = $capture AND raw_artifact_id = $artifact AND agent_id = $agent;
                """;
            assignment.Parameters.AddWithValue("$capture", capture.CaptureId.ToString("N"));
            assignment.Parameters.AddWithValue("$artifact", capture.ArtifactId.ToString("N"));
            assignment.Parameters.AddWithValue("$agent", capture.AgentId);
            Assert.AreEqual(capture.CaptureSequence, await assignment.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> SeedRehearsalEnvironmentAsync(string raw, CancellationToken token)
    {
        using (var environment = new SqliteEnvironmentalObservationStore())
            await environment.InitializeAsync(raw, token).ConfigureAwait(false);

        var database = Path.Combine(raw, ".environment", "environmental-observations.db");
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        await connection.OpenAsync(token).ConfigureAwait(false);
        using var seed = connection.CreateCommand();
        seed.CommandText = """
            ALTER TABLE environmental_location_held_polls DROP COLUMN settled;
            INSERT INTO environmental_source_runtime(source_id, observation_kind, required, consecutive_failures)
                VALUES('sqm', 'SkyQuality', 1, 0);
            INSERT INTO environmental_location_held_polls VALUES('sqm', 'SkyQualityMeter', 0, 1200, 1234);
            """;
        await seed.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        return database;
    }

}
