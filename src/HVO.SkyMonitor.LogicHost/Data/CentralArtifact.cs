using HVO.SkyMonitor.AgentCore;

namespace HVO.SkyMonitor.LogicHost.Data;

/// <summary>Stores immutable delivery metadata for one artifact belonging to a central frame.</summary>
internal sealed class CentralArtifact
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid CentralFrameId { get; set; }

    public CentralFrame? Frame { get; set; }

    public Guid ArtifactId { get; set; }

    public FrameArtifactRole Role { get; set; }

    public string RecipeVersion { get; set; } = string.Empty;

    public string ManifestSchemaVersion { get; set; } = string.Empty;

    public string MediaType { get; set; } = string.Empty;

    public long ByteLength { get; set; }

    public string ChecksumSha256 { get; set; } = string.Empty;

    public string StorageReference { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;
}
