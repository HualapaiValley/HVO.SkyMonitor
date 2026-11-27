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
    internal const string V1AlgorithmVersion = "expected-window-one-to-one-v1";
    public const string AlgorithmVersion = "expected-window-one-to-one-v2";
    private const int PositionDecimals = 4;
    private const int ValueDecimals = 6;
    private const double ArcsecondsToRadians = Math.PI / (180.0 * 3600.0);
    private const double JacobianStepRadians = 1e-4;

    private readonly record struct Covariance2D(double XX, double XY, double YY);

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
        var ephemUncertaintyArcsec = Round(settings.EffectivePlanetEphemerisUncertaintyArcseconds, ValueDecimals);
        var pointingUncertaintyArcsec = Round(settings.EffectivePlanetPointingUncertaintyArcseconds, ValueDecimals);
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
                    ephemUncertaintyArcsec, pointingUncertaintyArcsec,
                    Array.AsReadOnly(Array.Empty<MeasuredPointSourcePlanetAssociationV1>()),
                    Array.AsReadOnly(unmatchedPlanets));
            }
            return new([], unmatched, [], incompletePlanets);
        }

        // Joint point-source target pool: stars first, then point-source planets.
        // Keeping stars and planets in one deterministic 1-to-1 matcher ensures no detection is double-assigned
        // while accounting for propagated angular planet uncertainty and plausible competing catalog stars.
        var targets = new ProjectedCelestialObject[stars.Length + planets.Length];
        stars.CopyTo(targets, 0);
        for (var i = 0; i < planets.Length; i++)
            targets[stars.Length + i] = planets[i].Object;

        var starPredictionCov = new Covariance2D(
            settings.PredictionSigmaPixels * settings.PredictionSigmaPixels, 0,
            settings.PredictionSigmaPixels * settings.PredictionSigmaPixels);
        var targetCovariances = new Covariance2D[targets.Length];
        var targetPropagatedSigmas = new double[targets.Length];
        var targetMaxWindows = new double[targets.Length];
        var targetOffsetRadii = new double[targets.Length];
        var maxSearchRadius = settings.OffsetRadiusPixels;
        var angularSigmaRadians = double.Hypot(
            settings.EffectivePlanetEphemerisUncertaintyArcseconds,
            settings.EffectivePlanetPointingUncertaintyArcseconds) * ArcsecondsToRadians;
        var pointingAngularSigmaRadians = settings.EffectivePlanetPointingUncertaintyArcseconds * ArcsecondsToRadians;
        var jacobianProjector = planets.Length > 0 ? CreateJacobianProjector(scene.Projection) : null;

        for (var index = 0; index < stars.Length; index++)
        {
            targetCovariances[index] = starPredictionCov;
            targetPropagatedSigmas[index] = settings.PredictionSigmaPixels;
            targetMaxWindows[index] = settings.MaximumWindowPixels;
            targetOffsetRadii[index] = settings.OffsetRadiusPixels;
        }
        for (var p = 0; p < planets.Length; p++)
        {
            var index = stars.Length + p;
            var planetCov = ComputePredictionCovariance(
                planets[p].Object, scene, jacobianProjector!, settings.PredictionSigmaPixels, angularSigmaRadians);
            var propagatedSigma = Math.Max(MajorSigma(planetCov), settings.PredictionSigmaPixels);
            var maxWindow = MeasuredStellarAssociationJson.PlanetMaximumWindow(settings, propagatedSigma);
            var offsetRadius = Math.Max(settings.OffsetRadiusPixels, maxWindow);
            targetCovariances[index] = planetCov;
            targetPropagatedSigmas[index] = propagatedSigma;
            targetMaxWindows[index] = maxWindow;
            targetOffsetRadii[index] = offsetRadius;
            if (offsetRadius > maxSearchRadius) maxSearchRadius = offsetRadius;
        }

        var detectionGrid = new PointGrid(detections.Select(static item => item.Pixel).ToArray(), maxSearchRadius);
        var predictionCellSize = Math.Max(settings.CrowdingRadiusPixels,
            planets.Length > 0
                ? Math.Max(maxSearchRadius, Math.Min(Math.Max(64, settings.MaximumWindowPixels), maxSearchRadius * 1.5 + 4))
                : maxSearchRadius);
        var predictionGrid = new PointGrid(targets.Select(static item => item.Pixel).ToArray(), predictionCellSize);
        var preMatch = new string?[targets.Length];
        var contestedByCompetingTarget = new bool[targets.Length];
        var starPointingCovariances = planets.Length > 0 ? new Covariance2D?[stars.Length] : null;
        var windows = new List<(int Target, int Detection, double Distance, double Normalized, double NegativeLogLikelihood)>[targets.Length];
        var crowdingSquared = settings.CrowdingRadiusPixels * settings.CrowdingRadiusPixels;
        var windowSigmasSquared = settings.WindowSigmas * settings.WindowSigmas;
        for (var index = 0; index < targets.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = targets[index];
            var isPlanet = index >= stars.Length;
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
            // A planet prediction must account for ANY plausible competing catalog star or planet within
            // CrowdingRadiusPixels (never exempting itself because it is predicted brighter) so an absent planet
            // cannot steal a nearby real star via one-sided crowding exclusion.
            if (predictionGrid.Near(target.Pixel).Any(other => other != index &&
                    DistanceSquared(targets[other].Pixel, target.Pixel) <= crowdingSquared &&
                    (isPlanet || targets[other].Magnitude - target.Magnitude <= settings.CrowdingMagnitudeDifference)))
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.CrowdedPrediction;
                continue;
            }
            // If an exclusion footprint (e.g. saturated-excessive) covers the planet's predicted position AND its
            // peak lies within the planet's own search window, withhold the planet before matching so it cannot claim
            // a nearby moon or star inside its search window, without allowing a distant trail or extended region
            // whose axis-aligned bounding box spans over the planet to suppress a cleanly detected planet.
            if (isPlanet && ExclusionContaining(target.Pixel, exclusions, targetMaxWindows[index], settings.ExclusionMarginPixels) is { } containingExclusion)
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.MeasurementExcludedPrefix + containingExclusion.ReasonCode;
                continue;
            }
            var targetOffsetRadius = targetOffsetRadii[index];
            var targetMaxWindow = targetMaxWindows[index];
            var predCov = targetCovariances[index];
            var near = detectionGrid.Near(target.Pixel)
                .Select(candidate => (Detection: candidate, Distance: Math.Sqrt(DistanceSquared(detections[candidate].Pixel, target.Pixel))))
                .Where(item => item.Distance <= targetOffsetRadius)
                .OrderBy(static item => item.Distance).ThenBy(static item => item.Detection).ToArray();
            foreach (var (candidate, distance) in near)
            {
                if (distance > targetMaxWindow) continue;
                var (normalized, nll) = EvaluateResidual(detections[candidate], target.Pixel, predCov, settings.PredictionSigmaPixels);
                if (normalized <= windowSigmasSquared)
                    windows[index].Add((index, candidate, distance, normalized, nll));
            }
            if (windows[index].Count > 0 && near.Length > 1 && near[1].Distance < settings.AmbiguityRatio * near[0].Distance)
            {
                preMatch[index] = MeasuredStellarAssociationReasonCodes.Ambiguous;
                windows[index].Clear();
            }
        }

        // Greedy one-to-one assignment in a total order: for star-only scenes use normalized residual; when planets
        // with distinct propagated uncertainties enter the pool use exact 2D Gaussian negative log-likelihood
        // (normalized + ln|Sigma|) so a wide-uncertainty planet never beats a tighter, closer catalog star.
        var useNll = planets.Length > 0;
        var pairs = windows.SelectMany(static item => item)
            .OrderBy(pair => useNll ? pair.NegativeLogLikelihood : pair.Normalized)
            .ThenBy(static item => item.Distance)
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
        // Phase 2: revoke any planet assignment whose detection is also plausibly explained by any unassigned
        // competing catalog star (or other planet) within that competitor's own declared pointing + centroid window.
        // Running after Phase 1 guarantees a catalog star that already matched its own distinct detection does not
        // falsely block a real planet, and prevents a worse-fitting star from retroactively stealing a contested source.
        if (planets.Length > 0)
        {
            bool revoked;
            do
            {
                revoked = false;
                for (var planetIndex = stars.Length; planetIndex < targets.Length; planetIndex++)
                {
                    var detectionIndex = targetAssigned[planetIndex];
                    if (detectionIndex < 0) continue;
                    var planetDistance = Math.Sqrt(DistanceSquared(targets[planetIndex].Pixel, detections[detectionIndex].Pixel));
                    if (!HasPlausibleCompetingTarget(planetIndex, detectionIndex, planetDistance))
                        continue;
                    targetAssigned[planetIndex] = -1;
                    detectionAssigned[detectionIndex] = -1;
                    contestedByCompetingTarget[planetIndex] = true;
                    // Any unassigned target whose own matching window included this now-unclaimed detection is also
                    // ambiguous rather than ClaimedByOther.
                    for (var other = 0; other < targets.Length; other++)
                    {
                        if (other != planetIndex && targetAssigned[other] < 0 &&
                            windows[other].Any(entry => entry.Detection == detectionIndex))
                            contestedByCompetingTarget[other] = true;
                    }
                    revoked = true;
                }
            }
            while (revoked);
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
            targetCategory[index] = preMatch[index] is not null || contestedByCompetingTarget[index] ? 1 : 2;
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
                    planetAssociations.Add(PlanetAssociation(
                        planet, body, detections[assigned], targetCovariances[index],
                        targetPropagatedSigmas[index], targetMaxWindows[index], settings, labelPolicy));
                    targetCategory[index] = 0;
                    continue;
                }
                var reason = ResolveUnmatchedReason(index, planet.Pixel);
                unmatchedPlanets.Add(UnmatchedPlanet(planet, body, reason));
                targetCategory[index] = preMatch[index] is not null || contestedByCompetingTarget[index] ? 1 : 2;
            }
            planetEvidence = new(scene.EffectiveUtc, scene.EphemerisModelVersion ?? "unspecified",
                mappingVersion, Round(settings.PredictionSigmaPixels, ValueDecimals),
                ephemUncertaintyArcsec, pointingUncertaintyArcsec,
                planetAssociations.AsReadOnly(), unmatchedPlanets.AsReadOnly());
        }

        var pointSourceIds = targets.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        var nonPointSources = scene.Objects.Where(item => !pointSourceIds.Contains(item.Id))
            .Select(static item => item.Pixel).ToArray();
        var unassociated = new List<MeasuredStellarUnassociatedDetectionV1>();
        var defaultOffsetSquared = settings.OffsetRadiusPixels * settings.OffsetRadiusPixels;
        for (var index = 0; index < detections.Count; index++)
        {
            if (detectionAssigned[index] >= 0) continue;
            var detection = detections[index];
            var category = predictionGrid.Near(detection.Pixel)
                .Where(target => DistanceSquared(targets[target].Pixel, detection.Pixel) <= targetOffsetRadii[target] * targetOffsetRadii[target])
                .Select(target => targetCategory[target]).DefaultIfEmpty(int.MaxValue).Min();
            if (category > 1 && nonPointSources.Any(pixel => DistanceSquared(pixel, detection.Pixel) <= defaultOffsetSquared))
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
            if (contestedByCompetingTarget[targetIndex])
                return MeasuredStellarAssociationReasonCodes.Ambiguous;
            if (windows[targetIndex].Count > 0)
                return MeasuredStellarAssociationReasonCodes.ClaimedByOther;
            var isPlanetTarget = targetIndex >= stars.Length;
            var exclusion = isPlanetTarget
                ? ExcludedByPlanet(expectedPixel, exclusions, targetMaxWindows[targetIndex], settings.ExclusionMarginPixels)
                : ExcludedBy(expectedPixel, exclusions, targetMaxWindows[targetIndex], settings.ExclusionMarginPixels);
            if (exclusion is not null)
                return MeasuredStellarAssociationReasonCodes.MeasurementExcludedPrefix + exclusion.ReasonCode;
            var offsetRadius = targetOffsetRadii[targetIndex];
            if (detectionGrid.Near(expectedPixel).Any(candidate =>
                    DistanceSquared(detections[candidate].Pixel, expectedPixel) <= offsetRadius * offsetRadius))
                return MeasuredStellarAssociationReasonCodes.OffsetMeasuredSource;
            return MeasuredStellarAssociationReasonCodes.NoMeasuredSource;
        }

        // Preserve plausible competing catalog-star (and other planet) explanations even when the competing target was
        // excluded by pre-match filtering (crowded, near-edge, ambiguous, outside aperture) or sits outside its fixed
        // star window under the declared angular pointing uncertainty, so an absent planet never steals a star's source.
        bool HasPlausibleCompetingTarget(int planetIndex, int detectionIndex, double planetDistance)
        {
            var detection = detections[detectionIndex];
            // An ultra-wide plateau whose combined WindowSigmas-sigma window exceeds MaximumPlanetWindowPixels (64 px)
            // cannot be safely adjudicated against competing stars beyond 64 px; fail closed as ambiguous.
            if (settings.WindowSigmas * CombinedUncertaintyMajorSigma(detection, targetCovariances[planetIndex]) >
                MeasuredStellarAssociationJson.MaximumPlanetWindowPixels)
                return true;
            // Expand the spatial-grid query to cover the full combined covariance window (accounting for wide
            // detection centroid covariance as well as target pointing/ephemeris windows) and ambiguity radius.
            var competitorSearchRadius = Math.Max(
                Math.Max(settings.MaximumWindowPixels, MeasuredStellarAssociationJson.MaximumPlanetWindowPixels),
                Math.Min(settings.OffsetRadiusPixels, settings.AmbiguityRatio * planetDistance));
            foreach (var other in predictionGrid.Near(detection.Pixel, competitorSearchRadius))
            {
                if (other == planetIndex || targetAssigned[other] >= 0) continue;
                var otherDistance = Math.Sqrt(DistanceSquared(targets[other].Pixel, detection.Pixel));
                if (other < stars.Length)
                {
                    var starCov = GetStarPointingCovariance(other);
                    var starCombinedSigma = CombinedUncertaintyMajorSigma(detection, starCov);
                    if (settings.WindowSigmas * starCombinedSigma > MeasuredStellarAssociationJson.MaximumPlanetWindowPixels)
                        return true;
                    var starCombinedLimit = MeasuredStellarAssociationJson.PlanetMaximumWindow(
                        settings, starCombinedSigma);
                    if (otherDistance <= starCombinedLimit)
                    {
                        var (starNorm, _) = EvaluateResidual(
                            detection, targets[other].Pixel, starCov, settings.PredictionSigmaPixels);
                        if (starNorm <= windowSigmasSquared)
                            return true;
                    }
                    if (otherDistance <= settings.OffsetRadiusPixels &&
                        otherDistance < settings.AmbiguityRatio * planetDistance)
                        return true;
                }
                else
                {
                    var otherCombinedSigma = CombinedUncertaintyMajorSigma(detection, targetCovariances[other]);
                    if (settings.WindowSigmas * otherCombinedSigma > MeasuredStellarAssociationJson.MaximumPlanetWindowPixels)
                        return true;
                    var otherCombinedLimit = MeasuredStellarAssociationJson.PlanetMaximumWindow(
                        settings, otherCombinedSigma);
                    if (otherDistance <= otherCombinedLimit)
                    {
                        var (otherNorm, _) = EvaluateResidual(
                            detection, targets[other].Pixel, targetCovariances[other], settings.PredictionSigmaPixels);
                        if (otherNorm <= windowSigmasSquared)
                            return true;
                    }
                }
            }
            return false;
        }

        Covariance2D GetStarPointingCovariance(int starIndex)
        {
            if (starPointingCovariances![starIndex] is { } cached)
                return cached;
            var cov = ComputePredictionCovariance(
                targets[starIndex], scene, jacobianProjector!, settings.PredictionSigmaPixels, pointingAngularSigmaRadians);
            starPointingCovariances[starIndex] = cov;
            return cov;
        }
    }

    private static double BoundsDistanceSquared(StellarFootprintBounds footprint, PixelPoint point)
    {
        var dx = point.X < footprint.MinX ? footprint.MinX - point.X
            : point.X > footprint.MaxX + 1 ? point.X - (footprint.MaxX + 1) : 0.0;
        var dy = point.Y < footprint.MinY ? footprint.MinY - point.Y
            : point.Y > footprint.MaxY + 1 ? point.Y - (footprint.MaxY + 1) : 0.0;
        return dx * dx + dy * dy;
    }

    // Explicit legacy policy for retained v2 / external exclusions lacking an occupancy bitmap:
    // a 100%-solid box, any saturated-excessive core (whose peak often lands on a flat clipped edge far from center
    // and whose window is bounded below MaximumWindowSamples), or a compact (>= 50% filled) non-extended footprint
    // covers its bounding footprint; sparse or extended-region legacy footprints without occupancy fall back to peak
    // proximity so distant streaks or hollow arcs do not suppress isolated planets.
    private static bool LegacyFootprintCoversInterior(StellarMeasurementExclusion exclusion, long boxArea) =>
        exclusion.SampleCount >= boxArea ||
        exclusion.ReasonCode == StellarExclusionReasons.SaturatedExcessive ||
        (exclusion.ReasonCode != StellarExclusionReasons.ExtendedRegion &&
         exclusion.SampleCount * 2L >= boxArea);

    private static double OccupiedFootprintDistanceSquared(
        StellarMeasurementExclusion exclusion, PixelPoint point, double maxRadiusPixels)
    {
        if (exclusion.Footprint is not { } footprint || !footprint.Contains(point, maxRadiusPixels))
            return double.PositiveInfinity;
        if (exclusion.Occupancy is { } occupancy)
            return occupancy.DistanceSquaredToOccupied(footprint, point, maxRadiusPixels);
        var width = (long)footprint.MaxX - footprint.MinX + 1;
        var height = (long)footprint.MaxY - footprint.MinY + 1;
        return width > 0 && height > 0 && LegacyFootprintCoversInterior(exclusion, width * height)
            ? BoundsDistanceSquared(footprint, point)
            : double.PositiveInfinity;
    }

    private static StellarMeasurementExclusion? ExclusionContaining(
        PixelPoint point, IReadOnlyList<StellarMeasurementExclusion> exclusions, double maximumWindowPixels, double exclusionMarginPixels)
    {
        StellarMeasurementExclusion? best = null;
        var bestDistance = double.MaxValue;
        var maxMarginSquared = exclusionMarginPixels * exclusionMarginPixels;
        var maxWindowSquared = maximumWindowPixels * maximumWindowPixels;
        foreach (var exclusion in exclusions)
        {
            if (exclusion.Footprint is not { } footprint || !footprint.Contains(point, exclusionMarginPixels))
                continue;
            double distance;
            if (exclusion.Occupancy is { } occupancy)
            {
                distance = occupancy.DistanceSquaredToOccupied(footprint, point, exclusionMarginPixels);
                if (distance > maxMarginSquared)
                    continue;
            }
            else
            {
                var width = (long)footprint.MaxX - footprint.MinX + 1;
                var height = (long)footprint.MaxY - footprint.MinY + 1;
                if (width > 0 && height > 0 && LegacyFootprintCoversInterior(exclusion, width * height))
                {
                    distance = BoundsDistanceSquared(footprint, point);
                    if (distance > maxMarginSquared)
                        continue;
                }
                else
                {
                    distance = DistanceSquared(exclusion.Peak, point);
                    if (distance > maxWindowSquared)
                        continue;
                }
            }
            if (best is null || distance < bestDistance)
            {
                best = exclusion;
                bestDistance = distance;
            }
        }
        return best;
    }

    private static StellarMeasurementExclusion? ExcludedByPlanet(
        PixelPoint point, IReadOnlyList<StellarMeasurementExclusion> exclusions, double maximumWindowPixels, double exclusionMarginPixels)
    {
        StellarMeasurementExclusion? best = null;
        var bestRank = (Contained: false, Distance: double.MaxValue);
        var limit = maximumWindowPixels * maximumWindowPixels;
        var marginSquared = exclusionMarginPixels * exclusionMarginPixels;
        var searchRadius = Math.Max(maximumWindowPixels, exclusionMarginPixels);
        foreach (var exclusion in exclusions)
        {
            var occupiedDistance = OccupiedFootprintDistanceSquared(exclusion, point, searchRadius);
            var peakDistance = DistanceSquared(exclusion.Peak, point);
            var contained = occupiedDistance <= marginSquared;
            var distance = Math.Min(peakDistance, occupiedDistance);
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

    private static StellarMeasurementExclusion? ExcludedBy(
        PixelPoint point, IReadOnlyList<StellarMeasurementExclusion> exclusions, double maximumWindowPixels, double exclusionMarginPixels)
    {
        StellarMeasurementExclusion? best = null;
        var bestRank = (Contained: false, Distance: double.MaxValue);
        var limit = maximumWindowPixels * maximumWindowPixels;
        var marginSquared = exclusionMarginPixels * exclusionMarginPixels;
        var searchRadius = Math.Max(maximumWindowPixels, exclusionMarginPixels);
        foreach (var exclusion in exclusions)
        {
            bool contained;
            double distance;
            if (exclusion.Footprint is { } footprint && exclusion.Occupancy is { } occupancy)
            {
                var occupiedDistance = occupancy.DistanceSquaredToOccupied(footprint, point, searchRadius);
                contained = occupiedDistance <= marginSquared;
                distance = Math.Min(DistanceSquared(exclusion.Peak, point), occupiedDistance);
            }
            else
            {
                contained = exclusion.Footprint is { } fp && fp.Contains(point, exclusionMarginPixels);
                distance = DistanceSquared(exclusion.Peak, point);
            }
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
        Covariance2D predictionCovariance, double propagatedPredictionSigmaPixels, double maximumWindowPixels,
        MeasuredStellarAssociationSettingsV1 settings, StellarLabelPolicySettingsV1 labelPolicy)
    {
        var covariance = detection.CentroidCovariance;
        var trailed = detection.Conditions.HasFlag(StellarSourceConditions.Trailed);
        var angle = trailed ? Round(detection.TrailAngleDegrees) : null;
        if (angle >= 90) angle -= 180;
        var roundedPropagated = Math.Max(Round(propagatedPredictionSigmaPixels, ValueDecimals),
            Round(settings.PredictionSigmaPixels, ValueDecimals));
        // Clamp against the exact persisted rounded-sigma window validated by MeasuredStellarAssociationJson.Validate
        // so valid window-edge matches never fail Serialize after 6-decimal rounding.
        var validatedMaxWindow = MeasuredStellarAssociationJson.PlanetMaximumWindow(settings, roundedPropagated);
        var clampedResidual = Math.Min(
            Round(Math.Sqrt(DistanceSquared(detection.Pixel, planet.Pixel)), PositionDecimals),
            Round(Math.Min(maximumWindowPixels, validatedMaxWindow), ValueDecimals));
        var (normalized, _) = EvaluateResidual(detection, planet.Pixel, predictionCovariance, settings.PredictionSigmaPixels);
        var association = new MeasuredPointSourcePlanetAssociationV1(
            planet.Id,
            body,
            detection.Index,
            Round(planet.Magnitude, ValueDecimals),
            Round(planet.Pixel.X, PositionDecimals),
            Round(planet.Pixel.Y, PositionDecimals),
            Round(detection.Pixel.X, PositionDecimals),
            Round(detection.Pixel.Y, PositionDecimals),
            clampedResidual,
            Math.Min(Round(normalized, ValueDecimals), settings.WindowSigmas * settings.WindowSigmas),
            roundedPropagated,
            Math.Max(Round(CombinedUncertaintyMajorSigma(detection, predictionCovariance), ValueDecimals), roundedPropagated),
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
        return detection.Index == member.DetectionIndex &&
               member.MeasuredX == Round(detection.Pixel.X, PositionDecimals) &&
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
        return detection.Index == member.DetectionIndex &&
               member.MeasuredX == Round(detection.Pixel.X, PositionDecimals) &&
               member.MeasuredY == Round(detection.Pixel.Y, PositionDecimals) &&
               member.Flux == Round(detection.Flux, ValueDecimals) && member.SignalToNoise == Round(detection.SignalToNoise) &&
               member.CovarianceXX == Round(covariance?.XX) && member.CovarianceXY == Round(covariance?.XY) &&
               member.CovarianceYY == Round(covariance?.YY) && member.SaturatedSampleCount == detection.SaturatedSampleCount &&
               member.Trailed == detection.Conditions.HasFlag(StellarSourceConditions.Trailed);
    }

    internal static bool MatchesDetection(MeasuredStellarUnassociatedDetectionV1 member, IReadOnlyList<StellarDetection> detections)
    {
        if (member.DetectionIndex < 0 || member.DetectionIndex >= detections.Count)
            return false;
        var detection = detections[member.DetectionIndex];
        return detection.Index == member.DetectionIndex &&
               member.X == Round(detection.Pixel.X, PositionDecimals) &&
               member.Y == Round(detection.Pixel.Y, PositionDecimals) &&
               member.Flux == Round(detection.Flux, ValueDecimals) &&
               member.SignalToNoise == Round(detection.SignalToNoise);
    }

    private static IImageProjector CreateJacobianProjector(ProjectedSceneProjection p) => ProjectorFactory.Create(
        new ProjectionContext(p.Model, p.PrincipalPointX, p.PrincipalPointY, p.FocalLengthXPixels,
            p.FocalLengthYPixels, p.WidthPixels, p.HeightPixels, p.Aperture, p.ImageCircleRadiusPixels,
            p.BoresightAltitudeDegrees, p.BoresightAzimuthDegrees, p.RollDegrees, p.HorizontalFlip,
            EnforceSensorBounds: false, p.RadialDistortionK1));

    /// <summary>
    /// Propagates isotropic angular ephemeris+pointing (or pointing-only) uncertainty through the delivered-image
    /// projection Jacobian (including focal scale, off-axis perspective/fisheye geometry, distortion, crop, bin,
    /// mirror, and rotation) at the object's horizontal direction, combined in quadrature with the pixel-floor
    /// prediction variance.
    /// </summary>
    private static Covariance2D ComputePredictionCovariance(
        ProjectedCelestialObject planet,
        ProjectedSceneV1 scene,
        IImageProjector projector,
        double predictionSigmaPixels,
        double angularSigmaRadians)
    {
        var floorVariance = predictionSigmaPixels * predictionSigmaPixels;
        if (!(angularSigmaRadians > 0))
            return new(floorVariance, 0, floorVariance);

        var altRad = planet.ApparentHorizontal.AltitudeDegrees * (Math.PI / 180.0);
        var azRad = planet.ApparentHorizontal.AzimuthDegrees * (Math.PI / 180.0);
        var cosAlt = Math.Cos(altRad);
        var ux = cosAlt * Math.Cos(azRad);
        var uy = cosAlt * Math.Sin(azRad);
        var uz = Math.Sin(altRad);

        // Pole-safe orthonormal tangent basis (e1, e2) perpendicular to unit direction u.
        var (rx, ry, rz) = Math.Abs(uz) < 0.9 ? (0.0, 0.0, 1.0) : (1.0, 0.0, 0.0);
        var e1x = ry * uz - rz * uy;
        var e1y = rz * ux - rx * uz;
        var e1z = rx * uy - ry * ux;
        var e1Norm = Math.Sqrt(e1x * e1x + e1y * e1y + e1z * e1z);
        e1x /= e1Norm; e1y /= e1Norm; e1z /= e1Norm;
        var e2x = uy * e1z - uz * e1y;
        var e2y = uz * e1x - ux * e1z;
        var e2z = ux * e1y - uy * e1x;

        var transform = scene.ImageTransform;
        var fallbackScaleX = scene.Projection.FocalLengthXPixels / Math.Max(1, transform.BinX);
        var fallbackScaleY = scene.Projection.FocalLengthYPixels / Math.Max(1, transform.BinY);
        var fallbackScale = Math.Max(fallbackScaleX, fallbackScaleY);

        var (j1x, j1y) = TangentDerivative(e1x, e1y, e1z, fallbackScale, 0);
        var (j2x, j2y) = TangentDerivative(e2x, e2y, e2z, 0, fallbackScale);
        var s2 = angularSigmaRadians * angularSigmaRadians;
        return new(
            floorVariance + s2 * (j1x * j1x + j2x * j2x),
            s2 * (j1x * j1y + j2x * j2y),
            floorVariance + s2 * (j1y * j1y + j2y * j2y));

        (double Dx, double Dy) TangentDerivative(double ex, double ey, double ez, double fallbackX, double fallbackY)
        {
            var plus = ProjectOffset(+JacobianStepRadians, ex, ey, ez);
            var minus = ProjectOffset(-JacobianStepRadians, ex, ey, ez);
            if (plus is { } p && minus is { } m)
                return ((p.X - m.X) / (2 * JacobianStepRadians), (p.Y - m.Y) / (2 * JacobianStepRadians));
            if (plus is { } pOnly)
                return ((pOnly.X - planet.Pixel.X) / JacobianStepRadians, (pOnly.Y - planet.Pixel.Y) / JacobianStepRadians);
            if (minus is { } mOnly)
                return ((planet.Pixel.X - mOnly.X) / JacobianStepRadians, (planet.Pixel.Y - mOnly.Y) / JacobianStepRadians);
            return (fallbackX, fallbackY);
        }

        PixelPoint? ProjectOffset(double delta, double ex, double ey, double ez)
        {
            var vx = ux + delta * ex;
            var vy = uy + delta * ey;
            var vz = uz + delta * ez;
            var invNorm = 1.0 / Math.Sqrt(vx * vx + vy * vy + vz * vz);
            vx *= invNorm; vy *= invNorm; vz *= invNorm;
            var alt = Math.Asin(Math.Clamp(vz, -1.0, 1.0)) * (180.0 / Math.PI);
            var az = Math.Atan2(vy, vx) * (180.0 / Math.PI);
            if (az < 0) az += 360.0;
            var native = projector.Project(new AltAzPoint(alt, az));
            if (native is not { } n || !double.IsFinite(n.X) || !double.IsFinite(n.Y))
                return null;
            var delivered = ProjectedSceneImageTransform.Apply(transform, n);
            return double.IsFinite(delivered.X) && double.IsFinite(delivered.Y) ? delivered : null;
        }
    }

    private static double MajorSigma(Covariance2D covariance)
    {
        var a = Math.Max(0, covariance.XX);
        var b = double.IsFinite(covariance.XY) ? covariance.XY : 0;
        var c = Math.Max(0, covariance.YY);
        var diff = a - c;
        var majorVariance = .5 * (a + c + Math.Sqrt(diff * diff + 4 * b * b));
        return majorVariance > 0 ? Math.Sqrt(majorVariance) : 0;
    }

    /// <summary>1-sigma semi-major axis of the combined prediction-plus-centroid covariance ellipse.</summary>
    private static double CombinedUncertaintyMajorSigma(StellarDetection detection, Covariance2D predictionCovariance)
    {
        if (detection.CentroidCovariance is not { } covariance)
            return MajorSigma(predictionCovariance);
        var combined = new Covariance2D(
            predictionCovariance.XX + Math.Max(0, covariance.XX),
            predictionCovariance.XY + (double.IsFinite(covariance.XY) ? covariance.XY : 0),
            predictionCovariance.YY + Math.Max(0, covariance.YY));
        return Math.Max(MajorSigma(combined), MajorSigma(predictionCovariance));
    }

    /// <summary>Squared Mahalanobis residual and 2D Gaussian negative log-likelihood under prediction + centroid covariance.</summary>
    private static (double Normalized, double NegativeLogLikelihood) EvaluateResidual(
        StellarDetection detection, PixelPoint expected, Covariance2D predictionCovariance, double fallbackSigma)
    {
        var dx = detection.Pixel.X - expected.X;
        var dy = detection.Pixel.Y - expected.Y;
        var covariance = detection.CentroidCovariance ?? new StellarCentroidCovariance(0, 0, 0);
        var a = predictionCovariance.XX + Math.Max(0, covariance.XX);
        var b = predictionCovariance.XY + (double.IsFinite(covariance.XY) ? covariance.XY : 0);
        var c = predictionCovariance.YY + Math.Max(0, covariance.YY);
        var determinant = a * c - b * b;
        if (!(determinant > 0))
        {
            var fallbackVariance = fallbackSigma * fallbackSigma;
            var fallbackNormalized = (dx * dx + dy * dy) / fallbackVariance;
            return (fallbackNormalized, fallbackNormalized + 2 * Math.Log(fallbackVariance));
        }
        var normalized = (c * dx * dx - 2 * b * dx * dy + a * dy * dy) / determinant;
        return (normalized, normalized + Math.Log(determinant));
    }

    /// <summary>Squared Mahalanobis residual under the isotropic prediction variance plus the source's centroid covariance.</summary>
    private static double NormalizedSquared(StellarDetection detection, PixelPoint expected, double predictionSigma)
    {
        var variance = predictionSigma * predictionSigma;
        return EvaluateResidual(detection, expected, new(variance, 0, variance), predictionSigma).Normalized;
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

        public List<int> Near(PixelPoint point, double radius)
        {
            var r = Math.Max(0, radius);
            var minX = (long)Math.Floor((point.X - r) / cellSize);
            var maxX = (long)Math.Floor((point.X + r) / cellSize);
            var minY = (long)Math.Floor((point.Y - r) / cellSize);
            var maxY = (long)Math.Floor((point.Y + r) / cellSize);
            var found = new List<int>();
            var spanX = maxX - minX + 1;
            var spanY = maxY - minY + 1;
            if (spanX > 0 && spanY > 0 && spanX <= cells.Count / Math.Max(1, spanY))
            {
                for (var y = minY; y <= maxY; y++)
                    for (var x = minX; x <= maxX; x++)
                        if (cells.TryGetValue((x, y), out var list))
                            found.AddRange(list);
            }
            else
            {
                foreach (var ((x, y), list) in cells)
                    if (x >= minX && x <= maxX && y >= minY && y <= maxY)
                        found.AddRange(list);
            }
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
    internal const string V1Version = "measured-association-label-policy-v1";
    public const string Version = "measured-association-label-policy-v2";
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
    /// Resolved solar-system footprints (and non-point-source solar-system bodies such as Sun/Moon) stay outside
    /// point-source label gating, aligned with the matcher's resolved-footprint exclusion.
    /// </summary>
    public static bool IsOutsidePolicy(ProjectedAnnotationObject item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.FootprintParts is { Count: > 0 } || IsOutsidePolicy(item.Id);
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
            if (!item.DrawLabel || IsOutsidePolicy(item) || eligible.Contains(item.Id))
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
