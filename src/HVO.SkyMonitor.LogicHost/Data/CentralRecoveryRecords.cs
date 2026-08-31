namespace HVO.SkyMonitor.LogicHost.Data;

internal sealed class CentralRecoveryCheckpoint
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public long Generation { get; set; }
    public string Phase { get; set; } = CentralRecoveryPhases.Idle;
    public int ObjectPartition { get; set; }
    public string? ObjectCursor { get; set; }
    public int StagingPartition { get; set; }
    public string? StagingCursor { get; set; }
    public DateTimeOffset NextInventoryAtUtc { get; set; }
    public DateTimeOffset? InventoryStartedAtUtc { get; set; }
    public DateTimeOffset? LastProgressAtUtc { get; set; }
    public DateTimeOffset? LastCompletedAtUtc { get; set; }
    public DateTimeOffset? LastCycleAtUtc { get; set; }
    public DateTimeOffset? LastFailureAtUtc { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public long FindingCount { get; set; }
    public long FindingBytes { get; set; }
}

internal static class CentralRecoveryPhases
{
    public const string Idle = "Idle";
    public const string SqlArtifacts = "SqlArtifacts";
    public const string ObjectStoreArtifacts = "ObjectStoreArtifacts";
    public const string ObjectStoreArtifactsCatchAll = "ObjectStoreArtifactsCatchAll";
    public const string ObjectStoreDerivatives = "ObjectStoreDerivatives";
    public const string ObjectStoreDerivativesCatchAll = "ObjectStoreDerivativesCatchAll";
}

internal static class CentralObjectRecoveryKinds
{
    public const string OrphanQuarantine = "OrphanQuarantine";
    public const string ExpiredDelete = "ExpiredDelete";
}

internal static class CentralObjectRecoveryStates
{
    public const string PendingCopy = "PendingCopy";
    public const string PendingDelete = "PendingDelete";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}

internal sealed class CentralObjectRecoveryDisposition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string SourceObjectIdentitySha256 { get; set; } = string.Empty;
    public string SourceObjectKey { get; set; } = string.Empty;
    public string? TargetObjectKey { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public Guid? CentralArtifactId { get; set; }
    public Guid? OperationToken { get; set; }
    public long ByteLength { get; set; }
    public string? ContentChecksumSha256 { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string? ReasonCode { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
