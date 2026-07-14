namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Immutable validation requirements for a catalog snapshot.</summary>
public sealed record SqliteCelestialCatalogOptions(
    string DatabasePath,
    string ExpectedSha256,
    string ExpectedSchemaVersion,
    string ExpectedPreprocessingVersion,
    long? ExpectedRowCount = null,
    string? ExpectedCatalogVersion = null);
