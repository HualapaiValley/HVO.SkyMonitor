using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>Stores immutable delivery metadata for one artifact belonging to a central frame.</summary>
internal sealed class CentralArtifact
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CentralFrameId { get; set; }

    public CentralFrame? Frame { get; set; }

    public Guid ArtifactId { get; set; }

    public Guid? DevicePublicId { get; set; }

    public FrameArtifactRole Role { get; set; }

    public string RecipeVersion { get; set; } = string.Empty;

    public string ManifestSchemaVersion { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public long ByteLength { get; set; }

    public string ChecksumSha256 { get; set; } = string.Empty;

    public string StorageReference { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public string? SourceId { get; set; }

    public string? Variant { get; set; }

    public DateTimeOffset? CreatedUtc { get; set; }

    public CentralArtifactObjectState ObjectState { get; set; } = CentralArtifactObjectState.Available;

    public CentralReconstructionState ReconstructionState { get; set; } = CentralReconstructionState.LegacyIncomplete;

    public string? StateReasonCode { get; set; }

    public DateTimeOffset? ReconciledAtUtc { get; set; }

    public DateTimeOffset? ObjectVerifiedAtUtc { get; set; }

    public long RecoveryGeneration { get; set; }

    public int ReferenceRetryCount { get; set; }

    public DateTimeOffset? ReferenceRetryAtUtc { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public CentralArtifactLayout? Layout { get; set; }

    public CentralArtifactRecipe? Recipe { get; set; }

    public ICollection<CentralArtifactSource> Sources { get; } = [];

    public ICollection<CentralArtifactIngestIdentity> IngestIdentities { get; } = [];
}
