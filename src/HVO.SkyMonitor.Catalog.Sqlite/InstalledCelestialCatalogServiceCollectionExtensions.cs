using HVO.SkyMonitor.Astronomy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Registers the validated celestial catalog installed on the local host.</summary>
public static partial class InstalledCelestialCatalogServiceCollectionExtensions
{
    private const string CatalogRootKey = "Catalog:Root";
    private const string RequiredCatalogIdKey = "Catalog:RequiredCatalogId";
    private const string RequiredPackageKindKey = "Catalog:RequiredPackageKind";
    private const string RequiredPackageVersionKey = "Catalog:RequiredPackageVersion";

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

            var requiredCatalogId = configuration[RequiredCatalogIdKey];
            if (string.IsNullOrWhiteSpace(requiredCatalogId))
            {
                throw new InvalidOperationException($"Configuration value '{RequiredCatalogIdKey}' is required.");
            }

            var packageKind = ParseRequiredPackageKind(configuration[RequiredPackageKindKey]);
            var result = CatalogSnapshotResolver.Resolve(new CatalogSnapshotResolverOptions(installRoot, requiredCatalogId)
            {
                ExpectedPackageKind = packageKind,
                ExpectedPackageVersion = configuration[RequiredPackageVersionKey]
            });

            var logger = serviceProvider.GetRequiredService<ILogger<SqliteCelestialCatalog>>();
            if (logger.IsEnabled(LogLevel.Information))
            {
                CatalogSnapshotResolved(
                    logger,
                    result.PackageKind,
                    result.CatalogId,
                    result.CatalogIdentitySource,
                    result.CatalogVersion,
                    result.SchemaVersion,
                    result.PreprocessingVersion,
                    result.DatabaseSha256,
                    result.RowCount);
            }
            return result;
        });
        services.AddSingleton(static serviceProvider =>
            serviceProvider.GetRequiredService<CatalogSnapshotResult>().Catalog);
        services.AddSingleton<ICelestialCatalog>(static serviceProvider =>
        {
            var stars = serviceProvider.GetRequiredService<SqliteCelestialCatalog>();
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();
            if (stars.Options.ExpectedSchemaVersion == "5")
                throw new InvalidOperationException("The star selection cannot be a standalone deep-sky catalog.");
            var enabled = configuration["DeepSkyCatalog:Enabled"];
            if (enabled is not null && !bool.TryParse(enabled, out _))
                throw new InvalidOperationException("DeepSkyCatalog:Enabled must be true or false.");
            if (!string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase)) return stars;
            var root = configuration["DeepSkyCatalog:Root"];
            var id = configuration["DeepSkyCatalog:RequiredCatalogId"];
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(id))
                throw new InvalidOperationException("An enabled deep-sky catalog requires its own root and catalog ID.");
            var snapshot = CatalogSnapshotResolver.Resolve(new(root, id)
            {
                ExpectedPackageKind = ParseRequiredPackageKind(configuration["DeepSkyCatalog:RequiredPackageKind"]),
                ExpectedPackageVersion = configuration["DeepSkyCatalog:RequiredPackageVersion"]
            });
            if (snapshot.SchemaVersion != "5" || snapshot.Catalog.DeepSky is null || stars.Options.ExpectedSchemaVersion == "5")
                throw new InvalidOperationException("Independent selection requires a star snapshot and a standalone deep-sky snapshot.");
            return new InstalledCatalogPair(stars, snapshot);
        });
        services.AddSingleton<IHipparcosCatalog>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqliteCelestialCatalog>());
        services.AddSingleton<IAstrometricCatalogSource>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqliteCelestialCatalog>());
        services.AddSingleton<ICelestialCatalogMetadataSource>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqliteCelestialCatalog>());
        services.AddSingleton<ICelestialCatalogDetailsSource>(static serviceProvider =>
            serviceProvider.GetRequiredService<SqliteCelestialCatalog>());
        services.AddSingleton<IDeepSkyCatalogSource>(static serviceProvider =>
            (IDeepSkyCatalogSource)serviceProvider.GetRequiredService<ICelestialCatalog>());
        services.AddSingleton<ICelestialObjectSearch>(static serviceProvider => new CelestialObjectSearch(
            serviceProvider.GetRequiredService<ICelestialCatalogDetailsSource>(),
            serviceProvider.GetRequiredService<IDeepSkyCatalogSource>()));

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
        Message = "Installed celestial catalog snapshot {Kind}: identity {CatalogId} ({CatalogIdentitySource}), catalog {CatalogVersion}, schema {SchemaVersion}, preprocessing {PreprocessingVersion}, database SHA-256 {DatabaseSha256}, rows {RowCount}")]
    private static partial void CatalogSnapshotResolved(
        ILogger logger,
        CatalogSnapshotPackageKind kind,
        string catalogId,
        string catalogIdentitySource,
        string catalogVersion,
        string schemaVersion,
        string preprocessingVersion,
        string databaseSha256,
        long rowCount);
}
