namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed partial class SqliteCaptureProcessingStore
{
    internal async ValueTask<IReadOnlyList<Guid>> ReadRecentAstrometryCapturesAsync(int offset, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT output.capture_id
            FROM processing_outputs output
            JOIN raw_captures raw ON raw.capture_id = output.capture_id
            WHERE output.product_schema_version = 'measured-astrometry-solution-v1'
              AND output.availability_state = 'Available'
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = output.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = output.output_identity_sha256
                                AND association.published_flag = 1))
            GROUP BY output.capture_id
            ORDER BY raw.exposure_started_unix_ms DESC, raw.capture_sequence DESC, output.capture_id DESC
            LIMIT 16 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$offset", offset);
        var captures = new List<Guid>(16);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            captures.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return captures;
    }
}
