using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>The calibrated optical aperture of one projected scene, evaluated in emitted-image pixel-edge coordinates.</summary>
public sealed class ProjectedSceneAperture
{
    private readonly ProjectionContext projection;
    private readonly double ox, oy, xx, xy, yx, yy;

    public ProjectedSceneAperture(ProjectedSceneV1 scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var p = scene.Projection;
        projection = new ProjectionContext(p.Model, p.PrincipalPointX, p.PrincipalPointY, p.FocalLengthXPixels,
            p.FocalLengthYPixels, p.WidthPixels, p.HeightPixels, p.Aperture, p.ImageCircleRadiusPixels,
            p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees, p.RollDegrees, p.HorizontalFlip,
            p.EnforceSensorBounds, p.RadialDistortionK1);
        var transform = scene.ImageTransform;
        WidthPixels = transform.OutputWidthPixels;
        HeightPixels = transform.OutputHeightPixels;
        // The crop-bin-mirror-rotation transform is affine, so three exact inverse points define it everywhere.
        var origin = ProjectedSceneImageTransform.Inverse(transform, new PixelPoint(0, 0));
        var unitX = ProjectedSceneImageTransform.Inverse(transform, new PixelPoint(1, 0));
        var unitY = ProjectedSceneImageTransform.Inverse(transform, new PixelPoint(0, 1));
        (ox, oy) = (origin.X, origin.Y);
        (xx, xy) = (unitX.X - origin.X, unitX.Y - origin.Y);
        (yx, yy) = (unitY.X - origin.X, unitY.Y - origin.Y);
    }

    public int WidthPixels { get; }
    public int HeightPixels { get; }

    /// <summary>Returns whether an emitted-image point lies inside the image and the calibrated aperture.</summary>
    public bool Contains(PixelPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
            point.X < 0 || point.Y < 0 || point.X > WidthPixels || point.Y > HeightPixels)
            return false;
        return projection.ContainsSample(ox + point.X * xx + point.Y * yx, oy + point.X * xy + point.Y * yy);
    }

    /// <summary>Returns whether every point within the margin, sampled on eight compass directions, is contained.</summary>
    public bool ContainsWithMargin(PixelPoint point, double marginPixels)
    {
        if (!Contains(point)) return false;
        if (marginPixels <= 0) return true;
        var diagonal = marginPixels * Math.Sqrt(.5);
        return Contains(new(point.X - marginPixels, point.Y)) && Contains(new(point.X + marginPixels, point.Y)) &&
            Contains(new(point.X, point.Y - marginPixels)) && Contains(new(point.X, point.Y + marginPixels)) &&
            Contains(new(point.X - diagonal, point.Y - diagonal)) && Contains(new(point.X + diagonal, point.Y - diagonal)) &&
            Contains(new(point.X - diagonal, point.Y + diagonal)) && Contains(new(point.X + diagonal, point.Y + diagonal));
    }

    /// <summary>Creates the row-major valid mask over output sample centers.</summary>
    public bool[] CreateSampleMask(CancellationToken cancellationToken = default)
    {
        var mask = new bool[checked(WidthPixels * HeightPixels)];
        for (var y = 0; y < HeightPixels; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var x = 0; x < WidthPixels; x++)
                mask[y * WidthPixels + x] = Contains(new(x + .5, y + .5));
        }
        return mask;
    }
}

/// <summary>Owned matcher output, ordered for canonical serialization.</summary>
public sealed record MeasuredStellarAssociationEntries(
    IReadOnlyList<MeasuredStellarAssociationV1> Associations,
    IReadOnlyList<MeasuredStellarUnmatchedPredictionV1> UnmatchedPredictions,
    IReadOnlyList<MeasuredStellarUnassociatedDetectionV1> UnassociatedDetections);

