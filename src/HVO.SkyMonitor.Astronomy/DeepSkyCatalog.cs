using System.Globalization;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>
/// One deep-sky object from an OpenNGC-derived catalog. Positions are J2000.0 in the catalog's declared frame. The axes
/// are in arcminutes; the position angle runs from north through east in [0, 180). Each absent upstream value is
/// <see langword="null"/>, never zero.
/// </summary>
/// <param name="Id">The OpenNGC name, for example <c>NGC0224</c>; it is the stable key.</param>
/// <param name="Designation">The catalog designation as displayed, for example <c>NGC 224</c>.</param>
/// <param name="DisplayName">The Messier designation when the object has one, otherwise <paramref name="Designation"/>.</param>
/// <param name="ObjectType">The OpenNGC object type code, one of <see cref="DeepSkyObjectTypes.All"/>.</param>
/// <param name="RightAscensionHours">Right ascension in hours, [0, 24).</param>
/// <param name="DeclinationDegrees">Declination in degrees, [-90, 90].</param>
/// <param name="Constellation">The OpenNGC constellation abbreviation.</param>
/// <param name="MajorAxisArcminutes">The major axis, or <see langword="null"/> when unknown.</param>
/// <param name="MinorAxisArcminutes">The minor axis, present only with a major axis it does not exceed.</param>
/// <param name="PositionAngleDegrees">The major axis position angle, or <see langword="null"/> when unknown.</param>
/// <param name="BMagnitude">The B magnitude, or <see langword="null"/>.</param>
/// <param name="VMagnitude">The V magnitude, or <see langword="null"/>.</param>
/// <param name="SurfaceBrightness">The mean B surface brightness within the 25 mag isophote, or <see langword="null"/>.</param>
/// <param name="HubbleType">The morphological type, or <see langword="null"/>.</param>
/// <param name="MessierNumber">The Messier number, or <see langword="null"/>.</param>
/// <param name="CaldwellNumber">The Caldwell number, or <see langword="null"/>.</param>
/// <param name="HipparcosId">The canonical Hipparcos number of a stellar object, or <see langword="null"/>.</param>
/// <param name="CommonName">The first upstream common name, or <see langword="null"/>.</param>
public sealed record DeepSkyObject(
    string Id,
    string Designation,
    string DisplayName,
    string ObjectType,
    double RightAscensionHours,
    double DeclinationDegrees,
    string Constellation,
    double? MajorAxisArcminutes,
    double? MinorAxisArcminutes,
    double? PositionAngleDegrees,
    double? BMagnitude,
    double? VMagnitude,
    double? SurfaceBrightness,
    string? HubbleType,
    int? MessierNumber,
    int? CaldwellNumber,
    string? HipparcosId,
    string? CommonName)
{
    /// <summary>
    /// Gets whether the catalog gives an oriented ellipse: both axes, unequal or equal, and a position angle. An
    /// object with only a major axis, or with unequal axes and no position angle, is drawn as a size-qualified glyph.
    /// </summary>
    public bool HasOrientedExtent => MajorAxisArcminutes is not null && MinorAxisArcminutes is not null &&
        (PositionAngleDegrees is not null || MajorAxisArcminutes == MinorAxisArcminutes);

    /// <summary>Gets whether the object is a single or double star that the catalog carries for identity only.</summary>
    public bool IsStellar => DeepSkyObjectTypes.IsStellar(ObjectType);
}

/// <summary>The OpenNGC object type codes a deep-sky catalog may contain.</summary>
public static class DeepSkyObjectTypes
{
    /// <summary>A single star.</summary>
    public const string Star = "*";

    /// <summary>A double star.</summary>
    public const string DoubleStar = "**";

