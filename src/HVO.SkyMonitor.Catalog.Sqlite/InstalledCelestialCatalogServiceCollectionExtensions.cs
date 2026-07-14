using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Registers the validated celestial catalog installed on the local host.</summary>
public static partial class InstalledCelestialCatalogServiceCollectionExtensions
{
    private const string CatalogRootKey = "Catalog:Root";
    private const string RequiredPackageKindKey = "Catalog:RequiredPackageKind";

    /// <summary>
    /// Registers one lazily resolved installed snapshot and maps all catalog contracts to its immutable catalog.
    /// </summary>
    public static IServiceCollection AddInstalledCelestialCatalog(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(static serviceProvider =>
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            var installRoot = configuration[CatalogRootKey];
            if (string.IsNullOrWhiteSpace(installRoot))
            {
                throw new InvalidOperationException($"Configuration value '{CatalogRootKey}' is required.");
            }

            var packageKind = ParseRequiredPackageKind(configuration[RequiredPackageKindKey]);
            var result = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(installRoot)
            {
                ExpectedPackageKind = packageKind
            });

            CatalogSnapshotResolved(
                serviceProvider.GetRequiredService<ILogger<SqliteCelestialCatalog>>(),
                result.PackageKind,
                result.CatalogVersion,
                result.SchemaVersion,
                result.PreprocessingVersion,
                result.DatabaseSha256,
                result.RowCount);
            return result;
        });
        services.AddSingleton(static serviceProvider =>
            serviceProvider.GetRequiredService<CatalogSnapshotResult>().Catalog);
        services.AddSingleton<ICelestialCatalog>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqliteCelestialCatalog>());
        services.AddSingleton<IHipparcosCatalog>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqliteCelestialCatalog>());
        services.AddSingleton<ICelestialCatalogMetadataSource>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqliteCelestialCatalog>());

        return services;
    }

    private static CatalogSnapshotPackageKind ParseRequiredPackageKind(string? value)
    {
        if (string.Equals(value, nameof(CatalogSnapshotPackageKind.Production), StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSnapshotPackageKind.Production;
        }
        if (string.Equals(value, nameof(CatalogSnapshotPackageKind.Fixture), StringComparison.OrdinalIgnoreCase))
        {
            return CatalogSnapshotPackageKind.Fixture;
        }

        throw new InvalidOperationException(
            $"Configuration value '{RequiredPackageKindKey}' must be 'Production' or 'Fixture'.");
    }

    [LoggerMessage(
        EventId = 3000,
        EventName = "InstalledCatalogSnapshotResolved",
        Level = LogLevel.Information,
        Message = "Installed celestial catalog snapshot {Kind}: catalog {CatalogVersion}, schema {SchemaVersion}, preprocessing {PreprocessingVersion}, database SHA-256 {DatabaseSha256}, rows {RowCount}")]
    private static partial void CatalogSnapshotResolved(
        ILogger logger,
        CatalogSnapshotPackageKind kind,
        string catalogVersion,
        string schemaVersion,
        string preprocessingVersion,
        string databaseSha256,
        long rowCount);
}