/// <summary>
/// Deterministic one-to-one association of measured sources to catalog star predictions inside windows bounded by
/// each prediction's declared uncertainty and the source's centroid covariance. It never fits or moves a prediction,
/// so it cannot absorb a pose error: a misplaced scene associates nothing and every label fails closed.
/// </summary>
public static class MeasuredStellarAssociator
{
    public const string AlgorithmVersion = "expected-window-one-to-one-v1";
    private const int PositionDecimals = 4;
    private const int ValueDecimals = 6;

    public static MeasuredStellarAssociationEntries Associate(
        ProjectedSceneV1 scene,
        StellarMeasurementResult measurement,
        MeasuredStellarAssociationSettingsV1 settings,
        StellarLabelPolicySettingsV1 labelPolicy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(measurement);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(labelPolicy);
        settings.Validate();
        labelPolicy.Validate();
        var aperture = new ProjectedSceneAperture(scene);
        var stars = scene.Objects.Where(static item => item.Kind == CelestialObjectKind.Star)
            .OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();
        var detections = measurement.Detections;
        var unmatched = new List<MeasuredStellarUnmatchedPredictionV1>();
        if (measurement.Status != StellarMeasurementStatus.Completed)
        {
            foreach (var star in stars)
                unmatched.Add(Unmatched(star, aperture.Contains(star.Pixel)
                    ? MeasuredStellarAssociationReasonCodes.MeasurementIncomplete
                    : MeasuredStellarAssociationReasonCodes.OutsideAperture));
            return new([], unmatched, []);
        }

        var searchRadius = settings.OffsetRadiusPixels;
        var detectionGrid = new PointGrid(detections.Select(static item => item.Pixel).ToArray(), searchRadius);
        var predictionGrid = new PointGrid(stars.Select(static item => item.Pixel).ToArray(),
            Math.Max(settings.CrowdingRadiusPixels, searchRadius));
        var preMatch = new string?[stars.Length];
        var windows = new List<(int Star, int Detection, double Distance, double Normalized)>[stars.Length];
        var crowdingSquared = settings.CrowdingRadiusPixels * settings.CrowdingRadiusPixels;
        for (var index = 0; index < stars.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var star = stars[index];
            windows[index] = [];
            if (!aperture.Contains(star.Pixel))
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.OutsideAperture;
                continue;
            }
            if (!aperture.ContainsWithMargin(star.Pixel, settings.EdgeMarginPixels))
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.NearEdge;
                continue;
            }
            if (predictionGrid.Near(star.Pixel).Any(other => other != index &&
                    DistanceSquared(stars[other].Pixel, star.Pixel) <= crowdingSquared &&
                    stars[other].Magnitude - star.Magnitude <= settings.CrowdingMagnitudeDifference))
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.CrowdedPrediction;
                continue;
            }
            var near = detectionGrid.Near(star.Pixel)
                .Select(candidate => (Detection: candidate, Distance: Math.Sqrt(DistanceSquared(detections[candidate].Pixel, star.Pixel))))
                .Where(item => item.Distance <= searchRadius)
                .OrderBy(static item => item.Distance).ThenBy(static item => item.Detection).ToArray();
            foreach (var (candidate, distance) in near)
            {
                if (distance > settings.MaximumWindowPixels) continue;
                var normalized = NormalizedSquared(detections[candidate], star.Pixel, settings.PredictionSigmaPixels);
                if (normalized <= settings.WindowSigmas * settings.WindowSigmas)
                    windows[index].Add((index, candidate, distance, normalized));
            }
            if (windows[index].Count > 0 && near.Length > 1 && near[1].Distance < settings.AmbiguityRatio * near[0].Distance)
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.Ambiguous;
                windows[index].Clear();
            }
        }

        // Greedy one-to-one assignment in a total order: normalized residual, then catalog identity, then source index.
        var pairs = windows.SelectMany(static item => item)
            .OrderBy(static item => item.Normalized)
            .ThenBy(item => stars[item.Star].Id, StringComparer.Ordinal)
            .ThenBy(static item => item.Detection).ToArray();
        var starAssigned = new int[stars.Length];
        Array.Fill(starAssigned, -1);
        var detectionAssigned = new int[detections.Count];
        Array.Fill(detectionAssigned, -1);
        foreach (var pair in pairs)
        {
            if (starAssigned[pair.Star] >= 0 || detectionAssigned[pair.Detection] >= 0) continue;
            starAssigned[pair.Star] = pair.Detection;
            detectionAssigned[pair.Detection] = pair.Star;
        }

        var exclusions = measurement.Exclusions;
        var associations = new List<MeasuredStellarAssociationV1>();
        var starCategory = new int[stars.Length];
        for (var index = 0; index < stars.Length; index++)
        {
            var star = stars[index];
            if (starAssigned[index] is var assigned and >= 0)
            {
                associations.Add(Association(star, detections[assigned], settings, labelPolicy));
                starCategory[index] = 0;
                continue;
            }
            var reason = preMatch[index];
            if (reason is not null)
            {
                unmatched.Add(Unmatched(star, reason));
                starCategory[index] = 1;
                continue;
            }
            if (windows[index].Count > 0)
                reason = MeasuredStellarAssociationReasonCodes.ClaimedByOther;
            else if (ExcludedBy(star.Pixel, exclusions, settings) is { } exclusion)
                reason = MeasuredStellarAssociationReasonCodes.MeasurementExcludedPrefix + exclusion.ReasonCode;
            else if (detectionGrid.Near(star.Pixel).Any(candidate =>
                         DistanceSquared(detections[candidate].Pixel, star.Pixel) <= searchRadius * searchRadius))
                reason = MeasuredStellarAssociationReasonCodes.OffsetMeasuredSource;
            else
                reason = MeasuredStellarAssociationReasonCodes.NoMeasuredSource;
            unmatched.Add(Unmatched(star, reason));
            starCategory[index] = 2;
        }

        var nonStars = scene.Objects.Where(static item => item.Kind != CelestialObjectKind.Star)
            .Select(static item => item.Pixel).ToArray();
        var unassociated = new List<MeasuredStellarUnassociatedDetectionV1>();
        var offsetSquared = searchRadius * searchRadius;
        for (var index = 0; index < detections.Count; index++)
        {
            if (detectionAssigned[index] >= 0) continue;
            var detection = detections[index];
            var category = predictionGrid.Near(detection.Pixel)
                .Where(star => DistanceSquared(stars[star].Pixel, detection.Pixel) <= offsetSquared)
                .Select(star => starCategory[star]).DefaultIfEmpty(int.MaxValue).Min();
            if (category > 1 && nonStars.Any(pixel => DistanceSquared(pixel, detection.Pixel) <= offsetSquared))
                category = 1;
            var reason = category switch
            {
                0 => MeasuredStellarAssociationReasonCodes.NearAssociatedPrediction,
                1 => MeasuredStellarAssociationReasonCodes.NearIneligiblePrediction,
                2 => MeasuredStellarAssociationReasonCodes.NearUnassociatedPrediction,
                _ => MeasuredStellarAssociationReasonCodes.NoCatalogPrediction
            };
            unassociated.Add(new(detection.Index, Round(detection.Pixel.X, PositionDecimals),
                Round(detection.Pixel.Y, PositionDecimals), Round(detection.Flux, ValueDecimals),
                Round(detection.SignalToNoise), reason));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(associations.AsReadOnly(), unmatched.AsReadOnly(), unassociated.AsReadOnly());
    }

    private static StellarMeasurementExclusion? ExcludedBy(
        PixelPoint point, IReadOnlyList<StellarMeasurementExclusion> exclusions, MeasuredStellarAssociationSettingsV1 settings)
    {
        StellarMeasurementExclusion? best = null;
        var bestRank = (Contained: false, Distance: double.MaxValue);
        var limit = settings.MaximumWindowPixels * settings.MaximumWindowPixels;
        foreach (var exclusion in exclusions)
        {
            var contained = exclusion.Footprint is { } footprint && footprint.Contains(point, settings.ExclusionMarginPixels);
            var distance = DistanceSquared(exclusion.Peak, point);
            if (!contained && distance > limit) continue;
            if (best is null || contained && !bestRank.Contained ||
                contained == bestRank.Contained && distance < bestRank.Distance)
            {
                best = exclusion;
                bestRank = (contained, distance);
            }
        }
        return best;
    }

    private static MeasuredStellarAssociationV1 Association(
        ProjectedCelestialObject star, StellarDetection detection, MeasuredStellarAssociationSettingsV1 settings,
        StellarLabelPolicySettingsV1 labelPolicy)
    {
        var covariance = detection.CentroidCovariance;
        var trailed = detection.Conditions.HasFlag(StellarSourceConditions.Trailed);
        var angle = trailed ? Round(detection.TrailAngleDegrees) : null;
        if (angle >= 90) angle -= 180;
        var association = new MeasuredStellarAssociationV1(
            star.Id,
            detection.Index,
            Round(star.Magnitude, ValueDecimals),
            Round(star.Pixel.X, PositionDecimals),
            Round(star.Pixel.Y, PositionDecimals),
            Round(detection.Pixel.X, PositionDecimals),
            Round(detection.Pixel.Y, PositionDecimals),
            Math.Min(Round(Math.Sqrt(DistanceSquared(detection.Pixel, star.Pixel)), PositionDecimals), settings.MaximumWindowPixels),
            Math.Min(Round(NormalizedSquared(detection, star.Pixel, settings.PredictionSigmaPixels), ValueDecimals),
                settings.WindowSigmas * settings.WindowSigmas),
            Round(detection.Flux, ValueDecimals),
            Round(detection.SignalToNoise),
            Round(covariance?.XX),
            Round(covariance?.XY),
            Round(covariance?.YY),
            detection.SaturatedSampleCount > 0,
            detection.SaturatedSampleCount,
            trailed,
            Round(detection.TrailLengthPixels),
            angle,
            false,
            null);
        var rejection = StellarLabelPolicy.RejectionReason(association, labelPolicy);
        return association with { LabelEligible = rejection is null, LabelRejectionReason = rejection };
    }

    private static MeasuredStellarUnmatchedPredictionV1 Unmatched(ProjectedCelestialObject star, string reason) =>
        new(star.Id, Round(star.Magnitude, ValueDecimals), Round(star.Pixel.X, PositionDecimals),
            Round(star.Pixel.Y, PositionDecimals), reason);

    /// <summary>Squared Mahalanobis residual under the prediction variance plus the source's centroid covariance.</summary>
    private static double NormalizedSquared(StellarDetection detection, PixelPoint expected, double predictionSigma)
    {
        var dx = detection.Pixel.X - expected.X;
        var dy = detection.Pixel.Y - expected.Y;
        var prediction = predictionSigma * predictionSigma;
        var covariance = detection.CentroidCovariance ?? new StellarCentroidCovariance(0, 0, 0);
        var a = prediction + Math.Max(0, covariance.XX);
        var b = covariance.XY;
        var c = prediction + Math.Max(0, covariance.YY);
        var determinant = a * c - b * b;
        if (!(determinant > 0))
            return (dx * dx + dy * dy) / prediction;
        return (c * dx * dx - 2 * b * dx * dy + a * dy * dy) / determinant;
    }

    private static double DistanceSquared(PixelPoint left, PixelPoint right)
    {
        var dx = left.X - right.X;
        var dy = left.Y - right.Y;
        return dx * dx + dy * dy;
    }

    private static double Round(double value, int decimals)
    {
        var rounded = Math.Round(value, decimals, MidpointRounding.ToEven);
        return rounded == 0 ? 0 : rounded;
    }

    private static double? Round(double? value) => value is { } present && double.IsFinite(present)
        ? Round(present, ValueDecimals) : null;

    /// <summary>A uniform bucket grid; each query returns candidates in ascending index order.</summary>
    private sealed class PointGrid
    {
        private readonly Dictionary<(long, long), List<int>> cells = [];
        private readonly double cellSize;

        public PointGrid(PixelPoint[] points, double radius)
        {
            cellSize = Math.Max(radius, 1);
            for (var index = 0; index < points.Length; index++)
            {
                var key = Key(points[index]);
                if (!cells.TryGetValue(key, out var list))
                    cells[key] = list = [];
                list.Add(index);
            }
        }

        public List<int> Near(PixelPoint point)
        {
            var (cx, cy) = Key(point);
            var found = new List<int>();
            for (var y = cy - 1; y <= cy + 1; y++)
                for (var x = cx - 1; x <= cx + 1; x++)
                    if (cells.TryGetValue((x, y), out var list))
                        found.AddRange(list);
            found.Sort();
            return found;
        }

        private (long, long) Key(PixelPoint point) =>
            ((long)Math.Floor(point.X / cellSize), (long)Math.Floor(point.Y / cellSize));
    }
}

