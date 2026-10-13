using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>How a projected deep-sky object is drawn. Glyph representations are anchored at the object's pixel.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeepSkyRepresentation>))]
public enum DeepSkyRepresentation
{
    /// <summary>A sourced catalog outline, carried in the section's outlines.</summary>
    Outline,

    /// <summary>A catalog ellipse or circle, carried as a <c>resolved-footprint-v1</c> footprint.</summary>
    Footprint,

    /// <summary>A class symbol drawn at the projected major-axis size.</summary>
    SizedGlyph,

    /// <summary>A fixed-size class symbol for an object below the resolvable pixel threshold.</summary>
    MinimumGlyph,

    /// <summary>A fixed-size class symbol for an object whose catalog extent is unknown.</summary>
    UnknownExtentGlyph,

    /// <summary>A star or double-star symbol for a stellar catalog row drawn only for its Messier identity.</summary>
    StellarGlyph
}

/// <summary>The scene budget that moved a deep-sky object to a simpler representation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DeepSkyDegradation>))]
public enum DeepSkyDegradation
{
    /// <summary>The outline count or outline point budget was exhausted.</summary>
    OutlineLimit,

    /// <summary>The shared resolved-footprint budget was exhausted.</summary>
    FootprintLimit
}

/// <summary>
/// Every operator-selected input that changes deep-sky scene content. It is recorded on the scene selection so a
/// retained scene names the bounds it was produced under.
/// </summary>
public sealed record ProjectedSceneDeepSkySelection(
    [property: JsonRequired] int MaximumObjects,
    [property: JsonRequired] int MaximumFootprints,
    [property: JsonRequired] int MaximumOutlines,
    [property: JsonRequired] int PreferredOutlineLevel,
    [property: JsonRequired] double MinimumGlyphPixels,
    [property: JsonRequired] string PresetVersion = CuratedDeepSkyOptions.PresetVersion)
{
    /// <summary>The largest accepted object bound.</summary>
    public const int MaximumObjectLimit = 128;

    /// <summary>The largest accepted outline bound.</summary>
    public const int MaximumOutlineLimit = 0;

    /// <summary>The largest accepted resolvable-size threshold in output pixels.</summary>
    public const double MaximumMinimumGlyphPixels = 64;

    /// <summary>
    /// The default curated selection: up to 128 objects, 32 approximate footprints, no source contours,
    /// and an 8-pixel resolvable threshold.
    /// </summary>
    public static ProjectedSceneDeepSkySelection Default { get; } = new(
        128, 32, 0, DeepSkyOutline.WidestLevel, 8);

    /// <summary>Throws when a bound is outside its accepted range.</summary>
    public void Validate()
    {
        if (!string.Equals(PresetVersion, CuratedDeepSkyOptions.PresetVersion, StringComparison.Ordinal) ||
            MaximumObjects is < 1 or > MaximumObjectLimit ||
            MaximumFootprints is < 0 or > 32 ||
            MaximumOutlines != 0 ||
            PreferredOutlineLevel != DeepSkyOutline.WidestLevel ||
            !double.IsFinite(MinimumGlyphPixels) || MinimumGlyphPixels is <= 0 or > MaximumMinimumGlyphPixels)
            throw new ArgumentException("Deep-sky selection is outside its accepted bounds.", "selection.deepSky");
    }
}

