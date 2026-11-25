using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    private async ValueTask PruneCenteredTrailWorkAsync(SqliteConnection connection, long cutoff,
        IReadOnlySet<string> heldAbsolutePaths, Func<ValueTask> pause, CancellationToken token)
    {
        using var watermark = connection.CreateCommand();
        watermark.CommandText = "SELECT max(revision) FROM processing_centered_view_trail_work;";
        if (await watermark.ExecuteScalarAsync(token).ConfigureAwait(false) is not long throughRevision)
            return;

        using var read = connection.CreateCommand();
        read.CommandText = ReadTrailRetentionPageSql;
        var cursor = read.Parameters.AddWithValue("$after", 0L);
        read.Parameters.AddWithValue("$through", throughRevision);
        read.Parameters.AddWithValue("$limit", CenteredViewPruneBatchSize);
        read.Parameters.AddWithValue("$cutoff", cutoff);
        using var delete = connection.CreateCommand();
        delete.CommandText = DeleteTerminalTrailWorkSql;
        var revision = delete.Parameters.AddWithValue("$revision", 0L);
        var exposureId = delete.Parameters.AddWithValue("$exposure", "");
        var now = delete.Parameters.AddWithValue("$centered_now", 0L);
        delete.Parameters.AddWithValue("$cutoff", cutoff);
        delete.Parameters.AddWithValue("$linearSchema", CenteredExposureProductReader.LinearSchemaVersion);
        delete.Parameters.AddWithValue("$previewNode", CenteredViewPreviewNode);

        var candidates = new List<(long Revision, string ExposureId)>(CenteredViewPruneBatchSize);
        long after = 0;
        long pruned = 0;
        try
        {
            while (after < throughRevision)
            {
                token.ThrowIfCancellationRequested();
                var previous = after;
                cursor.Value = previous;
                candidates.Clear();
                using (var reader = await read.ExecuteReaderAsync(token).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        token.ThrowIfCancellationRequested();
                        after = reader.GetInt64(0);
                        if (await reader.IsDBNullAsync(3, token).ConfigureAwait(false) ||
                            !IsUnheldAndAbsent(reader.GetString(1), heldAbsolutePaths) ||
                            !IsUnheldAndAbsent(reader.GetString(2), heldAbsolutePaths))
                            continue;

                        var exposure = new ExposureKey(new(reader.GetString(3), reader.GetString(4)),
                            reader.GetGuid(5), reader.GetGuid(6));
                        candidates.Add((after, SqliteCenteredViews.ExposureIdentity(exposure)));
                    }
                }
                if (after == previous)
                    break;

                if (candidates.Count > 0)
                {
                    // Raw file checks run under the lifecycle locks. Recheck journal custody in the write transaction.
#pragma warning disable CA1849 // SQLite exposes immediate transactions only through this overload.
                    using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
                    delete.Transaction = transaction;
                    now.Value = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                    var removed = 0;
                    foreach (var candidate in candidates)
                    {
                        token.ThrowIfCancellationRequested();
                        revision.Value = candidate.Revision;
                        exposureId.Value = candidate.ExposureId;
                        removed += await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                    token.ThrowIfCancellationRequested();
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    pruned += removed;
                    delete.Transaction = null;
                }
                if (after < throughRevision || candidates.Count > 0)
                    await pause().ConfigureAwait(false);
            }
        }
        finally
        {
            if (pruned > 0)
                LogCenteredTrailWorkPruned(_logger, pruned);
        }
    }

    [LoggerMessage(EventId = 2549, Level = LogLevel.Information, Message = "Pruned {TrailWorkCount} expired centered-view trail work records from the journal.")]
    private static partial void LogCenteredTrailWorkPruned(ILogger logger, long trailWorkCount);

    // Page by revision before filtering so retained and pending history also has a bounded read cost.
    private const string ReadTrailRetentionPageSql = """
        WITH page AS MATERIALIZED (
            SELECT revision, capture_id, consumed FROM processing_centered_view_trail_work
            WHERE revision > $after AND revision <= $through
            ORDER BY revision LIMIT $limit
        )
        SELECT page.revision, raw.payload_relative_path, raw.sidecar_relative_path,
               run.agent_id, run.camera_id, run.run_id, raw.capture_id
        FROM page
        LEFT JOIN raw_captures raw ON raw.capture_id = page.capture_id AND page.consumed = 1
            AND raw.exposure_started_unix_ms <= $cutoff AND raw.retention_hold = 0
        LEFT JOIN centered_exposure_attempts attempt ON attempt.capture_id = raw.capture_id
        LEFT JOIN centered_exposure_runs run ON run.run_id = attempt.run_id
        ORDER BY page.revision;
        """;

    // A later view can rebuild from the trail projection while the publication inputs survive.
    // Any retained base can use this source, including a stack centered on another exposure.
    private const string DeleteTerminalTrailWorkSql = $$"""
        DELETE FROM processing_centered_view_trail_work AS work
        WHERE work.revision = $revision AND work.consumed = 1
          AND EXISTS (
              SELECT 1 FROM raw_captures raw
              WHERE raw.capture_id = work.capture_id
                AND raw.exposure_started_unix_ms <= $cutoff AND NOT ({{SqliteRawCaptureJournal.RawRetentionHoldPredicate}})
          ) AND NOT EXISTS (
              SELECT 1 FROM processing_centered_view_trail_work pending
              WHERE pending.capture_id = work.capture_id AND pending.consumed = 0
          ) AND NOT EXISTS (
              SELECT 1 FROM processing_centered_view_members member WHERE member.exposure_id = $exposure
          ) AND NOT EXISTS (
              SELECT 1 FROM centered_exposure_products product
              JOIN processing_outputs output ON output.output_identity_sha256 = product.output_identity
              WHERE product.capture_id = work.capture_id AND product.schema_version = $linearSchema
          ) AND NOT EXISTS (
              SELECT 1 FROM processing_outputs preview
              WHERE preview.capture_id = work.capture_id AND preview.node_id = $previewNode
          );
        """;
}
