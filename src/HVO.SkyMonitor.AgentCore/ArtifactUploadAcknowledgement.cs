namespace HVO.SkyMonitor.AgentCore;

/// <summary>Durable central acceptance of one exact artifact upload.</summary>
public sealed record ArtifactUploadAcknowledgement(
    string SchemaVersion,
    string IdempotencyKey,
    Guid ArtifactId,
    string ChecksumSha256,
    long ByteLength,
    DateTimeOffset AcceptedAtUtc,
    string AcceptedManifestSchemaVersion)
{
    public const string CurrentSchemaVersion = "v1";

    public void Validate()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(IdempotencyKey)
            || IdempotencyKey.Length != 64
            || IdempotencyKey.Any(static character => !Uri.IsHexDigit(character))
            || ArtifactId == Guid.Empty
            || string.IsNullOrWhiteSpace(ChecksumSha256)
            || ChecksumSha256.Length != 64
            || ChecksumSha256.Any(static character => !Uri.IsHexDigit(character))
            || ByteLength < 0
            || AcceptedAtUtc == default
            || AcceptedAtUtc.Offset != TimeSpan.Zero
            || string.IsNullOrWhiteSpace(AcceptedManifestSchemaVersion)
            || AcceptedManifestSchemaVersion.Length > 64)
        {
            throw new ArgumentException("Artifact upload acknowledgement is invalid.", nameof(ArtifactUploadAcknowledgement));
        }
    }
}
