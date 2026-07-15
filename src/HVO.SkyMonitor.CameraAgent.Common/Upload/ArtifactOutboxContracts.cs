using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.CameraAgent.Common.Upload;

public enum ArtifactOutboxStatus
{
    Pending,
    Leased,
    Retry,
    Acknowledged,
    Quarantined,
    Abandoned
}

public enum ArtifactOutboxManifestKind
{
    ManifestV2,
    LegacyV1,
    MalformedLegacy
}

public enum ArtifactUploadDisposition
{
    Acknowledged,
    Retry,
    Quarantine
}

public sealed record ArtifactUploadResult(
    ArtifactUploadDisposition Disposition,
    string Reason,
    TimeSpan? RetryAfter = null,
    ArtifactUploadAcknowledgement? Acknowledgement = null);

public sealed record ArtifactOutboxRecord(
    string IdempotencyKey,
    ArtifactOutboxManifestKind ManifestKind,
    ReadOnlyMemory<byte> ManifestBytes,
    ArtifactManifestDocument? Manifest,
    Guid? ArtifactId,
    FrameArtifactRole? Role,
    string? RelativeArtifactPath,
    string? PayloadSha256,
    long? PayloadLength,
    string? MediaType,
    ArtifactOutboxStatus Status,
    int AttemptCount,
    DateTimeOffset NextAttemptUtc,
    DateTimeOffset CreatedUtc,
    string? LeaseOwner,
    string? LeaseToken,
    DateTimeOffset? LeaseExpiresUtc,
    string? LastReason,
    ReadOnlyMemory<byte>? Acknowledgement,
    string? LegacyEvidencePath);

public sealed record ArtifactOutboxLease(
    ArtifactOutboxRecord Record,
    string Owner,
    string Token,
    DateTimeOffset ExpiresUtc);

public sealed record ArtifactOutboxRetentionHold(
    Guid ArtifactId,
    string RelativeArtifactPath,
    ArtifactOutboxStatus Status);

public sealed record ArtifactOutboxSnapshot(
    long HeldCount,
    long HeldBytes,
    DateTimeOffset? OldestHeldUtc,
    long PendingCount,
    long LeasedCount,
    long RetryCount,
    long AcknowledgedCount,
    long QuarantinedCount,
    long AbandonedCount);

public sealed record ArtifactOutboxAuditEntry(
    long Sequence,
    string Action,
    string Actor,
    string Reason,
    DateTimeOffset OccurredUtc);

[Serializable]
public sealed class ArtifactOutboxConflictException : InvalidOperationException
{
    public ArtifactOutboxConflictException()
    {
    }

    public ArtifactOutboxConflictException(string message)
        : base(message)
    {
    }

    public ArtifactOutboxConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[Serializable]
public sealed class ArtifactOutboxLeaseLostException : InvalidOperationException
{
    public ArtifactOutboxLeaseLostException()
    {
    }

    public ArtifactOutboxLeaseLostException(string message)
        : base(message)
    {
    }

    public ArtifactOutboxLeaseLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
