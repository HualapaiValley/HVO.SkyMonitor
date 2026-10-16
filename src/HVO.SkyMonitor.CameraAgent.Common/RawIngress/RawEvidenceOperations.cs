namespace HVO.SkyMonitor.CameraAgent.Common.RawIngress;

public enum RawEvidenceReleaseAction
{
    Revalidate,
    WriteOff
}

public sealed record RawEvidenceTarget(Guid CaptureId, string ManifestSha256, string State, string FailureReason);

public sealed record RawEvidenceAudit(
    RawEvidenceReleaseAction Action, string Actor, string Reason, DateTimeOffset OccurredUtc);

public sealed record RawEvidenceItem(
    RawEvidenceTarget Target, string AgentId, long CaptureSequence, Guid ArtifactId,
    long PayloadBytes, DateTimeOffset CreatedUtc, bool WrittenOff, RawEvidenceAudit? LatestAudit);

public sealed record RawEvidencePage(IReadOnlyList<RawEvidenceItem> Items, long? NextCursor);

public sealed record RawEvidenceReleaseReceipt(bool Duplicate, RawEvidenceAudit Audit);

/// <summary>Audited release of committed captures whose raw evidence failed reconciliation.</summary>
public interface IRawEvidenceOperations
{
    ValueTask<RawEvidencePage> ReadEvidencePageAsync(int pageSize, long? beforeRowId, CancellationToken cancellationToken);

    ValueTask<RawEvidenceReleaseReceipt> ReleaseEvidenceAsync(
        RawEvidenceTarget target, RawEvidenceReleaseAction action, string idempotencyKey,
        string actor, string reason, CancellationToken cancellationToken);
}
