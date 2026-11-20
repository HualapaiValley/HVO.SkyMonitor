using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal sealed record AstrometryOutputStamp(Guid ArtifactId, string ContentIdentitySha256);

internal sealed record AstrometryCaptureCandidate(
    Guid CaptureId, long ExposureUtc, long Sequence, long RowId, AstrometryOutputStamp? SoleOutput);

internal sealed partial class SqliteCaptureProcessingStore
{
    // Seed this read model from retained solution outputs once. A capture can be a candidate before its output is
    // published. Each read still checks current publication, availability and exact evidence through the retained reader.
    internal const string LatestAstrometryQuery = """
        SELECT raw.capture_id, raw.exposure_started_unix_ms, raw.capture_sequence, raw.raw_capture_row_id,
               candidate.sole_artifact, candidate.sole_content
        FROM (
            SELECT output.capture_id,
                   CASE WHEN COUNT(*) = 1 AND MIN(output.availability_state) = 'Available'
                       THEN MIN(output.artifact_id) END AS sole_artifact,
                   CASE WHEN COUNT(*) = 1 AND MIN(output.availability_state) = 'Available'
                       THEN MIN(output.content_identity_sha256) END AS sole_content
            FROM processing_outputs output INDEXED BY ix_processing_outputs_astrometry_lookup
            WHERE output.role = 'Metadata' AND output.product_schema_version = 'measured-astrometry-solution-v1'
            GROUP BY output.capture_id
        ) candidate
        CROSS JOIN raw_captures raw ON raw.capture_id = candidate.capture_id;
        """;

    private const string RefreshAstrometryCandidatesQuery = """
        SELECT raw.capture_id, raw.exposure_started_unix_ms, raw.capture_sequence, raw.raw_capture_row_id,
               CASE WHEN COUNT(*) = 1 AND MIN(output.availability_state) = 'Available'
                   THEN MIN(output.artifact_id) END,
               CASE WHEN COUNT(*) = 1 AND MIN(output.availability_state) = 'Available'
                   THEN MIN(output.content_identity_sha256) END
        FROM json_each($captures) changed
        CROSS JOIN raw_captures raw ON raw.capture_id = changed.value
        CROSS JOIN processing_outputs output INDEXED BY ix_processing_outputs_astrometry_lookup ON output.capture_id = raw.capture_id
          AND output.role = 'Metadata' AND output.product_schema_version = 'measured-astrometry-solution-v1'
        GROUP BY raw.capture_id;
        """;

    private readonly SemaphoreSlim _astrometryCandidateGate = new(1, 1);
    private readonly object _astrometryChangesLock = new();
    private HashSet<Guid>? _changedAstrometryCaptures;
    private AstrometryCaptureCandidate[]? _astrometryCandidates;

    private void MarkAstrometryCaptureChanged(Guid captureId)
    {
        lock (_astrometryChangesLock)
        {
            _changedAstrometryCaptures?.Add(captureId);
        }
    }

    internal async ValueTask<IReadOnlyList<AstrometryCaptureCandidate>> ReadAstrometryCandidatesAsync(
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _astrometryCandidateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            HashSet<Guid> changes;
            lock (_astrometryChangesLock)
            {
                if (_astrometryCandidates is not null && _changedAstrometryCaptures is { Count: 0 })
                    return _astrometryCandidates;

                changes = _changedAstrometryCaptures ?? [];
                // Enable notifications before the initial read. Changes arriving during a read are handled next time.
                _changedAstrometryCaptures = [];
            }

            try
            {
                using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                using var command = connection.CreateCommand();
                if (_astrometryCandidates is null)
                {
                    command.CommandText = LatestAstrometryQuery;
                }
                else
                {
                    command.CommandText = RefreshAstrometryCandidatesQuery;
                    command.Parameters.AddWithValue("$captures", JsonSerializer.Serialize(
                        changes.Select(static captureId => captureId.ToString("N"))));
                }

                var updated = _astrometryCandidates is null
                    ? new List<AstrometryCaptureCandidate>()
                    : _astrometryCandidates.Where(candidate => !changes.Contains(candidate.CaptureId)).ToList();
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var soleOutput = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ||
                        await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null
                        : new AstrometryOutputStamp(Guid.ParseExact(reader.GetString(4), "N"), reader.GetString(5));
                    updated.Add(new(Guid.ParseExact(reader.GetString(0), "N"), reader.GetInt64(1), reader.GetInt64(2),
                        reader.GetInt64(3), soleOutput));
                }
                cancellationToken.ThrowIfCancellationRequested();
                _astrometryCandidates = updated.OrderByDescending(static capture => capture.ExposureUtc)
                    .ThenByDescending(static capture => capture.Sequence).ThenByDescending(static capture => capture.RowId).ToArray();
                return _astrometryCandidates;
            }
            catch
            {
                lock (_astrometryChangesLock)
                {
                    _changedAstrometryCaptures.UnionWith(changes);
                }
                throw;
            }
        }
        finally
        {
            _astrometryCandidateGate.Release();
        }
    }
}
