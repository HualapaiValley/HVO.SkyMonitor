using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed record CenteredViewTrailWork(ExposureKey Exposure, long Revision, DateTimeOffset ObservedUtc, string Stage, string State);
internal sealed record CenteredViewTrailEvidence(ExposureProductLookup? Source, CenteredViewArtifact Pin, bool Incomplete, string? Reason);

internal sealed partial class SqliteCenteredViews
{
    internal async ValueTask<IReadOnlyList<CenteredViewTrailWork>> ReadDueTrailWorkAsync(int limit, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT run.agent_id, run.camera_id, run.run_id, work.capture_id, work.revision, work.observed_utc, work.stage_key, work.stage_state
            FROM processing_centered_view_trail_work work
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = work.capture_id
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            WHERE work.consumed = 0 AND (work.retry_utc IS NULL OR work.retry_utc <= $now)
                AND work.revision = (SELECT MAX(latest.revision) FROM processing_centered_view_trail_work latest
                    WHERE latest.capture_id = work.capture_id)
            ORDER BY work.revision LIMIT $limit;
            """, ("$now", clock.GetUtcNow().UtcTicks), ("$limit", limit));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        var work = new List<CenteredViewTrailWork>(limit);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            work.Add(ReadTrailWork(reader));
        return work;
    }

    internal async ValueTask<CenteredViewTrailWork?> ReadLatestTrailWorkAsync(ExposureKey exposure, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT run.agent_id, run.camera_id, run.run_id, work.capture_id, work.revision, work.observed_utc, work.stage_key, work.stage_state
            FROM processing_centered_view_trail_work work
            JOIN centered_exposure_attempts attempt ON attempt.capture_id = work.capture_id
            JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
            WHERE work.capture_id = $capture AND run.run_id = $run AND run.agent_id = $agent AND run.camera_id = $camera
            ORDER BY work.revision DESC LIMIT 1;
            """, ("$capture", exposure.CaptureId.ToString("N")), ("$run", exposure.RunId.ToString("N")),
            ("$agent", exposure.Camera.AgentId), ("$camera", exposure.Camera.CameraId));
        using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false) ? ReadTrailWork(reader) : null;
    }

    internal async ValueTask CheckpointTrailWorkAsync(CenteredViewTrailWork work, TimeSpan? retryAfter, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            UPDATE processing_centered_view_trail_work
            SET consumed = CASE WHEN $retry IS NULL THEN 1 ELSE 0 END, retry_utc = $retry
            WHERE capture_id = $capture AND consumed = 0
                AND (revision = $revision OR ($retry IS NULL AND revision < $revision));
            """, ("$capture", work.Exposure.CaptureId.ToString("N")), ("$revision", work.Revision),
            ("$retry", retryAfter is { } delay ? (clock.GetUtcNow() + delay).UtcTicks : null));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    internal async ValueTask<CenteredViewTrailEvidence?> ReadTrailEvidenceAsync(CenteredViewTrailWork work, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        return await ReadTrailEvidenceAsync(connection, null, work, token).ConfigureAwait(false);
    }

    internal async ValueTask<CenteredViewTrailEvidence> PinTrailEvidenceAsync(CenteredViewTrailWork work,
        CenteredViewTrailEvidence evidence, CancellationToken token)
    {
        using var connection = await OpenAsync(token).ConfigureAwait(false);
        using var transaction = BeginWrite(connection);
        await ExecuteAsync(connection, transaction, """
            UPDATE processing_centered_view_trail_work SET projection_json = $projection
            WHERE capture_id = $capture AND revision = $revision AND projection_json IS NULL;
            """, token, ("$capture", work.Exposure.CaptureId.ToString("N")), ("$revision", work.Revision),
            ("$projection", Serialize(evidence))).ConfigureAwait(false);
        var pinned = await ReadTrailEvidenceAsync(connection, transaction, work, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("Centered trail work disappeared before projection commit.");
        await transaction.CommitAsync(token).ConfigureAwait(false);
        return pinned;
    }

    private static async ValueTask<CenteredViewTrailEvidence?> ReadTrailEvidenceAsync(SqliteConnection connection,
        SqliteTransaction? transaction, CenteredViewTrailWork work, CancellationToken token)
    {
        using var command = CreateCommand(connection, transaction, """
            SELECT projection_json FROM processing_centered_view_trail_work
            WHERE capture_id = $capture AND revision = $revision;
            """, ("$capture", work.Exposure.CaptureId.ToString("N")), ("$revision", work.Revision));
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string json
            ? Parse<CenteredViewTrailEvidence>(json) : null;
    }

    private static CenteredViewTrailWork ReadTrailWork(SqliteDataReader reader) =>
        new(new(new(reader.GetString(0), reader.GetString(1)), reader.GetGuid(2), reader.GetGuid(3)),
            reader.GetInt64(4), ReadTime(reader, 5), reader.GetString(6), reader.GetString(7));
}
