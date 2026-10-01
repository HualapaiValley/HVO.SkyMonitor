using System.Collections.ObjectModel;

namespace HVO.SkyMonitor.Astronomy;

/// <summary>Explicit candidate and quadrature work bounds; exhaustion refuses the exposure.</summary>
public sealed record StellarExposureGeometryOptions(
    int MaximumCandidates = 32768,
    int MaximumSamplesPerSource = 64,
    double MaximumStepPixels = .15,
    double PsfSupportRadiusPixels = 4,
    int MinimumSamplesPerSource = 1,
    int MaximumDirectionEvaluations = 16000000)
{
    /// <summary>Validates bounded candidate, temporal and optical support parameters.</summary>
    public void Validate()
    {
        if (MaximumCandidates is < 1 or > 100000 || MaximumSamplesPerSource is < 1 or > 64 ||
            !double.IsFinite(MaximumStepPixels) || MaximumStepPixels is <= 0 or > .5 ||
            !double.IsFinite(PsfSupportRadiusPixels) || PsfSupportRadiusPixels is <= 0 or > 64 ||
            MinimumSamplesPerSource < 1 || MinimumSamplesPerSource > MaximumSamplesPerSource ||
            MaximumDirectionEvaluations is < 1 or > 100000000)
            throw new ArgumentOutOfRangeException(nameof(StellarExposureGeometryOptions));
    }
}

/// <summary>One noiseless temporal source sample, weighted by its fraction of the complete exposure.</summary>
public readonly record struct StellarExposureSample(
    DateTimeOffset Utc,
    AltAzPoint Horizontal,
    PixelPoint Pixel,
    double ExposureFraction);

/// <summary>Catalog identity and temporal footprint; the optional reference is an instantaneous midpoint, not a flux centroid.</summary>
public sealed class StellarExposureObject
{
    internal StellarExposureObject(CelestialCatalogObject source, ProjectedCelestialObject? midpointReference,
        List<StellarExposureSample> samples)
    {
        Source = source;
        MidpointReference = midpointReference;
        Samples = new ReadOnlyCollection<StellarExposureSample>(samples.ToArray());
    }

    /// <summary>Gets the immutable catalog source.</summary>
    public CelestialCatalogObject Source { get; }
    /// <summary>Gets a normal visible midpoint reference when its center is inside the aperture and horizon.</summary>
    public ProjectedCelestialObject? MidpointReference { get; }
    /// <summary>Gets supported temporal samples; omitted interval energy is never renormalized.</summary>
    public IReadOnlyList<StellarExposureSample> Samples { get; }
}

/// <summary>Bounded sky geometry before any sensor-dependent visibility or flux calculation.</summary>
public sealed class StellarExposureGeometry
{
    internal StellarExposureGeometry(VisibleSceneRequest request, DateTimeOffset startUtc, DateTimeOffset endUtc,
        int candidateCount, int temporalSlots, int directionEvaluations, StellarExposureGeometryOptions options, List<StellarExposureObject> sources)
    {
        Request = request;
        StartUtc = startUtc;
        EndUtc = endUtc;
        CandidateCount = candidateCount;
        TemporalSlots = temporalSlots;
        DirectionEvaluations = directionEvaluations;
        MaximumDirectionEvaluations = options.MaximumDirectionEvaluations;
        MaximumStepPixels = options.MaximumStepPixels;
        PsfSupportRadiusPixels = options.PsfSupportRadiusPixels;
        Sources = new ReadOnlyCollection<StellarExposureObject>(sources.ToArray());
    }

    /// <summary>Identifies the sky-motion and finite-support midpoint integration policy.</summary>
    public const string AlgorithmVersion = "stellar-exposure-iau1976-bounded-midpoint-v1";
    /// <summary>Gets the midpoint scene request and immutable catalog/projection inputs.</summary>
    public VisibleSceneRequest Request { get; }
    /// <summary>Gets the exact celestial interval start.</summary>
    public DateTimeOffset StartUtc { get; }
    /// <summary>Gets the exact celestial interval end.</summary>
    public DateTimeOffset EndUtc { get; }
    /// <summary>Gets the complete queried candidate count before spatial rejection.</summary>
    public int CandidateCount { get; }
    /// <summary>Gets uniform temporal slots before horizon-boundary subdivision.</summary>
    public int TemporalSlots { get; }
    /// <summary>Gets the declared motion bound between uniform samples.</summary>
    public double MaximumStepPixels { get; }
    /// <summary>Gets the source support used for conservative swept selection.</summary>
    public double PsfSupportRadiusPixels { get; }
    /// <summary>Gets actual direction evaluations, including horizon refinement.</summary>
    public int DirectionEvaluations { get; }
    /// <summary>Gets the whole-geometry direction evaluation limit.</summary>
    public int MaximumDirectionEvaluations { get; }
    /// <summary>Gets sources whose PSF support can intersect the image during the interval.</summary>
    public IReadOnlyList<StellarExposureObject> Sources { get; }
}

