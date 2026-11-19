namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record AstrometryCaptureCandidate(Guid CaptureId, long ExposureUtc, long Sequence, long RowId);

internal sealed partial class SqliteCaptureProcessingStore
{
    internal const string LatestAstrometryQuery = """
        SELECT raw.capture_id, raw.exposure_started_unix_ms, raw.capture_sequence, raw.raw_capture_row_id
        FROM raw_captures raw
        WHERE (raw.exposure_started_unix_ms, raw.capture_sequence, raw.raw_capture_row_id) < ($exposure, $sequence, $row)
          AND EXISTS (
            SELECT 1 FROM processing_outputs output
            WHERE output.capture_id = raw.capture_id
              AND output.product_schema_version = 'measured-astrometry-solution-v1'
              AND output.availability_state = 'Available'
              AND (NOT EXISTS (SELECT 1 FROM processing_execution_outputs association
                               WHERE association.output_identity_sha256 = output.output_identity_sha256)
                   OR EXISTS (SELECT 1 FROM processing_execution_outputs association
                              WHERE association.output_identity_sha256 = output.output_identity_sha256
                                AND association.published_flag = 1)))
        ORDER BY raw.exposure_started_unix_ms DESC, raw.capture_sequence DESC, raw.raw_capture_row_id DESC
        LIMIT 16;
        """;

    internal async ValueTask<IReadOnlyList<AstrometryCaptureCandidate>> ReadRecentAstrometryCapturesAsync(
        AstrometryCaptureCandidate? after, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = LatestAstrometryQuery;
        command.Parameters.AddWithValue("$exposure", after?.ExposureUtc ?? long.MaxValue);
        command.Parameters.AddWithValue("$sequence", after?.Sequence ?? long.MaxValue);
        command.Parameters.AddWithValue("$row", after?.RowId ?? long.MaxValue);
        var captures = new List<AstrometryCaptureCandidate>(16);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            captures.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
        return captures;
    }
}
