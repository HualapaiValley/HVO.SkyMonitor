using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The installer's view of the approved production catalogs (issue #521). Every constant comes from the reviewed
/// registry embedded in <c>HVO.SkyMonitor.Catalog.Sqlite</c>; a bundle, manifest or retained record can only name an
/// approved specification. HYG 4.2 remains the default for a fresh install that does not supply a catalog.
/// </summary>
internal static class ProductionCatalog
{
    public const string DefaultCatalogId = "hyg-v42-production";

    public static ApprovedCatalogSpecificationSet Specifications { get; } = new(
        ApprovedCatalogSpecifications.All.Select(ToContract));

    public static ApprovedCatalogContract Get(string? catalogId)
        => catalogId is not null && Specifications.TryGet(catalogId, out var specification)
            ? specification
            : throw new InstallerException("The catalog is not an approved production catalog specification.");

    public static ApprovedCatalogContract ForPackageVersion(string? packageVersion)
        => Specifications.TryGetByPackageVersion(packageVersion, out var specification)
            ? specification
            : throw new InstallerException("The catalog package version does not belong to an approved production catalog.");

    public static bool TryForPackageVersion(string? packageVersion, [NotNullWhen(true)] out ApprovedCatalogContract? specification)
        => Specifications.TryGetByPackageVersion(packageVersion, out specification);

    public static CatalogSnapshotResolverOptions ResolverOptions(string root, string catalogId, string? packageVersion = null)
    {
        var specification = Get(catalogId);
        return new(root, specification.CatalogId)
        {
            ExpectedPackageKind = CatalogSnapshotPackageKind.Production,
            ExpectedSchemaVersion = specification.SchemaVersion,
            ExpectedPreprocessingVersion = specification.PreprocessingVersion,
            ExpectedPackageVersion = packageVersion
        };
    }

    /// <summary>
    /// True when a recorded installation identity names an approved catalog and carries exactly the pinned database
    /// identity of that specification.
    /// </summary>
    public static bool IsPinned([NotNullWhen(true)] CatalogInstallationIdentity? value)
        => value is not null && Specifications.TryGet(value.CatalogId, out var specification) &&
           specification.IsPackageVersion(value.PackageVersion) &&
           value.SchemaVersion == specification.SchemaVersion &&
           value.PreprocessingVersion == specification.PreprocessingVersion &&
           value.DatabaseSha256 == specification.DatabaseSha256 &&
           value.DatabaseLength == specification.DatabaseLength && value.RowCount == specification.RowCount;

    public static CatalogInstallationIdentity ToIdentity(CatalogSnapshotResult snapshot, string installRoot)
    {
        var specification = Get(snapshot.CatalogId);
        var databaseSha256 = Convert.ToHexStringLower(Convert.FromHexString(snapshot.DatabaseSha256));
        if (databaseSha256 != specification.DatabaseSha256 ||
            snapshot.DatabaseLength != specification.DatabaseLength || snapshot.RowCount != specification.RowCount)
        {
            throw new InstallerException("The catalog does not match the installer-pinned production identity.");
        }
        using var manifest = File.OpenRead(snapshot.ManifestPath);
        var manifestSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(manifest));
        return new(
            snapshot.CatalogId,
            snapshot.SnapshotVersion,
            snapshot.SchemaVersion,
            snapshot.PreprocessingVersion,
            databaseSha256,
            snapshot.DatabaseLength,
            snapshot.RowCount,
            installRoot,
            manifestSha256,
            "local-offline");
    }

    /// <summary>
    /// Reads only the approved specification a bundle's manifest names. The package version selects the
    /// specification and the declared catalog ID must agree with it; the full manifest, file set and bytes are
    /// validated later by the snapshot resolver. This read decides only which side-by-side catalog root receives it.
    /// </summary>
    public static ApprovedCatalogContract ReadBundleSpecification(string bundlePath)
    {
        const int MaximumManifestBytes = 64 * 1024;
        using var stream = SafeFileSystem.OpenRegularFileRead(Path.Combine(bundlePath, "manifest.json"));
        if (stream.Length > MaximumManifestBytes)
            throw new InstallerException("The catalog bundle manifest is too large.");
        string? catalogId = null;
        string? packageVersion = null;
        try
        {
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("catalog", out var catalog) && catalog.ValueKind == JsonValueKind.Object &&
                    catalog.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    catalogId = id.GetString();
                }
                if (root.TryGetProperty("package", out var package) && package.ValueKind == JsonValueKind.Object &&
                    package.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String)
                {
                    packageVersion = version.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed metadata reports the same stable, redacted error as a missing package version.
        }
        if (!TryForPackageVersion(packageVersion, out var specification))
            throw new InstallerException("The catalog bundle package version is invalid.");
        if (!string.Equals(catalogId, specification.CatalogId, StringComparison.Ordinal))
            throw new InstallerException("The catalog bundle names a catalog other than its package lineage.");
        return specification;
    }

    private static ApprovedCatalogContract ToContract(ApprovedCatalogSpecification specification)
        => new(
            specification.CatalogId,
            specification.PackageLineage,
            specification.PackageVersionPrefix,
            specification.CatalogName,
            specification.CatalogVersion,
            specification.ManifestVersion,
            specification.SchemaVersion,
            specification.PreprocessingVersion,
            specification.Database.RelativePath,
            specification.Database.Sha256,
            specification.Database.Length,
            specification.Database.RowCount,
            specification.License.Identifier,
            specification.License.File.RelativePath,
            specification.License.Attribution.RelativePath,
            specification.Topology.Identity,
            specification.Topology.Sha256);
}