    /// <summary>Every supported type code, in ordinal order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        "*", "**", "*Ass", "Cl+N", "DrkN", "EmN", "G", "GCl", "GGroup", "GPair", "GTrpl", "HII", "Neb", "Nova", "OCl",
        "Other", "PN", "RfN", "SNR"
    ];

    /// <summary>Returns whether a type code is supported.</summary>
    public static bool IsSupported(string objectType) => All.Contains(objectType, StringComparer.Ordinal);

    /// <summary>Returns whether a type code names a single or double star.</summary>
    public static bool IsStellar(string objectType) => objectType is Star or DoubleStar;
}

/// <summary>The kinds of deep-sky alias, and the meaning of each.</summary>
public static class DeepSkyAliasKinds
{
    /// <summary>The object's own catalog designation.</summary>
    public const string Designation = "designation";

    /// <summary>The object's Messier designation.</summary>
    public const string Messier = "messier";

    /// <summary>The object's Caldwell designation.</summary>
    public const string Caldwell = "caldwell";

    /// <summary>
    /// A duplicate designation whose identification is disputed. It finds the object but is never displayed for it;
    /// search results and details must flag it.
    /// </summary>
    public const string Disputed = "disputed";

    /// <summary>A duplicate catalog row resolved to the object it duplicates.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>An NGC or IC designation the object's row cross-references that has no row of its own.</summary>
    public const string CrossReference = "cross-reference";

    /// <summary>The Hipparcos designation of a stellar object.</summary>
    public const string Hipparcos = "hipparcos";

    /// <summary>An upstream common name.</summary>
    public const string Common = "common";

    /// <summary>Another upstream catalog identifier.</summary>
    public const string Identifier = "identifier";

    /// <summary>Every supported kind, in ordinal order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [Caldwell, Common, CrossReference, Designation, Disputed, Duplicate, Hipparcos, Identifier, Messier];

    /// <summary>Returns whether a kind is supported.</summary>
    public static bool IsSupported(string kind) => All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>One searchable deep-sky alias and the object it names. One alias may name several objects.</summary>
public sealed record DeepSkyAlias(string Alias, string ObjectId, string Kind)
{
    /// <summary>Gets whether the alias's identification is disputed; such an alias is never displayed.</summary>
    public bool IsDisputed => string.Equals(Kind, DeepSkyAliasKinds.Disputed, StringComparison.Ordinal);
}

/// <summary>The reasons a catalog row has no live object.</summary>
public static class DeepSkyTombstoneReasons
{
    /// <summary>The upstream catalog records that the object does not exist.</summary>
    public const string Nonexistent = "nonexistent";

    /// <summary>A duplicate row whose references reach no live object.</summary>
    public const string DuplicateUnresolved = "duplicate-unresolved";

    /// <summary>A duplicate row whose references reach more than one live object.</summary>
    public const string DuplicateConflict = "duplicate-conflict";

    /// <summary>Every supported reason, in ordinal order.</summary>
    public static IReadOnlyList<string> All { get; } = [DuplicateConflict, DuplicateUnresolved, Nonexistent];
}

/// <summary>
/// A catalog name that resolves to no live object. <paramref name="Candidates"/> lists, in ordinal order, the live
/// objects a conflicting duplicate reaches, and is empty for every other reason.
/// </summary>
public sealed record DeepSkyTombstone(string Id, string Designation, string Reason, IReadOnlyList<string> Candidates);

/// <summary>One outline vertex: J2000.0 right ascension in degrees, [0, 360), and declination in degrees.</summary>
public readonly record struct DeepSkyOutlinePoint(double RightAscensionDegrees, double DeclinationDegrees);

/// <summary>
/// One closed outline ring. The first and last points are equal, no point repeats its predecessor, and a ring has at
/// least four points. Consecutive points are joined by great-circle arcs.
/// </summary>
public sealed record DeepSkyOutlineRing(IReadOnlyList<DeepSkyOutlinePoint> Points);

/// <summary>
/// One object's outline at one contour level. Level 1 is the widest contour, 2 the standard and 3 the narrowest; an
/// object has at most one outline per level.
/// </summary>
public sealed record DeepSkyOutline(string ObjectId, int Level, IReadOnlyList<DeepSkyOutlineRing> Rings)
{
    /// <summary>The widest contour level.</summary>
    public const int WidestLevel = 1;

