namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

internal sealed partial class SqliteCenteredViews
{
    internal async ValueTask<long> ReadMeasuredStackRecoveryOrderAsync(int recentRevisions, CancellationToken cancellationToken)
    {
        ValidatePage(0, recentRevisions);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            SELECT change_order FROM processing_centered_view_revisions
            ORDER BY change_order DESC LIMIT 1 OFFSET $recent;
            """, ("$recent", recentRevisions));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long order ? order : 0;
    }

    internal async ValueTask<(long ScannedThrough, IReadOnlyList<CenteredMeasuredDeepSkyWork> Work)> ReadMeasuredStackWorkAsync(
        long afterOrder, long throughOrder, int pageSize, CancellationToken cancellationToken)
    {
        ValidatePage(afterOrder, pageSize);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateCommand(connection, null, """
            WITH page AS MATERIALIZED (
                SELECT base_id, revision_id, change_order
                FROM processing_centered_view_revisions
                WHERE change_order > $after AND change_order <= $through
                ORDER BY change_order LIMIT $limit
            )
            SELECT initial.base_id, initial.revision_id, initial.change_order,
                CASE WHEN b.kind = $stack AND b.recomputed_from IS NULL
                  AND json_type(b.definition, '$.window') = 'object'
                  AND initial.change_order = (
                      SELECT MIN(change_order) FROM processing_centered_view_revisions WHERE base_id = b.base_id
                  )
                  AND NOT EXISTS (
                      SELECT 1
                      FROM json_each((
                          SELECT layers FROM processing_centered_view_revisions WHERE base_id = b.base_id
                          ORDER BY change_order DESC LIMIT 1
                      ))
                      WHERE json_extract(value, '$.key.kind') = $kind
                        AND json_extract(value, '$.evidenceOrder') >= initial.change_order
                  ) THEN 1 ELSE 0 END
            FROM page initial
            JOIN processing_centered_view_bases b ON b.base_id = initial.base_id
            ORDER BY initial.change_order;
            """, ("$after", afterOrder), ("$stack", (int)CenteredViewKind.Stack),
            ("$kind", CenteredMeasuredDeepSky.LayerKind), ("$limit", pageSize), ("$through", throughOrder));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<CenteredMeasuredDeepSkyWork>(pageSize);
        var scannedThrough = afterOrder;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            scannedThrough = reader.GetInt64(2);
            if (reader.GetBoolean(3))
            {
                result.Add(new(reader.GetString(0), reader.GetString(1), scannedThrough));
            }
        }
        return (scannedThrough, result);
    }
}
