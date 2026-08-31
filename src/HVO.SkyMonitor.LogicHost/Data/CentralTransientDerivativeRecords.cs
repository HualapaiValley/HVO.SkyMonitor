using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Processing;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>Durable bundle-job identity over one immutable event version.</summary>
internal sealed class CentralTransientDerivativeJob
{
    public Guid CentralDerivativeJobId { get; set; }
    public CentralDerivativeJob? Job { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public Guid SourceEventVersionId { get; set; }
    public CentralTransientEventVersionRecord? SourceEventVersion { get; set; }
    public string RequestIdentitySha256 { get; set; } = string.Empty;
    public string ProducerSchemaVersion { get; set; } = string.Empty;
    public string ProducerName { get; set; } = string.Empty;
    public string ProducerVersion { get; set; } = string.Empty;
    public string RecipeIdentitySha256 { get; set; } = string.Empty;
    public string OptionsIdentitySha256 { get; set; } = string.Empty;
    public string CanonicalRequestJson { get; set; } = string.Empty;
    public string CanonicalRequestSha256 { get; set; } = string.Empty;
    public int CanonicalRequestByteLength { get; set; }
    public int ExpectedOutputCount { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
    public ICollection<CentralTransientDerivativeOutputIntent> OutputIntents { get; } = [];
}

/// <summary>Mutable publication intent that fences object recovery before immutable evidence is committed.</summary>
internal sealed class CentralTransientDerivativeOutputIntent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid CentralDerivativeJobId { get; set; }
    public CentralTransientDerivativeJob? DerivativeJob { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public TransientDerivativeKind Kind { get; set; }
    public Guid DerivativeId { get; set; }
    public Guid ArtifactId { get; set; }
    public FrameArtifactRole ArtifactRole { get; set; }
    public string ArtifactVariant { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public string ChecksumSha256 { get; set; } = string.Empty;
    public string OutputIdentitySha256 { get; set; } = string.Empty;
    public string StorageReference { get; set; } = string.Empty;
    public string? StorageETag { get; set; }
    public CentralArtifactObjectState ObjectState { get; set; }
    public string? StateReasonCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ObjectVerifiedAtUtc { get; set; }
    public DateTimeOffset? CommittedAtUtc { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Immutable event-owned derivative evidence; storage location remains private.</summary>
internal sealed class CentralTransientDerivativeRecord
{
    public Guid DerivativeId { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public CentralTransientEventRecord? Event { get; set; }
    public Guid SourceEventVersionId { get; set; }
    public CentralTransientEventVersionRecord? SourceEventVersion { get; set; }
    public Guid CentralDerivativeJobId { get; set; }
    public CentralTransientDerivativeJob? DerivativeJob { get; set; }
    public Guid OutputIntentId { get; set; }
    public CentralTransientDerivativeOutputIntent? OutputIntent { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public TransientDerivativeKind Kind { get; set; }
    public Guid ArtifactId { get; set; }
    public FrameArtifactRole ArtifactRole { get; set; }
    public string ArtifactVariant { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public long ByteLength { get; set; }
    public string ArtifactChecksumSha256 { get; set; } = string.Empty;
    public string RecipeIdentitySha256 { get; set; } = string.Empty;
    public string OptionsIdentitySha256 { get; set; } = string.Empty;
    public string OutputIdentitySha256 { get; set; } = string.Empty;
    public string LimitationsJson { get; set; } = string.Empty;
    public Guid? AssessmentId { get; set; }
    public Guid? ReviewId { get; set; }
    public ICollection<CentralTransientDerivativeSourceReference> Sources { get; } = [];
    public ICollection<CentralTransientDerivativeBackgroundReference> Backgrounds { get; } = [];
}

internal sealed class CentralTransientDerivativeSourceReference
{
    public Guid DerivativeId { get; set; }
    public CentralTransientDerivativeRecord? Derivative { get; set; }
    public int Ordinal { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public Guid ObservationId { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid CentralArtifactId { get; set; }
    public Guid ArtifactId { get; set; }
    public string ArtifactChecksumSha256 { get; set; } = string.Empty;
    public DateTimeOffset ObservationStartedUtc { get; set; }
    public DateTimeOffset ObservationEndedUtc { get; set; }
}

internal sealed class CentralTransientDerivativeBackgroundReference
{
    public Guid DerivativeId { get; set; }
    public CentralTransientDerivativeRecord? Derivative { get; set; }
    public int ObservationOrdinal { get; set; }
    public int BackgroundOrdinal { get; set; }
    public Guid CentralTransientEventId { get; set; }
    public Guid ObservationId { get; set; }
    public Guid CentralArtifactId { get; set; }
    public Guid ArtifactId { get; set; }
    public string ArtifactChecksumSha256 { get; set; } = string.Empty;
}

internal sealed class CentralTransientEventVersionDerivative
{
    public Guid CentralTransientEventId { get; set; }
    public Guid EventVersionId { get; set; }
    public CentralTransientEventVersionRecord? EventVersion { get; set; }
    public int Ordinal { get; set; }
    public Guid DerivativeId { get; set; }
    public CentralTransientDerivativeRecord? Derivative { get; set; }
}
