using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Evidence;

/// <summary>
/// Durable source outbox for the transport-neutral <c>hvo-cameraagent-execution-evidence-v1</c> contract. Every
/// enlisted unit is retained until a matching terminal acknowledgement or an explicit operator disposition; a unit
/// is never rewritten, and its canonical bytes are sealed once so a re-send is byte-identical.
/// </summary>
public interface IExecutionEvidenceOutbox
{
    /// <summary>Creates or validates the durable store beneath <paramref name="root"/>. Idempotent per root.</summary>
    ValueTask InitializeAsync(string root, CancellationToken cancellationToken);

    /// <summary>Registers this boot session's origin, or returns the row already stored for the same identity.</summary>
    ValueTask<ExecutionEvidenceOriginRecord> EnsureOriginAsync(
        string root,
        ExecutionEvidenceOriginV1 origin,
        CancellationToken cancellationToken);

    /// <summary>Reads the bounded source-sweep cursor. It advances only over executions that were enlisted.</summary>
    ValueTask<ExecutionEvidenceDiscoveryCursor> ReadDiscoveryCursorAsync(
        string root,
        CancellationToken cancellationToken);

    /// <summary>
    /// Seals and enlists the supplied units under one transaction, assigning strictly increasing origin sequences in
    /// the order supplied, and advances the discovery cursor in the same transaction. A unit key already present with
    /// identical canonical bytes is idempotent; the same key with different bytes is a durable conflict.
    /// </summary>
    ValueTask<ExecutionEvidenceEnlistmentResult> EnlistAsync(
        string root,
        string originIdentitySha256,
        Guid executionId,
        IReadOnlyList<ExecutionEvidenceEnlistmentUnit> units,
        ExecutionEvidenceDiscoveryCursor cursor,
        ExecutionEvidenceEnlistmentLimits limits,
        CancellationToken cancellationToken);

    /// <summary>Advances the sweep cursor and counts one execution this contract version cannot express.</summary>
    ValueTask RecordProjectionRejectedAsync(
        string root,
        ExecutionEvidenceDiscoveryCursor cursor,
        Guid executionId,
        string reasonCode,
        CancellationToken cancellationToken);

    /// <summary>Advances the sweep cursor without enlisting anything, recording a bounded source-pruned event.</summary>
    ValueTask RecordSourcePrunedAsync(
        string root,
        ExecutionEvidenceDiscoveryCursor cursor,
        CancellationToken cancellationToken);

    /// <summary>Origin identities that still hold non-terminal units, oldest first.</summary>
    ValueTask<IReadOnlyList<ExecutionEvidenceOriginRecord>> ReadOriginsWithWorkAsync(
        string root,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads the next eligible units for one origin in ascending sequence order, bounded by count and bytes. Ordering
    /// is what makes a graph revision reach the receiver before or with the executions that depend on it.
    /// </summary>
    ValueTask<IReadOnlyList<ExecutionEvidenceUnit>> ReadPendingAsync(
        string root,
        string originIdentitySha256,
        int maximumUnits,
        long maximumBytes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    /// <summary>Reads specific sequences for a bounded resynchronization request.</summary>
    ValueTask<IReadOnlyList<ExecutionEvidenceUnit>> ReadRangeAsync(
        string root,
        string originIdentitySha256,
        IReadOnlyList<ExecutionEvidenceSequenceRangeV1> ranges,
        int maximumUnits,
        long maximumBytes,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles one unit as terminally acknowledged. The stored payload hash must equal the acknowledged hash, so an
    /// acknowledgement that names different bytes never releases the local retention of the unit it did not accept.
    /// </summary>
    ValueTask AcknowledgeAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        string payloadSha256,
        DateTimeOffset acknowledgedUtc,
        CancellationToken cancellationToken);

    /// <summary>Defers one unit until <paramref name="nextAttemptUtc"/> and records the bounded reason.</summary>
    ValueTask RetryAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        DateTimeOffset nextAttemptUtc,
        string reasonCode,
        CancellationToken cancellationToken);

    /// <summary>Moves one unit to quarantine. Quarantined units are retained and never silently dropped.</summary>
    ValueTask QuarantineAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        string reasonCode,
        CancellationToken cancellationToken);