/// <summary>
/// The single fail-closed rule deciding whether a catalog label may be drawn. A star label requires a policy-eligible
/// measured association; an unassociated, ambiguous or unmeasured star never receives a catalog label.
/// Solar-system bodies are ephemeris objects rather than catalog stars and are outside this policy.
/// </summary>
public static class StellarLabelPolicy
{
    public const string Version = "measured-association-label-policy-v1";
    public const string SolarSystemIdPrefix = "solar-system:";

    public static bool IsEligible(MeasuredStellarAssociationV1 association, StellarLabelPolicySettingsV1 policy) =>
        RejectionReason(association, policy) is null;

    public static string? RejectionReason(MeasuredStellarAssociationV1 association, StellarLabelPolicySettingsV1 policy)
    {
        ArgumentNullException.ThrowIfNull(association);
        ArgumentNullException.ThrowIfNull(policy);
        if (association.SignalToNoise is not { } snr)
            return MeasuredStellarAssociationReasonCodes.LabelSignalToNoiseUnavailable;
        if (snr < policy.MinimumSignalToNoise)
            return MeasuredStellarAssociationReasonCodes.LabelLowSignalToNoise;
        if (association.Saturated && !policy.AllowSaturated)
            return MeasuredStellarAssociationReasonCodes.LabelSaturated;
        if (association.Trailed && !policy.AllowTrailed)
            return MeasuredStellarAssociationReasonCodes.LabelTrailed;
        return null;
    }

