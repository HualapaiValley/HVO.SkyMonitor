using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment.ReleaseTool;

/// <summary>
/// The release tool's view of the approved catalog registry embedded in <c>HVO.SkyMonitor.Catalog.Sqlite</c>
/// (issue #521). A catalog release can only package and verify a catalog that registry approves.
/// </summary>
internal static class ReleaseCatalogs
{
    public static ApprovedCatalogSpecificationSet Specifications { get; } = new(
        ApprovedCatalogSpecifications.All.Select(static specification => new ApprovedCatalogContract(
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
            specification.Topology.Sha256)));

    public static ApprovedCatalogContract Get(string? catalogId, string? packageVersion)
        => catalogId is not null && Specifications.TryGet(catalogId, out var specification) &&
           specification.IsPackageVersion(packageVersion)
            ? specification
            : throw new ReleaseToolException("The catalog is not an approved production catalog specification.");

    /// <summary>The bundle file set in the ordinal order the archive is written.</summary>
    public static string[] OrderedFiles(ApprovedCatalogContract specification)
        => specification.RetainedFileNames.Order(StringComparer.Ordinal).ToArray();
}
