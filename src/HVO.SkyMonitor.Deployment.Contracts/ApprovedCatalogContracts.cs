using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace HVO.SkyMonitor.Deployment.Contracts;

/// <summary>
/// The deployment view of one approved production catalog specification (issue #521). The authority is the
/// embedded registry in <c>HVO.SkyMonitor.Catalog.Sqlite</c>; this record carries only the fields the signed
/// distribution and lifecycle seams compare, so those layers stay free of the catalog adapter. A manifest or bundle
/// can only name a specification; it can never introduce one.
/// </summary>
public sealed record ApprovedCatalogContract(
    string CatalogId,
    string PackageLineage,
    string PackageVersionPrefix,
    string CatalogName,
    string CatalogVersion,
    int ManifestVersion,
    string SchemaVersion,
    string PreprocessingVersion,
    string DatabaseRelativePath,
    string DatabaseSha256,
    long DatabaseLength,
    long RowCount,
    string LicenseIdentifier,
    string LicenseFile,
    string AttributionFile,
    string TopologyIdentity,
    string TopologySha256)
{
    /// <summary>The fixed production bundle and installed-version file set.</summary>
    public IReadOnlyList<string> RetainedFileNames =>
        ["manifest.json", DatabaseRelativePath, LicenseFile, AttributionFile];

    /// <summary>
    /// True when <paramref name="packageVersion"/> is this lineage's prefix followed by a positive revision without a
    /// leading zero, such as <c>hyg-v4.4-p4-s3-r1</c>.
    /// </summary>
    public bool IsPackageVersion([NotNullWhen(true)] string? packageVersion)
    {
        if (packageVersion is null || !packageVersion.StartsWith(PackageVersionPrefix, StringComparison.Ordinal))
            return false;
        var revision = packageVersion.AsSpan(PackageVersionPrefix.Length);
        return revision.Length is > 0 and <= 9 && revision[0] is >= '1' and <= '9' &&
            int.TryParse(revision, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }
}

/// <summary>Resolves approved catalog specifications by stable catalog ID. Unknown IDs fail closed.</summary>
public interface IApprovedCatalogSpecificationSource
{
    IReadOnlyCollection<ApprovedCatalogContract> All { get; }

    bool TryGet(string catalogId, [NotNullWhen(true)] out ApprovedCatalogContract? specification);

    /// <summary>The specification whose package-version prefix matches <paramref name="packageVersion"/>.</summary>
    bool TryGetByPackageVersion(string? packageVersion, [NotNullWhen(true)] out ApprovedCatalogContract? specification);
}

/// <summary>An immutable, duplicate-free set of approved catalog specifications.</summary>
public sealed class ApprovedCatalogSpecificationSet : IApprovedCatalogSpecificationSource
{
    private readonly Dictionary<string, ApprovedCatalogContract> _byId;

    public ApprovedCatalogSpecificationSet(IEnumerable<ApprovedCatalogContract> specifications)
    {
        ArgumentNullException.ThrowIfNull(specifications);
        _byId = new(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var lineages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var specification in specifications)
        {
            ArgumentNullException.ThrowIfNull(specification);
            if (string.IsNullOrWhiteSpace(specification.CatalogId) ||
                string.IsNullOrWhiteSpace(specification.PackageVersionPrefix) ||
                string.IsNullOrWhiteSpace(specification.PackageLineage) ||
                !_byId.TryAdd(specification.CatalogId, specification) ||
                !prefixes.Add(specification.PackageVersionPrefix) ||
                !lineages.Add(specification.PackageLineage))
            {
                throw new ArgumentException("Approved catalog specifications must have distinct identities.", nameof(specifications));
            }
        }
        All = _byId.Values.OrderBy(static item => item.CatalogId, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyCollection<ApprovedCatalogContract> All { get; }

    public bool TryGet(string catalogId, [NotNullWhen(true)] out ApprovedCatalogContract? specification)
    {
        specification = null;
        return catalogId is not null && _byId.TryGetValue(catalogId, out specification);
    }

    public bool TryGetByPackageVersion(string? packageVersion, [NotNullWhen(true)] out ApprovedCatalogContract? specification)
    {
        specification = All.FirstOrDefault(item => item.IsPackageVersion(packageVersion));
        return specification is not null;
    }
}
