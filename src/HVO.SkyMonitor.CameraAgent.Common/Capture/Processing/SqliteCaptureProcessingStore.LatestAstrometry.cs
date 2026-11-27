using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

internal enum RetainedAstrometryLineageState
{
    // Keep this precedence in sync with the SQL aggregation in AstrometryExactLineageState.
    Unknown,
    Available,
    Pending,
    Missing,
    Evicted,
    Quarantined
}

internal readonly record struct AstrometryLineageStates(RetainedAstrometryLineageState Previous,
    RetainedAstrometryLineageState Selected, RetainedAstrometryLineageState Result);

internal sealed record AstrometryOutputStamp(Guid ArtifactId, string ContentIdentitySha256);

internal sealed record AstrometryCaptureCandidate(Guid CaptureId, long ExposureUtc, long Sequence, long RowId);

internal enum AstrometryAssessmentSelectionKind
{
    Absent,
    Pending,
    Public,
    Ambiguous
}

internal readonly record struct AstrometryAssessmentSelection(AstrometryAssessmentSelectionKind Kind,
    Guid ArtifactId = default, string? ContentIdentitySha256 = null, string? AvailabilityState = null);

internal readonly record struct AstrometrySkipCheck(Guid CaptureId, AstrometryAssessmentSelectionKind Selection,
    AstrometryOutputStamp? Assessment = null, RetainedAstrometryLineageState Lineage = RetainedAstrometryLineageState.Unknown);

internal sealed partial class SqliteCaptureProcessingStore
{
    // All retained assessment rows seed the work list. This indexed query does not inspect
    // publication for the entire archive. Each probe classifies current selection on demand.
    internal const string LatestAstrometryQuery = """
        SELECT raw.capture_id, raw.exposure_started_unix_ms, raw.capture_sequence, raw.raw_capture_row_id
        FROM (
            SELECT DISTINCT output.capture_id
            FROM processing_outputs output INDEXED BY ix_processing_outputs_astrometry_lookup
            WHERE output.role = 'Metadata' AND output.product_schema_version = 'measured-astrometry-solution-v1'
        ) candidate
        CROSS JOIN raw_captures raw ON raw.capture_id = candidate.capture_id;
        """;

    private const string RefreshAstrometryCandidatesQuery = """
        SELECT raw.capture_id, raw.exposure_started_unix_ms, raw.capture_sequence, raw.raw_capture_row_id
        FROM json_each($captures) changed
        CROSS JOIN raw_captures raw ON raw.capture_id = changed.value
        CROSS JOIN processing_outputs output INDEXED BY ix_processing_outputs_astrometry_lookup ON output.capture_id = raw.capture_id
          AND output.role = 'Metadata' AND output.product_schema_version = 'measured-astrometry-solution-v1'
        GROUP BY raw.capture_id;
        """;

    // Legacy or already published evidence is public. An unpublished execution can
    // supply public evidence only if a live writer can still complete its publication.
    private const string AstrometryOutputPublication = """
        CASE
            WHEN NOT EXISTS (SELECT 1 FROM processing_execution_outputs
                WHERE output_identity_sha256 = output.output_identity_sha256)
              OR EXISTS (SELECT 1 FROM processing_execution_outputs
                WHERE output_identity_sha256 = output.output_identity_sha256 AND published_flag = 1) THEN 1
            WHEN EXISTS (
                SELECT 1 FROM processing_execution_outputs association
                JOIN processing_executions execution ON execution.execution_id = association.execution_id
                WHERE association.output_identity_sha256 = output.output_identity_sha256
                  AND execution.execution_class = 'Live' AND execution.allow_automatic_publication = 1
                  AND execution.status IN ('Pending', 'Running')) THEN 2
            ELSE 3
        END
        """;