/// <summary>Projects a complete bounded magnitude query through the swept field, including off-frame PSF centers.</summary>
public sealed class StellarExposureGeometryBuilder(ICelestialCatalog catalog, IConstellationTopology? constellationTopology = null)
{
    // Slightly above the geometric sidereal angular rate, including the coordinate-model drift.
    private const double MaximumSkyRadiansPerSecond = 7.3e-5;

    /// <summary>Builds storage-neutral temporal geometry without sensor, photometry or renderer dependencies.</summary>
    public async ValueTask<StellarExposureGeometry> BuildAsync(VisibleSceneRequest request,
        DateTimeOffset startUtc, TimeSpan exposure, StellarExposureGeometryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        options ??= new();
        options.Validate();
        if (startUtc == default || startUtc.Offset != TimeSpan.Zero || exposure < TimeSpan.Zero ||
            exposure > TimeSpan.FromDays(1) || request.Utc != startUtc.AddTicks(exposure.Ticks / 2))
            throw new ArgumentException("Scene reference must be the exact floor midpoint of the UTC exposure.", nameof(startUtc));
        var endUtc = startUtc + exposure;
        cancellationToken.ThrowIfCancellationRequested();
        if (catalog is ICelestialCatalogMetadataSource metadataSource && metadataSource.Metadata != request.CatalogMetadata)
            throw new ArgumentException("Catalog provenance does not match the provider.", nameof(request));
        var projection = request.Projection;
        if (exposure == TimeSpan.Zero) return new(request, startUtc, endUtc, 0, 0, 0, options, []);
        if (request.Refraction.Enabled)
            throw new NotSupportedException("Exposure geometry requires geometric altitude; refracted motion is not bounded by this version.");
        // A source can enter finite support between samples. Include half a motion step
        // in selection so neither the renderer nor a swept mask loses that interval.
        var selectionSupport = options.PsfSupportRadiusPixels + options.MaximumStepPixels / 2;
        var expanded = ExpandProjection(projection, selectionSupport);
        var speedBound = MaximumProjectionSpeed(expanded, selectionSupport);
        var requiredSlots = Math.Max(options.MinimumSamplesPerSource, Math.Ceiling(exposure.TotalSeconds * speedBound / options.MaximumStepPixels));
        // A geometric sidereal horizon arc of at most six hours contains at most one
        // culmination. Near the horizon, explicitly inspect that extremum instead of
        // inferring visibility solely from the endpoints and midpoint.
        if (request.HorizonPolicy == HorizonPolicy.GeometricHorizon)
            requiredSlots = Math.Max(requiredSlots, Math.Ceiling(exposure.TotalHours / 6));
        if (!double.IsFinite(requiredSlots) || requiredSlots > options.MaximumSamplesPerSource)
            throw new InvalidOperationException("stellar-exposure-temporal-budget-exceeded");
        var slots = (int)requiredSlots;
        var sources = new List<StellarExposureObject>();
        var directionEvaluations = 0;
        // A global magnitude query is deliberately conservative. No instantaneous spatial cap
        // may erase a star that enters the sensor/aperture or contributes only clipped PSF support.
        var queriedCandidates = await catalog.QueryCandidatesAsync(new(request.CatalogQuery.MaximumMagnitude), cancellationToken)
            .ConfigureAwait(false);
        var candidates = queriedCandidates;
        if (request.IncludeConstellationEndpointStars && request.ConstellationIds.Count > 0)
        {
            if (constellationTopology is null || catalog is not IHipparcosCatalog hipparcosCatalog)
                throw new InvalidOperationException("Constellation endpoint stars require topology and stable catalog lookup.");
            var ids = request.ConstellationIds.SelectMany(id => constellationTopology.GetSegments(id))
                .SelectMany(static segment => new[] { segment.FromHipparcosId, segment.ToHipparcosId })
                .Distinct(StringComparer.Ordinal).ToArray();
            var endpoints = await hipparcosCatalog.GetByHipparcosIdsAsync(ids, cancellationToken).ConfigureAwait(false);
            candidates = queriedCandidates.Concat(endpoints).DistinctBy(static source => source.Id, StringComparer.Ordinal).ToArray();
        }
        if (candidates.Count > options.MaximumCandidates)
            throw new InvalidOperationException("stellar-exposure-candidate-budget-exceeded");
        var projector = ProjectorFactory.Create(expanded);
        var midpointProjector = ProjectorFactory.Create(projection);
        var basis = CameraBasis.Create(projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees,
            projection.RollDegrees, projection.HorizontalFlip);
        foreach (var source in candidates.OrderBy(static item => item.Magnitude).ThenBy(static item => item.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var samples = new List<StellarExposureSample>();
            for (var index = 0; index < slots; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var left = startUtc.AddTicks(exposure.Ticks * index / slots);
                var right = startUtc.AddTicks(exposure.Ticks * (index + 1) / slots);
                AppendInterval(left, right, 0);
            }
            if (samples.Count == 0) continue;
            var (ofDate, geometric, apparent) = Direction(request.Utc);
            var pixel = midpointProjector.Project(apparent);
            var reference = pixel is null || request.HorizonPolicy == HorizonPolicy.GeometricHorizon && geometric.AltitudeDegrees < 0
                ? null : new ProjectedCelestialObject(source.Id, source.DisplayName, CelestialObjectKind.Star,
                    new(source.RightAscensionHours, source.DeclinationDegrees), ofDate, geometric, apparent,
                    basis.ToCamera(CameraBasis.FromHorizontal(apparent)), pixel.Value, source.Magnitude, source.ColorIndex,
                    request.CatalogMetadata.Version, request.ProjectionVersion, StellarExposureGeometry.AlgorithmVersion, source.HipparcosId);
            sources.Add(new(source, reference, samples));

            (EquatorialPoint OfDate, AltAzPoint Geometric, AltAzPoint Apparent) Direction(DateTimeOffset utc)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (directionEvaluations >= options.MaximumDirectionEvaluations)
                    throw new InvalidOperationException("stellar-exposure-direction-budget-exceeded");
                directionEvaluations++;
                var position = EquatorialPrecession.PrecessJ2000(new(source.RightAscensionHours, source.DeclinationDegrees), utc);
                var horizontal = CoordinateTransforms.EquatorialToHorizontal(position, utc,
                    request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees);
                return (position, horizontal, horizontal with
                {
                    AltitudeDegrees = AtmosphericRefraction.Apply(horizontal.AltitudeDegrees, request.Refraction)
                });
            }

            void AppendInterval(DateTimeOffset left, DateTimeOffset right, int depth)
            {
                if (right <= left) return;
                var middle = left.AddTicks((right - left).Ticks / 2);
                if (request.HorizonPolicy == HorizonPolicy.GeometricHorizon)
                {
                    var a = Direction(left).Geometric.AltitudeDegrees;
                    var b = Direction(right).Geometric.AltitudeDegrees;
                    var m = Direction(middle).Geometric.AltitudeDegrees;
                    if ((a >= 0) == (b >= 0) && (a >= 0) != (m >= 0))
                    {
                        if (depth >= 8) throw new InvalidOperationException("stellar-exposure-horizon-refinement-budget-exceeded");
                        AppendInterval(left, middle, depth + 1); AppendInterval(middle, right, depth + 1); return;
                    }
                    if ((a >= 0) == (b >= 0))
                    {
                        var visible = a >= 0;
                        var motionMarginDegrees = MaximumSkyRadiansPerSecond * 180 / Math.PI * (right - left).TotalSeconds / 4;
                        if (visible ? Math.Min(a, Math.Min(b, m)) < motionMarginDegrees :
                            Math.Max(a, Math.Max(b, m)) >= -motionMarginDegrees)
                        {
                            var extremum = HorizonExtremum(left, right, a, b, m, maximize: !visible);
                            if ((extremum.Altitude >= 0) != visible)
                            {
                                if (depth >= 8 || extremum.Utc <= left || extremum.Utc >= right)
                                    throw new InvalidOperationException("stellar-exposure-horizon-refinement-budget-exceeded");
                                AppendInterval(left, extremum.Utc, depth + 1);
                                AppendInterval(extremum.Utc, right, depth + 1);
                                return;
                            }
                        }
                    }
                    if (a < 0 && b < 0) return;
                    if ((a >= 0) != (b >= 0))
                    {
                        var low = left; var high = right;
                        while ((high - low).Ticks > 1)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var test = low.AddTicks((high - low).Ticks / 2);
                            if ((Direction(test).Geometric.AltitudeDegrees >= 0) == (a >= 0)) low = test; else high = test;
                        }
                        if (a >= 0) right = high; else left = high;
                        if (right <= left) return;
                        middle = left.AddTicks((right - left).Ticks / 2);
                    }
                }
                var direction = Direction(middle).Apparent;
                if (projector.Project(direction) is not { } samplePixel || !HasSupport(samplePixel, projection, selectionSupport)) return;
                if (samples.Count >= options.MaximumSamplesPerSource)
                    throw new InvalidOperationException("stellar-exposure-temporal-budget-exceeded");
                samples.Add(new(middle, direction, samplePixel, (double)(right - left).Ticks / exposure.Ticks));
            }

            (DateTimeOffset Utc, double Altitude) HorizonExtremum(DateTimeOffset left, DateTimeOffset right,
                double a, double b, double m, bool maximize)
            {
                var best = (Utc: left, Altitude: a);
                Consider(right, b);
                Consider(left.AddTicks((right - left).Ticks / 2), m);
                const double fraction = .3819660112501051;
                var low = left;
                var high = right;
                var first = low.AddTicks((long)Math.Round((high - low).Ticks * fraction));
                var second = high.AddTicks(-(long)Math.Round((high - low).Ticks * fraction));
                var firstValue = Evaluate(first);
                var secondValue = Evaluate(second);
                // 72 golden-section iterations cover a six-hour interval down to UTC ticks.
                // The independent direction budget also bounds degenerate near-horizon fields.
                for (var iteration = 0; iteration < 72 && (high - low).Ticks > 2 && first < second; iteration++)
                {
                    if (maximize ? firstValue < secondValue : firstValue > secondValue)
                    {
                        low = first;
                        first = second;
                        firstValue = secondValue;
                        second = high.AddTicks(-(long)Math.Round((high - low).Ticks * fraction));
                        secondValue = Evaluate(second);
                    }
                    else
                    {
                        high = second;
                        second = first;
                        secondValue = firstValue;
                        first = low.AddTicks((long)Math.Round((high - low).Ticks * fraction));
                        firstValue = Evaluate(first);
                    }
                }
                return best;

                double Evaluate(DateTimeOffset time)
                {
                    var altitude = Direction(time).Geometric.AltitudeDegrees;
                    Consider(time, altitude);
                    return altitude;
                }

                void Consider(DateTimeOffset time, double altitude)
                {
                    if (maximize ? altitude > best.Altitude : altitude < best.Altitude) best = (time, altitude);
                }
            }
        }
        return new(request, startUtc, endUtc, candidates.Count, slots, directionEvaluations, options, sources);
    }

    private static bool HasSupport(PixelPoint pixel, ProjectionContext projection, double radius)
        => pixel.X >= -radius && pixel.X <= projection.WidthPixels + radius &&
           pixel.Y >= -radius && pixel.Y <= projection.HeightPixels + radius &&
           (projection.ImageCircleRadiusPixels is not { } circle ||
            Math.Sqrt(Math.Pow(pixel.X - projection.PrincipalPointX, 2) + Math.Pow(pixel.Y - projection.PrincipalPointY, 2)) <= circle + radius);

    private static ProjectionContext ExpandProjection(ProjectionContext projection, double support)
    {
        if (projection.Model == ProjectionModel.Perspective) return projection with { EnforceSensorBounds = false };
        var maximumRadius = projection.Model switch
        {
            ProjectionModel.EquidistantFisheye => Math.PI * projection.FocalLengthXPixels,
            ProjectionModel.EquisolidFisheye => 2 * projection.FocalLengthXPixels,
            ProjectionModel.OrthographicFisheye => projection.FocalLengthXPixels,
            _ => double.MaxValue
        };
        return projection with
        {
            EnforceSensorBounds = false,
            ImageCircleRadiusPixels = Math.Min(maximumRadius, projection.ImageCircleRadiusPixels!.Value + support)
        };
    }

    private static double MaximumProjectionSpeed(ProjectionContext projection, double supportRadius)
    {
        var focal = Math.Max(projection.FocalLengthXPixels, projection.FocalLengthYPixels);
        double jacobian;
        if (projection.Model == ProjectionModel.Perspective)
        {
            var x = (supportRadius + Math.Max(Math.Abs(projection.PrincipalPointX), Math.Abs(projection.WidthPixels - projection.PrincipalPointX))) / projection.FocalLengthXPixels;
            var y = (supportRadius + Math.Max(Math.Abs(projection.PrincipalPointY), Math.Abs(projection.HeightPixels - projection.PrincipalPointY))) / projection.FocalLengthYPixels;
            jacobian = 1 + x * x + y * y;
        }
        else
        {
            var ratio = projection.ImageCircleRadiusPixels!.Value / focal;
            jacobian = projection.Model switch
            {
                ProjectionModel.EquidistantFisheye => ratio <= 1e-12 ? 1 : ratio / Math.Sin(ratio),
                ProjectionModel.EquisolidFisheye => 1 / Math.Sqrt(1 - ratio * ratio / 4),
                ProjectionModel.OrthographicFisheye => 1,
                ProjectionModel.StereographicFisheye => 1 + ratio * ratio / 4,
                _ => throw new ArgumentOutOfRangeException(nameof(projection))
            };
        }
        return MaximumSkyRadiansPerSecond * focal * jacobian;
    }
}
