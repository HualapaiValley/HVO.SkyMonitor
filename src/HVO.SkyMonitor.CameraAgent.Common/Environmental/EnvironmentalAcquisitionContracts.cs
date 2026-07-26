using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Environmental;

public enum EnvironmentalAcquisitionTrigger
{
    Periodic,
    BeforeCapture,
    AfterCapture,
    EveryNthCapture,
    RegimeChange,
    OnDemand
}

public enum EnvironmentalSourceAcquisitionOutcome
{
    Produced,
    Missing,
    Failed
}

public sealed record EnvironmentalSourceDescriptor(
    string Id,
    string Type,
    EnvironmentalObservationKind Kind,
    bool Required,
    IReadOnlyList<EnvironmentalAcquisitionTrigger> Triggers,
    DateTimeOffset ScheduleEpochUtc,
    int PeriodSeconds,
    int EveryNthCapture,
    int ValidForSeconds,
    int StaleAfterSeconds,
    string? RigId,
    JsonElement Options);

public sealed record EnvironmentalSourceAcquisitionContext(
    EnvironmentalAcquisitionTrigger Trigger,
    DateTimeOffset ObservedAtUtc,
    DeploymentLocationSnapshot DeploymentLocation,
    long? CaptureSequence = null,
    Guid? CaptureId = null);

public sealed record EnvironmentalSourceAcquisitionResult(
    EnvironmentalSourceAcquisitionOutcome Outcome,
    string Reason,
    EnvironmentalObservationFactV1? Fact = null);

public interface IEnvironmentalSource
{
    EnvironmentalSourceDescriptor Descriptor { get; }

    ValueTask<EnvironmentalSourceAcquisitionResult> AcquireAsync(
        EnvironmentalSourceAcquisitionContext context,
        CancellationToken cancellationToken);
}

public sealed record EnvironmentalSourceRegistration(
    string Alias,
    Type SourceType,
    Type OptionsType);

public enum LocalEnvironmentalAssociationStatus
{
    Fresh,
    Stale,
    Missing,
    Contradictory
}

public sealed record LocalEnvironmentalCaptureAssociation(
    Guid CaptureId,
    long CaptureSequence,
    EnvironmentalObservationKind Kind,
    string? RigId,
    DateTimeOffset ExposureFromUtc,
    DateTimeOffset ExposureThroughUtc,
    string PolicyIdentitySha256,
    LocalEnvironmentalAssociationStatus Status,
    long? SelectedRecordId,
    IReadOnlyList<long> ConflictingRecordIds,
    string AssociationIdentitySha256,
    DateTimeOffset CreatedUtc);

public interface ILocalEnvironmentalAssociationStore
{
    ValueTask SaveAssociationAsync(
        string root,
        LocalEnvironmentalCaptureAssociation association,
        CancellationToken cancellationToken);

    ValueTask SaveAssociationSetAsync(
        string root,
        IReadOnlyList<LocalEnvironmentalCaptureAssociation> associations,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<LocalEnvironmentalCaptureAssociation>> ReadAssociationsAsync(
        string root,
        Guid captureId,
        CancellationToken cancellationToken);
}

public sealed record EnvironmentalSourceRuntimeState(
    string SourceId,
    EnvironmentalObservationKind Kind,
    bool Required,
    EnvironmentalAcquisitionDisposition? LastDisposition,
    string? LastReason,
    Guid? LastObservationId,
    DateTimeOffset? LastObservedUtc,
    DateTimeOffset? LastStaleAfterUtc,
    DateTimeOffset? LastStartedUtc,
    DateTimeOffset? LastCompletedUtc,
    int ConsecutiveFailures,
    DateTimeOffset? NextPollUtc);

public sealed record EnvironmentalAcquisitionAttemptRecord(
    long AttemptId,
    string SourceId,
    EnvironmentalObservationKind Kind,
    bool Required,
    EnvironmentalAcquisitionTrigger Trigger,
    EnvironmentalAcquisitionDisposition Disposition,
    string Reason,
    Guid? ObservationId,
    long? CaptureSequence,
    Guid? CaptureId,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc);

public interface IEnvironmentalAcquisitionStateStore
{
    ValueTask<bool> RecordCaptureRegimeAsync(
        string root,
        long captureSequence,
        Guid captureId,
        CaptureSolarRegime regime,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);

    ValueTask UpdateSourceScheduleAsync(
        string root,
        EnvironmentalSourceDescriptor source,
        DateTimeOffset nextPollUtc,
        CancellationToken cancellationToken);

    ValueTask RecordAttemptAsync(
        string root,
        EnvironmentalSourceDescriptor source,
        EnvironmentalAcquisitionReceipt receipt,
        long? captureSequence,
        Guid? captureId,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<EnvironmentalSourceRuntimeState>> ReadSourceStatesAsync(
        string root,
        CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<EnvironmentalAcquisitionAttemptRecord>> ReadAttemptsAsync(
        string root,
        int maximumResults,
        CancellationToken cancellationToken);
}

public enum EnvironmentalOnDemandClaimDisposition
{
    Claimed,
    Completed,
    Busy
}

public sealed record EnvironmentalOnDemandCommandClaim(
    EnvironmentalOnDemandClaimDisposition Disposition,
    string LeaseToken,
    DateTimeOffset ObservedAtUtc,
    EnvironmentalAcquisitionReceipt? Receipt);

public interface IEnvironmentalOnDemandCommandStore
{
    ValueTask<EnvironmentalOnDemandCommandClaim> ClaimOnDemandAsync(
        string root,
        string idempotencyKey,
        string payloadSha256,
        string sourceId,
        string actorId,
        string? reason,
        DateTimeOffset observedAtUtc,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    ValueTask CompleteOnDemandAsync(
        string root,
        string idempotencyKey,
        string leaseToken,
        EnvironmentalAcquisitionReceipt receipt,
        CancellationToken cancellationToken);

    ValueTask ReleaseOnDemandAsync(
        string root,
        string idempotencyKey,
        string leaseToken,
        CancellationToken cancellationToken);
}

public sealed record EnvironmentalOnDemandAcquisitionResult(
    EnvironmentalAcquisitionReceipt Receipt,
    bool Replayed);

public sealed class EnvironmentalOnDemandCommandConflictException : Exception
{
    public EnvironmentalOnDemandCommandConflictException()
    {
    }

    public EnvironmentalOnDemandCommandConflictException(string message) : base(message)
    {
    }

    public EnvironmentalOnDemandCommandConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class EnvironmentalOnDemandCommandBusyException : Exception
{
    public EnvironmentalOnDemandCommandBusyException()
    {
    }

    public EnvironmentalOnDemandCommandBusyException(string message) : base(message)
    {
    }

    public EnvironmentalOnDemandCommandBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class EnvironmentalOnDemandCommandCapacityException : Exception
{
    public EnvironmentalOnDemandCommandCapacityException()
    {
    }

    public EnvironmentalOnDemandCommandCapacityException(string message) : base(message)
    {
    }

    public EnvironmentalOnDemandCommandCapacityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
