using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal enum RetainedAstrometryDisplayState
{
    Unknown,
    Retained,
    Evicted,
    Quarantined
}

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
    private readonly CancellationTokenSource _astrometrySeedLifetime = new();
    private Task? _astrometrySeed;
    private bool _astrometrySeedStopped;
    private HashSet<Guid>? _changedAstrometryCaptures;
    private AstrometryCaptureCandidate[]? _astrometryCandidates;

    private void MarkAstrometryCaptureChanged(Guid captureId)
    {
        lock (_astrometryChangesLock)
        {
            _changedAstrometryCaptures?.Add(captureId);
        }
    }

    internal async ValueTask<bool> IsAstrometryQuarantinedAsync(
        Guid captureId, Guid artifactId, string solutionIdentitySha256, CancellationToken cancellationToken)
        => await ReadAstrometryDisplayStateAsync(captureId, artifactId, solutionIdentitySha256, cancellationToken)
            .ConfigureAwait(false) == RetainedAstrometryDisplayState.Quarantined;

    internal async ValueTask<RetainedAstrometryDisplayState> ReadAstrometryDisplayStateAsync(
        Guid captureId, Guid artifactId, string solutionIdentitySha256, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // The caller supplies an already verified identity. Absent durable rows indicate eviction.
        // A present row marked Missing still belongs to the fit and can recover.
        command.CommandText = """
            SELECT CASE
                WHEN assessment.capture_id IS NOT $capture OR assessment.role IS NOT 'Metadata'
                  OR assessment.product_schema_version IS NOT 'measured-astrometry-solution-v1'
                  OR assessment.content_identity_sha256 IS NOT $solution COLLATE NOCASE THEN 0
                WHEN assessment.availability_state = 'Quarantined' THEN 3
                ELSE COALESCE((
                    SELECT MAX(CASE
                        WHEN input.availability_state = 'Quarantined' OR raw.state = 'quarantined' THEN 3
                        WHEN input.artifact_id IS NULL AND raw.raw_artifact_id IS NULL THEN 2
                        ELSE 1 END)
                    FROM processing_output_sources source
                    LEFT JOIN processing_outputs input ON input.artifact_id = source.source_artifact_id
                      AND input.capture_id = assessment.capture_id
                    LEFT JOIN raw_captures raw ON raw.raw_artifact_id = source.source_artifact_id
                      AND raw.capture_id = assessment.capture_id
                    WHERE source.output_identity_sha256 = assessment.output_identity_sha256
                ), 1)
            END
            FROM processing_outputs assessment
            WHERE assessment.artifact_id = $artifact;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        command.Parameters.AddWithValue("$solution", solutionIdentitySha256);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) switch
        {
            null => RetainedAstrometryDisplayState.Evicted,
            1L => RetainedAstrometryDisplayState.Retained,
            2L => RetainedAstrometryDisplayState.Evicted,
            3L => RetainedAstrometryDisplayState.Quarantined,
            _ => RetainedAstrometryDisplayState.Unknown
        };
    }

    // A deadline can prevent another SQL read. Check that the identified capture has no pending or
    // in-flight change before carrying its known quarantine back to the previous-summary caller.
    internal bool IsCurrentAstrometrySnapshotForCapture(IReadOnlyList<AstrometryCaptureCandidate> snapshot, Guid captureId)
    {
        lock (_astrometryChangesLock)
        {
            return ReferenceEquals(snapshot, _astrometryCandidates) && _astrometryCandidateGate.CurrentCount != 0 &&
                   _changedAstrometryCaptures is { } changes && !changes.Contains(captureId);
        }
    }

    internal async ValueTask<IReadOnlyList<AstrometryCaptureCandidate>> ReadAstrometryCandidatesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task? seed = null;
        lock (_astrometryChangesLock)
        {
            ObjectDisposedException.ThrowIf(_astrometrySeedStopped, this);
            if (_astrometryCandidates is null)
            {
                // Microsoft.Data.Sqlite can materialize rows synchronously. One store-owned worker
                // supplies the initial snapshot while each page request can cancel its own wait.
                if (_astrometrySeed is null or { IsCompleted: true })
                {
                    _astrometrySeed = Task.Run(async () =>
                    {
                        await ReadAstrometryCandidatesCoreAsync(_astrometrySeedLifetime.Token).ConfigureAwait(false);
                    }, CancellationToken.None);
                    // All page requests can leave before a seed faults. Observe the error while
                    // retaining it for any waiters. The next request can retry a failed seed.
                    _ = _astrometrySeed.ContinueWith(static completed => { _ = completed.Exception; },
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
                seed = _astrometrySeed;
            }
        }

        if (seed is not null) await seed.WaitAsync(cancellationToken).ConfigureAwait(false);

        // Reconcile writes and evictions that arrived during the seed before exposing its snapshot.
        return await ReadAstrometryCandidatesCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<AstrometryCaptureCandidate>> ReadAstrometryCandidatesCoreAsync(
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
                var previousSnapshot = _astrometryCandidates;
                var seeding = previousSnapshot is null;
                if (seeding)
                {
                    command.CommandText = LatestAstrometryQuery;
                }
                else
                {
                    command.CommandText = RefreshAstrometryCandidatesQuery;
                    command.Parameters.AddWithValue("$captures", JsonSerializer.Serialize(
                        changes.Select(static captureId => captureId.ToString("N"))));
                }

                var updated = previousSnapshot is null
                    ? new List<AstrometryCaptureCandidate>()
                    : previousSnapshot.Where(candidate => !changes.Contains(candidate.CaptureId)).ToList();
                var observeSeed = seeding && _faultInjector != NullCaptureProcessingFaultInjector.Instance;
                using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var soleOutput = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ||
                            await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null
                            : new AstrometryOutputStamp(Guid.ParseExact(reader.GetString(4), "N"), reader.GetString(5));
                        var captureId = reader.GetString(0);
                        updated.Add(new(Guid.ParseExact(captureId, "N"), reader.GetInt64(1), reader.GetInt64(2),
                            reader.GetInt64(3), soleOutput));
                        if (observeSeed) _faultInjector.Inject(CaptureProcessingFaultPoint.AfterAstrometrySeedRowMaterialized, captureId);
                    }
                }
                if (observeSeed) _faultInjector.Inject(CaptureProcessingFaultPoint.BeforeAstrometrySeedPublished, "astrometry");
                cancellationToken.ThrowIfCancellationRequested();
                var snapshot = updated.OrderByDescending(static capture => capture.ExposureUtc)
                    .ThenByDescending(static capture => capture.Sequence).ThenByDescending(static capture => capture.RowId).ToArray();
                lock (_astrometryChangesLock)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _astrometryCandidates = snapshot;
                }
                return snapshot;
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
