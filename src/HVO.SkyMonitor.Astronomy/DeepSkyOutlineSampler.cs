using System.Collections.ObjectModel;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Honest outcome of projecting or validating a sourced deep-sky outline.</summary>
public enum DeepSkyOutlineSampleStatus
{
    /// <summary>One or more clipped or unclipped polyline parts lie inside the visible projection.</summary>
    Valid,

    /// <summary>Every outline ring is geometrically valid, and none intersects the visible projection.</summary>
    OutsideView,

    /// <summary>At least one consecutive edge is antipodal or near-antipodal and defines no unique great circle.</summary>
    InvalidAntipodalEdge,

    /// <summary>The outline is degenerate, spans outside the valid hemisphere, or exceeds structural polyline limits.</summary>
    InvalidGeometry
}

/// <summary>Identifies one rejected outline edge and its angular separation in degrees.</summary>
public sealed record DeepSkyOutlineEdgeDiagnostic(
    int RingIndex,
    int EdgeIndex,
    double SeparationDegrees);

/// <summary>Result of projecting a catalog deep-sky outline without aborting sibling scene objects.</summary>
public sealed record DeepSkyOutlineSampleResult(
    DeepSkyOutlineSampleStatus Status,
    ProjectedDeepSkyOutline? Outline,
    IReadOnlyList<DeepSkyOutlineEdgeDiagnostic> RejectedEdges);

/// <summary>
/// Shared antipodal-safe great-circle outline projection, clipping, and gnomonic tangent-plane utilities for sourced
/// deep-sky contours. A catalog-valid ring with antipodal or near-antipodal consecutive vertices never throws to abort
/// a scene and never invents an ambiguous great-circle route.
/// </summary>
public static class DeepSkyOutlineSampler
{
    /// <summary>
    /// Minimum angular distance from 180 degrees required for a unique shortest great-circle arc. Safely exceeds
    /// <see cref="EnuVector.SphericalInterpolate"/>'s antipodal guard so well-conditioned slerp never throws.
    /// </summary>
    public const double AntipodalExclusionMarginRadians = 1e-5;

    /// <summary>Maximum angular step per base great-circle chord before adaptive subdivision.</summary>
    public const double MaximumBaseArcStepDegrees = 2.0;

    /// <summary>Maximum subdivision depth per base great-circle segment.</summary>
    public const int MaximumSubdivisionDepth = ResolvedFootprintSampler.MaximumSubdivisionDepth;

    /// <summary>Minimum cosine of angular distance from object center allowed for exact gnomonic tangent-plane mapping.</summary>
    public const double MinimumTangentCosine = 0.05;

    /// <summary>
    /// Validates every ring of a catalog outline for antipodal, near-antipodal, or degenerate edges without throwing.
    /// </summary>
    public static IReadOnlyList<DeepSkyOutlineEdgeDiagnostic> FindInvalidEdges(DeepSkyOutline outline)
    {
        ArgumentNullException.ThrowIfNull(outline);
        if (outline.Rings is not { Count: > 0 })
        {
            return [new DeepSkyOutlineEdgeDiagnostic(0, 0, double.NaN)];
        }

        var rejected = new List<DeepSkyOutlineEdgeDiagnostic>();
        var maximumAllowedRadians = Math.PI - AntipodalExclusionMarginRadians;
        for (var ringIndex = 0; ringIndex < outline.Rings.Count; ringIndex++)
        {
            var ring = outline.Rings[ringIndex];
            if (ring?.Points is not { Count: >= DeepSkyCatalog.MinimumRingPointCount } points || points[0] != points[^1])
            {
                rejected.Add(new DeepSkyOutlineEdgeDiagnostic(ringIndex, 0, double.NaN));
                continue;
            }

            EnuVector? previous = null;
            for (var pointIndex = 0; pointIndex < points.Count; pointIndex++)
            {
                var point = points[pointIndex];
                if (!double.IsFinite(point.RightAscensionDegrees) || point.RightAscensionDegrees is < 0 or >= 360 ||
                    !double.IsFinite(point.DeclinationDegrees) || point.DeclinationDegrees is < -90 or > 90)
                {
                    rejected.Add(new DeepSkyOutlineEdgeDiagnostic(ringIndex, Math.Max(0, pointIndex - 1), double.NaN));
                    previous = null;
                    continue;
                }

                var current = EquatorialUnitVector(point.RightAscensionDegrees, point.DeclinationDegrees);
                if (previous is { } start)
                {
                    var angle = AngleRadians(start, current);
                    if (!double.IsFinite(angle) || angle <= 1e-12 || angle >= maximumAllowedRadians)
                    {
                        rejected.Add(new DeepSkyOutlineEdgeDiagnostic(ringIndex, pointIndex - 1, angle * 180d / Math.PI));
                    }
                }
                previous = current;
            }
        }
        return new ReadOnlyCollection<DeepSkyOutlineEdgeDiagnostic>(rejected);
    }

