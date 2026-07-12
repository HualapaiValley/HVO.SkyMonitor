using System.Collections.ObjectModel;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>An immutable terrestrial observing location using east-positive longitude.</summary>
public readonly record struct ObserverLocation(double LatitudeDegrees, double LongitudeDegrees, double ElevationMeters)
{
    /// <summary>Validates finite terrestrial coordinates and elevation.</summary>
    public void Validate()
    {
        if (!double.IsFinite(LatitudeDegrees) || LatitudeDegrees is < -90 or > 90 ||
            !double.IsFinite(LongitudeDegrees) || LongitudeDegrees is < -180 or > 180 ||
            !double.IsFinite(ElevationMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(ObserverLocation));
        }
    }
}

/// <summary>Controls geometric horizon rejection before optical projection.</summary>
public enum HorizonPolicy
{
    /// <summary>Only directions on or above the geometric horizon are eligible.</summary>
    GeometricHorizon,

    /// <summary>The calibrated optical projection alone determines eligibility.</summary>
    ProjectionOnly
}

/// <summary>The physical class of a projected scene object.</summary>
public enum CelestialObjectKind
{
    /// <summary>A fixed catalog star.</summary>
    Star,

    /// <summary>A solar-system body supplied by the configured ephemeris.</summary>
    SolarSystemBody
}

/// <summary>An immutable request for one reproducible catalog-backed visible scene.</summary>
public sealed class VisibleSceneRequest
{
    /// <summary>Creates a request from the legacy equidistant-only context.</summary>
    public VisibleSceneRequest(
        DateTimeOffset utc,
        ObserverLocation observer,
        EquidistantProjectionContext projection,
        CatalogQuery catalogQuery,
        CatalogMetadata catalogMetadata,
        RefractionOptions refraction = default,
        HorizonPolicy horizonPolicy = HorizonPolicy.GeometricHorizon,
        string projectionVersion = "equidistant-v1",
        string algorithmVersion = "visible-scene-iau1976-v1",
        IReadOnlyList<string>? constellationIds = null,
        IReadOnlyList<SolarSystemBody>? solarSystemBodies = null)
        : this(utc, observer, ToProjectionContext(projection), catalogQuery, catalogMetadata,
            refraction, horizonPolicy, projectionVersion, algorithmVersion, constellationIds, solarSystemBodies)
    {
    }

