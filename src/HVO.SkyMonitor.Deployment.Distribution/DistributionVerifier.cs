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
        EnsureStrictJson(manifestBytes);
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
        EnsureStrictJson(indexBytes);
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

    private static void Validate(DistributionReleaseManifest manifest, DistributionTrustRoot root)
    {
        if (manifest.SchemaVersion != DistributionSchemaVersions.ReleaseManifest ||
            manifest.Signing.Algorithm != DistributionTrustRoot.Algorithm || manifest.Signing.KeyId != root.KeyId ||
            !TrainRegex().IsMatch(manifest.Release.Train) || !VersionRegex().IsMatch(manifest.Release.Version) ||
            !TagRegex().IsMatch(manifest.Release.Tag) || !RepositoryRegex().IsMatch(manifest.Release.Repository) ||
            !GitOidRegex().IsMatch(manifest.Release.SourceRevision) || !GitOidRegex().IsMatch(manifest.Release.SourceTree) ||
            manifest.Release.CreatedUtc == default || manifest.Release.CreatedUtc.Offset != TimeSpan.Zero ||
            manifest.Artifacts.Count == 0)
        {
            throw new DistributionValidationException("The distribution release identity is invalid.");
        }
        if (manifest.ManifestKind == DistributionManifestKind.CatalogRelease && manifest.Catalog is null ||
            manifest.ManifestKind == DistributionManifestKind.InstallerRelease && manifest.Catalog is not null)
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
            CountRole(manifest, DistributionArtifactRole.Provenance) != 1)
        {
            throw new DistributionValidationException("The distribution manifest omits required evidence assets.");
        }
        ValidateReleaseShape(manifest, names);
        if (manifest.Catalog is { } catalog &&
            (catalog.CatalogId != "hyg-v42-production" || catalog.PackageVersion != "hyg-v4.2-p3-s2-r1" ||
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

    private static void Validate(DistributionReleaseIndex index, DistributionTrustRoot root)
    {
        if (index.SchemaVersion != DistributionSchemaVersions.ReleaseIndex || index.ManifestKind != "release-index" ||
            index.Signing.Algorithm != DistributionTrustRoot.Algorithm || index.Signing.KeyId != root.KeyId ||
            index.Train is not ("installer" or "catalog") || index.Sequence < 1 || index.CreatedUtc == default ||
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
                release.Tag != $"{index.Train}-{(index.Train == "installer" ? "v" : string.Empty)}{release.Version}")
            {
                throw new DistributionValidationException("The signed release index contains an invalid entry.");
            }
        }
    }

    private static void ValidateReleaseShape(DistributionReleaseManifest manifest, HashSet<string> names)
    {
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

    private static void EnsureStrictJson(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            EnsureNoDuplicates(document.RootElement, "$");
        }
        catch (JsonException exception)
        {
            throw new DistributionValidationException("Signed distribution metadata is not strict JSON.", exception);
        }
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

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex AssetNameRegex();
}
