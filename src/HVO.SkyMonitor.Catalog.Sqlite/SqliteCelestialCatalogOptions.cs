namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Immutable validation requirements for a catalog snapshot.</summary>
/// <param name="ExpectedDeepSkyCounts">
/// The deep-sky table counts a schema-4 snapshot must hold, or <see langword="null"/> to accept any valid collection.
/// It must be <see langword="null"/> for an earlier schema, which has no deep-sky collection.
/// </param>
public sealed record SqliteCelestialCatalogOptions(
    string DatabasePath,
    string ExpectedSha256,
    string ExpectedSchemaVersion,
    string ExpectedPreprocessingVersion,
    long? ExpectedRowCount = null,
    string? ExpectedCatalogVersion = null,
    CatalogDeepSkyCounts? ExpectedDeepSkyCounts = null);
