using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment.Contracts;

public static class DistributionSchemaVersions
{
    public const int ReleaseManifest = 1;
    public const int ReleaseIndex = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<DistributionManifestKind>))]
public enum DistributionManifestKind
{
    InstallerRelease,
    CatalogRelease
}

[JsonConverter(typeof(JsonStringEnumConverter<DistributionArtifactRole>))]
public enum DistributionArtifactRole
{
    Installer,
    CatalogBundle,
    ImageArchive,
    Checksums,
    Sbom,
    Provenance,
    License,
    Attribution
}

public sealed record DistributionReleaseIdentity(
    string Train,
    string Version,
    string Tag,
    string Repository,
    string SourceRevision,
    string SourceTree,
    DateTimeOffset CreatedUtc);

public sealed record DistributionSigningIdentity(
    string Algorithm,
    string KeyId);

public sealed record DistributionArtifact(
    DistributionArtifactRole Role,
    string AssetName,
    string MediaType,
    long Length,
    string Sha256,
    string? OperatingSystem = null,
    string? Architecture = null);

public sealed record DistributionCatalogIdentity(
    string CatalogId,
    string PackageVersion,
    string PackageKind,
    int ManifestVersion,
    string SchemaVersion,
    string PreprocessingVersion,
    string BundleManifestSha256,
    string DatabaseSha256,
    long DatabaseLength,
    long RowCount,
    string LicenseIdentifier,
    string LicenseAsset,
    string AttributionAsset,
    string TopologyIdentity,
    string TopologySha256);

public sealed record DistributionImagePlatform(
    string OperatingSystem,
    string Architecture,
    string ManifestDigest,
    string? OfflineArchiveAsset,
    string? OfflineArchiveImageId);

public sealed record DistributionImageIdentity(
    string Component,
    string Repository,
    string ManifestDigest,
    string SourceRevision,
    string SourceTree,
    IReadOnlyList<DistributionImagePlatform> Platforms,
    string ProvenanceAsset,
    string SbomAsset);

public sealed record DistributionReleaseManifest(
    int SchemaVersion,
    DistributionManifestKind ManifestKind,
    DistributionReleaseIdentity Release,
    DistributionSigningIdentity Signing,
    IReadOnlyList<DistributionArtifact> Artifacts,
    DistributionCatalogIdentity? Catalog,
    IReadOnlyList<DistributionImageIdentity> Images);

public sealed record DistributionReleaseReference(
    string Version,
    string Tag,
    string ManifestAsset,
    long ManifestLength,
    string ManifestSha256,
    string SignatureAsset);

public sealed record DistributionReleaseIndex(
    int SchemaVersion,
    string ManifestKind,
    string Train,
    long Sequence,
    DateTimeOffset CreatedUtc,
    string DefaultVersion,
    DistributionSigningIdentity Signing,
    IReadOnlyList<DistributionReleaseReference> Releases);

public sealed record DistributionVerificationEvidence(
    string ManifestKind,
    string ReleaseTrain,
    string ReleaseVersion,
    string ReleaseTag,
    string ManifestSha256,
    long ManifestLength,
    string SigningKeyId,
    string AssetName,
    string AssetSha256,
    long AssetLength,
    Uri SourceBaseUri,
    Uri ResolvedPublicUri,
    string VerificationResult,
    DateTimeOffset VerifiedUtc,
    string? ProvenanceAssetName,
    string? ProvenanceSha256);