    private const string AstrometryExactLineageState = $"""
        CASE
            WHEN requested.artifact IS NULL THEN 0
            WHEN assessment.artifact_id IS NULL THEN 4
            WHEN assessment.capture_id IS NOT requested.capture OR assessment.role IS NOT 'Metadata'
              OR assessment.product_schema_version IS NOT 'measured-astrometry-solution-v1'
              OR assessment.content_identity_sha256 IS NOT requested.solution COLLATE NOCASE THEN 0
            WHEN assessment.availability_state = 'Quarantined' THEN 5
            ELSE (
                WITH lineage(ordinal, artifact) AS (
                    SELECT -1, assessment.artifact_id
                    UNION ALL
                    SELECT source_ordinal, source_artifact_id
                    FROM processing_output_sources
                    WHERE output_identity_sha256 = assessment.output_identity_sha256
                )
                SELECT CASE WHEN COUNT(*) <> 5 THEN 0 ELSE MAX(CASE
                    WHEN output.availability_state = 'Quarantined' OR raw.state = 'quarantined' THEN 5
                    WHEN output.artifact_id IS NULL AND raw.raw_artifact_id IS NULL THEN 4
                    WHEN raw.raw_artifact_id IS NOT NULL THEN CASE WHEN raw.state = 'committed' THEN 1 ELSE 2 END
                    ELSE CASE ({AstrometryOutputPublication})
                        WHEN 3 THEN 4
                        WHEN 2 THEN 2
                        ELSE CASE output.availability_state
                            WHEN 'Available' THEN 1
                            WHEN 'Missing' THEN 3
                            ELSE 2
                        END
                    END END)
                END
                FROM lineage
                LEFT JOIN processing_outputs output ON (lineage.ordinal = -1 OR lineage.ordinal BETWEEN 1 AND 3)
                  AND output.artifact_id = lineage.artifact AND output.capture_id = assessment.capture_id
                LEFT JOIN raw_captures raw ON lineage.ordinal = 0
                  AND raw.raw_artifact_id = lineage.artifact AND raw.capture_id = assessment.capture_id
            )
        END
        """;

