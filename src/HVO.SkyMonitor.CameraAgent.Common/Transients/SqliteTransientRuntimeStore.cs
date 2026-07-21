using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

public enum TransientQuarantineReleaseDisposition
{
    Abandoned,
    NotFound
}

public interface ITransientRuntimeManagement
{
    ValueTask<TransientQuarantineReleaseDisposition> AbandonQuarantinedCaptureAsync(
        Guid artifactId,
        CancellationToken cancellationToken);
}

internal sealed record TransientRuntimeFrame(
    long RawCaptureRowId,
    string AgentId,
    long CaptureSequence,
    Guid ArtifactId,
    DateTimeOffset AvailableUtc,
    int AttemptCount);

internal sealed record TransientLoadedFrame(
    TransientRuntimeFrame Runtime,
    ArtifactManifestV2 Manifest,
    ProcessingArtifact Artifact,
    TransientSourceEvidenceReferenceV1 Source);

internal sealed record TransientRuntimeCandidate(
    Guid CandidateId,
    Guid EventId,
    long TargetRawCaptureRowId,
    int SlotOrdinal,
    Guid ObservationId,
    Guid AssessmentId,
    Guid EventVersionId,
    DateTimeOffset AllocatedUtc,
    bool AssociationAmbiguous,
    int AttemptCount,
    DateTimeOffset AvailableUtc,
    TransientCandidateExtractionDescriptorV1? CausalExtraction,
    TransientCandidateExtractionDescriptorV1? ObservationExtraction,
    TransientAssessmentExecutionDescriptorV1? AssessmentExecution);

internal sealed record TransientRuntimeTotals(long Frames, long Candidates, long Quarantined);

internal sealed class SqliteTransientRuntimeStore : ITransientRuntimeManagement
{
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly TimeProvider _timeProvider;
    private readonly ITransientRuntimeFaultInjector _faultInjector;
    private readonly TransientWorkerTelemetry _telemetry;
    private readonly CaptureDistributionOptions _limits;
    private readonly bool _required;

