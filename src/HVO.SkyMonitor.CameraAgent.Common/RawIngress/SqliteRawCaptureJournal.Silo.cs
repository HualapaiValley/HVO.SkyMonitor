using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

internal sealed partial class SqliteRawCaptureJournal
{
    private const string SiloRawEntryColumns = """
        raw.agent_id, raw.capture_sequence, raw.capture_id, raw.raw_artifact_id,
        raw.descriptor_sha256, raw.manifest_sha256, raw.payload_sha256, raw.payload_length,
        raw.payload_relative_path, raw.sidecar_relative_path, raw.manifest_json,
        raw.exposure_started_unix_ms, raw.durable_ingress_unix_ms, raw.state, raw.retention_hold, raw.evidence_origin
        """;
    private const string SiloAdmissionSql = """
        CREATE TABLE silo_raw_admissions (
            raw_capture_row_id INTEGER PRIMARY KEY REFERENCES raw_captures(raw_capture_row_id),
            staged_bytes INTEGER NOT NULL CHECK (staged_bytes >= 0)
        ) STRICT;
        """;

    private static async Task TrackSiloAdmissionAsync(SqliteConnection connection, SqliteTransaction transaction, long rowId,
        RawIngressJournalEntry entry, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO silo_raw_admissions(raw_capture_row_id, staged_bytes) VALUES ($row, $bytes);";
        command.Parameters.AddWithValue("$row", rowId);
        command.Parameters.AddWithValue("$bytes", checked(entry.PayloadLength + entry.ManifestJson.LongLength));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask EnsureSiloStagingCapacityAsync(long bytes, long maximumBytes, int maximumFiles,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), COALESCE(SUM(staged_bytes), 0) FROM silo_raw_admissions;";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (reader.GetInt64(0) >= maximumFiles / 2 || bytes > maximumBytes - reader.GetInt64(1))
            throw new CaptureLaneBackpressureException("The Silo raw admission spool has reached its configured capacity.");
    }

