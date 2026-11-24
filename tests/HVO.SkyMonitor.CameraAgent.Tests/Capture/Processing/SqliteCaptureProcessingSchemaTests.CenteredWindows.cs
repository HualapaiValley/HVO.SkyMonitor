using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
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
