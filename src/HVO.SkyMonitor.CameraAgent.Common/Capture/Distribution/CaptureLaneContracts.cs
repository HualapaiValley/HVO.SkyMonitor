using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;

internal enum CaptureLaneWorkState
{
    Pending,
    Leased,
    RetryWait,
    Completed,
    Quarantined,
    Abandoned
}

public enum CaptureLaneHandlerOutcome
{
    Completed,
    Deferred,
    RetryableFailure,
    TerminalFailure
}

internal sealed record CaptureLaneDefinition(
    string Name,
    bool Enabled,
    bool Required,
    bool Ordered,
    string PolicySha256);

internal sealed record CaptureLaneEnvelope(
    CameraModuleConfig Configuration,
    CaptureLoopSubmission Submission);

public sealed record CaptureLaneHandlerContext(
    string Lane,
    int Attempt,
    CameraModuleConfig Configuration,
    CaptureLoopSubmission Submission,
    RawCaptureReceipt RawCapture,
    long WorkId = 0,
    string? LeaseToken = null,
    ProcessingExecutionContext? Execution = null);

public readonly record struct CaptureLaneHandlerResult(
    CaptureLaneHandlerOutcome Outcome,
    string Reason)
{
    public static CaptureLaneHandlerResult Success { get; } = new(CaptureLaneHandlerOutcome.Completed, "completed");

    public static CaptureLaneHandlerResult Retry(string reason)
        => new(CaptureLaneHandlerOutcome.RetryableFailure, reason);

    public static CaptureLaneHandlerResult Wait(string reason)
        => new(CaptureLaneHandlerOutcome.Deferred, reason);

    public static CaptureLaneHandlerResult Terminal(string reason)
        => new(CaptureLaneHandlerOutcome.TerminalFailure, reason);
}

internal sealed record CaptureLaneLease(
    long WorkId,
    string Lane,
    bool Required,
    bool Ordered,
    int Attempt,
    string LeaseToken,
    string LeaseOwner,
    DateTimeOffset LeaseExpiresUtc,
    CaptureLaneHandlerContext Context);

internal sealed record CaptureLanePendingCapture(string AgentId, long CaptureSequence);

internal sealed record CaptureLaneBacklog(
    string Lane,
    bool Required,
    long PendingCount,
    long PendingBytes,
    DateTimeOffset? OldestPendingUtc,
    long LeasedCount,
    long RetryCount,
    long QuarantineCount,
    int PressureLevel = 0,
    IReadOnlyList<CaptureLanePendingCapture>? PendingCaptures = null);

public interface IOperationsQueueSnapshotRefresher
{
    ValueTask RefreshOperationsQueueSnapshotsAsync(CancellationToken cancellationToken);
}

public interface ICaptureLaneHandler
{
    string Lane { get; }

    ValueTask<CaptureLaneHandlerResult> HandleAsync(
        CaptureLaneHandlerContext context,
        CancellationToken cancellationToken);
}

public interface ICaptureDistributor
{
    void NotifyCommittedCapture();

    ValueTask ProcessEphemeralAsync(
        CameraModuleConfig configuration,
        CaptureLoopSubmission submission,
        CancellationToken cancellationToken);
}

internal interface ICaptureLaneStore
{
    ValueTask InitializeLanesAsync(CancellationToken cancellationToken);

    ValueTask EnsureCanAcceptAsync(long payloadLength, CancellationToken cancellationToken);

    ValueTask<CaptureLaneLease?> ClaimAsync(
        CaptureLaneDefinition lane,
        string owner,
        CameraModuleConfig fallbackConfiguration,
        CancellationToken cancellationToken);

    ValueTask<bool> RenewAsync(CaptureLaneLease lease, CancellationToken cancellationToken);

    ValueTask CompleteAsync(CaptureLaneLease lease, CancellationToken cancellationToken);

    ValueTask<CaptureLaneHandlerOutcome> FailAsync(
        CaptureLaneLease lease,
        CaptureLaneHandlerResult result,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(CaptureLaneLease lease, CancellationToken cancellationToken);

    ValueTask<IReadOnlyList<CaptureLaneBacklog>> ReadBacklogsAsync(CancellationToken cancellationToken);
}

internal static class CaptureLanePressureMath
{
    internal static bool IsAtOrAbovePercentage(long value, long maximum, int percentage)
        => (decimal)value * 100 >= (decimal)maximum * percentage;

    internal static bool ExceedsAfterAdding(long current, long addition, long maximum)
        => current > maximum || addition > maximum - current;
}