    internal async ValueTask<AstrometryAssessmentSelection> ReadAstrometryAssessmentAsync(
        Guid captureId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH assessments AS MATERIALIZED (
                SELECT output.artifact_id, output.content_identity_sha256, output.availability_state,
                       {AstrometryOutputPublication} AS publication
                FROM processing_outputs output INDEXED BY ix_processing_outputs_astrometry_lookup
                WHERE output.capture_id = $capture AND output.role = 'Metadata'
                  AND output.product_schema_version = 'measured-astrometry-solution-v1'
            )
            SELECT artifact_id, content_identity_sha256, availability_state, publication
            FROM assessments WHERE publication IN (1, 2)
            ORDER BY publication LIMIT 2;
            """;
        var capture = captureId.ToString("N");
        command.Parameters.AddWithValue("$capture", capture);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var selection = await ReadSelectionAsync().ConfigureAwait(false);
        if (_faultInjector != NullCaptureProcessingFaultInjector.Instance)
            _faultInjector.Inject(CaptureProcessingFaultPoint.AfterAstrometryAssessmentLookup, capture);
        return selection;

        async ValueTask<AstrometryAssessmentSelection> ReadSelectionAsync()
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return new(AstrometryAssessmentSelectionKind.Absent);
            if (reader.GetInt32(3) == (int)RetainedAstrometryLineageState.Pending)
                return new(AstrometryAssessmentSelectionKind.Pending);

            var selected = new AstrometryAssessmentSelection(AstrometryAssessmentSelectionKind.Public,
                Guid.ParseExact(reader.GetString(0), "N"),
                await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(1),
                reader.GetString(2));
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) &&
                   reader.GetInt32(3) == (int)RetainedAstrometryLineageState.Available
                ? new(AstrometryAssessmentSelectionKind.Ambiguous) : selected;
        }
    }

    internal async ValueTask<string?> ReadAstrometrySkipReasonAsync(Guid captureId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT reason FROM processing_nodes
            WHERE capture_id = $capture AND recipe_name = $recipe AND status = 'Skipped'
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$capture", captureId.ToString("N"));
        command.Parameters.AddWithValue("$recipe", HVO.SkyMonitor.Processing.BuiltInProcessingRecipes.MeasuredAstrometry);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var reason = await reader.IsDBNullAsync(0, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(0);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? null : reason;
    }

    internal async ValueTask<HashSet<Guid>> ReadAstrometrySkipChangesAsync(
        IReadOnlyList<AstrometrySkipCheck> checks, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        // Compare the selection and any exact lineage proof in the same journal snapshot.
        // The batch is bounded and each requested capture uses the assessment index.
        command.CommandText = $"""
            WITH requested(capture, selection, artifact, solution, lineage) AS MATERIALIZED (
                SELECT json_extract(value, '$.capture'), json_extract(value, '$.selection'),
                       json_extract(value, '$.artifact'), json_extract(value, '$.solution'),
                       json_extract(value, '$.lineage')
                FROM json_each($checks)
            ), outputs AS MATERIALIZED (
                SELECT requested.capture, output.artifact_id, output.content_identity_sha256,
                       {AstrometryOutputPublication} AS publication
                FROM requested
                CROSS JOIN processing_outputs output INDEXED BY ix_processing_outputs_astrometry_lookup
                WHERE output.capture_id = requested.capture AND output.role = 'Metadata'
                  AND output.product_schema_version = 'measured-astrometry-solution-v1'
            ), observed AS (
                SELECT requested.capture,
                       COUNT(*) FILTER (WHERE outputs.publication = 1) AS public_count,
                       COUNT(*) FILTER (WHERE outputs.publication = 2) AS pending_count,
                       COUNT(*) FILTER (WHERE outputs.publication = 1 AND outputs.artifact_id = requested.artifact
                           AND outputs.content_identity_sha256 = requested.solution COLLATE NOCASE) AS exact_public
                FROM requested
                LEFT JOIN outputs ON outputs.capture = requested.capture
                GROUP BY requested.capture
            )
            SELECT requested.capture
            FROM requested
            JOIN observed ON observed.capture = requested.capture
            LEFT JOIN processing_outputs assessment ON assessment.artifact_id = requested.artifact
            WHERE CASE requested.selection
                WHEN $absent THEN observed.public_count > 0 OR observed.pending_count > 0
                WHEN $ambiguous THEN observed.public_count < 2
                ELSE CASE
                    WHEN observed.public_count <> 1 OR observed.exact_public <> 1 THEN 1
                    WHEN requested.lineage = 0 THEN assessment.availability_state IS NOT 'Available'
                    ELSE ({AstrometryExactLineageState}) <> requested.lineage
                END
            END;
            """;
        command.Parameters.AddWithValue("$absent", (int)AstrometryAssessmentSelectionKind.Absent);
        command.Parameters.AddWithValue("$ambiguous", (int)AstrometryAssessmentSelectionKind.Ambiguous);
        command.Parameters.AddWithValue("$checks", JsonSerializer.Serialize(checks.Select(static check => new
        {
            capture = check.CaptureId.ToString("N"),
            selection = (int)check.Selection,
            artifact = check.Assessment?.ArtifactId.ToString("N"),
            solution = check.Assessment?.ContentIdentitySha256,
            lineage = (int)check.Lineage
        })));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var changed = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            changed.Add(Guid.ParseExact(reader.GetString(0), "N"));
        return changed;
    }

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

    internal void NotifyLiveAstrometryPublished(Guid captureId)
        => NotifyAstrometryOutputsCommitted(captureId, assessmentWritten: false, inputMayBecomePublic: true);

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
        command.CommandText = $"""
            WITH requested(slot, capture, artifact, solution) AS (
                VALUES (0, $previousCapture, $previousArtifact, $previousSolution),
                       (1, $selectedCapture, $selectedArtifact, $selectedSolution),
                       (2, $resultCapture, $resultArtifact, $resultSolution)
            )
            SELECT {AstrometryExactLineageState}
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
                        var captureId = reader.GetString(0);
                        var id = Guid.ParseExact(captureId, "N");
                        queriedCaptures.Add(id);
                        updated.Add(new(id, reader.GetInt64(1), reader.GetInt64(2),
                            reader.GetInt64(3)));
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