    /// <summary>Creates and validates a visible-scene request.</summary>
    public VisibleSceneRequest(
        DateTimeOffset utc,
        ObserverLocation observer,
        ProjectionContext projection,
        CatalogQuery catalogQuery,
        CatalogMetadata catalogMetadata,
        RefractionOptions refraction = default,
        HorizonPolicy horizonPolicy = HorizonPolicy.GeometricHorizon,
        string projectionVersion = "equidistant-v1",
        string algorithmVersion = "visible-scene-iau1976-v1",
        IReadOnlyList<string>? constellationIds = null,
        IReadOnlyList<SolarSystemBody>? solarSystemBodies = null)
    {
        ArgumentNullException.ThrowIfNull(catalogQuery);
        ArgumentNullException.ThrowIfNull(catalogMetadata);
        observer.Validate();
        projection.Validate();
        catalogQuery.Validate();
        refraction.Validate();
        ValidateMetadata(catalogMetadata);
        if (!Enum.IsDefined(horizonPolicy))
        {
            throw new ArgumentOutOfRangeException(nameof(horizonPolicy));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(projectionVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(algorithmVersion);
        constellationIds ??= Array.Empty<string>();
        solarSystemBodies ??= Array.Empty<SolarSystemBody>();
        if (constellationIds.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Constellation identifiers cannot be blank.", nameof(constellationIds));
        }
        if (solarSystemBodies.Any(body => !Enum.IsDefined(body)))
        {
            throw new ArgumentOutOfRangeException(nameof(solarSystemBodies));
        }
        Utc = utc.ToUniversalTime();
        Observer = observer;
        Projection = projection;
        CatalogQuery = catalogQuery;
        CatalogMetadata = catalogMetadata;
        Refraction = refraction;
        HorizonPolicy = horizonPolicy;
        ProjectionVersion = projectionVersion;
        AlgorithmVersion = algorithmVersion;
        ConstellationIds = new ReadOnlyCollection<string>(constellationIds.Distinct(StringComparer.Ordinal).ToArray());
        SolarSystemBodies = new ReadOnlyCollection<SolarSystemBody>(solarSystemBodies.Distinct().ToArray());
    }

    /// <summary>Gets the normalized UTC scene instant.</summary>
    public DateTimeOffset Utc { get; }

    /// <summary>Gets the terrestrial observer.</summary>
    public ObserverLocation Observer { get; }

    /// <summary>Gets the calibrated model-neutral optical projection.</summary>
    public ProjectionContext Projection { get; }

    /// <summary>Gets magnitude and final visible-result limits.</summary>
    public CatalogQuery CatalogQuery { get; }

    /// <summary>Gets catalog provenance captured with the result.</summary>
    public CatalogMetadata CatalogMetadata { get; }

    /// <summary>Gets the apparent-altitude refraction policy.</summary>
    public RefractionOptions Refraction { get; }

    /// <summary>Gets the geometric horizon policy.</summary>
    public HorizonPolicy HorizonPolicy { get; }

    /// <summary>Gets the caller's projection/calibration version.</summary>
    public string ProjectionVersion { get; }

    /// <summary>Gets the coordinate and selection algorithm version.</summary>
    public string AlgorithmVersion { get; }

    /// <summary>Gets stable constellation identifiers requested for projected derivative topology.</summary>
    public IReadOnlyList<string> ConstellationIds { get; }

    /// <summary>Gets solar-system bodies requested from the configured ephemeris.</summary>
    public IReadOnlyList<SolarSystemBody> SolarSystemBodies { get; }

    private static void ValidateMetadata(CatalogMetadata metadata)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.Version);
        ArgumentNullException.ThrowIfNull(metadata.SourceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.Checksum);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.License);
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.SchemaVersion);
        if (!metadata.SourceUrl.IsAbsoluteUri)
        {
            throw new ArgumentException("The catalog source URL must be absolute.", nameof(metadata));
        }
    }

    private static ProjectionContext ToProjectionContext(EquidistantProjectionContext projection)
    {
        projection.Validate();
        var width = projection.WidthPixels > 0
            ? projection.WidthPixels
            : Math.Max(1, checked((int)Math.Ceiling(2 * (Math.Abs(projection.PrincipalPointX) + projection.ImageCircleRadiusPixels))));
        var height = projection.HeightPixels > 0
            ? projection.HeightPixels
            : Math.Max(1, checked((int)Math.Ceiling(2 * (Math.Abs(projection.PrincipalPointY) + projection.ImageCircleRadiusPixels))));
        return new ProjectionContext(
            ProjectionModel.EquidistantFisheye, projection.PrincipalPointX, projection.PrincipalPointY,
            projection.FocalLengthPixels, projection.FocalLengthPixels, width, height,
            ProjectionAperture.Circular, projection.ImageCircleRadiusPixels,
            projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees,
            projection.RollDegrees, projection.HorizontalFlip,
            projection.WidthPixels > 0);
    }
}

/// <summary>One immutable catalog object selected and projected into a visible scene.</summary>
public sealed record ProjectedCelestialObject(
    string Id,
    string DisplayName,
    CelestialObjectKind Kind,
    EquatorialPoint J2000Equatorial,
    EquatorialPoint EquatorialOfDate,
    AltAzPoint GeometricHorizontal,
    AltAzPoint ApparentHorizontal,
    EnuVector CameraDirection,
    PixelPoint Pixel,
    double Magnitude,
    double? ColorIndex,
    string CatalogVersion,
    string ProjectionVersion,
    string AlgorithmVersion,
    string? HipparcosId = null);

/// <summary>A constellation segment whose stable catalog endpoints are both present in this scene.</summary>
public sealed record ProjectedConstellationSegment(
    string ConstellationId,
    string FromObjectId,
    string ToObjectId,
    PixelPoint FromPixel,
    PixelPoint ToPixel);

/// <summary>The immutable geometry authority shared by rendering and annotation.</summary>
public sealed class VisibleScene
{
    internal VisibleScene(
        VisibleSceneRequest request,
        IEnumerable<ProjectedCelestialObject> objects,
        IEnumerable<ProjectedConstellationSegment>? segments = null)
    {
        Request = request;
        Objects = new ReadOnlyCollection<ProjectedCelestialObject>(objects.ToArray());
        Segments = new ReadOnlyCollection<ProjectedConstellationSegment>((segments ?? []).ToArray());
    }

    /// <summary>Gets the validated request and provenance for this scene.</summary>
    public VisibleSceneRequest Request { get; }

    /// <summary>Gets visible objects in stable magnitude-then-ID order.</summary>
    public IReadOnlyList<ProjectedCelestialObject> Objects { get; }

    /// <summary>Gets requested constellation segments resolved from the same visible object set.</summary>
    public IReadOnlyList<ProjectedConstellationSegment> Segments { get; }
}

/// <summary>Builds deterministic visible scenes without persistence or rendering dependencies.</summary>
public sealed class VisibleSceneBuilder
{
    private readonly ICelestialCatalog _catalog;
    private readonly IConstellationTopology? _constellationTopology;
    private readonly IPlanetEphemeris? _planetEphemeris;

