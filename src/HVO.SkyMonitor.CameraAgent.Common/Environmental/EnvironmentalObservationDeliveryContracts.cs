using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Operations;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed record EnvironmentalObservationResolvedTarget(
    Guid ObservatoryId,
    Guid DevicePublicId,
    string? RigId = null);

public interface IEnvironmentalObservationTargetResolver
{
    ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken);
}

internal sealed class NullEnvironmentalObservationTargetResolver : IEnvironmentalObservationTargetResolver
{
    public ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken)
        => ValueTask.FromResult<EnvironmentalObservationResolvedTarget?>(null);
}

public enum EnvironmentalObservationPublishDisposition
{
    Disabled,
    Enqueued,
    Duplicate
}

public sealed record EnvironmentalObservationPublishResult(
    EnvironmentalObservationPublishDisposition Disposition,
    EnvironmentalObservationV1? Observation);

public enum LocalEnvironmentalObservationCommitDisposition
{
    Committed,
    Duplicate
}

public sealed record LocalEnvironmentalObservationRecord(
    long RecordId,
    EnvironmentalObservationFactV1 Fact,
    string SourceIdentitySha256,
    string SourceContentSha256,
    string ContentSha256,
    int PayloadBytes,
    DateTimeOffset RecordedUtc);

public enum EnvironmentalObservationProjectionDisposition
{
    NotRequested,
    Waiting,
    Staged,
    Acknowledged
}

public sealed record LocalEnvironmentalObservationCommitResult(
    LocalEnvironmentalObservationCommitDisposition Disposition,
    LocalEnvironmentalObservationRecord Record,
    EnvironmentalObservationProjectionDisposition ProjectionDisposition,
    EnvironmentalObservationV1? DeliveryObservation,
    EnvironmentalObservationEnqueueDisposition? DeliveryDisposition = null);

public sealed record LocalEnvironmentalObservationSnapshot(
    long StoredCount,
    long StoredBytes,
    long OverflowCount,
    DateTimeOffset? OldestRecordedUtc,
    DateTimeOffset EvaluatedUtc);

public sealed record LocalEnvironmentalObservationCursor(
    DateTimeOffset ObservedAtUtc,
    long RecordId);

public sealed record LocalEnvironmentalObservationPage(
    IReadOnlyList<LocalEnvironmentalObservationRecord> Items,
    LocalEnvironmentalObservationCursor? NextCursor);

public interface ILocalEnvironmentalObservationStore
{
    ValueTask<LocalEnvironmentalObservationCommitResult> CommitLocalAsync(
        string root,
        EnvironmentalObservationFactV1 fact,
        EnvironmentalObservationResolvedTarget? deliveryTarget,
        CancellationToken cancellationToken);

    ValueTask<LocalEnvironmentalObservationCommitResult> CommitLocalAsync(
        string root,
        EnvironmentalObservationFactV1 fact,
        CancellationToken cancellationToken)
        => CommitLocalAsync(root, fact, null, cancellationToken);

    ValueTask<LocalEnvironmentalObservationSnapshot> GetLocalSnapshotAsync(
        string root,
        CancellationToken cancellationToken);

    ValueTask<LocalEnvironmentalObservationPage> ReadLocalPageAsync(
        string root,
        EnvironmentalObservationKind? kind,
        int pageSize,
        LocalEnvironmentalObservationCursor? cursor,
        CancellationToken cancellationToken);

    ValueTask<LocalEnvironmentalObservationRecord?> ReadLocalDetailAsync(
        string root,
        long recordId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<LocalEnvironmentalObservationRecord>> ReadLocalCandidatesAsync(
        string root,
        EnvironmentalObservationKind kind,
        string? rigId,
        DateTimeOffset intervalFromUtc,
        DateTimeOffset intervalThroughUtc,
        int maximumResults,
        CancellationToken cancellationToken);
}

public sealed record LocalEnvironmentalRetentionResult(
    int RemovedCount,
    long RemovedBytes,
    long RemainingCount,
    long RemainingBytes);

public interface ILocalEnvironmentalRetentionStore
{
    ValueTask<LocalEnvironmentalRetentionResult> RetainLocalAsync(
        string root,
        DateTimeOffset recordedBeforeUtc,
        int maximumResults,
        CancellationToken cancellationToken);
}

public interface IEnvironmentalObservationPublisher
{
    ValueTask<EnvironmentalObservationPublishResult> PublishAsync(
        EnvironmentalObservationFactV1 fact,
        CancellationToken cancellationToken = default);
}

public enum EnvironmentalObservationTransportDisposition
{
    Acknowledged,
    Retry,
    Quarantine,
    Terminal,
    AuthenticationBlocked
}

public sealed record EnvironmentalObservationTransportResult(
    EnvironmentalObservationTransportDisposition Disposition,
    string Reason,
    EnvironmentalObservationAcknowledgement? Acknowledgement = null,
    TimeSpan? RetryAfter = null);

public interface IEnvironmentalObservationTransport
{
    ValueTask<EnvironmentalObservationTransportResult> SendAsync(
        EnvironmentalObservationV1 observation,
        CancellationToken cancellationToken);
}

public sealed record EnvironmentalObservationOutboxRecord(
    long RecordId,
    EnvironmentalObservationV1 Observation,
    string SourceIdentitySha256,
    string ContentSha256,
    int PayloadBytes,
    int AttemptCount,
    long? LocalRecordId = null);

public interface IEnvironmentalObservationProjectionStore
{
    ValueTask<int> AssignUnprojectedAsync(
        string root,
        EnvironmentalObservationResolvedTarget target,
        int maximumResults,
        CancellationToken cancellationToken);

