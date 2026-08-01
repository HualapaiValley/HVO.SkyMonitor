using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Transients;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class Issue247TransientCandidateReservationColumnRaceTests
{
    [TestMethod]
    [DataRow("candidate_id")]
    [DataRow("event_id")]
    [DataRow("agent_id")]
    [DataRow("mode")]
    [DataRow("required")]
    [DataRow("reservation_identity_sha256")]
    [DataRow("state")]
    [DataRow("phase")]
    [DataRow("candidate_payload")]
    [DataRow("candidate_payload_sha256")]
    [DataRow("finalization_payload")]
    [DataRow("finalization_receipt_identity_sha256")]
    [DataRow("submission_payload")]
    [DataRow("submission_identity_sha256")]
    [DataRow("acknowledgement_payload")]
    [DataRow("acknowledgement_payload_sha256")]
    [DataRow("source_hold_released")]
    [DataRow("quarantine_reason")]
    [DataRow("timeout_unix_ms")]
    [DataRow("created_unix_ms")]
    [DataRow("updated_unix_ms")]
    [DataRow("candidate_state")]
    public async Task ReserveAsync_EachCandidateColumnMutationOrEquivalentInsertDeleteRetriesThenConverges(
        string column)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        var created = await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var mutation = await CandidateMutationAsync(fixture.Root, reservation, column).ConfigureAwait(false);
        var injector = new CyclingFaultInjector(mutation.Mutate, mutation.Restore);
        using var reconstructed = fixture.ReconstructJournal(injector);

        var result = await reconstructed.Journal.ReserveAsync(
            reservation, CancellationToken.None).ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount, column);
        Assert.AreEqual(TransientCandidateReservationDisposition.Existing, result.Disposition, column);
        Assert.AreEqual(created.Entry.ReservationIdentitySha256, result.Entry.ReservationIdentitySha256, column);
        await AssertExactCandidateAsync(fixture, reconstructed.Journal, reservation, source).ConfigureAwait(false);
        Assert.AreEqual(column == "event_id" ? 2L : 1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_event_identities;").ConfigureAwait(false), column);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false), column);
    }

    [TestMethod]
    [DataRow("event_id")]
    [DataRow("agent_id")]
    [DataRow("created_unix_ms")]
    public async Task ReserveAsync_EachEventColumnMutationOrEquivalentInsertDeleteRetriesThenConverges(
        string column)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        await fixture.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false);
        var mutation = EventMutation(fixture.Root, reservation, column);
        var injector = new CyclingFaultInjector(mutation.Mutate, mutation.Restore);
        using var reconstructed = fixture.ReconstructJournal(injector);

        var result = await reconstructed.Journal.ReserveAsync(
            reservation, CancellationToken.None).ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount, column);
        Assert.AreEqual(TransientCandidateReservationDisposition.Existing, result.Disposition, column);
        await AssertExactCandidateAsync(fixture, reconstructed.Journal, reservation, source).ConfigureAwait(false);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_event_identities WHERE event_id = '{reservation.EventId:N}' AND agent_id = '{reservation.AgentId}';")
            .ConfigureAwait(false), column);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false), column);
    }

    [TestMethod]
    [DataRow("conflict_id")]
    [DataRow("candidate_id")]
    [DataRow("event_id")]
    [DataRow("reason")]
    [DataRow("observed_unix_ms")]
    public async Task ReserveAsync_EachConflictColumnMutationOrEquivalentInsertDeleteRetriesThenPreservesConflict(
        string column)
    {
        using var fixture = await SqliteTransientCandidateJournalTests.Fixture.CreateAsync().ConfigureAwait(false);
        var source = await fixture.AddRawSourceAsync(1, 100).ConfigureAwait(false);
        var reservation = SqliteTransientCandidateJournalTests.Fixture.CreateReservation(source);
        await ExecuteAsync(fixture.Root,
            "INSERT INTO transient_candidate_conflicts(conflict_id, candidate_id, event_id, reason, observed_unix_ms) VALUES (1, $candidate, $event, 'column-race', 1);",
            ("$candidate", reservation.CandidateId.ToString("N")),
            ("$event", reservation.EventId.ToString("N"))).ConfigureAwait(false);
        var mutation = ConflictMutation(fixture.Root, reservation, column);
        var injector = new CyclingFaultInjector(mutation.Mutate, mutation.Restore);
        using var reconstructed = fixture.ReconstructJournal(injector);

        await Assert.ThrowsExactlyAsync<TransientCandidateIdentityConflictException>(async () =>
            await reconstructed.Journal.ReserveAsync(reservation, CancellationToken.None).ConfigureAwait(false))
            .ConfigureAwait(false);

        Assert.IsGreaterThanOrEqualTo(2, injector.InvocationCount, column);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_conflicts WHERE conflict_id = 1 AND candidate_id = '{reservation.CandidateId:N}' AND event_id = '{reservation.EventId:N}' AND reason = 'column-race' AND observed_unix_ms = 1;")
            .ConfigureAwait(false), column);
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_conflicts;").ConfigureAwait(false), column);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_event_identities;").ConfigureAwait(false), column);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidates;").ConfigureAwait(false), column);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM transient_candidate_sources;").ConfigureAwait(false), column);
        Assert.AreEqual(0L, await fixture.ScalarLongAsync(
            "SELECT COUNT(*) FROM raw_captures WHERE retention_hold != 0;").ConfigureAwait(false), column);
    }

    private static async Task AssertExactCandidateAsync(
        SqliteTransientCandidateJournalTests.Fixture fixture,
        ITransientCandidateJournal journal,
        TransientCandidateReservation reservation,
        TransientSourceEvidenceReferenceV1 source)
    {
        var durable = await journal.ReadAsync(reservation.CandidateId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(durable);
        Assert.AreEqual(reservation.CandidateId, durable.CandidateId);
        Assert.AreEqual(reservation.EventId, durable.EventId);
        Assert.AreEqual(reservation.AgentId, durable.AgentId);
        Assert.AreEqual(TransientEventState.Pending, durable.State);
        Assert.AreEqual(TransientCandidateWorkflowPhase.IdentityAllocated, durable.Phase);
        Assert.AreEqual(ComputeReservationIdentity(reservation), durable.ReservationIdentitySha256);
        CollectionAssert.AreEqual(new[] { source }, durable.Sources.ToArray());
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidates WHERE candidate_id = '{reservation.CandidateId:N}' AND event_id = '{reservation.EventId:N}' AND agent_id = '{reservation.AgentId}' AND mode = 'edge' AND required = 0 AND state = 'pending' AND phase = 'reserved' AND source_hold_released = 0 AND quarantine_reason IS NULL AND candidate_state IS NULL;")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            $"SELECT COUNT(*) FROM transient_candidate_sources WHERE candidate_id = '{reservation.CandidateId:N}' AND source_ordinal = 0 AND evidence_id = '{source.EvidenceId:N}' AND artifact_id = '{source.Locator.Artifact.ArtifactId:N}' AND checksum_sha256 = '{source.Locator.Artifact.ChecksumSha256}';")
            .ConfigureAwait(false));
        Assert.AreEqual(1L, await fixture.ScalarLongAsync(
            "SELECT retention_hold FROM raw_captures;").ConfigureAwait(false));
    }

    private static async Task<ColumnMutation> CandidateMutationAsync(
        string root,
        TransientCandidateReservation reservation,
        string column)
    {
        var candidate = reservation.CandidateId.ToString("N");
        if (column == "candidate_id")
        {
            var changed = Guid.NewGuid().ToString("N");
            return new(
                () => ExecuteSync(root, CandidateIdentitySql, ("$from", candidate), ("$to", changed)),
                () => ExecuteSync(root, CandidateIdentitySql, ("$from", changed), ("$to", candidate)));
        }
        if (column == "event_id")
        {
            var changedEvent = Guid.NewGuid().ToString("N");
            await ExecuteAsync(root,
                "INSERT INTO transient_event_identities(event_id, agent_id, created_unix_ms) VALUES ($event, $agent, 1);",
                ("$event", changedEvent), ("$agent", reservation.AgentId)).ConfigureAwait(false);
            return ScalarMutation(root, candidate, column, changedEvent, reservation.EventId.ToString("N"));
        }
        return column switch
        {
            "agent_id" => ScalarMutation(root, candidate, column, "changed-agent", reservation.AgentId),
            "mode" => ScalarMutation(root, candidate, column, "hybrid", "edge"),
            "required" => ScalarMutation(root, candidate, column, 1, 0),
            "reservation_identity_sha256" => ScalarMutation(
                root, candidate, column, new string('A', 64), ComputeReservationIdentity(reservation)),
            "state" => ScalarMutation(root, candidate, column, "provisional", "pending"),
            "phase" => ScalarMutation(root, candidate, column, "candidate_persisted", "reserved"),
            // Presence/hash pairs cannot remain semantically valid when only one column changes.
            // The transient one-column insert/delete is never consumed; exact snapshot comparison
            // forces restoration and a second retry before canonical candidate parsing.
            "candidate_payload" => ScalarMutation(root, candidate, column, new byte[] { 123, 125 }, DBNull.Value),
            "candidate_payload_sha256" => ScalarMutation(root, candidate, column, new string('B', 64), DBNull.Value),
            "finalization_payload" => ScalarMutation(root, candidate, column, new byte[] { 123, 125 }, DBNull.Value),
            "finalization_receipt_identity_sha256" => ScalarMutation(root, candidate, column, new string('C', 64), DBNull.Value),
            "submission_payload" => ScalarMutation(root, candidate, column, new byte[] { 123, 125 }, DBNull.Value),
            "submission_identity_sha256" => ScalarMutation(root, candidate, column, new string('D', 64), DBNull.Value),
            "acknowledgement_payload" => ScalarMutation(root, candidate, column, new byte[] { 123, 125 }, DBNull.Value),
            "acknowledgement_payload_sha256" => ScalarMutation(root, candidate, column, new string('E', 64), DBNull.Value),
            "source_hold_released" => ScalarMutation(root, candidate, column, 1, 0),
            "quarantine_reason" => ScalarMutation(root, candidate, column, "column-race", DBNull.Value),
            "timeout_unix_ms" => IncrementMutation(root, candidate, column),
            "created_unix_ms" => IncrementMutation(root, candidate, column),
            "updated_unix_ms" => IncrementMutation(root, candidate, column),
            "candidate_state" => ScalarMutation(root, candidate, column, "PendingContext", DBNull.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
    }

    private static ColumnMutation EventMutation(
        string root,
        TransientCandidateReservation reservation,
        string column)
    {
        var eventId = reservation.EventId.ToString("N");
        if (column == "event_id")
        {
            var changed = Guid.NewGuid().ToString("N");
            return new(
                () => ExecuteSync(root, EventIdentitySql, ("$from", eventId), ("$to", changed)),
                () => ExecuteSync(root, EventIdentitySql, ("$from", changed), ("$to", eventId)));
        }
        return column switch
        {
            "agent_id" => EventScalarMutation(root, eventId, column, "changed-agent", reservation.AgentId),
            "created_unix_ms" => EventIncrementMutation(root, eventId),
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
    }

    private static ColumnMutation ConflictMutation(
        string root,
        TransientCandidateReservation reservation,
        string column)
        => column switch
        {
            "conflict_id" => ConflictScalarMutation(root, "conflict_id", 2, 1),
            "candidate_id" => ConflictScalarMutation(
                root, "candidate_id", Guid.NewGuid().ToString("N"), reservation.CandidateId.ToString("N")),
            "event_id" => ConflictScalarMutation(
                root, "event_id", Guid.NewGuid().ToString("N"), reservation.EventId.ToString("N")),
            "reason" => ConflictScalarMutation(root, "reason", "changed-reason", "column-race"),
            "observed_unix_ms" => ConflictScalarMutation(root, "observed_unix_ms", 2, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };

    private static ColumnMutation ScalarMutation(
        string root,
        string candidate,
        string column,
        object changed,
        object original)
    {
        EnsureCandidateColumn(column);
        return new(
            () => ExecuteSync(root,
                $"UPDATE transient_candidates SET {column} = $value WHERE candidate_id = $candidate;",
                ("$value", changed), ("$candidate", candidate)),
            () => ExecuteSync(root,
                $"UPDATE transient_candidates SET {column} = $value WHERE candidate_id = $candidate;",
                ("$value", original), ("$candidate", candidate)));
    }

    private static ColumnMutation IncrementMutation(string root, string candidate, string column)
    {
        EnsureCandidateColumn(column);
        return new(
            () => ExecuteSync(root,
                $"UPDATE transient_candidates SET {column} = {column} + 1 WHERE candidate_id = $candidate;",
                ("$candidate", candidate)),
            () => ExecuteSync(root,
                $"UPDATE transient_candidates SET {column} = {column} - 1 WHERE candidate_id = $candidate;",
                ("$candidate", candidate)));
    }

    private static ColumnMutation EventScalarMutation(
        string root,
        string eventId,
        string column,
        object changed,
        object original)
    {
        EnsureEventColumn(column);
        return new(
            () => ExecuteSync(root,
                $"UPDATE transient_event_identities SET {column} = $value WHERE event_id = $event;",
                ("$value", changed), ("$event", eventId)),
            () => ExecuteSync(root,
                $"UPDATE transient_event_identities SET {column} = $value WHERE event_id = $event;",
                ("$value", original), ("$event", eventId)));
    }

    private static ColumnMutation EventIncrementMutation(string root, string eventId)
        => new(
            () => ExecuteSync(root,
                "UPDATE transient_event_identities SET created_unix_ms = created_unix_ms + 1 WHERE event_id = $event;",
                ("$event", eventId)),
            () => ExecuteSync(root,
                "UPDATE transient_event_identities SET created_unix_ms = created_unix_ms - 1 WHERE event_id = $event;",
                ("$event", eventId)));

    private static ColumnMutation ConflictScalarMutation(
        string root,
        string column,
        object changed,
        object original)
    {
        EnsureConflictColumn(column);
        return new(
            () => ExecuteSync(root,
                $"UPDATE transient_candidate_conflicts SET {column} = $value WHERE conflict_id = 1;",
                ("$value", changed)),
            () => ExecuteSync(root,
                $"UPDATE transient_candidate_conflicts SET {column} = $value WHERE {(column == "conflict_id" ? "conflict_id = 2" : "conflict_id = 1")};",
                ("$value", original)));
    }

    private static string ComputeReservationIdentity(TransientCandidateReservation reservation)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-transient-candidate-reservation-v1",
            reservation.CandidateId,
            reservation.EventId,
            reservation.AgentId,
            Mode = "edge",
            Required = false,
            Sources = reservation.Sources
        });

    private static void EnsureCandidateColumn(string column)
    {
        if (column is not ("event_id" or "agent_id" or "mode" or "required" or
            "reservation_identity_sha256" or "state" or "phase" or "candidate_payload" or
            "candidate_payload_sha256" or "finalization_payload" or
            "finalization_receipt_identity_sha256" or "submission_payload" or
            "submission_identity_sha256" or "acknowledgement_payload" or
            "acknowledgement_payload_sha256" or "source_hold_released" or "quarantine_reason" or
            "timeout_unix_ms" or "created_unix_ms" or "updated_unix_ms" or "candidate_state"))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
    }

    private static void EnsureEventColumn(string column)
    {
        if (column is not ("agent_id" or "created_unix_ms"))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
    }

    private static void EnsureConflictColumn(string column)
    {
        if (column is not ("conflict_id" or "candidate_id" or "event_id" or "reason" or "observed_unix_ms"))
        {
            throw new ArgumentOutOfRangeException(nameof(column));
        }
    }

    private static void ExecuteSync(
        string root,
        string sql,
        params (string Name, object Value)[] parameters)
        => ExecuteAsync(root, sql, parameters).GetAwaiter().GetResult();

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Column names are restricted by private test whitelists and values remain parameterized.")]
    private static async Task ExecuteAsync(
        string root,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(root, "journal", "raw-ingress.db")}");
        await connection.OpenAsync().ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private const string CandidateIdentitySql = """
        PRAGMA defer_foreign_keys = ON;
        BEGIN IMMEDIATE;
        UPDATE transient_candidate_sources SET candidate_id = $to WHERE candidate_id = $from;
        UPDATE transient_candidates SET candidate_id = $to WHERE candidate_id = $from;
        COMMIT;
        """;

    private const string EventIdentitySql = """
        PRAGMA defer_foreign_keys = ON;
        BEGIN IMMEDIATE;
        UPDATE transient_event_identities SET event_id = $to WHERE event_id = $from;
        UPDATE transient_candidates SET event_id = $to WHERE event_id = $from;
        COMMIT;
        """;

    private sealed record ColumnMutation(Action Mutate, Action Restore);

    private sealed class CyclingFaultInjector(Action mutate, Action restore) : ITransientCandidateFaultInjector
    {
        private int _invocations;

        internal int InvocationCount => Volatile.Read(ref _invocations);

        public void Inject(TransientCandidateFaultPoint point)
        {
            if (point != TransientCandidateFaultPoint.AfterReservationValidation)
            {
                return;
            }
            var invocation = Interlocked.Increment(ref _invocations);
            if (invocation == 1)
            {
                mutate();
            }
            else if (invocation == 2)
            {
                restore();
            }
        }
    }
}
