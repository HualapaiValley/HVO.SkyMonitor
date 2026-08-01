using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Transients;

public enum TransientCandidateReservationDisposition
{
    Created,
    Existing
}

public enum TransientCandidateWorkflowPhase
{
    IdentityAllocated,
    CandidatePersisted,
    Finalized,
    HandoffPending,
    Acknowledged,
    Quarantined
}

public sealed record TransientCandidateReservation(
    Guid CandidateId,
    Guid EventId,
    string AgentId,
    IReadOnlyList<TransientSourceEvidenceReferenceV1> Sources);

public sealed record TransientCandidateJournalEntry(
    Guid CandidateId,
    Guid EventId,
    string AgentId,
    TransientEventState State,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    DateTimeOffset TimeoutUtc,
    IReadOnlyList<TransientSourceEvidenceReferenceV1> Sources,
    TransientOperatingMode Mode,
    bool Required,
    TransientCandidateWorkflowPhase Phase,
    string ReservationIdentitySha256,
    string? CandidatePayloadSha256,
    string? FinalizationReceiptIdentitySha256,
    string? SubmissionIdentitySha256,
    string? AcknowledgementPayloadSha256,
    bool SourceHoldReleased,
    string? QuarantineReason,
    TransientCandidateV1? Candidate,
    TransientFinalizationReceiptV1? FinalizationReceipt,
    TransientCandidateSubmissionEnvelopeV1? Submission,
    TransientCandidateSubmissionAcknowledgementV1? Acknowledgement);

public sealed record TransientCandidateReservationResult(
    TransientCandidateReservationDisposition Disposition,
    TransientCandidateJournalEntry Entry);

public sealed record TransientCandidateBacklog(
    bool Required,
    long ActiveCount,
    long HeldSourceBytes,
    DateTimeOffset? OldestCreatedUtc,
    long MaximumCount,
    long MaximumHeldSourceBytes,
    int MaximumOldestAgeMinutes,
    int PressureLevel);

