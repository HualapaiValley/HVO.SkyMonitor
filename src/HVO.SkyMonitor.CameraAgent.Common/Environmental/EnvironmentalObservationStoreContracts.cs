using HVO.SkyMonitor.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Operations;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public enum EnvironmentalObservationPublishDisposition
{
    Committed,
    Duplicate
}

public sealed record EnvironmentalObservationPublishResult(
    EnvironmentalObservationPublishDisposition Disposition);

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

public sealed record LocalEnvironmentalObservationCommitResult(
    LocalEnvironmentalObservationCommitDisposition Disposition,
    LocalEnvironmentalObservationRecord Record)
{
    internal EnvironmentalAcquisitionReceipt? HeldPollReceipt { get; init; }
}

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
        CancellationToken cancellationToken);

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

    /// <summary>Filters primary-source eligibility before selecting the newest retained reading.</summary>
    ValueTask<LocalEnvironmentalObservationRecord?> ReadLocalLatestPrimaryAsync(
        string root,
        EnvironmentalObservationKind kind,
        IReadOnlyList<string> configuredSourceIds,
        IReadOnlyList<string> unavailableSourceIds,
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
    long RemainingBytes)
{
    /// <summary>At least one history or observation batch filled; another transaction may have more work.</summary>
    public bool BatchLimitReached { get; init; }
}

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

internal readonly record struct EnvironmentalHeldPoll(
    EnvironmentalSourceDescriptor Source, DateTimeOffset DueUtc, DateTimeOffset StartedUtc);

internal interface IEnvironmentalHeldPollPublisher
{
    bool SupportsHeldPollSettlement { get; }

    // One transaction settles the held slot, its acquisition attempt, and the observation.
    ValueTask<EnvironmentalAcquisitionReceipt> PublishHeldPollAsync(
        EnvironmentalObservationFactV1 fact, EnvironmentalHeldPoll poll, CancellationToken cancellationToken);
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
