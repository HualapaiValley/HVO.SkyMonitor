namespace HVO.SkyMonitor.Astronomy;

/// <summary>The catalog collection a search match belongs to.</summary>
public enum CelestialSearchCollection
{
    /// <summary>The fixed-star collection.</summary>
    Star,

    /// <summary>The deep-sky collection of a composed catalog.</summary>
    DeepSky
}

/// <summary>
/// One object a designation names. <paramref name="Kind"/> is the alias kind within its collection. A disputed match
/// finds the object but must be flagged wherever it is shown, and is never used as the object's display name.
/// </summary>
public sealed record CelestialSearchMatch(
    CelestialSearchCollection Collection,
    string ObjectId,
    string Alias,
    string Kind,
    bool Disputed);

/// <summary>Resolves a designation to the catalog objects it names, across every installed collection.</summary>
public interface ICelestialObjectSearch
{
    /// <summary>
    /// Returns every object a designation names, case-insensitively: star matches first, then deep-sky matches, each
    /// ordered by object ID and then alias.
    /// </summary>
    IReadOnlyList<CelestialSearchMatch> Find(string designation);
}

/// <summary>
/// Searches the star aliases and, when the catalog has one, the deep-sky aliases. Each object is one entry in its own
/// collection, so a deep-sky duplicate row resolves to the single live object it duplicates rather than to a second
/// entry.
/// </summary>
public sealed class CelestialObjectSearch : ICelestialObjectSearch
{
    private readonly ICelestialCatalogDetailsSource _stars;
    private readonly IDeepSkyCatalogSource? _deepSky;

    /// <summary>Creates a search over a catalog's star details and optional deep-sky collection.</summary>
    public CelestialObjectSearch(ICelestialCatalogDetailsSource stars, IDeepSkyCatalogSource? deepSky)
    {
        ArgumentNullException.ThrowIfNull(stars);
        _stars = stars;
        _deepSky = deepSky;
    }

    /// <inheritdoc />
    public IReadOnlyList<CelestialSearchMatch> Find(string designation)
    {
        ArgumentNullException.ThrowIfNull(designation);
        var stars = _stars.FindByAlias(designation);
        var deepSky = _deepSky?.DeepSky?.FindByAlias(designation) ?? [];
        var result = new CelestialSearchMatch[stars.Count + deepSky.Count];
        for (var index = 0; index < stars.Count; index++)
        {
            var item = stars[index];
            result[index] = new CelestialSearchMatch(CelestialSearchCollection.Star, item.ObjectId, item.Alias, item.Kind, false);
        }
        for (var index = 0; index < deepSky.Count; index++)
        {
            var item = deepSky[index];
            result[stars.Count + index] = new CelestialSearchMatch(
                CelestialSearchCollection.DeepSky, item.ObjectId, item.Alias, item.Kind, item.IsDisputed);
        }
        return result;
    }
}
