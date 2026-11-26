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
    IReadOnlyList<MeasuredStellarUnassociatedDetectionV1> UnassociatedDetections,
    MeasuredPointSourcePlanetEvidenceV1? PointSourcePlanets = null);

/// <summary>
/// Deterministic one-to-one association of measured sources to catalog star and point-source planet predictions inside
/// windows bounded by each prediction's declared uncertainty and the source's centroid covariance. It never fits or
/// moves a prediction, so it cannot absorb a pose error: a misplaced scene associates nothing and every label fails closed.
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
        ArgumentNullException.ThrowIfNull(measurement);
        return Associate(scene, measurement.Detections, measurement.Exclusions,
            measurement.Status == StellarMeasurementStatus.Completed, settings, labelPolicy, cancellationToken);
    }

    internal static MeasuredStellarAssociationEntries Associate(
        ProjectedSceneV1 scene,
        IReadOnlyList<StellarDetection> detections,
        IReadOnlyList<StellarMeasurementExclusion> exclusions,
        bool measurementCompleted,
        MeasuredStellarAssociationSettingsV1 settings,
        StellarLabelPolicySettingsV1 labelPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(labelPolicy);
        settings.Validate();
        labelPolicy.Validate();
        var aperture = new ProjectedSceneAperture(scene);
        var resolvedFootprintIds = (scene.ResolvedFootprints ?? [])
            .Where(static item => item.SourceKind == ResolvedFootprintSourceKind.SolarSystemBody)
            .Select(static item => item.Id)
            .ToHashSet(StringComparer.Ordinal);
        var stars = scene.Objects.Where(static item => item.Kind == CelestialObjectKind.Star)
            .OrderBy(static item => item.Id, StringComparer.Ordinal).ToArray();
        var planets = scene.Objects
            .Where(item => IsPointSourcePlanet(item, resolvedFootprintIds, out _))
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static item => (Object: item, Body: ParsePointSourcePlanet(item.Id)))
            .ToArray();
        var mappingVersion = $"{scene.Projection.Model}:{scene.Projection.CalibrationVersion}:{scene.Projection.AlgorithmVersion}";
        var unmatched = new List<MeasuredStellarUnmatchedPredictionV1>();
        if (!measurementCompleted)
        {
            foreach (var star in stars)
                unmatched.Add(Unmatched(star, aperture.Contains(star.Pixel)
                    ? MeasuredStellarAssociationReasonCodes.MeasurementIncomplete
                    : MeasuredStellarAssociationReasonCodes.OutsideAperture));
            MeasuredPointSourcePlanetEvidenceV1? incompletePlanets = null;
            if (planets.Length > 0)
            {
                var unmatchedPlanets = planets.Select(planet => UnmatchedPlanet(planet.Object, planet.Body,
                    aperture.Contains(planet.Object.Pixel)
                        ? MeasuredStellarAssociationReasonCodes.MeasurementIncomplete
                        : MeasuredStellarAssociationReasonCodes.OutsideAperture)).ToArray();
                incompletePlanets = new(scene.EffectiveUtc, scene.EphemerisModelVersion ?? "unspecified",
                    mappingVersion, Round(settings.PredictionSigmaPixels, ValueDecimals),
                    Array.AsReadOnly(Array.Empty<MeasuredPointSourcePlanetAssociationV1>()),
                    Array.AsReadOnly(unmatchedPlanets));
            }
            return new([], unmatched, [], incompletePlanets);
        }

        // Joint point-source target pool: stars first, then point-source planets.
        // Keeping stars and planets in one deterministic 1-to-1 matcher ensures no detection is double-assigned
        // and close star-planet pairs trigger crowding/ambiguity/conflict reasons identically.
        var targets = new ProjectedCelestialObject[stars.Length + planets.Length];
        stars.CopyTo(targets, 0);
        for (var i = 0; i < planets.Length; i++)
            targets[stars.Length + i] = planets[i].Object;

        var searchRadius = settings.OffsetRadiusPixels;
        var detectionGrid = new PointGrid(detections.Select(static item => item.Pixel).ToArray(), searchRadius);
        var predictionGrid = new PointGrid(targets.Select(static item => item.Pixel).ToArray(),
            Math.Max(settings.CrowdingRadiusPixels, searchRadius));
        var preMatch = new string?[targets.Length];
        var windows = new List<(int Target, int Detection, double Distance, double Normalized)>[targets.Length];
        var crowdingSquared = settings.CrowdingRadiusPixels * settings.CrowdingRadiusPixels;
        for (var index = 0; index < targets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[index];
            windows[index] = [];
            if (!aperture.Contains(target.Pixel))
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.OutsideAperture;
                continue;
            }
            if (!aperture.ContainsWithMargin(target.Pixel, settings.EdgeMarginPixels))
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.NearEdge;
                continue;
            }
            if (predictionGrid.Near(target.Pixel).Any(other => other != index &&
                    DistanceSquared(targets[other].Pixel, target.Pixel) <= crowdingSquared &&
                    targets[other].Magnitude - target.Magnitude <= settings.CrowdingMagnitudeDifference))
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.CrowdedPrediction;
                continue;
            }
            var near = detectionGrid.Near(target.Pixel)
                .Select(candidate => (Detection: candidate, Distance: Math.Sqrt(DistanceSquared(detections[candidate].Pixel, target.Pixel))))
                .Where(item => item.Distance <= searchRadius)
                .OrderBy(static item => item.Distance).ThenBy(static item => item.Detection).ToArray();
            foreach (var (candidate, distance) in near)
            {
                if (distance > settings.MaximumWindowPixels) continue;
                var normalized = NormalizedSquared(detections[candidate], target.Pixel, settings.PredictionSigmaPixels);
                if (normalized <= settings.WindowSigmas * settings.WindowSigmas)
                    windows[index].Add((index, candidate, distance, normalized));
            }
            if (windows[index].Count > 0 && near.Length > 1 && near[1].Distance < settings.AmbiguityRatio * near[0].Distance)
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.Ambiguous;
                windows[index].Clear();
            }
        }

        // Greedy one-to-one assignment in a total order: normalized residual, then object identity, then source index.
        var pairs = windows.SelectMany(static item => item)
            .OrderBy(static item => item.Normalized)
            .ThenBy(item => targets[item.Target].Id, StringComparer.Ordinal)
            .ThenBy(static item => item.Detection).ToArray();
        var targetAssigned = new int[targets.Length];
        Array.Fill(targetAssigned, -1);
        var detectionAssigned = new int[detections.Count];
        Array.Fill(detectionAssigned, -1);
        foreach (var pair in pairs)
        {
            if (targetAssigned[pair.Target] >= 0 || detectionAssigned[pair.Detection] >= 0) continue;
            targetAssigned[pair.Target] = pair.Detection;
            detectionAssigned[pair.Detection] = pair.Target;
        }

        var associations = new List<MeasuredStellarAssociationV1>();
        var targetCategory = new int[targets.Length];
        for (var index = 0; index < stars.Length; index++)
        {
            var star = stars[index];
            if (targetAssigned[index] is var assigned and >= 0)
            {
                associations.Add(Association(star, detections[assigned], settings, labelPolicy));
                targetCategory[index] = 0;
                continue;
            }
            var reason = ResolveUnmatchedReason(index, star.Pixel);
            unmatched.Add(Unmatched(star, reason));
            targetCategory[index] = preMatch[index] is not null ? 1 : 2;
        }

        MeasuredPointSourcePlanetEvidenceV1? planetEvidence = null;
        if (planets.Length > 0)
        {
            var planetAssociations = new List<MeasuredPointSourcePlanetAssociationV1>();
            var unmatchedPlanets = new List<MeasuredPointSourcePlanetUnmatchedPredictionV1>();
            for (var p = 0; p < planets.Length; p++)
            {
                var index = stars.Length + p;
                var (planet, body) = planets[p];
                if (targetAssigned[index] is var assigned and >= 0)
                {
                    planetAssociations.Add(PlanetAssociation(planet, body, detections[assigned], settings, labelPolicy));
                    targetCategory[index] = 0;
                    continue;
                }
                var reason = ResolveUnmatchedReason(index, planet.Pixel);
                unmatchedPlanets.Add(UnmatchedPlanet(planet, body, reason));
                targetCategory[index] = preMatch[index] is not null ? 1 : 2;
            }
            planetEvidence = new(scene.EffectiveUtc, scene.EphemerisModelVersion ?? "unspecified",
                mappingVersion, Round(settings.PredictionSigmaPixels, ValueDecimals),
                planetAssociations.AsReadOnly(), unmatchedPlanets.AsReadOnly());
        }

        var pointSourceIds = targets.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var nonPointSources = scene.Objects.Where(item => !pointSourceIds.Contains(item.Id))
            .Select(static item => item.Pixel).ToArray();
        var unassociated = new List<MeasuredStellarUnassociatedDetectionV1>();
        var offsetSquared = searchRadius * searchRadius;
        for (var index = 0; index < detections.Count; index++)
        {
            if (detectionAssigned[index] >= 0) continue;
            var detection = detections[index];
            var category = predictionGrid.Near(detection.Pixel)
                .Where(target => DistanceSquared(targets[target].Pixel, detection.Pixel) <= offsetSquared)
                .Select(target => targetCategory[target]).DefaultIfEmpty(int.MaxValue).Min();
            if (category > 1 && nonPointSources.Any(pixel => DistanceSquared(pixel, detection.Pixel) <= offsetSquared))
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
        return new(associations.AsReadOnly(), unmatched.AsReadOnly(), unassociated.AsReadOnly(), planetEvidence);

        string ResolveUnmatchedReason(int targetIndex, PixelPoint expectedPixel)
        {
            if (preMatch[targetIndex] is { } pre)
                return pre;
            if (windows[targetIndex].Count > 0)
                return MeasuredStellarAssociationReasonCodes.ClaimedByOther;
            if (ExcludedBy(expectedPixel, exclusions, settings) is { } exclusion)
                return MeasuredStellarAssociationReasonCodes.MeasurementExcludedPrefix + exclusion.ReasonCode;
            if (detectionGrid.Near(expectedPixel).Any(candidate =>
                    DistanceSquared(detections[candidate].Pixel, expectedPixel) <= searchRadius * searchRadius))
                return MeasuredStellarAssociationReasonCodes.OffsetMeasuredSource;
            return MeasuredStellarAssociationReasonCodes.NoMeasuredSource;
        }
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

    private static MeasuredPointSourcePlanetAssociationV1 PlanetAssociation(
        ProjectedCelestialObject planet, SolarSystemBody body, StellarDetection detection,
        MeasuredStellarAssociationSettingsV1 settings, StellarLabelPolicySettingsV1 labelPolicy)
    {
        var covariance = detection.CentroidCovariance;
        var trailed = detection.Conditions.HasFlag(StellarSourceConditions.Trailed);
        var angle = trailed ? Round(detection.TrailAngleDegrees) : null;
        if (angle >= 90) angle -= 180;
        var association = new MeasuredPointSourcePlanetAssociationV1(
            planet.Id,
            body,
            detection.Index,
            Round(planet.Magnitude, ValueDecimals),
            Round(planet.Pixel.X, PositionDecimals),
            Round(planet.Pixel.Y, PositionDecimals),
            Round(detection.Pixel.X, PositionDecimals),
            Round(detection.Pixel.Y, PositionDecimals),
            Math.Min(Round(Math.Sqrt(DistanceSquared(detection.Pixel, planet.Pixel)), PositionDecimals), settings.MaximumWindowPixels),
            Math.Min(Round(NormalizedSquared(detection, planet.Pixel, settings.PredictionSigmaPixels), ValueDecimals),
                settings.WindowSigmas * settings.WindowSigmas),
            Math.Max(Round(CombinedUncertaintyMajorSigma(detection, settings.PredictionSigmaPixels), ValueDecimals),
                Round(settings.PredictionSigmaPixels, ValueDecimals)),
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

    private static MeasuredPointSourcePlanetUnmatchedPredictionV1 UnmatchedPlanet(
        ProjectedCelestialObject planet, SolarSystemBody body, string reason) =>
        new(planet.Id, body, Round(planet.Magnitude, ValueDecimals), Round(planet.Pixel.X, PositionDecimals),
            Round(planet.Pixel.Y, PositionDecimals), reason);

    internal static bool IsPointSourcePlanet(
        ProjectedCelestialObject item, IReadOnlySet<string> resolvedFootprintIds, out SolarSystemBody body)
    {
        body = default;
        if (item.Kind != CelestialObjectKind.SolarSystemBody || resolvedFootprintIds.Contains(item.Id))
            return false;
        return TryParsePointSourcePlanet(item.Id, out body);
    }

    internal static bool TryParsePointSourcePlanet(string objectId, out SolarSystemBody body)
    {
        body = default;
        if (!objectId.StartsWith(StellarLabelPolicy.SolarSystemIdPrefix, StringComparison.Ordinal))
            return false;
        return Enum.TryParse(objectId[StellarLabelPolicy.SolarSystemIdPrefix.Length..], ignoreCase: false, out body) &&
            MeasuredStellarAssociationJson.IsPointSourcePlanetBody(body);
    }

    private static SolarSystemBody ParsePointSourcePlanet(string objectId) =>
        TryParsePointSourcePlanet(objectId, out var body)
            ? body
            : throw new InvalidOperationException($"Unsupported point-source planet identifier '{objectId}'.");

    internal static bool MatchesDetection(MeasuredStellarAssociationV1 member, IReadOnlyList<StellarDetection> detections)
    {
        if (member.DetectionIndex < 0 || member.DetectionIndex >= detections.Count)
            return false;
        var detection = detections[member.DetectionIndex];
        var covariance = detection.CentroidCovariance;
        return member.MeasuredX == Round(detection.Pixel.X, PositionDecimals) &&
               member.MeasuredY == Round(detection.Pixel.Y, PositionDecimals) &&
               member.Flux == Round(detection.Flux, ValueDecimals) && member.SignalToNoise == Round(detection.SignalToNoise) &&
               member.CovarianceXX == Round(covariance?.XX) && member.CovarianceXY == Round(covariance?.XY) &&
               member.CovarianceYY == Round(covariance?.YY) && member.SaturatedSampleCount == detection.SaturatedSampleCount &&
               member.Trailed == detection.Conditions.HasFlag(StellarSourceConditions.Trailed);
    }

    internal static bool MatchesDetection(MeasuredPointSourcePlanetAssociationV1 member, IReadOnlyList<StellarDetection> detections)
    {
        if (member.DetectionIndex < 0 || member.DetectionIndex >= detections.Count)
            return false;
        var detection = detections[member.DetectionIndex];
        var covariance = detection.CentroidCovariance;
        return member.MeasuredX == Round(detection.Pixel.X, PositionDecimals) &&
               member.MeasuredY == Round(detection.Pixel.Y, PositionDecimals) &&
               member.Flux == Round(detection.Flux, ValueDecimals) && member.SignalToNoise == Round(detection.SignalToNoise) &&
               member.CovarianceXX == Round(covariance?.XX) && member.CovarianceXY == Round(covariance?.XY) &&
               member.CovarianceYY == Round(covariance?.YY) && member.SaturatedSampleCount == detection.SaturatedSampleCount &&
               member.Trailed == detection.Conditions.HasFlag(StellarSourceConditions.Trailed);
    }

    /// <summary>1-sigma semi-major axis of the combined prediction-plus-centroid covariance ellipse.</summary>
    private static double CombinedUncertaintyMajorSigma(StellarDetection detection, double predictionSigma)
    {
        var prediction = predictionSigma * predictionSigma;
        if (detection.CentroidCovariance is not { } covariance)
            return predictionSigma;
        var a = prediction + Math.Max(0, covariance.XX);
        var b = double.IsFinite(covariance.XY) ? covariance.XY : 0;
        var c = prediction + Math.Max(0, covariance.YY);
        var diff = a - c;
        var majorVariance = .5 * (a + c + Math.Sqrt(diff * diff + 4 * b * b));
        return majorVariance > prediction ? Math.Sqrt(majorVariance) : predictionSigma;
    }

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
/// The single fail-closed rule deciding whether a point-source catalog or planet label may be drawn. A star or
/// point-source planet label requires a policy-eligible measured association; an unassociated, ambiguous or unmeasured
/// point source never receives an authoritative label. Resolved solar-system discs (Sun and Moon) remain outside this
/// point-source policy.
/// </summary>
public static class StellarLabelPolicy
{
    public const string Version = "measured-association-label-policy-v1";
    public const string SolarSystemIdPrefix = "solar-system:";

    public static bool IsEligible(MeasuredStellarAssociationV1 association, StellarLabelPolicySettingsV1 policy) =>
        RejectionReason(association, policy) is null;

    public static bool IsEligible(MeasuredPointSourcePlanetAssociationV1 association, StellarLabelPolicySettingsV1 policy) =>
        RejectionReason(association, policy) is null;

    public static string? RejectionReason(MeasuredStellarAssociationV1 association, StellarLabelPolicySettingsV1 policy)
    {
        ArgumentNullException.ThrowIfNull(association);
        ArgumentNullException.ThrowIfNull(policy);
        return RejectionReason(association.SignalToNoise, association.Saturated, association.Trailed, policy);
    }

    public static string? RejectionReason(MeasuredPointSourcePlanetAssociationV1 association, StellarLabelPolicySettingsV1 policy)
    {
        ArgumentNullException.ThrowIfNull(association);
        ArgumentNullException.ThrowIfNull(policy);
        return RejectionReason(association.SignalToNoise, association.Saturated, association.Trailed, policy);
    }

    private static string? RejectionReason(double? signalToNoise, bool saturated, bool trailed, StellarLabelPolicySettingsV1 policy)
    {
        if (signalToNoise is not { } snr)
            return MeasuredStellarAssociationReasonCodes.LabelSignalToNoiseUnavailable;
        if (snr < policy.MinimumSignalToNoise)
            return MeasuredStellarAssociationReasonCodes.LabelLowSignalToNoise;
        if (saturated && !policy.AllowSaturated)
            return MeasuredStellarAssociationReasonCodes.LabelSaturated;
        if (trailed && !policy.AllowTrailed)
            return MeasuredStellarAssociationReasonCodes.LabelTrailed;
        return null;
    }

    public static bool IsPointSourceSolarSystemBody(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return MeasuredStellarAssociator.TryParsePointSourcePlanet(objectId, out _);
    }

    public static bool IsOutsidePolicy(string objectId)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        return objectId.StartsWith(SolarSystemIdPrefix, StringComparison.Ordinal) &&
            !MeasuredStellarAssociator.TryParsePointSourcePlanet(objectId, out _);
    }

    /// <summary>
    /// Gates host-selected annotation objects. Marks stay at their expected projected pixels, so the overlay remains
    /// expected geometry; a star or point-source planet label is kept only for a policy-eligible measured association.
    /// Every other star or point-source planet label is suppressed or, only when expected-position diagnostics are
    /// requested, explicitly marked as expected. Final consumers preserve the complete diagnostic marker within their
    /// bound or suppress the label.
    /// </summary>
    public static IReadOnlyList<ProjectedAnnotationObject> Apply(
        IReadOnlyList<ProjectedAnnotationObject> objects,
        MeasuredStellarAssociationsV1? associations,
        bool expectedPositionDiagnostics = false)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var eligible = new HashSet<string>(StringComparer.Ordinal);
        if (associations is not null)
        {
            foreach (var item in associations.Associations)
                if (item.LabelEligible) eligible.Add(item.CatalogId);
            if (associations.PointSourcePlanets is { } planets)
                foreach (var item in planets.Associations)
                    if (item.LabelEligible) eligible.Add(item.ObjectId);
        }
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