    internal async Task<IReadOnlyList<RawIngressJournalEntry>> ReadPendingSiloAdmissionsAsync(int limit,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SiloRawEntryColumns},
                EXISTS (SELECT 1 FROM raw_capture_stage_events event
                    WHERE event.raw_capture_row_id = raw.raw_capture_row_id AND event.stage_key = 'raw-evidence-written-off')
            FROM silo_raw_admissions pending JOIN raw_captures raw ON raw.raw_capture_row_id = pending.raw_capture_row_id
            ORDER BY pending.raw_capture_row_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<RawIngressJournalEntry>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            entries.Add(ReadEntry(reader) with { EvidenceWrittenOff = reader.GetBoolean(16) });
        return entries;
    }

    internal async Task<IReadOnlyList<(long RowId, string ArtifactId)>> ReadPendingSiloAdmissionKeysAsync(
        long afterRowId, int limit, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pending.raw_capture_row_id, raw.raw_artifact_id
            FROM silo_raw_admissions pending JOIN raw_captures raw ON raw.raw_capture_row_id = pending.raw_capture_row_id
            WHERE pending.raw_capture_row_id > $after ORDER BY pending.raw_capture_row_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$after", afterRowId);
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<(long, string)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            entries.Add((reader.GetInt64(0), reader.GetString(1)));
        return entries;
    }

    internal async Task<RawIngressJournalEntry?> ReadSiloAdmissionByArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SiloRawEntryColumns},
                EXISTS (SELECT 1 FROM raw_capture_stage_events event
                    WHERE event.raw_capture_row_id = raw.raw_capture_row_id AND event.stage_key = 'raw-evidence-written-off'),
                (SELECT COUNT(*) FROM raw_capture_stage_events event
                    WHERE event.raw_capture_row_id = raw.raw_capture_row_id AND event.stage_key GLOB 'raw-evidence-release.*'
                        AND event.state = 'Revalidate:evidence-restored')
            FROM raw_captures raw WHERE raw.raw_artifact_id = $artifact;
            """;

        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadEntry(reader) with { EvidenceWrittenOff = reader.GetBoolean(16), AdmissionRevalidationCount = reader.GetInt64(17) }
            : null;
    }

    internal async Task<bool> SettleSiloAdmissionAsync(Guid captureId, string manifestSha256, bool siloVisible,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$hash", manifestSha256);
        var evidenceRestored = false;
        var lanesReleased = false;
        if (siloVisible)
        {
            // Check the current disposition in SQLite because publication releases the raw lifecycle lock.
            command.CommandText = """
                SELECT raw.raw_capture_row_id FROM raw_captures raw
                JOIN silo_raw_admissions pending ON pending.raw_capture_row_id = raw.raw_capture_row_id
                WHERE capture_id = $capture AND manifest_sha256 = $hash
                    AND NOT EXISTS (SELECT 1 FROM raw_capture_stage_events event
                        WHERE event.raw_capture_row_id = raw.raw_capture_row_id AND event.candidate_id = ''
                          AND event.stage_key IN ('raw-evidence-written-off', 'raw-evidence-silo-published-failure'));
                """;
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long rowId)
            {
                command.Parameters.AddWithValue("$raw", rowId);
                var now = _utcNow();
                command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                command.CommandText = """
                    UPDATE raw_captures SET state = 'committed', failure_reason = NULL
                    WHERE raw_capture_row_id = $raw AND state IN ('missing_evidence', 'quarantined');
                    """;
                evidenceRestored = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
                lanesReleased = await RestoreEvidenceLanesAsync(command, leasedWorkId: null, cancellationToken)
                    .ConfigureAwait(false);
                if (evidenceRestored || lanesReleased)
                {
                    var hasExecutionPins = await HasExecutionPinsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await SqliteCaptureLaneStore.RecomputeRetentionHoldAsync(
                        connection, transaction, rowId, hasExecutionPins, cancellationToken).ConfigureAwait(false);
                    if (lanesReleased)
                    {
                        await SqliteCaptureLaneStore.RecomputeRequiredPressureAsync(
                            connection, transaction, _distributionOptions, now, cancellationToken).ConfigureAwait(false);
                    }
                    await RecordSiloEvidenceRestorationAsync(command, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        command.CommandText = """
            DELETE FROM silo_raw_admissions WHERE raw_capture_row_id = (
                SELECT raw_capture_row_id FROM raw_captures WHERE capture_id = $capture AND manifest_sha256 = $hash);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return evidenceRestored || lanesReleased;
    }

    internal static async Task RecordSiloEvidenceRestorationAsync(SqliteCommand command, CancellationToken cancellationToken,
        string? laneStageKey = null)
    {
        command.CommandText = """
            INSERT INTO raw_capture_stage_events(raw_capture_row_id, stage_key, state, source, event_unix_ms)
            VALUES ($raw, $stage, 'evidence-restored', 'silo-publication', $now);
            """;
        command.Parameters.AddWithValue("$stage", laneStageKey ?? $"raw-evidence-silo-restored.{Guid.NewGuid():N}");
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task RecordPublishedSiloEvidenceFailureAsync(SqliteConnection connection, SqliteTransaction transaction,
        long rawRowId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // A lease can observe P4 before the publisher reacquires the lifecycle gate. Preserve failures
        // of the exact published pair across settlement, reclaim replay and sibling lease completion.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO raw_capture_stage_events(raw_capture_row_id, stage_key, state, source, event_unix_ms)
            VALUES ($raw, 'raw-evidence-silo-published-failure', 'evidence-failed', 'silo-publication', $now);
            """;
        command.Parameters.AddWithValue("$raw", rawRowId);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> HasPendingSiloAdmissionAsync(Guid captureId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (SELECT 1 FROM silo_raw_admissions pending
                JOIN raw_captures raw ON raw.raw_capture_row_id = pending.raw_capture_row_id WHERE raw.capture_id = $capture);
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    internal async Task<IReadOnlyList<Guid>> ReadSiloAdmissionHoldsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT raw.raw_artifact_id
            FROM silo_raw_admissions pending JOIN raw_captures raw ON raw.raw_capture_row_id = pending.raw_capture_row_id
            ORDER BY pending.raw_capture_row_id;
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var holds = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            holds.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return holds;
    }
}
