namespace HVO.SkyMonitor.LogicHost.Data;

// Imported facts have their own tables. No central job or graph owns or mutates them.
internal sealed class ExecutionEvidenceBinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ObservatoryId { get; set; }
    public Guid RegistrationId { get; set; }
    public Guid LogicalCameraInstallationId { get; set; }
    public Guid InstallationPublicId { get; set; }
    public Guid OriginInstallationId { get; set; }
    public Guid AgentInstanceId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedByUserId { get; set; } = string.Empty;
}

internal sealed class ExecutionEvidenceBindingUsage
{
    public Guid BindingId { get; set; }
    public long StoredBytes { get; set; }
    public int UnitCount { get; set; }
    public int OriginCount { get; set; }
    public int ConflictCount { get; set; }
    public int ObservationCount { get; set; }
}

internal sealed class ImportedExecutionEvidenceOrigin
{
    public long Id { get; set; }
    public Guid BindingId { get; set; }
    public string IdentitySha256 { get; set; } = string.Empty;
    public Guid BootSessionId { get; set; }
    public string SoftwareVersion { get; set; } = string.Empty;
    public DateTimeOffset FirstReceivedAtUtc { get; set; }
    public long HighestSeenSequence { get; set; }
    public long ContiguousThroughSequence { get; set; }
    public DateTimeOffset? GapDetectedAtUtc { get; set; }
}

internal sealed class ImportedExecutionEvidenceUnit
{
    public long Id { get; set; }
    public Guid BindingId { get; set; }
    public long OriginId { get; set; }
    public long OriginSequence { get; set; }
    public Guid EvidenceId { get; set; }
    public string PayloadSha256 { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string? RevisionId { get; set; }
    public Guid? ExecutionId { get; set; }
    public Guid? CaptureId { get; set; }
    public Guid? CorrectsEvidenceId { get; set; }
    public long? CorrectsOriginSequence { get; set; }
    public DateTimeOffset ProducedAtUtc { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
    public byte[] CanonicalBytes { get; set; } = [];
    public bool Accepted { get; set; }
    public string? RejectionReasonCode { get; set; }
    public string? RejectionFieldPath { get; set; }
}

// Disjoint accepted intervals make gap queries bounded by ranges, not lifetime unit count.
internal sealed class ImportedExecutionEvidenceRange
{
    public long OriginId { get; set; }
    public long FromSequence { get; set; }
    public long ToSequence { get; set; }
}

internal sealed class ImportedExecutionEvidenceConflict
{
    public long Id { get; set; }
    public Guid BindingId { get; set; }
    public long UnitId { get; set; }
    public Guid SubmittedEvidenceId { get; set; }
    public string SubmittedPayloadSha256 { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
}

internal sealed class ImportedExecutionEvidenceArtifact
{
    public long Id { get; set; }
    public Guid BindingId { get; set; }
    public long UnitId { get; set; }
    public int Ordinal { get; set; }
    public Guid ArtifactId { get; set; }
    public Guid CaptureId { get; set; }
    public string? OutputIdentitySha256 { get; set; }
    public string? PayloadSha256 { get; set; }
    public string? DescriptorSha256 { get; set; }
    public long? PayloadLength { get; set; }
    public string? MediaType { get; set; }
    public string? Role { get; set; }
    public string? Variant { get; set; }
}

internal sealed class ExecutionEvidenceArtifactCheck
{
    public long ArtifactReferenceId { get; set; }
    public Guid BindingId { get; set; }
    public Guid Version { get; set; } = Guid.NewGuid();
    public DateTimeOffset NextCheckAtUtc { get; set; }
    public string? State { get; set; }
    public string? ReasonCode { get; set; }
    public string? ObjectGeneration { get; set; }
}

internal sealed class ImportedExecutionEvidenceObservation
{
    public long Id { get; set; }
    public Guid BindingId { get; set; }
    public long ArtifactReferenceId { get; set; }
    public string State { get; set; } = string.Empty;
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset ObservedAtUtc { get; set; }
    public string? ObjectGeneration { get; set; }
}
