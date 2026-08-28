using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

public enum TransientRuntimeOperationDisposition
{
    Applied,
    Duplicate
}

public sealed record TransientRuntimeQuarantineCursor(long UpdatedUnixMs, long RawCaptureRowId);

public sealed record TransientRuntimeQuarantineRecord(
    long RawCaptureRowId,
    long LaneWorkId,
    long OuterLaneWorkId,
    string AgentId,
    long CaptureSequence,
    Guid CaptureId,
    Guid ArtifactId,
    string ManifestSha256,
    string PayloadSha256,
    string ProcessingProfileName,
    string ProcessingProfileVersion,
    string ProcessingProfileSha256,
    string Mode,
    bool Required,
    string OuterLaneState,
    string WorkState,
    string FrameState,
    string FailureReason,
    int AttemptCount,
    long PayloadBytes,
    bool RetentionHold,
    DateTimeOffset CreatedUtc,
    DateTimeOffset OuterLaneUpdatedUtc,
    DateTimeOffset WorkUpdatedUtc,
    DateTimeOffset FrameUpdatedUtc,
    TransientRuntimeQuarantineCursor Cursor);

public sealed record TransientRuntimeQuarantinePage(
    IReadOnlyList<TransientRuntimeQuarantineRecord> Items,
    TransientRuntimeQuarantineCursor? NextCursor);

public sealed record TransientRuntimeExternalOwnershipEvidence(
    string DeploymentRunId,
    string InventorySha256,
    bool LegacyOwnershipExternallyEstablished)
{
    public static bool TryCreate(
        string? deploymentRunId,
        string? inventorySha256,
        bool legacyOwnershipExternallyEstablished,
        out TransientRuntimeExternalOwnershipEvidence? evidence)
    {
        evidence = null;
        if (!legacyOwnershipExternallyEstablished || string.IsNullOrEmpty(deploymentRunId) ||
            deploymentRunId.Length > 128 || !char.IsAsciiLetterOrDigit(deploymentRunId[0]) ||
            deploymentRunId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-')) ||
            inventorySha256?.Length != 64 || !inventorySha256.All(Uri.IsHexDigit))
        {
            return false;
        }
        evidence = new TransientRuntimeExternalOwnershipEvidence(
            deploymentRunId,
            inventorySha256.ToUpperInvariant(),
            LegacyOwnershipExternallyEstablished: true);
        return true;
    }
}

public sealed record TransientRuntimeOperationTarget(
    long RawCaptureRowId,
    long LaneWorkId,
    long OuterLaneWorkId,
    string AgentId,
    long CaptureSequence,
    Guid CaptureId,
    Guid ArtifactId,
    string ManifestSha256,
    string PayloadSha256,
    string ProcessingProfileSha256,
    string Mode,
    bool Required,
    string ExpectedOuterLaneState,
    string ExpectedWorkState,
    string ExpectedFrameState,
    string ExpectedFailureReason,
    DateTimeOffset ExpectedOuterLaneUpdatedUtc,
    DateTimeOffset ExpectedWorkUpdatedUtc,
    DateTimeOffset ExpectedFrameUpdatedUtc,
    TransientRuntimeExternalOwnershipEvidence? ExternalOwnershipEvidence = null);

public sealed record TransientRuntimeOperationReceipt(
    TransientRuntimeOperationDisposition Disposition,
    string State,
    string Actor,
    string ReasonCode,
    DateTimeOffset CompletedUtc,
    string DeploymentRunId,
    string InventorySha256);

public interface ITransientRuntimeManagement
{
    ValueTask<TransientRuntimeQuarantinePage> ReadQuarantinePageAsync(
        int pageSize,
        TransientRuntimeQuarantineCursor? cursor,
        CancellationToken cancellationToken);

    ValueTask<TransientRuntimeOperationReceipt> AbandonQuarantinedCaptureAsync(
        TransientRuntimeOperationTarget target,
        string idempotencyKey,
        string actor,
        string reasonCode,
        CancellationToken cancellationToken);
}

