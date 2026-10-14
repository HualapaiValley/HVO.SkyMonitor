namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// Catalog proper motion in milliarcseconds per Julian year. Right ascension
/// motion is already multiplied by cos(declination).
/// </summary>
public readonly record struct CatalogProperMotion(
    double RightAscensionCosDeclinationMasPerYear,
    double DeclinationMasPerYear)
{
    /// <summary>Validates both finite components.</summary>
    public void Validate()
    {
        if (!double.IsFinite(RightAscensionCosDeclinationMasPerYear) || !double.IsFinite(DeclinationMasPerYear))
            throw new ArgumentOutOfRangeException(nameof(CatalogProperMotion));
    }
}

/// <summary>Catalog designations retained as published; absent designations are <see langword="null"/>.</summary>
public sealed record CelestialObjectDesignations(
    string? ProperName = null,
    string? Bayer = null,
    string? Flamsteed = null,
    string? Constellation = null,
    string? BayerFlamsteed = null,
    string? HenryDraperId = null,
    string? HarvardRevisedId = null,
    string? GlieseId = null)
{
    /// <summary>The designation set for a catalog that does not retain designations.</summary>
    public static CelestialObjectDesignations None { get; } = new();
}

/// <summary>Versioned optional object details beyond the render-selection record.</summary>
public sealed record CelestialCatalogObjectDetails(
    string Id,
    CatalogProperMotion? ProperMotion,
    CelestialObjectDesignations Designations);

/// <summary>One searchable alias and the catalog object it names. One alias may name several objects.</summary>
public sealed record CelestialCatalogAlias(string Alias, string ObjectId, string Kind);

/// <summary>
/// The versioned coordinate, photometric, and kinematic meaning of a catalog.
/// Consumers must not reinterpret one catalog's values with another's semantics.
/// </summary>
public sealed record CatalogSemantics(
    string CoordinateFrame,
    string CoordinateEquinox,
    string CoordinateEpoch,
    string MagnitudeBand,
    string ColorIndex,
    string? ProperMotionConvention)
{
    /// <summary>
    /// The meaning of a schema that predates explicit semantics: fixed J2000.0
    /// positions, unversioned magnitude and color, and no proper motion.
    /// </summary>
    public static CatalogSemantics LegacyFixedJ2000 { get; } = new(
        "equatorial-j2000", "J2000.0", "J2000.0", "unversioned", "unversioned", null);
}

/// <summary>
/// Optional catalog capability exposing versioned semantics, per-object details,
/// and alias lookup. Positions returned by <see cref="ICelestialCatalog"/> remain
/// at the catalog epoch; proper motion is never applied implicitly.
/// </summary>
public interface ICelestialCatalogDetailsSource
{
    /// <summary>Gets the catalog's declared coordinate, photometric, and kinematic meaning.</summary>
    CatalogSemantics Semantics { get; }

    /// <summary>Returns details for a catalog object, or <see langword="false"/> when the ID is unknown.</summary>
    bool TryGetDetails(string objectId, out CelestialCatalogObjectDetails details);

    /// <summary>Returns every object a designation names, case-insensitively, ordered by object ID.</summary>
    IReadOnlyList<CelestialCatalogAlias> FindByAlias(string designation);
}

/// <summary>
/// Rigorous linear space-motion propagation of a catalog direction on the unit
/// sphere. Opt-in only: scenes and catalog queries never call this implicitly.
/// </summary>
public static class ProperMotionPropagation
{
    private const double MasToRadians = Math.PI / (180d * 3600d * 1000d);

    /// <summary>
    /// Propagates a J2000.0-epoch direction by <paramref name="julianYears"/>
    /// years, returning right ascension in hours [0, 24) and declination in degrees.
    /// </summary>
    public static (double RightAscensionHours, double DeclinationDegrees) Propagate(
        double rightAscensionHours,
        double declinationDegrees,
        CatalogProperMotion properMotion,
        double julianYears)
    {
        properMotion.Validate();
        if (!double.IsFinite(rightAscensionHours) || rightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(declinationDegrees) || declinationDegrees is < -90 or > 90 ||
            !double.IsFinite(julianYears))
        {
            throw new ArgumentOutOfRangeException(nameof(rightAscensionHours));
        }

        var alpha = rightAscensionHours * Math.PI / 12d;
        var delta = declinationDegrees * Math.PI / 180d;
        var (sinAlpha, cosAlpha) = Math.SinCos(alpha);
        var (sinDelta, cosDelta) = Math.SinCos(delta);
        var muAlpha = properMotion.RightAscensionCosDeclinationMasPerYear * MasToRadians * julianYears;
        var muDelta = properMotion.DeclinationMasPerYear * MasToRadians * julianYears;

        var x = cosDelta * cosAlpha - muAlpha * sinAlpha - muDelta * sinDelta * cosAlpha;
        var y = cosDelta * sinAlpha + muAlpha * cosAlpha - muDelta * sinDelta * sinAlpha;
        var z = sinDelta + muDelta * cosDelta;
        var norm = Math.Sqrt(x * x + y * y + z * z);
        x /= norm;
        y /= norm;
        z /= norm;

        var raHours = Math.Atan2(y, x) * 12d / Math.PI;
        if (raHours < 0) raHours += 24d;
        if (raHours >= 24d) raHours -= 24d;
        var decDegrees = Math.Asin(Math.Clamp(z, -1d, 1d)) * 180d / Math.PI;
        return (raHours, decDegrees);
    }
}
