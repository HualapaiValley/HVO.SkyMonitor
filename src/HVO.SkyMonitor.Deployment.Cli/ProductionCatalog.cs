using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static class ProductionCatalog
{
    public const string CatalogId = "hyg-v42-production";
    public const string PackageVersion = "hyg-v4.2-p3-s2-r1";
    public const string DatabaseSha256 = "b51d18b722199e89aa8fe4622ebe507346c75effb375e546881452a263f0b9e2";
    public const long DatabaseLength = 9_302_016;
    public const long RowCount = 119_625;

    public static CatalogSnapshotResolverOptions ResolverOptions(string root, string? packageVersion = null) => new(root, CatalogId)
    {
        ExpectedManifestVersion = 2,
        ExpectedPackageKind = CatalogSnapshotPackageKind.Production,
        ExpectedSchemaVersion = "2",
        ExpectedPreprocessingVersion = "3",
        ExpectedPackageVersion = packageVersion
    };

    public static CatalogInstallationIdentity ToIdentity(CatalogSnapshotResult snapshot, string installRoot)
    {
        var databaseSha256 = Convert.ToHexStringLower(Convert.FromHexString(snapshot.DatabaseSha256));
        if (databaseSha256 != DatabaseSha256 ||
            snapshot.DatabaseLength != DatabaseLength || snapshot.RowCount != RowCount)
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
}
