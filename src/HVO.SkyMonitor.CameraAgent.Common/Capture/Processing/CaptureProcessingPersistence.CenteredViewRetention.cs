using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class CaptureProcessingPersistence
{
    private async ValueTask PruneCenteredViewJournalAsync(
        DateTimeOffset cutoffUtc,
        IReadOnlySet<string> heldAbsolutePaths,
        CancellationToken cancellationToken,
        CancellationToken interruptionToken)
    {
        // Raw retention removes the entire cutoff UTC day. The processing output cutoff uses
        // a strict comparison on its commit timestamp, so separately require the preview to be gone.
        var lastEligibleCaptureTime = new DateTimeOffset(cutoffUtc.UtcDateTime.Date.AddTicks(TimeSpan.TicksPerDay - 1))
            .ToUnixTimeMilliseconds();
        using var connection = await _store.OpenCenteredViewsAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TEMP TABLE IF NOT EXISTS centered_view_expiration_candidates (base_id TEXT PRIMARY KEY) STRICT;
            DELETE FROM temp.centered_view_expiration_candidates;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var candidates = connection.CreateCommand();
        candidates.CommandText = """
            SELECT b.base_id, raw.payload_relative_path, raw.sidecar_relative_path
            FROM processing_centered_view_bases b
            JOIN raw_captures raw ON raw.capture_id = lower(replace(b.reference_id, '-', ''))
            WHERE b.base_id > $after AND raw.exposure_started_unix_ms <= $cutoff AND raw.retention_hold = 0
                AND NOT EXISTS (
                    SELECT 1 FROM processing_outputs preview
                    WHERE preview.output_identity_sha256 = json_extract(b.definition, '$.preview.product.productIdentitySha256')
                        AND preview.capture_id = raw.capture_id
                )
            ORDER BY b.base_id
            LIMIT $limit;
            """;
        var cursor = candidates.Parameters.AddWithValue("$after", "");
        candidates.Parameters.AddWithValue("$cutoff", lastEligibleCaptureTime);
        var batchSize = _lifecycleOptions.ReconciliationBatchSize;
        candidates.Parameters.AddWithValue("$limit", batchSize);

        using var stage = connection.CreateCommand();
        stage.CommandText = "INSERT INTO temp.centered_view_expiration_candidates (base_id) VALUES ($base);";
        var stageId = stage.Parameters.AddWithValue("$base", "");
        var page = new List<(string BaseId, string Payload, string Sidecar)>(batchSize);
        var staged = false;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            interruptionToken.ThrowIfCancellationRequested();
            page.Clear();
            using (var reader = await candidates.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    page.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            foreach (var (baseId, payload, sidecar) in page)
            {
                interruptionToken.ThrowIfCancellationRequested();
                cursor.Value = baseId;
                if (!IsUnheldAndAbsent(payload, heldAbsolutePaths) || !IsUnheldAndAbsent(sidecar, heldAbsolutePaths))
                    continue;
                stageId.Value = baseId;
                await stage.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                staged = true;
            }
        }
        while (page.Count == batchSize);

        if (!staged)
            return;
        // Preserve references committed while candidates were being discovered. The delete claims
        // the SQLite writer before inspecting selections, intervals and window-work references.
        interruptionToken.ThrowIfCancellationRequested();
#pragma warning disable CA1849 // The SQLite overload acquires the write transaction up front.
        using var transaction = connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
        command.Transaction = transaction;
        command.CommandText = PruneCenteredViewJournalSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        interruptionToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    // Keep the exact change that closed retained history. An expired view kept only as that
    // change's owner can lose its own expired cross-base intervals, so it cannot pin a chain.
    // Keep terminal window checkpoints so deleting a base cannot make completed work due again.
    private const string PruneCenteredViewJournalSql = """
        CREATE TEMP TABLE centered_view_expiration_pins (base_id TEXT PRIMARY KEY) STRICT;
        INSERT INTO temp.centered_view_expiration_pins (base_id)
        SELECT base_id FROM processing_centered_view_intervals WHERE ended_order IS NULL
        UNION
        SELECT base_id FROM processing_centered_view_selections
        UNION
        SELECT live_base_id FROM processing_centered_view_window_work
        WHERE live_base_id IS NOT NULL AND retry_utc IS NOT NULL;

        DELETE FROM processing_centered_view_intervals AS interval
        WHERE interval.base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates)
          AND interval.base_id NOT IN (SELECT base_id FROM temp.centered_view_expiration_pins)
          AND EXISTS (
              SELECT 1 FROM processing_centered_view_changes ending
              WHERE ending.change_order = interval.ended_order AND ending.base_id <> interval.base_id
                AND ending.base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates)
                AND ending.base_id NOT IN (SELECT base_id FROM temp.centered_view_expiration_pins)
          );

        WITH RECURSIVE
        dependencies(base_id, required_base_id) AS (
            SELECT child.base_id, child.recomputed_from
            FROM processing_centered_view_bases child
            WHERE child.recomputed_from IN (SELECT base_id FROM temp.centered_view_expiration_candidates)
            UNION
            SELECT interval.base_id, ending.base_id
            FROM processing_centered_view_intervals interval
            JOIN processing_centered_view_changes ending ON ending.change_order = interval.ended_order
            WHERE interval.base_id <> ending.base_id
              AND ending.base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates)
        ),
        protected(base_id) AS (
            SELECT required_base_id FROM dependencies
            WHERE base_id NOT IN (SELECT base_id FROM temp.centered_view_expiration_candidates)
            UNION
            SELECT base_id FROM temp.centered_view_expiration_pins
            UNION
            SELECT dependency.required_base_id
            FROM protected retained
            JOIN dependencies dependency ON dependency.base_id = retained.base_id
        )
        DELETE FROM temp.centered_view_expiration_candidates WHERE base_id IN (SELECT base_id FROM protected);

        UPDATE processing_centered_view_window_work SET live_base_id = NULL
        WHERE live_base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates);
        DELETE FROM processing_centered_view_intervals
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates);
        DELETE FROM processing_centered_view_members
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates);
        DELETE FROM processing_centered_view_revisions
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates);
        DELETE FROM processing_centered_view_changes
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates);
        DELETE FROM processing_centered_view_bases
        WHERE base_id IN (SELECT base_id FROM temp.centered_view_expiration_candidates);
        DROP TABLE temp.centered_view_expiration_pins;
        DROP TABLE temp.centered_view_expiration_candidates;
        """;
}
