using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Durably queues stored artifact manifests until central ingestion acknowledges them.</summary>
public interface IArtifactOutbox
{
    ValueTask InitializeAsync(string root, CancellationToken cancellationToken);
    ValueTask EnqueueAsync(string root, ArtifactManifestV2 manifest, CancellationToken cancellationToken);
    ValueTask EnqueueAsync(string root, StructuredProcessingProductManifestV1 manifest, CancellationToken cancellationToken);
    ValueTask<ArtifactOutboxLease?> ClaimAsync(
        string root,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    ValueTask RenewAsync(
        string root,
        ArtifactOutboxLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);
    ValueTask RetryAsync(
        string root,
        ArtifactOutboxLease lease,
        DateTimeOffset nextAttemptUtc,
        string reason,
        CancellationToken cancellationToken);
    ValueTask AcknowledgeAsync(
        string root,
        ArtifactOutboxLease lease,
        ArtifactUploadAcknowledgement acknowledgement,
        CancellationToken cancellationToken);
    ValueTask QuarantineAsync(
        string root,
        ArtifactOutboxLease lease,
        string reason,
        CancellationToken cancellationToken);
    ValueTask ReplayAsync(
        string root,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken);
    ValueTask AbandonAsync(
        string root,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken);
    ValueTask<ArtifactOutboxRecord?> ReadAsync(
        string root,
        string idempotencyKey,
        CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ArtifactOutboxAuditEntry>> ReadAuditAsync(
        string root,
        string idempotencyKey,
        CancellationToken cancellationToken);
    ValueTask<ArtifactOutboxOperationsPage> ReadOperationsPageAsync(
        string root,
        int pageSize,
        ArtifactOutboxOperationsCursor? cursor,
        CancellationToken cancellationToken);
    ValueTask<ArtifactOutboxOperationsRecord?> ReadOperationsDetailAsync(
        string root,
        string recordKey,
        CancellationToken cancellationToken);
    ValueTask<OutboxOperationsAuditPage> ReadOperationsAuditAsync(
        string root,
        string recordKey,
        int pageSize,
        OutboxOperationsAuditCursor? cursor,
        CancellationToken cancellationToken);
    ValueTask<OutboxOperationDisposition> ResolveOperationsAsync(
        string root,
        string recordKey,
        OutboxOperationAction action,
        string operationKey,
        string actorKind,
        string reasonCode,
        CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ArtifactOutboxRetentionHold>> GetRetentionHoldsAsync(
        string root,
        CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<Guid>> GetAcknowledgedArtifactIdsAsync(
        string root,
        IReadOnlySet<Guid> artifactIds,
        CancellationToken cancellationToken);
    ValueTask<ArtifactOutboxSnapshot> GetSnapshotAsync(string root, CancellationToken cancellationToken);
}
