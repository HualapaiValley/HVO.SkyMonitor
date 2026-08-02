namespace HVO.SkyMonitor.LogicHost.Data;

internal enum CentralTransientPayloadReleaseState
{
    Pending,
    Completed,
    Failed
}

internal enum CentralTransientPayloadReleaseItemKind
{
    SourceArtifact,
    Derivative
}

internal enum CentralTransientPayloadReleaseItemOutcome
{
    Pending,
    Released,
    PreservedHeld,
    Failed
}

internal sealed class CentralTransientPayloadRelease
{
    public Guid ReleaseId { get; init; } = Guid.NewGuid();
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public string ActorIdentity { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string CanonicalRequestSha256 { get; set; } = string.Empty;
    public CentralTransientPayloadReleaseState State { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? CompletedUtc { get; set; }
    public string? ReasonCode { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<CentralTransientPayloadReleaseItem> Items { get; } = [];
}

internal sealed class CentralTransientPayloadReleaseItem
{
    public Guid ReleaseId { get; set; }
    public CentralTransientPayloadRelease? Release { get; set; }
    public int Ordinal { get; set; }
    public CentralTransientPayloadReleaseItemKind Kind { get; set; }
    public Guid RecordId { get; set; }
    public CentralTransientPayloadReleaseItemOutcome Outcome { get; set; }
    public DateTimeOffset? ReleasedUtc { get; set; }
    public Guid? ReservationToken { get; set; }
    public DateTimeOffset? RequestedAtUtc { get; set; }
    public string? StorageReference { get; set; }
    public byte[]? TargetRowVersion { get; set; }
    public long? TargetGeneration { get; set; }
    public int RetryCount { get; set; }
    public DateTimeOffset? RetryAtUtc { get; set; }
    public string? FailureReasonCode { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