public interface ITransientCandidateJournal
{
    ValueTask StageCaptureAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken);

    ValueTask CompleteCaptureAsync(Guid artifactId, CancellationToken cancellationToken);

    ValueTask<TransientCandidateReservationResult> ReserveAsync(
        TransientCandidateReservation reservation,
        CancellationToken cancellationToken);

    ValueTask<TransientCandidateJournalEntry?> ReadAsync(
        Guid candidateId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<TransientCandidateJournalEntry>> ReadResumableAsync(
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<TransientCandidateJournalEntry> PersistCandidateAsync(
        Guid candidateId,
        Guid eventId,
        TransientCandidateV1 candidate,
        CancellationToken cancellationToken);

    ValueTask<TransientCandidateJournalEntry> PersistFinalizationAsync(
        Guid candidateId,
        Guid eventId,
        TransientFinalizationReceiptV1 receipt,
        CancellationToken cancellationToken);

    ValueTask<TransientCandidateJournalEntry> PersistSubmissionAsync(
        Guid candidateId,
        Guid eventId,
        TransientCandidateSubmissionEnvelopeV1 submission,
        CancellationToken cancellationToken);

    ValueTask<TransientCandidateJournalEntry> AcknowledgeAsync(
        Guid candidateId,
        Guid eventId,
        TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
        CancellationToken cancellationToken);

    ValueTask<TransientCandidateBacklog> ReadBacklogAsync(CancellationToken cancellationToken);
}

internal sealed class SqliteTransientCandidateJournal : ITransientCandidateJournal
{
    private readonly string _databasePath;
    private readonly string _root;
    private readonly int _busyTimeoutSeconds;
    private readonly CaptureDistributionOptions _limits;
    private readonly bool _required;
    private readonly TransientOperatingMode _mode;
    private readonly int _candidateTimeoutMinutes;
    private readonly TimeProvider _timeProvider;
    private readonly ITransientCandidateFaultInjector _faultInjector;

    public SqliteTransientCandidateJournal(
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        ITransientCandidateFaultInjector? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var configured = options.Value;
        _root = Path.GetFullPath(configured.RawIngressRoot);
        _databasePath = Path.Combine(_root, "journal", "raw-ingress.db");
        _busyTimeoutSeconds = configured.RawIngressSqliteBusyTimeoutSeconds;
        _limits = configured.CaptureDistribution;
        _required = configured.TransientDetection.Required;
        _mode = configured.TransientDetection.Mode;
        _candidateTimeoutMinutes = configured.TransientDetection.CandidateTimeoutMinutes;
        _timeProvider = timeProvider;
        _faultInjector = faultInjector ?? NullTransientCandidateFaultInjector.Instance;
    }

    public async ValueTask StageCaptureAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        EnsureEdgeMode();
        if (context.WorkId < 1 || context.RawCapture.Manifest.Descriptor.Artifact.ArtifactId == Guid.Empty)
        {
            throw new ArgumentException("Transient lane staging requires durable lane and raw artifact identity.", nameof(context));
        }
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var artifactId = context.RawCapture.Manifest.Descriptor.Artifact.ArtifactId;
            var source = await ValidateCommittedArtifactAsync(
                connection,
                transaction,
                artifactId,
                context.RawCapture.Manifest.Descriptor.Artifact.ChecksumSha256,
                context.RawCapture.CommittedManifestSha256,
                cancellationToken).ConfigureAwait(false);
            using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandText = "SELECT lane_work_id, mode, required, artifact_id, manifest_sha256 FROM transient_capture_work WHERE raw_capture_row_id = $raw;";
                existing.Parameters.AddWithValue("$raw", source.RawCaptureRowId);
                using var reader = await existing.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var exact = reader.GetInt64(0) == context.WorkId &&
                                string.Equals(reader.GetString(1), WriteMode(_mode), StringComparison.Ordinal) &&
                                reader.GetBoolean(2) == _required &&
                                string.Equals(reader.GetString(3), artifactId.ToString("N"), StringComparison.Ordinal) &&
                                string.Equals(reader.GetString(4), context.RawCapture.CommittedManifestSha256, StringComparison.Ordinal);
                    if (!exact)
                    {
                        await reader.DisposeAsync().ConfigureAwait(false);
                        await QuarantineConflictAsync(
                            connection, transaction, null, null, "capture-work-conflict", cancellationToken).ConfigureAwait(false);
                        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        throw new TransientCandidateIdentityConflictException(
                            "Transient lane work identity conflicts with staged immutable evidence.");
                    }
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO transient_capture_work(
                        raw_capture_row_id, lane_work_id, mode, required, state, artifact_id,
                        manifest_sha256, created_unix_ms, updated_unix_ms)
                    VALUES ($raw, $work, $mode, $required, 'pending', $artifact, $manifest, $now, $now);
                    """;
                insert.Parameters.AddWithValue("$raw", source.RawCaptureRowId);
                insert.Parameters.AddWithValue("$work", context.WorkId);
                insert.Parameters.AddWithValue("$mode", WriteMode(_mode));
                insert.Parameters.AddWithValue("$required", _required ? 1 : 0);
                insert.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
                insert.Parameters.AddWithValue("$manifest", context.RawCapture.CommittedManifestSha256);
                insert.Parameters.AddWithValue("$now", now);
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await SetRawHoldAsync(connection, transaction, source.RawCaptureRowId, cancellationToken).ConfigureAwait(false);
            await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(TransientCandidateFaultPoint.BeforeStageCommit);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(TransientCandidateFaultPoint.AfterStageCommit);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask CompleteCaptureAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(artifactId, Guid.Empty);
        EnsureEdgeMode();
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            long rawRowId;
            string state;
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT raw_capture_row_id, state FROM transient_capture_work WHERE artifact_id = $artifact;";
                read.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
                using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException("Transient capture work is not durably staged.");
                }
                rawRowId = reader.GetInt64(0);
                state = reader.GetString(1);
            }
            if (state == "completed")
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (state != "pending")
            {
                throw new InvalidOperationException("Transient capture work already has candidate or quarantine state.");
            }
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE transient_capture_work SET state = 'completed', updated_unix_ms = $now WHERE artifact_id = $artifact AND state = 'pending';";
                update.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await RecomputeRawHoldAsync(connection, transaction, rawRowId, cancellationToken).ConfigureAwait(false);
            await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask<TransientCandidateReservationResult> ReserveAsync(
        TransientCandidateReservation reservation,
        CancellationToken cancellationToken)
    {
        EnsureEdgeMode();
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReserveCoreAsync(reservation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async ValueTask<TransientCandidateReservationResult> ReserveCoreAsync(
        TransientCandidateReservation reservation,
        CancellationToken cancellationToken)
    {
        ValidateReservationOwner(reservation);
        try
        {
            ValidateReservationSources(reservation);
        }
        catch (ArgumentException exception)
        {
            await QuarantineInvalidReservationAsync(reservation, cancellationToken).ConfigureAwait(false);
            throw new TransientCandidateIdentityConflictException(
                "Transient source identity is invalid and was durably quarantined.",
                exception);
        }
        var reservationIdentity = ComputeReservationIdentitySha256(reservation);

        while (true)
        {
            ReservationSnapshot snapshot;
            try
            {
                snapshot = await ReadReservationSnapshotAsync(reservation, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsSnapshotDecodeException(exception))
            {
                var invalid = await HandleUnreadableSnapshotAsync(
                    reservation,
                    reservationIdentity,
                    exception,
                    cancellationToken).ConfigureAwait(false);
                if (invalid.Retry)
                {
                    continue;
                }
                return invalid.Result!;
            }
            _faultInjector.Inject(TransientCandidateFaultPoint.BeforeReservationValidation);
            IReadOnlyList<SourceRow> sourceRows = [];
            try
            {
                if (snapshot.CandidateFacts.Rows.Count == 0 && snapshot.ConflictFacts.Rows.Count == 0)
                {
                    sourceRows = await ValidateReservationSnapshotAsync(
                        reservation, snapshot.Sources, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (IsSourceEvidenceException(exception))
            {
                var invalid = await HandleInvalidSourceEvidenceAsync(
                    reservation,
                    reservationIdentity,
                    snapshot,
                    exception,
                    cancellationToken).ConfigureAwait(false);
                if (invalid.Retry)
                {
                    continue;
                }
                return invalid.Result!;
            }
            _faultInjector.Inject(TransientCandidateFaultPoint.AfterReservationValidation);
            var result = await ReserveValidatedAsync(
                reservation,
                reservationIdentity,
                snapshot,
                sourceRows,
                cancellationToken).ConfigureAwait(false);
            if (!result.Retry)
            {
                return result.Result!;
            }
        }
    }

    public async ValueTask<TransientCandidateJournalEntry?> ReadAsync(
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(candidateId, Guid.Empty);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var entry = await ReadAsync(connection, transaction, candidateId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return entry;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<TransientCandidateJournalEntry>> ReadResumableAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1_000);
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var candidateIds = new List<Guid>(maximumCount);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT candidate_id
                    FROM transient_candidates
                    WHERE source_hold_released = 0 AND phase != 'quarantined'
                    ORDER BY timeout_unix_ms, created_unix_ms, candidate_id
                    LIMIT $maximum;
                    """;
                command.Parameters.AddWithValue("$maximum", maximumCount);
                using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    candidateIds.Add(Guid.ParseExact(reader.GetString(0), "N"));
                }
            }
            var entries = new List<TransientCandidateJournalEntry>(candidateIds.Count);
            foreach (var candidateId in candidateIds)
            {
                entries.Add(await ReadAsync(connection, transaction, candidateId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("Resumable transient candidate disappeared during read."));
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return entries;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public ValueTask<TransientCandidateJournalEntry> PersistCandidateAsync(
        Guid candidateId,
        Guid eventId,
        TransientCandidateV1 candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var validation = TransientContractJson.Validate(candidate);
        if (!validation.IsValid || candidate.CandidateId != candidateId || candidate.EventId != eventId ||
            candidate.State is not (TransientCandidateState.PendingContext or TransientCandidateState.Provisional))
        {
            throw new ArgumentException("Canonical transient candidate does not match a resumable reservation.", nameof(candidate));
        }
        var payload = TransientContractJson.Serialize(candidate);
        var state = candidate.State == TransientCandidateState.PendingContext
            ? TransientEventState.Pending
            : TransientEventState.Provisional;
        return PersistPayloadAsync(
            candidateId,
            eventId,
            candidate.AgentId,
            candidate.ContextSources,
            state,
            TransientCandidateWorkflowPhase.CandidatePersisted,
            candidate.State,
            "candidate_payload",
            "candidate_payload_sha256",
            payload,
            Convert.ToHexString(SHA256.HashData(payload)),
            TransientCandidateFaultPoint.BeforeCandidateCommit,
            TransientCandidateFaultPoint.AfterCandidateCommit,
            releaseHold: false,
            cancellationToken);
    }

    public ValueTask<TransientCandidateJournalEntry> PersistFinalizationAsync(
        Guid candidateId,
        Guid eventId,
        TransientFinalizationReceiptV1 receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var validation = TransientCandidateDeliveryJson.Validate(receipt);
        if (!validation.IsValid || receipt.CandidateId != candidateId || receipt.EventId != eventId)
        {
            throw new ArgumentException("Transient finalization receipt does not match the reservation.", nameof(receipt));
        }
        var payload = TransientCandidateDeliveryJson.Serialize(receipt);
        var originatingSources = receipt.Event.Observations
            .Where(observation => observation.Extraction.OriginatingCandidateId == candidateId)
            .Select(static observation => observation.Source)
            .ToArray();
        return PersistPayloadAsync(
            candidateId,
            eventId,
            receipt.Event.AgentId,
            originatingSources,
            receipt.Event.State,
            TransientCandidateWorkflowPhase.Finalized,
            null,
            "finalization_payload",
            "finalization_receipt_identity_sha256",
            payload,
            receipt.ReceiptIdentitySha256,
            TransientCandidateFaultPoint.BeforeFinalizationCommit,
            TransientCandidateFaultPoint.AfterFinalizationCommit,
            releaseHold: _mode == TransientOperatingMode.Edge,
            cancellationToken);
    }

    public ValueTask<TransientCandidateJournalEntry> PersistSubmissionAsync(
        Guid candidateId,
        Guid eventId,
        TransientCandidateSubmissionEnvelopeV1 submission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (_mode != TransientOperatingMode.Hybrid)
        {
            throw new InvalidOperationException("Only Hybrid mode persists central transient submissions.");
        }
        var validation = TransientCandidateDeliveryJson.Validate(submission);
        if (!validation.IsValid || submission.CandidateId != candidateId || submission.EventId != eventId)
        {
            throw new ArgumentException("Transient submission does not match the reservation.", nameof(submission));
        }
        var payload = TransientCandidateDeliveryJson.Serialize(submission);
        return PersistPayloadAsync(
            candidateId,
            eventId,
            submission.Candidate.AgentId,
            submission.Candidate.ContextSources,
            TransientEventState.Provisional,
            TransientCandidateWorkflowPhase.HandoffPending,
            null,
            "submission_payload",
            "submission_identity_sha256",
            payload,
            submission.SubmissionIdentitySha256,
            TransientCandidateFaultPoint.BeforeSubmissionCommit,
            TransientCandidateFaultPoint.AfterSubmissionCommit,
            releaseHold: false,
            cancellationToken);
    }

    public async ValueTask<TransientCandidateJournalEntry> AcknowledgeAsync(
        Guid candidateId,
        Guid eventId,
        TransientCandidateSubmissionAcknowledgementV1 acknowledgement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        if (_mode != TransientOperatingMode.Hybrid)
        {
            throw new InvalidOperationException("Only Hybrid mode accepts central transient acknowledgements.");
        }
        var validation = TransientCandidateDeliveryJson.Validate(acknowledgement);
        if (!validation.IsValid || acknowledgement.CandidateId != candidateId || acknowledgement.EventId != eventId)
        {
            throw new ArgumentException("Transient acknowledgement does not match the reservation.", nameof(acknowledgement));
        }
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadRequiredAsync(connection, transaction, candidateId, eventId, cancellationToken).ConfigureAwait(false);
            using var submissionCommand = connection.CreateCommand();
            submissionCommand.Transaction = transaction;
            submissionCommand.CommandText = "SELECT submission_payload FROM transient_candidates WHERE candidate_id = $candidate;";
            submissionCommand.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
            var submissionPayload = await submissionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as byte[]
                ?? throw new InvalidOperationException("Transient candidate has no durable Hybrid submission.");
            var submission = TransientCandidateDeliveryJson.ParseSubmission(submissionPayload).Value
                ?? throw new InvalidDataException("Durable transient submission is corrupt.");
            if (!TransientCandidateDeliveryJson.Matches(acknowledgement, submission))
            {
                await QuarantineConflictAsync(
                    connection, transaction, candidateId, eventId, "acknowledgement-conflict", cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw new TransientCandidateIdentityConflictException("Transient acknowledgement conflicts with the durable submission.");
            }
            if (current.AcknowledgementPayloadSha256 is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }
            var payload = TransientCandidateDeliveryJson.Serialize(acknowledgement);
            var payloadSha = Convert.ToHexString(SHA256.HashData(payload));
            var now = _timeProvider.GetUtcNow();
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE transient_candidates
                    SET phase = 'acknowledged', acknowledgement_payload = $payload,
                        acknowledgement_payload_sha256 = $sha,
                        updated_unix_ms = $now
                    WHERE candidate_id = $candidate AND event_id = $event;
                    """;
                update.Parameters.AddWithValue("$payload", payload);
                update.Parameters.AddWithValue("$sha", payloadSha);
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
                update.Parameters.AddWithValue("$event", eventId.ToString("N"));
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            var persisted = await ReadAsync(
                connection, transaction, candidateId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Persisted transient acknowledgement disappeared before commit.");
            using (var release = connection.CreateCommand())
            {
                release.Transaction = transaction;
                release.CommandText = "UPDATE transient_candidates SET source_hold_released = 1 WHERE candidate_id = $candidate;";
                release.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
                await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await RecomputeCandidateSourceHoldsAsync(connection, transaction, candidateId, cancellationToken).ConfigureAwait(false);
            await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(TransientCandidateFaultPoint.BeforeAcknowledgementCommit);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(TransientCandidateFaultPoint.AfterAcknowledgementCommit);
            return persisted with { SourceHoldReleased = true };
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async ValueTask<TransientCandidateBacklog> ReadBacklogAsync(CancellationToken cancellationToken)
    {
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            await VerifyActiveCandidatesAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            var (count, bytes, oldest, _) = await ReadActiveTotalsAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            var maximumCount = _required
                ? _limits.RequiredMaximumPendingCount
                : _limits.OptionalMaximumPendingCount;
            var maximumBytes = _required
                ? _limits.RequiredMaximumPendingBytes
                : _limits.OptionalMaximumPendingBytes;
            var maximumAge = _required
                ? _limits.RequiredMaximumOldestAgeMinutes
                : _limits.OptionalMaximumOldestAgeMinutes;
            using var pressureCommand = connection.CreateCommand();
            pressureCommand.Transaction = transaction;
            pressureCommand.CommandText = "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient';";
            var pressure = Convert.ToInt32(
                await pressureCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TransientCandidateBacklog(
                _required,
                count,
                bytes,
                oldest,
                maximumCount,
                maximumBytes,
                maximumAge,
                pressure);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Payload and identity columns are selected only from private constant call sites; all values remain parameterized.")]
    private async ValueTask<TransientCandidateJournalEntry> PersistPayloadAsync(
        Guid candidateId,
        Guid eventId,
        string agentId,
        IReadOnlyList<TransientSourceEvidenceReferenceV1>? sources,
        TransientEventState state,
        TransientCandidateWorkflowPhase phase,
        TransientCandidateState? candidateState,
        string payloadColumn,
        string identityColumn,
        byte[] payload,
        string identity,
        TransientCandidateFaultPoint beforeCommit,
        TransientCandidateFaultPoint afterCommit,
        bool releaseHold,
        CancellationToken cancellationToken)
    {
        EnsureEdgeMode();
        var lifecycleGate = RawIngressLifecycleLock.ForRoot(_root);
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            using var transaction = BeginImmediate(connection);
            var current = await ReadRequiredAsync(connection, transaction, candidateId, eventId, cancellationToken).ConfigureAwait(false);
            var sourcesMatch = sources is null ||
                (phase == TransientCandidateWorkflowPhase.Finalized
                    ? sources.All(current.Sources.Contains)
                    : current.Sources.SequenceEqual(sources));
            if (!string.Equals(current.AgentId, agentId, StringComparison.Ordinal) || !sourcesMatch)
            {
                await QuarantineConflictAsync(
                    connection, transaction, candidateId, eventId, "canonical-content-conflict", cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw new TransientCandidateIdentityConflictException(
                    "Canonical transient content conflicts with reserved immutable facts.");
            }
            if (phase != TransientCandidateWorkflowPhase.CandidatePersisted && current.CandidatePayloadSha256 is null)
            {
                throw new InvalidOperationException("Canonical candidate evidence must be durable before finalization or handoff.");
            }
            if (phase == TransientCandidateWorkflowPhase.HandoffPending)
            {
                var submission = TransientCandidateDeliveryJson.ParseSubmission(payload).Value!;
                var candidatePayload = TransientContractJson.Serialize(submission.Candidate);
                var candidateSha = Convert.ToHexString(SHA256.HashData(candidatePayload));
                if (!string.Equals(candidateSha, current.CandidatePayloadSha256, StringComparison.Ordinal))
                {
                    await QuarantineConflictAsync(
                        connection, transaction, candidateId, eventId, "submission-candidate-conflict", cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    throw new TransientCandidateIdentityConflictException(
                        "Hybrid submission candidate differs from durable canonical candidate evidence.");
                }
            }

            var existingIdentity = identityColumn switch
            {
                "candidate_payload_sha256" => current.CandidatePayloadSha256,
                "finalization_receipt_identity_sha256" => current.FinalizationReceiptIdentitySha256,
                "submission_identity_sha256" => current.SubmissionIdentitySha256,
                _ => throw new InvalidOperationException("Unsupported transient payload identity column.")
            };
            if (existingIdentity is not null)
            {
                if (!string.Equals(existingIdentity, identity, StringComparison.Ordinal))
                {
                    await QuarantineConflictAsync(
                        connection, transaction, candidateId, eventId, "payload-identity-conflict", cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    throw new TransientCandidateIdentityConflictException(
                        "Transient payload identity was reused with different canonical content.");
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return current;
            }

            var now = _timeProvider.GetUtcNow();
            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = $"""
                    UPDATE transient_candidates
                    SET state = CASE
                            WHEN state IN ('validated', 'rejected', 'needs_review') THEN state
                            ELSE $state
                        END,
                        phase = CASE
                            WHEN phase = 'acknowledged' THEN phase
                            WHEN phase = 'handoff_pending' AND $phase = 'finalized' THEN phase
                            ELSE $phase
                        END,
                        candidate_state = COALESCE($candidate_state, candidate_state),
                        {payloadColumn} = $payload,
                        {identityColumn} = $identity,
                        updated_unix_ms = $now
                    WHERE candidate_id = $candidate AND event_id = $event;
                    """;
                update.Parameters.AddWithValue("$state", WriteState(state));
                update.Parameters.AddWithValue("$phase", WritePhase(phase));
                update.Parameters.AddWithValue("$candidate_state", candidateState?.ToString() ?? (object)DBNull.Value);
                update.Parameters.AddWithValue("$payload", payload);
                update.Parameters.AddWithValue("$identity", identity);
                update.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                update.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
                update.Parameters.AddWithValue("$event", eventId.ToString("N"));
                if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                {
                    throw new InvalidOperationException("Transient payload persistence did not update exactly one row.");
                }
            }
            var persisted = await ReadAsync(
                connection, transaction, candidateId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Persisted transient candidate disappeared before payload commit.");
            if (releaseHold && !persisted.SourceHoldReleased)
            {
                using var release = connection.CreateCommand();
                release.Transaction = transaction;
                release.CommandText = "UPDATE transient_candidates SET source_hold_released = 1 WHERE candidate_id = $candidate;";
                release.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
                await release.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await RecomputeCandidateSourceHoldsAsync(
                    connection, transaction, candidateId, cancellationToken).ConfigureAwait(false);
            }
            await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(beforeCommit);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _faultInjector.Inject(afterCommit);
            return releaseHold ? persisted with { SourceHoldReleased = true } : persisted;
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<TransientCandidateJournalEntry> ReadRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(candidateId, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfEqual(eventId, Guid.Empty);
        var current = await ReadAsync(connection, transaction, candidateId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Transient candidate identity is not reserved.");
        if (current.EventId != eventId)
        {
            await QuarantineConflictAsync(
                connection, transaction, candidateId, eventId, "event-identity-conflict", cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new TransientCandidateIdentityConflictException(
                "Transient event identity does not match the candidate reservation.");
        }
        if (current.Phase == TransientCandidateWorkflowPhase.Quarantined)
        {
            throw new InvalidOperationException("Transient candidate is quarantined.");
        }
        return current;
    }

    private static void ValidateReservationOwner(TransientCandidateReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        if (reservation.CandidateId == Guid.Empty || reservation.EventId == Guid.Empty ||
            reservation.CandidateId == reservation.EventId)
        {
            throw new ArgumentException("Candidate and event identities must be distinct non-empty values.", nameof(reservation));
        }
        if (string.IsNullOrWhiteSpace(reservation.AgentId) || reservation.AgentId.Length > 128 ||
            !string.Equals(reservation.AgentId, reservation.AgentId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent identity is invalid.", nameof(reservation));
        }
    }

    private static void ValidateReservationSources(TransientCandidateReservation reservation)
    {
        if (reservation.Sources is null || reservation.Sources.Count == 0)
        {
            throw new ArgumentException("At least one source-hold reference is required.", nameof(reservation));
        }
        var evidenceIds = new HashSet<Guid>();
        foreach (var source in reservation.Sources)
        {
            var validation = TransientContractJson.ValidateSourceEvidence(source);
            if (!validation.IsValid)
            {
                throw new ArgumentException(
                    $"Transient source reference is invalid ({validation.ReasonCode}:{validation.FieldPath}).",
                    nameof(reservation));
            }
            if (!evidenceIds.Add(source.EvidenceId))
            {
                throw new ArgumentException("Transient source evidence identities must be unique.", nameof(reservation));
            }
        }
    }

    private void EnsureEdgeMode()
    {
        if (_mode is not (TransientOperatingMode.Edge or TransientOperatingMode.Hybrid))
        {
            throw new InvalidOperationException("Transient candidate state is disabled in Off and Central modes.");
        }
    }

    private string ComputeReservationIdentitySha256(TransientCandidateReservation reservation)
        => ComputeReservationIdentitySha256(
            reservation.CandidateId,
            reservation.EventId,
            reservation.AgentId,
            _mode,
            _required,
            reservation.Sources);

    private static string ComputeReservationIdentitySha256(
        Guid candidateId,
        Guid eventId,
        string agentId,
        TransientOperatingMode mode,
        bool required,
        IReadOnlyList<TransientSourceEvidenceReferenceV1> sources)
        => CaptureContractJson.ComputeCanonicalJsonSha256(new
        {
            schema = "hvo-transient-candidate-reservation-v1",
            CandidateId = candidateId,
            EventId = eventId,
            AgentId = agentId,
            Mode = WriteMode(mode),
            Required = required,
            Sources = sources
        });

    private static bool Matches(
        TransientCandidateJournalEntry existing,
        TransientCandidateReservation reservation)
        => existing.EventId == reservation.EventId &&
           string.Equals(existing.AgentId, reservation.AgentId, StringComparison.Ordinal) &&
           existing.Sources.SequenceEqual(reservation.Sources);

    private async Task ReserveEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransientCandidateReservation reservation,
        CancellationToken cancellationToken)
    {
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT agent_id FROM transient_event_identities WHERE event_id = $event;";
            existing.Parameters.AddWithValue("$event", reservation.EventId.ToString("N"));
            var value = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                var agent = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.Equals(agent, reservation.AgentId, StringComparison.Ordinal))
                {
                    await QuarantineConflictAsync(
                        connection,
                        transaction,
                        reservation.CandidateId,
                        reservation.EventId,
                        "event-agent-conflict",
                        cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    throw new TransientCandidateIdentityConflictException(
                        "Transient event identity is already reserved for a different agent.");
                }
                return;
            }
        }
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO transient_event_identities(event_id, agent_id, created_unix_ms) VALUES ($event, $agent, $now);";
        insert.Parameters.AddWithValue("$event", reservation.EventId.ToString("N"));
        insert.Parameters.AddWithValue("$agent", reservation.AgentId);
        insert.Parameters.AddWithValue("$now", _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task QuarantineInvalidReservationAsync(
        TransientCandidateReservation reservation,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity(
            "transient-candidate.reserve.immediate");
        using var transaction = BeginImmediate(connection);
        await QuarantineConflictAsync(
            connection,
            transaction,
            reservation.CandidateId,
            reservation.EventId,
            "source-identity-conflict",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        activity?.Dispose();
    }

    private async Task<ReservationSnapshot> ReadReservationSnapshotAsync(
        TransientCandidateReservation reservation,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity(
            "transient-candidate.reserve.snapshot");
        using var transaction = BeginDeferred(connection);
        var snapshot = await ReadReservationSnapshotAsync(
            connection, transaction, reservation, includeSources: true, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        activity?.Dispose();
        return snapshot;
    }

    private static async Task<ReservationSnapshot> ReadReservationSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransientCandidateReservation reservation,
        bool includeSources,
        CancellationToken cancellationToken)
    {
        var candidate = reservation.CandidateId.ToString("N");
        var eventId = reservation.EventId.ToString("N");
        var candidateFacts = await ReadDurableFactsAsync(
            connection,
            transaction,
            """
            SELECT candidate_id, event_id, agent_id, mode, required, reservation_identity_sha256,
                   state, phase, candidate_payload, candidate_payload_sha256, finalization_payload,
                   finalization_receipt_identity_sha256, submission_payload, submission_identity_sha256,
                   acknowledgement_payload, acknowledgement_payload_sha256, source_hold_released,
                   quarantine_reason, timeout_unix_ms, created_unix_ms, updated_unix_ms, candidate_state
            FROM transient_candidates WHERE candidate_id = $identity;
            """,
            candidate,
            cancellationToken).ConfigureAwait(false);
        var candidateSourceFacts = await ReadDurableFactsAsync(
            connection,
            transaction,
            """
            SELECT candidate_id, source_ordinal, evidence_id, raw_capture_row_id, source_schema,
                   locator_schema, locator_kind, artifact_id, artifact_role, artifact_variant,
                   recipe_identity_sha256, checksum_sha256, observation_started_utc_ticks,
                   observation_ended_utc_ticks, timing_quality, timing_source, timing_version
            FROM transient_candidate_sources WHERE candidate_id = $identity
            ORDER BY source_ordinal;
            """,
            candidate,
            cancellationToken).ConfigureAwait(false);
        var eventFacts = await ReadDurableFactsAsync(
            connection,
            transaction,
            "SELECT event_id, agent_id, created_unix_ms FROM transient_event_identities WHERE event_id = $identity;",
            eventId,
            cancellationToken).ConfigureAwait(false);
        var conflictFacts = await ReadDurableFactsAsync(
            connection,
            transaction,
            """
            SELECT conflict_id, candidate_id, event_id, reason, observed_unix_ms
            FROM transient_candidate_conflicts WHERE candidate_id = $identity ORDER BY conflict_id;
            """,
            candidate,
            cancellationToken).ConfigureAwait(false);
        var sources = new List<SourceArtifactSnapshot?>(includeSources ? reservation.Sources.Count : 0);
        if (includeSources)
        {
            foreach (var source in reservation.Sources)
            {
                sources.Add(await ReadSourceArtifactSnapshotAsync(
                    connection,
                    transaction,
                    source.Locator.Artifact.ArtifactId,
                    cancellationToken).ConfigureAwait(false));
            }
        }
        return new ReservationSnapshot(
            candidateFacts,
            candidateSourceFacts,
            eventFacts,
            conflictFacts,
            sources);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Reservation snapshot queries are private constants and all identities remain parameterized.")]
    private static async Task<DurableFacts> ReadDurableFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText,
        string identity,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.AddWithValue("$identity", identity);
        var rows = new List<IReadOnlyList<string>>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = new string[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                row[index] = ReadDurableValue(reader.GetValue(index));
            }
            rows.Add(row);
        }
        return new DurableFacts(rows);
    }

    private static string ReadDurableValue(object value) => value switch
    {
        DBNull => "null",
        long integer => $"integer:{integer.ToString(CultureInfo.InvariantCulture)}",
        double real => $"real:{real.ToString("R", CultureInfo.InvariantCulture)}",
        string text => $"text:{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}",
        byte[] bytes => $"blob:{Convert.ToHexString(bytes)}",
        _ => throw new InvalidDataException("Transient reservation snapshot contains an unsupported SQLite value.")
    };

    private static async Task<SourceArtifactSnapshot?> ReadSourceArtifactSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT raw_capture_row_id, capture_id, raw_artifact_id, agent_id, capture_sequence,
                   state, descriptor_sha256, payload_relative_path, sidecar_relative_path,
                   payload_length, payload_sha256, manifest_sha256, manifest_json
            FROM raw_captures WHERE raw_artifact_id = $artifact;
            """;
        command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        return new SourceArtifactSnapshot(
            reader.GetInt64(0),
            ReadSourceText(reader, 1),
            ReadSourceText(reader, 2),
            reader.GetString(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9),
            reader.GetString(10),
            reader.GetString(11),
            ((byte[])reader[12]).ToArray());
    }

    private static string ReadSourceText(SqliteDataReader reader, int ordinal)
    {
        if (reader.GetValue(ordinal) is not string value)
        {
            throw new InvalidDataException("Transient source snapshot contains a non-text durable identity.");
        }
        return value;
    }

    private async Task<IReadOnlyList<SourceRow>> ValidateReservationSnapshotAsync(
        TransientCandidateReservation reservation,
        IReadOnlyList<SourceArtifactSnapshot?> snapshots,
        CancellationToken cancellationToken)
    {
        var rows = new List<SourceRow>(reservation.Sources.Count);
        for (var index = 0; index < reservation.Sources.Count; index++)
        {
            var source = reservation.Sources[index];
            var snapshot = snapshots[index] ?? throw new InvalidDataException(
                "Transient source-hold reference does not identify committed raw evidence.");
            if (!Guid.TryParseExact(snapshot.CaptureId, "N", out var captureId) ||
                !Guid.TryParseExact(snapshot.RawArtifactId, "N", out var rawArtifactId) ||
                !string.Equals(snapshot.State, "committed", StringComparison.Ordinal) ||
                rawArtifactId != source.Locator.Artifact.ArtifactId ||
                !string.Equals(
                    snapshot.PayloadSha256,
                    source.Locator.Artifact.ChecksumSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Transient source-hold reference conflicts with committed raw evidence.");
            }
            var parsed = CaptureContractJson.ParseManifest(snapshot.ManifestJson);
            var manifest = parsed.Document?.Manifest;
            if (!parsed.IsValid || manifest is null ||
                manifest.Descriptor.Capture.CaptureId != captureId ||
                manifest.Descriptor.Capture.CaptureSequence != snapshot.CaptureSequence ||
                manifest.Descriptor.Artifact.ArtifactId != rawArtifactId ||
                manifest.Descriptor.Artifact.Role != FrameArtifactRole.Raw ||
                !string.Equals(manifest.Descriptor.Capture.AgentId, snapshot.AgentId, StringComparison.Ordinal) ||
                !string.Equals(
                    CaptureContractJson.ComputeDescriptorSha256(manifest.Descriptor),
                    snapshot.DescriptorSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.Descriptor.Artifact.ChecksumSha256,
                    snapshot.PayloadSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(manifest.RelativeArtifactPath, snapshot.PayloadRelativePath, StringComparison.Ordinal) ||
                !string.Equals(
                    CaptureContractJson.ComputeManifestSha256(snapshot.ManifestJson),
                    snapshot.ManifestSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Transient source manifest is invalid or altered.");
            }
            var payloadPath = Resolve(snapshot.PayloadRelativePath);
            var sidecarPath = Resolve(snapshot.SidecarRelativePath);
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, payloadPath);
            RawIngressFileStore.EnsureNoSymbolicLinks(_root, sidecarPath);
            if (!File.Exists(payloadPath) || !File.Exists(sidecarPath))
            {
                throw new FileNotFoundException("Transient source evidence is no longer physically retained.");
            }
            if (new FileInfo(payloadPath).Length != snapshot.PayloadLength)
            {
                throw new InvalidDataException("Transient source payload length differs from committed evidence.");
            }
            using (var stream = new FileStream(
                       payloadPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       64 * 1024,
                       FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var payloadSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(payloadSha256, snapshot.PayloadSha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Transient source payload checksum differs from committed evidence.");
                }
            }
            var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
            if (!sidecar.AsSpan().SequenceEqual(snapshot.ManifestJson) ||
                !string.Equals(
                    CaptureContractJson.ComputeManifestSha256(sidecar),
                    snapshot.ManifestSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException("Transient source sidecar differs from committed manifest evidence.");
            }
            if (!string.Equals(snapshot.AgentId, reservation.AgentId, StringComparison.Ordinal))
            {
                throw new TransientCandidateIdentityConflictException(
                    "Transient source-hold reference belongs to a different agent.");
            }
            var recipeIdentity = ProcessingIdentity.CreateRecipeIdentity(manifest.Descriptor.Artifact.Recipe).IdentitySha256;
            if (source.Locator.Artifact.Role != FrameArtifactRole.Raw ||
                !string.Equals(
                    manifest.Descriptor.Artifact.Variant,
                    source.Locator.Artifact.Variant,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    recipeIdentity,
                    source.Locator.Artifact.RecipeIdentitySha256,
                    StringComparison.Ordinal) ||
                source.ObservationStartedUtc != manifest.Descriptor.Timing.ExposureStartedUtc ||
                source.ObservationEndedUtc != manifest.Descriptor.Timing.ExposureEndedUtc)
            {
                throw new TransientCandidateIdentityConflictException(
                    "Transient source-hold reference does not match the retained raw artifact.");
            }
            rows.Add(new SourceRow(snapshot.RawCaptureRowId, snapshot.PayloadLength, snapshot.AgentId, manifest));
        }
        return rows;
    }

    private async Task<ReservationAttempt> HandleInvalidSourceEvidenceAsync(
        TransientCandidateReservation reservation,
        string reservationIdentity,
        ReservationSnapshot snapshot,
        Exception exception,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity(
            "transient-candidate.reserve.immediate");
        using var transaction = BeginImmediate(connection);
        ReservationSnapshot current;
        try
        {
            current = await ReadReservationSnapshotAsync(
                connection, transaction, reservation, includeSources: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception currentException) when (IsSnapshotDecodeException(currentException))
        {
            var unreadable = await ClassifyUnreadableCurrentSnapshotAsync(
                connection,
                transaction,
                reservation,
                reservationIdentity,
                currentException,
                cancellationToken).ConfigureAwait(false);
            return new ReservationAttempt(Retry: false, unreadable);
        }
        if (IdentityFactsChangedWithoutAppearance(snapshot, current))
        {
            return new ReservationAttempt(Retry: true, Result: null);
        }
        var existing = await TryCompleteExistingReservationAsync(
            connection,
            transaction,
            reservation,
            reservationIdentity,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ReservationAttempt(Retry: false, existing);
        }
        if (!ReservationFactsEqual(snapshot, current))
        {
            return new ReservationAttempt(Retry: true, Result: null);
        }
        await ReserveEventAsync(connection, transaction, reservation, cancellationToken).ConfigureAwait(false);
        await QuarantineConflictAsync(
            connection,
            transaction,
            reservation.CandidateId,
            reservation.EventId,
            "source-evidence-invalid",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        activity?.Dispose();
        ExceptionDispatchInfo.Capture(exception).Throw();
        throw new UnreachableException();
    }

    private async Task<ReservationAttempt> HandleUnreadableSnapshotAsync(
        TransientCandidateReservation reservation,
        string reservationIdentity,
        Exception exception,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity(
            "transient-candidate.reserve.immediate");
        using var transaction = BeginImmediate(connection);
        var existing = await TryCompleteExistingReservationAsync(
            connection,
            transaction,
            reservation,
            reservationIdentity,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ReservationAttempt(Retry: false, existing);
        }
        try
        {
            foreach (var source in reservation.Sources)
            {
                _ = await ReadSourceArtifactSnapshotAsync(
                    connection,
                    transaction,
                    source.Locator.Artifact.ArtifactId,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception current) when (IsSnapshotDecodeException(current))
        {
            await ReserveEventAsync(connection, transaction, reservation, cancellationToken).ConfigureAwait(false);
            await QuarantineConflictAsync(
                connection,
                transaction,
                reservation.CandidateId,
                reservation.EventId,
                "source-evidence-invalid",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await transaction.DisposeAsync().ConfigureAwait(false);
            activity?.Dispose();
            throw new InvalidDataException(
                "Transient source durable evidence has an unsupported storage format.",
                exception);
        }
        return new ReservationAttempt(Retry: true, Result: null);
    }

    private async Task<ReservationAttempt> ReserveValidatedAsync(
        TransientCandidateReservation reservation,
        string reservationIdentity,
        ReservationSnapshot snapshot,
        IReadOnlyList<SourceRow> sourceRows,
        CancellationToken cancellationToken)
    {
        using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var activity = TransientWorkerTelemetry.ActivitySource.StartActivity(
            "transient-candidate.reserve.immediate");
        using var transaction = BeginImmediate(connection);
        ReservationSnapshot current;
        try
        {
            current = await ReadReservationSnapshotAsync(
                connection, transaction, reservation, includeSources: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSnapshotDecodeException(exception))
        {
            var unreadable = await ClassifyUnreadableCurrentSnapshotAsync(
                connection,
                transaction,
                reservation,
                reservationIdentity,
                exception,
                cancellationToken).ConfigureAwait(false);
            return new ReservationAttempt(Retry: false, unreadable);
        }
        if (IdentityFactsChangedWithoutAppearance(snapshot, current))
        {
            return new ReservationAttempt(Retry: true, Result: null);
        }
        var existing = await TryCompleteExistingReservationAsync(
            connection,
            transaction,
            reservation,
            reservationIdentity,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return new ReservationAttempt(Retry: false, existing);
        }
        if (!ReservationFactsEqual(snapshot, current))
        {
            return new ReservationAttempt(Retry: true, Result: null);
        }

        await ReserveEventAsync(connection, transaction, reservation, cancellationToken).ConfigureAwait(false);
        await EnsureCapacityAsync(connection, transaction, sourceRows, cancellationToken).ConfigureAwait(false);
        var now = _timeProvider.GetUtcNow();
        using (var candidate = connection.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText = """
                INSERT INTO transient_candidates(
                    candidate_id, event_id, agent_id, mode, required, reservation_identity_sha256,
                    state, phase, source_hold_released, timeout_unix_ms, created_unix_ms, updated_unix_ms)
                VALUES ($candidate, $event, $agent, $mode, $required, $identity,
                    'pending', 'reserved', 0, $timeout, $now, $now);
                """;
            candidate.Parameters.AddWithValue("$candidate", reservation.CandidateId.ToString("N"));
            candidate.Parameters.AddWithValue("$event", reservation.EventId.ToString("N"));
            candidate.Parameters.AddWithValue("$agent", reservation.AgentId);
            candidate.Parameters.AddWithValue("$mode", WriteMode(_mode));
            candidate.Parameters.AddWithValue("$required", _required ? 1 : 0);
            candidate.Parameters.AddWithValue("$identity", reservationIdentity);
            candidate.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            candidate.Parameters.AddWithValue("$timeout", now.AddMinutes(_candidateTimeoutMinutes).ToUnixTimeMilliseconds());
            await candidate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        for (var index = 0; index < reservation.Sources.Count; index++)
        {
            await InsertSourceAsync(
                connection,
                transaction,
                reservation.CandidateId,
                index,
                reservation.Sources[index],
                sourceRows[index].RawCaptureRowId,
                cancellationToken).ConfigureAwait(false);
            await SetRawHoldAsync(
                connection, transaction, sourceRows[index].RawCaptureRowId, cancellationToken).ConfigureAwait(false);
        }
        await CompleteStagedSourcesAsync(connection, transaction, sourceRows, cancellationToken).ConfigureAwait(false);
        await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        _faultInjector.Inject(TransientCandidateFaultPoint.BeforeReservationCommit);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await transaction.DisposeAsync().ConfigureAwait(false);
        activity?.Dispose();
        _faultInjector.Inject(TransientCandidateFaultPoint.AfterReservationCommit);
        var entry = new TransientCandidateJournalEntry(
            reservation.CandidateId,
            reservation.EventId,
            reservation.AgentId,
            TransientEventState.Pending,
            now,
            now,
            now.AddMinutes(_candidateTimeoutMinutes),
            [.. reservation.Sources],
            _mode,
            _required,
            TransientCandidateWorkflowPhase.IdentityAllocated,
            reservationIdentity,
            null,
            null,
            null,
            null,
            SourceHoldReleased: false,
            QuarantineReason: null,
            Candidate: null,
            FinalizationReceipt: null,
            Submission: null,
            Acknowledgement: null);
        return new ReservationAttempt(
            Retry: false,
            new TransientCandidateReservationResult(TransientCandidateReservationDisposition.Created, entry));
    }

    private async Task<TransientCandidateReservationResult> ClassifyUnreadableCurrentSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransientCandidateReservation reservation,
        string reservationIdentity,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var existing = await TryCompleteExistingReservationAsync(
            connection,
            transaction,
            reservation,
            reservationIdentity,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }
        await ReserveEventAsync(connection, transaction, reservation, cancellationToken).ConfigureAwait(false);
        await QuarantineConflictAsync(
            connection,
            transaction,
            reservation.CandidateId,
            reservation.EventId,
            "source-evidence-invalid",
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        throw new InvalidDataException(
            "Transient source durable evidence has an unsupported storage format.",
            exception);
    }

    private async Task<TransientCandidateReservationResult?> TryCompleteExistingReservationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TransientCandidateReservation reservation,
        string reservationIdentity,
        CancellationToken cancellationToken)
    {
        using (var quarantined = connection.CreateCommand())
        {
            quarantined.Transaction = transaction;
            quarantined.CommandText = "SELECT EXISTS(SELECT 1 FROM transient_candidate_conflicts WHERE candidate_id = $candidate);";
            quarantined.Parameters.AddWithValue("$candidate", reservation.CandidateId.ToString("N"));
            if (Convert.ToInt64(
                    await quarantined.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) == 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw new TransientCandidateIdentityConflictException(
                    "Transient candidate identity is durably quarantined.");
            }
        }
        var existing = await ReadAsync(
            connection, transaction, reservation.CandidateId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }
        if (!Matches(existing, reservation) ||
            !string.Equals(existing.ReservationIdentitySha256, reservationIdentity, StringComparison.Ordinal) ||
            !await CandidateSourceBindingsMatchAsync(
                connection, transaction, reservation.CandidateId, cancellationToken).ConfigureAwait(false))
        {
            await QuarantineConflictAsync(
                connection,
                transaction,
                reservation.CandidateId,
                reservation.EventId,
                "candidate-identity-conflict",
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new TransientCandidateIdentityConflictException(
                "Transient candidate identity is already reserved for different immutable facts.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TransientCandidateReservationResult(
            TransientCandidateReservationDisposition.Existing,
            existing);
    }

    private static async Task<bool> CandidateSourceBindingsMatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT NOT EXISTS(
                SELECT 1
                FROM transient_candidate_sources s
                JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                LEFT JOIN raw_captures r ON r.raw_capture_row_id = s.raw_capture_row_id
                WHERE s.candidate_id = $candidate
                  AND (r.raw_capture_row_id IS NULL OR r.raw_artifact_id != s.artifact_id
                       OR r.payload_sha256 COLLATE NOCASE != s.checksum_sha256 COLLATE NOCASE
                       OR r.agent_id != c.agent_id
                       OR r.state != 'committed'));
            """;
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture) == 1;
    }

    private static bool IsSourceEvidenceException(Exception exception)
        => exception is IOException or InvalidDataException or UnauthorizedAccessException or
            TransientCandidateIdentityConflictException;

    private static bool IsSnapshotDecodeException(Exception exception)
        => exception is InvalidDataException or InvalidCastException or FormatException or OverflowException;

    private static bool ReservationFactsEqual(ReservationSnapshot left, ReservationSnapshot right)
        => DurableFactsEqual(left.CandidateFacts, right.CandidateFacts) &&
           DurableFactsEqual(left.CandidateSourceFacts, right.CandidateSourceFacts) &&
           DurableFactsEqual(left.EventFacts, right.EventFacts) &&
           DurableFactsEqual(left.ConflictFacts, right.ConflictFacts) &&
           SourceSnapshotsEqual(left.Sources, right.Sources);

    private static bool IdentityFactsChangedWithoutAppearance(
        ReservationSnapshot snapshot,
        ReservationSnapshot current)
    {
        var changed = !DurableFactsEqual(snapshot.CandidateFacts, current.CandidateFacts) ||
                      !DurableFactsEqual(snapshot.CandidateSourceFacts, current.CandidateSourceFacts) ||
                      !DurableFactsEqual(snapshot.EventFacts, current.EventFacts) ||
                      !DurableFactsEqual(snapshot.ConflictFacts, current.ConflictFacts);
        if (!changed)
        {
            return false;
        }
        var candidateAppeared = snapshot.CandidateFacts.Rows.Count == 0 && current.CandidateFacts.Rows.Count > 0;
        var conflictAppeared = snapshot.ConflictFacts.Rows.Count == 0 && current.ConflictFacts.Rows.Count > 0;
        return !candidateAppeared && !conflictAppeared;
    }

    private static bool DurableFactsEqual(DurableFacts left, DurableFacts right)
        => left.Rows.Count == right.Rows.Count && left.Rows.Zip(right.Rows).All(
            static rows => rows.First.SequenceEqual(rows.Second, StringComparer.Ordinal));

    private static bool SourceSnapshotsEqual(
        IReadOnlyList<SourceArtifactSnapshot?> left,
        IReadOnlyList<SourceArtifactSnapshot?> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }
        for (var index = 0; index < left.Count; index++)
        {
            var first = left[index];
            var second = right[index];
            if (first is null || second is null)
            {
                if (first is not null || second is not null)
                {
                    return false;
                }
                continue;
            }
            if (first.RawCaptureRowId != second.RawCaptureRowId ||
                first.CaptureSequence != second.CaptureSequence ||
                first.PayloadLength != second.PayloadLength ||
                !string.Equals(first.CaptureId, second.CaptureId, StringComparison.Ordinal) ||
                !string.Equals(first.RawArtifactId, second.RawArtifactId, StringComparison.Ordinal) ||
                !string.Equals(first.AgentId, second.AgentId, StringComparison.Ordinal) ||
                !string.Equals(first.State, second.State, StringComparison.Ordinal) ||
                !string.Equals(first.DescriptorSha256, second.DescriptorSha256, StringComparison.Ordinal) ||
                !string.Equals(first.PayloadRelativePath, second.PayloadRelativePath, StringComparison.Ordinal) ||
                !string.Equals(first.SidecarRelativePath, second.SidecarRelativePath, StringComparison.Ordinal) ||
                !string.Equals(first.PayloadSha256, second.PayloadSha256, StringComparison.Ordinal) ||
                !string.Equals(first.ManifestSha256, second.ManifestSha256, StringComparison.Ordinal) ||
                !first.ManifestJson.AsSpan().SequenceEqual(second.ManifestJson))
            {
                return false;
            }
        }
        return true;
    }

    private async Task<SourceRow> ValidateCommittedArtifactAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid artifactId,
        string expectedPayloadSha256,
        string? expectedManifestSha256,
        CancellationToken cancellationToken)
    {
        long rawRowId;
        long payloadLength;
        string agentId;
        string payloadSha256;
        string manifestSha256;
        string payloadRelativePath;
        string sidecarRelativePath;
        byte[] manifestJson;
        string state;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT raw_capture_row_id, payload_length, agent_id, payload_sha256,
                       manifest_sha256, payload_relative_path, sidecar_relative_path,
                       manifest_json, state
                FROM raw_captures WHERE raw_artifact_id = $artifact;
                """;
            command.Parameters.AddWithValue("$artifact", artifactId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("Transient source-hold reference does not identify committed raw evidence.");
            }
            rawRowId = reader.GetInt64(0);
            payloadLength = reader.GetInt64(1);
            agentId = reader.GetString(2);
            payloadSha256 = reader.GetString(3);
            manifestSha256 = reader.GetString(4);
            payloadRelativePath = reader.GetString(5);
            sidecarRelativePath = reader.GetString(6);
            manifestJson = (byte[])reader[7];
            state = reader.GetString(8);
        }
        if (!string.Equals(state, "committed", StringComparison.Ordinal) ||
            !string.Equals(payloadSha256, expectedPayloadSha256, StringComparison.OrdinalIgnoreCase) ||
            expectedManifestSha256 is not null &&
            !string.Equals(manifestSha256, expectedManifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transient source-hold reference conflicts with committed raw evidence.");
        }
        var parsed = CaptureContractJson.ParseManifest(manifestJson);
        var manifest = parsed.Document?.Manifest;
        if (!parsed.IsValid || manifest is null ||
            manifest.Descriptor.Artifact.ArtifactId != artifactId ||
            manifest.Descriptor.Artifact.Role != FrameArtifactRole.Raw ||
            !string.Equals(manifest.Descriptor.Capture.AgentId, agentId, StringComparison.Ordinal) ||
            !string.Equals(manifest.Descriptor.Artifact.ChecksumSha256, payloadSha256, StringComparison.Ordinal) ||
            !string.Equals(manifest.RelativeArtifactPath, payloadRelativePath, StringComparison.Ordinal) ||
            !string.Equals(CaptureContractJson.ComputeManifestSha256(manifestJson), manifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transient source manifest is invalid or altered.");
        }
        var payloadPath = Resolve(payloadRelativePath);
        var sidecarPath = Resolve(sidecarRelativePath);
        if (!File.Exists(payloadPath) || !File.Exists(sidecarPath))
        {
            throw new FileNotFoundException("Transient source evidence is no longer physically retained.");
        }
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, payloadPath);
        RawIngressFileStore.EnsureNoSymbolicLinks(_root, sidecarPath);
        if (new FileInfo(payloadPath).Length != payloadLength)
        {
            throw new InvalidDataException("Transient source payload length differs from committed evidence.");
        }
        using (var stream = new FileStream(
                         payloadPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var actualPayloadSha = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
            if (!string.Equals(actualPayloadSha, payloadSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Transient source payload checksum differs from committed evidence.");
            }
        }
        var sidecar = await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        if (!sidecar.AsSpan().SequenceEqual(manifestJson) ||
            !string.Equals(CaptureContractJson.ComputeManifestSha256(sidecar), manifestSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transient source sidecar differs from committed manifest evidence.");
        }
        return new SourceRow(rawRowId, payloadLength, agentId, manifest);
    }

    private async Task EnsureCapacityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SourceRow> sources,
        CancellationToken cancellationToken)
    {
        var (count, bytes, oldest, quarantined) = await ReadActiveTotalsAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var additionalBytes = 0L;
        var replacedCaptureWork = 0L;
        foreach (var source in sources.DistinctBy(static source => source.RawCaptureRowId))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(
                    SELECT 1
                    FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE s.raw_capture_row_id = $raw AND c.source_hold_released = 0
                    UNION ALL
                    SELECT 1 FROM transient_capture_work
                    WHERE raw_capture_row_id = $raw AND state = 'pending');
                """;
            command.Parameters.AddWithValue("$raw", source.RawCaptureRowId);
            var held = Convert.ToInt64(
                await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture) == 1;
            if (!held)
            {
                additionalBytes = checked(additionalBytes + source.PayloadLength);
            }
            using var staged = connection.CreateCommand();
            staged.Transaction = transaction;
            staged.CommandText = "SELECT EXISTS(SELECT 1 FROM transient_capture_work WHERE raw_capture_row_id = $raw AND state = 'pending');";
            staged.Parameters.AddWithValue("$raw", source.RawCaptureRowId);
            replacedCaptureWork += Convert.ToInt64(
                await staged.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        var maximumCount = _required
            ? _limits.RequiredMaximumPendingCount
            : _limits.OptionalMaximumPendingCount;
        var maximumBytes = _required
            ? _limits.RequiredMaximumPendingBytes
            : _limits.OptionalMaximumPendingBytes;
        var maximumAge = _required
            ? _limits.RequiredMaximumOldestAgeMinutes
            : _limits.OptionalMaximumOldestAgeMinutes;
        var age = oldest is null ? TimeSpan.Zero : _timeProvider.GetUtcNow() - oldest.Value;
        using var pressure = connection.CreateCommand();
        pressure.Transaction = transaction;
        pressure.CommandText = "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient';";
        var priorPressure = Convert.ToInt32(
            await pressure.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        var recovered = quarantined == 0 &&
                        count * 100 < maximumCount * _limits.PressureRecoveryPercent &&
                        !CaptureLanePressureMath.IsAtOrAbovePercentage(
                            bytes, maximumBytes, _limits.PressureRecoveryPercent) &&
                        age.TotalMinutes * 100 < maximumAge * _limits.PressureRecoveryPercent;
        var projectedCount = checked(count + 1 - replacedCaptureWork);
        var increasesBacklog = projectedCount > count || additionalBytes > 0;
        if (increasesBacklog &&
            (quarantined > 0 || priorPressure == 2 && !recovered ||
             projectedCount > maximumCount ||
             CaptureLanePressureMath.ExceedsAfterAdding(bytes, additionalBytes, maximumBytes) ||
             age >= TimeSpan.FromMinutes(maximumAge)))
        {
            throw new TransientCandidateJournalCapacityException(
                "Transient candidate journal reached the configured capture-lane count, held-byte, or age limit.");
        }
    }

    private static async Task InsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        int ordinal,
        TransientSourceEvidenceReferenceV1 source,
        long rawCaptureRowId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO transient_candidate_sources(
                candidate_id, source_ordinal, evidence_id, raw_capture_row_id,
                source_schema, locator_schema, locator_kind, artifact_id, artifact_role,
                artifact_variant, recipe_identity_sha256, checksum_sha256,
                observation_started_utc_ticks, observation_ended_utc_ticks,
                timing_quality, timing_source, timing_version)
            VALUES ($candidate, $ordinal, $evidence, $raw, $source_schema, $locator_schema,
                $locator_kind, $artifact, $role, $variant, $recipe, $checksum,
                $started, $ended, $quality, $timing_source, $timing_version);
            """;
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$evidence", source.EvidenceId.ToString("N"));
        command.Parameters.AddWithValue("$raw", rawCaptureRowId);
        command.Parameters.AddWithValue("$source_schema", source.SchemaVersion);
        command.Parameters.AddWithValue("$locator_schema", source.Locator.SchemaVersion);
        command.Parameters.AddWithValue("$locator_kind", (int)source.Locator.Kind);
        command.Parameters.AddWithValue("$artifact", source.Locator.Artifact.ArtifactId.ToString("N"));
        command.Parameters.AddWithValue("$role", (int)source.Locator.Artifact.Role);
        command.Parameters.AddWithValue("$variant", source.Locator.Artifact.Variant);
        command.Parameters.AddWithValue("$recipe", source.Locator.Artifact.RecipeIdentitySha256);
        command.Parameters.AddWithValue("$checksum", source.Locator.Artifact.ChecksumSha256);
        command.Parameters.AddWithValue("$started", source.ObservationStartedUtc.UtcTicks);
        command.Parameters.AddWithValue("$ended", source.ObservationEndedUtc.UtcTicks);
        command.Parameters.AddWithValue("$quality", (int)source.TimingQuality);
        command.Parameters.AddWithValue("$timing_source", source.TimingProvenance.Source);
        command.Parameters.AddWithValue("$timing_version", source.TimingProvenance.Version);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SetRawHoldAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE raw_captures SET retention_hold = 1 WHERE raw_capture_row_id = $raw;";
        command.Parameters.AddWithValue("$raw", rawRowId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CompleteStagedSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<SourceRow> sources,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources.DistinctBy(static source => source.RawCaptureRowId))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE transient_capture_work
                SET state = 'candidate_persisted', updated_unix_ms = unixepoch('subsec') * 1000
                WHERE raw_capture_row_id = $raw AND state = 'pending';
                """;
            command.Parameters.AddWithValue("$raw", source.RawCaptureRowId);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RecomputeCandidateSourceHoldsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        foreach (var rawRowId in await ReadSourceRawRowsAsync(
                     connection, transaction, candidateId, cancellationToken).ConfigureAwait(false))
        {
            await RecomputeRawHoldAsync(connection, transaction, rawRowId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RecomputeRawHoldAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRowId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE raw_captures
            SET retention_hold = CASE WHEN
                EXISTS (
                    SELECT 1 FROM capture_lane_work
                    WHERE raw_capture_row_id = $raw
                      AND ((required = 1 AND state != 'completed') OR state = 'leased'))
                OR EXISTS (
                    SELECT 1
                    FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE s.raw_capture_row_id = $raw AND c.source_hold_released = 0)
                OR EXISTS (
                    SELECT 1 FROM transient_capture_work
                    WHERE raw_capture_row_id = $raw AND state = 'pending')
                THEN 1 ELSE 0 END
            WHERE raw_capture_row_id = $raw;
            """;
        command.Parameters.AddWithValue("$raw", rawRowId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<long>> ReadSourceRawRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT DISTINCT raw_capture_row_id FROM transient_candidate_sources WHERE candidate_id = $candidate;";
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        var rows = new List<long>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(reader.GetInt64(0));
        }
        return rows;
    }

    private async Task QuarantineConflictAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid? candidateId,
        Guid? eventId,
        string reason,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO transient_candidate_conflicts(
                    candidate_id, event_id, reason, observed_unix_ms)
                VALUES ($candidate, $event, $reason, $now);
                """;
            insert.Parameters.AddWithValue("$candidate", candidateId?.ToString("N") ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$event", eventId?.ToString("N") ?? (object)DBNull.Value);
            insert.Parameters.AddWithValue("$reason", reason);
            insert.Parameters.AddWithValue("$now", now);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        if (candidateId is not null)
        {
            using var quarantine = connection.CreateCommand();
            quarantine.Transaction = transaction;
            quarantine.CommandText = """
                UPDATE transient_candidates
                SET state = 'needs_review', phase = 'quarantined', quarantine_reason = $reason,
                    updated_unix_ms = $now
                WHERE candidate_id = $candidate;
                """;
            quarantine.Parameters.AddWithValue("$reason", reason);
            quarantine.Parameters.AddWithValue("$now", now);
            quarantine.Parameters.AddWithValue("$candidate", candidateId.Value.ToString("N"));
            await quarantine.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await UpdatePressureAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task QuarantinePersistedMismatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        Guid eventId,
        string reason,
        CancellationToken cancellationToken)
    {
        await QuarantineConflictAsync(
            connection,
            transaction,
            candidateId,
            eventId,
            reason,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        throw new TransientCandidateIdentityConflictException(
            "Durable transient canonical content failed identity verification and was quarantined.");
    }

    private static string? VerifyPersistedEntry(
        TransientCandidateJournalEntry entry,
        byte[]? candidatePayload,
        byte[]? finalizationPayload,
        byte[]? submissionPayload,
        byte[]? acknowledgementPayload)
    {
        if (entry.Sources.Count == 0 ||
            entry.Sources.Any(source => !TransientContractJson.ValidateSourceEvidence(source).IsValid))
        {
            return "reservation-source-invalid";
        }
        var reservationIdentity = ComputeReservationIdentitySha256(
            entry.CandidateId,
            entry.EventId,
            entry.AgentId,
            entry.Mode,
            entry.Required,
            entry.Sources);
        if (!string.Equals(reservationIdentity, entry.ReservationIdentitySha256, StringComparison.Ordinal))
        {
            return "reservation-identity-mismatch";
        }

        if ((candidatePayload is null) != (entry.CandidatePayloadSha256 is null) ||
            (candidatePayload is null) != (entry.Candidate is null))
        {
            return "candidate-presence-mismatch";
        }
        if (candidatePayload is not null)
        {
            if (entry.Candidate is null ||
                !candidatePayload.AsSpan().SequenceEqual(TransientContractJson.Serialize(entry.Candidate)) ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(candidatePayload)),
                    entry.CandidatePayloadSha256,
                    StringComparison.Ordinal) ||
                entry.Candidate.CandidateId != entry.CandidateId ||
                entry.Candidate.EventId != entry.EventId ||
                !string.Equals(entry.Candidate.AgentId, entry.AgentId, StringComparison.Ordinal) ||
                !entry.Candidate.ContextSources.SequenceEqual(entry.Sources))
            {
                return "candidate-content-mismatch";
            }
        }

        if ((finalizationPayload is null) != (entry.FinalizationReceiptIdentitySha256 is null) ||
            (finalizationPayload is null) != (entry.FinalizationReceipt is null))
        {
            return "finalization-presence-mismatch";
        }
        if (finalizationPayload is not null)
        {
            if (entry.FinalizationReceipt is null || entry.Candidate is null ||
                !finalizationPayload.AsSpan().SequenceEqual(
                    TransientCandidateDeliveryJson.Serialize(entry.FinalizationReceipt)) ||
                !string.Equals(
                    entry.FinalizationReceipt.ReceiptIdentitySha256,
                    entry.FinalizationReceiptIdentitySha256,
                    StringComparison.Ordinal) ||
                entry.FinalizationReceipt.CandidateId != entry.CandidateId ||
                entry.FinalizationReceipt.EventId != entry.EventId ||
                !string.Equals(entry.FinalizationReceipt.Event.AgentId, entry.AgentId, StringComparison.Ordinal) ||
                entry.FinalizationReceipt.Event.Observations
                    .Where(observation => observation.Extraction.OriginatingCandidateId == entry.CandidateId)
                    .Any(observation => !entry.Sources.Contains(observation.Source)))
            {
                return "finalization-content-mismatch";
            }
        }

        if ((submissionPayload is null) != (entry.SubmissionIdentitySha256 is null) ||
            (submissionPayload is null) != (entry.Submission is null))
        {
            return "submission-presence-mismatch";
        }
        if (submissionPayload is not null)
        {
            if (entry.Submission is null || entry.Candidate is null ||
                !submissionPayload.AsSpan().SequenceEqual(
                    TransientCandidateDeliveryJson.Serialize(entry.Submission)) ||
                !string.Equals(
                    entry.Submission.SubmissionIdentitySha256,
                    entry.SubmissionIdentitySha256,
                    StringComparison.Ordinal) ||
                entry.Submission.CandidateId != entry.CandidateId ||
                entry.Submission.EventId != entry.EventId ||
                !TransientContractJson.Serialize(entry.Submission.Candidate).AsSpan().SequenceEqual(
                    TransientContractJson.Serialize(entry.Candidate)))
            {
                return "submission-content-mismatch";
            }
        }

        if ((acknowledgementPayload is null) != (entry.AcknowledgementPayloadSha256 is null) ||
            (acknowledgementPayload is null) != (entry.Acknowledgement is null))
        {
            return "acknowledgement-presence-mismatch";
        }
        if (acknowledgementPayload is not null)
        {
            if (entry.Acknowledgement is null || entry.Submission is null ||
                !acknowledgementPayload.AsSpan().SequenceEqual(
                    TransientCandidateDeliveryJson.Serialize(entry.Acknowledgement)) ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(acknowledgementPayload)),
                    entry.AcknowledgementPayloadSha256,
                    StringComparison.Ordinal) ||
                !TransientCandidateDeliveryJson.Matches(entry.Acknowledgement, entry.Submission))
            {
                return "acknowledgement-content-mismatch";
            }
        }

        if (entry.Mode == TransientOperatingMode.Hybrid && entry.SourceHoldReleased && entry.Acknowledgement is null ||
            entry.Mode == TransientOperatingMode.Edge && entry.SourceHoldReleased && entry.FinalizationReceipt is null)
        {
            return "hold-release-mismatch";
        }
        if (entry.Acknowledgement is not null &&
            entry.Phase is not (TransientCandidateWorkflowPhase.Acknowledged or TransientCandidateWorkflowPhase.Quarantined))
        {
            return "acknowledgement-phase-mismatch";
        }
        if (entry.Submission is not null && entry.Acknowledgement is null &&
            entry.Phase is not (TransientCandidateWorkflowPhase.HandoffPending or TransientCandidateWorkflowPhase.Quarantined))
        {
            return "submission-phase-mismatch";
        }
        return null;
    }

    private async Task UpdatePressureAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var (count, bytes, oldest, quarantined) = await ReadActiveTotalsAsync(
            connection, transaction, cancellationToken).ConfigureAwait(false);
        var maximumCount = _required
            ? _limits.RequiredMaximumPendingCount
            : _limits.OptionalMaximumPendingCount;
        var maximumBytes = _required
            ? _limits.RequiredMaximumPendingBytes
            : _limits.OptionalMaximumPendingBytes;
        var maximumAge = _required
            ? _limits.RequiredMaximumOldestAgeMinutes
            : _limits.OptionalMaximumOldestAgeMinutes;
        var age = oldest is null ? TimeSpan.Zero : _timeProvider.GetUtcNow() - oldest.Value;
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
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT pressure_state FROM capture_lane_definitions WHERE lane_name = 'transient';";
        var value = await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (value is null)
        {
            throw new InvalidOperationException("Transient lane pressure state is not initialized.");
        }
        var prior = Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
        var next = hard || prior == 2 && !recovered ? 2 : warning ? 1 : 0;
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE capture_lane_definitions SET pressure_state = $pressure WHERE lane_name = 'transient';";
        update.Parameters.AddWithValue("$pressure", next);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyActiveCandidatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var candidateIds = new List<Guid>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT candidate_id FROM transient_candidates WHERE source_hold_released = 0 AND phase != 'quarantined' ORDER BY candidate_id;";
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                candidateIds.Add(Guid.ParseExact(reader.GetString(0), "N"));
            }
        }
        foreach (var candidateId in candidateIds)
        {
            _ = await ReadAsync(connection, transaction, candidateId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TransientCandidateJournalEntry?> ReadAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        Guid eventId;
        string agentId;
        TransientEventState state;
        DateTimeOffset created;
        DateTimeOffset updated;
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT event_id, agent_id, mode, required, state, phase,
                       reservation_identity_sha256, candidate_payload_sha256, candidate_payload,
                       finalization_receipt_identity_sha256, finalization_payload,
                       submission_identity_sha256, submission_payload,
                       acknowledgement_payload_sha256, acknowledgement_payload,
                       source_hold_released, quarantine_reason,
                       timeout_unix_ms, created_unix_ms, updated_unix_ms
                FROM transient_candidates WHERE candidate_id = $candidate;
                """;
            command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            eventId = Guid.ParseExact(reader.GetString(0), "N");
            agentId = reader.GetString(1);
            var mode = ReadMode(reader.GetString(2));
            var required = reader.GetBoolean(3);
            state = ReadState(reader.GetString(4));
            var phase = ReadPhase(reader.GetString(5));
            var reservationIdentity = reader.GetString(6);
            var candidatePayloadSha = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7);
            var candidatePayload = await reader.IsDBNullAsync(8, cancellationToken).ConfigureAwait(false)
                ? null
                : (byte[])reader[8];
            var candidate = candidatePayload is null
                ? null
                : TransientContractJson.ParseCandidate(candidatePayload).Value;
            var finalizationIdentity = await reader.IsDBNullAsync(9, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(9);
            var finalizationPayload = await reader.IsDBNullAsync(10, cancellationToken).ConfigureAwait(false)
                ? null
                : (byte[])reader[10];
            var finalization = finalizationPayload is null
                ? null
                : TransientCandidateDeliveryJson.ParseFinalization(finalizationPayload).Value;
            var submissionIdentity = await reader.IsDBNullAsync(11, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(11);
            var submissionPayload = await reader.IsDBNullAsync(12, cancellationToken).ConfigureAwait(false)
                ? null
                : (byte[])reader[12];
            var submission = submissionPayload is null
                ? null
                : TransientCandidateDeliveryJson.ParseSubmission(submissionPayload).Value;
            var acknowledgementSha = await reader.IsDBNullAsync(13, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(13);
            var acknowledgementPayload = await reader.IsDBNullAsync(14, cancellationToken).ConfigureAwait(false)
                ? null
                : (byte[])reader[14];
            var acknowledgement = acknowledgementPayload is null
                ? null
                : TransientCandidateDeliveryJson.ParseAcknowledgement(acknowledgementPayload).Value;
            var sourceHoldReleased = reader.GetBoolean(15);
            var quarantineReason = await reader.IsDBNullAsync(16, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(16);
            var timeout = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(17));
            created = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(18));
            updated = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(19));
            var sources = await ReadSourcesAfterReaderAsync(
                reader, connection, transaction, candidateId, cancellationToken).ConfigureAwait(false);
            var entry = new TransientCandidateJournalEntry(
                candidateId,
                eventId,
                agentId,
                state,
                created,
                updated,
                timeout,
                sources,
                mode,
                required,
                phase,
                reservationIdentity,
                candidatePayloadSha,
                finalizationIdentity,
                submissionIdentity,
                acknowledgementSha,
                sourceHoldReleased,
                quarantineReason,
                candidate,
                finalization,
                submission,
                acknowledgement);
            var mismatch = VerifyPersistedEntry(
                entry,
                candidatePayload,
                finalizationPayload,
                submissionPayload,
                acknowledgementPayload);
            if (mismatch is not null)
            {
                await QuarantinePersistedMismatchAsync(
                    connection,
                    transaction,
                    candidateId,
                    eventId,
                    mismatch,
                    cancellationToken).ConfigureAwait(false);
            }
            return entry;
        }
    }

    private static async Task<IReadOnlyList<TransientSourceEvidenceReferenceV1>> ReadSourcesAfterReaderAsync(
        SqliteDataReader reader,
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        await reader.DisposeAsync().ConfigureAwait(false);
        return await ReadSourcesAsync(connection, transaction, candidateId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TransientSourceEvidenceReferenceV1>> ReadSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_schema, evidence_id, locator_schema, locator_kind, artifact_id,
                   artifact_role, artifact_variant, recipe_identity_sha256, checksum_sha256,
                   observation_started_utc_ticks, observation_ended_utc_ticks,
                   timing_quality, timing_source, timing_version
            FROM transient_candidate_sources
            WHERE candidate_id = $candidate
            ORDER BY source_ordinal;
            """;
        command.Parameters.AddWithValue("$candidate", candidateId.ToString("N"));
        var sources = new List<TransientSourceEvidenceReferenceV1>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(new TransientSourceEvidenceReferenceV1(
                reader.GetString(0),
                Guid.ParseExact(reader.GetString(1), "N"),
                new TransientWholeArtifactLocatorV1(
                    reader.GetString(2),
                    (TransientSourceLocatorKind)reader.GetInt32(3),
                    new TransientArtifactReferenceV1(
                        Guid.ParseExact(reader.GetString(4), "N"),
                        (HVO.SkyMonitor.AgentCore.FrameArtifactRole)reader.GetInt32(5),
                        reader.GetString(6),
                        reader.GetString(7),
                        reader.GetString(8))),
                new DateTimeOffset(reader.GetInt64(9), TimeSpan.Zero),
                new DateTimeOffset(reader.GetInt64(10), TimeSpan.Zero),
                (TransientTimingQuality)reader.GetInt32(11),
                new TransientTimingProvenanceV1(reader.GetString(12), reader.GetString(13))));
        }
        return sources;
    }

    private static async Task<(long Count, long Bytes, DateTimeOffset? Oldest, long Quarantined)> ReadActiveTotalsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM transient_capture_work WHERE state IN ('pending', 'quarantined')) +
                    (SELECT COUNT(*) FROM transient_candidates WHERE source_hold_released = 0),
                (SELECT COALESCE(SUM(payload_length), 0) FROM raw_captures WHERE raw_capture_row_id IN (
                    SELECT raw_capture_row_id FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                    UNION
                    SELECT s.raw_capture_row_id
                    FROM transient_candidate_sources s
                    JOIN transient_candidates c ON c.candidate_id = s.candidate_id
                    WHERE c.source_hold_released = 0)),
                (SELECT MIN(created_unix_ms) FROM (
                    SELECT created_unix_ms FROM transient_capture_work WHERE state IN ('pending', 'quarantined')
                    UNION ALL
                    SELECT created_unix_ms FROM transient_candidates WHERE source_hold_released = 0)),
                (SELECT COUNT(*) FROM transient_capture_work WHERE state = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidates WHERE phase = 'quarantined') +
                    (SELECT COUNT(*) FROM transient_candidate_conflicts);
            """;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (
            reader.GetInt64(0),
            reader.GetInt64(1),
            await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            reader.GetInt64(3));
    }

    private static string WriteState(TransientEventState state) => state switch
    {
        TransientEventState.Pending => "pending",
        TransientEventState.Provisional => "provisional",
        TransientEventState.Validated => "validated",
        TransientEventState.Rejected => "rejected",
        TransientEventState.NeedsReview => "needs_review",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static TransientEventState ReadState(string state) => state switch
    {
        "pending" => TransientEventState.Pending,
        "provisional" => TransientEventState.Provisional,
        "validated" => TransientEventState.Validated,
        "rejected" => TransientEventState.Rejected,
        "needs_review" => TransientEventState.NeedsReview,
        _ => throw new InvalidDataException("Transient candidate journal contains an unsupported state.")
    };

    private static string WriteMode(TransientOperatingMode mode) => mode switch
    {
        TransientOperatingMode.Edge => "edge",
        TransientOperatingMode.Hybrid => "hybrid",
        _ => throw new InvalidOperationException("Only edge transient modes own local candidate state.")
    };

    private static TransientOperatingMode ReadMode(string mode) => mode switch
    {
        "edge" => TransientOperatingMode.Edge,
        "hybrid" => TransientOperatingMode.Hybrid,
        _ => throw new InvalidDataException("Transient candidate journal contains an unsupported operating mode.")
    };

    private static string WritePhase(TransientCandidateWorkflowPhase phase) => phase switch
    {
        TransientCandidateWorkflowPhase.IdentityAllocated => "reserved",
        TransientCandidateWorkflowPhase.CandidatePersisted => "candidate_persisted",
        TransientCandidateWorkflowPhase.Finalized => "finalized",
        TransientCandidateWorkflowPhase.HandoffPending => "handoff_pending",
        TransientCandidateWorkflowPhase.Acknowledged => "acknowledged",
        TransientCandidateWorkflowPhase.Quarantined => "quarantined",
        _ => throw new ArgumentOutOfRangeException(nameof(phase))
    };

    private static TransientCandidateWorkflowPhase ReadPhase(string phase) => phase switch
    {
        "reserved" => TransientCandidateWorkflowPhase.IdentityAllocated,
        "candidate_persisted" => TransientCandidateWorkflowPhase.CandidatePersisted,
        "finalized" => TransientCandidateWorkflowPhase.Finalized,
        "handoff_pending" => TransientCandidateWorkflowPhase.HandoffPending,
        "acknowledged" => TransientCandidateWorkflowPhase.Acknowledged,
        "quarantined" => TransientCandidateWorkflowPhase.Quarantined,
        _ => throw new InvalidDataException("Transient candidate journal contains an unsupported workflow phase.")
    };

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The interpolated value is a validated integer host option used only for SQLite PRAGMA configuration.")]
    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
            DefaultTimeout = _busyTimeoutSeconds
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA busy_timeout = {_busyTimeoutSeconds * 1000}; PRAGMA foreign_keys = ON; PRAGMA synchronous = FULL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private string Resolve(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(_root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(
                prefix,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("Transient evidence path escapes the configured raw ingress root.");
        }
        return path;
    }

    private static SqliteTransaction BeginImmediate(SqliteConnection connection)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes immediate transactions only through the synchronous overload.
        return connection.BeginTransaction(deferred: false);
#pragma warning restore CA1849
    }

    private static SqliteTransaction BeginDeferred(SqliteConnection connection)
    {
#pragma warning disable CA1849 // Microsoft.Data.Sqlite exposes deferred transactions only through the synchronous overload.
        return connection.BeginTransaction(deferred: true);
#pragma warning restore CA1849
    }

    private sealed record ReservationAttempt(
        bool Retry,
        TransientCandidateReservationResult? Result);

    private sealed record ReservationSnapshot(
        DurableFacts CandidateFacts,
        DurableFacts CandidateSourceFacts,
        DurableFacts EventFacts,
        DurableFacts ConflictFacts,
        IReadOnlyList<SourceArtifactSnapshot?> Sources);

    private sealed record DurableFacts(IReadOnlyList<IReadOnlyList<string>> Rows);

    private sealed record SourceArtifactSnapshot(
        long RawCaptureRowId,
        string CaptureId,
        string RawArtifactId,
        string AgentId,
        long CaptureSequence,
        string State,
        string DescriptorSha256,
        string PayloadRelativePath,
        string SidecarRelativePath,
        long PayloadLength,
        string PayloadSha256,
        string ManifestSha256,
        byte[] ManifestJson);

    private sealed record SourceRow(
        long RawCaptureRowId,
        long PayloadLength,
        string AgentId,
        ArtifactManifestV2 Manifest);
}

[Serializable]
public sealed class TransientCandidateIdentityConflictException : InvalidOperationException
{
    public TransientCandidateIdentityConflictException()
    {
    }

    public TransientCandidateIdentityConflictException(string message)
        : base(message)
    {
    }

    public TransientCandidateIdentityConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[Serializable]
public sealed class TransientCandidateJournalCapacityException : IOException
{
    public TransientCandidateJournalCapacityException()
    {
    }

    public TransientCandidateJournalCapacityException(string message)
        : base(message)
    {
    }

    public TransientCandidateJournalCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
