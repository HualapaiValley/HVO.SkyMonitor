using System.Diagnostics.CodeAnalysis;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class SqliteCaptureProcessingSchemaTests
{
    [TestMethod]
    public async Task FreshSharedDatabaseCreatesCanonicalSchema7AndRestarts()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);

        using (var store = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }
        using (var writer = await fixture.OpenAsync().ConfigureAwait(false))
        {
            const int sqliteDbConfigNoCheckpointOnClose = 1006;
            var result = SQLitePCL.raw.sqlite3_db_config(
                writer.Handle,
                sqliteDbConfigNoCheckpointOnClose,
                1,
                out var checkpointDisabled);
            Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, result);
            Assert.AreEqual(1, checkpointDisabled);
            using var seed = writer.CreateCommand();
            seed.CommandText = """
                    PRAGMA wal_autocheckpoint = 0;
                    INSERT INTO processing_nodes(
                        capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                        output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                    VALUES(
                        '10000000000000000000000000000001', 'retained-wal', 1, '[]', 'schema-test',
                        'Preview', 'default', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                        'Completed', NULL, 1, 0);
                    """;
            await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        Assert.IsTrue(File.Exists($"{fixture.DatabasePath}-wal"));
        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(7L, await ScalarAsync(
            connection,
            "SELECT version FROM capture_processing_schema WHERE schema_key = 1;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(
            connection,
            "SELECT COUNT(*) FROM processing_nodes WHERE node_id = 'retained-wal';").ConfigureAwait(false));
        Assert.AreEqual(46L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name = 'capture_processing_schema'
               OR name LIKE 'processing_%'
               OR name LIKE 'ix_processing_%';
            """).ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM pragma_table_info('processing_outputs')
            WHERE name = 'legacy_recipe_version';
            """).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM pragma_table_info('processing_outputs')
            WHERE name = 'frame_artifact_recipe_version';
            """).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name = 'processing_executions';
            """).ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection, """
            SELECT COUNT(*) FROM pragma_index_list('processing_executions')
            WHERE name = 'ix_processing_executions_live_capture' AND [unique] = 1 AND partial = 1;
            """).ConfigureAwait(false));
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    [DataRow(8)]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Schema versions are fixed integer test data rows.")]
    public async Task UnsupportedPopulatedSchemaIsRejectedWithoutMutation(int version)
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = version == 0
                ? "DROP TABLE capture_processing_schema;"
                : $"""
                    DROP TABLE capture_processing_schema;
                    CREATE TABLE capture_processing_schema(
                        schema_key INTEGER PRIMARY KEY CHECK(schema_key = 1),
                        version INTEGER NOT NULL CHECK(version = {version})
                    ) STRICT;
                    INSERT INTO capture_processing_schema(schema_key, version) VALUES(1, {version});
                    """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        var filesBefore = ReadDatabaseFiles(fixture.DatabasePath);
        Assert.IsTrue(filesBefore.ContainsKey("raw-ingress.db-wal"));

        using var rejected = new SqliteCaptureProcessingStore(fixture.Options);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
            await rejected.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        var filesAfter = ReadDatabaseFiles(fixture.DatabasePath);
        CollectionAssert.AreEquivalent(filesBefore.Keys, filesAfter.Keys);
        foreach (var file in filesBefore)
        {
            if (file.Key.EndsWith("-shm", StringComparison.Ordinal))
            {
                // Read-only WAL readers update transient read marks in shared memory.
                continue;
            }
            CollectionAssert.AreEqual(file.Value, filesAfter[file.Key], file.Key);
        }

        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarAsync(
            verify, "SELECT COUNT(*) FROM processing_nodes;").ConfigureAwait(false));
        Assert.AreEqual(version == 0 ? 0L : 1L, await ScalarAsync(
            verify, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'capture_processing_schema';").ConfigureAwait(false));
        if (version != 0)
        {
            Assert.AreEqual((long)version, await ScalarAsync(
                verify, "SELECT version FROM capture_processing_schema WHERE schema_key = 1;").ConfigureAwait(false));
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only the canonical internal schema test fixture is executed.")]
    public async Task CanonicalSchema5IsMigratedWithoutLosingProcessingHistory()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema5SqlForTests + """
                INSERT INTO processing_nodes(
                    capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                    output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                VALUES(
                    '10000000000000000000000000000001', 'migrated-node', 1, '[]', 'schema-test',
                    'Preview', 'default', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                    'Completed', NULL, 1, 0);
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (var store = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(7L, await ScalarAsync(
            verify, "SELECT version FROM capture_processing_schema WHERE schema_key = 1;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(
            verify, "SELECT COUNT(*) FROM processing_nodes WHERE node_id = 'migrated-node';").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(
            verify, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'processing_executions';").ConfigureAwait(false));
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The query combines fixed test schema and insert SQL only.")]
    public async Task CanonicalSchema6GraphIdentitySurvivesSchema7Migration()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var definition = new ProcessingGraphDefinition(
            ProcessingGraphSchemaVersions.V1,
            "schema-six",
            "1",
            [new ProcessingGraphSourceDefinition("$raw",
                [new ProcessingGraphProductContract(
                    FrameArtifactRole.Raw, "source", ProcessingProductKind.PixelData)])],
            []);
        var plan = ProcessingGraphCompiler.Compile(definition).Plan!;
        var definitionJson = ProcessingGraphJson.SerializeCanonical(definition);
        Assert.DoesNotContain("\"mediaType\"", Encoding.UTF8.GetString(definitionJson), StringComparison.Ordinal);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema6SqlForTests + """
                INSERT INTO processing_graph_revisions(
                    revision_id, graph_name, revision_name, lifecycle,
                    definition_identity_sha256, shared_plan_identity_sha256, local_plan_identity_sha256,
                    pipeline_json, definition_json, frozen_plan_json, nodes_json, created_unix_ms,
                    validated_unix_ms, activated_unix_ms, retired_unix_ms)
                VALUES(
                    $revision, 'schema-six', '1', 'Active', $definition, $shared, $local,
                    x'7B7D', $json, x'7B7D', x'5B5D', 0, 0, 0, NULL);
                INSERT INTO processing_graph_registry_state(
                    state_key, selection_mode, active_revision_id, configured_basic_revision_id,
                    state_version, updated_unix_ms)
                VALUES(1, 'ConfiguredBasic', $revision, $revision, 1, 0);
                """;
            command.Parameters.AddWithValue("$revision", new string('A', 64));
            command.Parameters.AddWithValue("$definition", plan.DefinitionIdentitySha256);
            command.Parameters.AddWithValue("$shared", plan.PlanIdentitySha256);
            command.Parameters.AddWithValue("$local", new string('B', 64));
            command.Parameters.AddWithValue("$json", definitionJson);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using (var store = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        }

        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(7L, await ScalarAsync(
            verify, "SELECT version FROM capture_processing_schema WHERE schema_key = 1;").ConfigureAwait(false));
        using var read = verify.CreateCommand();
        read.CommandText = """
            SELECT definition_identity_sha256, definition_json
            FROM processing_graph_revisions
            WHERE revision_id = $revision;
            """;
        read.Parameters.AddWithValue("$revision", new string('A', 64));
        using var reader = await read.ExecuteReaderAsync().ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync().ConfigureAwait(false));
        Assert.AreEqual(plan.DefinitionIdentitySha256, reader.GetString(0));
        var parsed = ProcessingGraphJson.Parse((byte[])reader[1]);
        Assert.IsTrue(parsed.IsValid, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.AreEqual(plan.DefinitionIdentitySha256,
            ProcessingGraphCompiler.Compile(parsed.Definition!).Plan!.DefinitionIdentitySha256);
    }

    [TestMethod]
    public async Task MalformedCurrentSchemaIsRejectedWithoutMutation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        const string malformedDefinition = "CREATE TABLE capture_processing_schema(schema_key INTEGER PRIMARY KEY, version INTEGER NOT NULL) STRICT";
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                DROP TABLE capture_processing_schema;
                {malformedDefinition};
                INSERT INTO capture_processing_schema(schema_key, version) VALUES(1, 7);
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using var rejected = new SqliteCaptureProcessingStore(fixture.Options);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await rejected.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        using var definition = verify.CreateCommand();
        definition.CommandText = "SELECT sql FROM sqlite_schema WHERE name = 'capture_processing_schema';";
        Assert.AreEqual(malformedDefinition, await definition.ExecuteScalarAsync().ConfigureAwait(false));
    }

    [TestMethod]
    public async Task ArbitraryNamedTriggerOnProcessingTableIsRejected()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER rogue_output_trigger AFTER INSERT ON processing_outputs
                BEGIN
                    SELECT 1;
                END;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using var rejected = new SqliteCaptureProcessingStore(fixture.Options);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await rejected.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task RawVersionMarkerWithoutCanonicalRawSchemaIsRejectedWithoutProcessingMutation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-schema-invalid-raw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "journal"));
        var databasePath = Path.Combine(root, "journal", "raw-ingress.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                await connection.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version = 12;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            var options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });

            using var rejected = new SqliteCaptureProcessingStore(options);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await rejected.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);

            using var verify = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
            await verify.OpenAsync().ConfigureAwait(false);
            Assert.AreEqual(0L, await ScalarAsync(verify, """
                SELECT COUNT(*) FROM sqlite_schema
                WHERE name = 'capture_processing_schema'
                   OR name LIKE 'processing_%'
                   OR name LIKE 'ix_processing_%';
                """).ConfigureAwait(false));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow((int)CaptureProcessingFaultPoint.AfterSchemaTransactionBegan)]
    [DataRow((int)CaptureProcessingFaultPoint.BeforeSchemaCommit)]
    public async Task InterruptedCreationRollsBackAndRetryCreatesSchema7(int faultPoint)
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var fault = new ThrowOnceFaultInjector((CaptureProcessingFaultPoint)faultPoint);
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: fault))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        {
            Assert.AreEqual(0L, await ScalarAsync(connection, """
                SELECT COUNT(*) FROM sqlite_schema
                WHERE name = 'capture_processing_schema'
                   OR name LIKE 'processing_%'
                   OR name LIKE 'ix_processing_%';
                """).ConfigureAwait(false));
        }

        using var retried = new SqliteCaptureProcessingStore(fixture.Options);
        await retried.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(7L, await ScalarAsync(
            verify, "SELECT version FROM capture_processing_schema WHERE schema_key = 1;").ConfigureAwait(false));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass fixed test SQL only.")]
    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync().ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, byte[]> ReadDatabaseFiles(string databasePath)
        => new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm", $"{databasePath}-journal" }
            .Where(File.Exists)
            .ToDictionary(static path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    private sealed class ThrowOnceFaultInjector(CaptureProcessingFaultPoint target) : ICaptureProcessingFaultInjector
    {
        private int _thrown;

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point == target && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new InjectedProcessingSchemaException();
            }
        }
    }

    [SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Private test-only fault marker.")]
    private sealed class InjectedProcessingSchemaException : Exception;

    private sealed class SchemaFixture : IDisposable
    {
        private SchemaFixture(string root, IOptions<CameraAgentHostOptions> options)
        {
            Root = root;
            Options = options;
            DatabasePath = Path.Combine(root, "journal", "raw-ingress.db");
        }

        private string Root { get; }

        internal string DatabasePath { get; }

        internal IOptions<CameraAgentHostOptions> Options { get; }

        internal static async Task<SchemaFixture> CreateAsync(bool initializeProcessing = false)
        {
            var root = Path.Combine(Path.GetTempPath(), $"hvo-processing-schema-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var options = Microsoft.Extensions.Options.Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressReserveBytes = 0,
                RawIngressSqliteBusyTimeoutSeconds = 1
            });
            var fixture = new SchemaFixture(root, options);
            var rawJournal = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
            await rawJournal.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            if (initializeProcessing)
            {
                using var store = new SqliteCaptureProcessingStore(options);
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                using var connection = await fixture.OpenAsync().ConfigureAwait(false);
                using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO processing_nodes(
                        capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                        output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
                    VALUES(
                        '10000000000000000000000000000001', 'schema-test', 1, '[]', 'schema-test',
                        'Preview', 'default', 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
                        'Completed', NULL, 1, 0);
                    """;
                await seed.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            return fixture;
        }

        internal async Task<SqliteConnection> OpenAsync()
        {
            var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