    /// <summary>
    /// Rotates a J2000 catalog position angle (measured from north through east) to north of date.
    /// </summary>
    public static double PositionAngleOfDate(
        double rightAscensionHours,
        double declinationDegrees,
        double positionAngleDegrees,
        DateTimeOffset utc) =>
        DeepSkySceneProjector.PositionAngleOfDate(rightAscensionHours, declinationDegrees, positionAngleDegrees, utc);

    /// <summary>
    /// Projects and clips a catalog deep-sky outline into output pixels using the same horizon, refraction, distortion,
    /// sensor, and aperture conventions as <see cref="ResolvedFootprintSampler"/>.
    /// </summary>
    public static DeepSkyOutlineSampleResult Sample(
        VisibleSceneRequest request,
        DeepSkyOutline outline,
        string? sceneObjectId = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outline);
        var invalidEdges = FindInvalidEdges(outline);
        if (invalidEdges.Count > 0)
        {
            var hasAntipodal = invalidEdges.Any(static edge =>
                double.IsFinite(edge.SeparationDegrees) &&
                edge.SeparationDegrees >= (Math.PI - AntipodalExclusionMarginRadians) * 180d / Math.PI - 1e-9);
            return new DeepSkyOutlineSampleResult(
                hasAntipodal ? DeepSkyOutlineSampleStatus.InvalidAntipodalEdge : DeepSkyOutlineSampleStatus.InvalidGeometry,
                null,
                invalidEdges);
        }

        var basis = CameraBasis.Create(
            request.Projection.BoresightAltitudeDegrees,
            request.Projection.BoresightAzimuthDegrees,
            request.Projection.RollDegrees,
            request.Projection.HorizontalFlip);

        var allParts = new List<ResolvedFootprintPart>();
        var totalPoints = 0;
        var anyClipped = false;
        var baseStepRadians = MaximumBaseArcStepDegrees * Math.PI / 180d;

        foreach (var ring in outline.Rings)
        {
            var directions = new EnuVector[ring.Points.Count];
            for (var index = 0; index < ring.Points.Count; index++)
            {
                var point = ring.Points[index];
                var j2000 = new EquatorialPoint(point.RightAscensionDegrees / 15d, point.DeclinationDegrees);
                var horizontal = CoordinateTransforms.EquatorialToHorizontal(
                    EquatorialPrecession.PrecessJ2000(j2000, request.Utc),
                    request.Utc,
                    request.Observer.LatitudeDegrees,
                    request.Observer.LongitudeDegrees);
                directions[index] = CameraBasis.FromHorizontal(horizontal);
            }

            var ringChords = new List<(PixelPoint From, PixelPoint To)>();
            var ringClipped = false;
            var boresight = CameraBasis.FromHorizontal(new AltAzPoint(
                request.Projection.BoresightAltitudeDegrees,
                request.Projection.BoresightAzimuthDegrees));
            var minDomainCosine = ResolvedFootprintSampler.ComputeMinimumDomainCosine(request.Projection);
            for (var edgeIndex = 0; edgeIndex < directions.Length - 1; edgeIndex++)
            {
                var start = directions[edgeIndex];
                var end = directions[edgeIndex + 1];
                var angle = AngleRadians(start, end);
                if (!double.IsFinite(angle) || angle >= Math.PI - AntipodalExclusionMarginRadians)
                {
                    return new DeepSkyOutlineSampleResult(
                        DeepSkyOutlineSampleStatus.InvalidAntipodalEdge,
                        null,
                        [new DeepSkyOutlineEdgeDiagnostic(0, edgeIndex, angle * 180d / Math.PI)]);
                }

                var steps = Math.Max(1, (int)Math.Ceiling(angle / baseStepRadians));
                EnuVector Arc(double t) => SafeSlerp(start, end, angle, t);
                var horizonHints = request.HorizonPolicy == HorizonPolicy.GeometricHorizon
                    ? ResolvedFootprintSampler.SolveGreatCirclePlaneRoots(start, end, new EnuVector(0, 0, 1), 0d)
                    : null;
                var domainHints = !request.Refraction.Enabled && minDomainCosine > -1d + 1e-12
                    ? ResolvedFootprintSampler.SolveGreatCirclePlaneRoots(start, end, boresight, minDomainCosine)
                    : null;
                ResolvedFootprintSampler.AppendBoundarySafeCurve(
                    request, basis, Arc, 0d, 1d, steps, angle, horizonHints, domainHints,
                    MaximumSubdivisionDepth, ringChords, ref ringClipped);
                if (ringChords.Count > ProjectedDeepSkyOutline.MaximumPointCount * 2)
                {
                    return new DeepSkyOutlineSampleResult(
                        DeepSkyOutlineSampleStatus.InvalidGeometry,
                        null,
                        Array.Empty<DeepSkyOutlineEdgeDiagnostic>());
                }
            }

            var ringParts = ResolvedFootprintSampler.Stitch(ringChords, closedCandidate: !ringClipped);
            anyClipped |= ringClipped || ringParts.Count != 1 || !ringParts[0].Closed;
            foreach (var part in ringParts)
            {
                totalPoints += part.Points.Count;
                allParts.Add(part);
                if (allParts.Count > ProjectedDeepSkyOutline.MaximumPartCount ||
                    totalPoints > ProjectedDeepSkyOutline.MaximumPointCount)
                {
                    return new DeepSkyOutlineSampleResult(
                        DeepSkyOutlineSampleStatus.InvalidGeometry,
                        null,
                        Array.Empty<DeepSkyOutlineEdgeDiagnostic>());
                }
            }
        }

