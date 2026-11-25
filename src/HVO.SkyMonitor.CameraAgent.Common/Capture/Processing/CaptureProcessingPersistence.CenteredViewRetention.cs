using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    internal const int CenteredViewPruneBatchSize = 128;
    private const int CenteredViewPruneMaxPageSize = 512;
    private static readonly TimeSpan CenteredViewPrunePause = TimeSpan.FromMilliseconds(10);

    private async ValueTask PruneCenteredViewJournalAsync(
        DateTimeOffset cutoffUtc, IReadOnlySet<string> heldAbsolutePaths,
        Func<TimeSpan, ValueTask>? yieldLifecycleLocks, CancellationToken cancellationToken, CancellationToken interruptionToken)
    {
        using var interruption = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, interruptionToken);
        var sweepToken = interruption.Token;
        var pruned = 0;
        try
        {
            // Raw retention includes the entire cutoff UTC day. Preview expiry has its own commit cutoff.
            var lastEligibleCaptureTime = new DateTimeOffset(cutoffUtc.UtcDateTime.Date.AddTicks(TimeSpan.TicksPerDay - 1))
                .ToUnixTimeMilliseconds();
            using var connection = await _store.OpenCenteredViewsAsync(sweepToken).ConfigureAwait(false);
            if (await StageCenteredViewPruneCandidatesAsync(connection, lastEligibleCaptureTime, heldAbsolutePaths,
                    PauseAsync, sweepToken).ConfigureAwait(false))
            {
                // First detach the expired, unpinned links that would otherwise keep a chain of expired views.
                // Then remove recomputations before their original bases. A retained recomputation still pins its parent.
                await PruneBatchesAsync(null).ConfigureAwait(false);
                await PruneBatchesAsync(1).ConfigureAwait(false);
                await PruneBatchesAsync(0).ConfigureAwait(false);
            }

            await PruneCenteredTrailWorkAsync(connection, lastEligibleCaptureTime, heldAbsolutePaths,
                PauseAsync, sweepToken).ConfigureAwait(false);

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
                    sweepToken.ThrowIfCancellationRequested();
                    if (await batch.ExecuteScalarAsync(sweepToken).ConfigureAwait(false) is not string lastBase)
                        break;
                    after.Value = lastBase;
                    await ExcludeRestoredRawFilesAsync(connection, heldAbsolutePaths, sweepToken).ConfigureAwait(false);
#pragma warning disable CA1849 // This SQLite overload acquires the main journal write lock up front.
                    using (var transaction = connection.BeginTransaction(deferred: false))
#pragma warning restore CA1849
                    {
                        prune.Transaction = transaction;
                        var removed = await prune.ExecuteScalarAsync(sweepToken).ConfigureAwait(false);
                        sweepToken.ThrowIfCancellationRequested();
                        await transaction.CommitAsync(sweepToken).ConfigureAwait(false);
                        pruned += removed is long count ? checked((int)count) : 0;
                        prune.Transaction = null;
                        if (removed is long and > 0)
                            _faultInjector.Inject(CaptureProcessingFaultPoint.AfterCenteredViewPruneBatchCommit, "centered-view-retention");
                    }
                    // SQLite's asynchronous methods can complete synchronously. Give other journal writers a real gap.
                    await PauseAsync().ConfigureAwait(false);
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

        ValueTask PauseAsync()
        {
            sweepToken.ThrowIfCancellationRequested();
            return yieldLifecycleLocks is null
                ? new ValueTask(Task.Delay(CenteredViewPrunePause, sweepToken))
                : yieldLifecycleLocks(CenteredViewPrunePause);
        }
    }

    private async ValueTask<bool> StageCenteredViewPruneCandidatesAsync(
        SqliteConnection connection, long lastEligibleCaptureTime, IReadOnlySet<string> heldAbsolutePaths,
        Func<ValueTask> pause, CancellationToken cancellationToken)
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
        var pageSize = Math.Min(_lifecycleOptions.ReconciliationBatchSize, CenteredViewPruneMaxPageSize);
        candidates.Parameters.AddWithValue("$limit", pageSize);
        using var stage = connection.CreateCommand();
        stage.CommandText = """
            INSERT INTO temp.centered_view_expiration_candidates
                (base_id, capture_id, preview_id, recomputed, payload, sidecar)
            VALUES ($base, $capture, $preview, $recomputed, $payload, $sidecar);
            """;
        var stageId = stage.Parameters.AddWithValue("$base", "");
        var captureId = stage.Parameters.AddWithValue("$capture", "");
        var previewId = stage.Parameters.AddWithValue("$preview", "");
        var recomputed = stage.Parameters.AddWithValue("$recomputed", 0);
        var payload = stage.Parameters.AddWithValue("$payload", "");
        var sidecar = stage.Parameters.AddWithValue("$sidecar", "");
        var page = new List<CenteredViewPruneCandidate>(pageSize);
        var staged = false;
        var malformedCount = 0;
        string? firstMalformedBase = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            page.Clear();
            var readCount = 0;
            using (var reader = await candidates.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    readCount++;
                    cursor.Value = reader.GetString(0);
                    if (await reader.IsDBNullAsync(3, cancellationToken).ConfigureAwait(false) || reader.GetBoolean(6))
                        continue;
                    page.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                        reader.GetString(3), await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(4), reader.GetInt32(5)));
                }
            }
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsUnheldAndAbsent(row.Payload, heldAbsolutePaths) || !IsUnheldAndAbsent(row.Sidecar, heldAbsolutePaths))
                    continue;
                if (row.PreviewId is not { Length: 64 })
                {
                    firstMalformedBase ??= row.BaseId;
                    malformedCount++;
                    continue;
                }
                stageId.Value = row.BaseId;
                captureId.Value = row.CaptureId;
                previewId.Value = row.PreviewId;
                recomputed.Value = row.Recomputed;
                payload.Value = row.Payload;
                sidecar.Value = row.Sidecar;
                await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                staged = true;
            }

            if (readCount < pageSize)
                break;
            await pause().ConfigureAwait(false);
        }

        if (firstMalformedBase is not null)
            LogCenteredViewPruneSkippedDefinitions(_logger, malformedCount, firstMalformedBase);
        return staged;
    }

    private async ValueTask ExcludeRestoredRawFilesAsync(SqliteConnection connection,
        IReadOnlySet<string> heldAbsolutePaths, CancellationToken cancellationToken)
    {
        // Ingress may restore a raw file between the read page and this batch. The sweep owns
        // both lifecycle locks here. SQLite rechecks the durable holds and preview in the transaction.
        using var paths = connection.CreateCommand();
        paths.CommandText = """
            SELECT staged.base_id, staged.payload, staged.sidecar
            FROM temp.centered_view_expiration_batch batch
            JOIN temp.centered_view_expiration_candidates staged ON staged.base_id = batch.base_id;
            """;
        var retained = new List<string>();
        using (var reader = await paths.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsUnheldAndAbsent(reader.GetString(1), heldAbsolutePaths) ||
                    !IsUnheldAndAbsent(reader.GetString(2), heldAbsolutePaths))
                    retained.Add(reader.GetString(0));
            }
        }
        if (retained.Count == 0)
            return;

        using var exclude = connection.CreateCommand();
        exclude.CommandText = """
            DELETE FROM temp.centered_view_expiration_batch WHERE base_id = $base;
            DELETE FROM temp.centered_view_expiration_candidates WHERE base_id = $base;
            """;
        var baseId = exclude.Parameters.AddWithValue("$base", "");
        foreach (var id in retained)
        {
            baseId.Value = id;
            await exclude.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
        string BaseId, string Payload, string Sidecar, string CaptureId, string? PreviewId, int Recomputed);

    [LoggerMessage(EventId = 2546, Level = LogLevel.Warning, Message = "Centered-view journal pruning failed. Artifact retention completed and journal pruning will retry on the next sweep.")]
    private static partial void LogCenteredViewPruneFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2547, Level = LogLevel.Information, Message = "Pruned {BaseCount} expired centered-view bases from the journal.")]
    private static partial void LogCenteredViewsPruned(ILogger logger, int baseCount);

    [LoggerMessage(EventId = 2548, Level = LogLevel.Warning, Message = "Skipped {BaseCount} expired centered-view bases with an unreadable preview identity in their definition. First base: {BaseId}.")]
    private static partial void LogCenteredViewPruneSkippedDefinitions(ILogger logger, int baseCount, string baseId);

    // Temporary structures are scoped to this connection and reset on pooled reuse. All main-journal writes
    // hold at most one batch, including detaching closed cross-base intervals.
    private const string CenteredViewPruneSetupSql = """
        CREATE TEMP TABLE IF NOT EXISTS centered_view_expiration_candidates (
            base_id TEXT PRIMARY KEY, capture_id TEXT NOT NULL, preview_id TEXT NOT NULL, recomputed INTEGER NOT NULL,
            payload TEXT NOT NULL, sidecar TEXT NOT NULL
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

    // Page by base ID before applying the raw-capture filter, so a page of retained rows is
    // bounded too. Materialize the raw join before loading any definition payloads.
    private const string ReadCenteredViewPruneCandidatesSql = """
        WITH page AS MATERIALIZED (
            SELECT base_id, reference_id, recomputed_from IS NOT NULL AS recomputed
            FROM processing_centered_view_bases
            WHERE base_id > $after
            ORDER BY base_id
            LIMIT $limit
        ), raw_eligible AS MATERIALIZED (
            SELECT page.base_id, raw.payload_relative_path, raw.sidecar_relative_path, raw.capture_id
            FROM page
            JOIN raw_captures raw ON raw.capture_id = lower(replace(page.reference_id, '-', ''))
            WHERE raw.exposure_started_unix_ms <= $cutoff AND raw.retention_hold = 0
        ), definitions AS MATERIALIZED (
            SELECT raw_eligible.*,
                CASE WHEN json_valid(b.definition)
                    THEN json_extract(b.definition, '$.preview.product.productIdentitySha256') END AS preview_id
            FROM raw_eligible
            CROSS JOIN processing_centered_view_bases b ON b.base_id = raw_eligible.base_id
        )
        SELECT page.base_id, d.payload_relative_path, d.sidecar_relative_path, d.capture_id, d.preview_id, page.recomputed,
            EXISTS (
                SELECT 1 FROM processing_outputs preview
                WHERE length(d.preview_id) = 64 AND preview.output_identity_sha256 = d.preview_id
                    AND preview.capture_id = d.capture_id
            )
        FROM page
        LEFT JOIN definitions d ON d.base_id = page.base_id
        ORDER BY page.base_id;
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
