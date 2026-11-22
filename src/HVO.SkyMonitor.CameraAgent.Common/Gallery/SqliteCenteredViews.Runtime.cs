using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews
{
    // Before closure, use the same committed observation and inclusive cutoff as the calendar.
    // Once frozen, the immutable center supplies the decision.
    private const string SingleOutsideCenterSql = $$"""
        SELECT 1
        FROM processing_centered_attempt_calendar calendar
        JOIN processing_centered_windows window ON window.window_sha256 = calendar.window_sha256
        WHERE calendar.attempt_id = attempt.attempt_id AND calendar.run_id = attempt.run_id
            AND CASE WHEN window.closure_json IS NULL THEN NOT EXISTS (
                SELECT 1 FROM (
                    SELECT calendar.admitted_ticks, {{CenteredWindowRuntimeJournal.MatchingProductObservationTicksSql}} AS product_ticks
                    FROM centered_exposure_runs run
                    JOIN raw_captures raw ON raw.capture_id = attempt.capture_id AND raw.state = 'committed'
                        AND raw.agent_id = run.agent_id
                    WHERE run.run_id = attempt.run_id
                )
                WHERE {{CenteredWindowRuntimeJournal.EarliestAdmissionObservationTicksSql}} <= window.deadline_utc_ticks
            )
            WHEN window.terminal = 1 AND json_valid(CAST(window.closure_json AS TEXT))
                THEN json_extract(CAST(window.closure_json AS TEXT), '$.slots[' || $centerIndex || '].input') IS NULL
            END
        """;

    internal async ValueTask<string?> ReadLiveCandidateAsync(CenteredViewStream stream, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, $$"""
            SELECT b.base_id
            FROM processing_centered_view_bases b
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = lower(replace(b.reference_id, '-', ''))
            WHERE b.stream_id = $stream AND b.recomputed_from IS NULL AND attempt.run_id = $run
                AND (b.kind <> $single OR NOT EXISTS ({{SingleOutsideCenterSql}}))
                AND ((b.kind = $single AND attempt.ordinal <= 3) OR
                    EXISTS (SELECT 1 FROM processing_centered_view_window_work work WHERE work.live_base_id = b.base_id))
            ORDER BY b.reference_order DESC, b.kind DESC LIMIT 1;
            """, ("$stream", StreamId(stream)), ("$run", stream.RunId.ToString("N")), ("$single", (int)CenteredViewKind.Single),
            ("$centerIndex", CenteredWindowScheduler.CenterIndex));
        return (string?)await command.ExecuteScalarAsync(token).ConfigureAwait(false);
    }

    private static async ValueTask<bool> IsSingleOutsideCenterAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid runId, Guid captureId, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, $$"""
            SELECT 1
            FROM centered_exposure_attempts attempt
            WHERE attempt.capture_id = $capture AND attempt.run_id = $run AND EXISTS ({{SingleOutsideCenterSql}});
            """, ("$capture", captureId.ToString("N")), ("$run", runId.ToString("N")),
            ("$centerIndex", CenteredWindowScheduler.CenterIndex));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    internal async ValueTask<bool> IsPublishedSingleFallbackAsync(CenteredViewBase selected, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT 1
            FROM processing_centered_view_window_work work
            JOIN processing_centered_windows window ON window.window_sha256 = work.window_sha256
            WHERE work.live_base_id = $base AND window.terminal = 1 AND window.reference_capture_id = $reference
                AND CASE WHEN json_valid(CAST(window.publication_json AS TEXT))
                    THEN json_extract(CAST(window.publication_json AS TEXT), '$.stackDisposition') = $singleExposure
                END
            LIMIT 1;
            """, ("$base", selected.BaseId), ("$reference", selected.Definition.Reference.Capture.CaptureId.ToString("N")),
            ("$singleExposure", (int)CenteredStackDisposition.SingleExposure));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    internal async ValueTask<IReadOnlyList<string>> ReadDueWindowPublicationsAsync(int limit, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT window.window_sha256
            FROM processing_centered_windows window
            LEFT JOIN processing_centered_view_window_work work ON work.window_sha256 = window.window_sha256
            WHERE window.terminal = 1 AND window.reference_capture_id IS NOT NULL
                AND (work.window_sha256 IS NULL OR work.retry_utc <= $now)
            ORDER BY window.deadline_utc_ticks, window.window_sha256 LIMIT $limit;
            """, ("$now", clock.GetUtcNow().UtcTicks), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var values = new List<string>(limit);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(reader.GetString(0));
        return values;
    }

    internal async ValueTask CheckpointWindowAsync(string windowId, string? liveBaseId,
        TimeSpan? retryAfter, string? terminalReason, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            INSERT INTO processing_centered_view_window_work(window_sha256, live_base_id, retry_utc, terminal_reason)
            VALUES ($window, $base, $retry, $reason)
            ON CONFLICT(window_sha256) DO UPDATE SET
                live_base_id = COALESCE(excluded.live_base_id, live_base_id),
                retry_utc = excluded.retry_utc,
                terminal_reason = excluded.terminal_reason
            WHERE retry_utc IS NOT NULL;
            """, ("$window", windowId), ("$base", liveBaseId),
            ("$retry", retryAfter.HasValue ? (clock.GetUtcNow() + retryAfter.Value).UtcTicks : null), ("$reason", terminalReason));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    internal async ValueTask<long> ReadProductCursorAsync(CameraKey camera, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT consumed_revision FROM processing_centered_view_cursors WHERE agent_id = $agent AND camera_id = $camera;
            """, ("$agent", camera.AgentId), ("$camera", camera.CameraId));
        return (long?)await command.ExecuteScalarAsync(token).ConfigureAwait(false) ?? 0;
    }

    internal async ValueTask<IReadOnlyList<CameraKey>> ReadPublicationCamerasAsync(CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT DISTINCT agent_id, camera_id FROM centered_exposure_runs ORDER BY agent_id, camera_id;
            """);
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var cameras = new List<CameraKey>();
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            cameras.Add(new(reader.GetString(0), reader.GetString(1)));
        return cameras;
    }

    internal async ValueTask<IReadOnlyList<long>> ReadDueProductRetriesAsync(CameraKey camera, int limit, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT revision FROM processing_centered_view_retry
            WHERE agent_id = $agent AND camera_id = $camera AND retry_utc <= $now
            ORDER BY retry_utc, revision LIMIT $limit;
            """, ("$agent", camera.AgentId), ("$camera", camera.CameraId), ("$now", clock.GetUtcNow().UtcTicks), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var revisions = new List<long>(limit);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            revisions.Add(reader.GetInt64(0));
        return revisions;
    }

    internal async ValueTask CheckpointProductAsync(CameraKey camera, long revision, TimeSpan? retryAfter,
        bool advanceCursor, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var transaction = BeginWrite(connection);
        if (retryAfter.HasValue)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO processing_centered_view_retry(agent_id, camera_id, revision, retry_utc)
                VALUES ($agent, $camera, $revision, $retry)
                ON CONFLICT(agent_id, camera_id, revision) DO UPDATE SET retry_utc = excluded.retry_utc;
                """, token, ("$agent", camera.AgentId), ("$camera", camera.CameraId), ("$revision", revision),
                ("$retry", (clock.GetUtcNow() + retryAfter.Value).UtcTicks)).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync(connection, transaction, """
                DELETE FROM processing_centered_view_retry
                WHERE agent_id = $agent AND camera_id = $camera AND revision = $revision;
                """, token, ("$agent", camera.AgentId), ("$camera", camera.CameraId), ("$revision", revision)).ConfigureAwait(false);
        }
        if (advanceCursor)
        {
            await ExecuteAsync(connection, transaction, """
                INSERT INTO processing_centered_view_cursors(agent_id, camera_id, consumed_revision)
                VALUES ($agent, $camera, $revision)
                ON CONFLICT(agent_id, camera_id) DO UPDATE SET consumed_revision = excluded.consumed_revision
                WHERE consumed_revision < excluded.consumed_revision;
                """, token, ("$agent", camera.AgentId), ("$camera", camera.CameraId), ("$revision", revision)).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        await transaction.CommitAsync(token).ConfigureAwait(false);
    }
}
