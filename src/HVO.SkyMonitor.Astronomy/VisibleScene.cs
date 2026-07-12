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
        string algorithmVersion = "visible-scene-iau1976-constellation-v2",
        IReadOnlyList<string>? constellationIds = null,
        IReadOnlyList<SolarSystemBody>? solarSystemBodies = null,
        bool includeConstellationEndpointStars = false)
        : this(utc, observer, ToProjectionContext(projection), catalogQuery, catalogMetadata,
            refraction, horizonPolicy, projectionVersion, algorithmVersion, constellationIds, solarSystemBodies,
            includeConstellationEndpointStars)
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
        string algorithmVersion = "visible-scene-iau1976-constellation-v2",
        IReadOnlyList<string>? constellationIds = null,
        IReadOnlyList<SolarSystemBody>? solarSystemBodies = null,
        bool includeConstellationEndpointStars = false)
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
        ConstellationIds = new ReadOnlyCollection<string>(constellationIds
            .Select(static id => id.ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray());
        SolarSystemBodies = new ReadOnlyCollection<SolarSystemBody>(solarSystemBodies.Distinct().ToArray());
        IncludeConstellationEndpointStars = includeConstellationEndpointStars;
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

    /// <summary>Gets whether topology endpoints omitted by normal selection are included as virtual render objects.</summary>
    public bool IncludeConstellationEndpointStars { get; }

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

/// <summary>One clipped projected chord of a constellation segment.</summary>
public sealed record ProjectedConstellationSegment(
    string ConstellationId,
    string FromObjectId,
    string ToObjectId,
    PixelPoint FromPixel,
    PixelPoint ToPixel,
    int PartIndex = 0);

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

    /// <summary>Gets clipped constellation chords resolved independently of normal render-object selection.</summary>
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
            new CatalogCandidateQuery(request.CatalogQuery.MaximumMagnitude, CreateCandidateRegion(request)),
            cancellationToken).ConfigureAwait(false);
        var projector = ProjectorFactory.Create(request.Projection);
        var basis = CameraBasis.Create(
            request.Projection.BoresightAltitudeDegrees,
            request.Projection.BoresightAzimuthDegrees,
            request.Projection.RollDegrees,
            request.Projection.HorizontalFlip);
        var visible = new List<ProjectedCelestialObject>();

        var topologySegments = request.ConstellationIds.Count > 0 && _constellationTopology is null
            ? throw new InvalidOperationException("Constellations were requested without a topology provider.")
            : request.ConstellationIds
                .SelectMany(id => _constellationTopology?.GetSegments(id) ?? [])
                .ToArray();
        var endpointHipparcosIds = topologySegments
            .SelectMany(static segment => new[] { segment.FromHipparcosId, segment.ToHipparcosId })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var endpointCatalogObjects = endpointHipparcosIds.Length == 0
            ? Array.Empty<CelestialCatalogObject>()
            : _catalog is IHipparcosCatalog hipparcosCatalog
                ? (await hipparcosCatalog.GetByHipparcosIdsAsync(endpointHipparcosIds, cancellationToken)
                    .ConfigureAwait(false)).ToArray()
                : throw new InvalidOperationException(
                    "Constellations require a catalog that supports stable Hipparcos lookup.");
        var endpointsByHipparcosId = endpointCatalogObjects
            .Where(static item => item.HipparcosId is not null)
            .GroupBy(static item => item.HipparcosId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

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
        if (request.IncludeConstellationEndpointStars)
        {
            var selectedIds = new HashSet<string>(selected.Select(static item => item.Id), StringComparer.Ordinal);
            foreach (var endpoint in endpointCatalogObjects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (selectedIds.Contains(endpoint.Id))
                {
                    continue;
                }

                var projected = ProjectObject(
                    request, projector, basis, endpoint.Id, endpoint.DisplayName, CelestialObjectKind.Star,
                    new EquatorialPoint(endpoint.RightAscensionHours, endpoint.DeclinationDegrees),
                    endpoint.Magnitude, endpoint.ColorIndex, endpoint.HipparcosId);
                if (projected is not null)
                {
                    selected.Add(projected);
                    selectedIds.Add(projected.Id);
                }
            }
        }
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
        var segments = new List<ProjectedConstellationSegment>();
        foreach (var segment in topologySegments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endpointsByHipparcosId.TryGetValue(segment.FromHipparcosId, out var from) &&
                endpointsByHipparcosId.TryGetValue(segment.ToHipparcosId, out var to))
            {
                AppendProjectedSegmentChords(request, basis, segment.ConstellationId, from, to, segments);
            }
        }

        return new VisibleScene(request, selected, segments);
    }

    private static J2000SphericalCap? CreateCandidateRegion(VisibleSceneRequest request)
    {
        J2000SphericalCap? optical = null;
        if (!request.Refraction.Enabled)
        {
            var radius = OpticalRadiusDegrees(request.Projection);
            optical = CreateJ2000Cap(request, new AltAzPoint(
                request.Projection.BoresightAltitudeDegrees,
                request.Projection.BoresightAzimuthDegrees), radius);
        }

        var horizon = request.HorizonPolicy == HorizonPolicy.GeometricHorizon
            ? CreateJ2000Cap(request, new AltAzPoint(90, 0), 90)
            : (J2000SphericalCap?)null;
        if (optical is null)
        {
            return horizon;
        }
        if (horizon is null)
        {
            return optical;
        }
        return optical.Value.RadiusDegrees <= horizon.Value.RadiusDegrees ? optical : horizon;
    }

    private static J2000SphericalCap CreateJ2000Cap(
        VisibleSceneRequest request,
        AltAzPoint center,
        double radiusDegrees)
    {
        var ofDate = CoordinateTransforms.HorizontalToEquatorial(
            center, request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
        var j2000 = EquatorialPrecession.PrecessToJ2000(ofDate, request.Utc);
        return new J2000SphericalCap(
            j2000.RightAscensionHours, j2000.DeclinationDegrees,
            Math.Min(180, radiusDegrees + 1e-9));
    }

    private static double OpticalRadiusDegrees(ProjectionContext projection)
    {
        if (projection.Model == ProjectionModel.Perspective)
        {
            var horizontal = Math.Max(projection.PrincipalPointX,
                projection.WidthPixels - projection.PrincipalPointX) / projection.FocalLengthXPixels;
            var vertical = Math.Max(projection.PrincipalPointY,
                projection.HeightPixels - projection.PrincipalPointY) / projection.FocalLengthYPixels;
            return Math.Atan(Math.Sqrt(horizontal * horizontal + vertical * vertical)) * 180d / Math.PI;
        }

        var radius = projection.ImageCircleRadiusPixels!.Value;
        var focal = projection.FocalLengthXPixels;
        var angle = projection.Model switch
        {
            ProjectionModel.EquidistantFisheye => radius / focal,
            ProjectionModel.EquisolidFisheye => 2 * Math.Asin(Math.Clamp(radius / (2 * focal), -1d, 1d)),
            ProjectionModel.OrthographicFisheye => Math.Asin(Math.Clamp(radius / focal, -1d, 1d)),
            ProjectionModel.StereographicFisheye => 2 * Math.Atan(radius / (2 * focal)),
            _ => throw new ArgumentOutOfRangeException(nameof(projection))
        };
        return angle * 180d / Math.PI;
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

    internal static void AppendProjectedSegmentChords(
        VisibleSceneRequest request,
        CameraBasis basis,
        string constellationId,
        CelestialCatalogObject from,
        CelestialCatalogObject to,
        List<ProjectedConstellationSegment> output)
    {
        var chords = new List<ProjectedConstellationSegment>();
        var fromHorizontal = HorizontalOfDate(request, from);
        var toHorizontal = HorizontalOfDate(request, to);
        var fromDirection = CameraBasis.FromHorizontal(fromHorizontal);
        var toDirection = CameraBasis.FromHorizontal(toHorizontal);
        var angle = Math.Acos(Math.Clamp(EnuVector.Dot(fromDirection, toDirection), -1d, 1d));
        if (!double.IsFinite(angle) || angle >= Math.PI - 1e-9)
        {
            return;
        }

        var stepCount = Math.Max(1, (int)Math.Ceiling(angle / (2 * Math.PI / 180d)));
        var previous = fromDirection;
        for (var step = 1; step <= stepCount; step++)
        {
            var current = Slerp(fromDirection, toDirection, (double)step / stepCount);
            AppendClippedChord(request, basis, constellationId, from.Id, to.Id, previous, current, chords);
            previous = current;
        }
        for (var index = 0; index < chords.Count; index++)
        {
            output.Add(chords[index] with { PartIndex = index });
        }
    }

    private static AltAzPoint HorizontalOfDate(VisibleSceneRequest request, CelestialCatalogObject value)
    {
        var ofDate = EquatorialPrecession.PrecessJ2000(
            new EquatorialPoint(value.RightAscensionHours, value.DeclinationDegrees), request.Utc);
        return CoordinateTransforms.EquatorialToHorizontal(
            ofDate, request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
    }

    internal static void AppendClippedChord(
        VisibleSceneRequest request,
        CameraBasis basis,
        string constellationId,
        string fromObjectId,
        string toObjectId,
        EnuVector from,
        EnuVector to,
        List<ProjectedConstellationSegment> output,
        int subdivisionDepth = 0)
    {
        var fromValid = TryProjectGeometry(request, basis, from, out var fromPixel);
        var toValid = TryProjectGeometry(request, basis, to, out var toPixel);
        if (fromValid && toValid)
        {
            var middle = (from + to).Normalize();
            var middleValid = TryProjectGeometry(request, basis, middle, out var middlePixel);
            if (subdivisionDepth < 8 && (!middleValid ||
                DistanceFromChord(middlePixel, fromPixel, toPixel) > 0.25))
            {
                AppendClippedChord(request, basis, constellationId, fromObjectId, toObjectId,
                    from, middle, output, subdivisionDepth + 1);
                AppendClippedChord(request, basis, constellationId, fromObjectId, toObjectId,
                    middle, to, output, subdivisionDepth + 1);
                return;
            }
            AddClippedChord(request.Projection, constellationId, fromObjectId, toObjectId, fromPixel, toPixel, output);
            return;
        }

        if (subdivisionDepth < 8)
        {
            var middle = (from + to).Normalize();
            AppendClippedChord(request, basis, constellationId, fromObjectId, toObjectId,
                from, middle, output, subdivisionDepth + 1);
            AppendClippedChord(request, basis, constellationId, fromObjectId, toObjectId,
                middle, to, output, subdivisionDepth + 1);
            return;
        }

        if (fromValid == toValid)
        {
            return;
        }

        var validDirection = fromValid ? from : to;
        var invalidDirection = fromValid ? to : from;
        var boundaryPixel = fromValid ? fromPixel : toPixel;
        for (var iteration = 0; iteration < 32; iteration++)
        {
            var middle = (validDirection + invalidDirection).Normalize();
            if (TryProjectGeometry(request, basis, middle, out var middlePixel))
            {
                validDirection = middle;
                boundaryPixel = middlePixel;
            }
            else
            {
                invalidDirection = middle;
            }
        }

        if (fromValid)
        {
            AddClippedChord(request.Projection, constellationId, fromObjectId, toObjectId,
                fromPixel, boundaryPixel, output);
        }
        else
        {
            AddClippedChord(request.Projection, constellationId, fromObjectId, toObjectId,
                boundaryPixel, toPixel, output);
        }
    }

    internal static double DistanceFromChord(PixelPoint point, PixelPoint from, PixelPoint to)
    {
        var deltaX = to.X - from.X;
        var deltaY = to.Y - from.Y;
        var lengthSquared = deltaX * deltaX + deltaY * deltaY;
        if (lengthSquared <= 1e-20)
        {
            return Math.Sqrt(Math.Pow(point.X - from.X, 2) + Math.Pow(point.Y - from.Y, 2));
        }
        var amount = Math.Clamp(
            ((point.X - from.X) * deltaX + (point.Y - from.Y) * deltaY) / lengthSquared, 0, 1);
        var nearestX = from.X + amount * deltaX;
        var nearestY = from.Y + amount * deltaY;
        return Math.Sqrt(Math.Pow(point.X - nearestX, 2) + Math.Pow(point.Y - nearestY, 2));
    }

    internal static bool TryProjectGeometry(
        VisibleSceneRequest request,
        CameraBasis basis,
        EnuVector geometricDirection,
        out PixelPoint pixel)
    {
        if (request.HorizonPolicy == HorizonPolicy.GeometricHorizon && geometricDirection.Up < 0)
        {
            pixel = default;
            return false;
        }

        var geometric = CameraBasis.ToHorizontal(geometricDirection);
        var apparent = geometric with
        {
            AltitudeDegrees = AtmosphericRefraction.Apply(geometric.AltitudeDegrees, request.Refraction)
        };
        var camera = basis.ToCamera(CameraBasis.FromHorizontal(apparent));
        var context = request.Projection;
        if (context.Model == ProjectionModel.Perspective)
        {
            if (camera.Up <= 1e-12)
            {
                pixel = default;
                return false;
            }
            pixel = new PixelPoint(
                context.PrincipalPointX + context.FocalLengthXPixels * camera.East / camera.Up,
                context.PrincipalPointY - context.FocalLengthYPixels * camera.North / camera.Up);
            return double.IsFinite(pixel.X) && double.IsFinite(pixel.Y);
        }

        var theta = Math.Acos(Math.Clamp(camera.Up, -1d, 1d));
        if (context.Model == ProjectionModel.OrthographicFisheye && theta > Math.PI / 2 + 1e-12)
        {
            pixel = default;
            return false;
        }

        var planarLength = Math.Sqrt(camera.East * camera.East + camera.North * camera.North);
        if (planarLength <= 1e-15)
        {
            if (theta > 1e-12)
            {
                pixel = default;
                return false;
            }
            pixel = new PixelPoint(context.PrincipalPointX, context.PrincipalPointY);
            return true;
        }

        var radius = context.Model switch
        {
            ProjectionModel.EquidistantFisheye => context.FocalLengthXPixels * theta,
            ProjectionModel.EquisolidFisheye => 2 * context.FocalLengthXPixels * Math.Sin(theta / 2),
            ProjectionModel.OrthographicFisheye => context.FocalLengthXPixels * Math.Sin(theta),
            ProjectionModel.StereographicFisheye when theta < Math.PI - 1e-12
                => 2 * context.FocalLengthXPixels * Math.Tan(theta / 2),
            _ => double.NaN
        };
        pixel = new PixelPoint(
            context.PrincipalPointX + radius * camera.East / planarLength,
            context.PrincipalPointY - radius * camera.North / planarLength);
        return double.IsFinite(pixel.X) && double.IsFinite(pixel.Y);
    }

    internal static void AddClippedChord(
        ProjectionContext projection,
        string constellationId,
        string fromObjectId,
        string toObjectId,
        PixelPoint from,
        PixelPoint to,
        List<ProjectedConstellationSegment> output)
    {
        if (TryClipToProjection(projection, from, to, out var clippedFrom, out var clippedTo) &&
            (Math.Abs(clippedFrom.X - clippedTo.X) > 1e-9 || Math.Abs(clippedFrom.Y - clippedTo.Y) > 1e-9))
        {
            output.Add(new ProjectedConstellationSegment(
                constellationId, fromObjectId, toObjectId, clippedFrom, clippedTo));
        }
    }

    internal static bool TryClipToProjection(
        ProjectionContext projection,
        PixelPoint from,
        PixelPoint to,
        out PixelPoint clippedFrom,
        out PixelPoint clippedTo)
    {
        var deltaX = to.X - from.X;
        var deltaY = to.Y - from.Y;
        var minimum = 0d;
        var maximum = 1d;
        var enforceSensorBounds = projection.EnforceSensorBounds || projection.Model == ProjectionModel.Perspective;
        if (enforceSensorBounds &&
            (!ClipBoundary(-deltaX, from.X, ref minimum, ref maximum) ||
             !ClipBoundary(deltaX, projection.WidthPixels - from.X, ref minimum, ref maximum) ||
             !ClipBoundary(-deltaY, from.Y, ref minimum, ref maximum) ||
             !ClipBoundary(deltaY, projection.HeightPixels - from.Y, ref minimum, ref maximum)) ||
            projection.Aperture == ProjectionAperture.Circular &&
            !ClipCircle(projection, from, deltaX, deltaY, ref minimum, ref maximum))
        {
            clippedFrom = default;
            clippedTo = default;
            return false;
        }

        clippedFrom = new PixelPoint(from.X + minimum * deltaX, from.Y + minimum * deltaY);
        clippedTo = new PixelPoint(from.X + maximum * deltaX, from.Y + maximum * deltaY);
        return minimum <= maximum;
    }

    internal static bool ClipBoundary(double direction, double distance, ref double minimum, ref double maximum)
    {
        if (Math.Abs(direction) < 1e-15)
        {
            return distance >= 0;
        }
        var ratio = distance / direction;
        if (direction < 0)
        {
            if (ratio > maximum) return false;
            minimum = Math.Max(minimum, ratio);
        }
        else
        {
            if (ratio < minimum) return false;
            maximum = Math.Min(maximum, ratio);
        }
        return minimum <= maximum;
    }

    internal static bool ClipCircle(
        ProjectionContext projection,
        PixelPoint from,
        double deltaX,
        double deltaY,
        ref double minimum,
        ref double maximum)
    {
        var offsetX = from.X - projection.PrincipalPointX;
        var offsetY = from.Y - projection.PrincipalPointY;
        var quadratic = deltaX * deltaX + deltaY * deltaY;
        var linear = 2 * (offsetX * deltaX + offsetY * deltaY);
        var constant = offsetX * offsetX + offsetY * offsetY -
            projection.ImageCircleRadiusPixels!.Value * projection.ImageCircleRadiusPixels.Value;
        if (quadratic <= 1e-20)
        {
            return constant <= 0;
        }
        var discriminant = linear * linear - 4 * quadratic * constant;
        if (discriminant < 0)
        {
            return constant <= 0;
        }
        var root = Math.Sqrt(Math.Max(0, discriminant));
        var enter = (-linear - root) / (2 * quadratic);
        var exit = (-linear + root) / (2 * quadratic);
        minimum = Math.Max(minimum, enter);
        maximum = Math.Min(maximum, exit);
        return minimum <= maximum;
    }

    internal static EnuVector Slerp(EnuVector from, EnuVector to, double amount)
    {
        var dot = Math.Clamp(EnuVector.Dot(from, to), -1d, 1d);
        var angle = Math.Acos(dot);
        if (angle < 1e-12)
        {
            return from;
        }
        var sine = Math.Sin(angle);
        return (from * (Math.Sin((1 - amount) * angle) / sine) +
            to * (Math.Sin(amount * angle) / sine)).Normalize();
    }
}
