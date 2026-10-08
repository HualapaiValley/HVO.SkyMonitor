using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Identifies which kind of source produced a resolved footprint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResolvedFootprintSourceKind>))]
public enum ResolvedFootprintSourceKind
{
    SolarSystemBody,
    DeepSkyObject
}

/// <summary>The on-sky outline family of a resolved source.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResolvedFootprintShape>))]
public enum ResolvedFootprintShape
{
    Circle,
    Ellipse
}

/// <summary>
/// Angular extent of a resolved source. Axes are semi-axes in degrees; the position angle of the
/// major axis is measured from celestial north through east. A null uncertainty means unquantified.
/// </summary>
public sealed record ResolvedFootprintExtent(
    [property: JsonRequired] ResolvedFootprintShape Shape,
    [property: JsonRequired] double SemiMajorAxisDegrees,
    [property: JsonRequired] double SemiMinorAxisDegrees,
    [property: JsonRequired] double PositionAngleDegrees,
    [property: JsonRequired] double? SemiMajorAxisUncertaintyDegrees,
    [property: JsonRequired] double? SemiMinorAxisUncertaintyDegrees,
    [property: JsonRequired] double? PositionAngleUncertaintyDegrees,
    [property: JsonRequired] string Source)
{
    /// <summary>Creates a circular extent with an unquantified radius uncertainty.</summary>
    public static ResolvedFootprintExtent Circle(double radiusDegrees, string source) =>
        new(ResolvedFootprintShape.Circle, radiusDegrees, radiusDegrees, 0, null, null, null, source);
}

/// <summary>Instantaneous illumination facts for a resolved solar-system body.</summary>
public sealed record ResolvedBodyAppearance(
    [property: JsonRequired] string EphemerisAlgorithmVersion,
    [property: JsonRequired] double DistanceKilometers,
    [property: JsonRequired] double IlluminatedFraction,
    [property: JsonRequired] double BrightLimbAngleDegrees,
    [property: JsonRequired] double VisualMagnitude);

/// <summary>One continuous polyline of a footprint outline in output pixels.</summary>
public sealed record ResolvedFootprintPart(
    [property: JsonRequired] bool Closed,
    [property: JsonRequired] IReadOnlyList<PixelPoint> Points);

/// <summary>Axis-aligned output-pixel bounds of every footprint point.</summary>
public sealed record ResolvedFootprintBounds(
    [property: JsonRequired] double MinX,
    [property: JsonRequired] double MinY,
    [property: JsonRequired] double MaxX,
    [property: JsonRequired] double MaxY);

/// <summary>
/// The projected outline of a source with resolved angular size. The outline is sampled on the sky,
/// refracted per sample with the scene refraction model, projected, and clipped to the emitted image.
/// </summary>
public sealed record ProjectedResolvedFootprint(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string DisplayName,
    [property: JsonRequired] ResolvedFootprintSourceKind SourceKind,
    [property: JsonRequired] string ContractVersion,
    [property: JsonRequired] string SamplingAlgorithmVersion,
    [property: JsonRequired] ResolvedFootprintExtent Extent,
    [property: JsonRequired] AltAzPoint GeometricCenter,
    [property: JsonRequired] AltAzPoint ApparentCenter,
    [property: JsonRequired] string? RefractionModel,
    [property: JsonRequired] PixelPoint? CenterPixel,
    [property: JsonRequired] bool Clipped,
    [property: JsonRequired] ResolvedFootprintBounds Bounds,
    [property: JsonRequired] IReadOnlyList<ResolvedFootprintPart> Parts,
    [property: JsonRequired] ResolvedBodyAppearance? Appearance)
{
    public const string CurrentContractVersion = "resolved-footprint-v1";
    public const int MaximumPartCount = 256;
    public const int MaximumPointCount = 16_384;
}

