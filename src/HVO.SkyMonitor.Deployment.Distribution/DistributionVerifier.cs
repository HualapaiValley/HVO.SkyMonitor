using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.Distribution;

public sealed class DistributionValidationException : Exception
{
    public DistributionValidationException()
    {
    }

    public DistributionValidationException(string message)
        : base(message)
    {
    }

    public DistributionValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public static partial class DistributionVerifier
{
    public const int MaximumManifestBytes = 256 * 1024;
    public const int MaximumSignatureTextBytes = 128;
    public const long MaximumInstallerBytes = 512L * 1024 * 1024;
    public const long MaximumCatalogBundleBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumImageArchiveBytes = 32L * 1024 * 1024 * 1024;
    public const long MaximumEvidenceAssetBytes = 64L * 1024 * 1024;

    public static DistributionReleaseManifest VerifyManifest(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureText,
        DistributionTrustRoot trustRoot)
    {
        ArgumentNullException.ThrowIfNull(trustRoot);
        VerifySignedBytes(manifestBytes, signatureText, trustRoot);
        using (var document = ParseStrictJson(manifestBytes))
        {
            // The declared version is read before typed deserialization on purpose. A newer manifest may carry
            // members this build has no property for, and strict unmapped-member handling would reject it as a
            // schema error before the version check could explain that the installer is what needs upgrading.
            EnsureSupportedManifestVersion(document.RootElement);
        }
        DistributionReleaseManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, DistributionJsonContext.Default.DistributionReleaseManifest)
                ?? throw new DistributionValidationException("The distribution manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new DistributionValidationException("The distribution manifest schema is invalid.", exception);
        }
        Validate(manifest, trustRoot);
        return manifest;
    }

    public static DistributionReleaseIndex VerifyIndex(
        ReadOnlySpan<byte> indexBytes,
        ReadOnlySpan<byte> signatureText,
        DistributionTrustRoot trustRoot)
    {
        ArgumentNullException.ThrowIfNull(trustRoot);
        VerifySignedBytes(indexBytes, signatureText, trustRoot);
        ParseStrictJson(indexBytes).Dispose();
        DistributionReleaseIndex index;
        try
        {
            index = JsonSerializer.Deserialize(indexBytes, DistributionJsonContext.Default.DistributionReleaseIndex)
                ?? throw new DistributionValidationException("The distribution index is empty.");
        }
        catch (JsonException exception)
        {
            throw new DistributionValidationException("The distribution index schema is invalid.", exception);
        }
        Validate(index, trustRoot);
        return index;
    }

