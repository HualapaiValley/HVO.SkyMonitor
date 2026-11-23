using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    internal const int CenteredViewPruneBatchSize = 128;

    private async ValueTask PruneCenteredViewJournalAsync(
        DateTimeOffset cutoffUtc, IReadOnlySet<string> heldAbsolutePaths,
        CancellationToken cancellationToken, CancellationToken interruptionToken)
    {
        var pruned = 0;
        try
        {
            // Raw retention includes the entire cutoff UTC day. Preview expiry has its own commit cutoff.
            var lastEligibleCaptureTime = new DateTimeOffset(cutoffUtc.UtcDateTime.Date.AddTicks(TimeSpan.TicksPerDay - 1))
                .ToUnixTimeMilliseconds();
            using var connection = await _store.OpenCenteredViewsAsync(cancellationToken).ConfigureAwait(false);
            if (!await StageCenteredViewPruneCandidatesAsync(connection, lastEligibleCaptureTime, heldAbsolutePaths,
                    cancellationToken, interruptionToken).ConfigureAwait(false))
                return;

            // First detach the expired, unpinned links that would otherwise keep a chain of expired views.
            // Then remove recomputations before their original bases. A retained recomputation still pins its parent.
            await PruneBatchesAsync(null).ConfigureAwait(false);
            await PruneBatchesAsync(1).ConfigureAwait(false);
            await PruneBatchesAsync(0).ConfigureAwait(false);

            async ValueTask PruneBatchesAsync(int? recomputed)
            {
                using var batch = connection.CreateCommand();
                batch.CommandText = LoadCenteredViewPruneBatchSql;
                batch.Parameters.AddWithValue("$limit", CenteredViewPruneBatchSize);
                batch.Parameters.AddWithValue("$recomputed", (object?)recomputed ?? DBNull.Value);
                var after = batch.Parameters.AddWithValue("$after", "");
                using var prune = connection.CreateCommand();
#pragma warning disable CA2100 // Both alternatives are fixed internal SQL constants.
                prune.CommandText = recomputed is null ? DetachExpiredCenteredIntervalsSql : DeleteExpiredCenteredBasesSql;
#pragma warning restore CA2100
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    interruptionToken.ThrowIfCancellationRequested();
                    if (await batch.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string lastBase)
                        break;
                    after.Value = lastBase;
#pragma warning disable CA1849 // This SQLite overload acquires the main journal write lock up front.
                    using (var transaction = connection.BeginTransaction(deferred: false))
#pragma warning restore CA1849
                    {
                        prune.Transaction = transaction;
                        var removed = await prune.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                        interruptionToken.ThrowIfCancellationRequested();
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        pruned += removed is long count ? checked((int)count) : 0;
                        prune.Transaction = null;
                        if (removed is long and > 0)
                            _faultInjector.Inject(CaptureProcessingFaultPoint.AfterCenteredViewPruneBatchCommit, "centered-view-retention");
                    }
                }
            }
        }
        catch (Exception exception) when (exception is SqliteException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Artifact retention has already completed. Retry the remaining view cleanup on the next sweep.
            LogCenteredViewPruneFailed(_logger, exception);
        }
        finally
        {
            if (pruned > 0)
                LogCenteredViewsPruned(_logger, pruned);
        }
    }

    private async ValueTask<bool> StageCenteredViewPruneCandidatesAsync(
        SqliteConnection connection, long lastEligibleCaptureTime, IReadOnlySet<string> heldAbsolutePaths,
        CancellationToken cancellationToken, CancellationToken interruptionToken)
    {
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = CenteredViewPruneSetupSql;
            await setup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using var candidates = connection.CreateCommand();
        candidates.CommandText = ReadCenteredViewPruneCandidatesSql;
        var cursor = candidates.Parameters.AddWithValue("$after", "");
        candidates.Parameters.AddWithValue("$cutoff", lastEligibleCaptureTime);
        var pageSize = _lifecycleOptions.ReconciliationBatchSize;
        candidates.Parameters.AddWithValue("$limit", pageSize);
        using var stage = connection.CreateCommand();
        stage.CommandText = """
            INSERT INTO temp.centered_view_expiration_candidates (base_id, capture_id, preview_id, recomputed)
            VALUES ($base, $capture, $preview, $recomputed);
            """;
        var stageId = stage.Parameters.AddWithValue("$base", "");
        var captureId = stage.Parameters.AddWithValue("$capture", "");
        var previewId = stage.Parameters.AddWithValue("$preview", "");
        var recomputed = stage.Parameters.AddWithValue("$recomputed", 0);
        var page = new List<CenteredViewPruneCandidate>(pageSize);
        var staged = false;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            interruptionToken.ThrowIfCancellationRequested();
            page.Clear();
            using (var reader = await candidates.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    page.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetInt32(5)));
            }
            foreach (var row in page)
            {
                interruptionToken.ThrowIfCancellationRequested();
                cursor.Value = row.BaseId;
                if (!IsUnheldAndAbsent(row.Payload, heldAbsolutePaths) || !IsUnheldAndAbsent(row.Sidecar, heldAbsolutePaths))
                    continue;
                stageId.Value = row.BaseId;
                captureId.Value = row.CaptureId;
                previewId.Value = row.PreviewId;
                recomputed.Value = row.Recomputed;
                await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                staged = true;
            }
        }
        while (page.Count == pageSize);

        return staged;
    }

    private bool IsUnheldAndAbsent(string relativePath, IReadOnlySet<string> heldAbsolutePaths)
    {
        var path = ResolveSafePath(relativePath);
        if (heldAbsolutePaths.Contains(path))
            return false;

        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
    }

    private sealed record CenteredViewPruneCandidate(
        string BaseId, string Payload, string Sidecar, string CaptureId, string PreviewId, int Recomputed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Centered-view journal pruning failed. Artifact retention completed and journal pruning will retry on the next sweep.")]
    private static partial void LogCenteredViewPruneFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {BaseCount} expired centered-view bases from the journal.")]
    private static partial void LogCenteredViewsPruned(ILogger logger, int baseCount);

    // Temporary structures are scoped to this connection and reset on pooled reuse. All main-journal writes
    // hold at most one batch, including detaching closed cross-base intervals.
    private const string CenteredViewPruneSetupSql = """
        CREATE TEMP TABLE IF NOT EXISTS centered_view_expiration_candidates (
            base_id TEXT PRIMARY KEY, capture_id TEXT NOT NULL, preview_id TEXT NOT NULL, recomputed INTEGER NOT NULL
        ) STRICT;
        CREATE TEMP TABLE IF NOT EXISTS centered_view_expiration_batch (base_id TEXT PRIMARY KEY) STRICT;
        CREATE TEMP VIEW IF NOT EXISTS centered_view_expiration_eligible AS
            SELECT staged.base_id
            FROM centered_view_expiration_candidates staged
            JOIN main.raw_captures raw ON raw.capture_id = staged.capture_id AND raw.retention_hold = 0
            WHERE NOT EXISTS (
                SELECT 1 FROM main.processing_outputs output
                WHERE output.output_identity_sha256 = staged.preview_id AND output.capture_id = staged.capture_id
            ) AND NOT EXISTS (
                SELECT 1 FROM main.processing_centered_view_intervals interval
                WHERE interval.base_id = staged.base_id AND interval.ended_order IS NULL
            ) AND NOT EXISTS (
                SELECT 1 FROM main.processing_centered_view_selections saved WHERE saved.base_id = staged.base_id
            ) AND NOT EXISTS (
                SELECT 1 FROM main.processing_centered_view_window_work work
                WHERE work.live_base_id = staged.base_id AND work.retry_utc IS NOT NULL
            );

        DELETE FROM temp.centered_view_expiration_candidates;
        """;

    private const string ReadCenteredViewPruneCandidatesSql = """
        SELECT b.base_id, raw.payload_relative_path, raw.sidecar_relative_path, raw.capture_id,
            json_extract(CASE WHEN json_valid(b.definition) THEN b.definition ELSE '{}' END, '$.preview.product.productIdentitySha256') AS preview_id,
            b.recomputed_from IS NOT NULL
        FROM processing_centered_view_bases b
        JOIN raw_captures raw ON raw.capture_id = lower(replace(b.reference_id, '-', ''))
        WHERE b.base_id > $after AND raw.exposure_started_unix_ms <= $cutoff AND raw.retention_hold = 0
            AND length(preview_id) = 64 AND NOT EXISTS (
                SELECT 1 FROM processing_outputs preview
                WHERE preview.output_identity_sha256 = preview_id AND preview.capture_id = raw.capture_id
            )
        ORDER BY b.base_id
        LIMIT $limit;
        """;

    private const string LoadCenteredViewPruneBatchSql = """
        DELETE FROM temp.centered_view_expiration_batch;
        INSERT INTO temp.centered_view_expiration_batch (base_id)
        SELECT base_id FROM temp.centered_view_expiration_candidates
        WHERE base_id > $after AND ($recomputed IS NULL OR recomputed = $recomputed)
        ORDER BY base_id LIMIT $limit;
        SELECT max(base_id) FROM temp.centered_view_expiration_batch;
        """;

    private const string DetachExpiredCenteredIntervalsSql = """
        DELETE FROM processing_centered_view_intervals AS interval
        WHERE interval.base_id IN (SELECT base_id FROM temp.centered_view_expiration_batch)
          AND EXISTS (SELECT 1 FROM temp.centered_view_expiration_eligible owner WHERE owner.base_id = interval.base_id)
          AND EXISTS (
              SELECT 1 FROM processing_centered_view_changes ending
              JOIN temp.centered_view_expiration_eligible eligible ON eligible.base_id = ending.base_id
              WHERE ending.change_order = interval.ended_order AND ending.base_id <> interval.base_id
          );
        """;

    private const string DeleteExpiredCenteredBasesSql = """
        DELETE FROM temp.centered_view_expiration_batch AS batch
        WHERE NOT EXISTS (SELECT 1 FROM temp.centered_view_expiration_eligible eligible WHERE eligible.base_id = batch.base_id)
          OR EXISTS (
              SELECT 1 FROM processing_centered_view_bases child WHERE child.recomputed_from = batch.base_id
          ) OR EXISTS (
              SELECT 1 FROM processing_centered_view_changes ending
              JOIN processing_centered_view_intervals interval ON interval.ended_order = ending.change_order
              WHERE ending.base_id = batch.base_id AND interval.base_id <> batch.base_id
          );

        UPDATE processing_centered_view_window_work SET live_base_id = NULL
        WHERE live_base_id IN (SELECT base_id FROM temp.centered_view_expiration_batch);
        DELETE FROM processing_centered_view_intervals
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_batch);
        DELETE FROM processing_centered_view_members
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_batch);
        DELETE FROM processing_centered_view_revisions
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_batch);
        DELETE FROM processing_centered_view_changes
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_batch);
        DELETE FROM processing_centered_view_bases
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_batch);
        SELECT changes();
        """;
}
