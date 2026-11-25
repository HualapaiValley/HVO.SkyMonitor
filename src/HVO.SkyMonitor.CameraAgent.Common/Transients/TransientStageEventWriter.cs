using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

/// <summary>
/// Inserts one milestone in the same transaction as the authoritative state change. The
/// composite key rejects a contradictory retry; an identical retry is idempotent. Empty
/// candidate IDs denote capture-level events, never a fabricated candidate.
/// </summary>
internal static class TransientStageEventWriter
{
    internal static async ValueTask RecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawCaptureRowId,
        string stageKey,
        Guid? candidateId,
        string state,
        string source,
        DateTimeOffset recordedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO raw_capture_stage_events (
                raw_capture_row_id, stage_key, candidate_id, state, source, event_unix_ms)
            VALUES ($raw, $stage, $candidate, $state, $source, $time)
            ON CONFLICT (raw_capture_row_id, candidate_id, stage_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$raw", rawCaptureRowId);
        command.Parameters.AddWithValue("$stage", stageKey);
        command.Parameters.AddWithValue("$candidate", candidateId?.ToString("N") ?? string.Empty);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$time", recordedUtc.ToUnixTimeMilliseconds());
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0)
        {
            // The raw stage receipt prevents replay even after retention prunes consumed trail work.
            if (stageKey is "causal-scan" or "candidate-persisted" or "event-finalized" or "central-acknowledged")
                await RecordCenteredTrailChangeAsync(connection, transaction, rawCaptureRowId, stageKey, state, candidateId,
                    recordedUtc, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT state, source FROM raw_capture_stage_events
            WHERE raw_capture_row_id = $raw AND candidate_id = $candidate AND stage_key = $stage;
            """;
        existing.Parameters.AddWithValue("$raw", rawCaptureRowId);
        existing.Parameters.AddWithValue("$candidate", candidateId?.ToString("N") ?? string.Empty);
        existing.Parameters.AddWithValue("$stage", stageKey);
        using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != state || reader.GetString(1) != source)
        {
            throw new InvalidDataException("A transient stage event key conflicts with its durable outcome.");
        }
    }

    private static async ValueTask RecordCenteredTrailChangeAsync(SqliteConnection connection, SqliteTransaction transaction,
        long rawCaptureRowId, string stageKey, string state, Guid? candidateId, DateTimeOffset observedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Legacy journals and raw-only recovery do not require the processing tables.
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE name = 'processing_centered_view_trail_work' AND type = 'table');
            """;
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not 1L)
            return;

        command.CommandText = """
            INSERT INTO processing_centered_view_trail_work(capture_id, candidate_id, stage_key, stage_state, observed_utc)
            SELECT raw.capture_id, $candidate, $stage, $state, $utc
            FROM raw_captures raw
            JOIN centered_exposure_attempts admission ON admission.capture_id = raw.capture_id
            WHERE raw.raw_capture_row_id = $raw
            ON CONFLICT(capture_id, candidate_id, stage_key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$raw", rawCaptureRowId);
        command.Parameters.AddWithValue("$stage", stageKey);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$candidate", candidateId?.ToString("N") ?? string.Empty);
        command.Parameters.AddWithValue("$utc", observedUtc.UtcTicks);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
