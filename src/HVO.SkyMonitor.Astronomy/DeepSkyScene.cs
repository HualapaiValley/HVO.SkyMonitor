using System.Collections.ObjectModel;
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
    [property: JsonRequired] double MinimumGlyphPixels)
{
    /// <summary>The largest accepted object bound.</summary>
    public const int MaximumObjectLimit = 2_000;

    /// <summary>The largest accepted outline bound.</summary>
    public const int MaximumOutlineLimit = 256;

    /// <summary>The largest accepted resolvable-size threshold in output pixels.</summary>
    public const double MaximumMinimumGlyphPixels = 64;

    /// <summary>
    /// The default selection: up to 300 objects, every footprint the shared budget leaves, 64 outlines at the widest
    /// available level, and an 8-pixel resolvable threshold.
    /// </summary>
    public static ProjectedSceneDeepSkySelection Default { get; } = new(
        300, ProjectedSceneJson.MaximumResolvedFootprintCount, 64, DeepSkyOutline.WidestLevel, 8);

    /// <summary>Throws when a bound is outside its accepted range.</summary>
    public void Validate()
    {
        if (MaximumObjects is < 1 or > MaximumObjectLimit ||
            MaximumFootprints is < 0 or > ProjectedSceneJson.MaximumResolvedFootprintCount ||
            MaximumOutlines is < 0 or > MaximumOutlineLimit ||
            PreferredOutlineLevel is < DeepSkyOutline.WidestLevel or > DeepSkyOutline.NarrowestLevel ||
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
/// The projected sourced outline of one deep-sky object at one contour level. Each ring is sampled through the
/// resolved-footprint projector; the outline is unclipped only when every ring survives as one closed part.
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
    public static bool IsComplete(IReadOnlyList<ResolvedFootprintPart> parts, int ringCount) =>
        parts.Count == ringCount && parts.All(static part => part.Closed);
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
    public const string CurrentContractVersion = "projected-deep-sky-v1";

    /// <summary>
    /// Candidates are ordered featured first, then by projected major axis (unknown last), then by visual or blue
    /// magnitude (unknown last), then by ordinal ID; representations are assigned in that order.
    /// </summary>
    public const string CurrentAlgorithmVersion = "deep-sky-priority-v1-featured-major-axis-pixels-magnitude-id";

    /// <summary>The largest outline point total one scene carries.</summary>
    public const int MaximumTotalOutlinePointCount = 16_384;
}

/// <summary>Places a validated deep-sky collection into a visible scene.</summary>
internal static class DeepSkySceneProjector
{
    private const double OutlineEdgeStepRadians = 2 * Math.PI / 180d;
    private const double PlateScaleProbeRadians = 1e-5;
    private const double CandidateMarginDegrees = 1;

    public static (ProjectedDeepSky Section, IReadOnlyList<ProjectedResolvedFootprint> Footprints) Project(
        VisibleScene scene,
        IDeepSkyCatalog catalog,
        ProjectedSceneDeepSkySelection selection)
    {
        var request = scene.Request;
        var projector = ProjectorFactory.Create(request.Projection);
        var basis = CameraBasis.Create(
            request.Projection.BoresightAltitudeDegrees, request.Projection.BoresightAzimuthDegrees,
            request.Projection.RollDegrees, request.Projection.HorizontalFlip);
        var boresight = CameraBasis.FromHorizontal(new AltAzPoint(
            request.Projection.BoresightAltitudeDegrees, request.Projection.BoresightAzimuthDegrees));
        var reachDegrees = VisibleSceneBuilder.OpticalRadiusDegrees(request.Projection) + CandidateMarginDegrees;

        var candidates = new List<Candidate>();
        foreach (var item in catalog.Objects)
        {
            if (item.IsStellar && item.MessierNumber is null) continue;
            var ofDate = EquatorialPrecession.PrecessJ2000(
                new EquatorialPoint(item.RightAscensionHours, item.DeclinationDegrees), request.Utc);
            var geometric = CoordinateTransforms.EquatorialToHorizontal(
                ofDate, request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
            var apparent = geometric with
            {
                AltitudeDegrees = AtmosphericRefraction.Apply(geometric.AltitudeDegrees, request.Refraction)
            };
            var pixel = request.HorizonPolicy == HorizonPolicy.GeometricHorizon && geometric.AltitudeDegrees < 0
                ? null
                : projector.Project(apparent);
            var outlines = catalog.GetOutlines(item.Id);
            if (pixel is null)
            {
                // Only an object whose extent can reach into the field is worth sampling.
                if (item.IsStellar || item.MajorAxisArcminutes is null && outlines.Count == 0) continue;
                var center = CameraBasis.FromHorizontal(geometric);
                var reach = Math.Max((item.MajorAxisArcminutes ?? 0) / 120d, OutlineReachDegrees(request, center, outlines));
                if (AngleDegrees(center, boresight) > reachDegrees + reach) continue;
            }
            var majorAxisPixels = item.MajorAxisArcminutes is { } major
                ? MeasureMajorAxisPixels(request, basis, geometric, major)
                : null;
            candidates.Add(new Candidate(item, geometric, apparent, pixel, majorAxisPixels, outlines));
        }
        candidates.Sort(ComparePriority);

        var footprintBudget = Math.Min(
            selection.MaximumFootprints, ProjectedSceneJson.MaximumResolvedFootprintCount - scene.ResolvedFootprints.Count);
        var objects = new List<ProjectedDeepSkyObject>();
        var projectedOutlines = new List<ProjectedDeepSkyOutline>();
        var footprints = new List<ProjectedResolvedFootprint>();
        var outlinePoints = 0;
        var omitted = 0;
        var source = $"{catalog.Semantics.SourceName} {catalog.Semantics.SourceVersion}";
        foreach (var candidate in candidates)
        {
            if (objects.Count == selection.MaximumObjects)
            {
                omitted++;
                continue;
            }
            var item = candidate.Value;
            var id = ProjectedDeepSkyObject.SceneId(item.Id);
            DeepSkyRepresentation? representation = null;
            DeepSkyDegradation? degradation = null;
            int? outlineLevel = null;
            var resolvable = !item.IsStellar &&
                (candidate.MajorAxisPixels is not { } size || size >= selection.MinimumGlyphPixels);
            if (resolvable && SelectOutline(candidate.Outlines, selection.PreferredOutlineLevel) is { } outline)
            {
                if (projectedOutlines.Count >= selection.MaximumOutlines)
                {
                    degradation = DeepSkyDegradation.OutlineLimit;
                }
                else if (SampleOutline(request, basis, id, outline) is { } projected)
                {
                    var points = projected.Parts.Sum(static part => part.Points.Count);
                    if (projected.RingCount > ProjectedDeepSkyOutline.MaximumPartCount ||
                        projected.Parts.Count > ProjectedDeepSkyOutline.MaximumPartCount ||
                        points > ProjectedDeepSkyOutline.MaximumPointCount ||
                        outlinePoints + points > ProjectedDeepSky.MaximumTotalOutlinePointCount)
                    {
                        degradation = DeepSkyDegradation.OutlineLimit;
                    }
                    else
                    {
                        projectedOutlines.Add(projected);
                        outlinePoints += points;
                        representation = DeepSkyRepresentation.Outline;
                        outlineLevel = outline.Level;
                    }
                }
            }
            if (representation is null && resolvable && item.HasOrientedExtent)
            {
                if (footprints.Count < footprintBudget)
                {
                    var footprint = ResolvedFootprintSampler.Sample(
                        request, id, item.DisplayName, ResolvedFootprintSourceKind.DeepSkyObject,
                        CreateExtent(item, source, request.Utc), candidate.Geometric, null);
                    if (footprint is not null)
                    {
                        footprints.Add(footprint);
                        representation = DeepSkyRepresentation.Footprint;
                    }
                }
                else
                {
                    degradation = DeepSkyDegradation.FootprintLimit;
                }
            }
            if (representation is null)
            {
                if (candidate.Pixel is null) continue;
                representation = GlyphRepresentation(item.ObjectType, item.MajorAxisArcminutes, candidate.MajorAxisPixels,
                    selection.MinimumGlyphPixels);
            }
            objects.Add(new ProjectedDeepSkyObject(
                id, item.Designation, item.DisplayName, item.ObjectType, item.CommonName, item.MessierNumber,
                item.CaldwellNumber, ProjectedDeepSkyObject.IsFeatured(item.MessierNumber, item.CaldwellNumber, item.CommonName),
                new EquatorialPoint(item.RightAscensionHours, item.DeclinationDegrees), candidate.Geometric,
                candidate.Apparent, candidate.Pixel, item.VMagnitude, item.BMagnitude, item.MajorAxisArcminutes,
                item.MinorAxisArcminutes, item.PositionAngleDegrees, candidate.MajorAxisPixels, representation.Value,
                outlineLevel, degradation));
        }

        var semantics = catalog.Semantics;
        var section = new ProjectedDeepSky(
            ProjectedDeepSky.CurrentContractVersion, ProjectedDeepSky.CurrentAlgorithmVersion, semantics.SourceName,
            semantics.SourceVersion, semantics.SourceCommit, semantics.SourceUrl, semantics.License, omitted,
            Freeze(objects.OrderBy(static item => item.Id, StringComparer.Ordinal)),
            Freeze(projectedOutlines.OrderBy(static item => item.Id, StringComparer.Ordinal)));
        return (section, footprints);
    }

    /// <summary>
    /// Returns the glyph a placed object falls back to when it has no drawn geometry: stellar rows keep their stellar
    /// symbol, unknown extents their unknown-extent symbol, and sized objects a size-qualified or minimum symbol.
    /// </summary>
    internal static DeepSkyRepresentation GlyphRepresentation(
        string objectType,
        double? majorAxisArcminutes,
        double? majorAxisPixels,
        double minimumGlyphPixels)
    {
        if (DeepSkyObjectTypes.IsStellar(objectType)) return DeepSkyRepresentation.StellarGlyph;
        if (majorAxisArcminutes is null) return DeepSkyRepresentation.UnknownExtentGlyph;
        return majorAxisPixels is { } size && size >= minimumGlyphPixels
            ? DeepSkyRepresentation.SizedGlyph
            : DeepSkyRepresentation.MinimumGlyph;
    }

    /// <summary>Returns the outline at the available level closest to the preferred one, preferring the wider on a tie.</summary>
    internal static DeepSkyOutline? SelectOutline(IReadOnlyList<DeepSkyOutline> outlines, int preferredLevel) =>
        outlines
            .OrderBy(item => Math.Abs(item.Level - preferredLevel))
            .ThenBy(static item => item.Level)
            .FirstOrDefault();

    private static ProjectedDeepSkyOutline? SampleOutline(
        VisibleSceneRequest request,
        CameraBasis basis,
        string id,
        DeepSkyOutline outline)
    {
        var parts = new List<ResolvedFootprintPart>();
        foreach (var ring in outline.Rings)
        {
            // A ring repeats its first point last; the sampler closes the ring itself.
            var vertices = ring.Points.Take(ring.Points.Count - 1)
                .Select(point => GeometricDirection(request, point.RightAscensionDegrees / 15d, point.DeclinationDegrees))
                .ToArray();
            parts.AddRange(ResolvedFootprintSampler.SampleGreatCircleRing(request, basis, vertices, OutlineEdgeStepRadians));
        }
        if (parts.Count == 0) return null;
        return new ProjectedDeepSkyOutline(
            id, ProjectedDeepSkyOutline.CurrentContractVersion, ProjectedDeepSkyOutline.SamplingAlgorithm, outline.Level,
            outline.Rings.Count, request.Refraction.Enabled ? AtmosphericRefraction.ModelVersion : null,
            !ProjectedDeepSkyOutline.IsComplete(parts, outline.Rings.Count), ResolvedFootprintSampler.ComputeBounds(parts),
            Freeze(parts));
    }

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

    private static double OutlineReachDegrees(VisibleSceneRequest request, EnuVector center, IReadOnlyList<DeepSkyOutline> outlines)
    {
        var reach = 0d;
        foreach (var point in outlines.SelectMany(static item => item.Rings).SelectMany(static ring => ring.Points))
        {
            reach = Math.Max(reach, AngleDegrees(center, GeometricDirection(request, point.RightAscensionDegrees / 15d, point.DeclinationDegrees)));
        }
        return reach;
    }

    private static EnuVector GeometricDirection(VisibleSceneRequest request, double rightAscensionHours, double declinationDegrees)
    {
        var ofDate = EquatorialPrecession.PrecessJ2000(new EquatorialPoint(rightAscensionHours, declinationDegrees), request.Utc);
        return CameraBasis.FromHorizontal(CoordinateTransforms.EquatorialToHorizontal(
            ofDate, request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees));
    }

    private static double AngleDegrees(EnuVector left, EnuVector right) =>
        Math.Acos(Math.Clamp(EnuVector.Dot(left, right), -1d, 1d)) * 180d / Math.PI;

    private static int ComparePriority(Candidate left, Candidate right) =>
        ComparePriority(
            new PriorityKey(left.Featured, left.MajorAxisPixels, left.Magnitude, left.Value.Id),
            new PriorityKey(right.Featured, right.MajorAxisPixels, right.Magnitude, right.Value.Id));

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

    private sealed record Candidate(
        DeepSkyObject Value,
        AltAzPoint Geometric,
        AltAzPoint Apparent,
        PixelPoint? Pixel,
        double? MajorAxisPixels,
        IReadOnlyList<DeepSkyOutline> Outlines)
    {
        public bool Featured => ProjectedDeepSkyObject.IsFeatured(Value.MessierNumber, Value.CaldwellNumber, Value.CommonName);

        public double? Magnitude => Value.VMagnitude ?? Value.BMagnitude;
    }
}