/// <summary>
/// One deep-sky object placed in a scene. It is never a star or reference candidate. The pixel follows the
/// resolved-footprint centre rule: it is present only where the scene would admit a point at that direction.
/// </summary>
public sealed record ProjectedDeepSkyObject(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Designation,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] string ObjectType,
    [property: JsonRequired] string? CommonName,
    [property: JsonRequired] int? MessierNumber,
    [property: JsonRequired] int? CaldwellNumber,
    [property: JsonRequired] bool Featured,
    [property: JsonRequired] EquatorialPoint J2000Equatorial,
    [property: JsonRequired] AltAzPoint GeometricHorizontal,
    [property: JsonRequired] AltAzPoint ApparentHorizontal,
    [property: JsonRequired] PixelPoint? Pixel,
    [property: JsonRequired] double? VisualMagnitude,
    [property: JsonRequired] double? BlueMagnitude,
    [property: JsonRequired] double? MajorAxisArcminutes,
    [property: JsonRequired] double? MinorAxisArcminutes,
    [property: JsonRequired] double? PositionAngleDegrees,
    [property: JsonRequired] double? MajorAxisPixels,
    [property: JsonRequired] DeepSkyRepresentation Representation,
    [property: JsonRequired] int? OutlineLevel,
    [property: JsonRequired] DeepSkyDegradation? Degradation)
{
    /// <summary>The prefix that keeps deep-sky scene IDs apart from star and solar-system IDs.</summary>
    public const string IdPrefix = "deep-sky:";

    /// <summary>Gets the deep-sky catalog object ID this scene ID names.</summary>
    [JsonIgnore]
    public string CatalogObjectId => Id[IdPrefix.Length..];

    /// <summary>Returns the scene ID for a deep-sky catalog object ID.</summary>
    public static string SceneId(string catalogObjectId) => IdPrefix + catalogObjectId;

    /// <summary>Returns whether a catalog object is featured: it has a Messier or Caldwell number, or a common name.</summary>
    public static bool IsFeatured(int? messierNumber, int? caldwellNumber, string? commonName) =>
        messierNumber is not null || caldwellNumber is not null || commonName is not null;

    /// <summary>
    /// Orders placed objects by the rule the scene assigned representations in,
    /// <see cref="ProjectedDeepSky.CurrentAlgorithmVersion"/>, so a consumer that degrades them spends its own budget
    /// in the same order.
    /// </summary>
    public static int ComparePriority(ProjectedDeepSkyObject left, ProjectedDeepSkyObject right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return DeepSkySceneProjector.ComparePriority(
            new DeepSkySceneProjector.PriorityKey(
                left.Featured, left.MajorAxisPixels, left.VisualMagnitude ?? left.BlueMagnitude, left.Id),
            new DeepSkySceneProjector.PriorityKey(
                right.Featured, right.MajorAxisPixels, right.VisualMagnitude ?? right.BlueMagnitude, right.Id));
    }
}

/// <summary>
/// Reserved wire shape for source contours. Curated producers emit an empty collection and readers reject
/// populated contours; expanded contour presentation belongs to a separate delivery.
/// </summary>
public sealed record ProjectedDeepSkyOutline(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string SamplingAlgorithmVersion,
    [property: JsonRequired] int Level,
    [property: JsonRequired] int RingCount,
    [property: JsonRequired] string? RefractionModel,
    [property: JsonRequired] bool Clipped,
    [property: JsonRequired] ResolvedFootprintBounds Bounds,
    [property: JsonRequired] IReadOnlyList<ResolvedFootprintPart> Parts)
{
    public const string CurrentContractVersion = "deep-sky-outline-v1";
    public const string SamplingAlgorithm = "deep-sky-outline-great-circle-adaptive-v1";
    public const int MaximumPartCount = 256;
    public const int MaximumPointCount = 16_384;

    /// <summary>Returns whether parts are exactly one closed part per ring.</summary>
    public static bool IsComplete(IReadOnlyList<ResolvedFootprintPart> parts, int ringCount)
    {
        ArgumentNullException.ThrowIfNull(parts);
        return parts.Count == ringCount && parts.All(static part => part.Closed);
    }
}

/// <summary>
/// The deep-sky collection of a projected scene: OpenNGC attribution, the placed objects in ID order, and their
/// sourced outlines in ID order. Ellipses live in the scene's resolved footprints under the same IDs.
/// </summary>
public sealed record ProjectedDeepSky(
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string AlgorithmVersion,
    [property: JsonRequired] string SourceName,
    [property: JsonRequired] string SourceVersion,
    [property: JsonRequired] string SourceCommit,
    [property: JsonRequired] Uri SourceUrl,
    [property: JsonRequired] string License,
    [property: JsonRequired] int OmittedCandidateCount,
    [property: JsonRequired] IReadOnlyList<ProjectedDeepSkyObject> Objects,
    [property: JsonRequired] IReadOnlyList<ProjectedDeepSkyOutline> Outlines)
{
    public const string CurrentContractVersion = "projected-deep-sky-curated-v1";

    /// <summary>
    /// The canonical preset and object cap are chosen before geometry, in Messier-number/ID order.
    /// Presentation later ranks only the projected subset by featured status, size, known magnitude and ID.
    /// </summary>
    public const string CurrentAlgorithmVersion = "curated-messier-bright-ngc-preselect-v1";

    /// <summary>The largest outline point total one scene carries.</summary>
    public const int MaximumTotalOutlinePointCount = 16_384;
}

/// <summary>Explicit annotation-only opt-in. No contours, detector or raw image synthesis.</summary>
public sealed record CuratedDeepSkyOptions(bool Enabled = false, int MaximumObjects = 128,
    int MaximumFootprints = 32, double MinimumGlyphPixels = 8)
{
    public const string PresetVersion = "messier-bright-ngc-v1";

    public ProjectedSceneDeepSkySelection ToSelection()
    {
        var value = new ProjectedSceneDeepSkySelection(MaximumObjects, MaximumFootprints, 0,
            DeepSkyOutline.WidestLevel, MinimumGlyphPixels);
        value.Validate();
        return value;
    }
}