/// <summary>
/// Local tangent frame at a source centre. Rendering and annotation both map tangent-plane offsets
/// through <see cref="Direction"/>, so the disc they draw and the outline they label share one geometry.
/// </summary>
public readonly record struct ResolvedSourceFrame(
    EnuVector Center,
    EnuVector LocalUp,
    EnuVector LocalEast,
    EnuVector CelestialNorth,
    EnuVector CelestialEast)
{
    /// <summary>Creates the frame at a geometric (unrefracted) centre for an observer latitude.</summary>
    public static ResolvedSourceFrame Create(AltAzPoint geometricCenter, double observerLatitudeDegrees)
    {
        if (!double.IsFinite(observerLatitudeDegrees) || observerLatitudeDegrees is < -90 or > 90)
            throw new ArgumentOutOfRangeException(nameof(observerLatitudeDegrees));
        var center = CameraBasis.FromHorizontal(geometricCenter);
        var altitude = geometricCenter.AltitudeDegrees * Math.PI / 180d;
        var azimuth = geometricCenter.AzimuthDegrees * Math.PI / 180d;
        // Derivatives of the horizontal direction: up along increasing altitude, east along increasing azimuth.
        var localUp = new EnuVector(
            -Math.Sin(altitude) * Math.Sin(azimuth), -Math.Sin(altitude) * Math.Cos(azimuth), Math.Cos(altitude));
        var localEast = new EnuVector(Math.Cos(azimuth), -Math.Sin(azimuth), 0);
        var latitude = observerLatitudeDegrees * Math.PI / 180d;
        var pole = new EnuVector(0, Math.Cos(latitude), Math.Sin(latitude));
        var northTangent = pole - center * EnuVector.Dot(pole, center);
        var north = northTangent.Length < 1e-12 ? localUp : northTangent.Normalize();
        return new(center, localUp, localEast, north, EnuVector.Cross(north, center).Normalize());
    }

    /// <summary>Maps a tangent-plane offset in radians along the local up/east axes onto the sphere.</summary>
    public EnuVector DirectionFromLocal(double upRadians, double eastRadians) =>
        Direction(LocalUp * upRadians + LocalEast * eastRadians);

    /// <summary>Maps a tangent-plane offset in radians along celestial north/east onto the sphere.</summary>
    public EnuVector DirectionFromCelestial(double northRadians, double eastRadians) =>
        Direction(CelestialNorth * northRadians + CelestialEast * eastRadians);

    /// <summary>Exponential map of a tangent vector whose length is the angular offset in radians.</summary>
    public EnuVector Direction(EnuVector tangentOffset)
    {
        var angle = tangentOffset.Length;
        if (angle < 1e-15) return Center;
        return (Center * Math.Cos(angle) + tangentOffset * (Math.Sin(angle) / angle)).Normalize();
    }
}

/// <summary>
/// Projects one geometric direction through the scene pipeline: horizon policy, refraction, projection
/// and lens distortion. It does not clip to the sensor or aperture, so a renderer can spread light that
/// lands just outside the frame before it clips.
/// </summary>
public sealed class ResolvedSourceProjector
{
    private readonly CameraBasis _basis;

    public ResolvedSourceProjector(ProjectionContext projection, HorizonPolicy horizonPolicy, RefractionOptions refraction)
    {
        projection.Validate();
        refraction.Validate();
        if (!Enum.IsDefined(horizonPolicy)) throw new ArgumentOutOfRangeException(nameof(horizonPolicy));
        Projection = projection;
        HorizonPolicy = horizonPolicy;
        Refraction = refraction;
        _basis = CameraBasis.Create(projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees,
            projection.RollDegrees, projection.HorizontalFlip);
    }

    public ProjectionContext Projection { get; }
    public HorizonPolicy HorizonPolicy { get; }
    public RefractionOptions Refraction { get; }

    /// <summary>Returns false when the horizon policy or projection model rejects the direction.</summary>
    public bool TryProject(EnuVector geometricDirection, out PixelPoint pixel) =>
        VisibleSceneBuilder.TryProjectGeometry(Projection, HorizonPolicy, Refraction, _basis, geometricDirection, out pixel);
}

/// <summary>Samples resolved-source outlines into clipped projected polylines.</summary>
public static class ResolvedFootprintSampler
{
    public const string AlgorithmVersion = "resolved-footprint-limb-adaptive-v1";
    public const int BaseSampleCount = 64;
    public const int MaximumSubdivisionDepth = 7;
    public const double MaximumChordErrorPixels = 0.25;
    private const int BoundaryBisectionIterations = 32;
    internal const double JoinTolerancePixels = 1e-9;