    /// <summary>The narrowest contour level.</summary>
    public const int NarrowestLevel = 3;
}

/// <summary>
/// The declared meaning and provenance of a deep-sky collection. Consumers must not reinterpret one catalog's values
/// with another's conventions.
/// </summary>
public sealed record DeepSkySemantics(
    string SourceName,
    string SourceVersion,
    string SourceCommit,
    Uri SourceUrl,
    string License,
    string CoordinateFrame,
    string CoordinateEpoch,
    string AxisUnit,
    string PositionAngleConvention,
    string OutlineLevelConvention,
    string SurfaceBrightnessUnit);

/// <summary>A validated, immutable deep-sky collection: objects, aliases, tombstones and outlines.</summary>
public interface IDeepSkyCatalog
{
    /// <summary>Gets the collection's declared meaning and provenance.</summary>
    DeepSkySemantics Semantics { get; }

    /// <summary>Gets every live object, ordered by ordinal ID.</summary>
    IReadOnlyList<DeepSkyObject> Objects { get; }

    /// <summary>Gets the number of alias rows.</summary>
    int AliasCount { get; }

    /// <summary>Gets every tombstone, ordered by ordinal ID.</summary>
    IReadOnlyList<DeepSkyTombstone> Tombstones { get; }

    /// <summary>Returns a live object by ID, or <see langword="false"/> when the ID is unknown or tombstoned.</summary>
    bool TryGetObject(string objectId, out DeepSkyObject value);

    /// <summary>Returns an object's outlines ordered by level, or an empty list when it has none.</summary>
    IReadOnlyList<DeepSkyOutline> GetOutlines(string objectId);

    /// <summary>Returns every object a designation names, case-insensitively, ordered by object ID and then alias.</summary>
    IReadOnlyList<DeepSkyAlias> FindByAlias(string designation);

    /// <summary>Returns a tombstone by ID, or <see langword="false"/> when the ID is live or unknown.</summary>
    bool TryGetTombstone(string id, out DeepSkyTombstone tombstone);
}

/// <summary>
/// Optional catalog capability exposing the deep-sky collection of a composed catalog. A catalog without one returns
/// <see langword="null"/>.
/// </summary>
public interface IDeepSkyCatalogSource
{
    /// <summary>Gets the deep-sky collection, or <see langword="null"/> when the catalog has none.</summary>
    IDeepSkyCatalog? DeepSky { get; }
}

/// <summary>
/// The storage-neutral validated deep-sky collection. Construction proves the invariants every consumer relies on,
/// so a loader only proves that its rows are the ones it was asked to load.
/// </summary>
public sealed class DeepSkyCatalog : IDeepSkyCatalog
{
    /// <summary>The smallest number of points in a closed outline ring.</summary>
    public const int MinimumRingPointCount = 4;

    private readonly DeepSkyObject[] _objects;
    private readonly Dictionary<string, DeepSkyObject> _objectsById;
    private readonly Dictionary<string, DeepSkyOutline[]> _outlinesById;
    private readonly DeepSkyTombstone[] _tombstones;
    private readonly Dictionary<string, DeepSkyTombstone> _tombstonesById;
    private readonly Dictionary<string, DeepSkyAlias[]> _aliases;

