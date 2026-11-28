using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using static HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing.CenteredWindowSchedulerTests;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class SqliteCaptureProcessingSchemaTests
{
    internal const string DropCenteredRegistrationStateSql = """
        DROP TABLE processing_centered_registration_input_failures;
        DROP INDEX ix_processing_centered_windows_publication_retention;
        """;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The legacy test schema is a fixed store definition.")]
    public async Task CenteredWindowSchemaMigrationKeepsExistingProcessingHistory()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema9SqlForTests;
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await WritePinProbeAsync(connection, null, Guid.Empty, "historic-evidence", TestContext.CancellationToken).ConfigureAwait(false);
        }

        using (var processing = new SqliteCaptureProcessingStore(fixture.Options))
            await processing.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);

        var ingress = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
        await ingress.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
        await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion,
            await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_nodes WHERE node_id = 'historic-evidence';").ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_centered_windows;").ConfigureAwait(false));
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The legacy test schema is a fixed store definition.")]
    public async Task CenteredAvailabilityUpgradeReopensCanonicalEarlierSchema10()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SqliteCaptureProcessingStore.LegacySchema10BeforeAvailabilitySqlForTests;
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken).ConfigureAwait(false);
            await WritePinProbeAsync(connection, null, Guid.Empty, "historic-availability", TestContext.CancellationToken)
                .ConfigureAwait(false);
        }

        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options,
            faultInjector: new ThrowOnceFaultInjector(CaptureProcessingFaultPoint.BeforeSchemaCommit)))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
            Assert.AreEqual(0L, await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'processing_centered_output_availability';")
                .ConfigureAwait(false));
        using (var migrated = new SqliteCaptureProcessingStore(fixture.Options))
            await migrated.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var restarted = new SqliteCaptureProcessingStore(fixture.Options);
        await restarted.InitializeAsync(TestContext.CancellationToken).ConfigureAwait(false);
        using var verify = await fixture.OpenAsync().ConfigureAwait(false);
        Assert.AreEqual((long)SqliteCaptureProcessingStore.CurrentSchemaVersion,
            await ScalarAsync(verify, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_nodes WHERE node_id = 'historic-availability';")
            .ConfigureAwait(false));
        Assert.AreEqual(0L, await ScalarAsync(verify, "SELECT COUNT(*) FROM processing_centered_output_availability;").ConfigureAwait(false));
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The legacy test schema is fixed.")]
    public async Task CenteredOpenRunWithoutCalendarKeepsUnavailablePredecessorAfterSchema10Upgrade()
    {
        using var fixture = await SchemaFixture.CreateAsync().ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var camera = new CameraKey("legacy-agent", "legacy-camera");
        var run = Guid.NewGuid();
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = SqliteCaptureProcessingStore.LegacySchema10BeforeAvailabilitySqlForTests;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO centered_exposure_runs(run_id, agent_id, camera_id, snapshot_json, snapshot_sha256)
            VALUES ($run, 'legacy-agent', 'legacy-camera', X'7B7D', 'legacy');
            INSERT INTO centered_exposure_attempts(attempt_id, run_id, ordinal, status, reason)
            VALUES ($old, $run, 1, 'expired', 'target-progress-unknown');
            """;
        command.Parameters.AddWithValue("$run", run.ToString("N"));
        command.Parameters.AddWithValue("$old", Guid.NewGuid().ToString("N"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        using (var store = new SqliteCaptureProcessingStore(fixture.Options))
            await store.InitializeAsync(token).ConfigureAwait(false);

        for (var ordinal = 2; ordinal <= 3; ordinal++)
        {
            var current = new ExposureAttemptKey(camera, run, ordinal, Guid.NewGuid());
            var start = Start.AddSeconds(ordinal * 2);
            using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await CenteredWindowRuntimeSchedule.TrackDispatchedAsync(transaction, current,
                new(start, TimeSpan.FromSeconds(2), CaptureCadenceMode.MinimumStartInterval),
                TimeSpan.FromMilliseconds(250), start, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }

        command.CommandText = """
            SELECT json_extract(window.checkpoint_json, '$.projectedTimes[0].boundaryReason'),
                   json_extract(window.checkpoint_json, '$.projectedTimes[1].boundaryReason')
            FROM processing_centered_attempt_calendar calendar
            JOIN processing_centered_windows window ON window.window_sha256 = calendar.window_sha256
            WHERE calendar.run_id = $run ORDER BY calendar.slot;
            """;
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
        Assert.AreEqual("before-run-start", reader.GetString(0));
        Assert.AreEqual("calendar-predecessor-unavailable", reader.GetString(1));
        Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
        Assert.AreEqual("calendar-predecessor-unavailable", reader.GetString(0));
        Assert.IsTrue(await reader.IsDBNullAsync(1, token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredPublicationRetentionSeeksByDeadline()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            WITH RECURSIVE history(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM history WHERE n < 2003)
            INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                checkpoint_json, closure_json, publication_json, terminal)
            SELECT printf('%064x', n), $centered_ticks - 1, $centered_ticks + (n > 2000), X'7B7D', X'7B7D',
                CAST(json_object('registrationArtifactIds', json_array(printf('%032x', n))) AS BLOB), 1
            FROM history;
            INSERT INTO processing_centered_view_window_work(window_sha256, terminal_reason)
                VALUES(printf('%064x', 2002), 'not-publishable');
            INSERT INTO processing_centered_view_window_work(window_sha256, retry_utc)
                VALUES(printf('%064x', 2003), $centered_ticks);
            """;
        command.Parameters.AddWithValue("$centered_ticks", Start.UtcTicks);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteCaptureProcessingStore.CenteredPublicationRetentionSql;
        var plan = new List<string>();
        using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                plan.Add(reader.GetString(3));
        }
        Assert.IsTrue(plan.Any(step => step.Contains("ix_processing_centered_windows_publication_retention (registration_deadline_utc_ticks>?)",
            StringComparison.Ordinal)), string.Join(Environment.NewLine, plan));

        command.CommandText = SqliteCaptureProcessingStore.CenteredPublicationRetentionSql;
        var retained = new List<string>();
        using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                retained.Add(reader.GetString(0));
        }
        Assert.HasCount(2, retained);
        Assert.Contains("000000000000000000000000000007d1", retained);
        Assert.Contains("000000000000000000000000000007d3", retained);
        Assert.AreEqual(2003L, await ScalarAsync(connection, "SELECT COUNT(*) FROM processing_centered_windows;").ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredDueWindowPollingStaysBoundedAsRetainedHistoryGrows()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var clock = new CenteredTestClock { UtcNow = Start };
        using var store = new SqliteCaptureProcessingStore(fixture.Options, clock);
        var views = new SqliteCenteredViews(store, clock, NullLogger<SqliteCenteredViews>.Instance);
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);

        await SeedCompletedWindowsAsync(connection, 1, 1_000, Start.UtcTicks, token).ConfigureAwait(false);
        var plan = await ExplainDueWindowQueryPlanAsync(connection, Start.UtcTicks, 8, token).ConfigureAwait(false);
        Assert.IsTrue(plan.Any(step => step.Contains("ix_processing_centered_view_window_due", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, plan));
        Assert.IsFalse(plan.Any(step => step.Contains("ix_processing_centered_windows_reference", StringComparison.Ordinal) ||
            step.Contains("SCAN processing_centered_windows", StringComparison.Ordinal) ||
            step.Contains("SCAN window", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, plan));
        Assert.IsEmpty(await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false));
        var stepsAt1000 = MeasureDueWindowVmSteps(connection, Start.UtcTicks, 8);

        await SeedCompletedWindowsAsync(connection, 1_001, 10_000, Start.UtcTicks, token).ConfigureAwait(false);
        using (var future = connection.CreateCommand())
        {
            future.CommandText = """
                WITH RECURSIVE retry_rows(n) AS (VALUES(10001) UNION ALL SELECT n + 1 FROM retry_rows WHERE n < 10050)
                INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                    checkpoint_json, reference_capture_id, closure_json, terminal)
                SELECT printf('%064x', n), $deadline + n, $deadline + n + 1000, X'7B7D', printf('%032x', n), X'7B7D', 1
                FROM retry_rows;
                WITH RECURSIVE retry_rows(n) AS (VALUES(10001) UNION ALL SELECT n + 1 FROM retry_rows WHERE n < 10050)
                INSERT INTO processing_centered_view_window_work(window_sha256, retry_utc, retry_count)
                SELECT printf('%064x', n), $future_retry + n, 1
                FROM retry_rows;
                """;
            future.Parameters.AddWithValue("$deadline", Start.UtcTicks);
            future.Parameters.AddWithValue("$future_retry", Start.AddSeconds(15).UtcTicks);
            await future.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        Assert.IsEmpty(await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false));
        var stepsAt10000 = MeasureDueWindowVmSteps(connection, Start.UtcTicks, 8);
        TestContext.WriteLine($"Idle due-window poll VM steps: 1,000 completed={stepsAt1000}, 10,000 completed + 50 future retries={stepsAt10000}.");
        Assert.IsLessThan(200, stepsAt1000, $"Expected O(1) idle poll at 1,000 windows, observed {stepsAt1000} VM steps.");
        Assert.IsLessThan(200, stepsAt10000, $"Expected O(1) idle poll at 10,000 windows, observed {stepsAt10000} VM steps.");
        Assert.IsLessThanOrEqualTo(stepsAt1000 + 25, stepsAt10000,
            $"Idle poll VM steps grew with retained history: 1,000={stepsAt1000}, 10,000={stepsAt10000}.");

        // Enqueue 10 due windows (mix of newly terminal retry_utc=0 and due retries) out of deadline order.
        using (var dueCommand = connection.CreateCommand())
        {
            dueCommand.CommandText = """
                WITH RECURSIVE due_rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM due_rows WHERE n < 10)
                INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                    checkpoint_json, reference_capture_id, closure_json, terminal)
                SELECT printf('%064x', 20000 + n), $base_deadline + (11 - n) * 100, $base_deadline + 5000,
                    X'7B7D', printf('%032x', 20000 + n), X'7B7D', 1
                FROM due_rows;
                WITH RECURSIVE due_rows(n) AS (VALUES(1) UNION ALL SELECT n + 1 FROM due_rows WHERE n < 10)
                INSERT INTO processing_centered_view_window_work(window_sha256, retry_utc, retry_count)
                SELECT printf('%064x', 20000 + n), CASE WHEN n % 2 = 0 THEN 0 ELSE $now - n END, CASE WHEN n % 2 = 0 THEN 0 ELSE 1 END
                FROM due_rows;
                """;
            dueCommand.Parameters.AddWithValue("$base_deadline", Start.UtcTicks);
            dueCommand.Parameters.AddWithValue("$now", Start.UtcTicks);
            await dueCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var expectedOrder = Enumerable.Range(1, 10)
            .OrderBy(n => 11 - n)
            .Take(8)
            .Select(n => FormattableString.Invariant($"{20000 + n:x64}"))
            .ToArray();
        var due = await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false);
        CollectionAssert.AreEqual(expectedOrder, due.ToArray());

        static async Task SeedCompletedWindowsAsync(SqliteConnection db, int from, int to, long ticks, CancellationToken ct)
        {
            using var insert = db.CreateCommand();
            insert.CommandText = """
                WITH RECURSIVE history(n) AS (VALUES($from) UNION ALL SELECT n + 1 FROM history WHERE n < $to)
                INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                    checkpoint_json, reference_capture_id, closure_json, terminal)
                SELECT printf('%064x', n), $ticks + n, $ticks + n + 1000, X'7B7D', printf('%032x', n), X'7B7D', 1
                FROM history;
                WITH RECURSIVE history(n) AS (VALUES($from) UNION ALL SELECT n + 1 FROM history WHERE n < $to)
                INSERT INTO processing_centered_view_window_work(window_sha256, retry_utc, terminal_reason)
                SELECT printf('%064x', n), NULL, NULL
                FROM history;
                """;
            insert.Parameters.AddWithValue("$from", from);
            insert.Parameters.AddWithValue("$to", to);
            insert.Parameters.AddWithValue("$ticks", ticks);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        static async Task<List<string>> ExplainDueWindowQueryPlanAsync(SqliteConnection db, long nowTicks, int limit, CancellationToken ct)
        {
            using var explain = db.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN " + SqliteCenteredViews.DueWindowPublicationsSql;
            explain.Parameters.AddWithValue("$now", nowTicks);
            explain.Parameters.AddWithValue("$limit", limit);
            var steps = new List<string>();
            using var reader = await explain.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                steps.Add(reader.GetString(3));
            return steps;
        }

        static int MeasureDueWindowVmSteps(SqliteConnection db, long nowTicks, int limit)
        {
            Assert.AreEqual(SQLitePCL.raw.SQLITE_OK,
                SQLitePCL.raw.sqlite3_prepare_v2(db.Handle, SqliteCenteredViews.DueWindowPublicationsSql, out var stmt));
            try
            {
                var nowIndex = SQLitePCL.raw.sqlite3_bind_parameter_index(stmt, "$now");
                var limitIndex = SQLitePCL.raw.sqlite3_bind_parameter_index(stmt, "$limit");
                Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_bind_int64(stmt, nowIndex, nowTicks));
                Assert.AreEqual(SQLitePCL.raw.SQLITE_OK, SQLitePCL.raw.sqlite3_bind_int64(stmt, limitIndex, limit));
                var rows = 0;
                int rc;
                while ((rc = SQLitePCL.raw.sqlite3_step(stmt)) == SQLitePCL.raw.SQLITE_ROW)
                    rows++;
                Assert.AreEqual(SQLitePCL.raw.SQLITE_DONE, rc);
                Assert.AreEqual(0, rows);
                return SQLitePCL.raw.sqlite3_stmt_status(stmt, SQLitePCL.raw.SQLITE_STMTSTATUS_VM_STEP, 0);
            }
            finally
            {
                SQLitePCL.raw.sqlite3_finalize(stmt);
            }
        }
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The legacy window-work test DDL is a fixed store constant.")]
    public async Task CenteredWindowWorkQueueSchema12AdoptionIsAtomicAndBackfillsUnqueuedTerminalWindows()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var clock = new CenteredTestClock { UtcNow = Start };
        var completedId = new string('1', 64);
        var retryingId = new string('2', 64);
        var unqueuedId = new string('3', 64);
        var missingRefId = new string('4', 64);
        var capacityRefusedId = new string('5', 64);
        var retryTicks = Start.AddSeconds(10).UtcTicks;

        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                DROP INDEX ix_processing_centered_view_window_due;
                DROP INDEX ix_processing_centered_view_window_live_base;
                DROP TABLE processing_centered_view_window_work;
                """ + "\n" + SqliteCaptureProcessingStore.LegacyCenteredViewWindowWorkSchemaSqlForTests + """
                INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                    checkpoint_json, reference_capture_id, closure_json, failure_reason, terminal)
                VALUES
                    ($completed, $t1, $t2, X'7B7D', '11111111111111111111111111111111', X'7B7D', NULL, 1),
                    ($retrying,  $t1, $t2, X'7B7D', '22222222222222222222222222222222', X'7B7D', NULL, 1),
                    ($unqueued,  $t1, $t2, X'7B7D', '33333333333333333333333333333333', X'7B7D', NULL, 1),
                    ($no_ref,    $t1, $t2, X'7B7D', NULL,                               X'7B7D', NULL, 1),
                    ($refused,   $t1, $t2, X'7B7D', '55555555555555555555555555555555', NULL,    'window-capacity-exhausted', 1);
                INSERT INTO processing_centered_view_window_work(window_sha256, live_base_id, retry_utc, terminal_reason)
                VALUES
                    ($completed, NULL, NULL, 'centered.window-evidence-rejected'),
                    ($retrying,  NULL, $retry_ticks, NULL);
                """;
            setup.Parameters.AddWithValue("$completed", completedId);
            setup.Parameters.AddWithValue("$retrying", retryingId);
            setup.Parameters.AddWithValue("$unqueued", unqueuedId);
            setup.Parameters.AddWithValue("$no_ref", missingRefId);
            setup.Parameters.AddWithValue("$refused", capacityRefusedId);
            setup.Parameters.AddWithValue("$t1", Start.UtcTicks);
            setup.Parameters.AddWithValue("$t2", Start.AddSeconds(30).UtcTicks);
            setup.Parameters.AddWithValue("$retry_ticks", retryTicks);
            await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options, clock,
            new ThrowOnceFaultInjector(CaptureProcessingFaultPoint.BeforeSchemaCommit)))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        Assert.AreEqual(0L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_processing_centered_view_window_due';").ConfigureAwait(false));
        Assert.AreEqual(2L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM processing_centered_view_window_work;").ConfigureAwait(false));

        await new SqliteRawCaptureJournal(fixture.DatabasePath, 1).InitializeAsync(token).ConfigureAwait(false);
        using var reopened = new SqliteCaptureProcessingStore(fixture.Options, clock);
        await reopened.InitializeAsync(token).ConfigureAwait(false);
        Assert.AreEqual(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE name = 'ix_processing_centered_view_window_due';").ConfigureAwait(false));
        Assert.AreEqual(3L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM processing_centered_view_window_work;").ConfigureAwait(false));
        var views = new SqliteCenteredViews(reopened, clock, NullLogger<SqliteCenteredViews>.Instance);
        CollectionAssert.AreEqual(new[] { unqueuedId }, (await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray());

        clock.UtcNow = Start.AddSeconds(10);
        CollectionAssert.AreEquivalent(new[] { unqueuedId, retryingId },
            (await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray());
        Assert.AreEqual("centered.window-evidence-rejected",
            await ScalarObjectAsync(connection, "SELECT terminal_reason FROM processing_centered_view_window_work WHERE window_sha256 = '" + completedId + "';")
                .ConfigureAwait(false));

        static async Task<object?> ScalarObjectAsync(SqliteConnection db, string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            return await cmd.ExecuteScalarAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task InvalidCheckpointWithReferenceCaptureDoesNotEnqueueDueWindowWork()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var clock = new CenteredTestClock { UtcNow = Start.AddSeconds(12) };
        var schedule = Schedule(4, 12);
        var slots = Enumerable.Range(101, 5).Select(sequence => Exposure(sequence, 2 * (sequence - 101))).ToImmutableArray();
        var referenceCaptureId = slots[2].Input!.Key.CaptureId;
        var journal = new CenteredJournalProbe(slots);

        Assert.IsTrue(await TrackAsync(fixture, schedule, slots, 1, token).ConfigureAwait(false));
        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var corrupt = connection.CreateCommand())
        {
            corrupt.CommandText = """
                UPDATE processing_centered_windows
                SET reference_capture_id = $reference, checkpoint_json = X'7B'
                WHERE window_sha256 = $window;
                """;
            corrupt.Parameters.AddWithValue("$reference", referenceCaptureId.ToString("N"));
            corrupt.Parameters.AddWithValue("$window", schedule.Identity);
            Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync(token).ConfigureAwait(false));
        }

        using var store = new SqliteCaptureProcessingStore(fixture.Options, clock);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.TryCloseCenteredWindowAsync(schedule.Identity, journal, token).AsTask()).ConfigureAwait(false);

        using (var verify = await fixture.OpenAsync().ConfigureAwait(false))
        {
            using var windowCmd = verify.CreateCommand();
            windowCmd.CommandText = """
                SELECT terminal, closure_json IS NULL, failure_reason, reference_capture_id
                FROM processing_centered_windows WHERE window_sha256 = $window;
                """;
            windowCmd.Parameters.AddWithValue("$window", schedule.Identity);
            using var reader = await windowCmd.ExecuteReaderAsync(token).ConfigureAwait(false);
            Assert.IsTrue(await reader.ReadAsync(token).ConfigureAwait(false));
            Assert.AreEqual(1L, reader.GetInt64(0));
            Assert.AreEqual(1L, reader.GetInt64(1));
            Assert.AreEqual("invalid-window-checkpoint", reader.GetString(2));
            Assert.AreEqual(referenceCaptureId.ToString("N"), reader.GetString(3));

            using var workCmd = verify.CreateCommand();
            workCmd.CommandText = "SELECT COUNT(*) FROM processing_centered_view_window_work WHERE window_sha256 = $window;";
            workCmd.Parameters.AddWithValue("$window", schedule.Identity);
            Assert.AreEqual(0L, (long)(await workCmd.ExecuteScalarAsync(token).ConfigureAwait(false))!);
        }

        var views = new SqliteCenteredViews(store, clock, NullLogger<SqliteCenteredViews>.Instance);
        CollectionAssert.DoesNotContain(
            (await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
            schedule.Identity);
    }

    [TestMethod]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The legacy window-work test DDL is a fixed store constant.")]
    public async Task CenteredWindowWorkQueueAdoptionDropsLegacyUnclosedTerminalRetriesWhileKeepingValidRetries()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var clock = new CenteredTestClock { UtcNow = Start };
        var validRetryingId = new string('a', 64);
        var capacityRefusedRetryId = new string('b', 64);
        var invalidCheckpointRetryId = new string('c', 64);
        var settledRefusedId = new string('d', 64);
        var retryTicks = Start.AddSeconds(10).UtcTicks;

        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                DROP INDEX ix_processing_centered_view_window_due;
                DROP INDEX ix_processing_centered_view_window_live_base;
                DROP TABLE processing_centered_view_window_work;
                """ + "\n" + SqliteCaptureProcessingStore.LegacyCenteredViewWindowWorkSchemaSqlForTests + """
                INSERT INTO processing_centered_windows(window_sha256, deadline_utc_ticks, registration_deadline_utc_ticks,
                    checkpoint_json, reference_capture_id, closure_json, failure_reason, terminal)
                VALUES
                    ($valid,    $t1, $t2, X'7B7D', 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', X'7B7D', NULL,                        1),
                    ($capacity, $t1, $t2, X'7B7D', 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', NULL,    'window-capacity-exhausted', 1),
                    ($invalid,  $t1, $t2, X'7B7D', 'cccccccccccccccccccccccccccccccc', NULL,    'invalid-window-checkpoint', 1),
                    ($settled,  $t1, $t2, X'7B7D', 'dddddddddddddddddddddddddddddddd', NULL,    'window-capacity-exhausted', 1);
                INSERT INTO processing_centered_view_window_work(window_sha256, live_base_id, retry_utc, terminal_reason)
                VALUES
                    ($valid,    NULL, $retry_ticks, NULL),
                    ($capacity, NULL, $t1,          NULL),
                    ($invalid,  NULL, $retry_ticks, NULL),
                    ($settled,  NULL, NULL,         'centered.window-evidence-rejected');
                """;
            setup.Parameters.AddWithValue("$valid", validRetryingId);
            setup.Parameters.AddWithValue("$capacity", capacityRefusedRetryId);
            setup.Parameters.AddWithValue("$invalid", invalidCheckpointRetryId);
            setup.Parameters.AddWithValue("$settled", settledRefusedId);
            setup.Parameters.AddWithValue("$t1", Start.UtcTicks);
            setup.Parameters.AddWithValue("$t2", Start.AddSeconds(30).UtcTicks);
            setup.Parameters.AddWithValue("$retry_ticks", retryTicks);
            await setup.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await new SqliteRawCaptureJournal(fixture.DatabasePath, 1).InitializeAsync(token).ConfigureAwait(false);
        using var reopened = new SqliteCaptureProcessingStore(fixture.Options, clock);
        await reopened.InitializeAsync(token).ConfigureAwait(false);

        var views = new SqliteCenteredViews(reopened, clock, NullLogger<SqliteCenteredViews>.Instance);
        Assert.IsEmpty(await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false),
            "Legacy unclosed terminal retry rows must not become due after schema adoption.");

        clock.UtcNow = Start.AddSeconds(10);
        CollectionAssert.AreEqual(new[] { validRetryingId },
            (await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
            "Only valid closed pending retries should remain due after schema adoption.");

        Assert.AreEqual(0L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM processing_centered_view_window_work WHERE window_sha256 IN ('" + capacityRefusedRetryId + "', '" + invalidCheckpointRetryId + "');")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM processing_centered_view_window_work WHERE window_sha256 = '" + validRetryingId + "' AND retry_utc = " + retryTicks + " AND retry_count = 0;")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM processing_centered_view_window_work WHERE window_sha256 = '" + settledRefusedId + "' AND retry_utc IS NULL AND terminal_reason = 'centered.window-evidence-rejected';")
            .ConfigureAwait(false));
    }

    [TestMethod]
    public async Task CenteredCapacityRefusedWindowResolvesThroughReaderAfterAdmission()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var clock = new CenteredTestClock { UtcNow = Start };
        var schedule = Schedule(4, 12);
        var slots = Enumerable.Range(101, 5).Select(sequence => Exposure(sequence, 2 * (sequence - 101))).ToImmutableArray();
        var attempt = new ExposureAttemptKey(schedule.Camera, schedule.RunId, 1, Guid.NewGuid());
        var captureId = Guid.NewGuid();
        var checkpoint = new CenteredWindowCheckpoint(schedule, slots.Select(slot => slot.Time).ToImmutableArray(), attempt);

        using (var connection = await fixture.OpenAsync().ConfigureAwait(false))
        using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false))
        {
            using (var seed = connection.CreateCommand())
            {
                seed.Transaction = transaction;
                seed.CommandText = """
                    INSERT INTO centered_exposure_runs(run_id, agent_id, camera_id, snapshot_json, snapshot_sha256)
                    VALUES ($run, $agent, $camera, X'7B7D', 'capacity-refusal-run');
                    INSERT INTO centered_exposure_attempts(attempt_id, run_id, ordinal, status, capture_id)
                    VALUES ($attempt, $run, 1, 'observed', $capture);
                    """;
                seed.Parameters.AddWithValue("$run", schedule.RunId.ToString("N"));
                seed.Parameters.AddWithValue("$agent", schedule.Camera.AgentId);
                seed.Parameters.AddWithValue("$camera", schedule.Camera.CameraId);
                seed.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
                seed.Parameters.AddWithValue("$capture", captureId.ToString("N"));
                await seed.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await SqliteCaptureProcessingStore.RecordCenteredCapacityRefusalAsync(transaction, checkpoint, token)
                .ConfigureAwait(false);

            using (var calendar = connection.CreateCommand())
            {
                calendar.Transaction = transaction;
                calendar.CommandText = """
                    INSERT INTO processing_centered_attempt_calendar(
                        attempt_id, run_id, slot, start_ticks, interval_ticks, integration_ticks, cadence, dispatched_ticks, window_sha256)
                    VALUES ($attempt, $run, 1, $start, $interval, $integration, 'minimum-start-interval', $start, $window);
                    """;
                calendar.Parameters.AddWithValue("$attempt", attempt.AttemptId.ToString("N"));
                calendar.Parameters.AddWithValue("$run", schedule.RunId.ToString("N"));
                calendar.Parameters.AddWithValue("$start", Start.UtcTicks);
                calendar.Parameters.AddWithValue("$interval", TimeSpan.FromSeconds(2).Ticks);
                calendar.Parameters.AddWithValue("$integration", TimeSpan.FromMilliseconds(250).Ticks);
                calendar.Parameters.AddWithValue("$window", schedule.Identity);
                await calendar.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            await CenteredWindowRuntimeJournal.MarkAdmittedAsync(transaction, attempt, Start, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }

        using var store = new SqliteCaptureProcessingStore(fixture.Options, clock);
        var reader = new CenteredWindowReader(store);
        var snapshot = await reader.ReadAsync(captureId, token).ConfigureAwait(false);
        Assert.IsNotNull(snapshot, "Capacity-refused captures must still resolve through ICenteredWindowReader.");
        Assert.AreEqual(captureId, snapshot.ReferenceCaptureId);
        Assert.AreEqual(schedule.Identity, snapshot.WindowIdentity);
        Assert.AreEqual(schedule.Camera, snapshot.Camera);
        Assert.AreEqual(schedule.RunId, snapshot.RunId);
        Assert.IsTrue(snapshot.IsTerminal);
        Assert.AreEqual("window-capacity-exhausted", snapshot.FailureReason);
        Assert.IsNull(snapshot.ClosureIdentity);

        var views = new SqliteCenteredViews(store, clock, NullLogger<SqliteCenteredViews>.Instance);
        CollectionAssert.DoesNotContain(
            (await views.ReadDueWindowPublicationsAsync(8, token).ConfigureAwait(false)).ToArray(),
            schedule.Identity);
    }

    [TestMethod]
    public async Task CenteredRegistrationSchema12AdoptionIsAtomicAndPreservesClosuresAndCustody()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var schedule = Schedule(4, 12);
        var slots = ImmutableArray.Create(Exposure(101, 0), Exposure(102, 2), Exposure(103, 4), Empty(6), Empty(8));
        var historical = Schedule(6, 14);
        var historicalSlots = ImmutableArray.Create(slots[1], slots[2], Exposure(104, 6), Empty(8), Empty(10));
        var journal = new CenteredJournalProbe(slots);
        var clock = new CenteredTestClock { UtcNow = historical.DeadlineUtc };
        var referenceId = slots[2].Input!.Key.CaptureId;
        Assert.IsTrue(await TrackAsync(fixture, schedule, slots, 1, token).ConfigureAwait(false));
        using var original = new SqliteCaptureProcessingStore(fixture.Options, clock);
        var closed = await original.TryCloseCenteredWindowAsync(schedule.Identity, journal, token).ConfigureAwait(false);
        Assert.IsNotNull(closed);
        Assert.IsFalse(closed.IsTerminal);

        Assert.IsTrue(await TrackAsync(fixture, historical, historicalSlots, 2, token).ConfigureAwait(false));
        journal.Slots = historicalSlots;
        var historicalClosure = await original.TryCloseCenteredWindowAsync(historical.Identity, journal, token).ConfigureAwait(false);
        Assert.IsNotNull(historicalClosure);
        var receipt = await original.FailCenteredRegistrationAsync(historicalClosure.Closure, "registration.historical-test",
            journal, token).ConfigureAwait(false);
        Assert.IsTrue(receipt.IsTerminal);
        Assert.AreEqual(1L, await CountPinsAsync(fixture, referenceId).ConfigureAwait(false));

        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = DropCenteredRegistrationStateSql;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        var before = await ReadClosedStateAsync().ConfigureAwait(false);
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options, clock,
            new ThrowOnceFaultInjector(CaptureProcessingFaultPoint.BeforeSchemaCommit)))
        {
            await Assert.ThrowsExactlyAsync<InjectedProcessingSchemaException>(async () =>
                await interrupted.InitializeAsync(token).ConfigureAwait(false)).ConfigureAwait(false);
        }
        const string addedObjects = """
            SELECT COUNT(*) FROM sqlite_schema WHERE name IN
                ('processing_centered_registration_input_failures', 'ix_processing_centered_windows_publication_retention');
            """;
        Assert.AreEqual(0L, await ScalarAsync(connection, addedObjects).ConfigureAwait(false));
        Assert.AreEqual(before, await ReadClosedStateAsync().ConfigureAwait(false));

        var ingress = new SqliteRawCaptureJournal(fixture.DatabasePath, 1);
        await ingress.InitializeAsync(token).ConfigureAwait(false);
        using var reopened = new SqliteCaptureProcessingStore(fixture.Options, clock);
        await reopened.InitializeAsync(token).ConfigureAwait(false);
        Assert.AreEqual(2L, await ScalarAsync(connection, addedObjects).ConfigureAwait(false));
        Assert.AreEqual(12L, await ScalarAsync(connection, "SELECT version FROM capture_processing_schema;").ConfigureAwait(false));
        Assert.AreEqual(before, await ReadClosedStateAsync().ConfigureAwait(false));
        var after = await reopened.ReadCenteredWindowByReferenceAsync(referenceId, token).ConfigureAwait(false);
        Assert.AreEqual(closed.Closure.Identity, after?.Closure.Identity);
        var historyAfter = await reopened.ReadCenteredWindowDispositionAsync(historical.Identity, token).ConfigureAwait(false);
        Assert.IsNotNull(historyAfter);
        Assert.AreEqual(receipt.Closure.Identity, historyAfter.Closure.Identity);
        Assert.AreEqual(receipt.FailureReason, historyAfter.FailureReason);
        Assert.AreEqual(1L, await CountPinsAsync(fixture, referenceId).ConfigureAwait(false));

        async Task<string> ReadClosedStateAsync()
        {
            command.CommandText = """
                SELECT group_concat(hex(closure_json) || '/' || COALESCE(failure_reason, ''), '|' ORDER BY window_sha256)
                FROM processing_centered_windows;
                """;
            return (string)(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!;
        }
    }

    [TestMethod]
    public async Task CenteredWindowRestartClosesOnOriginalDeadlineAndRollsBackAnInterruptedClose()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var now = new CenteredTestClock { UtcNow = Start.AddSeconds(9) };
        var schedule = Schedule(4, 10);
        var late105 = Exposure(105, 8);
        var target104 = new ExposureAttemptKey(schedule.Camera, schedule.RunId, 104, Guid.NewGuid());
        var slots = ImmutableArray.Create(
            Exposure(101, 0),
            Exposure(102, 2),
            Exposure(103, 4),
            Empty(6) with
            {
                EmptySlotSettledUtc = Start.AddSeconds(7),
                EmptySlotReason = "target-acknowledgement-uncertain",
                EmptySlotKind = CenteredWindowEmptySlotKind.Uncertain,
                Attempt = target104
            },
            late105 with { Input = late105.Input! with { AdmittedUtc = Start.AddSeconds(11) }, Products = null });
        var journal = new CenteredJournalProbe(slots);

        Assert.IsTrue(await TrackAsync(fixture, schedule, slots, 2, token).ConfigureAwait(false));
        using (var beforeRestart = new SqliteCaptureProcessingStore(fixture.Options, now))
        {
            Assert.IsNull(await beforeRestart.TryCloseCenteredWindowAsync(schedule.Identity, journal, token).ConfigureAwait(false));
        }

        now.UtcNow = Start.AddSeconds(30);
        using (var interrupted = new SqliteCaptureProcessingStore(fixture.Options, now))
        {
            journal.FaultBeforeCloseCommit = true;
            await Assert.ThrowsExactlyAsync<SqliteException>(() =>
                interrupted.TryCloseCenteredWindowAsync(schedule.Identity, journal, token).AsTask()).ConfigureAwait(false);
            Assert.IsNull(await interrupted.ReadCenteredWindowDispositionAsync(schedule.Identity, token).ConfigureAwait(false));
            Assert.AreEqual(0L, await CountPinsAsync(fixture, slots[2].Input!.Key.CaptureId).ConfigureAwait(false));
        }

        using var restarted = new SqliteCaptureProcessingStore(fixture.Options, now);
        using var coordinator = new CenteredWindowCoordinator(restarted, journal, now, NullLogger<CenteredWindowCoordinator>.Instance);
        journal.FaultBeforeCloseCommit = false;
        await coordinator.CloseDueAsync(token).ConfigureAwait(false);
        var disposition = await restarted.ReadCenteredWindowDispositionAsync(schedule.Identity, token).ConfigureAwait(false);

        Assert.IsNotNull(disposition);
        Assert.AreEqual(schedule.DeadlineUtc, disposition.Closure.EvidenceCutoffUtc);
        Assert.AreEqual(schedule.DeadlineUtc, journal.LastCutoff);
        Assert.AreEqual(slots[2].Input, disposition.Closure.Center.Input);
        Assert.AreEqual(CenteredWindowSlotState.UncertainAttempt, disposition.Closure.Slots[3].State);
        Assert.AreEqual(target104, disposition.Closure.Slots[3].Attempt);
        Assert.AreEqual(CenteredWindowSlotState.MissingExposure, disposition.Closure.Slots[4].State);
        CollectionAssert.AreEqual(new long?[] { 101, 102, 103, null, null },
            disposition.Closure.Slots.Select(slot => slot.Input?.CaptureSequence).ToArray());
        Assert.AreEqual(1L, await CountPinsAsync(fixture, slots[2].Input!.Key.CaptureId).ConfigureAwait(false));

        using (var registrationRestart = new SqliteCaptureProcessingStore(fixture.Options, now))
        {
            var ready = await registrationRestart.ReadPendingCenteredRegistrationsAsync(null, 1, token).ConfigureAwait(false);
            Assert.AreEqual(schedule.Identity, ready.Single().WindowIdentity);
            var afterRead = await registrationRestart.ReadPendingCenteredRegistrationsAsync(ready.Single(), 1, token).ConfigureAwait(false);
            Assert.IsEmpty(afterRead);
        }

        // A lost response from the successful commit and a better late result
        // are both recovered by reading the same immutable disposition.
        journal.Slots = slots.SetItem(4, late105);
        var retry = await restarted.TryCloseCenteredWindowAsync(schedule.Identity, journal, token).ConfigureAwait(false);
        Assert.AreEqual(disposition.Closure.Identity, retry?.Closure.Identity);
        Assert.AreEqual(1L, await CountPinsAsync(fixture, slots[2].Input!.Key.CaptureId).ConfigureAwait(false));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => TrackAsync(fixture,
            schedule with { DeadlineUtc = schedule.DeadlineUtc.AddSeconds(1) }, slots, 2, token)).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CenteredWindowTerminalFailureAndExpiryReleaseIndependentPins()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var now = new CenteredTestClock { UtcNow = Start.AddSeconds(13) };
        var exposures = Enumerable.Range(101, 6).Select(sequence => Exposure(sequence, (sequence - 101) * 2)).ToImmutableArray();
        var first = Schedule(4, 12);
        var second = Schedule(6, 14);
        var capacityProbe = Schedule(8, 16);
        var journal = new CenteredJournalProbe(exposures[..5]);

        Assert.IsTrue(await TrackAsync(fixture, first, exposures[..5], 2, token).ConfigureAwait(false));
        Assert.IsTrue(await TrackAsync(fixture, second, exposures[1..], 2, token).ConfigureAwait(false));
        Assert.IsTrue(await TrackAsync(fixture, second, exposures[1..], 2, token).ConfigureAwait(false));
        Assert.IsFalse(await TrackAsync(fixture, capacityProbe, exposures[2..].Add(Empty(12)), 2, token).ConfigureAwait(false));

        using var store = new SqliteCaptureProcessingStore(fixture.Options, now);
        var firstClosed = await store.TryCloseCenteredWindowAsync(first.Identity, journal, token).ConfigureAwait(false);
        journal.Slots = exposures[1..];
        var secondClosed = await store.TryCloseCenteredWindowAsync(second.Identity, journal, token).ConfigureAwait(false);
        Assert.IsNotNull(firstClosed);
        Assert.IsNotNull(secondClosed);

        var reference103 = exposures[2].Input!.Key.CaptureId;
        var reference104 = exposures[3].Input!.Key.CaptureId;
        var source105 = exposures[4].Input!.Key.CaptureId;
        Assert.AreEqual(2L, await CountPinsAsync(fixture, source105).ConfigureAwait(false));

        var failures = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
            await store.FailCenteredRegistrationAsync(firstClosed.Closure, "registration.input-unavailable",
                journal, token).ConfigureAwait(false), token))).ConfigureAwait(false);
        Assert.IsTrue(failures.All(result => result.IsTerminal && result.FailureReason == "registration.input-unavailable"));
        Assert.AreEqual(1L, await CountPinsAsync(fixture, source105).ConfigureAwait(false));
        Assert.AreEqual("registration.input-unavailable",
            (await store.ReadCenteredWindowByReferenceAsync(reference103, token).ConfigureAwait(false))?.FailureReason);
        Assert.IsFalse((await store.ReadCenteredWindowByReferenceAsync(reference104, token).ConfigureAwait(false))!.IsTerminal);

        now.UtcNow = second.RegistrationDeadlineUtc;
        using var coordinator = new CenteredWindowCoordinator(store, journal, now, NullLogger<CenteredWindowCoordinator>.Instance);
        await coordinator.CloseDueAsync(token).ConfigureAwait(false);
        Assert.AreEqual("registration-deadline-expired",
            (await store.ReadCenteredWindowByReferenceAsync(reference104, token).ConfigureAwait(false))?.FailureReason);
        Assert.AreEqual(0L, await CountPinsAsync(fixture, source105).ConfigureAwait(false));
        Assert.IsTrue(await TrackAsync(fixture, capacityProbe, exposures[2..].Add(Empty(12)), 2, token).ConfigureAwait(false));
    }

    [TestMethod]
    public async Task InvalidEvidenceAtDeadlineDoesNotKeepTheNextWindowOpen()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var now = new CenteredTestClock { UtcNow = Start.AddSeconds(12) };
        var bad = Schedule(4, 11) with { GenerationId = "first-generation" };
        var good = Schedule(4, 12);
        var slots = Enumerable.Range(101, 5).Select(sequence => Exposure(sequence, 2 * (sequence - 101))).ToImmutableArray();
        var journal = new CenteredJournalProbe(slots) { InvalidWindowIdentity = bad.Identity };

        Assert.IsTrue(await TrackAsync(fixture, bad, slots, 2, token).ConfigureAwait(false));
        Assert.IsTrue(await TrackAsync(fixture, good, slots, 2, token).ConfigureAwait(false));
        using var store = new SqliteCaptureProcessingStore(fixture.Options, now);
        using var coordinator = new CenteredWindowCoordinator(store, journal, now, NullLogger<CenteredWindowCoordinator>.Instance);
        await coordinator.CloseDueAsync(token).ConfigureAwait(false);

        var rejected = await store.ReadCenteredWindowDispositionAsync(bad.Identity, token).ConfigureAwait(false);
        Assert.IsTrue(rejected!.IsTerminal);
        Assert.AreEqual("invalid-window-evidence", rejected.FailureReason);
        var healthy = await store.ReadCenteredWindowDispositionAsync(good.Identity, token).ConfigureAwait(false);
        Assert.AreEqual(slots[2].Input, healthy?.Closure.Center.Input);
        Assert.IsFalse(healthy!.IsTerminal);
    }

    [TestMethod]
    public async Task CenteredWindowAbsentReferenceClosesWithoutARegisteredView()
    {
        using var fixture = await SchemaFixture.CreateAsync(initializeProcessing: true).ConfigureAwait(false);
        var token = TestContext.CancellationToken;
        var now = new CenteredTestClock { UtcNow = Start.AddSeconds(11) };
        var schedule = Schedule(4, 12);
        var slots = ImmutableArray.Create(Exposure(101, 0), Exposure(102, 2), Empty(4), Exposure(104, 6), Exposure(105, 8));
        var journal = new CenteredJournalProbe(slots);

        Assert.IsTrue(await TrackAsync(fixture, schedule, slots, 1, token).ConfigureAwait(false));
        using var store = new SqliteCaptureProcessingStore(fixture.Options, now);
        using var coordinator = new CenteredWindowCoordinator(store, journal, now, NullLogger<CenteredWindowCoordinator>.Instance);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await coordinator.StartAsync(deadline.Token).ConfigureAwait(false);
        Assert.AreEqual(Start.AddSeconds(11), await now.FirstRead.WaitAsync(deadline.Token).ConfigureAwait(false));
        now.UtcNow = schedule.DeadlineUtc;
        CenteredWindowDisposition? closed;
        while ((closed = await store.ReadCenteredWindowDispositionAsync(schedule.Identity, deadline.Token).ConfigureAwait(false)) is null)
            await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token).ConfigureAwait(false);
        await coordinator.StopAsync(deadline.Token).ConfigureAwait(false);

        Assert.IsTrue(closed.IsTerminal);
        Assert.IsNull(closed.Publication);
        Assert.AreEqual(CenteredWindowSlotState.MissingExposure, closed.Closure.Center.State);
        Assert.AreEqual(0L, await CountPinsAsync(fixture, slots[1].Input!.Key.CaptureId).ConfigureAwait(false));
    }

    private static async Task<bool> TrackAsync(SchemaFixture fixture,
        CenteredWindowSchedule schedule,
        ImmutableArray<CenteredWindowSlotObservation> slots,
        int limit,
        CancellationToken token)
    {
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
        var tracked = await SqliteCaptureProcessingStore.TrackCenteredWindowAsync(transaction,
            new(schedule, slots.Select(slot => slot.Time).ToImmutableArray(), ReferenceCapture: slots[2].Input?.Key), limit, token).ConfigureAwait(false);
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return tracked;
    }

    private static async Task<long> CountPinsAsync(SchemaFixture fixture, Guid captureId)
    {
        using var connection = await fixture.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM processing_nodes WHERE capture_id = $capture AND node_id LIKE 'centered-probe:%';";
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        return (long)(await command.ExecuteScalarAsync().ConfigureAwait(false))!;
    }

    // Probe the adapter's transaction contract with independently owned test
    // records. Qualification must repeat this with #1919's real retention pins.
    private static async Task WritePinProbeAsync(
        SqliteConnection connection, SqliteTransaction? transaction, Guid capture, string owner, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO processing_nodes(
                capture_id, node_id, required, dependencies_json, recipe_name, output_role,
                output_variant, plan_sha256, status, reason, attempt, completed_unix_ms)
            VALUES($capture, $owner, 1, '[]', 'pin-probe', 'Preview', 'default', $recipe, 'Completed', NULL, 1, 0);
            """;
        command.Parameters.AddWithValue("$capture", capture.ToString("N"));
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$recipe", RecipeIdentity);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private sealed class CenteredTestClock : TimeProvider
    {
        private readonly TaskCompletionSource<DateTimeOffset> _firstRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _utcTicks;

        internal Task<DateTimeOffset> FirstRead => _firstRead.Task;

        internal DateTimeOffset UtcNow
        {
            get => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
        }

        public override DateTimeOffset GetUtcNow()
        {
            var time = UtcNow;
            _firstRead.TrySetResult(time);
            return time;
        }
    }

    private sealed class CenteredJournalProbe(ImmutableArray<CenteredWindowSlotObservation> slots) : ICenteredWindowJournal
    {
        internal ImmutableArray<CenteredWindowSlotObservation> Slots { get; set; } = slots;
        internal DateTimeOffset LastCutoff { get; private set; }
        internal bool FaultBeforeCloseCommit { get; set; }
        internal string? InvalidWindowIdentity { get; init; }

        public ValueTask<CenteredWindowJournalSnapshot> ReadWindowEvidenceAsync(
            SqliteTransaction transaction, CenteredWindowCheckpoint checkpoint, DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
        {
            LastCutoff = cutoffUtc;
            CollectionAssert.AreEqual(checkpoint.ProjectedTimes.ToArray(), Slots.Select(slot => slot.Time).ToArray());
            var generation = checkpoint.Schedule.Identity == InvalidWindowIdentity ? "wrong-generation" : checkpoint.Schedule.GenerationId;
            return ValueTask.FromResult(new CenteredWindowJournalSnapshot(generation, Slots[2].Input?.Key, [], Slots));
        }

        public async ValueTask PinClosedInputsAsync(
            SqliteTransaction transaction, CenteredWindowClosure closure, CancellationToken cancellationToken)
        {
            foreach (var slot in closure.Slots.Where(slot => slot.State == CenteredWindowSlotState.Eligible))
            {
                await WritePinProbeAsync(transaction.Connection!, transaction, slot.Input!.Key.CaptureId,
                    "centered-probe:" + closure.Schedule.Identity, cancellationToken).ConfigureAwait(false);
            }

            if (FaultBeforeCloseCommit)
            {
                using var command = transaction.Connection!.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TEMP TRIGGER inject_centered_close_failure
                    BEFORE UPDATE OF closure_json ON processing_centered_windows
                    BEGIN
                        SELECT RAISE(ABORT, 'fault before centered closure commit');
                    END;
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async ValueTask ReleaseWindowInputsAsync(
            SqliteTransaction transaction, string windowIdentity, CancellationToken cancellationToken)
        {
            using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM processing_nodes WHERE node_id = $owner;";
            command.Parameters.AddWithValue("$owner", "centered-probe:" + windowIdentity);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
