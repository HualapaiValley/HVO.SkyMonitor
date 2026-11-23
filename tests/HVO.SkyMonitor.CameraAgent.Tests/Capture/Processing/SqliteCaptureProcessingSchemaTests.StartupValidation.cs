using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    // integrity_check reads and verifies every page of the shared journal; on a 30-day retained history it
    // dominated the processing-store startup that delays the first headline (#1914). A current-schema restart
    // checks the whole database once without reusing inspection's snapshot: integrity_check on a read-only
    // connection that never checkpoints, then foreign_key_check as the writable handle's first statement, with
    // checkpoint-on-close disabled on that handle until initialization succeeds.
    private static readonly string[] RestartScanOnly = ["restart"];
    private static readonly string[] RestartValidationOrder = ["restart", "restart/writable-open", "restart/foreign-keys"];
    private static readonly string[] InspectionAndWriteScans = ["inspection", "schema"];
    private static readonly string[] RestartAndAdoptionScans = ["restart", "schema"];

    [TestMethod]
    public async Task CurrentSchemaRestartScansWholeDatabaseOnce()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var recorder = new ValidationRecorder();

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(RestartScanOnly, recorder.Scopes.ToArray());
        // One integrity_check and one foreign_key_check, the latter only after the writable handle opened.
        CollectionAssert.AreEqual(RestartValidationOrder, recorder.ValidationHooks.ToArray());
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
            command.CommandText = DropCenteredRegistrationStateSql + "\nDROP INDEX ix_processing_outputs_astrometry_lookup;";
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }
        var recorder = new ValidationRecorder();

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        // The restart scan precedes the adoption's first write; the adoption scan sits inside its transaction.
        CollectionAssert.AreEqual(RestartAndAdoptionScans, recorder.Scopes.ToArray());
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
        // inspection has taken (and, on this path, released) its read snapshot. The restart scan sees it.
        var injector = new CommitDamageAtScan(fixture.DatabasePath, "restart", TestContext.CancellationToken);

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: injector))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual("Capture processing SQLite foreign-key validation failed.", failure.Message);
        }

        Assert.AreEqual("restart", injector.Scope);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task DamageCommittedBetweenIntegrityAndForeignKeyChecksBlocksRestart()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        // integrity_check has already read the database when the orphan commits; foreign_key_check must still see it,
        // as the separate pre-#1914 writable checks did, rather than reuse a snapshot pinned by the first check.
        var injector = new CommitDamageAtScan(fixture.DatabasePath, "restart/foreign-keys", TestContext.CancellationToken);

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: injector))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual("Capture processing SQLite foreign-key validation failed.", failure.Message);
        }

        Assert.AreEqual("restart/foreign-keys", injector.Scope);
    }

    [TestMethod]
    public async Task DamageCommittedAsWritableHandleOpensBlocksRestart()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        // Close the fixture's pooled handles first: the last physical close checkpoints the journal.
        SqliteConnection.ClearAllPools();
        // Both read-only checks of inspection and integrity_check have finished; a second connection commits the
        // orphan as the writable handle opens, before that handle's first read.
        var injector = new CommitDamageAtScan(fixture.DatabasePath, "restart/writable-open", TestContext.CancellationToken, retainWal: true);

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: injector))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual("Capture processing SQLite foreign-key validation failed.", failure.Message);
        }
        Assert.AreEqual("restart/writable-open", injector.Scope);

        // The rejected writable handle must not checkpoint the orphan out of the WAL when it finally closes.
        SqliteConnection.ClearAllPools();
        var filesAfter = ReadDatabaseFiles(fixture.DatabasePath);
        Assert.IsNotNull(injector.FilesAfterCommit);
        Assert.IsTrue(injector.FilesAfterCommit.TryGetValue("raw-ingress.db-wal", out var retained) && retained.Length > 0,
            "The orphan must still be in the retained WAL when the writable handle reads.");
        CollectionAssert.AreEqual(injector.FilesAfterCommit["raw-ingress.db"], filesAfter["raw-ingress.db"], "raw-ingress.db");
        Assert.IsTrue(filesAfter.ContainsKey("raw-ingress.db-wal"), "The rejected restart removed the retained WAL.");
        CollectionAssert.AreEqual(injector.FilesAfterCommit["raw-ingress.db-wal"], filesAfter["raw-ingress.db-wal"], "raw-ingress.db-wal");
    }

    [TestMethod]
    public async Task RejectedRestartKeepsRetainedWalAfterConnectionsClose()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        // Close the fixture's pooled handles first: the last physical close checkpoints the journal.
        SqliteConnection.ClearAllPools();
        using (var writer = await fixture.OpenAsync().ConfigureAwait(false))
        {
            const int sqliteDbConfigNoCheckpointOnClose = 1006;
            Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_config(writer.Handle, sqliteDbConfigNoCheckpointOnClose, 1, out var disabled));
            Assert.AreEqual(1, disabled);
            using (var command = writer.CreateCommand())
            {
                command.CommandText = "PRAGMA wal_autocheckpoint = 0;";
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
            }
            await CommitOrphanFactAsync(writer, TestContext.CancellationToken).ConfigureAwait(false);
        }
        var filesBefore = ReadDatabaseFiles(fixture.DatabasePath);
        Assert.IsTrue(filesBefore.TryGetValue("raw-ingress.db-wal", out var retained) && retained.Length > 0,
            "The orphan must still be in the retained WAL.");

        using (var rejected = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await rejected.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        // Compare only after every physical connection has closed, when a writable handle would have checkpointed.
        SqliteConnection.ClearAllPools();

        var filesAfter = ReadDatabaseFiles(fixture.DatabasePath);
        CollectionAssert.AreEqual(filesBefore["raw-ingress.db"], filesAfter["raw-ingress.db"], "raw-ingress.db");
        Assert.IsTrue(filesAfter.ContainsKey("raw-ingress.db-wal"), "The rejected restart removed the retained WAL.");
        CollectionAssert.AreEqual(filesBefore["raw-ingress.db-wal"], filesAfter["raw-ingress.db-wal"], "raw-ingress.db-wal");
    }

    [TestMethod]
    public async Task ScanSqliteFailureFailsClosedAsInvalidDataAndRetries()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);

        using (var failing = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: new ThrowSqliteAtScan("restart")))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await failing.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual("Capture processing SQLite whole-database validation failed.", failure.Message);
            Assert.IsInstanceOfType<SqliteException>(failure.InnerException);
        }

        using var retried = new SqliteCaptureProcessingStore(fixture.Options);
        await retried.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CancelledScanSqliteFailureSurfacesAsCancellation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        using (var cancelled = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: new ThrowSqliteAtScan("restart", cancellation)))
        {
            var failure = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await cancelled.InitializeAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsInstanceOfType<SqliteException>(failure.InnerException);
        }

        using var retried = new SqliteCaptureProcessingStore(fixture.Options);
        await retried.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }

    private async Task AssertRestartRejectedWithoutMutationAsync(SchemaFixture fixture, string message)
    {
        // Close the fixture's own pooled handles first; their last physical close checkpoints the journal.
        SqliteConnection.ClearAllPools();
        var filesBefore = ReadDatabaseFiles(fixture.DatabasePath);
        var recorder = new ValidationRecorder();
        using (var rejected = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await rejected.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(message, failure.Message);
        }

        CollectionAssert.AreEqual(RestartScanOnly, recorder.Scopes.ToArray());
        // Nothing was written, checked after every physical connection closed: the database and any existing WAL are
        // byte-identical, and a WAL the rejected startup opened holds no frames. Shared memory holds only read marks.
        SqliteConnection.ClearAllPools();
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

        // One scan per entry; the hooks inside a scan (writable open, foreign-key check) are not separate scans.
        internal IEnumerable<string> Scopes => ValidationHooks.Where(static scope => !scope.Contains('/', StringComparison.Ordinal));

        internal IEnumerable<string> ValidationHooks => events
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

    private sealed class CommitDamageAtScan(string databasePath, string scope, CancellationToken cancellationToken, bool retainWal = false)
        : ICaptureProcessingFaultInjector
    {
        internal string? Scope { get; private set; }

        internal Dictionary<string, byte[]>? FilesAfterCommit { get; private set; }

        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point != CaptureProcessingFaultPoint.BeforeDatabaseValidation || nodeId != scope || Scope is not null)
                return;
            Scope = nodeId;
            using (var writer = new SqliteConnection($"Data Source={databasePath};Pooling=False"))
            {
                writer.Open();
                if (retainWal)
                {
                    // Keep the orphan in the WAL: the writable handle under test may not have attached to it yet.
                    const int sqliteDbConfigNoCheckpointOnClose = 1006;
                    Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_config(writer.Handle, sqliteDbConfigNoCheckpointOnClose, 1, out _));
                    using var command = writer.CreateCommand();
                    command.CommandText = "PRAGMA wal_autocheckpoint = 0;";
                    command.ExecuteNonQuery();
                }
                CommitOrphanFactAsync(writer, cancellationToken).GetAwaiter().GetResult();
            }
            FilesAfterCommit = ReadDatabaseFiles(databasePath);
        }
    }

    private sealed class ThrowSqliteAtScan(string scope, CancellationTokenSource? cancelFirst = null) : ICaptureProcessingFaultInjector
    {
        public void Inject(CaptureProcessingFaultPoint point, string nodeId)
        {
            if (point != CaptureProcessingFaultPoint.BeforeDatabaseValidation || nodeId != scope)
                return;
            cancelFirst?.Cancel();
            throw new SqliteException("Injected whole-database scan I/O failure.", 10);
        }
    }
}
