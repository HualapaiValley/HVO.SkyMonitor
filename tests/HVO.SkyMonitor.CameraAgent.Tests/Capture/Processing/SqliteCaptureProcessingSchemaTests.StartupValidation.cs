using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    // integrity_check reads and verifies every page of the shared journal; on a 30-day retained history it
    // dominated the processing-store startup that delays the first headline (#1914). A current-schema restart
    // runs the whole-database checks once, on the writable connection, before anything can write.
    private static readonly string[] WritableScanOnly = ["writable"];
    private static readonly string[] InspectionAndWriteScans = ["inspection", "schema"];
    private static readonly string[] WritableAndAdoptionScans = ["writable", "schema"];

    [TestMethod]
    public async Task CurrentSchemaRestartScansWholeDatabaseOnce()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var recorder = new ValidationRecorder();

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(WritableScanOnly, recorder.Scopes.ToArray());
    }

    [TestMethod]
    public async Task FreshDatabaseScansBeforeAndAfterCreatingProcessingSchema()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var recorder = new ValidationRecorder();

        using (var created = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await created.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(InspectionAndWriteScans, recorder.Scopes.ToArray());
        recorder.AssertWriteScanInsideTransaction();
    }

    [TestMethod]
    public async Task EarlierCenteredSchemaAdoptionScansAgainInsideItsTransaction()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP INDEX ix_processing_outputs_astrometry_lookup;";
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }
        var recorder = new ValidationRecorder();

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        // The writable scan precedes the adoption's first write; the adoption scan sits inside its transaction.
        CollectionAssert.AreEqual(WritableAndAdoptionScans, recorder.Scopes.ToArray());
        recorder.AssertWriteScanInsideTransaction();
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarAsync(verify,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_processing_outputs_astrometry_lookup';").ConfigureAwait(false));
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Executes the fixed legacy schema 9 test constant.")]
    public async Task Schema9MigrationScansBeforeWritingAndInsideItsTransaction()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema9SqlForTests;
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }
        var recorder = new ValidationRecorder();

        using (var migrated = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await migrated.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(InspectionAndWriteScans, recorder.Scopes.ToArray());
        recorder.AssertWriteScanInsideTransaction();
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion, await ScalarAsync(verify,
            "SELECT version FROM capture_processing_schema WHERE schema_key = 1;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CurrentSchemaForeignKeyViolationBlocksRestartWithoutMutation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        {
            await CommitOrphanFactAsync(connection, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
        }

        await AssertRestartRejectedWithoutMutationAsync(fixture, "Capture processing SQLite foreign-key validation failed.")
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CurrentSchemaIndexPageDamageBlocksRestartWithoutMutation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        {
            // Swap the b-tree roots of the two populated processing_nodes indexes. The schema text is unchanged,
            // so only integrity_check can see that each index now holds the other index's entries.
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    CREATE TEMP TABLE swapped_roots AS
                        SELECT name, rootpage FROM sqlite_schema
                        WHERE name IN ('ix_processing_nodes_status', 'ix_processing_nodes_recipe');
                    PRAGMA writable_schema = ON;
                    UPDATE sqlite_schema
                    SET rootpage = (SELECT other.rootpage FROM temp.swapped_roots other WHERE other.name <> sqlite_schema.name)
                    WHERE name IN ('ix_processing_nodes_status', 'ix_processing_nodes_recipe');
                    PRAGMA writable_schema = OFF;
                    """;
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
            }
        }
        SqliteConnection.ClearAllPools();
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            Assert.AreNotEqual("ok", await command.ExecuteScalarAsync(TestContext.CancellationToken).ConfigureAwait(false));
        }

        await AssertRestartRejectedWithoutMutationAsync(fixture, "Capture processing SQLite integrity check failed.")
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task DamageCommittedAfterInspectionSnapshotBlocksRestart()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        // Another writer commits the orphan when the first whole-database scan is about to run, which is after
        // inspection has taken (and, on this path, released) its read snapshot. The writable view sees it.
        var injector = new CommitDamageAtFirstScan(fixture.DatabasePath, TestContext.CancellationToken);

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: injector))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual("Capture processing SQLite foreign-key validation failed.", failure.Message);
        }

        Assert.AreEqual("writable", injector.Scope);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
    }

    private async Task AssertRestartRejectedWithoutMutationAsync(SchemaFixture fixture, string message)
    {
        var filesBefore = ReadDatabaseFiles(fixture.DatabasePath);
        var recorder = new ValidationRecorder();
        using (var rejected = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await rejected.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(message, failure.Message);
        }

        CollectionAssert.AreEqual(WritableScanOnly, recorder.Scopes.ToArray());
        // Nothing was written: the database and any existing WAL are byte-identical, and a WAL that the rejected
        // startup's connections opened holds no frames. Shared memory carries only transient read marks.
        var filesAfter = ReadDatabaseFiles(fixture.DatabasePath);
        foreach (var file in filesBefore.Where(static file => !file.Key.EndsWith("-shm", StringComparison.Ordinal)))
            CollectionAssert.AreEqual(file.Value, filesAfter[file.Key], file.Key);
        foreach (var file in filesAfter.Where(file => !filesBefore.ContainsKey(file.Key) && !file.Key.EndsWith("-shm", StringComparison.Ordinal)))
            Assert.IsEmpty(file.Value, file.Key);
    }

    private static async Task CommitOrphanFactAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        // Disable enforcement on this setup connection only, so the orphan commits as external damage would.
        command.CommandText = """
            PRAGMA foreign_keys = OFF;
            INSERT INTO processing_graph_delivery_facts(
                fact_id, proposal_id, fact_kind, occurred_unix_ms, delivery_state, attempt_count, next_attempt_unix_ms)
            VALUES('11111111111111111111111111111111', '22222222222222222222222222222222', 'Accepted', 0, 'Pending', 0, 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class ValidationRecorder : ICaptureProcessingFaultInjector
    {
        private readonly ConcurrentQueue<(CaptureProcessingFaultPoint Point, string Scope)> events = new();

        internal IEnumerable<string> Scopes => events
            .Where(static item => item.Point == CaptureProcessingFaultPoint.BeforeDatabaseValidation)
            .Select(static item => item.Scope);

        public void Inject(CaptureProcessingFaultPoint point, string nodeId) => events.Enqueue((point, nodeId));

        /// <summary>The write-transaction scan runs after the transaction began and before it commits.</summary>
        internal void AssertWriteScanInsideTransaction()
        {
            var sequence = events.ToArray();
            var began = Array.FindIndex(sequence, static item => item.Point == CaptureProcessingFaultPoint.AfterSchemaTransactionBegan);
            var scan = Array.FindIndex(sequence, static item =>
                item.Point == CaptureProcessingFaultPoint.BeforeDatabaseValidation && item.Scope == "schema");
            var commit = Array.FindIndex(sequence, static item => item.Point == CaptureProcessingFaultPoint.BeforeSchemaCommit);
            Assert.IsTrue(began >= 0 && began < scan && scan < commit,
                $"Expected began < scan < commit, observed {began}, {scan}, {commit}.");
        }
    }

    private sealed class CommitDamageAtFirstScan(string databasePath, CancellationToken cancellationToken) : ICaptureProcessingFaultInjector
    {
        internal string? Scope { get; private set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point != CaptureProcessingFaultPoint.BeforeDatabaseValidation || Scope is not null)
                return;
            Scope = nodeId;
            using var writer = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            writer.Open();
            CommitOrphanFactAsync(writer, cancellationToken).GetAwaiter().GetResult();
        }
    }
}
