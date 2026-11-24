using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    private const string OrphanDamageSql = """
        PRAGMA foreign_keys = OFF;
        INSERT INTO processing_graph_delivery_facts(
            fact_id, proposal_id, fact_kind, occurred_unix_ms, delivery_state, attempt_count, next_attempt_unix_ms)
        VALUES('11111111111111111111111111111111', '22222222222222222222222222222222', 'Accepted', 0, 'Pending', 0, 0);
        """;
    private const string IntegrityDamageSql = """
        PRAGMA ignore_check_constraints = ON;
        UPDATE processing_nodes SET required = 2 WHERE node_id = 'schema-test';
        """;
    private static readonly string[] RestartScanOnly = ["restart"];
    private static readonly string[] RestartValidationOrder = ["restart/writable-open", "restart", "restart/foreign-keys"];
    private static readonly string[] InspectionAndWriteScans = ["inspection", "schema"];
    private static readonly string[] RestartAndAdoptionScans = ["restart", "schema"];

    [TestMethod]
    public async Task FreshDatabaseScansBeforeAndAfterCreatingProcessingSchema()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var creation = new ValidationRecorder();

        using (var created = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: creation))
        {
            await created.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        CollectionAssert.AreEqual(InspectionAndWriteScans, creation.Scopes.ToArray());
        creation.AssertWriteScanInsideTransaction();
    }

    [TestMethod]
    public async Task CurrentSchemaRestartScansWholeDatabaseOnce()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var recorder = new ValidationRecorder();

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: recorder))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        var validationHooks = recorder.ValidationHooks.ToArray();
        CollectionAssert.AreEqual(RestartValidationOrder, validationHooks, $"Observed validation hooks {string.Join(", ", validationHooks)}.");
    }

    [TestMethod]
    public async Task CurrentSchemaRestartCheckpointsRetainedWalOnLastClose()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        // Close all fixture handles before writing a marker that only the restart's handle can checkpoint.
        SqliteConnection.ClearAllPools();
        using (var writer = await fixture.OpenAsync().ConfigureAwait(false))
        {
            RetainWalOnClose(writer);
            await ExecuteTestSqlAsync(writer, "PRAGMA application_id = 2005;", TestContext.CancellationToken).ConfigureAwait(false);
        }
        var filesBeforeRestart = ReadDatabaseFiles(fixture.DatabasePath);
        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options))
        {
            await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }

        AssertRetainedWalUnchanged(fixture.DatabasePath, filesBeforeRestart);
        SqliteConnection.ClearAllPools();
        Assert.IsFalse(File.Exists(fixture.DatabasePath + "-wal"), "The last close must checkpoint the retained WAL.");

        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual(2005L, await ScalarAsync(verify, "PRAGMA application_id;").ConfigureAwait(false));
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
    public async Task CurrentSchemaForeignKeyViolationBlocksRestartWithoutMutation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using (var writer = await fixture.OpenAsync().ConfigureAwait(false))
        {
            await ExecuteTestSqlAsync(writer, OrphanDamageSql, TestContext.CancellationToken).ConfigureAwait(false);
            Assert.AreEqual(1L, await ScalarAsync(writer, "SELECT COUNT(*) FROM pragma_foreign_key_check;").ConfigureAwait(false));
            // Put the preexisting violation in the main file before taking the byte-comparison baseline.
            Assert.AreEqual(0L, await ScalarAsync(writer, "PRAGMA wal_checkpoint(TRUNCATE);").ConfigureAwait(false));
        }

        await AssertRestartRejectedWithoutMutationAsync(fixture, "Capture processing SQLite foreign-key validation failed.")
            .ConfigureAwait(false);
    }

    [TestMethod]
    public async Task ForeignKeyDamageCommittedAfterInspectionBlocksRestart()
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
    [DataRow(OrphanDamageSql, "Capture processing SQLite foreign-key validation failed.", DisplayName = "Writable-open foreign-key damage")]
    [DataRow(IntegrityDamageSql, "Capture processing SQLite integrity check failed.", DisplayName = "Writable-open integrity damage")]
    public async Task DamageCommittedAsWritableHandleOpensBlocksRestart(string damageSql, string expectedMessage)
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        // Close the fixture's pooled handles first: the last physical close checkpoints the journal.
        SqliteConnection.ClearAllPools();
        // A separate writer commits after schema inspection and before the writable handle's first read.
        var injector = new CommitDamageAtScan(fixture.DatabasePath, "restart/writable-open", TestContext.CancellationToken, damageSql, retainWal: true);

        using (var restarted = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: injector))
        {
            var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(expectedMessage, failure.Message);
        }
        Assert.AreEqual("restart/writable-open", injector.Scope);

        SqliteConnection.ClearAllPools();
        Assert.IsNotNull(injector.FilesAfterCommit);
        AssertRetainedWalUnchanged(fixture.DatabasePath, injector.FilesAfterCommit);
    }

    [TestMethod]
    public async Task RejectedRestartKeepsRetainedWalAndSameStoreCheckpointsAfterRepair()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        // Close the fixture's pooled handles first: the last physical close checkpoints the journal.
        SqliteConnection.ClearAllPools();
        using (var writer = await fixture.OpenAsync().ConfigureAwait(false))
        {
            RetainWalOnClose(writer);
            await ExecuteTestSqlAsync(writer, OrphanDamageSql, TestContext.CancellationToken).ConfigureAwait(false);
        }
        var filesBefore = ReadDatabaseFiles(fixture.DatabasePath);

        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual("Capture processing SQLite foreign-key validation failed.", failure.Message);
        // Compare only after every physical connection has closed, when a writable handle would have checkpointed.
        SqliteConnection.ClearAllPools();
        AssertRetainedWalUnchanged(fixture.DatabasePath, filesBefore);

        using (var repair = await fixture.OpenAsync().ConfigureAwait(false))
        {
            RetainWalOnClose(repair);
            await ExecuteTestSqlAsync(repair, "DELETE FROM processing_graph_delivery_facts;", TestContext.CancellationToken).ConfigureAwait(false);
        }
        var filesAfterRepair = ReadDatabaseFiles(fixture.DatabasePath);
        await store.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        AssertRetainedWalUnchanged(fixture.DatabasePath, filesAfterRepair);
        SqliteConnection.ClearAllPools();
        Assert.IsFalse(File.Exists(fixture.DatabasePath + "-wal"), "The retry must restore checkpoint-on-close.");
    }

    [TestMethod]
    public async Task IntegrityScanSqliteFailureMapsInvalidDataAndAllowsRetry()
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
    public async Task CancelledIntegrityScanSqliteFailureSurfacesAsCancellation()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);

        using (var cancelled = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: new ThrowSqliteAtScan("restart", cancellation)))
        {
            var failure = await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
                await cancelled.InitializeAsync(cancellation.Token).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(cancellation.Token, failure.CancellationToken);
            Assert.IsInstanceOfType<SqliteException>(failure.InnerException);
        }

        using var retried = new SqliteCaptureProcessingStore(fixture.Options);
        await retried.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task WritableForeignKeyScanSqliteFailureFailsClosedAndSameStoreRetriesAfterRepair()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        // The referenced column is non-unique, so SQLite cannot execute foreign_key_check on this schema.
        var injector = new CommitDamageAtScan(fixture.DatabasePath, "restart/foreign-keys", TestContext.CancellationToken,
            "CREATE TABLE startup_invalid_fk (node_id TEXT REFERENCES processing_nodes (node_id));", retainWal: true);

        using var store = new SqliteCaptureProcessingStore(fixture.Options, faultInjector: injector);
        var failure = await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
            await store.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        Assert.AreEqual("Capture processing SQLite whole-database validation failed.", failure.Message);
        Assert.IsInstanceOfType<SqliteException>(failure.InnerException);
        StringAssert.Contains(failure.InnerException.Message, "foreign key mismatch", StringComparison.Ordinal);
        Assert.IsNotNull(injector.FilesAfterCommit);
        AssertRetainedWalUnchanged(fixture.DatabasePath, injector.FilesAfterCommit);

        // Repair on a separate connection so the same store can retry with its pooled writable handle.
        using (var repair = await fixture.OpenAsync().ConfigureAwait(false))
        {
            RetainWalOnClose(repair);
            await ExecuteTestSqlAsync(repair, "PRAGMA foreign_keys = OFF; DROP TABLE startup_invalid_fk;", TestContext.CancellationToken)
                .ConfigureAwait(false);
        }
        await store.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        Assert.IsFalse(File.Exists(fixture.DatabasePath + "-wal"), "The repaired retry must checkpoint on the last close.");
    }

    [TestMethod]
    public async Task ReadOnlyInspectionRejectsSymlinkBeforeOpeningDatabase()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This case uses a Unix symbolic-link fixture.");
        }
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        SqliteConnection.ClearAllPools();
        File.Delete(fixture.DatabasePath);
        var missingTarget = fixture.DatabasePath + ".missing";
        File.CreateSymbolicLink(fixture.DatabasePath, missingTarget);

        // Opening the dangling link would raise a SQLite error. Call the opener directly to verify its pre-open guard.
        using var store = new SqliteCaptureProcessingStore(fixture.Options);
        var failure = await Assert.ThrowsExactlyAsync<IOException>(async () =>
        {
            using var connection = await store.OpenReadOnlyInspectionConnectionForTestsAsync(TestContext.CancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Assert.IsTrue(FileSystemFaultCompatibility.IsContainmentFailure(failure));
        Assert.IsFalse(File.Exists(missingTarget));
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

    private static void AssertRetainedWalUnchanged(string databasePath, Dictionary<string, byte[]> before)
    {
        const string wal = "raw-ingress.db-wal";
        Assert.IsTrue(before.TryGetValue(wal, out var retained) && retained.Length > 0, "The fixture must contain a retained WAL.");
        var after = ReadDatabaseFiles(databasePath);
        CollectionAssert.AreEqual(before["raw-ingress.db"], after["raw-ingress.db"], "raw-ingress.db");
        Assert.IsTrue(after.ContainsKey(wal), "The retained WAL was removed.");
        CollectionAssert.AreEqual(retained, after[wal], wal);
    }

    private static void RetainWalOnClose(SqliteConnection writer)
    {
        const int sqliteDbConfigNoCheckpointOnClose = 1006;
        Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_db_config(writer.Handle, sqliteDbConfigNoCheckpointOnClose, 1, out var disabled));
        Assert.AreEqual(1, disabled);
        using var command = writer.CreateCommand();
        command.CommandText = "PRAGMA wal_autocheckpoint = 0;";
        command.ExecuteNonQuery();
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers supply fixed SQL for disposable database fixtures.")]
    private static async Task ExecuteTestSqlAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
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

    private sealed class CommitDamageAtScan(
        string databasePath, string scope, CancellationToken cancellationToken, string sql = OrphanDamageSql, bool retainWal = false)
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
                    RetainWalOnClose(writer);
                ExecuteTestSqlAsync(writer, sql, cancellationToken).GetAwaiter().GetResult();
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
