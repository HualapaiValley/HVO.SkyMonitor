using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Deployment.Contracts;

public static class DistributionSchemaVersions
{
    /// <summary>
    /// The release-manifest shape every train has published since the first signed release. It carries exactly one
    /// <see cref="DistributionArtifactRole.Sbom"/> document and no per-platform component inventory.
    /// </summary>
    public const int ReleaseManifest = 1;

    /// <summary>
    /// An image release that additionally publishes one <see cref="DistributionArtifactRole.ComponentSbom"/>
    /// inventory per published platform, bound to that platform through
    /// <see cref="DistributionImagePlatform.ComponentSbomAsset"/>. The addition is purely additive: version 1 stays
    /// a valid, verifiable shape, and only an image release may declare version 2.
    /// </summary>
    public const int ReleaseManifestWithComponentSboms = 2;

    /// <summary>The newest release-manifest version this build produces and verifies.</summary>
    public const int MaximumReleaseManifest = ReleaseManifestWithComponentSboms;

    public const int ReleaseIndex = 1;
}

[JsonConverter(typeof(JsonStringEnumConverter<DistributionManifestKind>))]
public enum DistributionManifestKind
{
    InstallerRelease,
    CatalogRelease,
    ImageRelease
}

[JsonConverter(typeof(JsonStringEnumConverter<DistributionArtifactRole>))]
public enum DistributionArtifactRole
{
    Installer,
    CatalogBundle,
    ImageArchive,
    Checksums,
    Sbom,
    ComponentSbom,
    Provenance,
    License,
    Attribution,
    VulnerabilityScan
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

/// <summary>
/// One published platform of an image release. <paramref name="ComponentSbomAsset"/> names the SPDX component
/// inventory for exactly this platform's image and is present only in release-manifest version
/// <see cref="DistributionSchemaVersions.ReleaseManifestWithComponentSboms"/> or later; a version 1 manifest leaves
/// it null and remains verifiable.
/// </summary>
public sealed record DistributionImagePlatform(
    string OperatingSystem,
    string Architecture,
    string ManifestDigest,
    string? OfflineArchiveAsset,
    string? OfflineArchiveImageId,
    string? ComponentSbomAsset = null);

/// <summary>
/// The durable-state, configuration, catalog, and replay boundaries the published image declares through its
/// OCI labels. The signed copy exists so an installation can reject an incompatible image before it loads or
/// starts anything, and so the labels the running image actually carries can be compared against signed values
/// rather than trusted on their own.
/// </summary>
public sealed record DistributionImageCompatibility(
    string StateContract,
    string MinimumCompatibleRevision,
    string IdentityMigration,
    int RawIngressSchema,
    int CatalogManifestVersion,
    string ConfigurationContract,
    string CatalogContract,
    string? ReplayRunnerContract);

public sealed record DistributionImageIdentity(
    string Component,
    string Repository,
    string ManifestDigest,
    string SourceRevision,
    string SourceTree,
    IReadOnlyList<DistributionImagePlatform> Platforms,
    string ProvenanceAsset,
    string SbomAsset,
    string VulnerabilityScanAsset,
    DistributionImageCompatibility Compatibility);

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
