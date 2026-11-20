using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal enum RetainedAstrometryLineageState
{
    Unknown,
    Available,
    Pending,
    Evicted,
    Quarantined
}

internal readonly record struct AstrometryLineageStates(RetainedAstrometryLineageState Previous,
    RetainedAstrometryLineageState Selected, RetainedAstrometryLineageState Result);

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
    private readonly HashSet<Guid> _astrometryCandidateIds = [];

    // A committed raw release can make an assessment readable without rewriting the assessment or its inputs.
    internal void NotifyRawEvidenceRevalidated(Guid captureId) => MarkAstrometryCaptureChanged(captureId);

    private void MarkAstrometryCaptureChanged(Guid captureId)
    {
        lock (_astrometryChangesLock)
        {
            _changedAstrometryCaptures?.Add(captureId);
        }
    }

    private void NotifyAstrometryOutputsCommitted(Guid captureId, bool assessmentWritten, bool inputMayBecomePublic)
    {
        lock (_astrometryChangesLock)
        {
            // An evicted input can reappear or gain a live association under the same identity.
            if (assessmentWritten || inputMayBecomePublic && _astrometryCandidateIds.Contains(captureId))
                _changedAstrometryCaptures?.Add(captureId);
        }
    }

    internal async ValueTask<AstrometryLineageStates> ReadAstrometryLineageAsync(
        CameraAgentAstrometryAssessmentIdentity? previous, CameraAgentAstrometryAssessmentIdentity? selected,
        CameraAgentAstrometryAssessmentIdentity? result, CancellationToken cancellationToken)
    {
        if (previous is null && selected is null && result is null) return default;
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // One SQLite snapshot for the displayed identities and the actual result. The output identity
        // names immutable, ordinal source rows: raw, measured associations, scene and context.
        // Quarantine and eviction outrank recoverable unavailability anywhere in that lineage.
        // An unpublished output is pending only while a live execution can publish it. A private
        // replay copy keeps the exact evidence evicted from the public view.
        command.CommandText = """
            WITH requested(slot, capture, artifact, solution) AS (
                VALUES (0, $previousCapture, $previousArtifact, $previousSolution),
                       (1, $selectedCapture, $selectedArtifact, $selectedSolution),
                       (2, $resultCapture, $resultArtifact, $resultSolution)
            )
            SELECT CASE
                WHEN requested.artifact IS NULL THEN 0
                WHEN assessment.artifact_id IS NULL THEN 3
                WHEN assessment.capture_id IS NOT requested.capture OR assessment.role IS NOT 'Metadata'
                  OR assessment.product_schema_version IS NOT 'measured-astrometry-solution-v1'
                  OR assessment.content_identity_sha256 IS NOT requested.solution COLLATE NOCASE THEN 0
                WHEN assessment.availability_state = 'Quarantined' THEN 4
                ELSE (
                    WITH lineage(ordinal, artifact) AS (
                        SELECT -1, assessment.artifact_id
                        UNION ALL
                        SELECT source_ordinal, source_artifact_id
                        FROM processing_output_sources
                        WHERE output_identity_sha256 = assessment.output_identity_sha256
                    )
                    SELECT CASE WHEN COUNT(*) <> 5 THEN 0 ELSE MAX(CASE
                        WHEN output.availability_state = 'Quarantined' OR raw.state = 'quarantined' THEN 4
                        WHEN output.artifact_id IS NULL AND raw.raw_artifact_id IS NULL THEN 3
                        WHEN EXISTS (SELECT 1 FROM processing_execution_outputs
                            WHERE output_identity_sha256 = output.output_identity_sha256)
                          AND NOT EXISTS (SELECT 1 FROM processing_execution_outputs
                            WHERE output_identity_sha256 = output.output_identity_sha256 AND published_flag = 1) THEN
                            CASE WHEN EXISTS (
                                SELECT 1
                                FROM processing_execution_outputs association
                                JOIN processing_executions execution ON execution.execution_id = association.execution_id
                                WHERE association.output_identity_sha256 = output.output_identity_sha256
                                  AND execution.execution_class = 'Live' AND execution.allow_automatic_publication = 1
                                  AND execution.status IN ('Pending', 'Running'))
                            THEN 2 ELSE 3 END
                        WHEN raw.state <> 'committed' OR output.availability_state <> 'Available' THEN 2
                        ELSE 1 END)
                    END
                    FROM lineage
                    LEFT JOIN processing_outputs output ON (lineage.ordinal = -1 OR lineage.ordinal BETWEEN 1 AND 3)
                      AND output.artifact_id = lineage.artifact AND output.capture_id = assessment.capture_id
                    LEFT JOIN raw_captures raw ON lineage.ordinal = 0
                      AND raw.raw_artifact_id = lineage.artifact AND raw.capture_id = assessment.capture_id
                )
            END
            FROM requested
            LEFT JOIN processing_outputs assessment ON assessment.artifact_id = requested.artifact
            ORDER BY requested.slot;
            """;
        Bind("previous", previous);
        Bind("selected", selected == previous ? null : selected);
        Bind("result", result == previous || result == selected ? null : result);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var previousState = await ReadStateAsync().ConfigureAwait(false);
        var selectedState = await ReadStateAsync().ConfigureAwait(false);
        var resultState = await ReadStateAsync().ConfigureAwait(false);
        if (selected == previous) selectedState = previousState;
        if (result == previous) resultState = previousState;
        else if (result == selected) resultState = selectedState;
        return new(previousState, selectedState, resultState);

        void Bind(string name, CameraAgentAstrometryAssessmentIdentity? identity)
        {
            command.Parameters.AddWithValue($"${name}Capture", (object?)identity?.CaptureId.ToString("N") ?? DBNull.Value);
            command.Parameters.AddWithValue($"${name}Artifact", (object?)identity?.ArtifactId.ToString("N") ?? DBNull.Value);
            command.Parameters.AddWithValue($"${name}Solution", (object?)identity?.SolutionIdentitySha256 ?? DBNull.Value);
        }

        async ValueTask<RetainedAstrometryLineageState> ReadStateAsync()
            => await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? (RetainedAstrometryLineageState)reader.GetInt32(0) : RetainedAstrometryLineageState.Unknown;
    }

    // A deadline can prevent another SQL read. Check that the identified capture has no pending or
    // in-flight change before returning its known invalidation to the caller.
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
                var queriedCaptures = new List<Guid>();
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
                        var id = Guid.ParseExact(captureId, "N");
                        queriedCaptures.Add(id);
                        updated.Add(new(id, reader.GetInt64(1), reader.GetInt64(2),
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
                    _astrometryCandidateIds.ExceptWith(changes);
                    _astrometryCandidateIds.UnionWith(queriedCaptures);
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
