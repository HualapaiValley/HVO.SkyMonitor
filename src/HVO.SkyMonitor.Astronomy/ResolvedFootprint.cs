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
    public const string BoundarySafeAlgorithmVersion = "resolved-footprint-limb-adaptive-v2";
    public const int BaseSampleCount = 64;
    public const int MaximumSubdivisionDepth = 7;
    public const double MaximumChordErrorPixels = 0.25;
    private const int BoundaryBisectionIterations = 32;
    private const int ConstraintBisectionIterations = 52;
    private const int ConstraintGoldenIterations = 60;
    internal const double JoinTolerancePixels = 1e-9;
    private const double BoundaryInsideTolerance = 1e-9;

    /// <summary>
    /// Samples a source outline for a visible-scene request using the frozen v1/v2 algorithm
    /// (<see cref="AlgorithmVersion"/>). Returns null when no part of the outline is above the horizon policy
    /// and inside the projection.
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
        var (frame, basis, limb, _) = PrepareEllipse(request, id, displayName, sourceKind, extent, geometricCenter);

        var chords = new List<(PixelPoint From, PixelPoint To)>();
        var clipped = false;
        var step = 2 * Math.PI / BaseSampleCount;
        for (var index = 0; index < BaseSampleCount; index++)
        {
            AppendLimbChord(request, basis, limb, index * step, (index + 1) * step, 0, chords, ref clipped);
        }
        return BuildFootprint(request, id, displayName, sourceKind, AlgorithmVersion, extent, geometricCenter, appearance, chords, clipped);
    }

    internal enum RefractionEvaluationBranch
    {
        Default,
        ForceRefracted,
        ForceUnrefracted
    }

    internal readonly record struct BranchedChord(
        PixelPoint From,
        PixelPoint To,
        RefractionEvaluationBranch Branch);

    /// <summary>
    /// Samples a source outline using constraint-isolated horizon, refraction-floor, optical-domain, and
    /// sensor/aperture boundary clipping (<see cref="BoundarySafeAlgorithmVersion"/>). Preserves visibility of
    /// arbitrarily narrow horizon and domain grazes without fabricating chords across exterior gaps or refraction
    /// discontinuities, and fails explicitly if its chord error bound cannot be met within the subdivision budget.
    /// </summary>
    public static ProjectedResolvedFootprint? SampleBoundarySafe(
        VisibleSceneRequest request,
        string id,
        string displayName,
        ResolvedFootprintSourceKind sourceKind,
        ResolvedFootprintExtent extent,
        AltAzPoint geometricCenter,
        ResolvedBodyAppearance? appearance) =>
        SampleBoundarySafe(
            request, id, displayName, sourceKind, extent, geometricCenter, appearance, MaximumSubdivisionDepth);

    internal static ProjectedResolvedFootprint? SampleBoundarySafe(
        VisibleSceneRequest request,
        string id,
        string displayName,
        ResolvedFootprintSourceKind sourceKind,
        ResolvedFootprintExtent extent,
        AltAzPoint geometricCenter,
        ResolvedBodyAppearance? appearance,
        int maxSubdivisionDepth)
    {
        var (_, basis, limb, semiMajorRadians) = PrepareEllipse(request, id, displayName, sourceKind, extent, geometricCenter);

        var chords = new List<BranchedChord>();
        var clipped = false;
        var errorBoundExceeded = false;
        AppendBoundarySafeCurve(
            request,
            basis,
            limb,
            0d,
            2d * Math.PI,
            BaseSampleCount,
            semiMajorRadians,
            analyticHorizonHints: null,
            analyticRefractionFloorHints: null,
            analyticDomainHints: null,
            maxSubdivisionDepth,
            chords,
            ref clipped,
            ref errorBoundExceeded);
        if (errorBoundExceeded)
        {
            throw new InvalidOperationException(
                "Resolved footprint sampler exceeded maximum subdivision depth without meeting its chord error bound.");
        }
        return BuildFootprint(request, id, displayName, sourceKind, BoundarySafeAlgorithmVersion, extent, geometricCenter, appearance, Stitch(chords, closedCandidate: !clipped));
    }

    private static (ResolvedSourceFrame Frame, CameraBasis Basis, Func<double, EnuVector> Limb, double SemiMajorRadians) PrepareEllipse(
        VisibleSceneRequest request,
        string id,
        string displayName,
        ResolvedFootprintSourceKind sourceKind,
        ResolvedFootprintExtent extent,
        AltAzPoint geometricCenter)
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
        return (frame, basis, Limb, semiMajor);
    }

    private static ProjectedResolvedFootprint? BuildFootprint(
        VisibleSceneRequest request,
        string id,
        string displayName,
        ResolvedFootprintSourceKind sourceKind,
        string samplingAlgorithmVersion,
        ResolvedFootprintExtent extent,
        AltAzPoint geometricCenter,
        ResolvedBodyAppearance? appearance,
        List<(PixelPoint From, PixelPoint To)> chords,
        bool clipped) =>
        BuildFootprint(request, id, displayName, sourceKind, samplingAlgorithmVersion, extent, geometricCenter, appearance,
            Stitch(chords, closedCandidate: !clipped));

    private static ProjectedResolvedFootprint? BuildFootprint(
        VisibleSceneRequest request,
        string id,
        string displayName,
        ResolvedFootprintSourceKind sourceKind,
        string samplingAlgorithmVersion,
        ResolvedFootprintExtent extent,
        AltAzPoint geometricCenter,
        ResolvedBodyAppearance? appearance,
        List<ResolvedFootprintPart> parts)
    {
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
            id, displayName, sourceKind, ProjectedResolvedFootprint.CurrentContractVersion, samplingAlgorithmVersion,
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

    /// <summary>
    /// Joins ordered branch-tagged chords into polylines without ever merging runs across a refraction-floor
    /// discontinuity branch break, while still stitching continuous same-branch runs across the periodic seam.
    /// </summary>
    internal static List<ResolvedFootprintPart> Stitch(
        IReadOnlyList<BranchedChord> chords,
        bool closedCandidate)
    {
        var runs = new List<(List<PixelPoint> Points, RefractionEvaluationBranch Branch)>();
        foreach (var (from, to, branch) in chords)
        {
            if (Distance(from, to) <= JoinTolerancePixels) continue;
            if (runs.Count == 0 ||
                runs[^1].Branch != branch ||
                Distance(runs[^1].Points[^1], from) > JoinTolerancePixels)
            {
                runs.Add(([from], branch));
            }
            runs[^1].Points.Add(to);
        }
        if (runs.Count == 0) return [];
        var sameSeamBranch = runs[^1].Branch == runs[0].Branch;
        var loops = sameSeamBranch && Distance(runs[^1].Points[^1], runs[0].Points[0]) <= JoinTolerancePixels;
        if (runs.Count == 1 && closedCandidate && loops && runs[0].Points.Count >= 4)
        {
            runs[0].Points.RemoveAt(runs[0].Points.Count - 1);
            return [new ResolvedFootprintPart(true, new ReadOnlyCollection<PixelPoint>(runs[0].Points))];
        }
        if (runs.Count > 1 && loops)
        {
            var last = runs[^1].Points;
            last.RemoveAt(last.Count - 1);
            last.AddRange(runs[0].Points);
            runs[0] = (last, runs[0].Branch);
            runs.RemoveAt(runs.Count - 1);
        }
        return runs
            .Where(static run => run.Points.Count >= 2)
            .Select(static run => new ResolvedFootprintPart(false, new ReadOnlyCollection<PixelPoint>(run.Points)))
            .ToList();
    }

    internal static bool IsInsideProjection(ProjectionContext projection, PixelPoint pixel)
    {
        if (!double.IsFinite(pixel.X) || !double.IsFinite(pixel.Y))
            return false;
        if (projection.EnforceSensorBounds &&
            (pixel.X < -BoundaryInsideTolerance || pixel.X > projection.WidthPixels + BoundaryInsideTolerance ||
             pixel.Y < -BoundaryInsideTolerance || pixel.Y > projection.HeightPixels + BoundaryInsideTolerance))
            return false;
        if (projection.Aperture == ProjectionAperture.Circular)
        {
            var dx = pixel.X - projection.PrincipalPointX;
            var dy = pixel.Y - projection.PrincipalPointY;
            var radius = projection.ImageCircleRadiusPixels!.Value;
            if (dx * dx + dy * dy > radius * radius + Math.Max(1d, radius) * 2e-9)
                return false;
        }
        return true;
    }

    internal static PixelPoint ClampToProjection(ProjectionContext projection, PixelPoint pixel)
    {
        var x = pixel.X;
        var y = pixel.Y;
        if (projection.EnforceSensorBounds)
        {
            x = Math.Clamp(x, 0d, projection.WidthPixels);
            y = Math.Clamp(y, 0d, projection.HeightPixels);
        }
        if (projection.Aperture == ProjectionAperture.Circular)
        {
            var dx = x - projection.PrincipalPointX;
            var dy = y - projection.PrincipalPointY;
            var radius = projection.ImageCircleRadiusPixels!.Value;
            var distSq = dx * dx + dy * dy;
            if (distSq > radius * radius && distSq > 0)
            {
                var scale = radius / Math.Sqrt(distSq);
                x = projection.PrincipalPointX + dx * scale;
                y = projection.PrincipalPointY + dy * scale;
            }
        }
        return new PixelPoint(x, y);
    }

    internal static bool TryProjectVisible(
        VisibleSceneRequest request,
        CameraBasis basis,
        EnuVector direction,
        out PixelPoint pixel)
    {
        if (VisibleSceneBuilder.TryProjectGeometry(request, basis, direction, out pixel) &&
            IsInsideProjection(request.Projection, pixel))
        {
            pixel = ClampToProjection(request.Projection, pixel);
            return true;
        }
        return false;
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
            if (!middleValid ||
                Distance(middlePixel, new PixelPoint((fromPixel.X + toPixel.X) / 2, (fromPixel.Y + toPixel.Y) / 2)) > MaximumChordErrorPixels)
            {
                if (depth < MaximumSubdivisionDepth)
                {
                    AppendLimbChord(request, basis, limb, from, middle, depth + 1, output, ref clipped);
                    AppendLimbChord(request, basis, limb, middle, to, depth + 1, output, ref clipped);
                }
                else
                {
                    // A bounded sampler cannot join across an unresolved projection discontinuity.
                    clipped = true;
                }
                return;
            }
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
        var boundaryMiddle = (validParameter + (fromValid ? from : to)) / 2;
        var endpoint = fromValid ? fromPixel : toPixel;
        if (!VisibleSceneBuilder.TryProjectGeometry(request, basis, limb(boundaryMiddle), out var boundaryMiddlePixel) ||
            Distance(boundaryMiddlePixel, new PixelPoint((endpoint.X + boundaryPixel.X) / 2, (endpoint.Y + boundaryPixel.Y) / 2)) > MaximumChordErrorPixels)
            return;
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

    internal static bool RequiresRefractionFloorSplit(VisibleSceneRequest request) =>
        request.Refraction.Enabled &&
        request.Refraction.MinimumAltitudeDegrees > -90d &&
        !(request.HorizonPolicy == HorizonPolicy.GeometricHorizon && request.Refraction.MinimumAltitudeDegrees <= 0d);

    internal static void AppendBoundarySafeCurve(
        VisibleSceneRequest request,
        CameraBasis basis,
        Func<double, EnuVector> curve,
        double tStart,
        double tEnd,
        int baseSteps,
        double maxAngularSpeedRadiansPerParam,
        IReadOnlyList<double>? analyticHorizonHints,
        IReadOnlyList<double>? analyticRefractionFloorHints,
        IReadOnlyList<double>? analyticDomainHints,
        int maxDepth,
        List<BranchedChord> output,
        ref bool clipped,
        ref bool errorBoundExceeded)
    {
        if (tEnd <= tStart) return;
        var fullSpan = tEnd - tStart;
        var intervals = new List<(double From, double To)> { (tStart, tEnd) };

        // Stage 1a: Isolate geometric horizon feasibility using smooth Up(t) >= 0 with Lipschitz exclusion + extremum search.
        if (request.HorizonPolicy == HorizonPolicy.GeometricHorizon)
        {
            var horizonIntervals = new List<(double From, double To)>();
            var allAbove = true;
            foreach (var (a, b) in intervals)
            {
                var steps = Math.Max(1, (int)Math.Ceiling(baseSteps * (b - a) / fullSpan));
                var sub = IsolatePositiveIntervals(
                    t => curve(t).Up,
                    a, b, steps,
                    Math.Max(1e-9, maxAngularSpeedRadiansPerParam),
                    analyticHorizonHints,
                    out var fullyPositive);
                if (!fullyPositive) allAbove = false;
                horizonIntervals.AddRange(sub);
            }
            if (!allAbove) clipped = true;
            intervals = horizonIntervals;
            if (intervals.Count == 0) return;
        }

        // Stage 1b: Split across the Bennett refraction altitude floor discontinuity (MinimumAltitudeDegrees)
        // whenever both sides of the floor are reachable, pinning each piece to its smooth one-sided limit so
        // no chord ever bridges across the altitude step jump.
        var branchedIntervals = new List<(double From, double To, RefractionEvaluationBranch Branch)>();
        if (RequiresRefractionFloorSplit(request))
        {
            var sinFloor = Math.Sin(request.Refraction.MinimumAltitudeDegrees * Math.PI / 180d);
            var hasAbove = false;
            var hasBelow = false;
            foreach (var (a, b) in intervals)
            {
                var steps = Math.Max(1, (int)Math.Ceiling(baseSteps * (b - a) / fullSpan));
                var above = IsolatePositiveIntervals(
                    t => curve(t).Up - sinFloor,
                    a, b, steps,
                    Math.Max(1e-9, maxAngularSpeedRadiansPerParam),
                    analyticRefractionFloorHints,
                    out _);
                foreach (var sub in above)
                {
                    if (sub.To - sub.From <= 1e-14 * Math.Max(1d, fullSpan)) continue;
                    hasAbove = true;
                    branchedIntervals.Add((sub.From, sub.To, RefractionEvaluationBranch.ForceRefracted));
                }

                var below = IsolatePositiveIntervals(
                    t => sinFloor - curve(t).Up,
                    a, b, steps,
                    Math.Max(1e-9, maxAngularSpeedRadiansPerParam),
                    analyticRefractionFloorHints,
                    out _);
                foreach (var sub in below)
                {
                    if (sub.To - sub.From <= 1e-14 * Math.Max(1d, fullSpan)) continue;
                    var midBelow = sinFloor - curve(0.5 * (sub.From + sub.To)).Up;
                    if (midBelow <= 1e-14)
                    {
                        var peakT = MaximizeGolden(t => sinFloor - curve(t).Up, sub.From, sub.To, ConstraintGoldenIterations);
                        if (sinFloor - curve(peakT).Up <= 1e-14) continue;
                    }
                    hasBelow = true;
                    branchedIntervals.Add((sub.From, sub.To, RefractionEvaluationBranch.ForceUnrefracted));
                }
            }
            if (hasAbove && hasBelow) clipped = true;
            branchedIntervals.Sort(static (left, right) => left.From.CompareTo(right.From));
            if (branchedIntervals.Count == 0) return;
        }
        else
        {
            foreach (var (a, b) in intervals)
            {
                branchedIntervals.Add((a, b, RefractionEvaluationBranch.Default));
            }
        }

        // Stage 2: Isolate optical projection domain feasibility (cz >= minDomainCosine) on each branch's apparent directions.
        var minDomainCosine = ComputeMinimumDomainCosine(request.Projection);
        if (minDomainCosine > -1d + 1e-12)
        {
            var domainIntervals = new List<(double From, double To, RefractionEvaluationBranch Branch)>();
            var allInDomain = true;
            foreach (var (a, b, branch) in branchedIntervals)
            {
                var branchRefracted = request.Refraction.Enabled && branch != RefractionEvaluationBranch.ForceUnrefracted;
                var domainSpeed = Math.Max(1e-9, maxAngularSpeedRadiansPerParam * (branchRefracted ? 1.25 : 1d));
                var refractionAllowance = branchRefracted ? 0.015 : 0d;
                var steps = Math.Max(1, (int)Math.Ceiling(baseSteps * (b - a) / fullSpan));
                var sub = IsolatePositiveIntervals(
                    t => DomainMargin(request, basis, branch, curve(t), minDomainCosine),
                    a, b, steps,
                    domainSpeed,
                    analyticDomainHints,
                    out var fullyPositive,
                    extraExclusionAllowance: refractionAllowance);
                if (!fullyPositive) allInDomain = false;
                foreach (var piece in sub)
                {
                    domainIntervals.Add((piece.From, piece.To, branch));
                }
            }
            if (!allInDomain) clipped = true;
            branchedIntervals = domainIntervals;
            if (branchedIntervals.Count == 0) return;
        }

        // Stage 3: Adaptive chord subdivision and sensor/aperture boundary bisection on each domain-feasible arc.
        foreach (var (a, b, branch) in branchedIntervals)
        {
            if (b - a <= 1e-14 * Math.Max(1d, fullSpan)) continue;
            var steps = Math.Max(1, (int)Math.Ceiling(baseSteps * (b - a) / fullSpan));
            var step = (b - a) / steps;
            for (var index = 0; index < steps; index++)
            {
                var segFrom = a + index * step;
                var segTo = index == steps - 1 ? b : a + (index + 1) * step;
                AppendFeasibleChord(
                    request, basis, branch, curve, segFrom, segTo, 0, maxDepth,
                    output, ref clipped, ref errorBoundExceeded);
                if (errorBoundExceeded) return;
            }
        }
    }

    internal static IReadOnlyList<double> SolveGreatCirclePlaneRoots(
        EnuVector start,
        EnuVector end,
        EnuVector planeNormal,
        double thresholdC)
    {
        var dot = Math.Clamp(EnuVector.Dot(start, end), -1d, 1d);
        var angle = Math.Acos(dot);
        if (angle <= 1e-12 || angle >= Math.PI - 1e-10) return Array.Empty<double>();
        var sinAngle = Math.Sin(angle);
        var v = (end - start * dot) * (1d / sinAngle);
        var a = EnuVector.Dot(start, planeNormal);
        var b = EnuVector.Dot(v, planeNormal);
        var amp = Math.Sqrt(a * a + b * b);
        if (amp <= 1e-15) return Array.Empty<double>();

        var roots = new List<double>();
        var phi = Math.Atan2(b, a);
        for (var k = -2; k <= 2; k++)
        {
            var sc = phi + k * Math.PI;
            if (sc > 0 && sc < angle) roots.Add(sc / angle);
        }
        var ratio = thresholdC / amp;
        if (Math.Abs(ratio) <= 1d + 1e-12)
        {
            var delta = Math.Acos(Math.Clamp(ratio, -1d, 1d));
            foreach (var raw in new[] { phi - delta, phi + delta })
            {
                for (var k = -2; k <= 2; k++)
                {
                    var s = raw + 2d * Math.PI * k;
                    if (s > 0 && s < angle) roots.Add(s / angle);
                }
            }
        }
        roots.Sort();
        return roots;
    }

    internal static double ComputeMinimumDomainCosine(ProjectionContext projection)
    {
        var k1 = projection.RadialDistortionK1;
        var maxIdeal = RadialDistortion.MaximumIdealRadius(projection.Model, k1);
        return projection.Model switch
        {
            ProjectionModel.Perspective => double.IsFinite(maxIdeal)
                ? 1d / Math.Sqrt(1d + maxIdeal * maxIdeal)
                : 1e-12,
            ProjectionModel.OrthographicFisheye => Math.Cos(Math.Asin(Math.Clamp(maxIdeal, 0d, 1d))),
            ProjectionModel.EquidistantFisheye => Math.Cos(Math.Min(Math.PI, maxIdeal)),
            ProjectionModel.EquisolidFisheye => Math.Cos(2d * Math.Asin(Math.Clamp(maxIdeal / 2d, 0d, 1d))),
            ProjectionModel.StereographicFisheye => Math.Cos(double.IsFinite(maxIdeal) ? 2d * Math.Atan(maxIdeal / 2d) : Math.PI - 1e-12),
            _ => -1d
        };
    }

    private static bool TryProjectGeometryForBranch(
        VisibleSceneRequest request,
        CameraBasis basis,
        RefractionEvaluationBranch branch,
        EnuVector geometricDirection,
        out PixelPoint pixel)
    {
        if (branch == RefractionEvaluationBranch.Default || !request.Refraction.Enabled)
        {
            return VisibleSceneBuilder.TryProjectGeometry(request, basis, geometricDirection, out pixel);
        }
        if (request.HorizonPolicy == HorizonPolicy.GeometricHorizon && geometricDirection.Up < 0)
        {
            pixel = default;
            return false;
        }
        if (branch == RefractionEvaluationBranch.ForceUnrefracted)
        {
            return VisibleSceneBuilder.TryProjectGeometry(
                request.Projection, HorizonPolicy.ProjectionOnly, new RefractionOptions(false), basis, geometricDirection, out pixel);
        }
        var geometric = CameraBasis.ToHorizontal(geometricDirection);
        var evalAlt = Math.Max(geometric.AltitudeDegrees, request.Refraction.MinimumAltitudeDegrees);
        var apparentAlt = AtmosphericRefraction.Apply(evalAlt, request.Refraction);
        var apparent = CameraBasis.FromHorizontal(geometric with { AltitudeDegrees = apparentAlt });
        return VisibleSceneBuilder.TryProjectGeometry(
            request.Projection, HorizonPolicy.ProjectionOnly, new RefractionOptions(false), basis, apparent, out pixel);
    }

    private static bool TryProjectVisibleForBranch(
        VisibleSceneRequest request,
        CameraBasis basis,
        RefractionEvaluationBranch branch,
        EnuVector direction,
        out PixelPoint pixel)
    {
        if (TryProjectGeometryForBranch(request, basis, branch, direction, out pixel) &&
            IsInsideProjection(request.Projection, pixel))
        {
            pixel = ClampToProjection(request.Projection, pixel);
            return true;
        }
        return false;
    }

    private static double DomainMargin(
        VisibleSceneRequest request,
        CameraBasis basis,
        RefractionEvaluationBranch branch,
        EnuVector geometricDirection,
        double minDomainCosine)
    {
        var apparent = geometricDirection;
        if (request.Refraction.Enabled && branch != RefractionEvaluationBranch.ForceUnrefracted)
        {
            var geom = CameraBasis.ToHorizontal(geometricDirection);
            var evalAlt = branch == RefractionEvaluationBranch.ForceRefracted
                ? Math.Max(geom.AltitudeDegrees, request.Refraction.MinimumAltitudeDegrees)
                : geom.AltitudeDegrees;
            var appAlt = AtmosphericRefraction.Apply(evalAlt, request.Refraction);
            apparent = CameraBasis.FromHorizontal(geom with { AltitudeDegrees = appAlt });
        }
        return basis.ToCamera(apparent).Up - minDomainCosine;
    }

    private static List<(double From, double To)> IsolatePositiveIntervals(
        Func<double, double> f,
        double tStart,
        double tEnd,
        int baseSteps,
        double lipschitzBound,
        IReadOnlyList<double>? analyticHints,
        out bool fullyPositive,
        double extraExclusionAllowance = 0d)
    {
        var grid = new List<double>(baseSteps + 1 + (analyticHints?.Count ?? 0));
        for (var i = 0; i <= baseSteps; i++)
        {
            grid.Add(tStart + (tEnd - tStart) * i / baseSteps);
        }
        if (analyticHints is not null)
        {
            foreach (var h in analyticHints)
            {
                if (h > tStart && h < tEnd) grid.Add(h);
            }
        }
        grid.Sort();
        var uniqueGrid = DeduplicateSorted(grid, 1e-13 * Math.Max(1d, tEnd - tStart));
        var values = new double[uniqueGrid.Count];
        for (var i = 0; i < uniqueGrid.Count; i++)
        {
            values[i] = f(uniqueGrid[i]);
        }

        var rawIntervals = new List<(double From, double To)>();
        var anyOutside = false;

        for (var i = 0; i < uniqueGrid.Count - 1; i++)
        {
            var a = uniqueGrid[i];
            var b = uniqueGrid[i + 1];
            var fa = values[i];
            var fb = values[i + 1];
            var inA = fa >= 0d;
            var inB = fb >= 0d;
            var halfWidth = 0.5 * (b - a);
            var maxVariation = lipschitzBound * halfWidth + extraExclusionAllowance;

            if (inA && inB)
            {
                var mid = 0.5 * (a + b);
                var fm = f(mid);
                if (fm < 0d || fm - maxVariation <= 1e-12)
                {
                    var dipT = MinimizeGolden(f, a, b, ConstraintGoldenIterations);
                    var dipVal = f(dipT);
                    if (dipVal < 0d)
                    {
                        anyOutside = true;
                        var exitRoot = BisectInsideRoot(f, a, dipT, aIsInside: true);
                        var enterRoot = BisectInsideRoot(f, dipT, b, aIsInside: false);
                        if (exitRoot > a) rawIntervals.Add((a, exitRoot));
                        if (b > enterRoot) rawIntervals.Add((enterRoot, b));
                        continue;
                    }
                }
                rawIntervals.Add((a, b));
            }
            else if (inA != inB)
            {
                anyOutside = true;
                var root = BisectInsideRoot(f, a, b, aIsInside: inA);
                if (inA)
                {
                    if (root > a) rawIntervals.Add((a, root));
                }
                else
                {
                    if (b > root) rawIntervals.Add((root, b));
                }
            }
            else
            {
                anyOutside = true;
                var mid = 0.5 * (a + b);
                var fm = f(mid);
                if (fm >= 0d || fm + maxVariation >= -1e-12)
                {
                    var peakT = MaximizeGolden(f, a, b, ConstraintGoldenIterations);
                    var peakVal = f(peakT);
                    if (peakVal > 0d)
                    {
                        var enterRoot = BisectInsideRoot(f, a, peakT, aIsInside: false);
                        var exitRoot = BisectInsideRoot(f, peakT, b, aIsInside: true);
                        if (exitRoot > enterRoot) rawIntervals.Add((enterRoot, exitRoot));
                    }
                }
            }
        }

        if (!anyOutside && rawIntervals.Count > 0)
        {
            fullyPositive = true;
            return [(tStart, tEnd)];
        }

        fullyPositive = false;
        if (rawIntervals.Count <= 1) return rawIntervals;

        var merged = new List<(double From, double To)>(rawIntervals.Count) { rawIntervals[0] };
        var mergeTol = 1e-13 * Math.Max(1d, tEnd - tStart);
        for (var i = 1; i < rawIntervals.Count; i++)
        {
            var last = merged[^1];
            var next = rawIntervals[i];
            if (next.From - last.To <= mergeTol)
            {
                merged[^1] = (last.From, Math.Max(last.To, next.To));
            }
            else
            {
                merged.Add(next);
            }
        }
        return merged;
    }

    private static double BisectInsideRoot(Func<double, double> f, double a, double b, bool aIsInside)
    {
        var inside = aIsInside ? a : b;
        var outside = aIsInside ? b : a;
        for (var i = 0; i < ConstraintBisectionIterations; i++)
        {
            var mid = 0.5 * (inside + outside);
            if (f(mid) >= 0d) inside = mid;
            else outside = mid;
        }
        return inside;
    }

    private static double MaximizeGolden(Func<double, double> f, double a, double b, int iterations)
    {
        const double invPhi = 0.6180339887498949;
        var c = b - invPhi * (b - a);
        var d = a + invPhi * (b - a);
        var fc = f(c);
        var fd = f(d);
        var bestT = fc >= fd ? c : d;
        var bestV = Math.Max(fc, fd);
        for (var i = 0; i < iterations; i++)
        {
            if (fc < fd)
            {
                a = c;
                c = d;
                fc = fd;
                d = a + invPhi * (b - a);
                fd = f(d);
                if (fd > bestV) { bestV = fd; bestT = d; }
            }
            else
            {
                b = d;
                d = c;
                fd = fc;
                c = b - invPhi * (b - a);
                fc = f(c);
                if (fc > bestV) { bestV = fc; bestT = c; }
            }
        }
        return bestT;
    }

    private static double MinimizeGolden(Func<double, double> f, double a, double b, int iterations) =>
        MaximizeGolden(t => -f(t), a, b, iterations);

    private static List<double> DeduplicateSorted(List<double> sorted, double tolerance)
    {
        var result = new List<double>(sorted.Count);
        foreach (var value in sorted)
        {
            if (result.Count == 0 || value - result[^1] > tolerance) result.Add(value);
        }
        return result;
    }

    private static double PixelSignedMargin(ProjectionContext projection, PixelPoint pixel)
    {
        var margin = double.PositiveInfinity;
        if (projection.EnforceSensorBounds)
        {
            margin = Math.Min(margin, pixel.X);
            margin = Math.Min(margin, projection.WidthPixels - pixel.X);
            margin = Math.Min(margin, pixel.Y);
            margin = Math.Min(margin, projection.HeightPixels - pixel.Y);
        }
        if (projection.Aperture == ProjectionAperture.Circular)
        {
            var dx = pixel.X - projection.PrincipalPointX;
            var dy = pixel.Y - projection.PrincipalPointY;
            margin = Math.Min(margin, projection.ImageCircleRadiusPixels!.Value - Math.Sqrt(dx * dx + dy * dy));
        }
        return margin;
    }

    private static void AppendFeasibleChord(
        VisibleSceneRequest request,
        CameraBasis basis,
        RefractionEvaluationBranch branch,
        Func<double, EnuVector> curve,
        double from,
        double to,
        int depth,
        int maxDepth,
        List<BranchedChord> output,
        ref bool clipped,
        ref bool errorBoundExceeded)
    {
        if (errorBoundExceeded) return;
        var fromValid = TryProjectGeometryForBranch(request, basis, branch, curve(from), out var fromPixel);
        var toValid = TryProjectGeometryForBranch(request, basis, branch, curve(to), out var toPixel);
        var fromIn = fromValid && IsInsideProjection(request.Projection, fromPixel);
        var toIn = toValid && IsInsideProjection(request.Projection, toPixel);
        var middle = 0.5 * (from + to);

        if (fromIn && toIn)
        {
            var middleIn = TryProjectVisibleForBranch(request, basis, branch, curve(middle), out var middlePixel);
            var clampedFrom = ClampToProjection(request.Projection, fromPixel);
            var clampedTo = ClampToProjection(request.Projection, toPixel);
            var sagitta = middleIn
                ? Distance(middlePixel, new PixelPoint(0.5 * (clampedFrom.X + clampedTo.X), 0.5 * (clampedFrom.Y + clampedTo.Y)))
                : double.PositiveInfinity;
            if (!middleIn || sagitta > MaximumChordErrorPixels)
            {
                if (depth < maxDepth)
                {
                    AppendFeasibleChord(request, basis, branch, curve, from, middle, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                    AppendFeasibleChord(request, basis, branch, curve, middle, to, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                }
                else if (!middleIn)
                {
                    // Midpoint exited the projection at maximum depth: split cleanly across the exterior dip.
                    AppendBoundaryExcursionSplit(request, basis, branch, curve, from, middle, to, depth, maxDepth, output, ref clipped, ref errorBoundExceeded);
                }
                else
                {
                    // Never silently bridge an out-of-tolerance chord when subdivision budget is exhausted.
                    clipped = true;
                    errorBoundExceeded = true;
                }
                return;
            }

            // Detect curved sensor or circular-aperture boundary excursions between endpoints and midpoint
            // before accepting an interior chord as unclipped.
            var minMargin = Math.Min(
                PixelSignedMargin(request.Projection, fromPixel),
                Math.Min(PixelSignedMargin(request.Projection, middlePixel), PixelSignedMargin(request.Projection, toPixel)));
            var chordLength = Distance(clampedFrom, clampedTo);
            var excursionGuard = Math.Max(4d * sagitta + 0.1, Math.Min(2d, 0.25 * chordLength));
            if (double.IsFinite(minMargin) && minMargin <= excursionGuard)
            {
                double EvalMargin(double t) =>
                    TryProjectGeometryForBranch(request, basis, branch, curve(t), out var p)
                        ? PixelSignedMargin(request.Projection, p)
                        : double.NegativeInfinity;

                var dipLeft = MinimizeGolden(EvalMargin, from, middle, ConstraintGoldenIterations);
                var leftOut = !TryProjectVisibleForBranch(request, basis, branch, curve(dipLeft), out _);
                var dipRight = MinimizeGolden(EvalMargin, middle, to, ConstraintGoldenIterations);
                var rightOut = !TryProjectVisibleForBranch(request, basis, branch, curve(dipRight), out _);
                if (leftOut || rightOut)
                {
                    clipped = true;
                    if (leftOut)
                    {
                        AppendBoundaryExcursionSplit(request, basis, branch, curve, from, dipLeft, middle, depth, maxDepth, output, ref clipped, ref errorBoundExceeded);
                    }
                    else if (depth < maxDepth)
                    {
                        AppendFeasibleChord(request, basis, branch, curve, from, middle, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                    }
                    else
                    {
                        AppendVerifiedInsideArc(request, basis, branch, curve, from, clampedFrom, middle, middlePixel, 0, 0, output, ref clipped, ref errorBoundExceeded);
                    }

                    if (errorBoundExceeded) return;

                    if (rightOut)
                    {
                        AppendBoundaryExcursionSplit(request, basis, branch, curve, middle, dipRight, to, depth, maxDepth, output, ref clipped, ref errorBoundExceeded);
                    }
                    else if (depth < maxDepth)
                    {
                        AppendFeasibleChord(request, basis, branch, curve, middle, to, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                    }
                    else
                    {
                        AppendVerifiedInsideArc(request, basis, branch, curve, middle, middlePixel, to, clampedTo, 0, 0, output, ref clipped, ref errorBoundExceeded);
                    }
                    return;
                }
            }

            output.Add(new BranchedChord(clampedFrom, clampedTo, branch));
            return;
        }

        if (fromIn != toIn)
        {
            if (depth < maxDepth)
            {
                AppendFeasibleChord(request, basis, branch, curve, from, middle, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                AppendFeasibleChord(request, basis, branch, curve, middle, to, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                return;
            }

            clipped = true;
            var insideStart = fromIn ? from : to;
            var insideParam = insideStart;
            var outsideParam = fromIn ? to : from;
            var insidePixel = ClampToProjection(request.Projection, fromIn ? fromPixel : toPixel);
            var boundaryPixel = insidePixel;
            for (var iteration = 0; iteration < ConstraintBisectionIterations; iteration++)
            {
                var candidate = 0.5 * (insideParam + outsideParam);
                if (TryProjectVisibleForBranch(request, basis, branch, curve(candidate), out var candidatePixel))
                {
                    insideParam = candidate;
                    boundaryPixel = candidatePixel;
                }
                else
                {
                    outsideParam = candidate;
                }
            }

            if (fromIn)
            {
                AppendVerifiedInsideArc(request, basis, branch, curve, insideStart, insidePixel, insideParam, boundaryPixel, 0, 4, output, ref clipped, ref errorBoundExceeded);
            }
            else
            {
                AppendVerifiedInsideArc(request, basis, branch, curve, insideParam, boundaryPixel, insideStart, insidePixel, 0, 4, output, ref clipped, ref errorBoundExceeded);
            }
            return;
        }

        // Both endpoints are outside the visible sensor/aperture (or residual domain corner).
        clipped = true;
        var midValid = TryProjectGeometryForBranch(request, basis, branch, curve(middle), out var midPixel);
        var midIn = midValid && IsInsideProjection(request.Projection, midPixel);
        if (depth < maxDepth)
        {
            if (midIn || fromValid != toValid || fromValid != midValid)
            {
                AppendFeasibleChord(request, basis, branch, curve, from, middle, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                AppendFeasibleChord(request, basis, branch, curve, middle, to, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                return;
            }
            if (!fromValid)
            {
                return;
            }
            var sagitta = Distance(midPixel, new PixelPoint(0.5 * (fromPixel.X + toPixel.X), 0.5 * (fromPixel.Y + toPixel.Y)));
            var margin = 2 * sagitta + 1d;
            if (VisibleSceneBuilder.TryClipToProjection(request.Projection, fromPixel, toPixel, out _, out _) ||
                CanApproachProjection(request.Projection, fromPixel, midPixel, toPixel, margin) ||
                depth == 0 && Math.Max(PixelSignedMargin(request.Projection, fromPixel),
                    Math.Max(PixelSignedMargin(request.Projection, midPixel), PixelSignedMargin(request.Projection, toPixel))) > -50d)
            {
                AppendFeasibleChord(request, basis, branch, curve, from, middle, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
                AppendFeasibleChord(request, basis, branch, curve, middle, to, depth + 1, maxDepth, output, ref clipped, ref errorBoundExceeded);
            }
            return;
        }

        // At leaf depth with both endpoints outside sensor/aperture: probe via golden-section for a tiny incursion
        // if the chord approaches the boundary, emitting verified interior chords only when a genuinely visible point exists.
        if (fromValid && toValid && midValid)
        {
            var sagitta = Distance(midPixel, new PixelPoint(0.5 * (fromPixel.X + toPixel.X), 0.5 * (fromPixel.Y + toPixel.Y)));
            if (CanApproachProjection(request.Projection, fromPixel, midPixel, toPixel, 2 * sagitta + 1d))
            {
                double EvalMargin(double t) =>
                    TryProjectGeometryForBranch(request, basis, branch, curve(t), out var p)
                        ? PixelSignedMargin(request.Projection, p)
                        : double.NegativeInfinity;
                var peakT = MaximizeGolden(EvalMargin, from, to, 48);
                if (TryProjectVisibleForBranch(request, basis, branch, curve(peakT), out var peakPixel))
                {
                    var enterT = BisectInsideRoot(EvalMargin, from, peakT, aIsInside: false);
                    var exitT = BisectInsideRoot(EvalMargin, peakT, to, aIsInside: true);
                    if (TryProjectVisibleForBranch(request, basis, branch, curve(enterT), out var enterPixel) &&
                        TryProjectVisibleForBranch(request, basis, branch, curve(exitT), out var exitPixel))
                    {
                        AppendVerifiedInsideArc(request, basis, branch, curve, enterT, enterPixel, peakT, peakPixel, 0, 4, output, ref clipped, ref errorBoundExceeded);
                        AppendVerifiedInsideArc(request, basis, branch, curve, peakT, peakPixel, exitT, exitPixel, 0, 4, output, ref clipped, ref errorBoundExceeded);
                    }
                }
            }
        }
    }

    private static void AppendVerifiedInsideArc(
        VisibleSceneRequest request,
        CameraBasis basis,
        RefractionEvaluationBranch branch,
        Func<double, EnuVector> curve,
        double paramA,
        PixelPoint pixelA,
        double paramB,
        PixelPoint pixelB,
        int leafDepth,
        int maxLeafDepth,
        List<BranchedChord> output,
        ref bool clipped,
        ref bool errorBoundExceeded)
    {
        if (errorBoundExceeded) return;
        if (Distance(pixelA, pixelB) <= JoinTolerancePixels) return;
        var midParam = 0.5 * (paramA + paramB);
        if (!TryProjectVisibleForBranch(request, basis, branch, curve(midParam), out var midPixel))
        {
            clipped = true;
            errorBoundExceeded = true;
            return;
        }
        var sagitta = Distance(midPixel, new PixelPoint(0.5 * (pixelA.X + pixelB.X), 0.5 * (pixelA.Y + pixelB.Y)));
        if (sagitta <= MaximumChordErrorPixels)
        {
            output.Add(new BranchedChord(pixelA, pixelB, branch));
            return;
        }
        if (leafDepth < maxLeafDepth)
        {
            AppendVerifiedInsideArc(request, basis, branch, curve, paramA, pixelA, midParam, midPixel, leafDepth + 1, maxLeafDepth, output, ref clipped, ref errorBoundExceeded);
            AppendVerifiedInsideArc(request, basis, branch, curve, midParam, midPixel, paramB, pixelB, leafDepth + 1, maxLeafDepth, output, ref clipped, ref errorBoundExceeded);
            return;
        }
        clipped = true;
        errorBoundExceeded = true;
    }

    private static void AppendBoundaryExcursionSplit(
        VisibleSceneRequest request,
        CameraBasis basis,
        RefractionEvaluationBranch branch,
        Func<double, EnuVector> curve,
        double a,
        double dipOut,
        double b,
        int depth,
        int maxDepth,
        List<BranchedChord> output,
        ref bool clipped,
        ref bool errorBoundExceeded)
    {
        clipped = true;
        var nextDepth = Math.Min(depth + 1, maxDepth);
        AppendFeasibleChord(request, basis, branch, curve, a, dipOut, nextDepth, maxDepth, output, ref clipped, ref errorBoundExceeded);
        AppendFeasibleChord(request, basis, branch, curve, dipOut, b, nextDepth, maxDepth, output, ref clipped, ref errorBoundExceeded);
    }

    private static bool CanApproachProjection(
        ProjectionContext projection,
        PixelPoint from,
        PixelPoint mid,
        PixelPoint to,
        double margin)
    {
        var minX = Math.Min(from.X, Math.Min(mid.X, to.X)) - margin;
        var maxX = Math.Max(from.X, Math.Max(mid.X, to.X)) + margin;
        var minY = Math.Min(from.Y, Math.Min(mid.Y, to.Y)) - margin;
        var maxY = Math.Max(from.Y, Math.Max(mid.Y, to.Y)) + margin;
        if (projection.EnforceSensorBounds &&
            (maxX < 0 || minX > projection.WidthPixels || maxY < 0 || minY > projection.HeightPixels))
            return false;
        if (projection.Aperture == ProjectionAperture.Circular)
        {
            var cx = projection.PrincipalPointX;
            var cy = projection.PrincipalPointY;
            var closestX = Math.Clamp(cx, minX, maxX);
            var closestY = Math.Clamp(cy, minY, maxY);
            var dx = closestX - cx;
            var dy = closestY - cy;
            var limit = projection.ImageCircleRadiusPixels!.Value + margin;
            if (dx * dx + dy * dy > limit * limit)
                return false;
        }
        return true;
    }

    internal static double Distance(PixelPoint left, PixelPoint right) =>
        Math.Sqrt(Math.Pow(left.X - right.X, 2) + Math.Pow(left.Y - right.Y, 2));
}
