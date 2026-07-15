using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

/// <summary>Durably queues stored artifact manifests until central ingestion acknowledges them.</summary>
public interface IArtifactOutbox
{
    ValueTask InitializeAsync(string root, CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not expose SQLite work state.");
    ValueTask EnqueueAsync(string root, ArtifactManifestV2 manifest, CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support manifest v2.");
    ValueTask EnqueueAsync(string root, ArtifactUploadManifest manifest, CancellationToken cancellationToken);
    ValueTask<ArtifactOutboxLease?> ClaimAsync(
        string root,
        string owner,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support leases.");
    ValueTask RenewAsync(
        string root,
        ArtifactOutboxLease lease,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support lease renewal.");
    ValueTask RetryAsync(
        string root,
        ArtifactOutboxLease lease,
        DateTimeOffset nextAttemptUtc,
        string reason,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support persisted retries.");
    ValueTask AcknowledgeAsync(
        string root,
        ArtifactOutboxLease lease,
        ArtifactUploadAcknowledgement acknowledgement,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support fenced acknowledgement.");
    ValueTask QuarantineAsync(
        string root,
        ArtifactOutboxLease lease,
        string reason,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support quarantine.");
    ValueTask ReplayAsync(
        string root,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support replay.");
    ValueTask AbandonAsync(
        string root,
        string idempotencyKey,
        string actor,
        string reason,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not support abandonment.");
    ValueTask<ArtifactOutboxRecord?> ReadAsync(
        string root,
        string idempotencyKey,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not expose records.");
    ValueTask<IReadOnlyList<ArtifactOutboxAuditEntry>> ReadAuditAsync(
        string root,
        string idempotencyKey,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not expose audit records.");
    ValueTask<IReadOnlyList<ArtifactOutboxRetentionHold>> GetRetentionHoldsAsync(
        string root,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not expose typed retention holds.");
    ValueTask<ArtifactOutboxSnapshot> GetSnapshotAsync(string root, CancellationToken cancellationToken)
        => throw new NotSupportedException("This legacy outbox does not expose snapshots.");
    ValueTask<bool> HasUnknownRetentionHoldsAsync(string root, CancellationToken cancellationToken)
        => ValueTask.FromResult(false);

    // Compatibility surface for the v1 drain and retention service during the manifest-v2 rollout.
    IReadOnlyList<ArtifactUploadManifest> List(
        string root,
        int maximumResults,
        IReadOnlySet<string>? excludedIdempotencyKeys = null);
    IEnumerable<ArtifactUploadManifest> EnumeratePending(string root, CancellationToken cancellationToken);
}