    /// <summary>Validates and freezes a deep-sky collection.</summary>
    /// <exception cref="InvalidDataException">The collection breaks an invariant.</exception>
    public DeepSkyCatalog(
        DeepSkySemantics semantics,
        IEnumerable<DeepSkyObject> objects,
        IEnumerable<DeepSkyAlias> aliases,
        IEnumerable<DeepSkyTombstone> tombstones,
        IEnumerable<DeepSkyOutline> outlines)
    {
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(tombstones);
        ArgumentNullException.ThrowIfNull(outlines);
        Semantics = ValidateSemantics(semantics);

        _objects = objects.ToArray();
        Array.Sort(_objects, static (left, right) => string.CompareOrdinal(left?.Id, right?.Id));
        _objectsById = new Dictionary<string, DeepSkyObject>(_objects.Length, StringComparer.Ordinal);
        var designations = new HashSet<string>(StringComparer.Ordinal);
        var displayNames = new HashSet<string>(StringComparer.Ordinal);
        var caldwell = new HashSet<int>();
        var hipparcos = new HashSet<string>(StringComparer.Ordinal);
        // A Messier number fixes the display name, so unique display names also make Messier numbers unique.
        foreach (var item in _objects)
        {
            ValidateObject(item);
            if (!_objectsById.TryAdd(item.Id, item) || !designations.Add(item.Designation) ||
                !displayNames.Add(item.DisplayName) ||
                item.CaldwellNumber is { } c && !caldwell.Add(c) ||
                item.HipparcosId is { } hip && !hipparcos.Add(hip))
            {
                throw new InvalidDataException($"Deep-sky object '{item.Id}' repeats another object's identity.");
            }
        }

        _tombstones = tombstones.ToArray();
        Array.Sort(_tombstones, static (left, right) => string.CompareOrdinal(left?.Id, right?.Id));
        _tombstonesById = new Dictionary<string, DeepSkyTombstone>(_tombstones.Length, StringComparer.Ordinal);
        foreach (var item in _tombstones)
        {
            ValidateTombstone(item);
            if (_objectsById.ContainsKey(item.Id) || !_tombstonesById.TryAdd(item.Id, item))
            {
                throw new InvalidDataException($"Deep-sky tombstone '{item.Id}' names a live or repeated ID.");
            }
        }

        _aliases = BuildAliasIndex(aliases, _objectsById, out var aliasCount);
        AliasCount = aliasCount;
        _outlinesById = BuildOutlineIndex(outlines, _objectsById);
    }

    /// <inheritdoc />
    public DeepSkySemantics Semantics { get; }

    /// <inheritdoc />
    public IReadOnlyList<DeepSkyObject> Objects => _objects;

    /// <inheritdoc />
    public int AliasCount { get; }

    /// <inheritdoc />
    public IReadOnlyList<DeepSkyTombstone> Tombstones => _tombstones;

    /// <summary>Gets the number of objects with at least one outline.</summary>
    public int OutlineObjectCount => _outlinesById.Count;

    /// <summary>Gets the number of (object, level) outlines.</summary>
    public int OutlineCount => _outlinesById.Values.Sum(static item => item.Length);

    /// <summary>Gets the number of outline rings.</summary>
    public int OutlineRingCount => _outlinesById.Values.Sum(static item => item.Sum(static outline => outline.Rings.Count));

    /// <summary>Gets the number of outline points.</summary>
    public long OutlinePointCount => _outlinesById.Values.Sum(static item =>
        item.Sum(static outline => outline.Rings.Sum(static ring => (long)ring.Points.Count)));

    /// <inheritdoc />
    public bool TryGetObject(string objectId, out DeepSkyObject value)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return _objectsById.TryGetValue(objectId, out value!);
    }

    /// <inheritdoc />
    public IReadOnlyList<DeepSkyOutline> GetOutlines(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return _outlinesById.TryGetValue(objectId, out var outlines) ? outlines : [];
    }

    /// <inheritdoc />
    public IReadOnlyList<DeepSkyAlias> FindByAlias(string designation)
    {
        ArgumentNullException.ThrowIfNull(designation);
        return _aliases.TryGetValue(designation.Trim(), out var matches) ? matches : [];
    }

    /// <inheritdoc />
    public bool TryGetTombstone(string id, out DeepSkyTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _tombstonesById.TryGetValue(id, out tombstone!);
    }