    /// <summary>
    /// Samples a source outline for a visible-scene request. Returns null when no part of the
    /// outline is above the horizon policy and inside the projection.
    /// </summary>
    public static ProjectedResolvedFootprint? Sample(
        VisibleSceneRequest request,
        string id,
        string displayName,
        ResolvedFootprintSourceKind sourceKind,
        ResolvedFootprintExtent extent,
        AltAzPoint geometricCenter,
        ResolvedBodyAppearance? appearance)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(extent);
        if (!Enum.IsDefined(sourceKind) || !Enum.IsDefined(extent.Shape) ||
            !double.IsFinite(extent.SemiMajorAxisDegrees) || !double.IsFinite(extent.SemiMinorAxisDegrees) ||
            !double.IsFinite(extent.PositionAngleDegrees) || extent.SemiMinorAxisDegrees <= 0 ||
            extent.SemiMajorAxisDegrees < extent.SemiMinorAxisDegrees || extent.SemiMajorAxisDegrees > 90)
            throw new ArgumentOutOfRangeException(nameof(extent));

        var frame = ResolvedSourceFrame.Create(geometricCenter, request.Observer.LatitudeDegrees);
        var basis = CameraBasis.Create(
            request.Projection.BoresightAltitudeDegrees, request.Projection.BoresightAzimuthDegrees,
            request.Projection.RollDegrees, request.Projection.HorizontalFlip);
        var positionAngle = extent.PositionAngleDegrees * Math.PI / 180d;
        var major = frame.CelestialNorth * Math.Cos(positionAngle) + frame.CelestialEast * Math.Sin(positionAngle);
        var minor = frame.CelestialEast * Math.Cos(positionAngle) - frame.CelestialNorth * Math.Sin(positionAngle);
        var semiMajor = extent.SemiMajorAxisDegrees * Math.PI / 180d;
        var semiMinor = extent.SemiMinorAxisDegrees * Math.PI / 180d;
        EnuVector Limb(double parameter) => frame.Direction(
            major * (semiMajor * Math.Cos(parameter)) + minor * (semiMinor * Math.Sin(parameter)));

        var chords = new List<(PixelPoint From, PixelPoint To)>();
        var clipped = false;
        var step = 2 * Math.PI / BaseSampleCount;
        for (var index = 0; index < BaseSampleCount; index++)
        {
            AppendLimbChord(request, basis, Limb, index * step, (index + 1) * step, 0, chords, ref clipped);
        }
        var parts = Stitch(chords, closedCandidate: !clipped);
        if (parts.Count == 0) return null;
        if (parts.Count > ProjectedResolvedFootprint.MaximumPartCount ||
            parts.Sum(static part => part.Points.Count) > ProjectedResolvedFootprint.MaximumPointCount)
            throw new InvalidOperationException("Resolved footprint exceeds its structural bounds.");

