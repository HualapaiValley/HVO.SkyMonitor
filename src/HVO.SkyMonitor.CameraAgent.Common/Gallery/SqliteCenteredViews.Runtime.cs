using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
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
        var stream = selected.Definition.Stream;
        var reference = selected.Definition.Reference.Capture.CaptureId;
        // A completed publisher decision can select a Single even when registration produced no receipt.
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT 1
            FROM processing_centered_view_window_work work
            JOIN processing_centered_windows window ON window.window_sha256 = work.window_sha256
            JOIN processing_centered_view_bases b ON b.base_id = work.live_base_id
            WHERE b.base_id = $base AND b.stream_id = $stream AND b.kind = $single AND b.recomputed_from IS NULL
                AND window.terminal = 1 AND window.reference_capture_id = $reference
                AND lower(replace(b.reference_id, '-', '')) = window.reference_capture_id
                AND work.retry_utc IS NULL AND work.terminal_reason IS NULL
                AND CASE WHEN json_valid(CAST(window.closure_json AS TEXT)) THEN
                    json_extract(CAST(window.closure_json AS TEXT), $center || '.captureId') = $capture
                    AND json_extract(CAST(window.closure_json AS TEXT), $center || '.runId') = $run
                END
            LIMIT 1;
            """, ("$base", selected.BaseId), ("$stream", StreamId(stream)), ("$single", (int)CenteredViewKind.Single),
            ("$reference", reference.ToString("N")), ("$capture", reference.ToString("D")), ("$run", stream.RunId.ToString("D")),
            ("$center", $"$.slots[{CenteredWindowScheduler.CenterIndex}].input.key"));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is not null;
    }

    internal const string DueWindowPublicationsSql = """
        SELECT work.window_sha256
        FROM processing_centered_view_window_work work
        JOIN processing_centered_windows window ON window.window_sha256 = work.window_sha256
        WHERE work.retry_utc IS NOT NULL AND work.retry_utc <= $now
            AND window.terminal = 1 AND window.reference_capture_id IS NOT NULL
        ORDER BY window.deadline_utc_ticks, work.window_sha256
        LIMIT $limit;
        """;

    internal readonly record struct CenterSingleResolution(string? BaseId, bool SettledWithoutBase);

    internal async ValueTask<IReadOnlyList<string>> ReadDueWindowPublicationsAsync(int limit, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, DueWindowPublicationsSql,
            ("$now", clock.GetUtcNow().UtcTicks), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var values = new List<string>(limit);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(reader.GetString(0));
        return values;
    }

    internal async ValueTask<CenterSingleResolution> ReadCenterSingleResolutionAsync(
        CenteredViewStream stream, Guid referenceCaptureId, long? settledLinearRevision, CancellationToken token)
    {
        var streamId = StreamId(stream);
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT
                (SELECT b.base_id
                 FROM processing_centered_view_bases b
                 WHERE b.stream_id = $stream AND b.reference_id = $reference
                   AND b.kind = $single AND b.recomputed_from IS NULL
                 LIMIT 1),
                CASE WHEN $linearRevision IS NOT NULL
                    AND COALESCE((SELECT c.consumed_revision
                                  FROM processing_centered_view_cursors c
                                  WHERE c.agent_id = $agent AND c.camera_id = $camera), 0) >= $linearRevision
                    AND NOT EXISTS (
                        SELECT 1 FROM processing_centered_view_retry r
                        WHERE r.agent_id = $agent AND r.camera_id = $camera AND r.revision = $linearRevision
                    )
                    AND NOT EXISTS (
                        SELECT 1 FROM centered_exposure_products p
                        WHERE p.capture_id = $captureHex AND p.schema_version = $linearSchema
                          AND p.revision > $linearRevision
                    )
                    THEN 1 ELSE 0 END;
            """,
            ("$stream", streamId), ("$reference", referenceCaptureId), ("$single", (int)CenteredViewKind.Single),
            ("$agent", stream.Camera.AgentId), ("$camera", stream.Camera.CameraId),
            ("$linearRevision", settledLinearRevision),
            ("$captureHex", referenceCaptureId.ToString("N")),
            ("$linearSchema", CenteredExposureProductReader.LinearSchemaVersion));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false))
            return default;
        var baseId = ReadNullableText(reader, 0);
        return new(baseId, baseId is null && reader.GetInt64(1) != 0);
    }

    internal async ValueTask<bool> CheckpointMissingCenterRetryAsync(
        string windowId, TimeSpan retryAfter, int maximumRetries, string terminalReason, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRetries);
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        var nextRetry = (clock.GetUtcNow() + retryAfter).UtcTicks;
        using var command = CreateCommand(connection, null, """
            INSERT INTO processing_centered_view_window_work(window_sha256, live_base_id, retry_utc, terminal_reason, retry_count)
            VALUES (
                $window,
                NULL,
                CASE WHEN 1 >= $maxRetries THEN NULL ELSE $retry END,
                CASE WHEN 1 >= $maxRetries THEN $reason ELSE NULL END,
                1
            )
            ON CONFLICT(window_sha256) DO UPDATE SET
                retry_utc = CASE WHEN retry_count + 1 >= $maxRetries THEN NULL ELSE $retry END,
                terminal_reason = CASE WHEN retry_count + 1 >= $maxRetries THEN $reason ELSE NULL END,
                retry_count = retry_count + 1
            WHERE retry_utc IS NOT NULL
            RETURNING terminal_reason IS NOT NULL;
            """,
            ("$window", windowId), ("$retry", nextRetry), ("$maxRetries", maximumRetries), ("$reason", terminalReason));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is 1L;
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