internal sealed class TransientRuntimeManagement(
    IRawCaptureIngress rawIngress,
    SqliteTransientRuntimeStore store) : ITransientRuntimeManagement
{
    public async ValueTask<TransientRuntimeQuarantinePage> ReadQuarantinePageAsync(
        int pageSize,
        TransientRuntimeQuarantineCursor? cursor,
        CancellationToken cancellationToken)
    {
        SqliteTransientRuntimeStore.ValidateQuarantineQuery(pageSize, cursor);
        await rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await store.ReadQuarantinePageAsync(pageSize, cursor, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<TransientRuntimeOperationReceipt> AbandonQuarantinedCaptureAsync(
        TransientRuntimeOperationTarget target,
        string idempotencyKey,
        string actor,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        SqliteTransientRuntimeStore.ValidateOperation(target, idempotencyKey, actor, reasonCode);
        await rawIngress.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await store.AbandonQuarantinedCaptureAsync(
            target, idempotencyKey, actor, reasonCode, cancellationToken).ConfigureAwait(false);
    }
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

internal sealed class SqliteTransientRuntimeStore : ITransientRuntimeManagement, IDisposable
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> CanonicalRuntimeSchemaDefinitions =
        new(CreateCanonicalRuntimeSchemaDefinitions);
    private readonly string _root;
    private readonly string _databasePath;
    private readonly int _busyTimeoutSeconds;
    private readonly TimeProvider _timeProvider;
    private readonly ITransientRuntimeFaultInjector _faultInjector;
    private readonly TransientWorkerTelemetry _telemetry;
    private readonly CaptureDistributionOptions _limits;
    private readonly bool _required;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

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
        if (Volatile.Read(ref _initialized))
        {
            return;
        }
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }
            if (!File.Exists(_databasePath))
            {
                throw new InvalidOperationException("Raw ingress schema 11 must initialize before transient runtime state.");
            }
            EnsureDatabaseFilesArePhysical();
            var inspection = await InspectRuntimeSchemaAsync(cancellationToken).ConfigureAwait(false);
            if (inspection.RawVersion != SqliteRawCaptureJournal.CurrentSchemaVersion)
            {
                throw new InvalidOperationException(
                    $"Raw ingress schema {inspection.RawVersion} is unsupported; schema {SqliteRawCaptureJournal.CurrentSchemaVersion} must initialize before transient runtime state.");
            }
            using var connection = await OpenUnconfiguredAsync(cancellationToken).ConfigureAwait(false);
            using (var transaction = BeginImmediate(connection))
            {
                var rawVersion = await ExecuteScalarLongAsync(
                    connection, "PRAGMA user_version;", transaction, cancellationToken).ConfigureAwait(false);
                var runtimeObjectCount = await CountRuntimeSchemaObjectsAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);
                if (rawVersion != SqliteRawCaptureJournal.CurrentSchemaVersion)
                {
                    throw new InvalidOperationException("Raw ingress schema changed during transient runtime initialization.");
                }
                if (inspection.RuntimeObjectCount == 0 && runtimeObjectCount == 0)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = RuntimeSchemaSql;
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
                await ValidateRuntimeSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await SqliteRawCaptureJournal.ValidateCanonicalSchemaDefinitionsAsync(
                    connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
            await VerifyOperationsSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            await ReconcileAsync(connection, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializeGate.Release();
        }
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

    public async ValueTask<TransientRuntimeQuarantinePage> ReadQuarantinePageAsync(
        int pageSize,
        TransientRuntimeQuarantineCursor? cursor,
        CancellationToken cancellationToken)
    {
        ValidateQuarantineQuery(pageSize, cursor);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = CreateQuarantineReadCommand(connection, transaction: null);
        command.CommandText += "\n" + """
            WHERE f.state = 'quarantined' AND ($cursor_updated IS NULL OR
                f.updated_unix_ms < $cursor_updated OR
                (f.updated_unix_ms = $cursor_updated AND f.raw_capture_row_id < $cursor_raw))
            ORDER BY f.updated_unix_ms DESC, f.raw_capture_row_id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$cursor_updated", cursor is null ? DBNull.Value : cursor.UpdatedUnixMs);
        command.Parameters.AddWithValue("$cursor_raw", cursor is null ? DBNull.Value : cursor.RawCaptureRowId);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        var items = new List<TransientRuntimeQuarantineRecord>(pageSize + 1);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(await ReadQuarantineRecordAsync(reader, cancellationToken).ConfigureAwait(false));
        }
        var next = items.Count > pageSize ? items[pageSize - 1].Cursor : null;
        if (items.Count > pageSize)
        {
            items.RemoveAt(pageSize);
        }
        return new TransientRuntimeQuarantinePage(items, next);
    }

    public async ValueTask<TransientRuntimeOperationReceipt> AbandonQuarantinedCaptureAsync(
        TransientRuntimeOperationTarget target,
        string idempotencyKey,
        string actor,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ValidateOperation(target, idempotencyKey, actor, reasonCode);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            var duplicate = await ReadOperationReceiptAsync(
                connection, null, target, idempotencyKey, actor, reasonCode, cancellationToken)
                .ConfigureAwait(false);
            if (duplicate is not null)
            {
                return duplicate;
            }

            {
                using var evidenceRead = CreateQuarantineReadCommand(connection, transaction: null);
                evidenceRead.CommandText += "\nWHERE f.raw_capture_row_id = $raw;";
                evidenceRead.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
                using var evidenceReader = await evidenceRead.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await evidenceReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new OutboxOperationCollisionException("Transient runtime quarantine no longer exists.");
                }
                var evidence = await ReadQuarantineRecordAsync(evidenceReader, cancellationToken).ConfigureAwait(false);
                EnsureExactTarget(evidence, target);
            }
            await VerifyPayloadAsync(connection, target, cancellationToken).ConfigureAwait(false);

            using var transaction = BeginImmediate(connection);
            duplicate = await ReadOperationReceiptAsync(
                connection, transaction, target, idempotencyKey, actor, reasonCode, cancellationToken)
                .ConfigureAwait(false);
            if (duplicate is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return duplicate;
            }
            using var read = CreateQuarantineReadCommand(connection, transaction);
            read.CommandText += "\nWHERE f.raw_capture_row_id = $raw;";
            read.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
            using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new OutboxOperationCollisionException("Transient runtime quarantine no longer exists.");
            }
            var current = await ReadQuarantineRecordAsync(reader, cancellationToken).ConfigureAwait(false);
            await reader.DisposeAsync().ConfigureAwait(false);
            EnsureExactTarget(current, target);

            var now = DateTimeOffset.FromUnixTimeMilliseconds(
                _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
            using (var work = connection.CreateCommand())
            {
                work.Transaction = transaction;
                work.CommandText = """
                    UPDATE transient_capture_work
                    SET state = 'abandoned', updated_unix_ms = $now
                    WHERE raw_capture_row_id = $raw AND lane_work_id = $lane_work
                      AND state = 'quarantined' AND updated_unix_ms = $expected;
                    """;
                work.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                work.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
                work.Parameters.AddWithValue("$lane_work", target.LaneWorkId);
                work.Parameters.AddWithValue("$expected", target.ExpectedWorkUpdatedUtc.ToUnixTimeMilliseconds());
                if (await work.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new OutboxOperationCollisionException("Transient lane work changed before abandonment.");
                }
            }
            using (var frame = connection.CreateCommand())
            {
                frame.Transaction = transaction;
                frame.CommandText = """
                    UPDATE transient_worker_frames
                    SET state = 'abandoned', failure_reason = 'operator-abandoned', updated_unix_ms = $now
                    WHERE raw_capture_row_id = $raw AND state = 'quarantined'
                      AND updated_unix_ms = $expected AND failure_reason = $failure;
                    """;
                frame.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                frame.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
                frame.Parameters.AddWithValue("$expected", target.ExpectedFrameUpdatedUtc.ToUnixTimeMilliseconds());
                frame.Parameters.AddWithValue("$failure", target.ExpectedFailureReason);
                if (await frame.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new OutboxOperationCollisionException("Transient worker frame changed before abandonment.");
                }
            }
            await InsertOperationReceiptAsync(
                connection, transaction, target, idempotencyKey, actor, reasonCode, now, cancellationToken)
                .ConfigureAwait(false);
            using (var hold = connection.CreateCommand())
            {
                hold.Transaction = transaction;
                hold.CommandText = """
                    UPDATE raw_captures SET retention_hold = CASE WHEN
                        EXISTS (SELECT 1 FROM capture_lane_work WHERE raw_capture_row_id = $raw
                            AND ((required = 1 AND state != 'completed') OR state = 'leased'))
                        OR EXISTS (SELECT 1 FROM transient_candidate_sources s JOIN transient_candidates c
                            ON c.candidate_id = s.candidate_id
                            WHERE s.raw_capture_row_id = $raw AND c.source_hold_released = 0)
                        THEN 1 ELSE 0 END WHERE raw_capture_row_id = $raw;
                    """;
                hold.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
                if (await hold.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidDataException("Transient runtime source disappeared before hold release.");
                }
            }
            await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TransientRuntimeOperationReceipt(
                TransientRuntimeOperationDisposition.Applied, "abandoned", actor, reasonCode, now,
                target.ExternalOwnershipEvidence!.DeploymentRunId,
                target.ExternalOwnershipEvidence.InventorySha256);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            throw new OutboxOperationCollisionException("Transient runtime operation collided with durable state.", exception);
        }
        finally
        {
            lifecycleGate.Release();
        }
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

    private static SqliteCommand CreateQuarantineReadCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT f.raw_capture_row_id, w.lane_work_id, lane.work_id,
                   r.agent_id, r.capture_sequence, r.capture_id, r.raw_artifact_id, w.artifact_id,
                   r.manifest_sha256, w.manifest_sha256, r.manifest_json,
                   w.mode, w.required, lane.lane_name, lane.required,
                   lane.state, w.state, f.state, f.failure_reason, f.attempt_count,
                   r.payload_length, r.retention_hold, f.created_unix_ms,
                   lane.updated_unix_ms, w.updated_unix_ms, f.updated_unix_ms,
                   r.state, lane.lease_token, lane.lease_owner, lane.lease_expires_unix_ms,
                   r.payload_sha256
            FROM transient_worker_frames f
            JOIN transient_capture_work w ON w.raw_capture_row_id = f.raw_capture_row_id
            JOIN capture_lane_work lane ON lane.work_id = w.lane_work_id
            JOIN raw_captures r ON r.raw_capture_row_id = f.raw_capture_row_id
            """;
        return command;
    }

    private static async ValueTask<TransientRuntimeQuarantineRecord> ReadQuarantineRecordAsync(
        SqliteDataReader reader,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(reader.GetString(5), "N", out var captureId) ||
            !Guid.TryParseExact(reader.GetString(6), "N", out var artifactId) ||
            !string.Equals(reader.GetString(6), reader.GetString(7), StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(8), reader.GetString(9), StringComparison.Ordinal) ||
            reader.GetString(8).Length != 64 ||
            !string.Equals(reader.GetString(13), "transient", StringComparison.Ordinal) ||
            reader.GetBoolean(12) != reader.GetBoolean(14) ||
            !string.Equals(reader.GetString(15), "completed", StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(16), "quarantined", StringComparison.Ordinal) ||
            !string.Equals(reader.GetString(17), "quarantined", StringComparison.Ordinal) ||
            await reader.IsDBNullAsync(18, cancellationToken).ConfigureAwait(false) ||
            !reader.GetBoolean(21) ||
            !string.Equals(reader.GetString(26), "committed", StringComparison.Ordinal) ||
            !await reader.IsDBNullAsync(27, cancellationToken).ConfigureAwait(false) ||
            !await reader.IsDBNullAsync(28, cancellationToken).ConfigureAwait(false) ||
            !await reader.IsDBNullAsync(29, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException("Transient runtime quarantine contains malformed or unheld durable state.");
        }
        var manifestJson = await reader.GetFieldValueAsync<byte[]>(10, cancellationToken).ConfigureAwait(false);
        var parsed = CaptureContractJson.ParseManifest(manifestJson);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null ||
            !string.Equals(CaptureContractJson.ComputeManifestSha256(manifestJson), reader.GetString(8), StringComparison.Ordinal) ||
            manifest.Descriptor.Capture.CaptureId != captureId ||
            manifest.Descriptor.Capture.CaptureSequence != reader.GetInt64(4) ||
            !string.Equals(manifest.Descriptor.Capture.AgentId, reader.GetString(3), StringComparison.Ordinal) ||
            manifest.Descriptor.Artifact.ArtifactId != artifactId ||
            !string.Equals(manifest.Descriptor.Artifact.ChecksumSha256, reader.GetString(30), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transient runtime quarantine source manifest is invalid or unrelated.");
        }
        var profile = manifest.Descriptor.Profiles.Processing;
        if (string.IsNullOrWhiteSpace(profile.Name) || string.IsNullOrWhiteSpace(profile.Version) ||
            profile.Sha256.Length != 64)
        {
            throw new InvalidDataException("Transient runtime quarantine processing profile identity is malformed.");
        }
        var frameUpdated = reader.GetInt64(25);
        return new TransientRuntimeQuarantineRecord(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), reader.GetInt64(4),
            captureId, artifactId, reader.GetString(8), reader.GetString(30), profile.Name, profile.Version, profile.Sha256,
            reader.GetString(11), reader.GetBoolean(12), reader.GetString(15), reader.GetString(16), reader.GetString(17),
            reader.GetString(18), reader.GetInt32(19), reader.GetInt64(20), reader.GetBoolean(21),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(22)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(23)),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(24)),
            DateTimeOffset.FromUnixTimeMilliseconds(frameUpdated),
            new TransientRuntimeQuarantineCursor(frameUpdated, reader.GetInt64(0)));
    }

    internal static void ValidateQuarantineQuery(
        int pageSize,
        TransientRuntimeQuarantineCursor? cursor)
    {
        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }
        if (cursor is { RawCaptureRowId: < 1 })
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }
    }

    internal static void ValidateOperation(
        TransientRuntimeOperationTarget target,
        string idempotencyKey,
        string actor,
        string reasonCode)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.RawCaptureRowId < 1 || target.LaneWorkId < 1 || target.OuterLaneWorkId < 1 ||
            target.CaptureSequence < 1 || target.CaptureId == Guid.Empty || target.ArtifactId == Guid.Empty ||
            string.IsNullOrWhiteSpace(target.AgentId) || target.AgentId.Length > 128 ||
            target.ManifestSha256.Length != 64 || target.PayloadSha256.Length != 64 ||
            target.ProcessingProfileSha256.Length != 64 ||
            target.Mode is not ("edge" or "hybrid") ||
            target.ExpectedOuterLaneState != "completed" || target.ExpectedWorkState != "quarantined" ||
            target.ExpectedFrameState != "quarantined" || string.IsNullOrWhiteSpace(target.ExpectedFailureReason) ||
            target.ExpectedFailureReason.Length > 128 || string.IsNullOrWhiteSpace(idempotencyKey) ||
            idempotencyKey.Length > 128 || idempotencyKey.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(actor) || actor.Length > 128 || actor.Any(char.IsControl) ||
            target.ExternalOwnershipEvidence is not { } ownership ||
            !TransientRuntimeExternalOwnershipEvidence.TryCreate(
                ownership.DeploymentRunId,
                ownership.InventorySha256,
                ownership.LegacyOwnershipExternallyEstablished,
                out var normalizedOwnership) || ownership != normalizedOwnership ||
            !OutboxOperationsReasonCodes.IsAllowed(OutboxOperationAction.Abandon, reasonCode))
        {
            throw new ArgumentException("Transient runtime operation is invalid.");
        }
    }

    private static void EnsureExactTarget(
        TransientRuntimeQuarantineRecord current,
        TransientRuntimeOperationTarget target)
    {
        if (current.RawCaptureRowId != target.RawCaptureRowId || current.LaneWorkId != target.LaneWorkId ||
            current.OuterLaneWorkId != target.OuterLaneWorkId || current.CaptureSequence != target.CaptureSequence ||
            current.CaptureId != target.CaptureId || current.ArtifactId != target.ArtifactId ||
            !string.Equals(current.AgentId, target.AgentId, StringComparison.Ordinal) ||
            !string.Equals(current.ManifestSha256, target.ManifestSha256, StringComparison.Ordinal) ||
            !string.Equals(current.PayloadSha256, target.PayloadSha256, StringComparison.Ordinal) ||
            !string.Equals(current.ProcessingProfileSha256, target.ProcessingProfileSha256, StringComparison.Ordinal) ||
            !string.Equals(current.Mode, target.Mode, StringComparison.Ordinal) || current.Required != target.Required ||
            !string.Equals(current.OuterLaneState, target.ExpectedOuterLaneState, StringComparison.Ordinal) ||
            !string.Equals(current.WorkState, target.ExpectedWorkState, StringComparison.Ordinal) ||
            !string.Equals(current.FrameState, target.ExpectedFrameState, StringComparison.Ordinal) ||
            !string.Equals(current.FailureReason, target.ExpectedFailureReason, StringComparison.Ordinal) ||
            current.OuterLaneUpdatedUtc != target.ExpectedOuterLaneUpdatedUtc ||
            current.WorkUpdatedUtc != target.ExpectedWorkUpdatedUtc ||
            current.FrameUpdatedUtc != target.ExpectedFrameUpdatedUtc)
        {
            throw new OutboxOperationCollisionException("Transient runtime identity or state changed.");
        }
    }

    private async ValueTask VerifyPayloadAsync(
        SqliteConnection connection,
        TransientRuntimeOperationTarget target,
        CancellationToken cancellationToken)
    {
        string payloadRelativePath;
        string sidecarRelativePath;
        long payloadLength;
        byte[] manifestJson;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT payload_relative_path, sidecar_relative_path, payload_length, payload_sha256,
                       manifest_json, manifest_sha256, state, retention_hold
                FROM raw_captures WHERE raw_capture_row_id = $raw;
                """;
            command.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ||
                reader.GetString(3) != target.PayloadSha256 || reader.GetString(5) != target.ManifestSha256 ||
                reader.GetString(6) != "committed" || !reader.GetBoolean(7))
            {
                throw new InvalidDataException("Transient runtime source evidence or retention hold changed.");
            }
            payloadRelativePath = reader.GetString(0);
            sidecarRelativePath = reader.GetString(1);
            payloadLength = reader.GetInt64(2);
            manifestJson = await reader.GetFieldValueAsync<byte[]>(4, cancellationToken).ConfigureAwait(false);
        }
        var payloadPath = Resolve(payloadRelativePath);
        var sidecarPath = Resolve(sidecarRelativePath);
        if (!File.Exists(payloadPath) || !File.Exists(sidecarPath) || new FileInfo(payloadPath).Length != payloadLength)
        {
            throw new InvalidDataException("Transient runtime source evidence is unavailable.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, payloadPath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, sidecarPath);
        using (var payload = OpenEvidence(payloadPath))
        {
            var checksum = Convert.ToHexString(
                await SHA256.HashDataAsync(payload, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(checksum, target.PayloadSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Transient runtime source payload checksum is invalid.");
            }
        }
        using var sidecar = OpenEvidence(sidecarPath);
        var sidecarBytes = await ReadExactlyAsync(sidecar, manifestJson.Length, cancellationToken).ConfigureAwait(false);
        if (!sidecarBytes.AsSpan().SequenceEqual(manifestJson))
        {
            throw new InvalidDataException("Transient runtime source sidecar differs from its durable manifest.");
        }
    }

    private static async ValueTask<TransientRuntimeOperationReceipt?> ReadOperationReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        TransientRuntimeOperationTarget target,
        string idempotencyKey,
        string actor,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT raw_capture_row_id, lane_work_id, outer_lane_work_id, agent_id, capture_sequence,
                   capture_id, artifact_id, manifest_sha256, payload_sha256, processing_profile_sha256, mode, required,
                   expected_outer_lane_state, expected_work_state, expected_frame_state, expected_failure_reason,
                   expected_outer_lane_updated_unix_ms, expected_work_updated_unix_ms,
                    expected_frame_updated_unix_ms, deployment_run_id, inventory_sha256,
                    legacy_ownership_externally_established, action, actor, reason_code, result_state, completed_unix_ms,
                    receipt_identity_sha256,
                    hvo_sha256(json_array(
                        idempotency_key, raw_capture_row_id, lane_work_id, outer_lane_work_id, agent_id,
                        capture_sequence, capture_id, artifact_id, manifest_sha256, payload_sha256,
                        processing_profile_sha256, mode, required, expected_outer_lane_state, expected_work_state,
                        expected_frame_state, expected_failure_reason, expected_outer_lane_updated_unix_ms,
                        expected_work_updated_unix_ms, expected_frame_updated_unix_ms, deployment_run_id,
                        inventory_sha256, legacy_ownership_externally_established, action, actor, reason_code,
                        result_state, completed_unix_ms))
            FROM transient_runtime_operations WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        if (reader.GetInt64(0) != target.RawCaptureRowId || reader.GetInt64(1) != target.LaneWorkId ||
            reader.GetInt64(2) != target.OuterLaneWorkId || !string.Equals(reader.GetString(3), target.AgentId, StringComparison.Ordinal) ||
            reader.GetInt64(4) != target.CaptureSequence || reader.GetString(5) != target.CaptureId.ToString("N") ||
            reader.GetString(6) != target.ArtifactId.ToString("N") || reader.GetString(7) != target.ManifestSha256 ||
            reader.GetString(8) != target.PayloadSha256 || reader.GetString(9) != target.ProcessingProfileSha256 ||
            reader.GetString(10) != target.Mode || reader.GetBoolean(11) != target.Required ||
            reader.GetString(12) != target.ExpectedOuterLaneState || reader.GetString(13) != target.ExpectedWorkState ||
            reader.GetString(14) != target.ExpectedFrameState || reader.GetString(15) != target.ExpectedFailureReason ||
            reader.GetInt64(16) != target.ExpectedOuterLaneUpdatedUtc.ToUnixTimeMilliseconds() ||
            reader.GetInt64(17) != target.ExpectedWorkUpdatedUtc.ToUnixTimeMilliseconds() ||
            reader.GetInt64(18) != target.ExpectedFrameUpdatedUtc.ToUnixTimeMilliseconds() ||
            reader.GetString(19) != target.ExternalOwnershipEvidence!.DeploymentRunId ||
            reader.GetString(20) != target.ExternalOwnershipEvidence.InventorySha256 || !reader.GetBoolean(21) ||
            reader.GetString(22) != "abandon" || reader.GetString(23) != actor || reader.GetString(24) != reasonCode ||
            reader.GetString(27) != reader.GetString(28))
        {
            throw new OutboxOperationCollisionException("Idempotency key belongs to another transient runtime operation.");
        }
        return new TransientRuntimeOperationReceipt(
            TransientRuntimeOperationDisposition.Duplicate, reader.GetString(25), actor, reasonCode,
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(26)), reader.GetString(19), reader.GetString(20));
    }

    private static async ValueTask InsertOperationReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransientRuntimeOperationTarget target,
        string idempotencyKey,
        string actor,
        string reasonCode,
        DateTimeOffset completedUtc,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO transient_runtime_operations(
                idempotency_key, raw_capture_row_id, lane_work_id, outer_lane_work_id, agent_id,
                capture_sequence, capture_id, artifact_id, manifest_sha256, payload_sha256, processing_profile_sha256,
                mode, required, expected_outer_lane_state, expected_work_state, expected_frame_state,
                expected_failure_reason, expected_outer_lane_updated_unix_ms, expected_work_updated_unix_ms,
                expected_frame_updated_unix_ms, deployment_run_id, inventory_sha256,
                legacy_ownership_externally_established, action, actor, reason_code, result_state, completed_unix_ms,
                receipt_identity_sha256)
            VALUES($key, $raw, $lane_work, $outer_work, $agent, $sequence, $capture, $artifact,
                   $manifest, $payload, $profile, $mode, $required, $outer_state, $work_state, $frame_state,
                   $failure, $outer_updated, $work_updated, $frame_updated, $deployment_run, $inventory, 1, 'abandon', $actor,
                   $reason, 'abandoned', $completed,
                   hvo_sha256(json_array(
                       $key, $raw, $lane_work, $outer_work, $agent, $sequence, $capture, $artifact,
                       $manifest, $payload, $profile, $mode, $required, $outer_state, $work_state,
                       $frame_state, $failure, $outer_updated, $work_updated, $frame_updated,
                       $deployment_run, $inventory, 1, 'abandon', $actor, $reason, 'abandoned', $completed)));
            """;
        command.Parameters.AddWithValue("$key", idempotencyKey);
        command.Parameters.AddWithValue("$raw", target.RawCaptureRowId);
        command.Parameters.AddWithValue("$lane_work", target.LaneWorkId);
        command.Parameters.AddWithValue("$outer_work", target.OuterLaneWorkId);
        command.Parameters.AddWithValue("$agent", target.AgentId);
        command.Parameters.AddWithValue("$sequence", target.CaptureSequence);
        command.Parameters.AddWithValue("$capture", target.CaptureId.ToString("N"));
        command.Parameters.AddWithValue("$artifact", target.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$manifest", target.ManifestSha256);
        command.Parameters.AddWithValue("$payload", target.PayloadSha256);
        command.Parameters.AddWithValue("$profile", target.ProcessingProfileSha256);
        command.Parameters.AddWithValue("$mode", target.Mode);
        command.Parameters.AddWithValue("$required", target.Required ? 1 : 0);
        command.Parameters.AddWithValue("$outer_state", target.ExpectedOuterLaneState);
        command.Parameters.AddWithValue("$work_state", target.ExpectedWorkState);
        command.Parameters.AddWithValue("$frame_state", target.ExpectedFrameState);
        command.Parameters.AddWithValue("$failure", target.ExpectedFailureReason);
        command.Parameters.AddWithValue("$outer_updated", target.ExpectedOuterLaneUpdatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$work_updated", target.ExpectedWorkUpdatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$frame_updated", target.ExpectedFrameUpdatedUtc.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$deployment_run", target.ExternalOwnershipEvidence!.DeploymentRunId);
        command.Parameters.AddWithValue("$inventory", target.ExternalOwnershipEvidence.InventorySha256);
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$reason", reasonCode);
        command.Parameters.AddWithValue("$completed", completedUtc.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    private static async ValueTask VerifyOperationsSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string expectedColumns = "operation_id,idempotency_key,raw_capture_row_id,lane_work_id,outer_lane_work_id,agent_id,capture_sequence,capture_id,artifact_id,manifest_sha256,payload_sha256,processing_profile_sha256,mode,required,expected_outer_lane_state,expected_work_state,expected_frame_state,expected_failure_reason,expected_outer_lane_updated_unix_ms,expected_work_updated_unix_ms,expected_frame_updated_unix_ms,deployment_run_id,inventory_sha256,legacy_ownership_externally_established,action,actor,reason_code,result_state,completed_unix_ms,receipt_identity_sha256";
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM pragma_table_info('transient_runtime_operations') ORDER BY cid);";
            var actual = Convert.ToString(
                await columns.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(actual, expectedColumns, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Transient runtime operation table columns do not match the supported schema.");
            }
        }
        using (var table = connection.CreateCommand())
        {
            table.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'transient_runtime_operations';";
            var sql = Convert.ToString(
                await table.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            string[] requiredFragments =
            [
                "STRICT", "idempotency_key TEXT NOT NULL UNIQUE", "length(idempotency_key) BETWEEN 1 AND 128",
                "length(agent_id) BETWEEN 1 AND 128", "capture_sequence > 0", "length(capture_id) = 32",
                "length(artifact_id) = 32", "length(manifest_sha256) = 64", "length(payload_sha256) = 64",
                "length(processing_profile_sha256) = 64", "mode IN ('edge', 'hybrid')", "required IN (0, 1)",
                "expected_outer_lane_state = 'completed'", "expected_work_state = 'quarantined'",
                "expected_frame_state = 'quarantined'", "length(expected_failure_reason) BETWEEN 1 AND 128",
                "length(deployment_run_id) BETWEEN 1 AND 128", "length(inventory_sha256) = 64",
                "inventory_sha256 NOT GLOB '*[^0-9A-F]*'", "legacy_ownership_externally_established = 1",
                "action = 'abandon'", "length(actor) BETWEEN 1 AND 128", "length(reason_code) BETWEEN 1 AND 64",
                "result_state = 'abandoned'", "length(receipt_identity_sha256) = 64",
                "receipt_identity_sha256 NOT GLOB '*[^0-9A-F]*'"
            ];
            var missing = sql is null
                ? requiredFragments
                : requiredFragments.Where(fragment =>
                    !sql.Contains(fragment, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (missing.Length > 0)
            {
                throw new InvalidDataException(
                    $"Transient runtime operation table definition is malformed ({string.Join(", ", missing)}).");
            }
        }
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = """
                SELECT group_concat("from" || '>' || "table" || '.' || "to", ',')
                FROM (SELECT "from", "table", "to" FROM pragma_foreign_key_list('transient_runtime_operations') ORDER BY "from");
                """;
            var actual = Convert.ToString(
                await foreignKeys.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(
                    actual,
                    "lane_work_id>transient_capture_work.lane_work_id,outer_lane_work_id>capture_lane_work.work_id,raw_capture_row_id>raw_captures.raw_capture_row_id",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Transient runtime operation foreign keys are malformed.");
            }
        }
        using (var unique = connection.CreateCommand())
        {
            unique.CommandText = """
                SELECT COUNT(*) FROM pragma_index_list('transient_runtime_operations') indexes
                WHERE indexes."unique" = 1 AND indexes.origin = 'u'
                  AND (SELECT group_concat(name, ',') FROM pragma_index_info(indexes.name)) = 'idempotency_key';
                """;
            if (Convert.ToInt64(
                    await unique.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidDataException("Transient runtime operation idempotency constraint is malformed.");
            }
        }
        using var index = connection.CreateCommand();
        index.CommandText = """
            SELECT group_concat(name, ',') FROM (
                SELECT name FROM pragma_index_info('ix_transient_runtime_operations_target') ORDER BY seqno);
            """;
        var indexColumns = Convert.ToString(
            await index.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(indexColumns, "raw_capture_row_id,operation_id", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transient runtime operation index does not match the supported schema.");
        }
        using var indexDefinition = connection.CreateCommand();
        indexDefinition.CommandText = """
            SELECT COUNT(*) FROM pragma_index_list('transient_runtime_operations')
            WHERE name = 'ix_transient_runtime_operations_target' AND "unique" = 0 AND partial = 0;
            """;
        if (Convert.ToInt64(
                await indexDefinition.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidDataException("Transient runtime operation index definition is malformed.");
        }
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
        var connection = await OpenUnconfiguredAsync(cancellationToken).ConfigureAwait(false);
        await ConfigureConnectionAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async ValueTask<SqliteConnection> OpenUnconfiguredAsync(CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical();
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        EnsureDatabaseFilesArePhysical();
        connection.CreateFunction<string?, string>(
            "hvo_sha256",
            static value => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))),
            isDeterministic: true);
        return connection;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The busy timeout is a validated integer option; no SQL value is user supplied.")]
    private async ValueTask ConfigureConnectionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutSeconds * 1000}; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask ValidateExistingRuntimeSchemaAsync(
        string root,
        int busyTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var databasePath = Path.Combine(normalizedRoot, "journal", "raw-ingress.db");
        if (!File.Exists(databasePath))
        {
            return;
        }
        try
        {
            _ = await InspectRuntimeSchemaAsync(
                normalizedRoot, databasePath, busyTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception) when (exception.InnerException is SqliteException)
        {
            // Raw ingress owns diagnostics for corruption in its canonical tables.
        }
    }

    private ValueTask<RuntimeSchemaInspection> InspectRuntimeSchemaAsync(CancellationToken cancellationToken)
        => InspectRuntimeSchemaAsync(_root, _databasePath, _busyTimeoutSeconds, cancellationToken);

    private static async ValueTask<RuntimeSchemaInspection> InspectRuntimeSchemaAsync(
        string root,
        string databasePath,
        int busyTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        EnsureDatabaseFilesArePhysical(root, databasePath);
        var recoveryFiles = new[]
        {
            string.Concat(databasePath, "-wal"),
            string.Concat(databasePath, "-journal")
        }.Where(File.Exists).ToArray();
        var hasRecoveryState = recoveryFiles.Length > 0 || File.Exists(string.Concat(databasePath, "-shm"));
        DirectoryInfo? snapshotRoot = null;
        try
        {
            var inspectionPath = databasePath;
            var immutable = !hasRecoveryState;
            if (!immutable)
            {
                snapshotRoot = Directory.CreateTempSubdirectory("hvo-transient-runtime-inspection-");
                inspectionPath = Path.Combine(snapshotRoot.FullName, Path.GetFileName(databasePath));
                File.Copy(databasePath, inspectionPath);
                foreach (var recoveryFile in recoveryFiles)
                {
                    File.Copy(
                        recoveryFile,
                        string.Concat(inspectionPath, recoveryFile.AsSpan(databasePath.Length)));
                }
            }
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = immutable
                    ? string.Concat(new Uri(inspectionPath).AbsoluteUri, "?immutable=1")
                    : inspectionPath,
                Mode = immutable ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
                Pooling = false,
                DefaultTimeout = busyTimeoutSeconds
            }.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var rawVersion = await ExecuteScalarLongAsync(
                connection, "PRAGMA user_version;", null, cancellationToken).ConfigureAwait(false);
            var runtimeObjectCount = await CountRuntimeSchemaObjectsAsync(
                connection, null, cancellationToken).ConfigureAwait(false);
            if (runtimeObjectCount > 0)
            {
                await ValidateRuntimeSchemaAsync(connection, null, cancellationToken).ConfigureAwait(false);
            }
            if (rawVersion == SqliteRawCaptureJournal.CurrentSchemaVersion)
            {
                await SqliteRawCaptureJournal.ValidateCanonicalSchemaDefinitionsAsync(
                    connection, null, cancellationToken).ConfigureAwait(false);
            }
            return new(rawVersion, runtimeObjectCount);
        }
        catch (SqliteException exception)
        {
            throw new InvalidDataException(
                $"Transient runtime SQLite schema inspection failed for '{databasePath}'; archive the database and complete an explicit state-disposition procedure before starting this CameraAgent.",
                exception);
        }
        finally
        {
            snapshotRoot?.Delete(recursive: true);
        }
    }

    private static async ValueTask ValidateRuntimeSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        var actual = await ReadRuntimeSchemaDefinitionsAsync(connection, transaction, cancellationToken)
            .ConfigureAwait(false);
        if (CanonicalRuntimeSchemaDefinitions.Value.Any(expected =>
                !actual.TryGetValue(expected.Key, out var definition) ||
                !string.Equals(definition, expected.Value, StringComparison.Ordinal)) ||
            actual.Keys.Any(name => !CanonicalRuntimeSchemaDefinitions.Value.ContainsKey(name)))
        {
            throw new InvalidDataException(
                "Transient runtime schema is unsupported; archive the database and complete an explicit state-disposition procedure before starting this CameraAgent.");
        }
    }

    private static async ValueTask<long> CountRuntimeSchemaObjectsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
        => await ExecuteScalarLongAsync(connection, RuntimeSchemaObjectCountSql, transaction, cancellationToken)
            .ConfigureAwait(false);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass internal constant schema inspection statements only.")]
    private static async ValueTask<long> ExecuteScalarLongAsync(
        SqliteConnection connection,
        string sql,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, string> CreateCanonicalRuntimeSchemaDefinitions()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = RuntimeSchemaSql;
        command.ExecuteNonQuery();
        return ReadRuntimeSchemaDefinitions(connection);
    }

    private static async ValueTask<Dictionary<string, string>> ReadRuntimeSchemaDefinitionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = CreateRuntimeSchemaDefinitionCommand(connection, transaction);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            definitions.Add(reader.GetString(1), ReadSchemaDefinition(reader));
        }
        return definitions;
    }

    private static Dictionary<string, string> ReadRuntimeSchemaDefinitions(SqliteConnection connection)
    {
        using var command = CreateRuntimeSchemaDefinitionCommand(connection, null);
        using var reader = command.ExecuteReader();
        var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            definitions.Add(reader.GetString(1), ReadSchemaDefinition(reader));
        }
        return definitions;
    }

    private static SqliteCommand CreateRuntimeSchemaDefinitionCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RuntimeSchemaDefinitionsSql;
        return command;
    }

    private static string ReadSchemaDefinition(SqliteDataReader reader)
    {
        var definition = new StringBuilder();
        foreach (var ordinal in new[] { 0, 2 })
        {
            var value = reader.GetString(ordinal);
            definition.Append(value.Length).Append(':').Append(value);
        }
        var sql = SqliteRawCaptureJournal.NormalizeSchemaSql(reader.GetString(3));
        definition.Append(sql.Length).Append(':').Append(sql);
        return definition.ToString();
    }

    private void EnsureDatabaseFilesArePhysical()
        => EnsureDatabaseFilesArePhysical(_root, _databasePath);

    private static void EnsureDatabaseFilesArePhysical(string root, string databasePath)
    {
        RawIngressFileStore.EnsureNoSymbolicLinks(root, databasePath);
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-wal"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-shm"));
        RawIngressFileStore.EnsureNoSymbolicLinks(root, string.Concat(databasePath, "-journal"));
    }

    public void Dispose() => _initializeGate.Dispose();

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

    private const string RuntimeSchemaObjectCountSql = """
        SELECT COUNT(*) FROM sqlite_schema
        WHERE name IN (
                'transient_worker_frames', 'ix_transient_worker_frames_ready',
                'transient_worker_candidates', 'ix_transient_worker_candidates_pending')
           OR name LIKE 'transient_worker_%'
           OR name LIKE 'ix_transient_worker_%'
           OR tbl_name IN ('transient_worker_frames', 'transient_worker_candidates');
        """;

    private const string RuntimeSchemaDefinitionsSql = """
        SELECT type, name, tbl_name, sql
        FROM sqlite_schema
        WHERE sql IS NOT NULL AND (
                name IN (
                    'transient_worker_frames', 'ix_transient_worker_frames_ready',
                    'transient_worker_candidates', 'ix_transient_worker_candidates_pending')
                OR name LIKE 'transient_worker_%'
                OR name LIKE 'ix_transient_worker_%'
                OR tbl_name IN ('transient_worker_frames', 'transient_worker_candidates'))
        ORDER BY type, name;
        """;

    private sealed record RuntimeSchemaInspection(long RawVersion, long RuntimeObjectCount);

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