    private static DeepSkySemantics ValidateSemantics(DeepSkySemantics value)
    {
        if (!IsText(value.SourceName) || !IsText(value.SourceVersion) || !IsText(value.License) ||
            !IsText(value.CoordinateFrame) || !IsText(value.CoordinateEpoch) || !IsText(value.AxisUnit) ||
            !IsText(value.PositionAngleConvention) || !IsText(value.OutlineLevelConvention) ||
            !IsText(value.SurfaceBrightnessUnit) || value.SourceUrl is not { IsAbsoluteUri: true } ||
            value.SourceCommit is not { Length: 40 } commit ||
            !commit.All(static item => item is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException("Deep-sky semantics must declare every convention and a 40-character commit.");
        }
        return value;
    }

    private static void ValidateObject(DeepSkyObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsText(item.Id) || !IsText(item.Designation) || !IsText(item.DisplayName) || !IsText(item.Constellation) ||
            item.ObjectType is null || !DeepSkyObjectTypes.IsSupported(item.ObjectType) ||
            !double.IsFinite(item.RightAscensionHours) || item.RightAscensionHours is < 0 or >= 24 ||
            !double.IsFinite(item.DeclinationDegrees) || item.DeclinationDegrees is < -90 or > 90 ||
            item.MajorAxisArcminutes is { } major && (!double.IsFinite(major) || major <= 0) ||
            item.MinorAxisArcminutes is { } minor &&
                (!double.IsFinite(minor) || minor <= 0 || item.MajorAxisArcminutes is not { } majorAxis || minor > majorAxis) ||
            item.PositionAngleDegrees is { } angle && (!double.IsFinite(angle) || angle is < 0 or >= 180) ||
            item.BMagnitude is { } b && !double.IsFinite(b) || item.VMagnitude is { } v && !double.IsFinite(v) ||
            item.SurfaceBrightness is { } surface && !double.IsFinite(surface) ||
            item.HubbleType is not null && !IsText(item.HubbleType) || item.CommonName is not null && !IsText(item.CommonName) ||
            item.MessierNumber is <= 0 || item.CaldwellNumber is <= 0 ||
            item.MessierNumber is { } messier &&
                !string.Equals(item.DisplayName, "M" + messier.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            item.MessierNumber is null && !string.Equals(item.DisplayName, item.Designation, StringComparison.Ordinal) ||
            item.HipparcosId is { } hipparcosId &&
                (!int.TryParse(hipparcosId, NumberStyles.None, CultureInfo.InvariantCulture, out var hip) || hip <= 0 ||
                 !string.Equals(hipparcosId, hip.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Deep-sky object '{item.Id}' contains invalid data.");
        }
    }

    private static void ValidateTombstone(DeepSkyTombstone item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsText(item.Id) || !IsText(item.Designation) || item.Reason is null ||
            !DeepSkyTombstoneReasons.All.Contains(item.Reason, StringComparer.Ordinal) || item.Candidates is null ||
            string.Equals(item.Reason, DeepSkyTombstoneReasons.DuplicateConflict, StringComparison.Ordinal) !=
                item.Candidates.Count > 1 ||
            item.Candidates.Any(static candidate => !IsText(candidate)) ||
            item.Candidates.Zip(item.Candidates.Skip(1)).Any(static pair => string.CompareOrdinal(pair.First, pair.Second) >= 0))
        {
            throw new InvalidDataException($"Deep-sky tombstone '{item.Id}' contains invalid data.");
        }
    }

    private static Dictionary<string, DeepSkyAlias[]> BuildAliasIndex(
        IEnumerable<DeepSkyAlias> aliases,
        Dictionary<string, DeepSkyObject> objects,
        out int count)
    {
        var rows = aliases.ToArray();
        foreach (var item in rows)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (!IsText(item.Alias) || item.Kind is null || !DeepSkyAliasKinds.IsSupported(item.Kind) ||
                item.ObjectId is null || !objects.ContainsKey(item.ObjectId))
            {
                throw new InvalidDataException(
                    $"Deep-sky alias '{item.Alias}' must be trimmed, typed, and name a live object.");
            }
        }
        // Each group is ordered by object ID and then alias, matching the star catalog's alias order.
        Array.Sort(rows, static (left, right) =>
        {
            var byObject = string.CompareOrdinal(left.ObjectId, right.ObjectId);
            return byObject != 0 ? byObject : string.CompareOrdinal(left.Alias, right.Alias);
        });
        var grouped = new Dictionary<string, List<DeepSkyAlias>>(StringComparer.OrdinalIgnoreCase);
        var designations = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < rows.Length; index++)
        {
            var item = rows[index];
            if (index > 0 && string.Equals(rows[index - 1].ObjectId, item.ObjectId, StringComparison.Ordinal) &&
                string.Equals(rows[index - 1].Alias, item.Alias, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Deep-sky alias '{item.Alias}' repeats for object '{item.ObjectId}'.");
            }
            if (string.Equals(item.Kind, DeepSkyAliasKinds.Designation, StringComparison.Ordinal))
            {
                if (!string.Equals(objects[item.ObjectId].Designation, item.Alias, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Deep-sky designation alias '{item.Alias}' is not its object's designation.");
                }
                designations.Add(item.ObjectId);
            }
            if (!grouped.TryGetValue(item.Alias, out var matches))
            {
                grouped.Add(item.Alias, matches = []);
            }
            matches.Add(item);
        }
        if (designations.Count != objects.Count)
        {
            throw new InvalidDataException("Every deep-sky object must have its designation as an alias.");
        }

        count = rows.Length;
        var result = new Dictionary<string, DeepSkyAlias[]>(grouped.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, matches) in grouped)
        {
            result.Add(alias, matches.ToArray());
        }
        return result;
    }

    private static Dictionary<string, DeepSkyOutline[]> BuildOutlineIndex(
        IEnumerable<DeepSkyOutline> outlines,
        Dictionary<string, DeepSkyObject> objects)
    {
        var grouped = new Dictionary<string, List<DeepSkyOutline>>(StringComparer.Ordinal);
        foreach (var item in outlines)
        {
            ArgumentNullException.ThrowIfNull(item);
            if (item.ObjectId is null || !objects.ContainsKey(item.ObjectId) ||
                item.Level is < DeepSkyOutline.WidestLevel or > DeepSkyOutline.NarrowestLevel ||
                item.Rings is not { Count: > 0 } || item.Rings.Any(static ring => !IsValidRing(ring)))
            {
                throw new InvalidDataException($"Deep-sky outline for '{item.ObjectId}' contains invalid data.");
            }
            if (!grouped.TryGetValue(item.ObjectId, out var levels))
            {
                grouped.Add(item.ObjectId, levels = []);
            }
            if (levels.Any(level => level.Level == item.Level))
            {
                throw new InvalidDataException($"Deep-sky object '{item.ObjectId}' repeats outline level {item.Level}.");
            }
            levels.Add(item);
        }

        var result = new Dictionary<string, DeepSkyOutline[]>(grouped.Count, StringComparer.Ordinal);
        foreach (var (id, levels) in grouped)
        {
            levels.Sort(static (left, right) => left.Level.CompareTo(right.Level));
            result.Add(id, levels.ToArray());
        }
        return result;
    }

    private static bool IsValidRing(DeepSkyOutlineRing ring)
    {
        if (ring?.Points is not { Count: >= MinimumRingPointCount } points || points[0] != points[^1])
        {
            return false;
        }
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!double.IsFinite(point.RightAscensionDegrees) || point.RightAscensionDegrees is < 0 or >= 360 ||
                !double.IsFinite(point.DeclinationDegrees) || point.DeclinationDegrees is < -90 or > 90 ||
                index > 0 && point == points[index - 1])
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsText(string? value)
        => !string.IsNullOrWhiteSpace(value) && string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