    public static async Task VerifyAssetAsync(
        Stream stream,
        DistributionArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(artifact);
        if (!stream.CanRead)
        {
            throw new DistributionValidationException("The distribution asset stream is not readable.");
        }
        ValidateArtifact(artifact);
        long length = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            length = checked(length + read);
            if (length > artifact.Length)
            {
                throw new DistributionValidationException($"Asset '{artifact.AssetName}' exceeds its signed length.");
            }
            hash.AppendData(buffer, 0, read);
        }
        var actualHash = hash.GetHashAndReset();
        var expectedHash = Convert.FromHexString(artifact.Sha256);
        if (length != artifact.Length || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            throw new DistributionValidationException($"Asset '{artifact.AssetName}' does not match its signed identity.");
        }
    }

    public static byte[] Sign(ReadOnlySpan<byte> bytes, string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static void VerifySignedBytes(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> signatureText,
        DistributionTrustRoot trustRoot)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumManifestBytes || signatureText.IsEmpty ||
            signatureText.Length > MaximumSignatureTextBytes)
        {
            throw new DistributionValidationException("Signed distribution metadata exceeds its bounded size.");
        }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            throw new DistributionValidationException("Signed distribution metadata must be UTF-8 without a BOM.");
        }
        var signatureValue = Encoding.ASCII.GetString(signatureText);
        if (signatureValue.EndsWith("\r\n", StringComparison.Ordinal))
        {
            signatureValue = signatureValue[..^2];
        }
        else if (signatureValue.EndsWith('\n'))
        {
            signatureValue = signatureValue[..^1];
        }
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureValue);
        }
        catch (FormatException exception)
        {
            throw new DistributionValidationException("The distribution signature is not canonical base64.", exception);
        }
        if (signature.Length != 64 || Convert.ToBase64String(signature) != signatureValue)
        {
            throw new DistributionValidationException("The distribution signature must be a 64-byte P1363 value.");
        }
        using var key = trustRoot.CreateVerifier();
        if (!key.VerifyData(bytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        {
            throw new DistributionValidationException("The distribution signature is invalid or untrusted.");
        }
    }

    /// <summary>
    /// Reads the manifest's declared schema version straight from the signed bytes and rejects one this build does
    /// not implement. The verifier ships inside the installer, so this is the one failure an operator can act on,
    /// and it must survive a future manifest that also adds members this build does not know.
    /// </summary>
    private static void EnsureSupportedManifestVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var declared))
        {
            throw new DistributionValidationException("The distribution manifest does not declare a schema version.");
        }
        if (declared < DistributionSchemaVersions.ReleaseManifest ||
            declared > DistributionSchemaVersions.MaximumReleaseManifest)
        {
            throw new DistributionValidationException(
                $"This installation implements distribution release manifest versions " +
                $"{DistributionSchemaVersions.ReleaseManifest} through " +
                $"{DistributionSchemaVersions.MaximumReleaseManifest}, but the release declares version " +
                $"{declared}. Upgrade the installer before installing this release.");
        }
    }

    private static void Validate(DistributionReleaseManifest manifest, DistributionTrustRoot root)
    {
        // EnsureSupportedManifestVersion already rejected an unsupported version before deserialization. This
        // repeats the bound so the invariant holds for any future caller that validates a manifest it did not
        // read through VerifyManifest.
        if (manifest.SchemaVersion < DistributionSchemaVersions.ReleaseManifest ||
            manifest.SchemaVersion > DistributionSchemaVersions.MaximumReleaseManifest)
        {
            throw new DistributionValidationException(
                $"This installation implements distribution release manifest versions " +
                $"{DistributionSchemaVersions.ReleaseManifest} through " +
                $"{DistributionSchemaVersions.MaximumReleaseManifest}, but the release declares version " +
                $"{manifest.SchemaVersion}. Upgrade the installer before installing this release.");
        }
        if (manifest.Signing.Algorithm != DistributionTrustRoot.Algorithm || manifest.Signing.KeyId != root.KeyId ||
            !TrainRegex().IsMatch(manifest.Release.Train) || !VersionRegex().IsMatch(manifest.Release.Version) ||
            !TagRegex().IsMatch(manifest.Release.Tag) || !RepositoryRegex().IsMatch(manifest.Release.Repository) ||
            !GitOidRegex().IsMatch(manifest.Release.SourceRevision) || !GitOidRegex().IsMatch(manifest.Release.SourceTree) ||
            manifest.Release.CreatedUtc == default || manifest.Release.CreatedUtc.Offset != TimeSpan.Zero ||
            manifest.Artifacts.Count == 0)
        {
            throw new DistributionValidationException("The distribution release identity is invalid.");
        }
        var expectedCatalogCount = manifest.ManifestKind == DistributionManifestKind.CatalogRelease ? 1 : 0;
        var expectedImageCount = manifest.ManifestKind == DistributionManifestKind.ImageRelease ? 1 : 0;
        if ((manifest.Catalog is null ? 0 : 1) != expectedCatalogCount || manifest.Images.Count != expectedImageCount)
        {
            throw new DistributionValidationException("The distribution manifest kind does not match its payload identity.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in manifest.Artifacts)
        {
            if (!names.Add(artifact.AssetName))
            {
                throw new DistributionValidationException("The distribution artifact set is invalid.");
            }
            ValidateArtifact(artifact);
        }
        if (CountRole(manifest, DistributionArtifactRole.Checksums) != 1 ||
            CountRole(manifest, DistributionArtifactRole.Sbom) != 1 ||
            CountRole(manifest, DistributionArtifactRole.Provenance) != 1 ||
            CountRole(manifest, DistributionArtifactRole.VulnerabilityScan) !=
                (manifest.ManifestKind == DistributionManifestKind.ImageRelease ? 1 : 0))
        {
            throw new DistributionValidationException("The distribution manifest omits required evidence assets.");
        }
        ValidateComponentInventoryShape(manifest);
        ValidateReleaseShape(manifest, names);
        if (manifest.Catalog is { } catalog &&
            (catalog.CatalogId != "hyg-v42-production" || !ProductionCatalogVersionRegex().IsMatch(catalog.PackageVersion) ||
             catalog.PackageKind != "production" || catalog.ManifestVersion != 2 || catalog.SchemaVersion != "2" ||
             catalog.PreprocessingVersion != "3" || !Sha256Regex().IsMatch(catalog.BundleManifestSha256) ||
              !Sha256Regex().IsMatch(catalog.DatabaseSha256) || catalog.DatabaseLength <= 0 || catalog.RowCount <= 0 ||
              string.IsNullOrWhiteSpace(catalog.LicenseIdentifier) || string.IsNullOrWhiteSpace(catalog.TopologyIdentity) ||
              !Sha256Regex().IsMatch(catalog.TopologySha256)))
        {
            throw new DistributionValidationException("The signed catalog identity is invalid.");
        }
        foreach (var image in manifest.Images)
        {
            if (!DigestRegex().IsMatch(image.ManifestDigest) || image.Platforms.Count == 0 ||
                image.Platforms.Any(static platform => platform.OperatingSystem != "linux" ||
                    platform.Architecture is not ("amd64" or "arm64") || !DigestRegex().IsMatch(platform.ManifestDigest)))
            {
                throw new DistributionValidationException("The signed image identity is invalid.");
            }
        }
    }

    /// <summary>
    /// Applies the versioned per-platform component-inventory rule. Version
    /// <see cref="DistributionSchemaVersions.ReleaseManifest"/> is the shape published before component inventories
    /// existed and must declare none, so a manifest signed under it stays verifiable unchanged. Version
    /// <see cref="DistributionSchemaVersions.ReleaseManifestWithComponentSboms"/> is an image release that publishes
    /// exactly one inventory per platform, each named by the platform whose operating system and architecture the
    /// inventory itself declares, so an inventory for one architecture can never be presented as another's.
    /// </summary>
    private static void ValidateComponentInventoryShape(DistributionReleaseManifest manifest)
    {
        var inventories = manifest.Artifacts
            .Where(static artifact => artifact.Role == DistributionArtifactRole.ComponentSbom)
            .ToArray();
        var platforms = manifest.Images.SelectMany(static image => image.Platforms).ToArray();
        if (manifest.SchemaVersion == DistributionSchemaVersions.ReleaseManifest)
        {
            if (inventories.Length != 0 || platforms.Any(static platform => platform.ComponentSbomAsset is not null))
            {
                throw new DistributionValidationException(
                    "A version 1 distribution manifest cannot declare a per-platform component inventory.");
            }
            return;
        }
        if (manifest.ManifestKind != DistributionManifestKind.ImageRelease)
        {
            throw new DistributionValidationException(
                "Only an image release may declare a per-platform component inventory manifest version.");
        }
        if (inventories.Length != platforms.Length)
        {
            throw new DistributionValidationException(
                "The image release does not publish exactly one component inventory per platform.");
        }
        foreach (var platform in platforms)
        {
            if (platform.ComponentSbomAsset is not { } asset ||
                inventories.SingleOrDefault(artifact => artifact.AssetName == asset) is not { } inventory ||
                inventory.OperatingSystem != platform.OperatingSystem || inventory.Architecture != platform.Architecture)
            {
                throw new DistributionValidationException(
                    "The image release platform does not name a signed component inventory for its own platform.");
            }
        }
    }

    /// <summary>
    /// Validates the image release payload. Every platform must carry an offline archive asset and the immutable
    /// image ID that archive loads, so an air-gapped installation can select, verify, and load its own architecture
    /// from signed metadata alone, and so a substituted archive fails before Docker is asked to load it.
    /// </summary>
    private static void ValidateImageRelease(DistributionReleaseManifest manifest, HashSet<string> names)
    {
        var image = manifest.Images[0];
        if (manifest.Release.Train != "image" || manifest.Release.Tag != $"image-v{manifest.Release.Version}" ||
            image.Component != "CameraAgent" || !ImageRepositoryRegex().IsMatch(image.Repository) ||
            image.SourceRevision != manifest.Release.SourceRevision || image.SourceTree != manifest.Release.SourceTree ||
            CountRole(manifest, DistributionArtifactRole.License) != 1 ||
            manifest.Artifacts.Any(static artifact => artifact.Role is DistributionArtifactRole.Installer or
                DistributionArtifactRole.CatalogBundle or DistributionArtifactRole.Attribution))
        {
            throw new DistributionValidationException("The image release identity is invalid.");
        }
        if (image.SbomAsset != SingleRoleAsset(manifest, DistributionArtifactRole.Sbom) ||
            image.ProvenanceAsset != SingleRoleAsset(manifest, DistributionArtifactRole.Provenance) ||
            image.VulnerabilityScanAsset != SingleRoleAsset(manifest, DistributionArtifactRole.VulnerabilityScan))
        {
            throw new DistributionValidationException("The signed image evidence assets do not match the release artifacts.");
        }
        var archives = manifest.Artifacts.Where(static artifact => artifact.Role == DistributionArtifactRole.ImageArchive).ToArray();
        if (archives.Length != image.Platforms.Count ||
            image.Platforms.Select(static platform => $"{platform.OperatingSystem}/{platform.Architecture}")
                .Distinct(StringComparer.Ordinal).Count() != image.Platforms.Count ||
            image.Platforms.Select(static platform => platform.ManifestDigest)
                .Distinct(StringComparer.Ordinal).Count() != image.Platforms.Count)
        {
            throw new DistributionValidationException("The image release does not declare one distinct archive per platform.");
        }
        foreach (var platform in image.Platforms)
        {
            if (platform.OfflineArchiveAsset is not { } archiveAsset || !names.Contains(archiveAsset) ||
                platform.OfflineArchiveImageId is not { } imageId || !DigestRegex().IsMatch(imageId) ||
                archives.SingleOrDefault(artifact => artifact.AssetName == archiveAsset) is not { } archive ||
                archive.OperatingSystem != platform.OperatingSystem || archive.Architecture != platform.Architecture)
            {
                throw new DistributionValidationException("The image release platform does not identify a signed offline archive.");
            }
        }
        var compatibility = image.Compatibility;
        if (compatibility.StateContract != "cameraagent-state-v2" ||
            !GitOidRegex().IsMatch(compatibility.MinimumCompatibleRevision) ||
            !ContractIdentityRegex().IsMatch(compatibility.IdentityMigration) ||
            compatibility.RawIngressSchema <= 0 || compatibility.CatalogManifestVersion <= 0 ||
            !ContractIdentityRegex().IsMatch(compatibility.ConfigurationContract) ||
            !ContractIdentityRegex().IsMatch(compatibility.CatalogContract) ||
            compatibility.ReplayRunnerContract is { } replay && !ContractIdentityRegex().IsMatch(replay))
        {
            throw new DistributionValidationException("The signed image compatibility identity is invalid.");
        }
    }

    private static string SingleRoleAsset(DistributionReleaseManifest manifest, DistributionArtifactRole role)
        => manifest.Artifacts.Single(artifact => artifact.Role == role).AssetName;

    private static void Validate(DistributionReleaseIndex index, DistributionTrustRoot root)
    {
        if (index.SchemaVersion != DistributionSchemaVersions.ReleaseIndex || index.ManifestKind != "release-index" ||
            index.Signing.Algorithm != DistributionTrustRoot.Algorithm || index.Signing.KeyId != root.KeyId ||
            index.Train is not ("installer" or "catalog" or "image") || index.Sequence < 1 || index.CreatedUtc == default ||
            index.CreatedUtc.Offset != TimeSpan.Zero ||
            index.Releases.Count == 0 || !index.Releases.Any(release => release.Version == index.DefaultVersion))
        {
            throw new DistributionValidationException("The signed release index is invalid.");
        }
        var versions = new HashSet<string>(StringComparer.Ordinal);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var release in index.Releases)
        {
            if (!versions.Add(release.Version) || !tags.Add(release.Tag) || !VersionRegex().IsMatch(release.Version) ||
                !TagRegex().IsMatch(release.Tag) || !AssetNameRegex().IsMatch(release.ManifestAsset) ||
                !AssetNameRegex().IsMatch(release.SignatureAsset) || release.ManifestLength <= 0 ||
                release.ManifestLength > MaximumManifestBytes || !Sha256Regex().IsMatch(release.ManifestSha256) ||
                release.Tag != $"{index.Train}-{(index.Train == "catalog" ? string.Empty : "v")}{release.Version}")
            {
                throw new DistributionValidationException("The signed release index contains an invalid entry.");
            }
        }
    }

    private static void ValidateReleaseShape(DistributionReleaseManifest manifest, HashSet<string> names)
    {
        if (manifest.ManifestKind == DistributionManifestKind.ImageRelease)
        {
            ValidateImageRelease(manifest, names);
            return;
        }

        if (manifest.Artifacts.Any(static artifact => artifact.Role == DistributionArtifactRole.ImageArchive))
        {
            throw new DistributionValidationException("Only an image release may publish image archives.");
        }

        if (manifest.ManifestKind == DistributionManifestKind.InstallerRelease)
        {
            var installers = manifest.Artifacts.Where(static artifact => artifact.Role == DistributionArtifactRole.Installer).ToArray();
            if (manifest.Release.Train != "installer" || manifest.Release.Tag != $"installer-v{manifest.Release.Version}" ||
                installers.Length != 2 ||
                !installers.Any(static artifact => artifact.OperatingSystem == "linux" && artifact.Architecture == "x64") ||
                !installers.Any(static artifact => artifact.OperatingSystem == "linux" && artifact.Architecture == "arm64") ||
                manifest.Artifacts.Any(static artifact => artifact.Role is DistributionArtifactRole.CatalogBundle or DistributionArtifactRole.Attribution))
            {
                throw new DistributionValidationException("The installer release artifact set is invalid.");
            }
            return;
        }

        var catalog = manifest.Catalog!;
        if (manifest.Release.Train != "catalog" || manifest.Release.Version != catalog.PackageVersion ||
            manifest.Release.Tag != $"catalog-{manifest.Release.Version}" ||
            CountRole(manifest, DistributionArtifactRole.CatalogBundle) != 1 ||
            CountRole(manifest, DistributionArtifactRole.License) != 1 ||
            CountRole(manifest, DistributionArtifactRole.Attribution) != 1 ||
            manifest.Artifacts.Single(static artifact => artifact.Role == DistributionArtifactRole.License).AssetName != catalog.LicenseAsset ||
            manifest.Artifacts.Single(static artifact => artifact.Role == DistributionArtifactRole.Attribution).AssetName != catalog.AttributionAsset ||
            !names.Contains(catalog.LicenseAsset) || !names.Contains(catalog.AttributionAsset))
        {
            throw new DistributionValidationException("The catalog release artifact set is invalid.");
        }
    }

    private static int CountRole(DistributionReleaseManifest manifest, DistributionArtifactRole role)
        => manifest.Artifacts.Count(artifact => artifact.Role == role);

    private static void ValidateArtifact(DistributionArtifact artifact)
    {
        var maximumLength = artifact.Role switch
        {
            DistributionArtifactRole.Installer => MaximumInstallerBytes,
            DistributionArtifactRole.CatalogBundle => MaximumCatalogBundleBytes,
            DistributionArtifactRole.ImageArchive => MaximumImageArchiveBytes,
            _ => MaximumEvidenceAssetBytes
        };
        if (!AssetNameRegex().IsMatch(artifact.AssetName) || artifact.Length <= 0 || artifact.Length > maximumLength ||
            !Sha256Regex().IsMatch(artifact.Sha256) || string.IsNullOrWhiteSpace(artifact.MediaType))
        {
            throw new DistributionValidationException($"Distribution asset '{artifact.AssetName}' has an invalid signed identity.");
        }
    }

    /// <summary>
    /// Parses signed metadata strictly and hands the document back, so a caller that also needs to read a member
    /// before typed deserialization does not parse the same bytes a second time. The caller owns the document.
    /// </summary>
    private static JsonDocument ParseStrictJson(ReadOnlySpan<byte> bytes)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        }
        catch (JsonException exception)
        {
            throw new DistributionValidationException("Signed distribution metadata is not strict JSON.", exception);
        }
        try
        {
            EnsureNoDuplicates(document.RootElement, "$");
        }
        catch
        {
            document.Dispose();
            throw;
        }
        return document;
    }

    private static void EnsureNoDuplicates(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new DistributionValidationException($"Signed distribution metadata contains duplicate member '{path}{property.Name}'.");
                }
                EnsureNoDuplicates(property.Value, $"{path}{property.Name}.");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureNoDuplicates(item, path);
            }
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex TrainRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagRegex();

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();

    [GeneratedRegex("^[a-f0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex GitOidRegex();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();

    [GeneratedRegex("^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{0,63}(:[0-9]{1,5})?(/[a-z0-9]+([._-][a-z0-9]+)*){1,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImageRepositoryRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContractIdentityRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetNameRegex();

    [GeneratedRegex("^hyg-v4\\.2-p3-s2-r[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ProductionCatalogVersionRegex();
}
