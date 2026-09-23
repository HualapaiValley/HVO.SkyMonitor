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
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 0) return;
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = """
            SELECT state, source, event_unix_ms FROM raw_capture_stage_events
            WHERE raw_capture_row_id = $raw AND candidate_id = $candidate AND stage_key = $stage;
            """;
        existing.Parameters.AddWithValue("$raw", rawCaptureRowId);
        existing.Parameters.AddWithValue("$candidate", candidateId?.ToString("N") ?? string.Empty);
        existing.Parameters.AddWithValue("$stage", stageKey);
        using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
            reader.GetString(0) != state || reader.GetString(1) != source ||
            reader.GetInt64(2) != recordedUtc.ToUnixTimeMilliseconds())
        {
            throw new InvalidDataException("A transient stage event key conflicts with its durable outcome.");
        }
    }
}