    ValueTask<int> ProjectWaitingAsync(
        string root,
        int maximumResults,
        CancellationToken cancellationToken);
}

public sealed record EnvironmentalObservationOutboxLease(
    EnvironmentalObservationOutboxRecord Record,
    string Owner,
    string Token,
    DateTimeOffset ExpiresUtc);

public sealed record EnvironmentalObservationOutboxSnapshot(
    long StoredCount,
    long StoredBytes,
    long PendingCount,
    long PendingBytes,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    long TerminalCount,
    long OverflowCount,
    DateTimeOffset? OldestPendingUtc,
    DateTimeOffset EvaluatedUtc);

public enum EnvironmentalObservationDeadLetterStatus
{
    Quarantined,
    Terminal
}

public sealed record EnvironmentalObservationDeadLetter(
    long RecordId,
    EnvironmentalObservationDeadLetterStatus Status,
    string Reason,
    int PayloadBytes,
    int AttemptCount,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public enum EnvironmentalObservationEnqueueDisposition
{
    Enqueued,
    Duplicate
}

public interface IEnvironmentalObservationOutbox
{
    ValueTask<EnvironmentalObservationEnqueueDisposition> EnqueueAsync(
        string root,
        EnvironmentalObservationV1 observation,
        CancellationToken cancellationToken);

    ValueTask<EnvironmentalObservationOutboxLease?> ClaimAsync(
        string root,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask AcknowledgeAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        EnvironmentalObservationAcknowledgement acknowledgement,
        CancellationToken cancellationToken);

    ValueTask RetryAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        DateTimeOffset retryAtUtc,
        string reason,
        CancellationToken cancellationToken);

    ValueTask QuarantineAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string reason,
        CancellationToken cancellationToken);

    ValueTask TerminalAsync(
        string root,
        EnvironmentalObservationOutboxLease lease,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<EnvironmentalObservationOutboxSnapshot> GetSnapshotAsync(
        string root,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<EnvironmentalObservationDeadLetter>> ReadDeadLettersAsync(
        string root,
        int maximumResults,
        CancellationToken cancellationToken);

    ValueTask ReplayAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        CancellationToken cancellationToken);

    ValueTask AbandonAsync(
        string root,
        long recordId,
        string actor,
        string reason,
        CancellationToken cancellationToken);

    ValueTask<EnvironmentalOutboxOperationsPage> ReadOperationsPageAsync(
        string root,
        int pageSize,
        EnvironmentalOutboxOperationsCursor? cursor,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This outbox does not expose operational records.");

    ValueTask<EnvironmentalOutboxOperationsRecord?> ReadOperationsDetailAsync(
        string root,
        long recordId,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This outbox does not expose operational records.");

    ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
        string root,
        long recordId,
        int pageSize,
        OutboxOperationsAuditCursor? cursor,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This outbox does not expose operational audit records.");

    ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
        string root,
        long recordId,
        OutboxOperationAction action,
        string operationKey,
        string actorKind,
        string reasonCode,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This outbox does not support operational resolution.");
}

public interface IEnvironmentalObservationOutboxFaultInjector
{
    ValueTask BeforeEnqueueCommitAsync(CancellationToken cancellationToken);
    ValueTask AfterEnqueueCommitAsync(CancellationToken cancellationToken);
}

public sealed class NullEnvironmentalObservationOutboxFaultInjector : IEnvironmentalObservationOutboxFaultInjector
{
    public static NullEnvironmentalObservationOutboxFaultInjector Instance { get; } = new();

    public ValueTask BeforeEnqueueCommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    public ValueTask AfterEnqueueCommitAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

public sealed class EnvironmentalObservationOutboxCapacityException : InvalidOperationException
{
    public EnvironmentalObservationOutboxCapacityException()
    {
    }

    public EnvironmentalObservationOutboxCapacityException(string message) : base(message)
    {
    }

    public EnvironmentalObservationOutboxCapacityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class LocalEnvironmentalObservationCapacityException : InvalidOperationException
{
    public LocalEnvironmentalObservationCapacityException()
    {
    }

    public LocalEnvironmentalObservationCapacityException(string message) : base(message)
    {
    }

    public LocalEnvironmentalObservationCapacityException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class EnvironmentalObservationIdentityConflictException : InvalidOperationException
{
    public EnvironmentalObservationIdentityConflictException()
    {
    }

    public EnvironmentalObservationIdentityConflictException(string message) : base(message)
    {
    }

    public EnvironmentalObservationIdentityConflictException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

public sealed class EnvironmentalObservationLeaseLostException : InvalidOperationException
{
    public EnvironmentalObservationLeaseLostException()
    {
    }

    public EnvironmentalObservationLeaseLostException(string message) : base(message)
    {
    }

    public EnvironmentalObservationLeaseLostException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