/// <summary>Resolved once per immutable catalog instance. No per-frame query or contour traversal.</summary>
public sealed class CuratedDeepSkySelectionPlan
{
    private static readonly ConditionalWeakTable<IDeepSkyCatalog, Lazy<CuratedDeepSkySelectionPlan>> Cache = new();
    private static readonly string[] BrightNgc = ["NGC7000", "NGC1499", "NGC6960", "NGC6992", "NGC7635", "NGC0869", "NGC0884", "NGC7789"];

    private CuratedDeepSkySelectionPlan(IDeepSkyCatalog catalog)
    {
        var selected = new Dictionary<string, DeepSkyObject>(StringComparer.Ordinal);
        var unresolved = new List<string>();
        var aliases = Enumerable.Range(1, 110).Select(static number => "M" + number.ToString(CultureInfo.InvariantCulture))
            .Concat(BrightNgc);
        foreach (var alias in aliases)
        {
            var ids = catalog.FindByAlias(alias).Where(static row => !row.IsDisputed)
                .Select(static row => row.ObjectId).Distinct(StringComparer.Ordinal).ToArray();
            DeepSkyObject? item = null;
            if (ids.Length == 1 && catalog.TryGetObject(ids[0], out var resolved)) item = resolved;
            else if (ids.Length == 0 && catalog.TryGetObject(alias, out var direct)) item = direct;
            if (item is null || item.IsStellar && item.MessierNumber is null)
            {
                unresolved.Add(alias);
                continue;
            }
            selected.TryAdd(item.Id, item);
        }
        // The preset and cap are decided BEFORE any view-dependent geometry work.
        Objects = Array.AsReadOnly(selected.Values.OrderBy(static item => item.MessierNumber ?? int.MaxValue)
            .ThenBy(static item => item.Id, StringComparer.Ordinal).ToArray());
        UnresolvedAliases = Array.AsReadOnly(unresolved.ToArray());
    }

