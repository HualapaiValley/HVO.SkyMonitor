using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Catalog.Sqlite;

/// <summary>Independent immutable selections; star queries retain the selected star catalog's semantics.</summary>
public sealed class InstalledCatalogPair(SqliteCelestialCatalog stars, CatalogSnapshotResult deepSkySnapshot) :
    ICelestialCatalog, IHipparcosCatalog, ICelestialCatalogMetadataSource, ICelestialCatalogDetailsSource, IDeepSkyCatalogSource, IAstrometricCatalogSource
{
    public CatalogMetadata Metadata => stars.Metadata;
    public string PreprocessingVersion => stars.PreprocessingVersion;
    public string? CatalogId => stars.CatalogId;
    public string? CatalogPackageVersion => stars.CatalogPackageVersion;
    public CatalogSemantics Semantics => stars.Semantics;
    public CatalogSnapshotResult DeepSkySnapshot => deepSkySnapshot;
    public IDeepSkyCatalog? DeepSky => deepSkySnapshot.Catalog.DeepSky;

    public ValueTask<AstrometricCatalogData> ReadAsync(double maximumMagnitude, int maximumEntries, CancellationToken cancellationToken = default) =>
        stars.ReadAsync(maximumMagnitude, maximumEntries, cancellationToken);

    public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => stars.Query(query);
    public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(CatalogCandidateQuery query,
        CancellationToken cancellationToken = default) => stars.QueryCandidatesAsync(query, cancellationToken);
    public ValueTask<IReadOnlyList<CelestialCatalogObject>> GetByHipparcosIdsAsync(IReadOnlyCollection<string> hipparcosIds,
        CancellationToken cancellationToken = default) => stars.GetByHipparcosIdsAsync(hipparcosIds, cancellationToken);
    public bool TryGetDetails(string objectId, out CelestialCatalogObjectDetails details) => stars.TryGetDetails(objectId, out details);
    public IReadOnlyList<CelestialCatalogAlias> FindByAlias(string designation) => stars.FindByAlias(designation);
}