        if (allParts.Count == 0)
        {
            return new DeepSkyOutlineSampleResult(
                DeepSkyOutlineSampleStatus.OutsideView,
                null,
                Array.Empty<DeepSkyOutlineEdgeDiagnostic>());
        }

        var id = sceneObjectId ?? ProjectedDeepSkyObject.SceneId(outline.ObjectId);
        var projected = new ProjectedDeepSkyOutline(
            id,
            ProjectedDeepSkyOutline.CurrentContractVersion,
            ProjectedDeepSkyOutline.SamplingAlgorithm,
            outline.Level,
            outline.Rings.Count,
            request.Refraction.Enabled ? AtmosphericRefraction.ModelVersion : null,
            anyClipped || !ProjectedDeepSkyOutline.IsComplete(allParts, outline.Rings.Count),
            ResolvedFootprintSampler.ComputeBounds(allParts),
            new ReadOnlyCollection<ResolvedFootprintPart>(allParts));
        return new DeepSkyOutlineSampleResult(
            DeepSkyOutlineSampleStatus.Valid,
            projected,
            Array.Empty<DeepSkyOutlineEdgeDiagnostic>());
    }

    /// <summary>
    /// Maps a valid catalog outline into the gnomonic tangent plane of <see cref="ResolvedSourceFrame"/> at the
    /// object's geometric centre. Because gnomonic projection maps great circles to exact straight lines, spherical
    /// polygon containment on the sky is exact in this plane without RA-wrap or pole singularities.
    /// Coordinates are <c>(east, north)</c> in tangent-plane units where a point <c>(u, v)</c> maps to the sky
    /// direction <c>(Center + CelestialEast * u + CelestialNorth * v).Normalize()</c>.
    /// </summary>
    public static bool TryProjectToGnomonicTangentPlane(
        DeepSkyOutline outline,
        ResolvedSourceFrame frame,
        DateTimeOffset utc,
        double observerLatitudeDegrees,
        double observerLongitudeDegrees,
        out IReadOnlyList<PixelPoint[]> tangentRings,
        out DeepSkyOutlineSampleStatus status)
    {
        ArgumentNullException.ThrowIfNull(outline);
        tangentRings = Array.Empty<PixelPoint[]>();
        var invalidEdges = FindInvalidEdges(outline);
        if (invalidEdges.Count > 0)
        {
            var hasAntipodal = invalidEdges.Any(static edge =>
                double.IsFinite(edge.SeparationDegrees) &&
                edge.SeparationDegrees >= (Math.PI - AntipodalExclusionMarginRadians) * 180d / Math.PI - 1e-9);
            status = hasAntipodal ? DeepSkyOutlineSampleStatus.InvalidAntipodalEdge : DeepSkyOutlineSampleStatus.InvalidGeometry;
            return false;
        }

        var rings = new List<PixelPoint[]>(outline.Rings.Count);
        foreach (var ring in outline.Rings)
        {
            var vertices = new PixelPoint[ring.Points.Count];
            for (var index = 0; index < ring.Points.Count; index++)
            {
                var point = ring.Points[index];
                var j2000 = new EquatorialPoint(point.RightAscensionDegrees / 15d, point.DeclinationDegrees);
                var horizontal = CoordinateTransforms.EquatorialToHorizontal(
                    EquatorialPrecession.PrecessJ2000(j2000, utc),
                    utc,
                    observerLatitudeDegrees,
                    observerLongitudeDegrees);
                var direction = CameraBasis.FromHorizontal(horizontal);
                var cosine = EnuVector.Dot(direction, frame.Center);
                if (!double.IsFinite(cosine) || cosine <= MinimumTangentCosine)
                {
                    status = DeepSkyOutlineSampleStatus.InvalidGeometry;
                    return false;
                }
                var u = EnuVector.Dot(direction, frame.CelestialEast) / cosine;
                var v = EnuVector.Dot(direction, frame.CelestialNorth) / cosine;
                if (!double.IsFinite(u) || !double.IsFinite(v))
                {
                    status = DeepSkyOutlineSampleStatus.InvalidGeometry;
                    return false;
                }
                vertices[index] = new PixelPoint(u, v);
            }
            rings.Add(vertices);
        }

        tangentRings = new ReadOnlyCollection<PixelPoint[]>(rings);
        status = DeepSkyOutlineSampleStatus.Valid;
        return true;
    }

    /// <summary>Tests whether tangent-plane point <c>(u, v)</c> lies inside the multi-ring outline (even-odd rule).</summary>
    public static bool ContainsTangentPoint(IReadOnlyList<PixelPoint[]> tangentRings, double u, double v)
    {
        ArgumentNullException.ThrowIfNull(tangentRings);
        var inside = false;
        for (var r = 0; r < tangentRings.Count; r++)
        {
            var ring = tangentRings[r];
            for (var i = 0; i < ring.Length - 1; i++)
            {
                var a = ring[i];
                var b = ring[i + 1];
                if ((a.Y > v) != (b.Y > v))
                {
                    var crossX = a.X + (v - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                    if (u < crossX)
                    {
                        inside = !inside;
                    }
                }
            }
        }
        return inside;
    }

    /// <summary>Returns the minimum Euclidean distance from <c>(u, v)</c> to any edge of the tangent-plane outline.</summary>
    public static double DistanceToTangentBoundary(IReadOnlyList<PixelPoint[]> tangentRings, double u, double v)
    {
        ArgumentNullException.ThrowIfNull(tangentRings);
        var minimumSquared = double.PositiveInfinity;
        for (var r = 0; r < tangentRings.Count; r++)
        {
            var ring = tangentRings[r];
            for (var i = 0; i < ring.Length - 1; i++)
            {
                var a = ring[i];
                var b = ring[i + 1];
                var dx = b.X - a.X;
                var dy = b.Y - a.Y;
                var lenSq = dx * dx + dy * dy;
                double distSq;
                if (lenSq <= 1e-24)
                {
                    distSq = (u - a.X) * (u - a.X) + (v - a.Y) * (v - a.Y);
                }
                else
                {
                    var t = Math.Clamp(((u - a.X) * dx + (v - a.Y) * dy) / lenSq, 0d, 1d);
                    var px = a.X + t * dx - u;
                    var py = a.Y + t * dy - v;
                    distSq = px * px + py * py;
                }
                if (distSq < minimumSquared)
                {
                    minimumSquared = distSq;
                }
            }
        }
        return double.IsFinite(minimumSquared) ? Math.Sqrt(minimumSquared) : 0d;
    }

    private static EnuVector SafeSlerp(EnuVector start, EnuVector end, double angle, double t)
    {
        if (t <= 0) return start;
        if (t >= 1) return end;
        if (angle < 1e-12)
        {
            return (start * (1d - t) + end * t).Normalize();
        }
        var sinAngle = Math.Sin(angle);
        var weightStart = Math.Sin((1d - t) * angle) / sinAngle;
        var weightEnd = Math.Sin(t * angle) / sinAngle;
        return (start * weightStart + end * weightEnd).Normalize();
    }

    private static EnuVector EquatorialUnitVector(double rightAscensionDegrees, double declinationDegrees)
    {
        var ra = rightAscensionDegrees * Math.PI / 180d;
        var dec = declinationDegrees * Math.PI / 180d;
        var cosDec = Math.Cos(dec);
        return new EnuVector(cosDec * Math.Cos(ra), cosDec * Math.Sin(ra), Math.Sin(dec));
    }

    private static double AngleRadians(EnuVector left, EnuVector right)
    {
        var cross = EnuVector.Cross(left, right).Length;
        var dot = EnuVector.Dot(left, right);
        return Math.Atan2(cross, dot);
    }
}