    public static bool IsOutsidePolicy(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return objectId.StartsWith(SolarSystemIdPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gates host-selected annotation objects. Marks stay at their expected projected pixels, so the overlay remains
    /// expected geometry; a star label is kept only for a policy-eligible measured association. Every other star label
    /// is suppressed or, only when expected-position diagnostics are requested, explicitly marked as expected. Final
    /// consumers preserve the complete diagnostic marker within their bound or suppress the label.
    /// </summary>
    public static IReadOnlyList<ProjectedAnnotationObject> Apply(
        IReadOnlyList<ProjectedAnnotationObject> objects,
        MeasuredStellarAssociationsV1? associations,
        bool expectedPositionDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var eligible = associations?.Associations.Where(static item => item.LabelEligible)
            .Select(static item => item.CatalogId).ToHashSet(StringComparer.Ordinal) ?? [];
        var gated = new List<ProjectedAnnotationObject>(objects.Count);
        foreach (var item in objects)
        {
            if (!item.DrawLabel || IsOutsidePolicy(item.Id) || eligible.Contains(item.Id))
                gated.Add(item);
            else if (expectedPositionDiagnostics)
                gated.Add(item with { ExpectedPosition = true });
            else
                gated.Add(item with { DrawLabel = false });
        }
        return gated.AsReadOnly();
    }

    public const string ExpectedSuffix = AnnotationLabelFormatter.ExpectedSuffix;
}