    /// <summary>Records the receiver's contiguous acknowledged prefix for one origin.</summary>
    ValueTask RecordAcknowledgedThroughAsync(
        string root,
        string originIdentitySha256,
        long acknowledgedThroughSequence,
        CancellationToken cancellationToken);

    /// <summary>Records one receiver sequence conflict for operator review, bounded to the newest entries.</summary>
    ValueTask RecordConflictAsync(
        string root,
        string originIdentitySha256,
        long originSequence,
        string localPayloadSha256,
        string? receiverPayloadSha256,
        string reasonCode,
        CancellationToken cancellationToken);

    /// <summary>Bounded backlog facts across every origin in this store.</summary>
    ValueTask<ExecutionEvidenceBacklog> ReadBacklogAsync(string root, CancellationToken cancellationToken);

    /// <summary>
    /// Prunes acknowledged units older than the retention window and beyond the retained count, and removes origins
    /// with no remaining unit other than <paramref name="retainedOriginIdentitySha256"/>, which is the live boot
    /// session and must survive an empty backlog. Pending, retry, quarantined, and abandoned units are never pruned.
    /// </summary>
    ValueTask<int> RetainAsync(
        string root,
        TimeSpan acknowledgementRetention,
        int maximumRetainedAcknowledgements,
        string? retainedOriginIdentitySha256,
        CancellationToken cancellationToken);

    ValueTask<ExecutionEvidenceOutboxOperationsPage> ReadOperationsPageAsync(
        string root,
        int pageSize,
        ExecutionEvidenceOutboxOperationsCursor? cursor,
        CancellationToken cancellationToken);

    ValueTask<ExecutionEvidenceOutboxOperationsRecord?> ReadOperationsDetailAsync(
        string root,
        long recordId,
        CancellationToken cancellationToken);

    ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
        string root,
        long recordId,
        int pageSize,
        OutboxOperationsAuditCursor? cursor,
        CancellationToken cancellationToken);

    /// <summary>
    /// Applies an owner-approved replay or abandon to one quarantined unit, keyed by an operation key so a repeated
    /// request is a detected duplicate rather than a second disposition.
    /// </summary>
    ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
        string root,
        long recordId,
        OutboxOperationAction action,
        string operationKey,
        string actorKind,
        string reasonCode,
        CancellationToken cancellationToken);
}

/// <summary>Bounded limits the store enforces at the enlistment boundary. Overflow refuses; it never drops.</summary>
public sealed record ExecutionEvidenceEnlistmentLimits(
    long MaximumPendingUnits,
    long MaximumPendingBytes,
    long MaximumStorageBytes,
    int MaximumUnitBytes);

/// <summary>
/// The read-only view of the delivered durable processing store that the exporter sweeps. It exists so the export
/// lane depends on immutable reads rather than on the operations coordinator's full surface, which keeps the lane
/// unable to mutate anything local correctness depends on and keeps its behaviour testable in isolation.
/// </summary>
internal interface IExecutionEvidenceSource
{
    ValueTask<IReadOnlyList<Capture.Processing.ProcessingGraphTerminalExecution>> ReadTerminalExecutionsAsync(
        long afterTerminalUnixMs,
        string afterExecutionId,
        int maximumCount,
        CancellationToken cancellationToken);

    ValueTask<long?> ReadOldestTerminalExecutionKeyAsync(CancellationToken cancellationToken);

    ValueTask<Capture.Processing.ProcessingGraphExecutionDetail?> ReadExecutionDetailAsync(
        Guid executionId,
        CancellationToken cancellationToken);

    ValueTask<Capture.Processing.ProcessingGraphRevisionSnapshot> ReadRevisionSnapshotAsync(
        string revisionId,
        CancellationToken cancellationToken);

    ValueTask<ExecutionEvidenceAssignmentProvenanceV1?> ReadAssignmentProvenanceAsync(
        string revisionId,
        CancellationToken cancellationToken);
}