    /// <summary>Creates a builder over a storage-neutral celestial catalog.</summary>
    public VisibleSceneBuilder(
        ICelestialCatalog catalog,
        IConstellationTopology? constellationTopology = null,
        IPlanetEphemeris? planetEphemeris = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _catalog = catalog;
        _constellationTopology = constellationTopology;
        _planetEphemeris = planetEphemeris;
    }

    /// <summary>
    /// Queries all magnitude candidates, performs exact horizon and projection
    /// rejection, then applies the visible-result limit deterministically.
    /// </summary>
    public async ValueTask<VisibleScene> BuildAsync(
        VisibleSceneRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var candidates = await _catalog.QueryCandidatesAsync(
            new CatalogCandidateQuery(request.CatalogQuery.MaximumMagnitude), cancellationToken).ConfigureAwait(false);
        var projector = ProjectorFactory.Create(request.Projection);
        var basis = CameraBasis.Create(
            request.Projection.BoresightAltitudeDegrees,
            request.Projection.BoresightAzimuthDegrees,
            request.Projection.RollDegrees,
            request.Projection.HorizontalFlip);
        var visible = new List<ProjectedCelestialObject>();

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projected = ProjectObject(
                request, projector, basis,
                item.Id,
                item.DisplayName,
                CelestialObjectKind.Star,
                new EquatorialPoint(item.RightAscensionHours, item.DeclinationDegrees),
                item.Magnitude,
                item.ColorIndex,
                item.HipparcosId);
            if (projected is not null)
            {
                visible.Add(projected);
            }
        }

        var selectedStars = visible
            .OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .Take(request.CatalogQuery.MaximumResults)
            .ToArray();
        var selected = selectedStars.ToList();
        if (request.SolarSystemBodies.Count > 0 && _planetEphemeris is null)
        {
            throw new InvalidOperationException("Solar-system bodies were requested without an ephemeris provider.");
        }
        foreach (var body in request.SolarSystemBodies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = _planetEphemeris!.GetPosition(body, request.Utc);
            var projected = ProjectObject(
                request, projector, basis,
                $"solar-system:{body}", body.ToString(),
                CelestialObjectKind.SolarSystemBody, position.EquatorialJ2000, position.VisualMagnitude, null, null);
            if (projected is not null)
            {
                selected.Add(projected);
            }
        }

        selected = selected.OrderBy(static item => item.Magnitude)
            .ThenBy(static item => item.Id, StringComparer.Ordinal)
            .ToList();
        var byHipparcosId = selectedStars
            .Where(static item => item.HipparcosId is not null)
            .GroupBy(static item => item.HipparcosId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var segments = new List<ProjectedConstellationSegment>();
        if (_constellationTopology is not null)
        {
            foreach (var constellationId in request.ConstellationIds)
            {
                foreach (var segment in _constellationTopology.GetSegments(constellationId))
                {
                    if (byHipparcosId.TryGetValue(segment.FromHipparcosId, out var from) &&
                        byHipparcosId.TryGetValue(segment.ToHipparcosId, out var to))
                    {
                        segments.Add(new ProjectedConstellationSegment(
                            segment.ConstellationId, from.Id, to.Id,
                            from.Pixel, to.Pixel));
                    }
                }
            }
        }

        return new VisibleScene(request, selected, segments);
    }

    private static ProjectedCelestialObject? ProjectObject(
        VisibleSceneRequest request,
        IImageProjector projector,
        CameraBasis basis,
        string id,
        string displayName,
        CelestialObjectKind kind,
        EquatorialPoint j2000,
        double magnitude,
        double? colorIndex,
        string? hipparcosId)
    {
        var ofDate = EquatorialPrecession.PrecessJ2000(j2000, request.Utc);
        var geometric = CoordinateTransforms.EquatorialToHorizontal(
            ofDate, request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
        if (request.HorizonPolicy == HorizonPolicy.GeometricHorizon && geometric.AltitudeDegrees < 0)
        {
            return null;
        }

        var apparent = geometric with
        {
            AltitudeDegrees = AtmosphericRefraction.Apply(geometric.AltitudeDegrees, request.Refraction)
        };
        var pixel = projector.Project(apparent);
        return pixel is null
            ? null
            : new ProjectedCelestialObject(
                id, displayName, kind, j2000, ofDate, geometric, apparent,
                basis.ToCamera(CameraBasis.FromHorizontal(apparent)), pixel.Value, magnitude, colorIndex,
                request.CatalogMetadata.Version, request.ProjectionVersion, request.AlgorithmVersion, hipparcosId);
    }
}