        var apparentCenter = geometricCenter with
        {
            AltitudeDegrees = AtmosphericRefraction.Apply(geometricCenter.AltitudeDegrees, request.Refraction)
        };
        // The centre carries a pixel only where the scene would admit a point object at that direction.
        var centerPixel = request.HorizonPolicy == HorizonPolicy.GeometricHorizon && geometricCenter.AltitudeDegrees < 0
            ? null
            : ProjectorFactory.Create(request.Projection).Project(apparentCenter);
        return new ProjectedResolvedFootprint(
            id, displayName, sourceKind, ProjectedResolvedFootprint.CurrentContractVersion, AlgorithmVersion,
            extent, geometricCenter, apparentCenter,
            request.Refraction.Enabled ? AtmosphericRefraction.ModelVersion : null,
            centerPixel, !IsSingleClosed(parts), ComputeBounds(parts), parts, appearance);
    }

    internal static bool IsSingleClosed(IReadOnlyList<ResolvedFootprintPart> parts) =>
        parts.Count == 1 && parts[0].Closed;

    internal static ResolvedFootprintBounds ComputeBounds(IReadOnlyList<ResolvedFootprintPart> parts)
    {
        var points = parts.SelectMany(static part => part.Points).ToArray();
        return new(points.Min(static point => point.X), points.Min(static point => point.Y),
            points.Max(static point => point.X), points.Max(static point => point.Y));
    }

    /// <summary>
    /// Joins ordered chords into polylines. A candidate closed outline whose chords form one loop is
    /// returned as a single closed part without the repeated start point; a broken loop whose last
    /// run ends where the first starts is joined across the sampling seam.
    /// </summary>
    internal static List<ResolvedFootprintPart> Stitch(
        IReadOnlyList<(PixelPoint From, PixelPoint To)> chords,
        bool closedCandidate)
    {
        var runs = new List<List<PixelPoint>>();
        foreach (var (from, to) in chords)
        {
            if (Distance(from, to) <= JoinTolerancePixels) continue;
            if (runs.Count == 0 || Distance(runs[^1][^1], from) > JoinTolerancePixels)
            {
                runs.Add([from]);
            }
            runs[^1].Add(to);
        }
        if (runs.Count == 0) return [];
        var loops = Distance(runs[^1][^1], runs[0][0]) <= JoinTolerancePixels;
        if (runs.Count == 1 && closedCandidate && loops && runs[0].Count >= 4)
        {
            runs[0].RemoveAt(runs[0].Count - 1);
            return [new ResolvedFootprintPart(true, new ReadOnlyCollection<PixelPoint>(runs[0]))];
        }
        if (runs.Count > 1 && loops)
        {
            var last = runs[^1];
            last.RemoveAt(last.Count - 1);
            last.AddRange(runs[0]);
            runs[0] = last;
            runs.RemoveAt(runs.Count - 1);
        }
        return runs
            .Where(static run => run.Count >= 2)
            .Select(static run => new ResolvedFootprintPart(false, new ReadOnlyCollection<PixelPoint>(run)))
            .ToList();
    }

    private static void AppendLimbChord(
        VisibleSceneRequest request,
        CameraBasis basis,
        Func<double, EnuVector> limb,
        double from,
        double to,
        int depth,
        List<(PixelPoint From, PixelPoint To)> output,
        ref bool clipped)
    {
        var fromValid = VisibleSceneBuilder.TryProjectGeometry(request, basis, limb(from), out var fromPixel);
        var toValid = VisibleSceneBuilder.TryProjectGeometry(request, basis, limb(to), out var toPixel);
        var middle = (from + to) / 2;
        if (fromValid && toValid)
        {
            var middleValid = VisibleSceneBuilder.TryProjectGeometry(request, basis, limb(middle), out var middlePixel);
            if (depth < MaximumSubdivisionDepth && (!middleValid ||
                VisibleSceneBuilder.DistanceFromChord(middlePixel, fromPixel, toPixel) > MaximumChordErrorPixels))
            {
                AppendLimbChord(request, basis, limb, from, middle, depth + 1, output, ref clipped);
                AppendLimbChord(request, basis, limb, middle, to, depth + 1, output, ref clipped);
                return;
            }
            if (!middleValid) clipped = true;
            AddClipped(request.Projection, fromPixel, toPixel, output, ref clipped);
            return;
        }

        if (depth < MaximumSubdivisionDepth)
        {
            AppendLimbChord(request, basis, limb, from, middle, depth + 1, output, ref clipped);
            AppendLimbChord(request, basis, limb, middle, to, depth + 1, output, ref clipped);
            return;
        }

        clipped = true;
        if (fromValid == toValid) return;
        var validParameter = fromValid ? from : to;
        var invalidParameter = fromValid ? to : from;
        var boundaryPixel = fromValid ? fromPixel : toPixel;
        for (var iteration = 0; iteration < BoundaryBisectionIterations; iteration++)
        {
            var candidate = (validParameter + invalidParameter) / 2;
            if (VisibleSceneBuilder.TryProjectGeometry(request, basis, limb(candidate), out var candidatePixel))
            {
                validParameter = candidate;
                boundaryPixel = candidatePixel;
            }
            else
            {
                invalidParameter = candidate;
            }
        }
        if (fromValid)
            AddClipped(request.Projection, fromPixel, boundaryPixel, output, ref clipped);
        else
            AddClipped(request.Projection, boundaryPixel, toPixel, output, ref clipped);
    }

    private static void AddClipped(
        ProjectionContext projection,
        PixelPoint from,
        PixelPoint to,
        List<(PixelPoint From, PixelPoint To)> output,
        ref bool clipped)
    {
        if (!VisibleSceneBuilder.TryClipToProjection(projection, from, to, out var clippedFrom, out var clippedTo))
        {
            clipped = true;
            return;
        }
        if (Distance(clippedFrom, from) > JoinTolerancePixels || Distance(clippedTo, to) > JoinTolerancePixels)
            clipped = true;
        output.Add((clippedFrom, clippedTo));
    }

    internal static double Distance(PixelPoint left, PixelPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
}
