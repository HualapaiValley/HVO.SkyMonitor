namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

/// <summary>The exact standard-lane quarantine an acknowledgement may release.</summary>
public sealed record ProcessingLaneQuarantineTarget(
    long WorkId,
    Guid CaptureId,
    string ManifestSha256,
    string FailureReason,
    long UpdatedUnixMs);

public sealed record ProcessingLaneQuarantineAudit(
    string Actor,
    string Reason,
    DateTimeOffset OccurredUtc);

public sealed record ProcessingLaneQuarantineItem(
    ProcessingLaneQuarantineTarget Target,
    string AgentId,
    long CaptureSequence,
    Guid ArtifactId,
    long PayloadBytes,
    int AttemptCount,
    DateTimeOffset CreatedUtc,
    bool Acknowledged,
    ProcessingLaneQuarantineAudit? LatestAudit);

public sealed record ProcessingLaneQuarantinePage(
    IReadOnlyList<ProcessingLaneQuarantineItem> Items,
    long? NextCursor);

public sealed record ProcessingLaneQuarantineReceipt(bool Duplicate, ProcessingLaneQuarantineAudit Audit);

/// <summary>
/// Audited release of a standard-lane quarantine whose raw capture is still committed. Evidence-invalid quarantines
/// stay on the raw-evidence release path.
/// </summary>
public interface IProcessingLaneQuarantineOperations
{
    public const string ReasonCode = "processing-failure-acknowledged";

    ValueTask<ProcessingLaneQuarantinePage> ReadPageAsync(
        int pageSize, long? beforeWorkId, CancellationToken cancellationToken);

    ValueTask<ProcessingLaneQuarantineReceipt> AcknowledgeAsync(
        ProcessingLaneQuarantineTarget target, string idempotencyKey, string actor, string reason,
        CancellationToken cancellationToken);
}