    public IReadOnlyList<DeepSkyObject> Objects { get; }
    public IReadOnlyList<string> UnresolvedAliases { get; }
    public static CuratedDeepSkySelectionPlan For(IDeepSkyCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return Cache.GetValue(catalog, static value => new Lazy<CuratedDeepSkySelectionPlan>(
            () => new CuratedDeepSkySelectionPlan(value), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}

/// <summary>Projects only the cached curated preset, preserving unknown catalog values.</summary>
internal static class DeepSkySceneProjector
{
    private const double PlateScaleProbeRadians = 1e-5;

    public static (ProjectedDeepSky Section, IReadOnlyList<ProjectedResolvedFootprint> Footprints) Project(
        VisibleScene scene, IDeepSkyCatalog catalog, ProjectedSceneDeepSkySelection selection)
    {
        var request = scene.Request;
        var plan = CuratedDeepSkySelectionPlan.For(catalog);
        var projector = ProjectorFactory.Create(request.Projection);
        var basis = CameraBasis.Create(request.Projection.BoresightAltitudeDegrees,
            request.Projection.BoresightAzimuthDegrees, request.Projection.RollDegrees, request.Projection.HorizontalFlip);
        var boresight = CameraBasis.FromHorizontal(new AltAzPoint(
            request.Projection.BoresightAltitudeDegrees, request.Projection.BoresightAzimuthDegrees));
        var footprintBudget = Math.Min(selection.MaximumFootprints,
            ProjectedSceneJson.MaximumResolvedFootprintCount - scene.ResolvedFootprints.Count);
        var footprints = new List<ProjectedResolvedFootprint>();
        var objects = new List<ProjectedDeepSkyObject>();
        var source = $"{catalog.Semantics.SourceName} {catalog.Semantics.SourceVersion}";
        foreach (var item in plan.Objects.Take(selection.MaximumObjects))
        {
            var j2000 = new EquatorialPoint(item.RightAscensionHours, item.DeclinationDegrees);
            var geometric = CoordinateTransforms.EquatorialToHorizontal(
                EquatorialPrecession.PrecessJ2000(j2000, request.Utc), request.Utc,
                request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
            var apparent = geometric with
            {
                AltitudeDegrees = AtmosphericRefraction.Apply(geometric.AltitudeDegrees, request.Refraction)
            };
            var pixel = request.HorizonPolicy == HorizonPolicy.GeometricHorizon && geometric.AltitudeDegrees < 0
                ? null : projector.Project(apparent);
            if (pixel is null && !IsDrawableExtent(item)) continue;
            // Preserve a limb that crosses the field even when its centre is outside.
            if (pixel is null && !request.Refraction.Enabled &&
                Math.Acos(Math.Clamp(EnuVector.Dot(boresight, CameraBasis.FromHorizontal(geometric)), -1d, 1d)) * 180d / Math.PI >
                VisibleSceneBuilder.OpticalRadiusDegrees(request.Projection) + item.MajorAxisArcminutes!.Value / 120d + 1e-4)
                continue;
            var size = item.MajorAxisArcminutes is { } major
                ? MeasureMajorAxisPixels(request, basis, geometric, major) : null;
            var id = ProjectedDeepSkyObject.SceneId(item.Id);
            var representation = GlyphRepresentation(item.ObjectType, item.MajorAxisArcminutes, size, selection.MinimumGlyphPixels);
            DeepSkyDegradation? degradation = null;
            if (!item.IsStellar && IsDrawableExtent(item) && (size is null || size >= selection.MinimumGlyphPixels))
            {
                if (footprints.Count < footprintBudget)
                {
                    var footprint = ResolvedFootprintSampler.Sample(request, id, item.DisplayName,
                        ResolvedFootprintSourceKind.DeepSkyObject, CreateExtent(item, source, request.Utc), geometric, null);
                    if (footprint is not null)
                    {
                        footprints.Add(footprint);
                        representation = DeepSkyRepresentation.Footprint;
                    }
                }
                else degradation = DeepSkyDegradation.FootprintLimit;
            }
            if (pixel is null && representation != DeepSkyRepresentation.Footprint) continue;
            objects.Add(new(id, item.Designation, item.DisplayName, item.ObjectType, item.CommonName,
                item.MessierNumber, item.CaldwellNumber,
                ProjectedDeepSkyObject.IsFeatured(item.MessierNumber, item.CaldwellNumber, item.CommonName),
                j2000, geometric, apparent, pixel, item.VMagnitude, item.BMagnitude, item.MajorAxisArcminutes,
                item.MinorAxisArcminutes, item.PositionAngleDegrees, size, representation, null, degradation));
        }
        var semantics = catalog.Semantics;
        return (new(ProjectedDeepSky.CurrentContractVersion, ProjectedDeepSky.CurrentAlgorithmVersion,
            semantics.SourceName, semantics.SourceVersion, semantics.SourceCommit, semantics.SourceUrl, semantics.License,
            Math.Max(0, plan.Objects.Count - selection.MaximumObjects),
            Freeze(objects.OrderBy(static item => item.Id, StringComparer.Ordinal)), []), footprints);
    }

    internal static bool IsDrawableExtent(DeepSkyObject item) => item.HasOrientedExtent &&
        item.MajorAxisArcminutes is > 0 and <= 1200 && item.MinorAxisArcminutes is > 0;

    internal static DeepSkyRepresentation GlyphRepresentation(string objectType, double? majorAxisArcminutes,
        double? majorAxisPixels, double minimumGlyphPixels) => DeepSkyObjectTypes.IsStellar(objectType)
        ? DeepSkyRepresentation.StellarGlyph : majorAxisArcminutes is null
            ? DeepSkyRepresentation.UnknownExtentGlyph : majorAxisPixels is { } size && size >= minimumGlyphPixels
                ? DeepSkyRepresentation.SizedGlyph : DeepSkyRepresentation.MinimumGlyph;
    private static ResolvedFootprintExtent CreateExtent(DeepSkyObject item, string source, DateTimeOffset utc)
    {
        var major = item.MajorAxisArcminutes!.Value / 120d;
        var minor = item.MinorAxisArcminutes!.Value / 120d;
        return major == minor
            ? ResolvedFootprintExtent.Circle(major, source)
            : new ResolvedFootprintExtent(ResolvedFootprintShape.Ellipse, major, minor,
                PositionAngleOfDate(item.RightAscensionHours, item.DeclinationDegrees, item.PositionAngleDegrees!.Value, utc),
                null, null, null, source);
    }

    /// <summary>
    /// Rotates a catalogue position angle, measured from J2000 north, to north of date, the frame a resolved
    /// footprint's position angle uses. Precession is a rotation, so the bearing from the precessed centre to a
    /// precessed point one degree along the J2000 axis is the axis' position angle of date. Near the pole the two
    /// differ by about 0.9 degrees at declination 80 in 2026, too much to ignore on an outline.
    /// </summary>
    internal static double PositionAngleOfDate(
        double rightAscensionHours,
        double declinationDegrees,
        double positionAngleDegrees,
        DateTimeOffset utc)
    {
        const double Step = Math.PI / 180d;
        var rightAscension = rightAscensionHours * Math.PI / 12d;
        var declination = declinationDegrees * Math.PI / 180d;
        var angle = positionAngleDegrees * Math.PI / 180d;
        var offsetDeclination = Math.Asin(Math.Clamp(
            Math.Sin(declination) * Math.Cos(Step) + Math.Cos(declination) * Math.Sin(Step) * Math.Cos(angle), -1d, 1d));
        var offsetRightAscension = rightAscension + Math.Atan2(
            Math.Sin(angle) * Math.Sin(Step) * Math.Cos(declination),
            Math.Cos(Step) - Math.Sin(declination) * Math.Sin(offsetDeclination));
        var center = EquatorialPrecession.PrecessJ2000(new EquatorialPoint(rightAscensionHours, declinationDegrees), utc);
        var offset = EquatorialPrecession.PrecessJ2000(new EquatorialPoint(
            NormalizeHours(offsetRightAscension * 12d / Math.PI), offsetDeclination * 180d / Math.PI), utc);
        return Bearing(center, offset);
    }

    /// <summary>Returns the position angle, in [0, 180), of the great circle from one point toward another.</summary>
    internal static double Bearing(EquatorialPoint from, EquatorialPoint to)
    {
        var fromDeclination = from.DeclinationDegrees * Math.PI / 180d;
        var toDeclination = to.DeclinationDegrees * Math.PI / 180d;
        var difference = (to.RightAscensionHours - from.RightAscensionHours) * Math.PI / 12d;
        var degrees = Math.Atan2(
            Math.Sin(difference) * Math.Cos(toDeclination),
            Math.Cos(fromDeclination) * Math.Sin(toDeclination) -
                Math.Sin(fromDeclination) * Math.Cos(toDeclination) * Math.Cos(difference)) * 180d / Math.PI % 180d;
        if (degrees < 0) degrees += 180d;
        return degrees >= 180d ? 0 : degrees;
    }

    private static double NormalizeHours(double hours)
    {
        var normalized = hours % 24d;
        if (normalized < 0) normalized += 24d;
        return normalized >= 24d ? 0 : normalized;
    }

    /// <summary>
    /// Measures the projected major axis from the local plate scale at the geometric centre: the mean pixel length of
    /// small celestial-north and celestial-east offsets. Returns null when the centre does not project.
    /// </summary>
    private static double? MeasureMajorAxisPixels(
        VisibleSceneRequest request,
        CameraBasis basis,
        AltAzPoint geometric,
        double majorAxisArcminutes)
    {
        var frame = ResolvedSourceFrame.Create(geometric, request.Observer.LatitudeDegrees);
        if (!VisibleSceneBuilder.TryProjectGeometry(request, basis, frame.Center, out var center) ||
            !VisibleSceneBuilder.TryProjectGeometry(request, basis, frame.DirectionFromCelestial(PlateScaleProbeRadians, 0), out var north) ||
            !VisibleSceneBuilder.TryProjectGeometry(request, basis, frame.DirectionFromCelestial(0, PlateScaleProbeRadians), out var east))
            return null;
        var scale = (ResolvedFootprintSampler.Distance(center, north) + ResolvedFootprintSampler.Distance(center, east)) /
            (2 * PlateScaleProbeRadians);
        var size = scale * majorAxisArcminutes / 60d * Math.PI / 180d;
        return double.IsFinite(size) && size > 0 ? size : null;
    }

    // Scene IDs share one prefix, so ordering them is ordering their catalog IDs.
    internal static int ComparePriority(PriorityKey left, PriorityKey right)
    {
        var result = right.Featured.CompareTo(left.Featured);
        if (result == 0) result = CompareDescendingNullsLast(left.MajorAxisPixels, right.MajorAxisPixels);
        if (result == 0) result = CompareAscendingNullsLast(left.Magnitude, right.Magnitude);
        return result == 0 ? StringComparer.Ordinal.Compare(left.Id, right.Id) : result;
    }

    private static int CompareDescendingNullsLast(double? left, double? right) =>
        (left, right) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            ({ } l, { } r) => r.CompareTo(l)
        };

    private static int CompareAscendingNullsLast(double? left, double? right) =>
        (left, right) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            ({ } l, { } r) => l.CompareTo(r)
        };

    private static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values) => new(values.ToArray());

    internal readonly record struct PriorityKey(bool Featured, double? MajorAxisPixels, double? Magnitude, string Id);

}
