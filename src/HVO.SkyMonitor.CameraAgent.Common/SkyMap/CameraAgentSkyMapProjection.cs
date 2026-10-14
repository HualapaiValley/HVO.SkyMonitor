using System.Globalization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;

namespace HVO.SkyMonitor.CameraAgent.Common.SkyMap;

/// <summary>
/// Projects the installed catalog, the authoritative observer location, and the
/// active rig geometry into one bounded read-only view for a UTC instant. The
/// projection is offline and deterministic for the same instant, location, rig,
/// catalog, and planet ephemeris; it never reads LogicHost, the network, or a storage path.
/// </summary>
public interface ICameraAgentSkyMapProjection
{
    /// <summary>Projects the sky map for <paramref name="atUtc"/>, or for the current instant when it is null.</summary>
    ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken);
}

/// <summary>
/// Projects the installed catalog, the authoritative observer location, and the active rig geometry into one
/// bounded read-only view for a UTC instant.
/// </summary>
/// <remarks>
/// <para>
/// The object bound applies to catalog stars only: at most <c>maximumObjects</c> of the brightest visible stars
/// are kept, and <see cref="CameraAgentSkyMapProjectionResult.ObjectsAtBound"/> reports whether more were
/// visible. When a <paramref name="planetEphemeris"/> is installed, the Sun, the Moon and the eight planets are
/// added on top of that bound whenever they are above the horizon and inside the calibrated image. They are
/// never dropped by the bound or by the limiting magnitude, so a faint Neptune or a daytime Sun is still shown,
/// and the result carries at most nine more objects than the bound.
/// </para>
/// <para>
/// Constellation figures are returned as simplified image-space polylines for every installed figure with at
/// least one chord inside the image, so a consumer can draw a figure without recomputing its geometry.
/// </para>
/// </remarks>
public sealed class CameraAgentSkyMapProjection(
    ICameraAgentConfigurationAccessor configurationAccessor,
    ICelestialCatalog catalog,
    TimeProvider timeProvider,
    IConstellationTopology? constellationTopology = null,
    IDeploymentLocationStore? deploymentLocationStore = null,
    ILatestFrameAccessor? latestFrameAccessor = null,
    CaptureScheduleRuntimeCoordinator? scheduleRuntime = null,
    Func<int>? maximumObjects = null,
    IPlanetEphemeris? planetEphemeris = null) : ICameraAgentSkyMapProjection
{
    /// <summary>The object bound applied when the host does not configure <c>SkyMap:MaximumObjects</c>.</summary>
    public const int DefaultMaximumObjects = 200;

    /// <summary>The largest object bound a host may configure for one projection.</summary>
    public const int MaximumConfigurableObjects = 5_000;

    /// <summary>The fixed limiting magnitude applied before exact horizon and projection rejection.</summary>
    public const double MaximumMagnitude = 6.5;

    /// <summary>
    /// The coordinate and selection algorithm version recorded with every projection. Version three adds the
    /// solar-system bodies, exempts them from the object bound, and reports their kind as Sun, Moon or Planet.
    /// </summary>
    public const string AstronomyAlgorithmVersion = "visible-scene-iau1976-constellation-solar-system-v3";

    /// <summary>The altitudes, in degrees, of the circles published in <see cref="CameraAgentSkyMapGeometry.AltitudeRings"/>.</summary>
    public static IReadOnlyList<double> AltitudeRingDegrees { get; } = [0, 30, 60];

    private const string SolarSystemIdPrefix = "solar-system:";
    private const double JoinTolerancePixels = 0.5;
    private const double SimplifyTolerancePixels = 1.0;
    private const int AltitudeRingSamples = 360;
    private static readonly SolarSystemBody[] SolarSystemBodies = Enum.GetValues<SolarSystemBody>();

    /// <inheritdoc />
    public async ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken)
    {
        var instant = (atUtc ?? timeProvider.GetUtcNow()).ToUniversalTime();
        // Read on every projection, so an operator's change to the bound applies to the next one.
        var limit = maximumObjects?.Invoke() ?? DefaultMaximumObjects;
        if (limit is < 1 or > MaximumConfigurableObjects)
        {
            throw new InvalidOperationException(
                $"The sky map object bound must be between 1 and {MaximumConfigurableObjects}.");
        }
        var startupConfig = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var config = scheduleRuntime?.Snapshot?.Configuration ?? startupConfig;
        if (catalog is not ICelestialCatalogMetadataSource metadataSource)
        {
            throw new InvalidOperationException(
                "The sky map requires an installed catalog that publishes validated provenance metadata.");
        }

        var location = deploymentLocationStore?.Active ?? startupConfig.DeploymentLocation
            ?? throw new InvalidOperationException(
                "The sky map requires authoritative deployment coordinates from the local configuration owner.");
        var sourceKind = deploymentLocationStore?.ResolveSourceKind(location) ?? DeploymentLocationSourceKind.Unspecified;
        var observer = new CameraAgentSkyMapObserver(
            location.LocationId,
            location.Version,
            location.CanonicalSha256,
            location.Source,
            sourceKind,
            location.Version <= 1
                ? CameraAgentSkyMapLocationOrigin.StartupSeed
                : CameraAgentSkyMapLocationOrigin.SucceededVersion,
            deploymentLocationStore?.Staged is not null,
            location.LatitudeDegrees,
            location.LongitudeDegrees,
            location.ElevationMeters,
            location.TimeZoneId,
            location.HorizontalAccuracyMeters,
            location.EffectiveFromUtc,
            location.EffectiveUntilUtc,
            location.IsEffectiveAt(instant));

        var metadata = metadataSource.Metadata;
        var projection = RigProjectionContextFactory.Create(config.Rig);
        var constellationIds = ResolveConstellationIds();
        var request = new VisibleSceneRequest(
            instant,
            new ObserverLocation(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters),
            projection,
            // One past the bound so truncation is observed rather than inferred from an exact count.
            new CatalogQuery(MaximumMagnitude, limit + 1),
            metadata,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: config.Rig.Optics.CalibrationVersion,
            algorithmVersion: AstronomyAlgorithmVersion,
            constellationIds: constellationIds,
            // Bodies are appended by the builder after the star bound, so they never displace a star.
            solarSystemBodies: planetEphemeris is null ? null : SolarSystemBodies);
        var scene = await new VisibleSceneBuilder(catalog, constellationTopology, planetEphemeris)
            .BuildAsync(request, cancellationToken).ConfigureAwait(false);

        var visibleStars = scene.Objects.Count(static item => item.Kind != CelestialObjectKind.SolarSystemBody);
        var stars = scene.Objects
            .Where(static item => item.Kind != CelestialObjectKind.SolarSystemBody)
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Take(limit);
        var bodies = scene.Objects.Where(static item => item.Kind == CelestialObjectKind.SolarSystemBody);
        var objects = stars
            .Concat(bodies)
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Select(static item => new CameraAgentSkyMapObject(
                item.Id,
                item.DisplayName,
                KindOf(item),
                item.Magnitude,
                item.ApparentHorizontal.AltitudeDegrees,
                item.ApparentHorizontal.AzimuthDegrees,
                item.Pixel.X,
                item.Pixel.Y,
                item.HipparcosId))
            .ToArray();
        var bodyCount = objects.Count(static item => item.Kind != nameof(CelestialObjectKind.Star));
        var constellations = scene.Segments
            .GroupBy(static segment => segment.ConstellationId, StringComparer.Ordinal)
            .Select(static group => new CameraAgentSkyMapConstellation(group.Key, group.Count(), JoinChords(group)))
            .OrderBy(static item => item.ConstellationId, StringComparer.Ordinal)
            .ToArray();

        var latestScene = ResolveLatestCaptureScene();
        return new CameraAgentSkyMapProjectionResult(
            instant,
            new CameraAgentSkyMapCatalogIdentity(
                metadata.Name,
                metadata.Version,
                metadata.Checksum,
                metadata.License,
                metadata.SchemaVersion,
                metadataSource.PreprocessingVersion),
            scene.ComputationProvenance.EphemerisModelVersion,
            observer,
            CreateGeometry(config, projection),
            objects,
            constellations,
            constellationIds,
            limit,
            visibleStars > limit,
            MaximumMagnitude,
            AstronomyAlgorithmVersion,
            CreateSummary(
                objects.Length - bodyCount,
                limit,
                bodyCount,
                scene.ComputationProvenance.EphemerisModelVersion,
                constellations.Length,
                constellationIds.Length,
                latestScene),
            latestScene);
    }

    private static string KindOf(ProjectedCelestialObject item)
    {
        if (item.Kind != CelestialObjectKind.SolarSystemBody)
        {
            return item.Kind.ToString();
        }

        return item.Id.StartsWith(SolarSystemIdPrefix, StringComparison.Ordinal)
            && Enum.TryParse<SolarSystemBody>(item.Id[SolarSystemIdPrefix.Length..], out var body)
                ? body switch
                {
                    SolarSystemBody.Sun => "Sun",
                    SolarSystemBody.Moon => "Moon",
                    _ => "Planet"
                }
                : "Planet";
    }

    // Consecutive chords of one figure segment share endpoints, so they are joined into one run before
    // simplification; the chord subdivision then costs nothing in the payload.
    private static CameraAgentSkyMapPolyline[] JoinChords(IEnumerable<ProjectedConstellationSegment> chords)
    {
        var lines = new List<CameraAgentSkyMapPolyline>();
        List<PixelPoint>? run = null;
        ProjectedConstellationSegment? previous = null;
        foreach (var chord in chords)
        {
            var continues = run is not null
                && previous is not null
                && chord.PartIndex == previous.PartIndex + 1
                && string.Equals(chord.FromObjectId, previous.FromObjectId, StringComparison.Ordinal)
                && string.Equals(chord.ToObjectId, previous.ToObjectId, StringComparison.Ordinal)
                && Distance(run[^1], chord.FromPixel) <= JoinTolerancePixels;
            if (!continues)
            {
                AddPolyline(run, lines);
                run = [chord.FromPixel];
            }

            run!.Add(chord.ToPixel);
            previous = chord;
        }

        AddPolyline(run, lines);
        return [.. lines];
    }

    private static CameraAgentSkyMapAltitudeRing[] CreateAltitudeRings(ProjectionContext projection)
    {
        var projector = ProjectorFactory.Create(projection);
        return AltitudeRingDegrees
            .Select(altitude => new CameraAgentSkyMapAltitudeRing(altitude, TraceAltitudeRing(projector, altitude)))
            .ToArray();
    }

    private static CameraAgentSkyMapPolyline[] TraceAltitudeRing(IImageProjector projector, double altitude)
    {
        var samples = new PixelPoint?[AltitudeRingSamples];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = projector.Project(new AltAzPoint(altitude, index * 360d / AltitudeRingSamples));
        }

        var lines = new List<CameraAgentSkyMapPolyline>();
        var gap = Array.FindIndex(samples, static sample => sample is null);
        if (gap < 0)
        {
            // The whole circle is inside the image: close it on its first sample.
            AddPolyline([.. samples.Select(static sample => sample!.Value), samples[0]!.Value], lines);
            return [.. lines];
        }

        // Start just after a gap so a run that crosses azimuth zero stays one run.
        List<PixelPoint>? run = null;
        for (var offset = 1; offset <= samples.Length; offset++)
        {
            if (samples[(gap + offset) % samples.Length] is { } pixel)
            {
                (run ??= []).Add(pixel);
            }
            else
            {
                AddPolyline(run, lines);
                run = null;
            }
        }

        AddPolyline(run, lines);
        return [.. lines];
    }

    private static void AddPolyline(List<PixelPoint>? run, List<CameraAgentSkyMapPolyline> lines)
    {
        if (run is null || run.Count < 2)
        {
            return;
        }

        var keep = new bool[run.Count];
        keep[0] = keep[^1] = true;
        Simplify(run, 0, run.Count - 1, keep);
        var points = new List<CameraAgentSkyMapPoint>(run.Count);
        for (var index = 0; index < run.Count; index++)
        {
            if (!keep[index])
            {
                continue;
            }

            var point = new CameraAgentSkyMapPoint(Math.Round(run[index].X, 1), Math.Round(run[index].Y, 1));
            if (points.Count == 0 || points[^1] != point)
            {
                points.Add(point);
            }
        }

        if (points.Count >= 2)
        {
            lines.Add(new CameraAgentSkyMapPolyline(points));
        }
    }

    // Ramer-Douglas-Peucker: keeps every point that lies farther than the tolerance from the kept chord.
    private static void Simplify(List<PixelPoint> run, int first, int last, bool[] keep)
    {
        if (last - first < 2)
        {
            return;
        }

        var farthest = -1;
        var farthestDistance = SimplifyTolerancePixels;
        for (var index = first + 1; index < last; index++)
        {
            var distance = DistanceFromLine(run[index], run[first], run[last]);
            if (distance > farthestDistance)
            {
                farthest = index;
                farthestDistance = distance;
            }
        }

        if (farthest < 0)
        {
            return;
        }

        keep[farthest] = true;
        Simplify(run, first, farthest, keep);
        Simplify(run, farthest, last, keep);
    }

    private static double Distance(PixelPoint left, PixelPoint right)
        => Math.Sqrt(((left.X - right.X) * (left.X - right.X)) + ((left.Y - right.Y) * (left.Y - right.Y)));

    private static double DistanceFromLine(PixelPoint point, PixelPoint start, PixelPoint end)
    {
        var length = Distance(start, end);
        if (length <= 1e-9)
        {
            return Distance(point, start);
        }

        return Math.Abs(((end.X - start.X) * (start.Y - point.Y)) - ((start.X - point.X) * (end.Y - start.Y))) / length;
    }

    private string[]? _constellationIds;

    // The topology is immutable for the process lifetime, so the sorted id set is computed once.
    private string[] ResolveConstellationIds()
        => _constellationIds ??= constellationTopology is InMemoryConstellationTopology topology && catalog is IHipparcosCatalog
            ? topology.AllSegments
                .Select(static segment => segment.ConstellationId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];

    private static CameraAgentSkyMapGeometry CreateGeometry(CameraModuleConfig config, ProjectionContext projection)
    {
        var landmarks = RigProjectionContextFactory.CreateAnnotationLandmarks(projection);
        return new CameraAgentSkyMapGeometry(
            projection.Model.ToString(),
            projection.Aperture.ToString(),
            projection.BoresightAltitudeDegrees,
            projection.BoresightAzimuthDegrees,
            projection.RollDegrees,
            projection.HorizontalFlip,
            config.Rig.Optics.FieldOfViewDegrees,
            config.Rig.Optics.VerticalFieldOfViewDegrees,
            projection.PrincipalPointX,
            projection.PrincipalPointY,
            projection.FocalLengthXPixels,
            projection.FocalLengthYPixels,
            projection.WidthPixels,
            projection.HeightPixels,
            projection.ImageCircleRadiusPixels,
            config.Rig.ProfileVersion,
            RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
            config.Rig.Optics.CalibrationVersion,
            RigProjectionContextFactory.AlgorithmVersion,
            [
                new CameraAgentSkyMapCardinal("North", 0, landmarks?.North.X, landmarks?.North.Y),
                new CameraAgentSkyMapCardinal("East", 90, landmarks?.East.X, landmarks?.East.Y),
                new CameraAgentSkyMapCardinal("South", 180, landmarks?.South.X, landmarks?.South.Y),
                new CameraAgentSkyMapCardinal("West", 270, landmarks?.West.X, landmarks?.West.Y)
            ],
            CreateAltitudeRings(projection),
            projection.RadialDistortionK1);
    }

    private CameraAgentSkyMapCaptureProvenance? ResolveLatestCaptureScene()
    {
        if (latestFrameAccessor is null ||
            !latestFrameAccessor.TryGetSnapshot(out var snapshot) ||
            snapshot.Metadata?.Scene is not { } scene)
        {
            return null;
        }

        return new CameraAgentSkyMapCaptureProvenance(
            scene.SceneId,
            scene.SceneUtc,
            scene.CatalogName,
            scene.CatalogVersion,
            scene.CatalogChecksumSha256,
            scene.ProjectionModel,
            scene.RigProfileVersion,
            scene.RigProfileHashSha256,
            scene.ProjectionCalibrationVersion,
            scene.ProjectedSceneStageIdentitySha256);
    }

    private static string CreateSummary(
        int starCount,
        int limit,
        int bodyCount,
        string? ephemerisModelVersion,
        int constellationCount,
        int requestedConstellationCount,
        CameraAgentSkyMapCaptureProvenance? latestScene)
    {
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"{starCount} of at most {limit} catalog objects brighter than magnitude {MaximumMagnitude} fall inside the calibrated image, with {constellationCount} of {requestedConstellationCount} installed constellation figures partly visible.");
        summary += ephemerisModelVersion is null
            ? " No planet ephemeris is installed, so the Sun, the Moon and the planets are omitted."
            : string.Create(
                CultureInfo.InvariantCulture,
                $" {bodyCount} solar-system {(bodyCount == 1 ? "body is" : "bodies are")} also above the horizon in the image, placed by ephemeris {ephemerisModelVersion} and exempt from the object limit and the limiting magnitude.");
        if (requestedConstellationCount == 0)
        {
            summary += " Constellation topology is unavailable to this catalog, so no figures were resolved.";
        }
        return latestScene is null
            ? summary + " No retained capture currently carries scene provenance, so capture-time scene identity is omitted."
            : summary;
    }
}