    public SqliteTransientRuntimeStore(
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        TransientWorkerTelemetry telemetry,
        ITransientRuntimeFaultInjector? faultInjector = null)
    {
        var configured = options.Value;
        _root = Path.GetFullPath(configured.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = configured.RawIngressSqliteBusyTimeoutSeconds;
        _timeProvider = timeProvider;
        _telemetry = telemetry;
        _faultInjector = faultInjector ?? NullTransientRuntimeFaultInjector.Instance;
        _limits = configured.CaptureDistribution;
        _required = configured.TransientDetection.Required;
    }

    internal async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = RuntimeSchemaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await MigrateLegacyFrameSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
        await ReconcileAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<TransientRuntimeFrame?> ReadNextAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileAsync(connection, cancellationToken).ConfigureAwait(false);
        using (var discover = connection.CreateCommand())
        {
            discover.CommandText = """
                INSERT OR IGNORE INTO transient_worker_frames(
                    raw_capture_row_id, state, attempt_count, available_unix_ms, created_unix_ms, updated_unix_ms)
                SELECT raw_capture_row_id, 'queued', 0, created_unix_ms, created_unix_ms, created_unix_ms
                FROM transient_capture_work
                WHERE state = 'pending';
                """;
            await discover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.raw_capture_row_id, r.agent_id, r.capture_sequence, r.raw_artifact_id,
                   f.available_unix_ms, f.attempt_count
            FROM transient_worker_frames f
            JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id
            JOIN transient_capture_work w ON w.raw_capture_row_id = f.raw_capture_row_id
            WHERE f.state IN ('queued', 'retry_wait') AND f.available_unix_ms <= $now
              AND w.state IN ('pending', 'candidate_persisted')
            ORDER BY r.capture_sequence, f.raw_capture_row_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TransientRuntimeFrame(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                Guid.ParseExact(reader.GetString(3), "N"),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
                reader.GetInt32(5))
            : null;
    }

    internal async ValueTask<TransientRuntimeFrame> ReadFrameAsync(
        long rawCaptureRowId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.raw_capture_row_id, r.agent_id, r.capture_sequence, r.raw_artifact_id,
                   COALESCE(f.available_unix_ms, r.committed_unix_ms), COALESCE(f.attempt_count, 0)
            FROM raw_captures r
            LEFT JOIN transient_worker_frames f ON f.raw_capture_row_id = r.raw_capture_row_id
            WHERE r.raw_capture_row_id = $raw;
            """;
        command.Parameters.AddWithValue("$raw", rawCaptureRowId);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Transient runtime target no longer exists.");
        }
        return new TransientRuntimeFrame(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            Guid.ParseExact(reader.GetString(3), "N"),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
            reader.GetInt32(5));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Opened evidence streams transfer to the snapshot owner and are disposed on every success and failure path.")]
    internal async ValueTask<IReadOnlyDictionary<int, TransientLoadedFrame>> LoadWindowAsync(
        string agentId,
        long targetSequence,
        IReadOnlyCollection<int> offsets,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<TransientEvidenceSnapshot>();
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            foreach (var offset in offsets.Order())
            {
                var sequence = checked(targetSequence + offset);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT r.raw_capture_row_id, r.raw_artifact_id, r.payload_length,
                           r.payload_relative_path, r.sidecar_relative_path, r.manifest_json,
                           r.manifest_sha256, r.payload_sha256, r.state,
                           COALESCE(f.attempt_count, 0), COALESCE(f.available_unix_ms, r.committed_unix_ms)
                    FROM raw_captures r
                    JOIN transient_capture_work w ON w.raw_capture_row_id = r.raw_capture_row_id
                    LEFT JOIN transient_worker_frames f ON f.raw_capture_row_id = r.raw_capture_row_id
                    WHERE r.agent_id = $agent AND r.capture_sequence = $sequence
                      AND w.state IN ('pending', 'candidate_persisted', 'completed');
                    """;
                command.Parameters.AddWithValue("$agent", agentId);
                command.Parameters.AddWithValue("$sequence", sequence);
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }
                var rawRowId = reader.GetInt64(0);
                var artifactId = Guid.ParseExact(reader.GetString(1), "N");
                var payloadLength = reader.GetInt64(2);
                var payloadRelativePath = reader.GetString(3);
                var sidecarRelativePath = reader.GetString(4);
                var manifestJson = await reader.GetFieldValueAsync<byte[]>(5, cancellationToken).ConfigureAwait(false);
                var manifestSha256 = reader.GetString(6);
                var payloadSha256 = reader.GetString(7);
                var state = reader.GetString(8);
                var attempt = reader.GetInt32(9);
                var available = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10));
                await reader.DisposeAsync().ConfigureAwait(false);
                if (!string.Equals(state, "committed", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Transient runtime source is not committed raw evidence.");
                }
                var parsed = CaptureContractJson.ParseManifest(manifestJson);
                var manifest = parsed.Document?.Manifest;
                if (!parsed.IsValid || manifest is null || manifest.Descriptor.Artifact.ArtifactId != artifactId ||
                    manifest.Descriptor.Artifact.Role != FrameArtifactRole.Raw ||
                    !string.Equals(manifest.RelativeArtifactPath, payloadRelativePath, StringComparison.Ordinal) ||
                    !string.Equals(manifest.Descriptor.Artifact.ChecksumSha256, payloadSha256, StringComparison.Ordinal) ||
                    !string.Equals(CaptureContractJson.ComputeManifestSha256(manifestJson), manifestSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Transient runtime source manifest is invalid or altered.");
                }
                var payloadPath = Resolve(payloadRelativePath);
                var sidecarPath = Resolve(sidecarRelativePath);
                if (!File.Exists(payloadPath) || !File.Exists(sidecarPath))
                {
                    throw new FileNotFoundException("Transient runtime source evidence is missing.");
                }
                RawIngressFileStore.EnsureNoSymbolicLinks(_root, payloadPath);
                RawIngressFileStore.EnsureNoSymbolicLinks(_root, sidecarPath);
                if (payloadLength > int.MaxValue || new FileInfo(payloadPath).Length != payloadLength)
                {
                    throw new InvalidDataException("Transient runtime source payload length differs from committed evidence.");
                }
                var payloadStream = OpenEvidence(payloadPath);
                try
                {
                    var sidecarStream = OpenEvidence(sidecarPath);
                    snapshots.Add(new TransientEvidenceSnapshot(
                        offset,
                        payloadLength,
                        payloadSha256,
                        manifestJson,
                        manifest,
                        payloadStream,
                        sidecarStream,
                        new TransientRuntimeFrame(rawRowId, agentId, sequence, artifactId, available, attempt)));
                }
                catch
                {
                    await payloadStream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
        }
        catch
        {
            foreach (var snapshot in snapshots)
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            lifecycleGate.Release();
        }

        var output = new Dictionary<int, TransientLoadedFrame>();
        try
        {
            foreach (var snapshot in snapshots)
            {
                var payload = await ReadExactlyAsync(
                    snapshot.PayloadStream, checked((int)snapshot.PayloadLength), cancellationToken).ConfigureAwait(false);
                var sidecar = await ReadExactlyAsync(
                    snapshot.SidecarStream, checked((int)snapshot.SidecarStream.Length), cancellationToken).ConfigureAwait(false);
                _telemetry.RecordEvidenceRead(snapshot.PayloadLength, sidecar.LongLength);
                if (!sidecar.AsSpan().SequenceEqual(snapshot.ManifestJson) ||
                    !string.Equals(
                        Convert.ToHexString(SHA256.HashData(payload)),
                        snapshot.PayloadSha256,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Transient runtime source bytes differ from committed evidence.");
                }
                var descriptor = snapshot.Manifest.Descriptor;
                var artifact = new ProcessingArtifact(
                    snapshot.Runtime.ArtifactId,
                    FrameArtifactRole.Raw,
                    descriptor.Artifact.Variant,
                    ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256,
                    descriptor.Artifact.MediaType,
                    descriptor.Layout,
                    payload,
                    descriptor.Artifact.CreatedUtc,
                    descriptor.Controls.EffectiveExposure,
                    CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor),
                    descriptor.Capture.CaptureSequence,
                    descriptor.Artifact.SourceArtifactIds,
                    descriptor.Timing.ExposureStartedUtc,
                    descriptor.Timing.ExposureEndedUtc);
                var source = new TransientSourceEvidenceReferenceV1(
                    TransientSourceEvidenceReferenceV1.CurrentSchemaVersion,
                    snapshot.Runtime.ArtifactId,
                    new TransientWholeArtifactLocatorV1(
                        TransientWholeArtifactLocatorV1.CurrentSchemaVersion,
                        TransientSourceLocatorKind.WholeArtifact,
                        new TransientArtifactReferenceV1(
                            snapshot.Runtime.ArtifactId,
                            FrameArtifactRole.Raw,
                            artifact.Variant,
                            artifact.RecipeIdentitySha256,
                            snapshot.PayloadSha256)),
                    descriptor.Timing.ExposureStartedUtc,
                    descriptor.Timing.ExposureEndedUtc,
                    TransientTimingQuality.Reported,
                    new TransientTimingProvenanceV1("raw-ingress-manifest", ArtifactManifestV2.CurrentSchemaVersion));
                output.Add(snapshot.Offset, new TransientLoadedFrame(
                    snapshot.Runtime,
                    snapshot.Manifest,
                    artifact,
                    source));
            }
        }
        finally
        {
            foreach (var snapshot in snapshots)
            {
                await snapshot.DisposeAsync().ConfigureAwait(false);
            }
        }
        return output;
    }

    internal async ValueTask<IReadOnlyList<TransientRuntimeCandidate>> ReadAllocationsAsync(
        long targetRawCaptureRowId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate_id, event_id, target_raw_capture_row_id, slot_ordinal,
                   observation_id, assessment_id, event_version_id, allocated_unix_ms,
                   association_ambiguous, attempt_count, available_unix_ms, causal_extraction_json,
                   observation_extraction_json, assessment_execution_json
            FROM transient_worker_candidates
            WHERE target_raw_capture_row_id = $raw
            ORDER BY slot_ordinal;
            """;
        command.Parameters.AddWithValue("$raw", targetRawCaptureRowId);
        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<TransientRuntimeCandidate>> ReadPendingCandidatesAsync(
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await ReconcileAsync(connection, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.candidate_id, c.event_id, c.target_raw_capture_row_id, c.slot_ordinal,
                   c.observation_id, c.assessment_id, c.event_version_id, c.allocated_unix_ms,
                   c.association_ambiguous, c.attempt_count, c.available_unix_ms, c.causal_extraction_json,
                   c.observation_extraction_json, c.assessment_execution_json
            FROM transient_worker_candidates c
            JOIN transient_candidates j ON j.candidate_id = c.candidate_id
            WHERE c.state = 'pending' AND c.available_unix_ms <= $now
              AND j.phase NOT IN ('finalized', 'handoff_pending', 'acknowledged', 'quarantined')
            ORDER BY c.allocated_unix_ms, c.slot_ordinal;
            """;
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<IReadOnlyList<TransientCandidateV1>> ReadAdjacentCandidatesAsync(
        string agentId,
        long captureSequence,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT j.candidate_payload
            FROM transient_worker_candidates c
            JOIN raw_captures r ON r.raw_capture_row_id = c.target_raw_capture_row_id
            JOIN transient_candidates j ON j.candidate_id = c.candidate_id
            WHERE r.agent_id = $agent AND r.capture_sequence = $sequence
              AND j.candidate_payload IS NOT NULL AND j.phase != 'quarantined';
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$sequence", captureSequence);
        var output = new List<TransientCandidateV1>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var parsed = TransientContractJson.ParseCandidate(await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false));
            if (!parsed.Validation.IsValid || parsed.Value is null)
            {
                throw new InvalidDataException("Transient runtime candidate payload is invalid.");
            }
            output.Add(parsed.Value);
        }
        return output;
    }

    internal async ValueTask<IReadOnlyList<TransientEventV1>> ReadEventHistoryAsync(
        Guid eventId,
        Guid excludingCandidateId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT finalization_payload
            FROM transient_candidates
            WHERE event_id = $event AND candidate_id != $candidate AND finalization_payload IS NOT NULL
            ORDER BY updated_unix_ms, candidate_id;
            """;
        command.Parameters.AddWithValue("$event", eventId.ToString("N"));
        command.Parameters.AddWithValue("$candidate", excludingCandidateId.ToString("N"));
        var output = new List<TransientEventV1>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var parsed = TransientCandidateDeliveryJson.ParseFinalization(
                await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false));
            if (!parsed.Validation.IsValid || parsed.Value is null)
            {
                throw new InvalidDataException("Transient event history contains an invalid finalization receipt.");
            }
            output.Add(parsed.Value.Event);
        }
        return output;
    }

    internal async ValueTask<IReadOnlyList<TransientRuntimeCandidate>> AllocateBatchAsync(
        long targetRawCaptureRowId,
        IReadOnlyList<TransientRuntimeCandidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0 || candidates.Any(candidate =>
                candidate.TargetRawCaptureRowId != targetRawCaptureRowId) ||
            !candidates.Select(static candidate => candidate.SlotOrdinal).SequenceEqual(
                Enumerable.Range(0, candidates.Count)) ||
            candidates.Select(static candidate => candidate.CandidateId).Distinct().Count() != candidates.Count)
        {
            throw new ArgumentException("Transient identity allocation batch is invalid.", nameof(candidates));
        }
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT COUNT(*) FROM transient_worker_candidates WHERE target_raw_capture_row_id = $raw;";
            existing.Parameters.AddWithValue("$raw", targetRawCaptureRowId);
            if (Convert.ToInt32(
                    await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) > 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return await ReadAllocationsAsync(targetRawCaptureRowId, cancellationToken).ConfigureAwait(false);
            }
        }
        foreach (var candidate in candidates)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO transient_worker_candidates(
                    candidate_id, event_id, target_raw_capture_row_id, slot_ordinal,
                    observation_id, assessment_id, event_version_id, allocated_unix_ms,
                    association_ambiguous, attempt_count, available_unix_ms, state)
                VALUES ($candidate, $event, $raw, $slot, $observation, $assessment,
                        $event_version, $allocated, $ambiguous, $attempt, $available, 'pending');
                """;
            command.Parameters.AddWithValue("$candidate", candidate.CandidateId.ToString("N"));
            command.Parameters.AddWithValue("$event", candidate.EventId.ToString("N"));
            command.Parameters.AddWithValue("$raw", candidate.TargetRawCaptureRowId);
            command.Parameters.AddWithValue("$slot", candidate.SlotOrdinal);
            command.Parameters.AddWithValue("$observation", candidate.ObservationId.ToString("N"));
            command.Parameters.AddWithValue("$assessment", candidate.AssessmentId.ToString("N"));
            command.Parameters.AddWithValue("$event_version", candidate.EventVersionId.ToString("N"));
            command.Parameters.AddWithValue("$allocated", candidate.AllocatedUtc.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$ambiguous", candidate.AssociationAmbiguous ? 1 : 0);
            command.Parameters.AddWithValue("$attempt", candidate.AttemptCount);
            command.Parameters.AddWithValue("$available", candidate.AvailableUtc.ToUnixTimeMilliseconds());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        _faultInjector.Inject(TransientRuntimeFaultPoint.BeforeIdentityBatchCommit);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _faultInjector.Inject(TransientRuntimeFaultPoint.AfterIdentityBatchCommit);
        return await ReadAllocationsAsync(targetRawCaptureRowId, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<bool> IsCausalWindowCompleteAsync(
        string agentId,
        long targetSequence,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM raw_captures r
            JOIN transient_worker_frames f ON f.raw_capture_row_id = r.raw_capture_row_id
            WHERE r.agent_id = $agent AND r.capture_sequence BETWEEN $first AND $last
              AND f.state IN ('history', 'completed') AND f.causal_succeeded = 1
              AND NOT EXISTS (
                  SELECT 1
                  FROM transient_worker_candidates c
                  LEFT JOIN transient_candidates j ON j.candidate_id = c.candidate_id
                  WHERE c.target_raw_capture_row_id = r.raw_capture_row_id
                    AND (c.state = 'quarantined' OR j.phase = 'quarantined'));
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$first", checked(targetSequence - 2));
        command.Parameters.AddWithValue("$last", checked(targetSequence + 2));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture) == 5;
    }

    internal async ValueTask<IReadOnlyList<Guid>> ReadKnownEventEvidenceIdsAsync(
        string agentId,
        long targetSequence,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT j.candidate_payload
            FROM transient_worker_candidates c
            JOIN raw_captures r ON r.raw_capture_row_id = c.target_raw_capture_row_id
            JOIN transient_candidates j ON j.candidate_id = c.candidate_id
            WHERE r.agent_id = $agent AND r.capture_sequence BETWEEN $first AND $last
              AND r.capture_sequence != $target AND j.candidate_payload IS NOT NULL
              AND c.state != 'quarantined';
            """;
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$first", checked(targetSequence - 2));
        command.Parameters.AddWithValue("$last", checked(targetSequence + 2));
        command.Parameters.AddWithValue("$target", targetSequence);
        var output = new List<Guid>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var parsed = TransientContractJson.ParseCandidate(
                await reader.GetFieldValueAsync<byte[]>(0, cancellationToken).ConfigureAwait(false));
            if (!parsed.Validation.IsValid || parsed.Value is null)
            {
                throw new InvalidDataException("Known transient context contains invalid candidate evidence.");
            }
            output.Add(parsed.Value.CenterEvidenceId);
        }
        return output.Distinct().ToArray();
    }

    internal async ValueTask PersistCausalExtractionAsync(
        Guid candidateId,
        TransientCandidateExtractionDescriptorV1 extraction,
        CancellationToken cancellationToken)
    {
        var payload = TransientCandidateExtractionJson.Serialize(extraction);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transient_worker_candidates
            SET causal_extraction_json = $payload, causal_extraction_sha256 = $sha, updated_unix_ms = $now
            WHERE candidate_id = $candidate
              AND (causal_extraction_sha256 IS NULL OR causal_extraction_sha256 = $sha);
            """;
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$sha", Convert.ToHexString(SHA256.HashData(payload)));
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        _faultInjector.Inject(TransientRuntimeFaultPoint.BeforeCausalExtractionCommit);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new TransientCandidateIdentityConflictException("Transient causal extraction identity conflicts with durable runtime state.");
        }
        _faultInjector.Inject(TransientRuntimeFaultPoint.AfterCausalExtractionCommit);
    }

    internal ValueTask PersistObservationExtractionAsync(
        Guid candidateId,
        TransientCandidateExtractionDescriptorV1 extraction,
        CancellationToken cancellationToken)
        => PersistCanonicalRuntimePayloadAsync(
            candidateId,
            "observation_extraction_json",
            "observation_extraction_sha256",
            TransientCandidateExtractionJson.Serialize(extraction),
            TransientRuntimeFaultPoint.BeforeObservationExtractionCommit,
            TransientRuntimeFaultPoint.AfterObservationExtractionCommit,
            cancellationToken);

    internal ValueTask PersistAssessmentExecutionAsync(
        Guid candidateId,
        TransientAssessmentExecutionDescriptorV1 assessment,
        CancellationToken cancellationToken)
        => PersistCanonicalRuntimePayloadAsync(
            candidateId,
            "assessment_execution_json",
            "assessment_execution_sha256",
            TransientAssessmentJson.Serialize(assessment),
            TransientRuntimeFaultPoint.BeforeAssessmentCommit,
            TransientRuntimeFaultPoint.AfterAssessmentCommit,
            cancellationToken);

    internal async ValueTask<IReadOnlyList<TransientRuntimeCandidate>> ReadEventCandidatesAsync(
        Guid eventId,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.candidate_id, c.event_id, c.target_raw_capture_row_id, c.slot_ordinal,
                   c.observation_id, c.assessment_id, c.event_version_id, c.allocated_unix_ms,
                   c.association_ambiguous, c.attempt_count, c.available_unix_ms, c.causal_extraction_json,
                   c.observation_extraction_json, c.assessment_execution_json
            FROM transient_worker_candidates c
            JOIN raw_captures r ON r.raw_capture_row_id = c.target_raw_capture_row_id
            WHERE c.event_id = $event AND c.state != 'quarantined'
            ORDER BY r.capture_sequence, c.slot_ordinal;
            """;
        command.Parameters.AddWithValue("$event", eventId.ToString("N"));
        return await ReadCandidatesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask MarkCausalCompletionAsync(
        long rawCaptureRowId,
        string reason,
        bool succeeded,
        CancellationToken cancellationToken)
        => SetFrameStateAsync(
            rawCaptureRowId,
            "history",
            reason,
            null,
            succeeded,
            cancellationToken,
            TransientRuntimeFaultPoint.BeforeFrameHistoryCommit,
            TransientRuntimeFaultPoint.AfterFrameHistoryCommit);

    internal ValueTask MarkRetryAsync(
        long rawCaptureRowId,
        string reason,
        int attemptCount,
        DateTimeOffset availableUtc,
        CancellationToken cancellationToken)
        => SetFrameStateAsync(
            rawCaptureRowId, "retry_wait", reason, (attemptCount, availableUtc), null, cancellationToken);

    internal ValueTask QuarantineAsync(long rawCaptureRowId, string reason, CancellationToken cancellationToken)
        => SetFrameStateAsync(rawCaptureRowId, "quarantined", reason, null, false, cancellationToken);

    internal async ValueTask RetireBeforeAsync(
        string agentId,
        long exclusiveSequence,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        var rows = new List<long>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT f.raw_capture_row_id
                FROM transient_worker_frames f
                JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id
                WHERE r.agent_id = $agent AND r.capture_sequence < $sequence AND f.state = 'history';
                """;
            read.Parameters.AddWithValue("$agent", agentId);
            read.Parameters.AddWithValue("$sequence", exclusiveSequence);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(reader.GetInt64(0));
            }
        }
        foreach (var row in rows)
        {
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE transient_worker_frames SET state = 'completed', updated_unix_ms = $now WHERE raw_capture_row_id = $raw;
                UPDATE transient_capture_work SET state = 'completed', updated_unix_ms = $now
                    WHERE raw_capture_row_id = $raw AND state IN ('pending', 'candidate_persisted');
                UPDATE raw_captures SET retention_hold = CASE WHEN
                    EXISTS (SELECT 1 FROM capture_lane_work WHERE raw_capture_row_id = $raw
                        AND ((required = 1 AND state != 'completed') OR state = 'leased'))
                    OR EXISTS (SELECT 1 FROM transient_candidate_sources s JOIN transient_candidates c
                        ON c.candidate_id = s.candidate_id WHERE s.raw_capture_row_id = $raw AND c.source_hold_released = 0)
                    THEN 1 ELSE 0 END WHERE raw_capture_row_id = $raw;
                """;
            update.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            update.Parameters.AddWithValue("$raw", row);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (rows.Count > 0)
        {
            _faultInjector.Inject(TransientRuntimeFaultPoint.BeforeRetirementCommit);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > 0)
        {
            _faultInjector.Inject(TransientRuntimeFaultPoint.AfterRetirementCommit);
        }
    }

    internal async ValueTask MarkCandidateCompletedAsync(Guid candidateId, CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        _faultInjector.Inject(TransientRuntimeFaultPoint.BeforeRuntimeCompletionCommit);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE transient_worker_candidates SET state = 'completed', updated_unix_ms = $now WHERE candidate_id = $candidate;";
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _faultInjector.Inject(TransientRuntimeFaultPoint.AfterRuntimeCompletionCommit);
    }

    public async ValueTask<TransientQuarantineReleaseDisposition> AbandonQuarantinedCaptureAsync(
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(artifactId, Guid.Empty);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var work = connection.CreateCommand())
        {
            work.Transaction = transaction;
            work.CommandText = """
            UPDATE transient_capture_work
            SET state = 'abandoned', updated_unix_ms = $now
            WHERE raw_capture_row_id = (
                SELECT raw_capture_row_id FROM raw_captures WHERE raw_artifact_id = $artifact)
              AND state = 'quarantined';
            """;
            work.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            work.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
            if (await work.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return TransientQuarantineReleaseDisposition.NotFound;
            }
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE transient_worker_frames
            SET state = 'abandoned', failure_reason = 'operator-abandoned', updated_unix_ms = $now
            WHERE raw_capture_row_id = (
                SELECT raw_capture_row_id FROM raw_captures WHERE raw_artifact_id = $artifact)
              AND state = 'quarantined';
            UPDATE raw_captures SET retention_hold = CASE WHEN
                EXISTS (SELECT 1 FROM capture_lane_work WHERE raw_capture_row_id = raw_captures.raw_capture_row_id
                    AND ((required = 1 AND state != 'completed') OR state = 'leased'))
                OR EXISTS (SELECT 1 FROM transient_candidate_sources s JOIN transient_candidates c
                    ON c.candidate_id = s.candidate_id
                    WHERE s.raw_capture_row_id = raw_captures.raw_capture_row_id AND c.source_hold_released = 0)
                THEN 1 ELSE 0 END WHERE raw_artifact_id = $artifact;
            """;
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return TransientQuarantineReleaseDisposition.Abandoned;
    }

    internal async ValueTask MarkCandidateRetryAsync(
        Guid candidateId,
        string reason,
        int attemptCount,
        DateTimeOffset availableUtc,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE transient_worker_candidates
            SET attempt_count = $attempt, available_unix_ms = $available,
                failure_reason = $reason, updated_unix_ms = $now
            WHERE candidate_id = $candidate AND state = 'pending';
            """;
        command.Parameters.AddWithValue("$attempt", attemptCount);
        command.Parameters.AddWithValue("$available", availableUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask QuarantineCandidateAsync(
        Guid candidateId,
        Guid eventId,
        string reason,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var runtime = connection.CreateCommand())
        {
            runtime.Transaction = transaction;
            runtime.CommandText = """
                UPDATE transient_worker_candidates
                SET state = 'quarantined', failure_reason = $reason, updated_unix_ms = $now
                WHERE candidate_id = $candidate;
                """;
            runtime.Parameters.AddWithValue("$reason", reason);
            runtime.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            runtime.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
            await runtime.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        using (var journal = connection.CreateCommand())
        {
            journal.Transaction = transaction;
            journal.CommandText = """
                UPDATE transient_candidates
                SET state = 'needs_review', phase = 'quarantined', quarantine_reason = $reason, updated_unix_ms = $now
                WHERE candidate_id = $candidate;
                INSERT INTO transient_candidate_conflicts(candidate_id, event_id, reason, observed_unix_ms)
                VALUES ($candidate, $event, $reason, $now);
                """;
            journal.Parameters.AddWithValue("$reason", reason);
            journal.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            journal.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
            journal.Parameters.AddWithValue("$event", eventId.ToString("N"));
            await journal.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<TransientRuntimeTotals> ReadTotalsAsync(CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM transient_worker_frames WHERE state IN ('queued', 'retry_wait')),
                (SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'pending'),
                (SELECT COUNT(*) FROM transient_worker_frames WHERE state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_worker_candidates WHERE state = 'quarantined');
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        _ = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new TransientRuntimeTotals(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private async ValueTask SetFrameStateAsync(
        long rawCaptureRowId,
        string state,
        string reason,
        (int Attempt, DateTimeOffset Available)? retry,
        bool? causalSucceeded,
        CancellationToken cancellationToken,
        TransientRuntimeFaultPoint? beforeCommit = null,
        TransientRuntimeFaultPoint? afterCommit = null)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = BeginImmediate(connection);
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE transient_worker_frames
                SET state = $state, failure_reason = $reason,
                    attempt_count = COALESCE($attempt, attempt_count),
                    available_unix_ms = COALESCE($available, available_unix_ms),
                    causal_succeeded = COALESCE($causal_succeeded, causal_succeeded), updated_unix_ms = $now
                WHERE raw_capture_row_id = $raw;
                """;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$attempt", retry?.Attempt ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$available", retry?.Available.ToUnixTimeMilliseconds() ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$causal_succeeded", causalSucceeded.HasValue
                ? causalSucceeded.Value ? 1 : 0
                : DBNull.Value);
            command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$raw", rawCaptureRowId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (state == "quarantined")
        {
            using var quarantine = connection.CreateCommand();
            quarantine.Transaction = transaction;
            quarantine.CommandText = "UPDATE transient_capture_work SET state = 'quarantined', updated_unix_ms = $now WHERE raw_capture_row_id = $raw;";
            quarantine.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            quarantine.Parameters.AddWithValue("$raw", rawCaptureRowId);
            await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        if (beforeCommit.HasValue)
        {
            _faultInjector.Inject(beforeCommit.Value);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (afterCommit.HasValue)
        {
            _faultInjector.Inject(afterCommit.Value);
        }
    }

    private async ValueTask UpdatePressureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var totals = connection.CreateCommand();
        totals.Transaction = transaction;
        totals.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM transient_capture_work WHERE state IN ('pending', 'quarantined')) +
                    (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0),
                (SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures WHERE raw_capture_row_id IN (
                    SELECT raw_capture_row_id FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                    UNION
                    SELECT s.raw_capture_row_id FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE c.source_hold_released = 0)),
                (SELECT MIN(created_unix_ms) FROM (
                    SELECT created_unix_ms FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                    UNION ALL
                    SELECT created_unix_ms FROM transient_candidates WHERE source_hold_released = 0)),
                (SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidate_conflicts),
                (SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient');
            """;
        using var reader = await totals.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Transient pressure state is unavailable.");
        }
        var count = reader.GetInt64(0);
        var bytes = reader.GetInt64(1);
        var age = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
            ? TimeSpan.Zero
            : _timeProvider.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
        var quarantined = reader.GetInt64(3);
        var prior = reader.GetInt32(4);
        await reader.DisposeAsync().ConfigureAwait(false);
        var maximumCount = _required
            ? _limits.RequiredMaximumPendingCount
            : _limits.OptionalMaximumPendingCount;
        var maximumBytes = _required
            ? _limits.RequiredMaximumPendingBytes
            : _limits.OptionalMaximumPendingBytes;
        var maximumAge = _required
            ? _limits.RequiredMaximumOldestAgeMinutes
            : _limits.OptionalMaximumOldestAgeMinutes;
        var hard = quarantined > 0 || count >= maximumCount || bytes >= maximumBytes ||
                   age >= TimeSpan.FromMinutes(maximumAge);
        var recovered = quarantined == 0 &&
                        count * 100 < maximumCount * _limits.PressureRecoveryPercent &&
                        !CaptureLanePressureMath.IsAtOrAbovePercentage(
                            bytes, maximumBytes, _limits.PressureRecoveryPercent) &&
                        age.TotalMinutes * 100 < maximumAge * _limits.PressureRecoveryPercent;
        var warning = count * 100 >= maximumCount * _limits.PressureRecoveryPercent ||
                      CaptureLanePressureMath.IsAtOrAbovePercentage(
                          bytes, maximumBytes, _limits.PressureRecoveryPercent) ||
                      age.TotalMinutes * 100 >= maximumAge * _limits.PressureRecoveryPercent;
        var next = hard || prior == 2 && !recovered ? 2 : warning ? 1 : 0;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE capture_lane_definitions SET pressure_state = $pressure WHERE lane_name = 'transient';";
        update.Parameters.AddWithValue("$pressure", next);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Column names are selected only by private constant call sites; values remain parameterized.")]
    private async ValueTask PersistCanonicalRuntimePayloadAsync(
        Guid candidateId,
        string payloadColumn,
        string hashColumn,
        byte[] payload,
        TransientRuntimeFaultPoint beforeCommit,
        TransientRuntimeFaultPoint afterCommit,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE transient_worker_candidates
            SET {payloadColumn} = $payload, {hashColumn} = $hash, updated_unix_ms = $now
            WHERE candidate_id = $candidate AND ({hashColumn} IS NULL OR {hashColumn} = $hash);
            """;
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        _faultInjector.Inject(beforeCommit);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new TransientCandidateIdentityConflictException(
                "Transient runtime canonical payload conflicts with durable state.");
        }
        _faultInjector.Inject(afterCommit);
    }

    private async ValueTask ReconcileAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        using var transaction = BeginImmediate(connection);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE transient_worker_candidates
            SET state = 'completed', updated_unix_ms = $now
            WHERE candidate_id IN (
                SELECT candidate_id FROM transient_candidates
                WHERE phase IN ('finalized', 'handoff_pending', 'acknowledged'));

            UPDATE transient_worker_frames
            SET state = 'history', causal_succeeded = 1,
                failure_reason = 'reconciled-causal-commit', updated_unix_ms = $now
            WHERE state IN ('queued', 'retry_wait')
              AND EXISTS (
                SELECT 1 FROM transient_worker_candidates c
                WHERE c.target_raw_capture_row_id = transient_worker_frames.raw_capture_row_id)
              AND NOT EXISTS (
                SELECT 1
                FROM transient_worker_candidates c
                LEFT JOIN transient_candidates j ON j.candidate_id = c.candidate_id
                WHERE c.target_raw_capture_row_id = transient_worker_frames.raw_capture_row_id
                  AND (c.causal_extraction_json IS NULL OR j.candidate_payload IS NULL));
            """;
        command.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask MigrateLegacyFrameSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'transient_worker_frames';";
        var schema = Convert.ToString(
            await inspect.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (schema?.Contains("causal_succeeded", StringComparison.Ordinal) == true &&
            schema.Contains("'abandoned'", StringComparison.Ordinal))
        {
            return;
        }
        using var transaction = BeginImmediate(connection);
        using var migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = LegacyFrameMigrationSql;
        await migrate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<TransientRuntimeCandidate>> ReadCandidatesAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var output = new List<TransientRuntimeCandidate>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            TransientCandidateExtractionDescriptorV1? extraction = null;
            if (!await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false))
            {
                extraction = TransientCandidateExtractionJson.Parse(
                    await reader.GetFieldValueAsync<byte[]>(11, cancellationToken).ConfigureAwait(false));
            }
            TransientCandidateExtractionDescriptorV1? observationExtraction = null;
            if (!await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false))
            {
                observationExtraction = TransientCandidateExtractionJson.Parse(
                    await reader.GetFieldValueAsync<byte[]>(12, cancellationToken).ConfigureAwait(false));
            }
            TransientAssessmentExecutionDescriptorV1? assessmentExecution = null;
            if (!await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false))
            {
                assessmentExecution = TransientAssessmentJson.Parse(
                    await reader.GetFieldValueAsync<byte[]>(13, cancellationToken).ConfigureAwait(false));
            }
            output.Add(new TransientRuntimeCandidate(
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                reader.GetInt64(2),
                reader.GetInt32(3),
                Guid.ParseExact(reader.GetString(4), "N"),
                Guid.ParseExact(reader.GetString(5), "N"),
                Guid.ParseExact(reader.GetString(6), "N"),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
                reader.GetBoolean(8),
                reader.GetInt32(9),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)),
                extraction,
                observationExtraction,
                assessmentExecution));
        }
        return output;
    }

    private static FileStream OpenEvidence(string path)
        => new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async ValueTask<byte[]> ReadExactlyAsync(
        FileStream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var output = new byte[length];
        await stream.ReadExactlyAsync(output, cancellationToken).ConfigureAwait(false);
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException("Transient evidence length changed after its durable snapshot.");
        }
        return output;
    }

    private string Resolve(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Transient runtime source path is not a safe relative path.");
        }
        var resolved = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transient runtime source path escapes the ingress root.");
        }
        return resolved;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated busy timeout is a validated integer option; no SQL value is user supplied.")]
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutSeconds * 1000}; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
    }

    private const string RuntimeSchemaSql = """
        CREATE TABLE IF NOT EXISTS transient_worker_frames (
            raw_capture_row_id INTEGER PRIMARY KEY,
            state TEXT NOT NULL CHECK (state IN ('queued', 'history', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
            available_unix_ms INTEGER NOT NULL,
            causal_succeeded INTEGER NOT NULL DEFAULT 0 CHECK (causal_succeeded IN (0, 1)),
            failure_reason TEXT,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_transient_worker_frames_ready
            ON transient_worker_frames(state, available_unix_ms, raw_capture_row_id);
        CREATE TABLE IF NOT EXISTS transient_worker_candidates (
            candidate_id TEXT PRIMARY KEY,
            event_id TEXT NOT NULL,
            target_raw_capture_row_id INTEGER NOT NULL,
            slot_ordinal INTEGER NOT NULL CHECK (slot_ordinal >= 0),
            observation_id TEXT NOT NULL,
            assessment_id TEXT NOT NULL,
            event_version_id TEXT NOT NULL,
            allocated_unix_ms INTEGER NOT NULL,
            association_ambiguous INTEGER NOT NULL CHECK (association_ambiguous IN (0, 1)),
            attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
            available_unix_ms INTEGER NOT NULL,
            causal_extraction_json BLOB,
            causal_extraction_sha256 TEXT CHECK (causal_extraction_sha256 IS NULL OR length(causal_extraction_sha256) = 64),
            observation_extraction_json BLOB,
            observation_extraction_sha256 TEXT CHECK (observation_extraction_sha256 IS NULL OR length(observation_extraction_sha256) = 64),
            assessment_execution_json BLOB,
            assessment_execution_sha256 TEXT CHECK (assessment_execution_sha256 IS NULL OR length(assessment_execution_sha256) = 64),
            failure_reason TEXT,
            state TEXT NOT NULL CHECK (state IN ('pending', 'completed', 'quarantined')),
            updated_unix_ms INTEGER,
            UNIQUE (target_raw_capture_row_id, slot_ordinal),
            FOREIGN KEY (target_raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
        ) STRICT;
        CREATE INDEX IF NOT EXISTS ix_transient_worker_candidates_pending
            ON transient_worker_candidates(state, allocated_unix_ms, candidate_id);
        """;

    private const string LegacyFrameMigrationSql = """
        DROP INDEX ix_transient_worker_frames_ready;
        ALTER TABLE transient_worker_frames RENAME TO transient_worker_frames_legacy;
        CREATE TABLE transient_worker_frames (
            raw_capture_row_id INTEGER PRIMARY KEY,
            state TEXT NOT NULL CHECK (state IN ('queued', 'history', 'retry_wait', 'completed', 'quarantined', 'abandoned')),
            attempt_count INTEGER NOT NULL CHECK (attempt_count >= 0),
            available_unix_ms INTEGER NOT NULL,
            causal_succeeded INTEGER NOT NULL DEFAULT 0 CHECK (causal_succeeded IN (0, 1)),
            failure_reason TEXT,
            created_unix_ms INTEGER NOT NULL,
            updated_unix_ms INTEGER NOT NULL,
            FOREIGN KEY (raw_capture_row_id) REFERENCES raw_captures(raw_capture_row_id)
        ) STRICT;
        INSERT INTO transient_worker_frames(
            raw_capture_row_id, state, attempt_count, available_unix_ms, causal_succeeded,
            failure_reason, created_unix_ms, updated_unix_ms)
        SELECT raw_capture_row_id, state, attempt_count, available_unix_ms, 0,
               failure_reason, created_unix_ms, updated_unix_ms
        FROM transient_worker_frames_legacy;
        DROP TABLE transient_worker_frames_legacy;
        CREATE INDEX ix_transient_worker_frames_ready
            ON transient_worker_frames(state, available_unix_ms, raw_capture_row_id);
        """;

    private sealed record TransientEvidenceSnapshot(
        int Offset,
        long PayloadLength,
        string PayloadSha256,
        byte[] ManifestJson,
        ArtifactManifestV2 Manifest,
        FileStream PayloadStream,
        FileStream SidecarStream,
        TransientRuntimeFrame Runtime) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await PayloadStream.DisposeAsync().ConfigureAwait(false);
            await SidecarStream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
