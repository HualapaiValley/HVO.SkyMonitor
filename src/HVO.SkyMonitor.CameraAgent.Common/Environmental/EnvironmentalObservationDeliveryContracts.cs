using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public sealed record EnvironmentalObservationResolvedTarget(
    Guid ObservatoryId,
    Guid DevicePublicId,
    string? RigId = null);

public interface IEnvironmentalObservationTargetResolver
{
    ValueTask<EnvironmentalObservationResolvedTarget?> ResolveAsync(CancellationToken cancellationToken);
}

public enum EnvironmentalObservationPublishDisposition
{
    Enqueued,
    Duplicate
}

public sealed record EnvironmentalObservationPublishResult(
    EnvironmentalObservationPublishDisposition Disposition,
    EnvironmentalObservationV1 Observation);

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
    int AttemptCount);

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
