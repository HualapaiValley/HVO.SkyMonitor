using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;

namespace HVO.SkyMonitor.Imaging;

/// <summary>Bounded settings for deterministic parametric extended deep-sky source rendering.</summary>
public sealed record DeepSkyRenderSettings(
    double MinimumSignalToNoise = 3.0,
    int MaximumCandidates = 128,
    int MaximumRenderedSources = 64,
    int MaximumSparsePixels = DeepSkyRenderPlan.MaximumSparsePixelsLimit,
    int MaximumSamplePointsPerSource = 65_536,
    int MaximumTotalSamplePoints = 2_000_000,
    long MaximumKernelCellVisits = DeepSkyRenderPlan.MaximumKernelCellVisitsLimit,
    int SamplesPerProjectedPixel = 2,
    double PsfSigmaPixels = 1,
    double PsfRadiusPixels = 4,
    HorizonPolicy HorizonPolicy = HorizonPolicy.GeometricHorizon,
    RefractionOptions Refraction = default,
    bool PreferCatalogOutlines = true,
    int PreferredOutlineLevel = DeepSkyOutline.WidestLevel,
    bool AllowSurfaceBrightnessB25Fallback = false,
    double CelestialDiffuseBackgroundElectronsPerSecond = 0,
    int DigitalBinX = 1,
    int DigitalBinY = 1,
    bool DigitalAverage = true,
    int CfaOriginX = 0,
    int CfaOriginY = 0,
    DeepSkySyntheticScenario? SyntheticScenario = null,
    long MaximumOutlineEdgeVisits = DeepSkyRenderPlan.MaximumOutlineEdgeVisitsLimit,
    DateTimeOffset? ExposureStartUtc = null,
    int MaximumTemporalSamples = 64,
    double MaximumTemporalStepPixels = 0.15,
    int MinimumTemporalSamples = 1)
{
    public void Validate()
    {
        if (!double.IsFinite(MinimumSignalToNoise) || MinimumSignalToNoise is < 0 or > 1e6 ||
            MaximumCandidates is < 1 or > 2048 ||
            MaximumRenderedSources is < 1 or > 512 ||
            MaximumSparsePixels is < 1 or > DeepSkyRenderPlan.MaximumSparsePixelsLimit ||
            MaximumSamplePointsPerSource is < 16 or > 262_144 ||
            MaximumTotalSamplePoints is < 16 or > 8_000_000 ||
            MaximumKernelCellVisits is < 1 or > DeepSkyRenderPlan.MaximumKernelCellVisitsLimit ||
            MaximumOutlineEdgeVisits is < 1 or > DeepSkyRenderPlan.MaximumOutlineEdgeVisitsLimit ||
            SamplesPerProjectedPixel is < 1 or > 8 ||
            !double.IsFinite(PsfSigmaPixels) || PsfSigmaPixels is <= 0 or > 64 ||
            !double.IsFinite(PsfRadiusPixels) || PsfRadiusPixels is <= 0 or > 64 ||
            !Enum.IsDefined(HorizonPolicy) ||
            PreferredOutlineLevel is < DeepSkyOutline.WidestLevel or > DeepSkyOutline.NarrowestLevel ||
            !double.IsFinite(CelestialDiffuseBackgroundElectronsPerSecond) ||
            CelestialDiffuseBackgroundElectronsPerSecond is < 0 or > 1e12 ||
            DigitalBinX is < 1 or > 64 || DigitalBinY is < 1 or > 64 ||
            CfaOriginX is < 0 or > 1 || CfaOriginY is < 0 or > 1 ||
            ExposureStartUtc is { Offset: var offset } && offset != TimeSpan.Zero ||
            MaximumTemporalSamples is < 1 or > 64 ||
            MinimumTemporalSamples < 1 || MinimumTemporalSamples > MaximumTemporalSamples ||
            !double.IsFinite(MaximumTemporalStepPixels) || MaximumTemporalStepPixels is <= 0 or > 0.5)
        {
            throw new ArgumentOutOfRangeException(nameof(DeepSkyRenderSettings));
        }
        Refraction.Validate();
        SyntheticScenario?.Validate();
    }
}

/// <summary>
/// Conditional per-source simulation diagnostics retained for scoring and verification. Never treated as measured
/// detections; measured associations continue to come solely from rendered pixels.
/// </summary>
public sealed record DeepSkySourceDiagnostic(
    string ObjectId,
    string DisplayName,
    string ObjectType,
    DeepSkyProfileClass ProfileClass,
    bool Admitted,
    string Reason,
    DeepSkyPhotometryBasis PhotometryBasis,
    DeepSkyGeometryBasis GeometryBasis,
    string ProfileSchemaVersion,
    string MorphologyModel,
    string SpectralModel,
    DeepSkySpectralOrigin SpectralOrigin,
    bool IsSimulationParameter,
    double SourceElectronRate,
    double VisibleElectronRate,
    double RetainedElectronRate,
    double RetainedEnergyFraction,
    int MatchedHygStarCount,
    double MatchedHygFluxFraction,
    int UnresolvedMemberCount,
    double AttenuatedDiffuseElectronRate,
    StellarSignalPrediction PredictedSignal,
    int SampledPoints,
    int RasterizedPixels,
    bool UsedUnresolvedPsfPath,
    RenderedObjectGeometry? Footprint);

/// <summary>Aggregate work, admission, and provenance counters for one rendered deep-sky plan.</summary>
public sealed record DeepSkyRenderStatistics(
    int CandidateCount,
    int EligibleCount,
    int AdmittedCount,
    int AnnotationOnlyCount,
    int UnresolvedPsfCount,
    int DarkAttenuationCount,
    long TotalSampledPoints,
    int TotalRasterizedPixels,
    long TotalKernelCellVisits,
    string DiagnosticsSha256);

/// <summary>
/// Bounded deterministic parametric deep-sky raster in incident electron-rate space.
/// Normalizes compatible integrated band flux over the full angular profile before horizon, projection, PSF,
/// sensor, or aperture clipping. Unresolved sources converge smoothly to the stellar PSF path, open clusters avoid
/// double-counting HYG members, and dark nebulae attenuate only compatible celestial diffuse backgrounds.
/// </summary>
public sealed class DeepSkyRenderPlan
{
    public const string AlgorithmVersion = "deep-sky-parametric-v1";
    public const string DiagnosticIdentityVersion = "deep-sky-expected-diagnostics-sha256-v1";
    public const int MaximumSparsePixelsLimit = 524_288;
    public const long MaximumKernelCellVisitsLimit = 67_108_864;
    public const long MaximumOutlineEdgeVisitsLimit = 16_777_216;

    /// <summary>Sources with projected major axis at or below this multiple of PSF sigma take the stellar PSF path.</summary>
    public const double UnresolvedDiameterPsfSigmaRatio = 0.5;

    private const double CompactDirectPsfDiameterSigmaRatio = 4.0;
    private const double PlateScaleProbeRadians = 1e-5;
    private const double ArcsecondsPerArcminute = 60d;
    private const double MaximumSiderealDegreesPerSecond = 0.0042;

    private readonly Dictionary<int, DeepSkyPixelRate> _rates;
    private readonly Dictionary<int, DeepSkyPixelRate> _temporalRates;
    private readonly LinearSceneRenderOptions _normalizedRenderOptions;

    private DeepSkyRenderPlan(
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        DateTimeOffset utc,
        ObserverLocation observer,
        LinearSceneRenderOptions normalizedRenderOptions,
        Dictionary<int, DeepSkyPixelRate> rates,
        Dictionary<int, DeepSkyPixelRate> temporalRates,
        double celestialDiffuseBackground,
        double sourceElectronRate,
        double visibleElectronRate,
        double retainedElectronRate,
        double maximumElectronRate,
        IReadOnlyList<DeepSkySourceDiagnostic> diagnostics,
        DeepSkyRenderStatistics statistics)
    {
        Projection = projection;
        Settings = settings;
        Utc = utc;
        Observer = observer;
        _normalizedRenderOptions = normalizedRenderOptions;
        _rates = rates;
        _temporalRates = temporalRates;
        CelestialDiffuseBackgroundElectronsPerSecond = celestialDiffuseBackground;
        SourceElectronRate = sourceElectronRate;
        VisibleElectronRate = visibleElectronRate;
        RetainedElectronRate = retainedElectronRate;
        MaximumElectronRate = maximumElectronRate;
        Diagnostics = diagnostics;
        Statistics = statistics;
    }

    public ProjectionContext Projection { get; }
    public DeepSkyRenderSettings Settings { get; }
    public DateTimeOffset Utc { get; }
    public ObserverLocation Observer { get; }
    public double CelestialDiffuseBackgroundElectronsPerSecond { get; }
    internal bool HasTemporalPointSources => _temporalRates.Count > 0;

    /// <summary>Integrated pre-clipping flux rate over all admitted luminous sources.</summary>
    public double SourceElectronRate { get; }

    /// <summary>Rate admitted by horizon policy and optical projection before PSF and sensor/aperture clipping.</summary>
    public double VisibleElectronRate { get; }

    /// <summary>Rate landing inside the sensor and aperture after PSF convolution and clipping. Lost light is never renormalized.</summary>
    public double RetainedElectronRate { get; }

    public double MaximumElectronRate { get; }
    public IReadOnlyList<DeepSkySourceDiagnostic> Diagnostics { get; }
    public DeepSkyRenderStatistics Statistics { get; }

    /// <summary>Returns incident electron rate at native pixel <c>(x, y)</c> for channel <c>-1</c> (mono), <c>0</c> (R), <c>1</c> (G), or <c>2</c> (B).</summary>
    public double ElectronRate(int x, int y, int channel = -1) =>
        PreCloudElectronRate(x, y, channel) + TemporalPointSourceElectronRate(x, y, channel);

    internal double PreCloudElectronRate(int x, int y, int channel = -1)
    {
        if (x < 0 || x >= Projection.WidthPixels || y < 0 || y >= Projection.HeightPixels)
        {
            return 0d;
        }
        var baseline = CelestialDiffuseBackgroundElectronsPerSecond;
        if (_rates.TryGetValue(y * Projection.WidthPixels + x, out var delta))
        {
            return Math.Max(0d, baseline + delta.Channel(channel));
        }
        return baseline;
    }

    internal double TemporalPointSourceElectronRate(int x, int y, int channel = -1)
    {
        if (_temporalRates.Count == 0 || x < 0 || x >= Projection.WidthPixels || y < 0 || y >= Projection.HeightPixels)
        {
            return 0d;
        }
        return _temporalRates.TryGetValue(y * Projection.WidthPixels + x, out var rate)
            ? Math.Max(0d, rate.Channel(channel))
            : 0d;
    }

    internal void AddPreCloudRates(double[] rates, ProjectionContext projection, int channel, double channelScale)
    {
        var baseline = CelestialDiffuseBackgroundElectronsPerSecond;
        if (baseline > 0)
        {
            var scaledBaseline = baseline * channelScale;
            for (var y = 0; y < projection.HeightPixels; y++)
            {
                var row = y * projection.WidthPixels;
                for (var x = 0; x < projection.WidthPixels; x++)
                {
                    if (projection.ContainsSample(x + 0.5, y + 0.5))
                    {
                        rates[row + x] += scaledBaseline;
                    }
                }
            }
            foreach (var (index, delta) in _rates)
            {
                var x = index % projection.WidthPixels;
                var y = index / projection.WidthPixels;
                if (!projection.ContainsSample(x + 0.5, y + 0.5)) continue;
                var total = Math.Max(0d, baseline + delta.Channel(channel)) * channelScale;
                rates[index] += total - scaledBaseline;
            }
        }
        else
        {
            foreach (var (index, delta) in _rates)
            {
                var value = delta.Channel(channel);
                if (value <= 0) continue;
                var x = index % projection.WidthPixels;
                var y = index / projection.WidthPixels;
                if (!projection.ContainsSample(x + 0.5, y + 0.5)) continue;
                rates[index] += value * channelScale;
            }
        }
    }

    internal void AddTemporalPointSourceRates(double[] rates, ProjectionContext projection, int channel, double channelScale)
    {
        foreach (var (index, rate) in _temporalRates)
        {
            var value = rate.Channel(channel);
            if (value <= 0) continue;
            var x = index % projection.WidthPixels;
            var y = index / projection.WidthPixels;
            if (!projection.ContainsSample(x + 0.5, y + 0.5)) continue;
            rates[index] += value * channelScale;
        }
    }

    internal void ValidateFor(VisibleScene scene, ImageLayout layout, LinearSceneRenderOptions options)
    {
        if (options is BayerRggb16RenderOptions && (Settings.CfaOriginX != 0 || Settings.CfaOriginY != 0))
        {
            throw new ArgumentException("The native RGGB renderer cannot publish a shifted CFA phase as RGGB.", nameof(options));
        }
        if (scene.Request.Projection != Projection ||
            scene.Request.Utc != Utc ||
            scene.Request.Observer != Observer ||
            scene.Request.HorizonPolicy != Settings.HorizonPolicy ||
            scene.Request.Refraction != Settings.Refraction ||
            layout.Width != Projection.WidthPixels ||
            layout.Height != Projection.HeightPixels ||
            Normalize(options) != _normalizedRenderOptions)
        {
            throw new ArgumentException("Deep-sky render plan does not match the scene and sensor options.", nameof(options));
        }
    }

    /// <summary>Deterministic candidate ranking policy applied before candidate-budget truncation.</summary>
    public static int CompareCandidatePriority(DeepSkyObject left, DeepSkyObject right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var messierComparison = (left.MessierNumber ?? int.MaxValue).CompareTo(right.MessierNumber ?? int.MaxValue);
        if (messierComparison != 0)
        {
            return messierComparison;
        }
        var magnitudeComparison = (left.VMagnitude ?? double.PositiveInfinity)
            .CompareTo(right.VMagnitude ?? double.PositiveInfinity);
        if (magnitudeComparison != 0)
        {
            return magnitudeComparison;
        }
        var sizeComparison = (right.MajorAxisArcminutes ?? 0d)
            .CompareTo(left.MajorAxisArcminutes ?? 0d);
        if (sizeComparison != 0)
        {
            return sizeComparison;
        }
        return StringComparer.Ordinal.Compare(left.Id, right.Id);
    }

    /// <summary>
    /// Computes the single canonical bounded candidate selection after merging any synthetic scenario additions,
    /// sorting by <see cref="CompareCandidatePriority"/>, and truncating to <paramref name="maximumCandidates"/>.
    /// </summary>
    public static DeepSkyObject[] SelectCandidates(
        IEnumerable<DeepSkyObject>? candidates,
        DeepSkySyntheticScenario? syntheticScenario,
        int maximumCandidates)
    {
        if (maximumCandidates is < 1 or > 2048)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCandidates));
        }

        var byId = new Dictionary<string, DeepSkyObject>(StringComparer.Ordinal);
        if (candidates is not null)
        {
            foreach (var item in candidates)
            {
                ArgumentNullException.ThrowIfNull(item);
                byId.TryAdd(item.Id, item);
            }
        }
        if (syntheticScenario?.AdditionalObjects is { Count: > 0 } synthetic)
        {
            foreach (var item in synthetic)
            {
                ArgumentNullException.ThrowIfNull(item);
                byId[item.Id] = item;
            }
        }
        var sorted = byId.Values.ToArray();
        Array.Sort(sorted, CompareCandidatePriority);
        return sorted.Length <= maximumCandidates ? sorted : sorted[..maximumCandidates];
    }

    /// <summary>Prepares a deterministic bounded deep-sky render plan for one capture geometry and sensor configuration.</summary>
    public static DeepSkyRenderPlan Prepare(
        VisibleSceneRequest sceneRequest,
        IDeepSkyCatalog? catalog,
        IEnumerable<DeepSkyObject>? explicitCandidates,
        IEnumerable<CelestialCatalogObject>? renderedHygStars,
        LinearSceneRenderOptions renderOptions,
        DeepSkyRenderSettings? settings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sceneRequest);
        ArgumentNullException.ThrowIfNull(renderOptions);
        renderOptions.Validate();
        settings ??= new DeepSkyRenderSettings(
            PsfSigmaPixels: renderOptions.PsfSigmaPixels,
            PsfRadiusPixels: renderOptions.PsfRadiusPixels,
            HorizonPolicy: sceneRequest.HorizonPolicy,
            Refraction: sceneRequest.Refraction);
        settings.Validate();
        if (settings.PsfSigmaPixels != renderOptions.PsfSigmaPixels ||
            settings.PsfRadiusPixels != renderOptions.PsfRadiusPixels ||
            settings.HorizonPolicy != sceneRequest.HorizonPolicy ||
            settings.Refraction != sceneRequest.Refraction)
        {
            throw new ArgumentException("Deep-sky render settings must bind the scene PSF, horizon policy, and refraction.", nameof(settings));
        }

        var exposure = TimeSpan.FromSeconds(renderOptions.ExposureSeconds);
        DateTimeOffset exposureStartUtc;
        if (settings.ExposureStartUtc is { } explicitStart)
        {
            if (sceneRequest.Utc != explicitStart.AddTicks(exposure.Ticks / 2))
            {
                throw new ArgumentException("Scene reference must be the exact floor midpoint of the UTC exposure.", nameof(settings));
            }
            exposureStartUtc = explicitStart;
        }
        else
        {
            exposureStartUtc = sceneRequest.Utc.AddTicks(-(exposure.Ticks / 2));
        }

        var projection = sceneRequest.Projection;
        var projector = new ResolvedSourceProjector(projection, settings.HorizonPolicy, settings.Refraction);
        var basis = CameraBasis.Create(
            projection.BoresightAltitudeDegrees,
            projection.BoresightAzimuthDegrees,
            projection.RollDegrees,
            projection.HorizontalFlip);
        var boresight = CameraBasis.FromHorizontal(new AltAzPoint(
            projection.BoresightAltitudeDegrees,
            projection.BoresightAzimuthDegrees));
        var opticalRadiusDegrees = ProjectionSamplingBounds.OpticalRadiusDegrees(projection);
        var exposureMarginDegrees = Math.Max(0.5, renderOptions.ExposureSeconds * MaximumSiderealDegreesPerSecond + 0.5);

        var overridesById = (settings.SyntheticScenario?.Overrides ?? [])
            .ToDictionary(static o => o.ObjectId, StringComparer.Ordinal);
        var candidates = CollectCandidates(catalog, explicitCandidates, settings);
        var outlinesById = BuildOutlineLookup(catalog, settings.SyntheticScenario, candidates, settings.PreferCatalogOutlines);
        var hygStars = BuildHygStarIndex(renderedHygStars, sceneRequest, renderOptions.MagnitudeZeroElectronsPerSecond);
        Dictionary<string, AdmittedStellarExposure>? admittedStellarById = null;
        if (renderOptions.StellarExposure is { } stellarExposure &&
            stellarExposure.Geometry.Request.Projection == projection &&
            stellarExposure.Geometry.Request.Utc == sceneRequest.Utc &&
            stellarExposure.Geometry.StartUtc == exposureStartUtc &&
            stellarExposure.Geometry.EndUtc == exposureStartUtc + exposure &&
            stellarExposure.Sources.Count > 0)
        {
            admittedStellarById = new Dictionary<string, AdmittedStellarExposure>(
                stellarExposure.Sources.Count, StringComparer.Ordinal);
            foreach (var admittedStar in stellarExposure.Sources)
            {
                admittedStellarById.TryAdd(admittedStar.Source.Source.Id, admittedStar);
            }
        }

        var padding = (int)Math.Ceiling(settings.PsfRadiusPixels);
        var paddedWidth = (long)projection.WidthPixels + 2 * padding;
        var paddedHeight = (long)projection.HeightPixels + 2 * padding;
        var kernel = SolarDiskRenderPlan.CreateKernel(settings.PsfSigmaPixels, settings.PsfRadiusPixels);

        var rates = new Dictionary<int, DeepSkyPixelRate>();
        var temporalRates = new Dictionary<int, DeepSkyPixelRate>();
        var renderedSparsePixels = new HashSet<int>();
        var prePsfSparsePixels = new HashSet<long>();
        var diffusePrePsf = new Dictionary<long, DeepSkyPixelRate>();
        var cumulativeTransmission = new Dictionary<long, double>();
        var diagnostics = new List<DeepSkySourceDiagnostic>(candidates.Length);
        var darkCandidates = new List<DeepSkyObject>();

        var provisionalLuminous = new List<ProvisionalLuminousEntry>();
        var overlapDiffusePreCloud = new Dictionary<int, DeepSkyPixelRate>();
        var overlapDiffuseTemporal = new Dictionary<int, DeepSkyPixelRate>();

        var declaredDiffuseBg = settings.CelestialDiffuseBackgroundElectronsPerSecond +
            (settings.SyntheticScenario?.CelestialDiffuseBackgroundElectronsPerSecond ?? 0d);

        var totalSourceRate = 0d;
        var totalVisibleRate = 0d;
        var totalSampledPoints = 0L;
        var totalKernelVisits = 0L;
        var totalOutlineEdgeVisits = 0L;
        var eligibleCount = 0;
        var admittedCount = 0;
        var unresolvedPsfCount = 0;
        var darkAttenuationCount = 0;

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            overridesById.TryGetValue(item.Id, out var syntheticOverride);
            var profileClass = syntheticOverride?.ProfileClass ?? DeepSkySourceProfileResolver.Classify(item.ObjectType);
            if (profileClass == DeepSkyProfileClass.DarkNebula)
            {
                darkCandidates.Add(item);
                continue;
            }
            if (profileClass == DeepSkyProfileClass.Unsupported)
            {
                diagnostics.Add(CreateSkipped(item, profileClass,
                    DeepSkyObjectTypes.IsStellar(item.ObjectType)
                        ? "annotation-only-stellar-row"
                        : "annotation-only-unsupported-class",
                    DeepSkyPhotometryBasis.None, DeepSkyGeometryBasis.None, null));
                continue;
            }

            var profile = DeepSkySourceProfileResolver.Resolve(item, syntheticOverride);
            var geometryInfo = ResolveGeometry(
                item, syntheticOverride, profileClass, sceneRequest, basis, boresight, opticalRadiusDegrees,
                exposureMarginDegrees, projector, outlinesById, settings, out var skipReason);
            if (skipReason is not null)
            {
                diagnostics.Add(CreateSkipped(item, profileClass, skipReason,
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            var (photometryBasis, sourceRate) = ResolvePhotometry(
                item, syntheticOverride, geometryInfo, renderOptions.MagnitudeZeroElectronsPerSecond, settings);
            if (photometryBasis == DeepSkyPhotometryBasis.None || sourceRate <= 0 || !double.IsFinite(sourceRate))
            {
                diagnostics.Add(CreateSkipped(item, profileClass, "annotation-only-missing-photometry",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            eligibleCount++;
            if (admittedCount >= settings.MaximumRenderedSources)
            {
                diagnostics.Add(CreateSkipped(item, profileClass, "budget-skipped",
                    photometryBasis, geometryInfo.Basis, profile));
                continue;
            }

            var isUnresolvedSize = geometryInfo.Basis == DeepSkyGeometryBasis.UnresolvedPointSource ||
                geometryInfo.MajorAxisPixels <= UnresolvedDiameterPsfSigmaRatio * settings.PsfSigmaPixels;
            var isPureDiffuseClass = profileClass is DeepSkyProfileClass.DiffuseNebula or DeepSkyProfileClass.SupernovaRemnant;
            var routesPrePsfToDiffuseMap = profileClass is DeepSkyProfileClass.DiffuseNebula
                or DeepSkyProfileClass.CompositeClusterNebula
                or DeepSkyProfileClass.SupernovaRemnant;

            var sourceDeposits = new Dictionary<long, double>();
            var nonDiffuseClippedRates = new Dictionary<int, double>();
            var nonDiffuseTempPostRates = new Dictionary<int, double>();
            var nonDiffuseTempPreRates = new Dictionary<int, double>();
            var diffuseTempPostRates = new Dictionary<int, double>();
            var diffuseTempPreRates = new Dictionary<int, double>();
            var unresolvedDiffuseSamples = new List<UnresolvedDiffuseSample>();
            var unresolvedDiffuseFluxRate = 0d;
            PixelPoint? temporalReferencePixel = null;
            var visibleRate = 0d;
            var sampledPoints = 0;
            var matchedHygCount = 0;
            var matchedHygFluxFraction = 0d;
            var unresolvedMembers = 0;
            var usedUnresolvedPsf = false;
            var effectiveSourceRate = sourceRate;

            if (profileClass == DeepSkyProfileClass.OpenCluster)
            {
                // Always deduct rendered HYG stars before deciding unresolved vs resolved member rendering.
                var (matchedCount, matchedFlux) = MatchHygStarsInFootprint(
                    hygStars, geometryInfo, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                matchedHygCount = matchedCount;
                matchedHygFluxFraction = sourceRate > 0 ? matchedFlux / sourceRate : 0d;
                var residualRate = Math.Max(0d, sourceRate - matchedFlux);
                effectiveSourceRate = residualRate;
                if (residualRate <= 0d)
                {
                    diagnostics.Add(new DeepSkySourceDiagnostic(
                        item.Id, item.DisplayName, item.ObjectType, profileClass,
                        Admitted: false, Reason: "annotation-only-hyg-flux-covered",
                        photometryBasis, geometryInfo.Basis,
                        profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                        profile.Spectral.Origin, profile.IsSimulationParameter,
                        sourceRate, 0d, 0d, 0d,
                        matchedHygCount, matchedHygFluxFraction, 0, 0d, default, 0, 0, false, null));
                    continue;
                }

                if (isUnresolvedSize)
                {
                    usedUnresolvedPsf = true;
                    visibleRate = RasterizeUnresolvedTemporal(
                        item, residualRate, sceneRequest, exposureStartUtc, exposure,
                        geometryInfo, projector, projection, renderOptions, settings,
                        nonDiffuseTempPostRates, nonDiffuseTempPreRates, unresolvedDiffuseSamples: null,
                        ref totalSampledPoints, ref totalKernelVisits, out sampledPoints, out temporalReferencePixel,
                        cancellationToken);
                }
                else
                {
                    unresolvedMembers = DepositUnresolvedClusterMembers(
                        item.Id, profile.MaximumUnresolvedClusterMembers, settings.MaximumSamplePointsPerSource,
                        residualRate, geometryInfo, projector,
                        nonDiffuseClippedRates, projection, settings,
                        ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits, out visibleRate);
                    sampledPoints = unresolvedMembers;
                }
            }
            else if (profileClass == DeepSkyProfileClass.CompositeClusterNebula)
            {
                var nebulaRate = sourceRate * profile.CompositeNebulaFluxFraction;
                var clusterBudget = sourceRate * (1d - profile.CompositeNebulaFluxFraction);
                var (matchedCount, matchedFlux) = MatchHygStarsInFootprint(
                    hygStars, geometryInfo, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                matchedHygCount = matchedCount;
                matchedHygFluxFraction = clusterBudget > 0 ? matchedFlux / clusterBudget : 0d;
                var residualClusterRate = Math.Max(0d, clusterBudget - matchedFlux);
                effectiveSourceRate = nebulaRate + residualClusterRate;
                if (effectiveSourceRate <= 0d)
                {
                    diagnostics.Add(new DeepSkySourceDiagnostic(
                        item.Id, item.DisplayName, item.ObjectType, profileClass,
                        Admitted: false, Reason: "annotation-only-hyg-flux-covered",
                        photometryBasis, geometryInfo.Basis,
                        profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                        profile.Spectral.Origin, profile.IsSimulationParameter,
                        sourceRate, 0d, 0d, 0d,
                        matchedHygCount, matchedHygFluxFraction, 0, 0d, default, 0, 0, false, null));
                    continue;
                }

                if (isUnresolvedSize)
                {
                    usedUnresolvedPsf = true;
                    unresolvedDiffuseFluxRate = nebulaRate;
                    var unitPostRates = new Dictionary<int, double>();
                    var unitPreRates = new Dictionary<int, double>();
                    var unitVisibleFraction = RasterizeUnresolvedTemporal(
                        item, 1d, sceneRequest, exposureStartUtc, exposure,
                        geometryInfo, projector, projection, renderOptions, settings,
                        unitPostRates, unitPreRates,
                        nebulaRate > 0 ? unresolvedDiffuseSamples : null,
                        ref totalSampledPoints, ref totalKernelVisits, out sampledPoints, out temporalReferencePixel,
                        cancellationToken);
                    if (nebulaRate > 0)
                    {
                        foreach (var (idx, val) in unitPostRates)
                        {
                            AddScalarRate(diffuseTempPostRates, idx, val * nebulaRate, settings.MaximumSparsePixels);
                        }
                        foreach (var (idx, val) in unitPreRates)
                        {
                            AddScalarRate(diffuseTempPreRates, idx, val * nebulaRate, settings.MaximumSparsePixels);
                        }
                        visibleRate += unitVisibleFraction * nebulaRate;
                    }
                    if (residualClusterRate > 0)
                    {
                        foreach (var (idx, val) in unitPostRates)
                        {
                            AddScalarRate(nonDiffuseTempPostRates, idx, val * residualClusterRate, settings.MaximumSparsePixels);
                        }
                        foreach (var (idx, val) in unitPreRates)
                        {
                            AddScalarRate(nonDiffuseTempPreRates, idx, val * residualClusterRate, settings.MaximumSparsePixels);
                        }
                        visibleRate += unitVisibleFraction * residualClusterRate;
                    }
                }
                else
                {
                    var nebulaSampleCap = settings.MaximumSamplePointsPerSource;
                    if (nebulaRate > 0 && residualClusterRate > 0)
                    {
                        var clusterReserve = Math.Min(
                            profile.MaximumUnresolvedClusterMembers,
                            Math.Max(1, settings.MaximumSamplePointsPerSource / 4));
                        nebulaSampleCap = Math.Max(1, settings.MaximumSamplePointsPerSource - clusterReserve);
                    }

                    if (nebulaRate > 0)
                    {
                        var nebVis = RasterizeExtendedProfile(
                            item.Id, profile, nebulaRate, geometryInfo, projector, projection,
                            settings, nebulaSampleCap, padding, paddedWidth, paddedHeight, sourceDeposits, nonDiffuseClippedRates,
                            ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits,
                            out var nebSamples, cancellationToken);
                        visibleRate += nebVis;
                        sampledPoints += nebSamples;
                    }
                    if (residualClusterRate > 0)
                    {
                        var remainingSourceSamples = Math.Max(0, settings.MaximumSamplePointsPerSource - sampledPoints);
                        unresolvedMembers = DepositUnresolvedClusterMembers(
                            item.Id, profile.MaximumUnresolvedClusterMembers, remainingSourceSamples,
                            residualClusterRate, geometryInfo, projector,
                            nonDiffuseClippedRates, projection, settings,
                            ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits, out var clVis);
                        visibleRate += clVis;
                        sampledPoints += unresolvedMembers;
                    }
                }
            }
            else if (isUnresolvedSize)
            {
                usedUnresolvedPsf = true;
                if (isPureDiffuseClass)
                {
                    unresolvedDiffuseFluxRate = sourceRate;
                    visibleRate = RasterizeUnresolvedTemporal(
                        item, sourceRate, sceneRequest, exposureStartUtc, exposure,
                        geometryInfo, projector, projection, renderOptions, settings,
                        diffuseTempPostRates, diffuseTempPreRates, unresolvedDiffuseSamples,
                        ref totalSampledPoints, ref totalKernelVisits, out sampledPoints, out temporalReferencePixel,
                        cancellationToken);
                }
                else
                {
                    visibleRate = RasterizeUnresolvedTemporal(
                        item, sourceRate, sceneRequest, exposureStartUtc, exposure,
                        geometryInfo, projector, projection, renderOptions, settings,
                        nonDiffuseTempPostRates, nonDiffuseTempPreRates, unresolvedDiffuseSamples: null,
                        ref totalSampledPoints, ref totalKernelVisits, out sampledPoints, out temporalReferencePixel,
                        cancellationToken);
                }
            }
            else
            {
                visibleRate = RasterizeExtendedProfile(
                    item.Id, profile, sourceRate, geometryInfo, projector, projection,
                    settings, settings.MaximumSamplePointsPerSource, padding, paddedWidth, paddedHeight,
                    sourceDeposits, nonDiffuseClippedRates,
                    ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits,
                    out sampledPoints, cancellationToken);
            }

            // Convolve any padded bilinear deposits with the configured PSF for admission, footprint, and
            // overlapping-diffuse S/N variance evaluation.
            var convolvedDepositsMono = new Dictionary<int, double>();
            if (sourceDeposits.Count > 0)
            {
                ConvolveToClipped(
                    sourceDeposits, convolvedDepositsMono, projection, kernel, padding, paddedWidth,
                    settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits, cancellationToken);
            }

            var preCloudClippedRates = new Dictionary<int, double>(nonDiffuseClippedRates);
            foreach (var (idx, val) in convolvedDepositsMono)
            {
                AddScalarRate(preCloudClippedRates, idx, val, settings.MaximumSparsePixels);
            }

            var combinedTempPostRates = MergeScalarRates(nonDiffuseTempPostRates, diffuseTempPostRates, settings.MaximumSparsePixels);
            var combinedTempPreRates = MergeScalarRates(nonDiffuseTempPreRates, diffuseTempPreRates, settings.MaximumSparsePixels);

            if (preCloudClippedRates.Count == 0 && combinedTempPostRates.Count == 0)
            {
                diagnostics.Add(new DeepSkySourceDiagnostic(
                    item.Id, item.DisplayName, item.ObjectType, profileClass,
                    Admitted: false, Reason: "annotation-only-outside-fov",
                    photometryBasis, geometryInfo.Basis,
                    profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                    profile.Spectral.Origin, profile.IsSimulationParameter,
                    effectiveSourceRate, visibleRate, 0d, 0d,
                    matchedHygCount, matchedHygFluxFraction, unresolvedMembers, 0d, default,
                    sampledPoints, 0, usedUnresolvedPsf, null));
                continue;
            }

            var (prediction, causesSaturation, footprint, retainedMonoRate, activePixelCount) = EvaluateSignalAndFootprint(
                item.Id, geometryInfo, temporalReferencePixel, profile.Spectral,
                preCloudClippedRates, combinedTempPostRates, combinedTempPreRates,
                declaredDiffuseBg, overlapDiffusePreCloud, overlapDiffuseTemporal,
                selfDiffusePreCloud: null, selfDiffuseTemporal: null,
                effectiveSourceRate, projection, renderOptions, settings, cancellationToken);

            var admitted = settings.MinimumSignalToNoise <= 0 ||
                prediction.SignalToNoise >= settings.MinimumSignalToNoise ||
                causesSaturation;
            if (!admitted)
            {
                diagnostics.Add(new DeepSkySourceDiagnostic(
                    item.Id, item.DisplayName, item.ObjectType, profileClass,
                    Admitted: false, Reason: "annotation-only-below-snr",
                    photometryBasis, geometryInfo.Basis,
                    profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                    profile.Spectral.Origin, profile.IsSimulationParameter,
                    effectiveSourceRate, visibleRate, retainedMonoRate,
                    effectiveSourceRate > 0 ? retainedMonoRate / effectiveSourceRate : 0d,
                    matchedHygCount, matchedHygFluxFraction, unresolvedMembers, 0d, prediction,
                    sampledPoints, activePixelCount, usedUnresolvedPsf, footprint));
                continue;
            }

            Dictionary<int, DeepSkyPixelRate>? ownDiffusePreCloud = null;
            Dictionary<int, DeepSkyPixelRate>? ownDiffuseTemporal = null;
            if (routesPrePsfToDiffuseMap && convolvedDepositsMono.Count > 0)
            {
                ownDiffusePreCloud = new Dictionary<int, DeepSkyPixelRate>(convolvedDepositsMono.Count);
                foreach (var (idx, monoRate) in convolvedDepositsMono)
                {
                    var scaled = ScaleRate(monoRate, profile.Spectral);
                    ownDiffusePreCloud[idx] = scaled;
                    AddUnboundedSparseRate(overlapDiffusePreCloud, idx, scaled);
                }
            }
            if (routesPrePsfToDiffuseMap && diffuseTempPostRates.Count > 0)
            {
                ownDiffuseTemporal = new Dictionary<int, DeepSkyPixelRate>(diffuseTempPostRates.Count);
                foreach (var (idx, monoRate) in diffuseTempPostRates)
                {
                    var scaled = ScaleRate(monoRate, profile.Spectral);
                    ownDiffuseTemporal[idx] = scaled;
                    AddUnboundedSparseRate(overlapDiffuseTemporal, idx, scaled);
                }
            }
            admittedCount++;
            provisionalLuminous.Add(new ProvisionalLuminousEntry(
                item,
                profileClass,
                photometryBasis,
                geometryInfo,
                profile,
                temporalReferencePixel,
                effectiveSourceRate,
                visibleRate,
                retainedMonoRate,
                matchedHygCount,
                matchedHygFluxFraction,
                unresolvedMembers,
                sampledPoints,
                activePixelCount,
                usedUnresolvedPsf,
                routesPrePsfToDiffuseMap,
                sourceDeposits,
                nonDiffuseClippedRates,
                preCloudClippedRates,
                nonDiffuseTempPostRates,
                diffuseTempPostRates,
                combinedTempPostRates,
                combinedTempPreRates,
                unresolvedDiffuseSamples,
                unresolvedDiffuseFluxRate,
                ownDiffusePreCloud,
                ownDiffuseTemporal,
                prediction,
                causesSaturation,
                footprint));

            // Immediately re-evaluate provisional luminous sources whenever newly added diffuse emission can
            // increase local aperture noise on earlier sources, so any demotion frees its render slot for
            // subsequent eligible candidates in deterministic priority order.
            if ((ownDiffusePreCloud is not null || ownDiffuseTemporal is not null) && admittedCount > 1)
            {
                ReevaluateProvisionalLuminous(
                    provisionalLuminous,
                    declaredDiffuseBg,
                    overlapDiffusePreCloud,
                    overlapDiffuseTemporal,
                    projection,
                    renderOptions,
                    settings,
                    ref admittedCount,
                    cancellationToken);
            }
        }

        // Commit surviving luminous sources into pre-PSF diffuse, direct pre-cloud, and temporal rate maps while
        // enforcing MaximumSparsePixels across the combined union of rendered pixels.
        var admittedUnresolvedDiffuse = new List<AdmittedUnresolvedDiffuseSource>();
        foreach (var entry in provisionalLuminous)
        {
            if (entry.Demoted)
            {
                diagnostics.Add(new DeepSkySourceDiagnostic(
                    entry.Item.Id, entry.Item.DisplayName, entry.Item.ObjectType, entry.ProfileClass,
                    Admitted: false, Reason: "annotation-only-below-snr",
                    entry.PhotometryBasis, entry.Geometry.Basis,
                    entry.Profile.ProfileSchemaVersion, entry.Profile.MorphologyModel, entry.Profile.Spectral.SpectralModel,
                    entry.Profile.Spectral.Origin, entry.Profile.IsSimulationParameter,
                    entry.EffectiveSourceRate, entry.VisibleRate, entry.RetainedMonoRate,
                    entry.EffectiveSourceRate > 0 ? entry.RetainedMonoRate / entry.EffectiveSourceRate : 0d,
                    entry.MatchedHygCount, entry.MatchedHygFluxFraction, entry.UnresolvedMembers, 0d, entry.Prediction,
                    entry.SampledPoints, entry.ActivePixelCount, entry.UsedUnresolvedPsf, entry.Footprint));
                continue;
            }

            if (entry.UsedUnresolvedPsf) unresolvedPsfCount++;
            totalSourceRate += entry.EffectiveSourceRate;
            totalVisibleRate += entry.VisibleRate;

            if (entry.RoutesPrePsfToDiffuseMap)
            {
                // Commit resolved pre-PSF diffuse emission so overlapping dark nebulae attenuate it multiplicatively
                // BEFORE final PSF convolution. Non-diffuse subcomponents (such as resolved Cl+N cluster members)
                // commit directly to the pre-cloud rate map.
                foreach (var (paddedKey, monoVal) in entry.SourceDeposits)
                {
                    AddCombinedPaddedRate(diffusePrePsf, prePsfSparsePixels, paddedKey,
                        ScaleRate(monoVal, entry.Profile.Spectral), settings.MaximumSparsePixels);
                }
                foreach (var (pixelIndex, monoRate) in entry.NonDiffuseClippedRates)
                {
                    AddCombinedSparseRate(rates, renderedSparsePixels, pixelIndex,
                        ScaleRate(monoRate, entry.Profile.Spectral), settings.MaximumSparsePixels);
                }
                if (entry.UnresolvedDiffuseSamples.Count > 0 && entry.UnresolvedDiffuseFluxRate > 0)
                {
                    admittedUnresolvedDiffuse.Add(new AdmittedUnresolvedDiffuseSource(
                        entry.Item.Id,
                        new EquatorialPoint(entry.Item.RightAscensionHours, entry.Item.DeclinationDegrees),
                        entry.Geometry.Frame.Center,
                        entry.UnresolvedDiffuseFluxRate,
                        entry.Profile.Spectral,
                        entry.UnresolvedDiffuseSamples,
                        entry.DiffuseTempPostRates));
                }
            }
            else
            {
                foreach (var (pixelIndex, monoRate) in entry.PreCloudClippedRates)
                {
                    AddCombinedSparseRate(rates, renderedSparsePixels, pixelIndex,
                        ScaleRate(monoRate, entry.Profile.Spectral), settings.MaximumSparsePixels);
                }
            }

            foreach (var (pixelIndex, monoRate) in entry.NonDiffuseTempPostRates)
            {
                AddCombinedSparseRate(temporalRates, renderedSparsePixels, pixelIndex,
                    ScaleRate(monoRate, entry.Profile.Spectral), settings.MaximumSparsePixels);
            }

            diagnostics.Add(new DeepSkySourceDiagnostic(
                entry.Item.Id, entry.Item.DisplayName, entry.Item.ObjectType, entry.ProfileClass,
                Admitted: true,
                Reason: entry.UsedUnresolvedPsf ? "admitted-unresolved-psf" : "admitted",
                entry.PhotometryBasis, entry.Geometry.Basis,
                entry.Profile.ProfileSchemaVersion, entry.Profile.MorphologyModel, entry.Profile.Spectral.SpectralModel,
                entry.Profile.Spectral.Origin, entry.Profile.IsSimulationParameter,
                entry.EffectiveSourceRate, entry.VisibleRate, entry.RetainedMonoRate,
                entry.EffectiveSourceRate > 0 ? entry.RetainedMonoRate / entry.EffectiveSourceRate : 0d,
                entry.MatchedHygCount, entry.MatchedHygFluxFraction, entry.UnresolvedMembers, 0d, entry.Prediction,
                entry.SampledPoints, entry.ActivePixelCount, entry.UsedUnresolvedPsf, entry.Footprint));
        }

        // Pass 2: Dark nebulae attenuate ONLY compatible celestial diffuse background (overlapping diffuse nebulae,
        // overlapping unresolved diffuse emitters, or explicitly declared celestial diffuse background), never foreground
        // terrestrial skyglow or foreground non-diffuse sources. Each dark cloud selects resolved vs fine quadrature
        // strictly from its own projected minor/major dimensions and the fixed scene-wide resolved subpixel density
        // (never mutating a neighbor's sampling grid). Cross-cloud overlap evaluation uses exact sky-cap intersection
        // (never wasting evaluations on sky-disjoint clouds), never mutates committed clouds before admission, rejects
        // solo-below-S/N candidates before any cross-cloud work, composes admitted layers in strict canonical order
        // independent of admission priority order while strictly enforcing MaximumSamplePointsPerSource (both caller
        // and target profile) and MaximumTotalSamplePoints, and caches/fairly budgets temporal attenuation across
        // overlapping unresolved diffuse emitters.
        var cumulativeAttenuatedConvolved = new Dictionary<int, DeepSkyPixelRate>();
        var cumulativeTemporalAttenuated = new Dictionary<int, DeepSkyPixelRate>();
        var unresolvedDarkCumulativeTau = new Dictionary<(int SourceIndex, int SampleIndex), double>();
        Dictionary<int, DeepSkyPixelRate>? foregroundHygTemporalRates = null;
        HashSet<string>? rasterizedForegroundStars = null;

        var hasCompatibleDiffuseBackground =
            declaredDiffuseBg > 0 || diffusePrePsf.Count > 0 || admittedUnresolvedDiffuse.Count > 0;
        var hasExtendedDiffuseBackground = declaredDiffuseBg > 0 || diffusePrePsf.Count > 0;
        var darkResolvedSub = Math.Clamp(settings.SamplesPerProjectedPixel * 2, 2, 8);
        var darkLayerState = new DarkAttenuationLayerState(darkResolvedSub);

        if (darkCandidates.Count > 0)
        {
            if (settings.MinimumSignalToNoise <= 0d)
            {
                EvaluateDarkCandidatesCanonical(
                    darkCandidates,
                    overridesById,
                    outlinesById,
                    hasCompatibleDiffuseBackground,
                    hasExtendedDiffuseBackground,
                    declaredDiffuseBg,
                    diffusePrePsf,
                    admittedUnresolvedDiffuse,
                    hygStars,
                    admittedStellarById,
                    overlapDiffusePreCloud,
                    overlapDiffuseTemporal,
                    rates,
                    temporalRates,
                    cumulativeAttenuatedConvolved,
                    cumulativeTemporalAttenuated,
                    unresolvedDarkCumulativeTau,
                    cumulativeTransmission,
                    prePsfSparsePixels,
                    darkLayerState,
                    sceneRequest,
                    exposureStartUtc,
                    exposure,
                    basis,
                    boresight,
                    opticalRadiusDegrees,
                    exposureMarginDegrees,
                    projector,
                    projection,
                    renderOptions,
                    settings,
                    padding,
                    paddedWidth,
                    kernel,
                    diagnostics,
                    ref foregroundHygTemporalRates,
                    ref rasterizedForegroundStars,
                    ref eligibleCount,
                    ref admittedCount,
                    ref darkAttenuationCount,
                    ref totalSampledPoints,
                    ref totalKernelVisits,
                    ref totalOutlineEdgeVisits,
                    cancellationToken);
            }
            else
            {
                EvaluateDarkCandidatesWithSnrGating(
                    darkCandidates,
                    overridesById,
                    outlinesById,
                    hasCompatibleDiffuseBackground,
                    hasExtendedDiffuseBackground,
                    declaredDiffuseBg,
                    diffusePrePsf,
                    admittedUnresolvedDiffuse,
                    hygStars,
                    admittedStellarById,
                    overlapDiffusePreCloud,
                    overlapDiffuseTemporal,
                    rates,
                    temporalRates,
                    cumulativeAttenuatedConvolved,
                    cumulativeTemporalAttenuated,
                    unresolvedDarkCumulativeTau,
                    cumulativeTransmission,
                    prePsfSparsePixels,
                    darkLayerState,
                    sceneRequest,
                    exposureStartUtc,
                    exposure,
                    basis,
                    boresight,
                    opticalRadiusDegrees,
                    exposureMarginDegrees,
                    projector,
                    projection,
                    renderOptions,
                    settings,
                    padding,
                    paddedWidth,
                    kernel,
                    diagnostics,
                    ref foregroundHygTemporalRates,
                    ref rasterizedForegroundStars,
                    ref eligibleCount,
                    ref admittedCount,
                    ref darkAttenuationCount,
                    ref totalSampledPoints,
                    ref totalKernelVisits,
                    ref totalOutlineEdgeVisits,
                    cancellationToken);
            }
        }

        // Convolve the combined post-attenuation diffuse emission and dark-nebula attenuation delta once with the PSF,
        // enforcing MaximumSparsePixels across the union of all rendered pixels (rates U temporalRates).
        if (diffusePrePsf.Count > 0 || cumulativeTransmission.Count > 0)
        {
            var diffuseKeys = new HashSet<long>(diffusePrePsf.Keys);
            diffuseKeys.UnionWith(cumulativeTransmission.Keys);
            foreach (var paddedKey in diffuseKeys.Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var emission = diffusePrePsf.GetValueOrDefault(paddedKey);
                var transmission = cumulativeTransmission.GetValueOrDefault(paddedKey, 1d);
                var bgLoss = declaredDiffuseBg * (1d - transmission);
                var netDelta = new DeepSkyPixelRate(
                    emission.Mono * transmission - bgLoss,
                    emission.Red * transmission - bgLoss,
                    emission.Green * transmission - bgLoss,
                    emission.Blue * transmission - bgLoss);
                if (netDelta.Mono == 0 && netDelta.Red == 0 && netDelta.Green == 0 && netDelta.Blue == 0)
                {
                    continue;
                }

                totalKernelVisits += kernel.Length;
                if (totalKernelVisits > settings.MaximumKernelCellVisits)
                {
                    throw new InvalidOperationException("Deep-sky PSF kernel budget exceeded.");
                }
                var sx = (int)(paddedKey % paddedWidth) - padding;
                var sy = (int)(paddedKey / paddedWidth) - padding;
                foreach (var (dx, dy, w) in kernel)
                {
                    var px = sx + dx;
                    var py = sy + dy;
                    if (px < 0 || px >= projection.WidthPixels || py < 0 || py >= projection.HeightPixels ||
                        !projection.ContainsSample(px + 0.5, py + 0.5))
                    {
                        continue;
                    }
                    var idx = py * projection.WidthPixels + px;
                    var scaled = new DeepSkyPixelRate(netDelta.Mono * w, netDelta.Red * w, netDelta.Green * w, netDelta.Blue * w);
                    AddCombinedSparseRate(rates, renderedSparsePixels, idx, scaled, settings.MaximumSparsePixels);
                }
            }
        }

        // Commit admitted unresolved diffuse emitters using exact subpixel PSF placement, applying any pre-PSF
        // dark-cloud attenuation exp(-sum_k tau_k) cleanly while enforcing the combined sparse pixel cap.
        for (var srcIdx = 0; srcIdx < admittedUnresolvedDiffuse.Count; srcIdx++)
        {
            var uSrc = admittedUnresolvedDiffuse[srcIdx];
            var anyDarkAttenuation = false;
            for (var sampIdx = 0; sampIdx < uSrc.Samples.Count; sampIdx++)
            {
                if (unresolvedDarkCumulativeTau.GetValueOrDefault((srcIdx, sampIdx), 0d) > 0d)
                {
                    anyDarkAttenuation = true;
                    break;
                }
            }
            if (!anyDarkAttenuation)
            {
                foreach (var (pixelIndex, monoRate) in uSrc.UnattenuatedPostCloudRates)
                {
                    AddCombinedSparseRate(temporalRates, renderedSparsePixels, pixelIndex,
                        ScaleRate(monoRate, uSrc.Spectral), settings.MaximumSparsePixels);
                }
            }
            else
            {
                var attenuatedSamples = new WeightedPointSpreadSample[uSrc.Samples.Count];
                for (var sampIdx = 0; sampIdx < uSrc.Samples.Count; sampIdx++)
                {
                    var sample = uSrc.Samples[sampIdx];
                    var darkTau = unresolvedDarkCumulativeTau.GetValueOrDefault((srcIdx, sampIdx), 0d);
                    var darkT = darkTau > 0d ? Math.Exp(-darkTau) : 1d;
                    attenuatedSamples[sampIdx] = new WeightedPointSpreadSample(
                        sample.Pixel, sample.ExposureFraction * darkT, sample.CloudTransmission);
                }
                var attenuatedPostMono = new Dictionary<int, double>();
                var attenuatedPreMono = new Dictionary<int, double>();
                RasterizeWeightedSamplesWithPsf(
                    attenuatedSamples, uSrc.FluxRate, projection, settings,
                    attenuatedPostMono, attenuatedPreMono, ref totalKernelVisits, cancellationToken);
                foreach (var (pixelIndex, monoRate) in attenuatedPostMono)
                {
                    AddCombinedSparseRate(temporalRates, renderedSparsePixels, pixelIndex,
                        ScaleRate(monoRate, uSrc.Spectral), settings.MaximumSparsePixels);
                }
            }
        }

        diagnostics.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.ObjectId, right.ObjectId));
        var frozenDiagnostics = new ReadOnlyCollection<DeepSkySourceDiagnostic>(diagnostics);

        var retainedTotal = 0d;
        var maxRate = declaredDiffuseBg;
        foreach (var idx in renderedSparsePixels)
        {
            var pre = rates.GetValueOrDefault(idx);
            var tmp = temporalRates.GetValueOrDefault(idx);
            var mono = Math.Max(0d, declaredDiffuseBg + pre.Mono) + Math.Max(0d, tmp.Mono);
            var peak = Math.Max(mono, Math.Max(
                Math.Max(0d, declaredDiffuseBg + pre.Red) + Math.Max(0d, tmp.Red),
                Math.Max(
                    Math.Max(0d, declaredDiffuseBg + pre.Green) + Math.Max(0d, tmp.Green),
                    Math.Max(0d, declaredDiffuseBg + pre.Blue) + Math.Max(0d, tmp.Blue))));
            retainedTotal += Math.Max(0d, pre.Mono) + Math.Max(0d, tmp.Mono);
            if (peak > maxRate) maxRate = peak;
        }

        var diagnosticsSha256 = ComputeDiagnosticsSha256(frozenDiagnostics, cancellationToken);
        var statistics = new DeepSkyRenderStatistics(
            candidates.Length,
            eligibleCount,
            admittedCount,
            candidates.Length - admittedCount,
            unresolvedPsfCount,
            darkAttenuationCount,
            totalSampledPoints,
            renderedSparsePixels.Count,
            totalKernelVisits,
            diagnosticsSha256);

        return new DeepSkyRenderPlan(
            projection, settings, sceneRequest.Utc, sceneRequest.Observer, Normalize(renderOptions),
            rates, temporalRates, declaredDiffuseBg, totalSourceRate, totalVisibleRate, retainedTotal, maxRate,
            frozenDiagnostics, statistics);
    }

    public static string ComputeDiagnosticsSha256(
        IReadOnlyList<DeepSkySourceDiagnostic> diagnostics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(DiagnosticIdentityVersion + "\n"));
        foreach (var item in diagnostics)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonical = CaptureContractJson.Canonicalize(CaptureContractJson.SerializeToElement(item));
            hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(canonical));
            hash.AppendData("\n"u8);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static DeepSkyObject[] CollectCandidates(
        IDeepSkyCatalog? catalog,
        IEnumerable<DeepSkyObject>? explicitCandidates,
        DeepSkyRenderSettings settings)
        => SelectCandidates(explicitCandidates ?? catalog?.Objects, settings.SyntheticScenario, settings.MaximumCandidates);

    private static List<HygStarEntry> BuildHygStarIndex(
        IEnumerable<CelestialCatalogObject>? stars,
        VisibleSceneRequest sceneRequest,
        double magnitudeZeroElectronsPerSecond)
    {
        var list = new List<HygStarEntry>();
        if (stars is null) return list;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var star in stars)
        {
            if (star is null || !seen.Add(star.Id) || !double.IsFinite(star.Magnitude)) continue;
            var j2000 = new EquatorialPoint(star.RightAscensionHours, star.DeclinationDegrees);
            var horizontal = CoordinateTransforms.EquatorialToHorizontal(
                EquatorialPrecession.PrecessJ2000(j2000, sceneRequest.Utc),
                sceneRequest.Utc,
                sceneRequest.Observer.LatitudeDegrees,
                sceneRequest.Observer.LongitudeDegrees);
            var fluxRate = Mono16SceneRenderer.RelativeFlux(star.Magnitude) * magnitudeZeroElectronsPerSecond;
            list.Add(new HygStarEntry(star.Id, star, CameraBasis.FromHorizontal(horizontal), fluxRate));
        }
        return list;
    }

    private static ResolvedSourceGeometry ResolveGeometry(
        DeepSkyObject item,
        DeepSkySyntheticSourceOverride? syntheticOverride,
        DeepSkyProfileClass profileClass,
        VisibleSceneRequest request,
        CameraBasis basis,
        EnuVector boresight,
        double opticalRadiusDegrees,
        double exposureMarginDegrees,
        ResolvedSourceProjector projector,
        Dictionary<string, IReadOnlyList<DeepSkyOutline>> syntheticOutlines,
        DeepSkyRenderSettings settings,
        out string? skipReason)
    {
        skipReason = null;
        var j2000 = new EquatorialPoint(item.RightAscensionHours, item.DeclinationDegrees);
        var geometric = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(j2000, request.Utc),
            request.Utc,
            request.Observer.LatitudeDegrees,
            request.Observer.LongitudeDegrees);
        var frame = ResolvedSourceFrame.Create(geometric, request.Observer.LatitudeDegrees);
        PixelPoint? centerPixel = projector.TryProject(frame.Center, out var cp) ? cp : null;

        var majorArcmin = syntheticOverride?.MajorAxisArcminutes ?? item.MajorAxisArcminutes;
        var minorArcmin = syntheticOverride?.MinorAxisArcminutes ?? item.MinorAxisArcminutes;
        var paJ2000 = syntheticOverride?.PositionAngleDegrees ?? item.PositionAngleDegrees;
        var hasSyntheticGeom = syntheticOverride?.MajorAxisArcminutes is not null ||
            syntheticOverride?.MinorAxisArcminutes is not null ||
            syntheticOverride?.PositionAngleDegrees is not null;

        if (hasSyntheticGeom)
        {
            if (majorArcmin is { } mMaj && (!double.IsFinite(mMaj) || mMaj is <= 0 or > 1200))
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "Merged synthetic deep-sky major axis is out of range.");
            }
            if (minorArcmin is { } mMin && (!double.IsFinite(mMin) || mMin is <= 0 or > 1200))
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "Merged synthetic deep-sky minor axis is out of range.");
            }
            if (majorArcmin is { } majCheck && minorArcmin is { } minCheck && minCheck > majCheck)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "Merged synthetic deep-sky minor axis cannot exceed major axis.");
            }
            if (paJ2000 is { } paCheck && (!double.IsFinite(paCheck) || paCheck is < 0 or >= 180))
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "Merged synthetic deep-sky position angle is out of range.");
            }
        }

        // Check sourced outlines for eligible classes first when preferred.
        IReadOnlyList<PixelPoint[]>? tangentRings = null;
        double outlineMinU = 0, outlineMaxU = 0, outlineMinV = 0, outlineMaxV = 0;
        var outlineReachRadians = 0d;
        var hadAntipodalOutline = false;
        var supportsOutline = profileClass is DeepSkyProfileClass.DiffuseNebula or DeepSkyProfileClass.PlanetaryNebula
            or DeepSkyProfileClass.SupernovaRemnant or DeepSkyProfileClass.CompositeClusterNebula
            or DeepSkyProfileClass.DarkNebula;

        if (settings.PreferCatalogOutlines && supportsOutline)
        {
            var outline = SelectOutline(item.Id, settings.PreferredOutlineLevel, syntheticOutlines);
            if (outline is not null)
            {
                if (DeepSkyOutlineSampler.TryProjectToGnomonicTangentPlane(
                    outline, frame, request.Utc, request.Observer.LatitudeDegrees, request.Observer.LongitudeDegrees,
                    out var rings, out var status))
                {
                    tangentRings = rings;
                    ComputeTangentBounds(rings, out outlineMinU, out outlineMaxU, out outlineMinV, out outlineMaxV, out outlineReachRadians);
                }
                else if (status == DeepSkyOutlineSampleStatus.InvalidAntipodalEdge)
                {
                    hadAntipodalOutline = true;
                }
            }
        }

        // Measure local plate scale around center if projectable.
        var pixelsPerRadian = MeasureLocalPixelsPerRadian(projector, frame);

        if (tangentRings is not null && outlineReachRadians > 0)
        {
            var reachDegrees = outlineReachRadians * 180d / Math.PI;
            if (!request.Refraction.Enabled &&
                Math.Acos(Math.Clamp(EnuVector.Dot(boresight, frame.Center), -1d, 1d)) * 180d / Math.PI >
                opticalRadiusDegrees + reachDegrees + exposureMarginDegrees)
            {
                skipReason = "annotation-only-outside-fov";
                return new ResolvedSourceGeometry(DeepSkyGeometryBasis.CatalogOutline, frame, geometric, centerPixel,
                    outlineReachRadians, outlineReachRadians, 0, frame.CelestialNorth, frame.CelestialEast,
                    0, 0, tangentRings, outlineMinU, outlineMaxU, outlineMinV, outlineMaxV);
            }
            var scale = pixelsPerRadian ?? ProjectionSamplingBounds.MaximumPixelsPerRadian(request.Projection, geometric, outlineReachRadians);
            var majorPx = Math.Max(outlineMaxU - outlineMinU, outlineMaxV - outlineMinV) * scale;
            var minorPx = Math.Min(outlineMaxU - outlineMinU, outlineMaxV - outlineMinV) * scale;
            return new ResolvedSourceGeometry(
                DeepSkyGeometryBasis.CatalogOutline, frame, geometric, centerPixel,
                outlineReachRadians, outlineReachRadians, 0, frame.CelestialNorth, frame.CelestialEast,
                majorPx, minorPx, tangentRings, outlineMinU, outlineMaxU, outlineMinV, outlineMaxV);
        }

        if (majorArcmin is null || majorArcmin <= 0 || majorArcmin > 1200)
        {
            skipReason = hadAntipodalOutline ? "annotation-only-invalid-antipodal-outline" : "annotation-only-missing-geometry";
            return new ResolvedSourceGeometry(DeepSkyGeometryBasis.None, frame, geometric, centerPixel,
                0, 0, 0, frame.CelestialNorth, frame.CelestialEast, 0, 0, null, 0, 0, 0, 0);
        }

        var semiMajorRad = majorArcmin.Value / 120d * Math.PI / 180d;
        var semiMajorDeg = majorArcmin.Value / 120d;
        var estimatedScale = pixelsPerRadian ?? ProjectionSamplingBounds.MaximumPixelsPerRadian(request.Projection, geometric, semiMajorRad);
        var estimatedMajorPx = 2d * semiMajorRad * estimatedScale;

        // If minor axis is absent, ONLY allow unresolved point-source convergence; never fabricate an axis ratio for resolved sources.
        if (minorArcmin is null)
        {
            if (profileClass != DeepSkyProfileClass.DarkNebula &&
                estimatedMajorPx > 0 && estimatedMajorPx <= UnresolvedDiameterPsfSigmaRatio * settings.PsfSigmaPixels)
            {
                return new ResolvedSourceGeometry(
                    DeepSkyGeometryBasis.UnresolvedPointSource, frame, geometric, centerPixel,
                    semiMajorRad, semiMajorRad, 0, frame.CelestialNorth, frame.CelestialEast,
                    estimatedMajorPx, estimatedMajorPx, null, 0, 0, 0, 0);
            }
            skipReason = hadAntipodalOutline ? "annotation-only-invalid-antipodal-outline" : "annotation-only-missing-geometry";
            return new ResolvedSourceGeometry(DeepSkyGeometryBasis.None, frame, geometric, centerPixel,
                0, 0, 0, frame.CelestialNorth, frame.CelestialEast, 0, 0, null, 0, 0, 0, 0);
        }

        var isCircle = Math.Abs(majorArcmin.Value - minorArcmin.Value) <= 1e-9;
        if (!isCircle && paJ2000 is null)
        {
            // Unequal axes without orientation cannot fabricate PA=0 unless strictly unresolved.
            if (profileClass != DeepSkyProfileClass.DarkNebula &&
                estimatedMajorPx > 0 && estimatedMajorPx <= UnresolvedDiameterPsfSigmaRatio * settings.PsfSigmaPixels)
            {
                return new ResolvedSourceGeometry(
                    DeepSkyGeometryBasis.UnresolvedPointSource, frame, geometric, centerPixel,
                    semiMajorRad, semiMajorRad, 0, frame.CelestialNorth, frame.CelestialEast,
                    estimatedMajorPx, estimatedMajorPx, null, 0, 0, 0, 0);
            }
            skipReason = hadAntipodalOutline ? "annotation-only-invalid-antipodal-outline" : "annotation-only-missing-geometry";
            return new ResolvedSourceGeometry(DeepSkyGeometryBasis.None, frame, geometric, centerPixel,
                0, 0, 0, frame.CelestialNorth, frame.CelestialEast, 0, 0, null, 0, 0, 0, 0);
        }

        if (!request.Refraction.Enabled &&
            Math.Acos(Math.Clamp(EnuVector.Dot(boresight, frame.Center), -1d, 1d)) * 180d / Math.PI >
            opticalRadiusDegrees + semiMajorDeg + exposureMarginDegrees)
        {
            skipReason = "annotation-only-outside-fov";
            return new ResolvedSourceGeometry(
                hasSyntheticGeom ? DeepSkyGeometryBasis.SyntheticScenario : (isCircle ? DeepSkyGeometryBasis.CatalogCircle : DeepSkyGeometryBasis.CatalogOrientedEllipse),
                frame, geometric, centerPixel, semiMajorRad, minorArcmin.Value / 120d * Math.PI / 180d, 0,
                frame.CelestialNorth, frame.CelestialEast, 0, 0, null, 0, 0, 0, 0);
        }

        var semiMinorRad = minorArcmin.Value / 120d * Math.PI / 180d;
        var paOfDate = isCircle
            ? 0d
            : DeepSkyOutlineSampler.PositionAngleOfDate(item.RightAscensionHours, item.DeclinationDegrees, paJ2000!.Value, request.Utc);
        var paRad = paOfDate * Math.PI / 180d;
        var majorAxis = frame.CelestialNorth * Math.Cos(paRad) + frame.CelestialEast * Math.Sin(paRad);
        var minorAxis = frame.CelestialEast * Math.Cos(paRad) - frame.CelestialNorth * Math.Sin(paRad);
        var basisKind = hasSyntheticGeom
            ? DeepSkyGeometryBasis.SyntheticScenario
            : isCircle ? DeepSkyGeometryBasis.CatalogCircle : DeepSkyGeometryBasis.CatalogOrientedEllipse;

        return new ResolvedSourceGeometry(
            basisKind, frame, geometric, centerPixel,
            semiMajorRad, semiMinorRad, paOfDate, majorAxis, minorAxis,
            2d * semiMajorRad * estimatedScale, 2d * semiMinorRad * estimatedScale,
            null, 0, 0, 0, 0);
    }

    private static Dictionary<string, IReadOnlyList<DeepSkyOutline>> BuildOutlineLookup(
        IDeepSkyCatalog? catalog,
        DeepSkySyntheticScenario? scenario,
        IEnumerable<DeepSkyObject> candidates,
        bool preferCatalogOutlines)
    {
        var map = new Dictionary<string, IReadOnlyList<DeepSkyOutline>>(StringComparer.Ordinal);
        if (!preferCatalogOutlines)
        {
            return map;
        }
        if (scenario?.AdditionalOutlines is { Count: > 0 } syntheticOutlines)
        {
            foreach (var group in syntheticOutlines.GroupBy(static o => o.ObjectId, StringComparer.Ordinal))
            {
                map[group.Key] = group.OrderBy(static o => o.Level).ToArray();
            }
        }
        if (catalog is not null)
        {
            foreach (var item in candidates)
            {
                if (map.ContainsKey(item.Id)) continue;
                var catalogOutlines = catalog.GetOutlines(item.Id);
                if (catalogOutlines.Count > 0)
                {
                    map[item.Id] = catalogOutlines;
                }
            }
        }
        return map;
    }

    private static DeepSkyOutline? SelectOutline(
        string objectId,
        int preferredLevel,
        Dictionary<string, IReadOnlyList<DeepSkyOutline>> outlines)
    {
        if (!outlines.TryGetValue(objectId, out var available) || available.Count == 0) return null;
        foreach (var item in available)
        {
            if (item.Level == preferredLevel) return item;
        }
        return available[0];
    }

    private static (DeepSkyPhotometryBasis Basis, double SourceElectronRate) ResolvePhotometry(
        DeepSkyObject item,
        DeepSkySyntheticSourceOverride? syntheticOverride,
        ResolvedSourceGeometry geometry,
        double magnitudeZeroElectronsPerSecond,
        DeepSkyRenderSettings settings)
    {
        if (syntheticOverride?.VisualMagnitude is { } syntheticV)
        {
            if (!double.IsFinite(syntheticV) || syntheticV is < -30 or > 30)
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "Merged synthetic deep-sky visual magnitude is out of range.");
            }
            return (DeepSkyPhotometryBasis.SyntheticScenario,
                Mono16SceneRenderer.RelativeFlux(syntheticV) * magnitudeZeroElectronsPerSecond);
        }
        if (item.VMagnitude is { } vMag)
        {
            return (DeepSkyPhotometryBasis.CatalogVisualMagnitude,
                Mono16SceneRenderer.RelativeFlux(vMag) * magnitudeZeroElectronsPerSecond);
        }
        if (settings.AllowSurfaceBrightnessB25Fallback &&
            item.SurfaceBrightness is { } sbB25 &&
            item.MajorAxisArcminutes is { } maj && item.MinorAxisArcminutes is { } min)
        {
            // Mean B surface brightness in mag/arcsec^2 within the elliptical area pi * a * b (arcsec^2).
            var areaArcsec2 = Math.PI * (maj * ArcsecondsPerArcminute / 2d) * (min * ArcsecondsPerArcminute / 2d);
            if (areaArcsec2 > 0 && double.IsFinite(areaArcsec2))
            {
                var rate = magnitudeZeroElectronsPerSecond * areaArcsec2 * Mono16SceneRenderer.RelativeFlux(sbB25);
                return (DeepSkyPhotometryBasis.CatalogSurfaceBrightnessB25, rate);
            }
        }
        return (DeepSkyPhotometryBasis.None, 0d);
    }

    private static double RasterizeUnresolvedTemporal(
        DeepSkyObject item,
        double fluxRate,
        VisibleSceneRequest sceneRequest,
        DateTimeOffset exposureStartUtc,
        TimeSpan exposure,
        ResolvedSourceGeometry geometry,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        LinearSceneRenderOptions renderOptions,
        DeepSkyRenderSettings settings,
        Dictionary<int, double> temporalPostCloudRates,
        Dictionary<int, double> temporalPreCloudRates,
        List<UnresolvedDiffuseSample>? unresolvedDiffuseSamples,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        out int sampledPoints,
        out PixelPoint? referencePixel,
        CancellationToken cancellationToken)
    {
        referencePixel = geometry.CenterPixel;
        if (exposure > TimeSpan.Zero && !sceneRequest.Refraction.Enabled)
        {
            var syntheticStar = new CelestialCatalogObject(
                item.Id, item.DisplayName, item.RightAscensionHours, item.DeclinationDegrees, 0d, 0d);
            var singleCatalog = new InMemoryCelestialCatalog([syntheticStar]);
            var tempRequest = new VisibleSceneRequest(
                sceneRequest.Utc,
                sceneRequest.Observer,
                sceneRequest.Projection,
                new CatalogQuery(1d, 1),
                sceneRequest.CatalogMetadata,
                sceneRequest.Refraction,
                sceneRequest.HorizonPolicy,
                projectionVersion: sceneRequest.ProjectionVersion);
            var maxSamples = Math.Min(settings.MaximumTemporalSamples, Math.Min(64, settings.MaximumSamplePointsPerSource));
            var maxStep = settings.MaximumTemporalStepPixels;
            if (settings.PsfRadiusPixels >= 0.75 && settings.PsfSigmaPixels <= settings.PsfRadiusPixels / 4d)
            {
                maxStep = Math.Min(maxStep, TemporalPointSpreadRaster.MaximumTemporalStepPixels(settings.PsfSigmaPixels, settings.PsfRadiusPixels));
            }
            var minSamples = Math.Min(settings.MinimumTemporalSamples, maxSamples);
            var geomOptions = new StellarExposureGeometryOptions(
                MaximumCandidates: 1,
                MaximumSamplesPerSource: maxSamples,
                MaximumStepPixels: maxStep,
                PsfSupportRadiusPixels: settings.PsfRadiusPixels,
                MinimumSamplesPerSource: minSamples);
            var temporalGeometryTask = new StellarExposureGeometryBuilder(singleCatalog)
                .BuildAsync(tempRequest, exposureStartUtc, exposure, geomOptions, cancellationToken);
            var temporalGeometry = temporalGeometryTask.IsCompleted
                ? temporalGeometryTask.Result
                : temporalGeometryTask.AsTask().GetAwaiter().GetResult();
            if (temporalGeometry.Sources.Count == 0)
            {
                sampledPoints = 1;
                totalSampledPoints += 1;
                if (totalSampledPoints > settings.MaximumTotalSamplePoints)
                {
                    throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
                }
                return 0d;
            }

            var temporalSource = temporalGeometry.Sources[0];
            referencePixel = temporalSource.MidpointReference?.Pixel ?? temporalSource.Samples[0].Pixel;
            sampledPoints = temporalSource.Samples.Count;
            totalSampledPoints += sampledPoints;
            if (totalSampledPoints > settings.MaximumTotalSamplePoints)
            {
                throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
            }

            var weightedSamples = new WeightedPointSpreadSample[temporalSource.Samples.Count];
            var visibleExposureFraction = 0d;
            for (var i = 0; i < temporalSource.Samples.Count; i++)
            {
                var sample = temporalSource.Samples[i];
                visibleExposureFraction += sample.ExposureFraction;
                var transmission = renderOptions.Cloud?.RequiresEvaluation == true
                    ? renderOptions.Cloud.Field.Evaluate(
                        sample.Horizontal,
                        renderOptions.Cloud.IntegrationStartUtc + (sample.Utc - exposureStartUtc)).Transmission
                    : 1d;
                weightedSamples[i] = new WeightedPointSpreadSample(sample.Pixel, sample.ExposureFraction, transmission);
                unresolvedDiffuseSamples?.Add(new UnresolvedDiffuseSample(
                    CameraBasis.FromHorizontal(sample.Horizontal),
                    sample.Pixel,
                    sample.ExposureFraction,
                    transmission,
                    sample.Utc));
            }

            RasterizeWeightedSamplesWithPsf(
                weightedSamples, fluxRate, projection, settings,
                temporalPostCloudRates, temporalPreCloudRates, ref totalKernelVisits, cancellationToken);
            return fluxRate * visibleExposureFraction;
        }

        // Instantaneous fallback (zero-second exposure or refracted scene where StellarExposureGeometryBuilder does not apply).
        sampledPoints = 1;
        totalSampledPoints += 1;
        if (totalSampledPoints > settings.MaximumTotalSamplePoints)
        {
            throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
        }
        if (!projector.TryProject(geometry.Frame.Center, out var centerPixel))
        {
            return 0d;
        }
        referencePixel = centerPixel;

        var apparent = geometry.GeometricCenter with
        {
            AltitudeDegrees = AtmosphericRefraction.Apply(geometry.GeometricCenter.AltitudeDegrees, sceneRequest.Refraction)
        };
        var cloudTransmission = renderOptions.Cloud?.RequiresEvaluation == true
            ? renderOptions.Cloud.Field.Evaluate(apparent, renderOptions.Cloud.IntegrationStartUtc).Transmission
            : 1d;
        unresolvedDiffuseSamples?.Add(new UnresolvedDiffuseSample(
            geometry.Frame.Center,
            centerPixel,
            1d,
            cloudTransmission,
            sceneRequest.Utc));
        DepositDirectSubpixelPsf(
            temporalPostCloudRates, projection, centerPixel, fluxRate * cloudTransmission,
            settings.PsfSigmaPixels, settings.PsfRadiusPixels,
            settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
        DepositDirectSubpixelPsf(
            temporalPreCloudRates, projection, centerPixel, fluxRate,
            settings.PsfSigmaPixels, settings.PsfRadiusPixels,
            settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
        return fluxRate;
    }

    private static void RasterizeWeightedSamplesWithPsf(
        WeightedPointSpreadSample[] weightedSamples,
        double fluxRate,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        Dictionary<int, double> postCloudRates,
        Dictionary<int, double> preCloudRates,
        ref long totalKernelVisits,
        CancellationToken cancellationToken)
    {
        if (weightedSamples.Length == 0 || fluxRate <= 0)
        {
            return;
        }
        if (settings.PsfRadiusPixels >= 0.75 &&
            weightedSamples.Length <= 64 &&
            (long)projection.WidthPixels * projection.HeightPixels <= 16_777_216)
        {
            var remainingVisits = settings.MaximumKernelCellVisits - totalKernelVisits;
            if (remainingVisits < 1)
            {
                throw new InvalidOperationException("Deep-sky PSF kernel budget exceeded.");
            }
            var raster = TemporalPointSpreadRaster.Rasterize(
                weightedSamples,
                projection,
                settings.PsfSigmaPixels,
                settings.PsfRadiusPixels,
                Math.Min(remainingVisits, 1_000_000_000L),
                Math.Min(settings.MaximumSparsePixels, 65_536),
                cancellationToken);
            totalKernelVisits += raster.KernelCellVisits;
            foreach (var pixel in raster.Pixels)
            {
                AddScalarRate(postCloudRates, pixel.Index, fluxRate * pixel.AfterTransmission, settings.MaximumSparsePixels);
                AddScalarRate(preCloudRates, pixel.Index, fluxRate * pixel.BeforeTransmission, settings.MaximumSparsePixels);
            }
            return;
        }

        foreach (var sample in weightedSamples)
        {
            DepositDirectSubpixelPsf(
                postCloudRates, projection, sample.Pixel,
                fluxRate * sample.ExposureFraction * sample.Transmission,
                settings.PsfSigmaPixels, settings.PsfRadiusPixels,
                settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
            DepositDirectSubpixelPsf(
                preCloudRates, projection, sample.Pixel,
                fluxRate * sample.ExposureFraction,
                settings.PsfSigmaPixels, settings.PsfRadiusPixels,
                settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
        }
    }

    private static double RasterizeExtendedProfile(
        string objectId,
        DeepSkySimulationParameters profile,
        double sourceRate,
        ResolvedSourceGeometry geometry,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        int maxSourceSamples,
        int padding,
        long paddedWidth,
        long paddedHeight,
        Dictionary<long, double> paddedDeposits,
        Dictionary<int, double> directClippedRates,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        ref long totalOutlineEdgeVisits,
        out int sampledPoints,
        CancellationToken cancellationToken)
    {
        var sourceHash = DeepSkySourceProfileResolver.ComputeStableObjectHash(objectId);
        var useDirectSubpixelPsf = geometry.MajorAxisPixels <= CompactDirectPsfDiameterSigmaRatio * settings.PsfSigmaPixels &&
            profile.ProfileClass is not (DeepSkyProfileClass.DiffuseNebula or DeepSkyProfileClass.CompositeClusterNebula or DeepSkyProfileClass.SupernovaRemnant);

        var samples = new List<(EnuVector Direction, double Weight)>();
        var totalWeight = 0d;

        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            var spanU = geometry.OutlineMaxU - geometry.OutlineMinU;
            var spanV = geometry.OutlineMaxV - geometry.OutlineMinV;
            var angularScale = ProjectionSamplingBounds.MaximumPixelsPerRadian(
                projection, geometry.GeometricCenter, Math.Max(1e-6, geometry.SemiMajorRadians));
            var maxAxis = Math.Max(1, (int)Math.Floor(Math.Sqrt(maxSourceSamples)));
            var minGrid = Math.Min(useDirectSubpixelPsf ? 10 : 20, maxAxis);
            var nu = (int)Math.Clamp(Math.Ceiling(spanU * angularScale * settings.SamplesPerProjectedPixel), minGrid, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(spanV * angularScale * settings.SamplesPerProjectedPixel), minGrid, maxAxis);
            ClampGridToBudget(ref nu, ref nv, maxSourceSamples);

            var edgeCount = CountOutlineEdges(rings);
            ChargeOutlineEdgeVisits((long)nu * nv * edgeCount, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);

            var du = spanU / nu;
            var dv = spanV / nv;
            var cellArea = du * dv;
            var maxInteriorDist = 0d;
            var rawInterior = new List<(double U, double V, double EdgeDist, double CenterDist)>();
            var maxCenterDist = Math.Max(1e-9, Math.Sqrt(
                Math.Max(geometry.OutlineMinU * geometry.OutlineMinU, geometry.OutlineMaxU * geometry.OutlineMaxU) +
                Math.Max(geometry.OutlineMinV * geometry.OutlineMinV, geometry.OutlineMaxV * geometry.OutlineMaxV)));

            for (var iy = 0; iy < nv; iy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var v = geometry.OutlineMinV + (iy + 0.5) * dv;
                for (var ix = 0; ix < nu; ix++)
                {
                    var u = geometry.OutlineMinU + (ix + 0.5) * du;
                    if (!DeepSkyOutlineSampler.ContainsTangentPoint(rings, u, v)) continue;
                    ChargeOutlineEdgeVisits(edgeCount, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                    var dist = DeepSkyOutlineSampler.DistanceToTangentBoundary(rings, u, v);
                    if (dist > maxInteriorDist) maxInteriorDist = dist;
                    rawInterior.Add((u, v, dist, Math.Sqrt(u * u + v * v)));
                }
            }
            if (rawInterior.Count == 0 || maxInteriorDist <= 0)
            {
                sampledPoints = 0;
                return 0d;
            }
            foreach (var (u, v, dist, centerDist) in rawInterior)
            {
                var density = DeepSkySourceProfileResolver.EvaluateOutlineDensity(
                    profile, dist / maxInteriorDist, centerDist / maxCenterDist);
                if (density <= 0) continue;
                // Exact gnomonic solid-angle Jacobian: dOmega = (1 + u^2 + v^2)^(-3/2) du dv.
                var rSq = u * u + v * v;
                var jacobian = Math.Pow(1d + rSq, -1.5);
                var weight = density * jacobian * cellArea;
                if (weight <= 0) continue;
                var direction = (geometry.Frame.Center + geometry.Frame.CelestialEast * u + geometry.Frame.CelestialNorth * v).Normalize();
                samples.Add((direction, weight));
                totalWeight += weight;
            }
        }
        else
        {
            var angularScale = ProjectionSamplingBounds.MaximumPixelsPerRadian(
                projection, geometry.GeometricCenter, Math.Max(1e-6, geometry.SemiMajorRadians));
            var budgetMaxAxis = Math.Max(1, (int)Math.Floor(Math.Sqrt(maxSourceSamples)));
            var maxAxis = useDirectSubpixelPsf ? Math.Min(16, budgetMaxAxis) : budgetMaxAxis;
            var minAxis = Math.Min(useDirectSubpixelPsf ? 8 : 20, maxAxis);
            var nu = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMajorRadians * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMinorRadians * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            ClampGridToBudget(ref nu, ref nv, maxSourceSamples);

            var du = 2d / nu;
            var dv = 2d / nv;
            var cellArea = du * dv * geometry.SemiMajorRadians * geometry.SemiMinorRadians;

            for (var iy = 0; iy < nv; iy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var v = -1d + (iy + 0.5) * dv;
                for (var ix = 0; ix < nu; ix++)
                {
                    var u = -1d + (ix + 0.5) * du;
                    var qSq = u * u + v * v;
                    if (qSq >= 1d) continue;
                    var q = Math.Sqrt(qSq);
                    var angle = Math.Atan2(v, u);
                    var density = DeepSkySourceProfileResolver.EvaluateRadialDensity(profile, q, angle, sourceHash);
                    if (density <= 0) continue;
                    var tangent = geometry.MajorAxis * (u * geometry.SemiMajorRadians) +
                        geometry.MinorAxis * (v * geometry.SemiMinorRadians);
                    var r = tangent.Length;
                    // Exact exponential-map solid-angle Jacobian: sin(r)/r.
                    var jacobian = r < 1e-8 ? 1d : Math.Sin(r) / r;
                    var weight = density * jacobian * cellArea;
                    if (weight <= 0) continue;
                    samples.Add((geometry.Frame.Direction(tangent), weight));
                    totalWeight += weight;
                }
            }
        }

        sampledPoints = samples.Count;
        totalSampledPoints += sampledPoints;
        if (totalSampledPoints > settings.MaximumTotalSamplePoints)
        {
            throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
        }
        if (samples.Count == 0 || totalWeight <= 0)
        {
            return 0d;
        }

        // Normalize strictly over the FULL angular profile BEFORE horizon, projection, PSF, or sensor clipping.
        var visibleRate = 0d;
        var directPsf = useDirectSubpixelPsf && samples.Count <= 256;
        foreach (var (direction, weight) in samples)
        {
            var sampleFlux = sourceRate * (weight / totalWeight);
            if (!projector.TryProject(direction, out var pixel)) continue;
            visibleRate += sampleFlux;
            if (directPsf)
            {
                DepositDirectSubpixelPsf(
                    directClippedRates, projection, pixel, sampleFlux,
                    settings.PsfSigmaPixels, settings.PsfRadiusPixels,
                    settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
            }
            else
            {
                DepositBilinear(
                    paddedDeposits, pixel, sampleFlux, projection.WidthPixels, projection.HeightPixels,
                    padding, paddedWidth, settings.MaximumSparsePixels);
            }
        }
        return visibleRate;
    }

    private static int DepositUnresolvedClusterMembers(
        string objectId,
        int maxMembers,
        int maxSourceSamples,
        double residualRate,
        ResolvedSourceGeometry geometry,
        ResolvedSourceProjector projector,
        Dictionary<int, double> directClippedRates,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        ref long totalOutlineEdgeVisits,
        out double visibleRate)
    {
        var effectiveMaxMembers = Math.Min(maxMembers, maxSourceSamples);
        if (effectiveMaxMembers <= 0)
        {
            visibleRate = 0d;
            return 0;
        }
        var minMembers = Math.Min(4, effectiveMaxMembers);
        var memberCount = Math.Clamp(
            (int)Math.Round(Math.Sqrt(Math.Max(16d, geometry.MajorAxisPixels * Math.Max(1d, geometry.MinorAxisPixels)))),
            minMembers, effectiveMaxMembers);
        var state = DeepSkySourceProfileResolver.ComputeStableObjectHash(objectId + ":unresolved-members");
        var members = new List<(EnuVector Direction, double Weight)>(memberCount);
        var totalWeight = 0d;

        for (var index = 0; index < memberCount; index++)
        {
            // Deterministic identity per unresolved member: "{objectId}:member:{index:D3}".
            var memberHash = DeepSkySourceProfileResolver.ComputeStableObjectHash($"{objectId}:member:{index:D3}");
            var direction = SampleMemberDirection(
                geometry, memberHash, ref state, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            // Bounded Salpeter-like deterministic flux weights normalized strictly to residualRate.
            var u = NextUnit(ref memberHash);
            var weight = Math.Pow(0.2 + 0.8 * u, 2.2);
            members.Add((direction, weight));
            totalWeight += weight;
        }

        totalSampledPoints += members.Count;
        if (totalSampledPoints > settings.MaximumTotalSamplePoints)
        {
            throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
        }

        visibleRate = 0d;
        foreach (var (direction, weight) in members)
        {
            var flux = residualRate * (weight / totalWeight);
            if (!projector.TryProject(direction, out var pixel)) continue;
            visibleRate += flux;
            DepositDirectSubpixelPsf(
                directClippedRates, projection, pixel, flux,
                settings.PsfSigmaPixels, settings.PsfRadiusPixels,
                settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
        }
        return members.Count;
    }

    private static EnuVector SampleMemberDirection(
        ResolvedSourceGeometry geometry,
        ulong seed,
        ref ulong state,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        var local = seed ^ state;
        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            var edgeCount = CountOutlineEdges(rings);
            for (var attempt = 0; attempt < 64; attempt++)
            {
                ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                var u = geometry.OutlineMinU + NextUnit(ref local) * (geometry.OutlineMaxU - geometry.OutlineMinU);
                var v = geometry.OutlineMinV + NextUnit(ref local) * (geometry.OutlineMaxV - geometry.OutlineMinV);
                if (DeepSkyOutlineSampler.ContainsTangentPoint(rings, u, v))
                {
                    state = local;
                    return (geometry.Frame.Center + geometry.Frame.CelestialEast * u + geometry.Frame.CelestialNorth * v).Normalize();
                }
            }
            state = local;
            return geometry.Frame.Center;
        }

        // Concentrated radial distribution inside the cluster ellipse (King/Plummer-like concentration).
        var radius = Math.Pow(NextUnit(ref local), 0.75) * 0.92;
        var angle = NextUnit(ref local) * 2d * Math.PI;
        state = local;
        var tangent = geometry.MajorAxis * (radius * Math.Cos(angle) * geometry.SemiMajorRadians) +
            geometry.MinorAxis * (radius * Math.Sin(angle) * geometry.SemiMinorRadians);
        return geometry.Frame.Direction(tangent);
    }

    private static void ReevaluateProvisionalLuminous(
        List<ProvisionalLuminousEntry> provisionalLuminous,
        double declaredDiffuseBg,
        Dictionary<int, DeepSkyPixelRate> overlapDiffusePreCloud,
        Dictionary<int, DeepSkyPixelRate> overlapDiffuseTemporal,
        ProjectionContext projection,
        LinearSceneRenderOptions renderOptions,
        DeepSkyRenderSettings settings,
        ref int admittedCount,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var lowestPriorityFailingIndex = -1;
            for (var i = 0; i < provisionalLuminous.Count; i++)
            {
                var entry = provisionalLuminous[i];
                if (entry.Demoted) continue;
                var (updatedPrediction, updatedSat, _, _, _) = EvaluateSignalAndFootprint(
                    entry.Item.Id, entry.Geometry, entry.TemporalReferencePixel, entry.Profile.Spectral,
                    entry.PreCloudClippedRates, entry.CombinedTempPostRates, entry.CombinedTempPreRates,
                    declaredDiffuseBg, overlapDiffusePreCloud, overlapDiffuseTemporal,
                    entry.OwnDiffusePreCloud, entry.OwnDiffuseTemporal,
                    entry.EffectiveSourceRate, projection, renderOptions, settings, cancellationToken);
                entry.Prediction = updatedPrediction;
                entry.CausesSaturation = updatedSat;
                if (settings.MinimumSignalToNoise > 0 &&
                    updatedPrediction.SignalToNoise < settings.MinimumSignalToNoise &&
                    !updatedSat)
                {
                    lowestPriorityFailingIndex = i;
                }
            }
            if (lowestPriorityFailingIndex < 0) break;

            var demoted = provisionalLuminous[lowestPriorityFailingIndex];
            demoted.Demoted = true;
            admittedCount--;
            if (demoted.OwnDiffusePreCloud is not null)
            {
                SubtractUnboundedSparseRates(overlapDiffusePreCloud, demoted.OwnDiffusePreCloud);
            }
            if (demoted.OwnDiffuseTemporal is not null)
            {
                SubtractUnboundedSparseRates(overlapDiffuseTemporal, demoted.OwnDiffuseTemporal);
            }
        }
    }

    private const int OutlineDistanceGridSize = 25;

    private static OutlineOrientedFrame ComputeOutlineOrientedFrame(
        IReadOnlyList<PixelPoint[]> rings,
        double minU,
        double maxU,
        double minV,
        double maxV,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var axisAligned = new OutlineOrientedFrame(0d, 0d, 1d, 0d, 0d, 1d, minU, maxU, minV, maxV);
        var edgeCount = CountOutlineEdges(rings);
        if (edgeCount <= 0) return axisAligned;
        ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);

        var points = new List<PixelPoint>((int)Math.Min(edgeCount + 1, 16_384));
        for (var r = 0; r < rings.Count; r++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ring = rings[r];
            var count = ring.Length > 1 && ring[0] == ring[^1] ? ring.Length - 1 : ring.Length;
            for (var i = 0; i < count; i++)
            {
                points.Add(ring[i]);
            }
        }
        if (points.Count < 3) return axisAligned;

        points.Sort(static (a, b) =>
        {
            var cmp = a.X.CompareTo(b.X);
            return cmp != 0 ? cmp : a.Y.CompareTo(b.Y);
        });

        var unique = new List<PixelPoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            if (i == 0 || points[i] != points[i - 1]) unique.Add(points[i]);
        }
        if (unique.Count < 3) return axisAligned;

        static double Cross(PixelPoint o, PixelPoint a, PixelPoint b) =>
            (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        var hull = new List<PixelPoint>(unique.Count * 2);
        for (var i = 0; i < unique.Count; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], unique[i]) <= 0d)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(unique[i]);
        }
        var lowerCount = hull.Count + 1;
        for (var i = unique.Count - 2; i >= 0; i--)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            while (hull.Count >= lowerCount && Cross(hull[^2], hull[^1], unique[i]) <= 0d)
            {
                hull.RemoveAt(hull.Count - 1);
            }
            hull.Add(unique[i]);
        }
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
        var h = hull.Count;
        if (h < 3) return axisAligned;

        static double Dot(PixelPoint p, double ax, double ay) => p.X * ax + p.Y * ay;
        static int NextIndex(int index, int count) => index + 1 == count ? 0 : index + 1;

        var bestFrame = axisAligned;
        var bestArea = Math.Max(0d, (maxU - minU) * (maxV - minV));

        // O(H) rotating-calipers minimum-area oriented bounding box over the strictly convex CCW hull.
        var kRight = 1;
        var kTop = 1;
        var kLeft = 1;
        var initialized = false;

        for (var i = 0; i < h; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var a = hull[i];
            var b = hull[NextIndex(i, h)];
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = double.Hypot(dx, dy);
            if (len <= 1e-15) continue;

            var axisSU = dx / len;
            var axisSV = dy / len;
            var axisTU = -axisSV;
            var axisTV = axisSU;

            if (!initialized)
            {
                kRight = NextIndex(i, h);
                var rightVal = Dot(hull[kRight], axisSU, axisSV);
                for (var step = 0; step < h; step++)
                {
                    var next = NextIndex(kRight, h);
                    var nextVal = Dot(hull[next], axisSU, axisSV);
                    if (nextVal > rightVal)
                    {
                        kRight = next;
                        rightVal = nextVal;
                    }
                    else
                    {
                        break;
                    }
                }

                kTop = kRight;
                var topVal = Dot(hull[kTop], axisTU, axisTV);
                for (var step = 0; step < h; step++)
                {
                    var next = NextIndex(kTop, h);
                    var nextVal = Dot(hull[next], axisTU, axisTV);
                    if (nextVal > topVal)
                    {
                        kTop = next;
                        topVal = nextVal;
                    }
                    else
                    {
                        break;
                    }
                }

                kLeft = kTop;
                var leftVal = Dot(hull[kLeft], axisSU, axisSV);
                for (var step = 0; step < h; step++)
                {
                    var next = NextIndex(kLeft, h);
                    var nextVal = Dot(hull[next], axisSU, axisSV);
                    if (nextVal < leftVal)
                    {
                        kLeft = next;
                        leftVal = nextVal;
                    }
                    else
                    {
                        break;
                    }
                }
                initialized = true;
            }
            else
            {
                var rightVal = Dot(hull[kRight], axisSU, axisSV);
                for (var step = 0; step < h; step++)
                {
                    var next = NextIndex(kRight, h);
                    var nextVal = Dot(hull[next], axisSU, axisSV);
                    if (nextVal > rightVal)
                    {
                        kRight = next;
                        rightVal = nextVal;
                    }
                    else
                    {
                        break;
                    }
                }

                var topVal = Dot(hull[kTop], axisTU, axisTV);
                for (var step = 0; step < h; step++)
                {
                    var next = NextIndex(kTop, h);
                    var nextVal = Dot(hull[next], axisTU, axisTV);
                    if (nextVal > topVal)
                    {
                        kTop = next;
                        topVal = nextVal;
                    }
                    else
                    {
                        break;
                    }
                }

                var leftVal = Dot(hull[kLeft], axisSU, axisSV);
                for (var step = 0; step < h; step++)
                {
                    var next = NextIndex(kLeft, h);
                    var nextVal = Dot(hull[next], axisSU, axisSV);
                    if (nextVal < leftVal)
                    {
                        kLeft = next;
                        leftVal = nextVal;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            var sMin = Math.Min(Dot(hull[kLeft], axisSU, axisSV), Math.Min(Dot(a, axisSU, axisSV), Dot(b, axisSU, axisSV)));
            var sMax = Math.Max(Dot(hull[kRight], axisSU, axisSV), Math.Max(Dot(a, axisSU, axisSV), Dot(b, axisSU, axisSV)));
            var tMin = Math.Min(Dot(a, axisTU, axisTV), Dot(b, axisTU, axisTV));
            var tMax = Math.Max(Dot(hull[kTop], axisTU, axisTV), tMin);

            var spanS = sMax - sMin;
            var spanT = tMax - tMin;
            var area = spanS * spanT;
            if (area >= bestArea * (1d - 1e-12)) continue;

            if (spanS < spanT)
            {
                // Rotate +90 deg so AxisS is always the major axis while keeping (AxisS, AxisT) right-handed.
                var newAxisSU = axisTU;
                var newAxisSV = axisTV;
                var newAxisTU = -axisSU;
                var newAxisTV = -axisSV;
                var newSMin = tMin;
                var newSMax = tMax;
                var newTMin = -sMax;
                var newTMax = -sMin;
                axisSU = newAxisSU;
                axisSV = newAxisSV;
                axisTU = newAxisTU;
                axisTV = newAxisTV;
                sMin = newSMin;
                sMax = newSMax;
                tMin = newTMin;
                tMax = newTMax;
            }

            bestArea = area;
            bestFrame = new OutlineOrientedFrame(0d, 0d, axisSU, axisSV, axisTU, axisTV, sMin, sMax, tMin, tMax);
        }

        return bestFrame;
    }

    private static double ComputeMaxOutlineDistance(
        ResolvedSourceGeometry geometry,
        OutlineOrientedFrame? outlineFrame,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        if (geometry.TangentRings is not { Count: > 0 } rings)
        {
            return 0d;
        }
        var edgeCount = CountOutlineEdges(rings);
        var gridVisits = (long)OutlineDistanceGridSize * OutlineDistanceGridSize * edgeCount;
        // Enforce the remaining outline-edge budget BEFORE performing any oriented-box or grid probing work.
        if (gridVisits < 0 || gridVisits > maximumOutlineEdgeVisits - totalOutlineEdgeVisits)
        {
            throw new InvalidOperationException("Deep-sky outline edge-visit budget exceeded.");
        }
        var frame = outlineFrame ?? ComputeOutlineOrientedFrame(
            rings, geometry.OutlineMinU, geometry.OutlineMaxU, geometry.OutlineMinV, geometry.OutlineMaxV,
            maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits, cancellationToken);
        var spanS = frame.SpanS;
        var spanT = frame.SpanT;
        if (spanS <= 0 || spanT <= 0) return 0d;

        const int grid = OutlineDistanceGridSize;
        ChargeOutlineEdgeVisits(gridVisits, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
        var ds = spanS / grid;
        var dt = spanT / grid;
        var maxDist = 0d;
        for (var iy = 0; iy < grid; iy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var t = frame.MinT + (iy + 0.5) * dt;
            for (var ix = 0; ix < grid; ix++)
            {
                var s = frame.MinS + (ix + 0.5) * ds;
                var (u, v) = frame.ToTangent(s, t);
                if (!DeepSkyOutlineSampler.ContainsTangentPoint(rings, u, v)) continue;
                ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                var dist = DeepSkyOutlineSampler.DistanceToTangentBoundary(rings, u, v);
                if (dist > maxDist) maxDist = dist;
            }
        }
        return maxDist;
    }

    private static PaddedPixelBounds? ComputeDarkCloudPaddedBounds(
        ResolvedSourceGeometry geometry,
        ResolvedSourceProjector projector,
        CameraBasis basis,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        int padding,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        var minPx = double.PositiveInfinity;
        var maxPx = double.NegativeInfinity;
        var minPy = double.PositiveInfinity;
        var maxPy = double.NegativeInfinity;
        var anyProjected = false;

        void Include(PixelPoint p)
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y)) return;
            if (p.X < minPx) minPx = p.X;
            if (p.X > maxPx) maxPx = p.X;
            if (p.Y < minPy) minPy = p.Y;
            if (p.Y > maxPy) maxPy = p.Y;
            anyProjected = true;
        }

        if (geometry.CenterPixel is { } cp)
        {
            Include(cp);
        }

        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            var edgeCount = CountOutlineEdges(rings);
            ChargeOutlineEdgeVisits(edgeCount, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            foreach (var ring in rings)
            {
                for (var i = 0; i < ring.Length; i++)
                {
                    if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    var p = ring[i];
                    var dir = (geometry.Frame.Center + geometry.Frame.CelestialEast * p.X + geometry.Frame.CelestialNorth * p.Y).Normalize();
                    if (projector.TryProject(dir, out var px)) Include(px);
                    if (i + 1 < ring.Length)
                    {
                        var q = ring[i + 1];
                        var midDir = (geometry.Frame.Center +
                            geometry.Frame.CelestialEast * (0.5 * (p.X + q.X)) +
                            geometry.Frame.CelestialNorth * (0.5 * (p.Y + q.Y))).Normalize();
                        if (projector.TryProject(midDir, out var mpx)) Include(mpx);
                    }
                }
            }
            var spanU = geometry.OutlineMaxU - geometry.OutlineMinU;
            var spanV = geometry.OutlineMaxV - geometry.OutlineMinV;
            for (var iy = 0; iy < 8; iy++)
            {
                var v = geometry.OutlineMinV + (iy + 0.5) / 8d * spanV;
                for (var ix = 0; ix < 8; ix++)
                {
                    var u = geometry.OutlineMinU + (ix + 0.5) / 8d * spanU;
                    var dir = (geometry.Frame.Center + geometry.Frame.CelestialEast * u + geometry.Frame.CelestialNorth * v).Normalize();
                    if (projector.TryProject(dir, out var px)) Include(px);
                }
            }
        }
        else if (geometry.SemiMajorRadians > 0 && geometry.SemiMinorRadians > 0)
        {
            const int perimeterSamples = 64;
            for (var i = 0; i < perimeterSamples; i++)
            {
                var angle = 2d * Math.PI * i / perimeterSamples;
                var tangent = geometry.MajorAxis * (Math.Cos(angle) * geometry.SemiMajorRadians) +
                    geometry.MinorAxis * (Math.Sin(angle) * geometry.SemiMinorRadians);
                if (projector.TryProject(geometry.Frame.Direction(tangent), out var px)) Include(px);
            }
            for (var iy = 0; iy < 8; iy++)
            {
                var v = -1d + (iy + 0.5) * 0.25;
                for (var ix = 0; ix < 8; ix++)
                {
                    var u = -1d + (ix + 0.5) * 0.25;
                    if (u * u + v * v >= 1d) continue;
                    var tangent = geometry.MajorAxis * (u * geometry.SemiMajorRadians) +
                        geometry.MinorAxis * (v * geometry.SemiMinorRadians);
                    if (projector.TryProject(geometry.Frame.Direction(tangent), out var px)) Include(px);
                }
            }
        }

        // Check padded sensor corners and center so a huge dark cloud enclosing the entire sensor is never missed.
        foreach (var probe in new[]
        {
            new PixelPoint(-padding, -padding),
            new PixelPoint(projection.WidthPixels + padding, -padding),
            new PixelPoint(-padding, projection.HeightPixels + padding),
            new PixelPoint(projection.WidthPixels + padding, projection.HeightPixels + padding),
            new PixelPoint(projection.WidthPixels * 0.5, projection.HeightPixels * 0.5)
        })
        {
            if (TryUnprojectPadded(probe.X, probe.Y, projection, basis, settings.HorizonPolicy, settings.Refraction, out var dir) &&
                DarkCloudContainsDirection(geometry, dir, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits))
            {
                Include(probe);
            }
        }

        if (!anyProjected) return null;
        var minBoundX = -padding;
        var maxBoundX = projection.WidthPixels + padding - 1;
        var minBoundY = -padding;
        var maxBoundY = projection.HeightPixels + padding - 1;
        var minX = (int)Math.Floor(minPx) - 1;
        var maxX = (int)Math.Floor(maxPx) + 1;
        var minY = (int)Math.Floor(minPy) - 1;
        var maxY = (int)Math.Floor(maxPy) + 1;
        if (maxX < minBoundX || minX > maxBoundX || maxY < minBoundY || minY > maxBoundY)
        {
            return null;
        }
        return new PaddedPixelBounds(
            Math.Clamp(minX, minBoundX, maxBoundX),
            Math.Clamp(minY, minBoundY, maxBoundY),
            Math.Clamp(maxX, minBoundX, maxBoundX),
            Math.Clamp(maxY, minBoundY, maxBoundY));
    }

    private static bool DarkCloudContainsDirection(
        ResolvedSourceGeometry geometry,
        EnuVector direction,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            var cosine = EnuVector.Dot(direction, geometry.Frame.Center);
            if (cosine <= DeepSkyOutlineSampler.MinimumTangentCosine) return false;
            var u = EnuVector.Dot(direction, geometry.Frame.CelestialEast) / cosine;
            var v = EnuVector.Dot(direction, geometry.Frame.CelestialNorth) / cosine;
            if (u < geometry.OutlineMinU || u > geometry.OutlineMaxU ||
                v < geometry.OutlineMinV || v > geometry.OutlineMaxV)
            {
                return false;
            }
            ChargeOutlineEdgeVisits(CountOutlineEdges(rings), maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            return DeepSkyOutlineSampler.ContainsTangentPoint(rings, u, v);
        }

        if (geometry.SemiMajorRadians <= 0 || geometry.SemiMinorRadians <= 0) return false;
        var sinMajor = EnuVector.Dot(direction, geometry.MajorAxis);
        var sinMinor = EnuVector.Dot(direction, geometry.MinorAxis);
        var sinAngle = double.Hypot(sinMajor, sinMinor);
        var cosAngle = EnuVector.Dot(direction, geometry.Frame.Center);
        var angle = Math.Atan2(sinAngle, cosAngle);
        if (angle > geometry.SemiMajorRadians * (1d + 1e-9)) return false;
        var scale = sinAngle > 1e-15 ? angle / sinAngle : 1d;
        var eu = (sinMajor * scale) / geometry.SemiMajorRadians;
        var ev = (sinMinor * scale) / geometry.SemiMinorRadians;
        return eu * eu + ev * ev < 1d;
    }

    private static long PaddedBoxArea(PaddedPixelBounds bounds)
        => (long)(bounds.MaxX - bounds.MinX + 1) * (bounds.MaxY - bounds.MinY + 1);

    private static bool DarkCloudsMayOverlapOnSky(
        ResolvedSourceGeometry leftGeometry,
        ResolvedSourceGeometry rightGeometry)
    {
        var sumR = leftGeometry.SemiMajorRadians + rightGeometry.SemiMajorRadians;
        if (sumR <= 0d) return false;
        var sep = AngularSeparationRadians(leftGeometry.Frame.Center, rightGeometry.Frame.Center);
        // Unit-vector cross products carry absolute rounding error, even at small separations.
        // Treat uncertain boundary contact as possible overlap so no positive overlap is discarded.
        const double angularRoundoffRadians = 8d * 2.220446049250313e-16;
        return sep <= sumR + angularRoundoffRadians;
    }

    private static bool DarkCloudsMayOverlap(
        PaddedPixelBounds? leftBounds,
        ResolvedSourceGeometry leftGeometry,
        PaddedPixelBounds? rightBounds,
        ResolvedSourceGeometry rightGeometry)
    {
        if (!DarkCloudsMayOverlapOnSky(leftGeometry, rightGeometry))
        {
            return false;
        }
        if (leftBounds is { } lb && rightBounds is { } rb)
        {
            return lb.MinX <= rb.MaxX && lb.MaxX >= rb.MinX &&
                lb.MinY <= rb.MaxY && lb.MaxY >= rb.MinY;
        }
        return true;
    }

    private static bool DarkCloudsMayOverlap(AdmittedDarkCloud left, AdmittedDarkCloud right)
        => DarkCloudsMayOverlap(left.PaddedBounds, left.Geometry, right.PaddedBounds, right.Geometry);

    private static void ChargeSamplePoints(long additional, long limit, ref long totalSampledPoints)
    {
        if (additional == 0) return;
        if (additional < 0 || additional > limit - totalSampledPoints)
        {
            throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
        }
        totalSampledPoints += additional;
    }

    private static bool HaveIdenticalFineSamplingGeometry(AdmittedDarkCloud left, AdmittedDarkCloud right)
    {
        var lg = left.Geometry;
        var rg = right.Geometry;
        if (lg.Frame.Center != rg.Frame.Center ||
            lg.Frame.CelestialNorth != rg.Frame.CelestialNorth ||
            lg.Frame.CelestialEast != rg.Frame.CelestialEast ||
            lg.GeometricCenter != rg.GeometricCenter ||
            lg.SemiMajorRadians != rg.SemiMajorRadians ||
            lg.SemiMinorRadians != rg.SemiMinorRadians)
        {
            return false;
        }

        var leftRings = lg.TangentRings;
        var rightRings = rg.TangentRings;
        if (leftRings is { Count: > 0 } || rightRings is { Count: > 0 })
        {
            if (leftRings is null || rightRings is null ||
                leftRings.Count != rightRings.Count ||
                lg.OutlineMinU != rg.OutlineMinU || lg.OutlineMaxU != rg.OutlineMaxU ||
                lg.OutlineMinV != rg.OutlineMinV || lg.OutlineMaxV != rg.OutlineMaxV ||
                left.MaxOutlineDist != right.MaxOutlineDist ||
                left.Profile.SoftEdgeFraction != right.Profile.SoftEdgeFraction)
            {
                return false;
            }
            for (var r = 0; r < leftRings.Count; r++)
            {
                var lr = leftRings[r];
                var rr = rightRings[r];
                if (lr.Length != rr.Length) return false;
                for (var p = 0; p < lr.Length; p++)
                {
                    if (lr[p] != rr[p]) return false;
                }
            }
            return true;
        }

        return lg.MajorAxis == rg.MajorAxis && lg.MinorAxis == rg.MinorAxis;
    }

    private static bool HaveMatchingSampleDirections(FineDarkCloudSample[] left, FineDarkCloudSample[] right)
    {
        if (left.Length != right.Length) return false;
        for (var i = 0; i < left.Length; i++)
        {
            if (left[i].Direction != right[i].Direction) return false;
        }
        return true;
    }

    private static int CompareFineCloudCanonicalOrder(AdmittedDarkCloud left, AdmittedDarkCloud right)
    {
        var cmp = right.MinSpanPixels.CompareTo(left.MinSpanPixels);
        if (cmp != 0) return cmp;
        cmp = right.ProjectedAreaEstimate.CompareTo(left.ProjectedAreaEstimate);
        if (cmp != 0) return cmp;
        cmp = right.MaxSpanPixels.CompareTo(left.MaxSpanPixels);
        if (cmp != 0) return cmp;
        cmp = right.Geometry.SemiMajorRadians.CompareTo(left.Geometry.SemiMajorRadians);
        if (cmp != 0) return cmp;
        cmp = right.Geometry.SemiMinorRadians.CompareTo(left.Geometry.SemiMinorRadians);
        if (cmp != 0) return cmp;
        cmp = left.Item.RightAscensionHours.CompareTo(right.Item.RightAscensionHours);
        if (cmp != 0) return cmp;
        cmp = left.Item.DeclinationDegrees.CompareTo(right.Item.DeclinationDegrees);
        if (cmp != 0) return cmp;
        return StringComparer.Ordinal.Compare(left.Item.Id, right.Item.Id);
    }

    private static bool CanBudgetCrossCloudEvaluation(
        int callerSamples,
        int targetProfileEvaluations,
        int additionalEvaluations,
        int maximumSamplePointsPerSource) =>
        additionalEvaluations >= 0 &&
        additionalEvaluations <= maximumSamplePointsPerSource - callerSamples &&
        additionalEvaluations <= maximumSamplePointsPerSource - targetProfileEvaluations;

    private static AdmittedDarkCloud BuildDarkCloudModel(
        DeepSkyObject dark,
        DeepSkySyntheticSourceOverride? syntheticOverride,
        DeepSkySimulationParameters profile,
        ResolvedSourceGeometry geometryInfo,
        int darkResolvedSub,
        ResolvedSourceProjector projector,
        CameraBasis basis,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        int padding,
        ref long totalOutlineEdgeVisits,
        out bool isResolved,
        CancellationToken cancellationToken)
    {
        var paJ2000 = syntheticOverride?.PositionAngleDegrees ?? dark.PositionAngleDegrees;
        var majorArcmin = syntheticOverride?.MajorAxisArcminutes ?? dark.MajorAxisArcminutes;
        var minorArcmin = syntheticOverride?.MinorAxisArcminutes ?? dark.MinorAxisArcminutes;
        var isCircle = majorArcmin is { } majA && minorArcmin is { } minA && Math.Abs(majA - minA) <= 1e-9;

        // Enforce the remaining outline-edge budget and CancellationToken BEFORE any outline bounds,
        // oriented-box hull, or inscribed-distance work so large convex outlines fail immediately.
        if (geometryInfo.TangentRings is { Count: > 0 } precheckRings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preEdgeCount = CountOutlineEdges(precheckRings);
            var minRequiredVisits = checked(preEdgeCount * (2L + (long)OutlineDistanceGridSize * OutlineDistanceGridSize));
            if (minRequiredVisits > settings.MaximumOutlineEdgeVisits - totalOutlineEdgeVisits)
            {
                throw new InvalidOperationException("Deep-sky outline edge-visit budget exceeded.");
            }
        }

        var paddedBounds = ComputeDarkCloudPaddedBounds(
            geometryInfo, projector, basis, projection, settings, padding, ref totalOutlineEdgeVisits, cancellationToken);
        var outlineFrame = geometryInfo.TangentRings is { Count: > 0 } outlineRings
            ? ComputeOutlineOrientedFrame(
                outlineRings,
                geometryInfo.OutlineMinU,
                geometryInfo.OutlineMaxU,
                geometryInfo.OutlineMinV,
                geometryInfo.OutlineMaxV,
                settings.MaximumOutlineEdgeVisits,
                ref totalOutlineEdgeVisits,
                cancellationToken)
            : (OutlineOrientedFrame?)null;
        var maxOutlineDist = ComputeMaxOutlineDistance(
            geometryInfo, outlineFrame, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits, cancellationToken);
        var maxSpanPixels = Math.Max(geometryInfo.MajorAxisPixels, geometryInfo.MinorAxisPixels);
        var minSpanPixels = Math.Min(geometryInfo.MajorAxisPixels, geometryInfo.MinorAxisPixels);
        var projectedAreaEstimate = Math.PI * 0.25 * geometryInfo.MajorAxisPixels * geometryInfo.MinorAxisPixels;
        if (outlineFrame is { } obb)
        {
            var outlineMaxSpanTangent = Math.Max(
                geometryInfo.OutlineMaxU - geometryInfo.OutlineMinU,
                geometryInfo.OutlineMaxV - geometryInfo.OutlineMinV);
            if (outlineMaxSpanTangent > 0d)
            {
                var outlineScale = maxSpanPixels / outlineMaxSpanTangent;
                var orientedMajorPx = Math.Max(obb.SpanS, obb.SpanT) * outlineScale;
                var orientedMinorPx = Math.Min(obb.SpanS, obb.SpanT) * outlineScale;
                maxSpanPixels = Math.Max(maxSpanPixels, orientedMajorPx);
                minSpanPixels = Math.Min(minSpanPixels, orientedMinorPx);
                if (maxOutlineDist > 0d)
                {
                    minSpanPixels = Math.Min(minSpanPixels, 2d * maxOutlineDist * outlineScale);
                }
                projectedAreaEstimate = obb.SpanS * obb.SpanT * outlineScale * outlineScale;
            }
        }

        // Require BOTH projected dimensions to be well-resolved at the fixed scene-wide subpixel spacing so
        // narrow high-aspect ellipses/outlines never fall between subpixel samples and neighbors never alter
        // each other's quadrature grid.
        isResolved = minSpanPixels >= 1.0 &&
            minSpanPixels * darkResolvedSub >= 8.0 &&
            paddedBounds is { } resolvedBounds &&
            PaddedBoxArea(resolvedBounds) * darkResolvedSub * darkResolvedSub <= settings.MaximumSamplePointsPerSource;

        return new AdmittedDarkCloud(
            dark,
            paJ2000,
            isCircle,
            profile,
            geometryInfo,
            outlineFrame,
            maxOutlineDist,
            minSpanPixels,
            maxSpanPixels,
            projectedAreaEstimate,
            paddedBounds);
    }

    private static int ComputeFineOwnSampleCapForUnresolvedOverlap(
        AdmittedDarkCloud cloud,
        List<AdmittedUnresolvedDiffuseSource> admittedUnresolvedDiffuse,
        DeepSkyRenderSettings settings)
    {
        var cap = settings.MaximumSamplePointsPerSource;
        if (admittedUnresolvedDiffuse.Count == 0)
        {
            return cap;
        }
        HashSet<(double RaHours, double DecDegrees)>? distinctGroups = null;
        var reach = cloud.Geometry.SemiMajorRadians + 0.02;
        for (var i = 0; i < admittedUnresolvedDiffuse.Count; i++)
        {
            var uSrc = admittedUnresolvedDiffuse[i];
            if (AngularSeparationRadians(uSrc.MidpointGeometricDirection, cloud.Geometry.Frame.Center) <= reach)
            {
                distinctGroups ??= [];
                distinctGroups.Add((uSrc.EmitterJ2000.RightAscensionHours, uSrc.EmitterJ2000.DeclinationDegrees));
            }
        }
        if (distinctGroups is null || distinctGroups.Count == 0)
        {
            return cap;
        }
        // For ellipses/circles, the disk interior uses <= pi/4 (~78.5%) of nu*nv cells (12/16 at cap=16, 52/64 at cap=64),
        // leaving >= 4 slots automatically when distinctGroups.Count <= 4. Only trim ownSampleCap when the own grid could
        // otherwise starve distinct overlapping unresolved emitters (e.g. a full rectangular outline at cap=16 or many groups).
        var reserve = Math.Min(distinctGroups.Count, Math.Max(1, cap / 2));
        if (cloud.Geometry.TangentRings is { Count: > 0 })
        {
            return Math.Max(1, cap - reserve);
        }
        var maxAxis = Math.Max(1, (int)Math.Floor(Math.Sqrt(cap)));
        var approxDiskSamples = (int)Math.Ceiling(maxAxis * maxAxis * 0.82);
        return cap - approxDiskSamples >= distinctGroups.Count ? cap : Math.Max(1, cap - reserve);
    }

    private static PreSampledDarkCloud SampleDarkCloudOwnGridOnce(
        AdmittedDarkCloud cloud,
        bool isResolved,
        int resolvedSub,
        List<AdmittedUnresolvedDiffuseSource> admittedUnresolvedDiffuse,
        ResolvedSourceProjector projector,
        CameraBasis basis,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        int padding,
        long paddedWidth,
        Dictionary<long, CachedResolvedPixel> sharedResolvedDirections,
        ref long totalSampledPoints,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        if (isResolved && cloud.PaddedBounds is { } bounds)
        {
            var subCount = resolvedSub * resolvedSub;
            var invSub = 1d / resolvedSub;
            var invSubSq = invSub * invSub;
            var boxArea = PaddedBoxArea(bounds);
            var ownSamples = checked((int)(boxArea * subCount));

            // Charge all subpixel sample evaluations BEFORE executing the sampling loop.
            ChargeSamplePoints(ownSamples, settings.MaximumTotalSamplePoints, ref totalSampledPoints);
            cloud.ProfileEvaluations = checked(cloud.ProfileEvaluations + ownSamples);
            cloud.ReportedSamples = checked(cloud.ReportedSamples + ownSamples);

            var ownSubTaus = new Dictionary<long, double[]>();
            var standaloneTransmission = new Dictionary<long, double>();
            for (var sy = bounds.MinY; sy <= bounds.MaxY; sy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rowBase = ((long)sy + padding) * paddedWidth + padding;
                for (var sx = bounds.MinX; sx <= bounds.MaxX; sx++)
                {
                    var paddedKey = rowBase + sx;
                    var hasCached = sharedResolvedDirections.TryGetValue(paddedKey, out var pixelObj);
                    if (!hasCached)
                    {
                        pixelObj = new CachedResolvedPixel(subCount);
                    }
                    var subTaus = new double[subCount];
                    var anyAdded = false;
                    var ownAbsorbSum = 0d;
                    var subIdx = 0;
                    for (var iy = 0; iy < resolvedSub; iy++)
                    {
                        var py = sy + (iy + 0.5) * invSub;
                        for (var ix = 0; ix < resolvedSub; ix++, subIdx++)
                        {
                            EnuVector? dir;
                            if (hasCached)
                            {
                                dir = pixelObj!.Directions[subIdx];
                            }
                            else
                            {
                                var px = sx + (ix + 0.5) * invSub;
                                dir = TryUnprojectPadded(px, py, projection, basis, settings.HorizonPolicy, settings.Refraction, out var unproj)
                                    ? unproj
                                    : null;
                                pixelObj!.Directions[subIdx] = dir;
                            }

                            var addedTau = dir is { } validDir
                                ? EvaluateDarkCloudOpticalDepth(cloud, validDir, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits)
                                : 0d;
                            if (addedTau > 0d)
                            {
                                anyAdded = true;
                                subTaus[subIdx] = addedTau;
                                ownAbsorbSum += 1d - Math.Exp(-addedTau);
                            }
                        }
                    }

                    if (!hasCached)
                    {
                        sharedResolvedDirections[paddedKey] = pixelObj!;
                    }
                    if (!anyAdded) continue;
                    var ownResT = Math.Clamp(1d - ownAbsorbSum * invSubSq, 0d, 1d);
                    ownSubTaus[paddedKey] = subTaus;
                    if (ownResT < 1d)
                    {
                        standaloneTransmission[paddedKey] = ownResT;
                    }
                }
            }
            cloud.StandaloneTransmission = standaloneTransmission;
            return new PreSampledDarkCloud(cloud, IsResolved: true, ownSamples, ownSubTaus, FineSamples: null, SoloFineDeposits: null);
        }

        var ownSampleCap = ComputeFineOwnSampleCapForUnresolvedOverlap(cloud, admittedUnresolvedDiffuse, settings);
        var fineSamples = SampleFineDarkCloudOnce(
            cloud,
            projector,
            projection,
            settings,
            ownSampleCap,
            padding,
            paddedWidth,
            ref totalSampledPoints,
            ref totalOutlineEdgeVisits,
            out var chargedFineSamples,
            cancellationToken);
        cloud.ProfileEvaluations = checked(cloud.ProfileEvaluations + chargedFineSamples);
        cloud.ReportedSamples = checked(cloud.ReportedSamples + chargedFineSamples);
        var soloTau = new double[fineSamples.Length];
        var zeroPrior = new double[fineSamples.Length];
        for (var m = 0; m < fineSamples.Length; m++)
        {
            soloTau[m] = fineSamples[m].Tau;
        }
        var soloDeposits = fineSamples.Length > 0
            ? BuildFineCellDeposits(fineSamples, soloTau, zeroPrior, settings.MaximumSparsePixels)
            : new Dictionary<long, FineCellDeposit>();
        return new PreSampledDarkCloud(cloud, IsResolved: false, chargedFineSamples, ResolvedOwnSubTaus: null, fineSamples, soloDeposits);
    }

    private static bool HasVisibleDiffuseSupport(
        PreSampledDarkCloud sampled,
        double declaredDiffuseBg,
        Dictionary<long, DeepSkyPixelRate> diffusePrePsf,
        ProjectionContext projection,
        (int Dx, int Dy, double Weight)[] kernel,
        int padding,
        long paddedWidth,
        out bool hasBackgroundTouch)
    {
        hasBackgroundTouch = false;
        if (sampled.IsResolved)
        {
            if (sampled.Cloud.StandaloneTransmission is null || sampled.Cloud.StandaloneTransmission.Count == 0)
            {
                return false;
            }
            foreach (var (paddedKey, ownT) in sampled.Cloud.StandaloneTransmission)
            {
                if (1d - ownT <= 1e-15) continue;
                var existingDiffuse = diffusePrePsf.GetValueOrDefault(paddedKey);
                if (existingDiffuse.Mono + declaredDiffuseBg <= 0d &&
                    existingDiffuse.Red + declaredDiffuseBg <= 0d &&
                    existingDiffuse.Green + declaredDiffuseBg <= 0d &&
                    existingDiffuse.Blue + declaredDiffuseBg <= 0d)
                {
                    continue;
                }
                hasBackgroundTouch = true;
                var sx = (int)(paddedKey % paddedWidth) - padding;
                var sy = (int)(paddedKey / paddedWidth) - padding;
                foreach (var (dx, dy, _) in kernel)
                {
                    var px = sx + dx;
                    var py = sy + dy;
                    if (px >= 0 && px < projection.WidthPixels && py >= 0 && py < projection.HeightPixels &&
                        projection.ContainsSample(px + 0.5, py + 0.5))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        if (sampled.SoloFineDeposits is null || sampled.SoloFineDeposits.Count == 0)
        {
            return false;
        }
        foreach (var (paddedKey, deposit) in sampled.SoloFineDeposits)
        {
            if (deposit.SoloAlpha <= 1e-15) continue;
            var existingDiffuse = diffusePrePsf.GetValueOrDefault(paddedKey);
            if (existingDiffuse.Mono + declaredDiffuseBg <= 0d &&
                existingDiffuse.Red + declaredDiffuseBg <= 0d &&
                existingDiffuse.Green + declaredDiffuseBg <= 0d &&
                existingDiffuse.Blue + declaredDiffuseBg <= 0d)
            {
                continue;
            }
            hasBackgroundTouch = true;
            var sx = (int)(paddedKey % paddedWidth) - padding;
            var sy = (int)(paddedKey / paddedWidth) - padding;
            foreach (var (dx, dy, _) in kernel)
            {
                var px = sx + dx;
                var py = sy + dy;
                if (px >= 0 && px < projection.WidthPixels && py >= 0 && py < projection.HeightPixels &&
                    projection.ContainsSample(px + 0.5, py + 0.5))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static Dictionary<(int SourceIndex, int SampleIndex), double> EvaluateCloudTemporalUnresolvedTaus(
        PreSampledDarkCloud sampled,
        List<AdmittedUnresolvedDiffuseSource> admittedUnresolvedDiffuse,
        VisibleSceneRequest sceneRequest,
        ResolvedSourceProjector projector,
        DeepSkyRenderSettings settings,
        int padding,
        long paddedWidth,
        ref long totalSampledPoints,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<(int SourceIndex, int SampleIndex), double>();
        if (admittedUnresolvedDiffuse.Count == 0)
        {
            return result;
        }

        var cloud = sampled.Cloud;
        var reach = cloud.Geometry.SemiMajorRadians + 0.02;

        // Group overlapping unresolved emitters by exact J2000 sky coordinates so co-located emitters
        // share cached cloud optical-depth evaluations with zero duplicate sample charges, and sort
        // distinct position groups canonically so fair budget allocation is independent of list order.
        var groupsByPosition = new Dictionary<(double RaHours, double DecDegrees), TemporalUnresolvedPositionGroup>();
        for (var srcIdx = 0; srcIdx < admittedUnresolvedDiffuse.Count; srcIdx++)
        {
            var uSrc = admittedUnresolvedDiffuse[srcIdx];
            if (AngularSeparationRadians(uSrc.MidpointGeometricDirection, cloud.Geometry.Frame.Center) > reach)
            {
                continue;
            }
            var posKey = (uSrc.EmitterJ2000.RightAscensionHours, uSrc.EmitterJ2000.DeclinationDegrees);
            if (!groupsByPosition.TryGetValue(posKey, out var posGroup))
            {
                posGroup = new TemporalUnresolvedPositionGroup(uSrc.EmitterJ2000, uSrc.MidpointGeometricDirection);
                groupsByPosition[posKey] = posGroup;
            }
            posGroup.SourceIndices.Add(srcIdx);
            for (var sampIdx = 0; sampIdx < uSrc.Samples.Count; sampIdx++)
            {
                posGroup.UniqueSampleTicks.Add(uSrc.Samples[sampIdx].SampleUtc.UtcTicks);
            }
        }
        if (groupsByPosition.Count == 0)
        {
            return result;
        }

        var orderedGroups = groupsByPosition.Values.ToList();
        orderedGroups.Sort(static (left, right) =>
        {
            var cmp = left.EmitterJ2000.RightAscensionHours.CompareTo(right.EmitterJ2000.RightAscensionHours);
            return cmp != 0 ? cmp : left.EmitterJ2000.DeclinationDegrees.CompareTo(right.EmitterJ2000.DeclinationDegrees);
        });

        var remainingPerSource = Math.Max(0, Math.Min(
            settings.MaximumSamplePointsPerSource - cloud.ReportedSamples,
            settings.MaximumSamplePointsPerSource - cloud.ProfileEvaluations));

        var uniqueTimestampsByGroup = new long[orderedGroups.Count][];
        var alloc = new int[orderedGroups.Count];
        for (var g = 0; g < orderedGroups.Count; g++)
        {
            var ticks = orderedGroups[g].UniqueSampleTicks.ToArray();
            Array.Sort(ticks);
            uniqueTimestampsByGroup[g] = ticks;
        }

        // Water-filling fair allocation across distinct overlapping emitter positions so no single emitter
        // starves another while strictly honoring MaximumSamplePointsPerSource and MaximumTotalSamplePoints.
        var remaining = remainingPerSource;
        while (remaining > 0)
        {
            var progressed = false;
            for (var g = 0; g < orderedGroups.Count && remaining > 0; g++)
            {
                if (alloc[g] < uniqueTimestampsByGroup[g].Length)
                {
                    alloc[g]++;
                    remaining--;
                    progressed = true;
                }
            }
            if (!progressed) break;
        }

        for (var g = 0; g < orderedGroups.Count; g++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = orderedGroups[g];
            var allTicks = uniqueTimestampsByGroup[g];
            var budget = alloc[g];
            if (allTicks.Length == 0) continue;

            Dictionary<long, double>? exactCache = null;
            long[]? evaluatedTicks = null;
            double[]? evaluatedTaus = null;

            if (budget == allTicks.Length)
            {
                exactCache = new Dictionary<long, double>(allTicks.Length);
                for (var tIdx = 0; tIdx < allTicks.Length; tIdx++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tick = allTicks[tIdx];
                    ChargeSamplePoints(1, settings.MaximumTotalSamplePoints, ref totalSampledPoints);
                    cloud.ReportedSamples = checked(cloud.ReportedSamples + 1);
                    cloud.ProfileEvaluations = checked(cloud.ProfileEvaluations + 1);
                    exactCache[tick] = EvaluateDarkCloudOpticalDepthAtTime(
                        cloud,
                        group.EmitterJ2000,
                        group.MidpointGeometricDirection,
                        new DateTimeOffset(tick, TimeSpan.Zero),
                        sceneRequest,
                        settings.MaximumOutlineEdgeVisits,
                        ref totalOutlineEdgeVisits);
                }
            }
            else if (budget > 0)
            {
                evaluatedTicks = new long[budget];
                evaluatedTaus = new double[budget];
                exactCache = new Dictionary<long, double>(budget);
                for (var slot = 0; slot < budget; slot++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var mappedIdx = budget == 1
                        ? allTicks.Length / 2
                        : (int)Math.Round(slot * (allTicks.Length - 1d) / (budget - 1d));
                    var tick = allTicks[mappedIdx];
                    ChargeSamplePoints(1, settings.MaximumTotalSamplePoints, ref totalSampledPoints);
                    cloud.ReportedSamples = checked(cloud.ReportedSamples + 1);
                    cloud.ProfileEvaluations = checked(cloud.ProfileEvaluations + 1);
                    var tau = EvaluateDarkCloudOpticalDepthAtTime(
                        cloud,
                        group.EmitterJ2000,
                        group.MidpointGeometricDirection,
                        new DateTimeOffset(tick, TimeSpan.Zero),
                        sceneRequest,
                        settings.MaximumOutlineEdgeVisits,
                        ref totalOutlineEdgeVisits);
                    evaluatedTicks[slot] = tick;
                    evaluatedTaus[slot] = tau;
                    exactCache[tick] = tau;
                }
            }

            // A starved group shares the same co-moving midpoint and cached depth across all
            // of its samples and co-located emitters. Charge outline containment once per group.
            var fallbackTau = budget == 0
                ? LookupAlreadySampledDarkCloudTau(
                    sampled, group.MidpointGeometricDirection, projector, padding, paddedWidth,
                    settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits)
                : 0d;
            foreach (var srcIdx in group.SourceIndices)
            {
                var uSrc = admittedUnresolvedDiffuse[srcIdx];
                for (var sampIdx = 0; sampIdx < uSrc.Samples.Count; sampIdx++)
                {
                    var sample = uSrc.Samples[sampIdx];
                    var tick = sample.SampleUtc.UtcTicks;
                    double tau;
                    if (exactCache is not null && exactCache.TryGetValue(tick, out var cachedTau))
                    {
                        tau = cachedTau;
                    }
                    else if (evaluatedTicks is not null && evaluatedTaus is not null)
                    {
                        var bestSlot = 0;
                        var bestDiff = Math.Abs(tick - evaluatedTicks[0]);
                        for (var slot = 1; slot < evaluatedTicks.Length; slot++)
                        {
                            var diff = Math.Abs(tick - evaluatedTicks[slot]);
                            if (diff < bestDiff)
                            {
                                bestDiff = diff;
                                bestSlot = slot;
                            }
                        }
                        tau = evaluatedTaus[bestSlot];
                    }
                    else
                    {
                        tau = fallbackTau;
                    }
                    if (tau > 0d)
                    {
                        result[(srcIdx, sampIdx)] = tau;
                    }
                }
            }
        }

        return result;
    }

    private static double LookupAlreadySampledDarkCloudTau(
        PreSampledDarkCloud sampled,
        EnuVector midpointDirection,
        ResolvedSourceProjector projector,
        int padding,
        long paddedWidth,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        var cloud = sampled.Cloud;
        if (!DarkCloudContainsDirection(cloud.Geometry, midpointDirection, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits))
        {
            return 0d;
        }

        if (sampled.IsResolved && sampled.ResolvedOwnSubTaus is not null)
        {
            if (!projector.TryProject(midpointDirection, out var midPixel))
            {
                return 0d;
            }
            var cx = (long)Math.Floor(midPixel.X);
            var cy = (long)Math.Floor(midPixel.Y);
            var key = (cy + padding) * paddedWidth + cx + padding;
            if (!sampled.ResolvedOwnSubTaus.TryGetValue(key, out var subTaus) || subTaus.Length == 0)
            {
                return 0d;
            }
            var sub = (int)Math.Round(Math.Sqrt(subTaus.Length));
            var fx = Math.Clamp((int)Math.Floor((midPixel.X - cx) * sub), 0, sub - 1);
            var fy = Math.Clamp((int)Math.Floor((midPixel.Y - cy) * sub), 0, sub - 1);
            return subTaus[fy * sub + fx];
        }

        if (sampled.FineSamples is { Length: > 0 } fineSamples)
        {
            var bestTau = 0d;
            var bestDistanceSquared = double.PositiveInfinity;
            var count = fineSamples.Length;
            var matchLimit = Math.Max(1e-7, cloud.Geometry.SemiMajorRadians * 2d / Math.Sqrt(count));
            for (var i = 0; i < count; i++)
            {
                var sample = fineSamples[i];
                var delta = midpointDirection - sample.Direction;
                var distanceSquared = EnuVector.Dot(delta, delta);
                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    bestTau = sample.Tau;
                }
            }
            var matchChord = 2d * Math.Sin(Math.Min(Math.PI, matchLimit) / 2d);
            return bestDistanceSquared <= matchChord * matchChord ? bestTau : 0d;
        }
        return 0d;
    }

    private static void BuildTemporalDarkDeficits(
        Dictionary<(int SourceIndex, int SampleIndex), double> cloudTaus,
        Dictionary<(int SourceIndex, int SampleIndex), double> unresolvedDarkCumulativeTau,
        List<AdmittedUnresolvedDiffuseSource> admittedUnresolvedDiffuse,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        Dictionary<int, DeepSkyPixelRate> temporalDarkDeficitPostCloud,
        Dictionary<int, DeepSkyPixelRate> temporalDarkDeficitPreCloud,
        Dictionary<(int SourceIndex, int SampleIndex), double> provisionalUnresolvedTauUpdates,
        ref double attenuatedPrePsf,
        ref long totalKernelVisits,
        CancellationToken cancellationToken)
    {
        if (cloudTaus.Count == 0) return;

        for (var srcIdx = 0; srcIdx < admittedUnresolvedDiffuse.Count; srcIdx++)
        {
            var uSrc = admittedUnresolvedDiffuse[srcIdx];
            List<WeightedPointSpreadSample>? samplePostDeficits = null;
            for (var sampIdx = 0; sampIdx < uSrc.Samples.Count; sampIdx++)
            {
                var key = (srcIdx, sampIdx);
                if (!cloudTaus.TryGetValue(key, out var tau) || tau <= 0d) continue;
                var priorTau = unresolvedDarkCumulativeTau.GetValueOrDefault(key, 0d);
                var deltaFraction = Math.Exp(-priorTau) * (1d - Math.Exp(-tau));
                if (deltaFraction <= 0d) continue;
                provisionalUnresolvedTauUpdates[key] = priorTau + tau;
                samplePostDeficits ??= [];
                var sample = uSrc.Samples[sampIdx];
                samplePostDeficits.Add(new WeightedPointSpreadSample(
                    sample.Pixel, sample.ExposureFraction * deltaFraction, sample.CloudTransmission));
            }
            if (samplePostDeficits is { Count: > 0 })
            {
                var postMono = new Dictionary<int, double>();
                var preMono = new Dictionary<int, double>();
                RasterizeWeightedSamplesWithPsf(
                    samplePostDeficits.ToArray(), uSrc.FluxRate, projection, settings,
                    postMono, preMono, ref totalKernelVisits, cancellationToken);
                foreach (var (idx, val) in postMono)
                {
                    var neg = ScaleRate(-val, uSrc.Spectral);
                    AddSparseRate(temporalDarkDeficitPostCloud, idx, neg, settings.MaximumSparsePixels);
                }
                foreach (var (idx, val) in preMono)
                {
                    var neg = ScaleRate(-val, uSrc.Spectral);
                    AddSparseRate(temporalDarkDeficitPreCloud, idx, neg, settings.MaximumSparsePixels);
                    attenuatedPrePsf += -neg.Mono;
                }
            }
        }
    }

    private static Dictionary<int, DeepSkyPixelRate> ConvolveDarkNegativePrePsf(
        Dictionary<long, DeepSkyPixelRate> negPrePsf,
        ProjectionContext projection,
        (int Dx, int Dy, double Weight)[] kernel,
        int padding,
        long paddedWidth,
        DeepSkyRenderSettings settings,
        ref long totalKernelVisits,
        CancellationToken cancellationToken)
    {
        var clippedDelta = new Dictionary<int, DeepSkyPixelRate>();
        foreach (var (paddedKey, negRate) in negPrePsf)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalKernelVisits += kernel.Length;
            if (totalKernelVisits > settings.MaximumKernelCellVisits)
            {
                throw new InvalidOperationException("Deep-sky PSF kernel budget exceeded.");
            }
            var sx = (int)(paddedKey % paddedWidth) - padding;
            var sy = (int)(paddedKey / paddedWidth) - padding;
            foreach (var (dx, dy, w) in kernel)
            {
                var px = sx + dx;
                var py = sy + dy;
                if (px < 0 || px >= projection.WidthPixels || py < 0 || py >= projection.HeightPixels ||
                    !projection.ContainsSample(px + 0.5, py + 0.5))
                {
                    continue;
                }
                var idx = py * projection.WidthPixels + px;
                var scaled = new DeepSkyPixelRate(negRate.Mono * w, negRate.Red * w, negRate.Green * w, negRate.Blue * w);
                AddSparseRate(clippedDelta, idx, scaled, settings.MaximumSparsePixels);
            }
        }
        return clippedDelta;
    }

    private static (double AttenuatedRetained, HashSet<int> ActiveDarkPixels, RenderedObjectGeometry? Footprint) SummarizeDarkDelta(
        AdmittedDarkCloud cloud,
        Dictionary<int, DeepSkyPixelRate> clippedDelta,
        Dictionary<int, DeepSkyPixelRate> temporalDarkDeficitPreCloud,
        ProjectionContext projection)
    {
        var attenuatedRetained = 0d;
        var minX = projection.WidthPixels;
        var minY = projection.HeightPixels;
        var maxX = -1;
        var maxY = -1;
        var sumW = 0d;
        var wx = 0d;
        var wy = 0d;
        var activeDarkPixels = new HashSet<int>();
        foreach (var (idx, delta) in clippedDelta)
        {
            if (delta.Mono < 0 || delta.Red < 0 || delta.Green < 0 || delta.Blue < 0)
            {
                activeDarkPixels.Add(idx);
            }
            var mag = -delta.Mono;
            if (mag <= 0) continue;
            var x = idx % projection.WidthPixels;
            var y = idx / projection.WidthPixels;
            attenuatedRetained += mag;
            sumW += mag;
            wx += mag * (x + 0.5);
            wy += mag * (y + 0.5);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        foreach (var (idx, delta) in temporalDarkDeficitPreCloud)
        {
            if (delta.Mono < 0 || delta.Red < 0 || delta.Green < 0 || delta.Blue < 0)
            {
                activeDarkPixels.Add(idx);
            }
            var mag = -delta.Mono;
            if (mag <= 0) continue;
            var x = idx % projection.WidthPixels;
            var y = idx / projection.WidthPixels;
            attenuatedRetained += mag;
            sumW += mag;
            wx += mag * (x + 0.5);
            wy += mag * (y + 0.5);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        if (attenuatedRetained <= 0 || activeDarkPixels.Count == 0)
        {
            return (0d, activeDarkPixels, null);
        }
        var centerRef = cloud.Geometry.CenterPixel ?? new PixelPoint(wx / sumW, wy / sumW);
        var centroid = new PixelPoint(wx / sumW, wy / sumW);
        var darkFootprint = new RenderedObjectGeometry(cloud.Item.Id, centerRef, centroid, 0d, 0d, 0d, minX, minY, maxX, maxY);
        return (attenuatedRetained, activeDarkPixels, darkFootprint);
    }

    private static void EvaluateDarkCandidatesCanonical(
        List<DeepSkyObject> darkCandidates,
        Dictionary<string, DeepSkySyntheticSourceOverride> overridesById,
        Dictionary<string, IReadOnlyList<DeepSkyOutline>> outlinesById,
        bool hasCompatibleDiffuseBackground,
        bool hasExtendedDiffuseBackground,
        double declaredDiffuseBg,
        Dictionary<long, DeepSkyPixelRate> diffusePrePsf,
        List<AdmittedUnresolvedDiffuseSource> admittedUnresolvedDiffuse,
        List<HygStarEntry> hygStars,
        Dictionary<string, AdmittedStellarExposure>? admittedStellarById,
        Dictionary<int, DeepSkyPixelRate> overlapDiffusePreCloud,
        Dictionary<int, DeepSkyPixelRate> overlapDiffuseTemporal,
        Dictionary<int, DeepSkyPixelRate> rates,
        Dictionary<int, DeepSkyPixelRate> temporalRates,
        Dictionary<int, DeepSkyPixelRate> cumulativeAttenuatedConvolved,
        Dictionary<int, DeepSkyPixelRate> cumulativeTemporalAttenuated,
        Dictionary<(int SourceIndex, int SampleIndex), double> unresolvedDarkCumulativeTau,
        Dictionary<long, double> cumulativeTransmission,
        HashSet<long> prePsfSparsePixels,
        DarkAttenuationLayerState darkLayerState,
        VisibleSceneRequest sceneRequest,
        DateTimeOffset exposureStartUtc,
        TimeSpan exposure,
        CameraBasis basis,
        EnuVector boresight,
        double opticalRadiusDegrees,
        double exposureMarginDegrees,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        LinearSceneRenderOptions renderOptions,
        DeepSkyRenderSettings settings,
        int padding,
        long paddedWidth,
        (int Dx, int Dy, double Weight)[] kernel,
        List<DeepSkySourceDiagnostic> diagnostics,
        ref Dictionary<int, DeepSkyPixelRate>? foregroundHygTemporalRates,
        ref HashSet<string>? rasterizedForegroundStars,
        ref int eligibleCount,
        ref int admittedCount,
        ref int darkAttenuationCount,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        // Phase 2A: Determine intrinsic eligibility and sample each cloud's own grid once in priority order
        // (enforcing MaximumRenderedSources without allowing later or skipped clouds to mutate earlier ones).
        var admittedPreSampled = new List<PreSampledDarkCloud>();
        var preEvaluatedTemporalTaus = new Dictionary<AdmittedDarkCloud, Dictionary<(int SourceIndex, int SampleIndex), double>>();
        var projectedAdmitted = admittedCount;

        foreach (var dark in darkCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            overridesById.TryGetValue(dark.Id, out var syntheticOverride);
            var profile = DeepSkySourceProfileResolver.Resolve(dark, syntheticOverride);
            var geometryInfo = ResolveGeometry(
                dark, syntheticOverride, DeepSkyProfileClass.DarkNebula, sceneRequest, basis, boresight, opticalRadiusDegrees,
                exposureMarginDegrees, projector, outlinesById, settings, out var skipReason);
            if (skipReason is not null)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula, skipReason,
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }
            if (!hasCompatibleDiffuseBackground)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            eligibleCount++;
            if (projectedAdmitted >= settings.MaximumRenderedSources)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula, "budget-skipped",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }
            if (profile.PeakOpticalDepth <= 0d)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            var currentDarkCloud = BuildDarkCloudModel(
                dark, syntheticOverride, profile, geometryInfo, darkLayerState.ResolvedSub,
                projector, basis, projection, settings, padding, ref totalOutlineEdgeVisits,
                out var isResolved, cancellationToken);

            var sampled = SampleDarkCloudOwnGridOnce(
                currentDarkCloud,
                isResolved,
                darkLayerState.ResolvedSub,
                admittedUnresolvedDiffuse,
                projector,
                basis,
                projection,
                settings,
                padding,
                paddedWidth,
                darkLayerState.ResolvedPixels,
                ref totalSampledPoints,
                ref totalOutlineEdgeVisits,
                cancellationToken);

            var hasDiffuseTouch = false;
            var hasDiffuseInFov = hasExtendedDiffuseBackground &&
                HasVisibleDiffuseSupport(sampled, declaredDiffuseBg, diffusePrePsf, projection, kernel, padding, paddedWidth, out hasDiffuseTouch);

            var temporalTaus = EvaluateCloudTemporalUnresolvedTaus(
                sampled, admittedUnresolvedDiffuse, sceneRequest, projector, settings, padding, paddedWidth,
                ref totalSampledPoints, ref totalOutlineEdgeVisits, cancellationToken);
            if (!hasDiffuseInFov && temporalTaus.Count == 0)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    hasDiffuseTouch ? "annotation-only-outside-fov" : "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }
            preEvaluatedTemporalTaus[currentDarkCloud] = temporalTaus;

            projectedAdmitted++;
            admittedPreSampled.Add(sampled);
        }

        if (admittedPreSampled.Count == 0)
        {
            return;
        }

        ComposeCanonicalDarkClouds(
            admittedPreSampled,
            preEvaluatedTemporalTaus,
            soloPrecomputedByCloud: null,
            declaredDiffuseBg,
            diffusePrePsf,
            admittedUnresolvedDiffuse,
            hygStars,
            admittedStellarById,
            overlapDiffusePreCloud,
            overlapDiffuseTemporal,
            rates,
            temporalRates,
            cumulativeAttenuatedConvolved,
            cumulativeTemporalAttenuated,
            unresolvedDarkCumulativeTau,
            cumulativeTransmission,
            prePsfSparsePixels,
            darkLayerState,
            sceneRequest,
            exposureStartUtc,
            exposure,
            projector,
            projection,
            renderOptions,
            settings,
            padding,
            paddedWidth,
            kernel,
            diagnostics,
            ref foregroundHygTemporalRates,
            ref rasterizedForegroundStars,
            ref admittedCount,
            ref darkAttenuationCount,
            ref totalSampledPoints,
            ref totalKernelVisits,
            ref totalOutlineEdgeVisits,
            cancellationToken);
    }

    private static void EvaluateDarkCandidatesWithSnrGating(
        List<DeepSkyObject> darkCandidates,
        Dictionary<string, DeepSkySyntheticSourceOverride> overridesById,
        Dictionary<string, IReadOnlyList<DeepSkyOutline>> outlinesById,
        bool hasCompatibleDiffuseBackground,
        bool hasExtendedDiffuseBackground,
        double declaredDiffuseBg,
        Dictionary<long, DeepSkyPixelRate> diffusePrePsf,
        List<AdmittedUnresolvedDiffuseSource> admittedUnresolvedDiffuse,
        List<HygStarEntry> hygStars,
        Dictionary<string, AdmittedStellarExposure>? admittedStellarById,
        Dictionary<int, DeepSkyPixelRate> overlapDiffusePreCloud,
        Dictionary<int, DeepSkyPixelRate> overlapDiffuseTemporal,
        Dictionary<int, DeepSkyPixelRate> rates,
        Dictionary<int, DeepSkyPixelRate> temporalRates,
        Dictionary<int, DeepSkyPixelRate> cumulativeAttenuatedConvolved,
        Dictionary<int, DeepSkyPixelRate> cumulativeTemporalAttenuated,
        Dictionary<(int SourceIndex, int SampleIndex), double> unresolvedDarkCumulativeTau,
        Dictionary<long, double> cumulativeTransmission,
        HashSet<long> prePsfSparsePixels,
        DarkAttenuationLayerState darkLayerState,
        VisibleSceneRequest sceneRequest,
        DateTimeOffset exposureStartUtc,
        TimeSpan exposure,
        CameraBasis basis,
        EnuVector boresight,
        double opticalRadiusDegrees,
        double exposureMarginDegrees,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        LinearSceneRenderOptions renderOptions,
        DeepSkyRenderSettings settings,
        int padding,
        long paddedWidth,
        (int Dx, int Dy, double Weight)[] kernel,
        List<DeepSkySourceDiagnostic> diagnostics,
        ref Dictionary<int, DeepSkyPixelRate>? foregroundHygTemporalRates,
        ref HashSet<string>? rasterizedForegroundStars,
        ref int eligibleCount,
        ref int admittedCount,
        ref int darkAttenuationCount,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        // Sample each intrinsic candidate once. If canonical composition rejects a cloud, resume
        // screening at the next priority candidate to fill the vacated slot.
        var soloPassing = new List<SoloScreenedDarkCloud>();
        var remainingSlots = settings.MaximumRenderedSources - admittedCount;
        var nextCandidate = 0;

        while (true)
        {
            while (nextCandidate < darkCandidates.Count && soloPassing.Count < remainingSlots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dark = darkCandidates[nextCandidate++];
                overridesById.TryGetValue(dark.Id, out var syntheticOverride);
                var profile = DeepSkySourceProfileResolver.Resolve(dark, syntheticOverride);
                var geometryInfo = ResolveGeometry(
                    dark, syntheticOverride, DeepSkyProfileClass.DarkNebula, sceneRequest, basis, boresight, opticalRadiusDegrees,
                    exposureMarginDegrees, projector, outlinesById, settings, out var skipReason);
                if (skipReason is not null)
                {
                    diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula, skipReason,
                        DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                    continue;
                }
                if (!hasCompatibleDiffuseBackground)
                {
                    diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                        "annotation-only-dark-no-compatible-background",
                        DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                    continue;
                }

                eligibleCount++;
                if (profile.PeakOpticalDepth <= 0d)
                {
                    diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                        "annotation-only-dark-no-compatible-background",
                        DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                    continue;
                }

                var currentDarkCloud = BuildDarkCloudModel(
                    dark, syntheticOverride, profile, geometryInfo, darkLayerState.ResolvedSub,
                    projector, basis, projection, settings, padding, ref totalOutlineEdgeVisits,
                    out var isResolved, cancellationToken);

                var sampled = SampleDarkCloudOwnGridOnce(
                    currentDarkCloud,
                    isResolved,
                    darkLayerState.ResolvedSub,
                    admittedUnresolvedDiffuse,
                    projector,
                    basis,
                    projection,
                    settings,
                    padding,
                    paddedWidth,
                    darkLayerState.ResolvedPixels,
                    ref totalSampledPoints,
                    ref totalOutlineEdgeVisits,
                    cancellationToken);

                var soloNegPrePsf = BuildSoloNegativePrePsf(sampled, declaredDiffuseBg, diffusePrePsf, out var soloAttenuatedPrePsf);
                var temporalTaus = EvaluateCloudTemporalUnresolvedTaus(
                    sampled, admittedUnresolvedDiffuse, sceneRequest, projector, settings, padding, paddedWidth,
                    ref totalSampledPoints, ref totalOutlineEdgeVisits, cancellationToken);
                var soloTempPost = new Dictionary<int, DeepSkyPixelRate>();
                var soloTempPre = new Dictionary<int, DeepSkyPixelRate>();
                var dummyUpdates = new Dictionary<(int SourceIndex, int SampleIndex), double>();
                if (temporalTaus.Count > 0)
                {
                    BuildTemporalDarkDeficits(
                        temporalTaus,
                        new Dictionary<(int SourceIndex, int SampleIndex), double>(),
                        admittedUnresolvedDiffuse,
                        projection,
                        settings,
                        soloTempPost,
                        soloTempPre,
                        dummyUpdates,
                        ref soloAttenuatedPrePsf,
                        ref totalKernelVisits,
                        cancellationToken);
                }

                if ((soloNegPrePsf.Count == 0 && soloTempPre.Count == 0) || soloAttenuatedPrePsf <= 0d)
                {
                    diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                        "annotation-only-dark-no-compatible-background",
                        DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                    continue;
                }

                var soloClippedDelta = ConvolveDarkNegativePrePsf(
                    soloNegPrePsf, projection, kernel, padding, paddedWidth, settings, ref totalKernelVisits, cancellationToken);
                var (soloRetained, soloActivePixels, soloFootprint) = SummarizeDarkDelta(
                    currentDarkCloud, soloClippedDelta, soloTempPre, projection);
                if (soloRetained <= 0d || soloActivePixels.Count == 0 || soloFootprint is null)
                {
                    diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                        "annotation-only-outside-fov", DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                    continue;
                }

                var nativeSoloPixels = CompleteBinSupport(soloActivePixels, projection, settings);
                if (hygStars.Count > 0)
                {
                    foregroundHygTemporalRates ??= new Dictionary<int, DeepSkyPixelRate>();
                    rasterizedForegroundStars ??= new HashSet<string>(StringComparer.Ordinal);
                    AccumulateForegroundHygRatesForAperture(
                        hygStars,
                        admittedStellarById,
                        currentDarkCloud.Geometry,
                        nativeSoloPixels,
                        sceneRequest,
                        exposureStartUtc,
                        exposure,
                        projector,
                        projection,
                        renderOptions,
                        settings,
                        rasterizedForegroundStars,
                        foregroundHygTemporalRates,
                        ref totalSampledPoints,
                        ref totalKernelVisits,
                        cancellationToken);
                }

                var soloPrediction = EvaluateDarkSignalPrediction(
                    nativeSoloPixels,
                    soloClippedDelta,
                    soloTempPost,
                    declaredDiffuseBg,
                    overlapDiffusePreCloud,
                    overlapDiffuseTemporal,
                    cumulativeAttenuatedConvolved,
                    cumulativeTemporalAttenuated,
                    rates,
                    temporalRates,
                    foregroundHygTemporalRates,
                    projection,
                    renderOptions,
                    settings,
                    cancellationToken);

                if (soloPrediction.SignalToNoise < settings.MinimumSignalToNoise)
                {
                    diagnostics.Add(new DeepSkySourceDiagnostic(
                        dark.Id, dark.DisplayName, dark.ObjectType, DeepSkyProfileClass.DarkNebula,
                        Admitted: false, Reason: "annotation-only-below-snr",
                        DeepSkyPhotometryBasis.None, geometryInfo.Basis,
                        profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                        profile.Spectral.Origin, profile.IsSimulationParameter,
                        0d, 0d, 0d, 0d, 0, 0d, 0, 0d, soloPrediction,
                        currentDarkCloud.ReportedSamples, soloActivePixels.Count, false, soloFootprint));
                    continue;
                }

                soloPassing.Add(new SoloScreenedDarkCloud(
                    sampled,
                    temporalTaus,
                    soloClippedDelta,
                    soloRetained,
                    soloActivePixels,
                    nativeSoloPixels,
                    soloFootprint,
                    soloPrediction));
            }

            if (soloPassing.Count == 0)
            {
                break;
            }

            var admittedList = new List<PreSampledDarkCloud>(soloPassing.Count);
            var preTemporal = new Dictionary<AdmittedDarkCloud, Dictionary<(int SourceIndex, int SampleIndex), double>>(soloPassing.Count);
            var soloPrecomputed = new Dictionary<AdmittedDarkCloud, SoloScreenedDarkCloud>(soloPassing.Count);
            foreach (var entry in soloPassing)
            {
                admittedList.Add(entry.Sampled);
                preTemporal[entry.Sampled.Cloud] = entry.TemporalTaus;
                soloPrecomputed[entry.Sampled.Cloud] = entry;
            }

            // A trial layout uses bounds from the reusable profile tables. Only the final
            // layout may evaluate a profile at another cloud's quadrature directions.
            var screenOnly = NeedsCrossCloudScreening(soloPassing);
            var admissionScreened = false;
            while (true)
            {
                var diagnosticStart = diagnostics.Count;
                var admittedStart = admittedCount;
                var darkStart = darkAttenuationCount;
                var rejected = ComposeCanonicalDarkClouds(
                    admittedList,
                    preTemporal,
                    soloPrecomputed,
                    declaredDiffuseBg,
                    diffusePrePsf,
                    admittedUnresolvedDiffuse,
                    hygStars,
                    admittedStellarById,
                    overlapDiffusePreCloud,
                    overlapDiffuseTemporal,
                    rates,
                    temporalRates,
                    cumulativeAttenuatedConvolved,
                    cumulativeTemporalAttenuated,
                    unresolvedDarkCumulativeTau,
                    cumulativeTransmission,
                    prePsfSparsePixels,
                    darkLayerState,
                    sceneRequest,
                    exposureStartUtc,
                    exposure,
                    projector,
                    projection,
                    renderOptions,
                    settings,
                    padding,
                    paddedWidth,
                    kernel,
                    diagnostics,
                    ref foregroundHygTemporalRates,
                    ref rasterizedForegroundStars,
                    ref admittedCount,
                    ref darkAttenuationCount,
                    ref totalSampledPoints,
                    ref totalKernelVisits,
                    ref totalOutlineEdgeVisits,
                    cancellationToken,
                    screenOnly, admissionScreened);

                if (rejected is null && !screenOnly)
                {
                    EmitBudgetSkippedDarkCandidates(
                        darkCandidates, nextCandidate, overridesById, outlinesById, hasCompatibleDiffuseBackground,
                        sceneRequest, basis, boresight, opticalRadiusDegrees, exposureMarginDegrees, projector,
                        settings, diagnostics, ref eligibleCount);
                    return;
                }

                // Composition has not published pixel transmissions. Discard its provisional
                // convolution, temporal updates, and diagnostics, retaining only actual work charges.
                cumulativeAttenuatedConvolved.Clear();
                cumulativeTemporalAttenuated.Clear();
                unresolvedDarkCumulativeTau.Clear();
                diagnostics.RemoveRange(diagnosticStart, diagnostics.Count - diagnosticStart);
                admittedCount = admittedStart;
                darkAttenuationCount = darkStart;

                if (rejected is not null)
                {
                    diagnostics.Add(rejected);
                    soloPassing.RemoveAll(entry => entry.Sampled.Cloud.Item.Id == rejected.ObjectId);
                    break;
                }
                screenOnly = false;
                admissionScreened = true;
            }
        }

        EmitBudgetSkippedDarkCandidates(
            darkCandidates, nextCandidate, overridesById, outlinesById, hasCompatibleDiffuseBackground,
            sceneRequest, basis, boresight, opticalRadiusDegrees, exposureMarginDegrees, projector,
            settings, diagnostics, ref eligibleCount);
    }

    private static bool NeedsCrossCloudScreening(List<SoloScreenedDarkCloud> clouds)
    {
        for (var i = 0; i < clouds.Count; i++)
        {
            for (var j = i + 1; j < clouds.Count; j++)
            {
                var left = clouds[i].Sampled;
                var right = clouds[j].Sampled;
                if (left.IsResolved && right.IsResolved) continue;
                if (DarkCloudsMayOverlap(left.Cloud, right.Cloud)) return true;
            }
        }
        return false;
    }

    private static void EmitBudgetSkippedDarkCandidates(
        List<DeepSkyObject> darkCandidates,
        int startIndex,
        Dictionary<string, DeepSkySyntheticSourceOverride> overridesById,
        Dictionary<string, IReadOnlyList<DeepSkyOutline>> outlinesById,
        bool hasCompatibleDiffuseBackground,
        VisibleSceneRequest sceneRequest,
        CameraBasis basis,
        EnuVector boresight,
        double opticalRadiusDegrees,
        double exposureMarginDegrees,
        ResolvedSourceProjector projector,
        DeepSkyRenderSettings settings,
        List<DeepSkySourceDiagnostic> diagnostics,
        ref int eligibleCount)
    {
        for (var idx = startIndex; idx < darkCandidates.Count; idx++)
        {
            var dark = darkCandidates[idx];
            overridesById.TryGetValue(dark.Id, out var syntheticOverride);
            var profile = DeepSkySourceProfileResolver.Resolve(dark, syntheticOverride);
            var geometryInfo = ResolveGeometry(
                dark, syntheticOverride, DeepSkyProfileClass.DarkNebula, sceneRequest, basis, boresight, opticalRadiusDegrees,
                exposureMarginDegrees, projector, outlinesById, settings, out var skipReason);
            if (skipReason is not null)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula, skipReason,
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }
            if (!hasCompatibleDiffuseBackground)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }
            eligibleCount++;
            diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula, "budget-skipped",
                DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
        }
    }

    private static Dictionary<long, DeepSkyPixelRate> BuildSoloNegativePrePsf(
        PreSampledDarkCloud sampled,
        double declaredDiffuseBg,
        Dictionary<long, DeepSkyPixelRate> diffusePrePsf,
        out double attenuatedPrePsf)
    {
        var neg = new Dictionary<long, DeepSkyPixelRate>();
        attenuatedPrePsf = 0d;
        if (sampled.IsResolved)
        {
            if (sampled.Cloud.StandaloneTransmission is null) return neg;
            foreach (var (paddedKey, ownT) in sampled.Cloud.StandaloneTransmission)
            {
                var drop = 1d - ownT;
                if (drop <= 1e-15) continue;
                var existingDiffuse = diffusePrePsf.GetValueOrDefault(paddedKey);
                var rawBgMono = existingDiffuse.Mono + declaredDiffuseBg;
                var rawBgR = existingDiffuse.Red + declaredDiffuseBg;
                var rawBgG = existingDiffuse.Green + declaredDiffuseBg;
                var rawBgB = existingDiffuse.Blue + declaredDiffuseBg;
                if (rawBgMono <= 0 && rawBgR <= 0 && rawBgG <= 0 && rawBgB <= 0) continue;
                var delta = new DeepSkyPixelRate(-rawBgMono * drop, -rawBgR * drop, -rawBgG * drop, -rawBgB * drop);
                neg[paddedKey] = delta;
                attenuatedPrePsf += -delta.Mono;
            }
            return neg;
        }
        if (sampled.SoloFineDeposits is null) return neg;
        foreach (var (paddedKey, cell) in sampled.SoloFineDeposits)
        {
            var drop = cell.SoloAlpha;
            if (drop <= 1e-15) continue;
            var existingDiffuse = diffusePrePsf.GetValueOrDefault(paddedKey);
            var rawBgMono = existingDiffuse.Mono + declaredDiffuseBg;
            var rawBgR = existingDiffuse.Red + declaredDiffuseBg;
            var rawBgG = existingDiffuse.Green + declaredDiffuseBg;
            var rawBgB = existingDiffuse.Blue + declaredDiffuseBg;
            if (rawBgMono <= 0 && rawBgR <= 0 && rawBgG <= 0 && rawBgB <= 0) continue;
            var delta = new DeepSkyPixelRate(-rawBgMono * drop, -rawBgR * drop, -rawBgG * drop, -rawBgB * drop);
            neg[paddedKey] = delta;
            attenuatedPrePsf += -delta.Mono;
        }
        return neg;
    }

    private static (List<PreSampledDarkCloud> ResolvedItems, List<CanonicalFineGroup> FineGroups,
        Dictionary<AdmittedDarkCloud, int> ReportedSamples) BuildCanonicalDarkLayout(
        List<PreSampledDarkCloud> admittedPreSampled,
        bool screenOnly,
        DeepSkyRenderSettings settings,
        ref long totalSampledPoints,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken)
    {
        var resolvedItems = new List<PreSampledDarkCloud>();
        var fineItems = new List<PreSampledDarkCloud>();
        for (var i = 0; i < admittedPreSampled.Count; i++)
        {
            if (admittedPreSampled[i].IsResolved) resolvedItems.Add(admittedPreSampled[i]);
            else fineItems.Add(admittedPreSampled[i]);
        }
        resolvedItems.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Cloud.Item.Id, right.Cloud.Item.Id));
        fineItems.Sort(static (left, right) => CompareFineCloudCanonicalOrder(left.Cloud, right.Cloud));

        // 1. Merge coincident fine clouds into canonical groups (canonical first member is RepresentativeCloud).
        var fineGroups = new List<CanonicalFineGroup>();
        for (var i = 0; i < fineItems.Count; i++)
        {
            var item = fineItems[i];
            var samples = item.FineSamples ?? [];
            CanonicalFineGroup? matched = null;
            if (samples.Length > 0)
            {
                for (var g = 0; g < fineGroups.Count; g++)
                {
                    var existing = fineGroups[g];
                    if (HaveIdenticalFineSamplingGeometry(existing.Group.RepresentativeCloud, item.Cloud) &&
                        HaveMatchingSampleDirections(existing.Group.Samples, samples))
                    {
                        matched = existing;
                        break;
                    }
                }
            }

            if (matched is not null)
            {
                for (var m = 0; m < samples.Length; m++)
                {
                    matched.Group.CombinedTau[m] += samples[m].Tau;
                }
                matched.Group.CombinedPeakOpticalDepth += item.Cloud.Profile.PeakOpticalDepth;
                matched.Members.Add(item);
            }
            else
            {
                var combinedTau = new double[samples.Length];
                for (var m = 0; m < samples.Length; m++)
                {
                    combinedTau[m] = samples[m].Tau;
                }
                var priorTau = new double[samples.Length];
                var group = new AdmittedFineDarkCloudGroup(
                    item.Cloud,
                    samples,
                    combinedTau,
                    item.Cloud.Profile.PeakOpticalDepth,
                    priorTau,
                    new Dictionary<long, FineCellDeposit>(),
                    [],
                    [],
                    []);
                var canonicalGroup = new CanonicalFineGroup(group);
                canonicalGroup.Members.Add(item);
                fineGroups.Add(canonicalGroup);
            }
        }

        // Order fine groups strictly canonically with strongest optical depth first so weaker clouds
        // never precede or displace stronger absorption pairs, completely independent of admission order.
        fineGroups.Sort(static (left, right) =>
        {
            var cmp = right.Group.CombinedPeakOpticalDepth.CompareTo(left.Group.CombinedPeakOpticalDepth);
            if (cmp != 0) return cmp;
            return CompareFineCloudCanonicalOrder(left.Group.RepresentativeCloud, right.Group.RepresentativeCloud);
        });

        // 2. Collect all sky-overlapping cross-cloud pairs and sort strictly canonically (strongest absorption first).
        var pairs = new List<CanonicalCrossPair>();
        for (var r = 0; r < resolvedItems.Count; r++)
        {
            var rc = resolvedItems[r].Cloud;
            for (var f = 0; f < fineGroups.Count; f++)
            {
                var fg = fineGroups[f].Group;
                if (fg.Samples.Length == 0 || !DarkCloudsMayOverlap(rc, fg.RepresentativeCloud)) continue;
                var strength = rc.Profile.PeakOpticalDepth * fg.CombinedPeakOpticalDepth;
                pairs.Add(new CanonicalCrossPair(
                    IsResolvedTarget: true,
                    TargetIndex: r,
                    FineIndex: f,
                    Strength: strength));
            }
        }
        for (var a = 0; a < fineGroups.Count; a++)
        {
            var fa = fineGroups[a].Group;
            for (var b = a + 1; b < fineGroups.Count; b++)
            {
                var fb = fineGroups[b].Group;
                if (fb.Samples.Length == 0 || !DarkCloudsMayOverlap(fa.RepresentativeCloud, fb.RepresentativeCloud)) continue;
                var strength = fa.CombinedPeakOpticalDepth * fb.CombinedPeakOpticalDepth;
                pairs.Add(new CanonicalCrossPair(
                    IsResolvedTarget: false,
                    TargetIndex: a,
                    FineIndex: b,
                    Strength: strength));
            }
        }

        pairs.Sort(static (left, right) =>
        {
            var cmp = right.Strength.CompareTo(left.Strength);
            if (cmp != 0) return cmp;
            cmp = right.IsResolvedTarget.CompareTo(left.IsResolvedTarget);
            if (cmp != 0) return cmp;
            cmp = left.TargetIndex.CompareTo(right.TargetIndex);
            if (cmp != 0) return cmp;
            return left.FineIndex.CompareTo(right.FineIndex);
        });

        // Both trial and final layouts make the same canonical allocation. Trial pairs
        // use cached depth levels, so these are ceilings on hypothetical work until commit.
        var localReported = new Dictionary<AdmittedDarkCloud, int>(admittedPreSampled.Count);
        var localProfileEvals = new Dictionary<AdmittedDarkCloud, int>(admittedPreSampled.Count);
        for (var i = 0; i < admittedPreSampled.Count; i++)
        {
            var c = admittedPreSampled[i].Cloud;
            localReported[c] = c.ReportedSamples;
            localProfileEvals[c] = c.ProfileEvaluations;
        }

        for (var p = 0; p < pairs.Count; p++)
        {
            var pair = pairs[p];
            var fineGroup = fineGroups[pair.FineIndex].Group;
            var samples = fineGroup.Samples;
            var receiver = fineGroup.RepresentativeCloud;
            var target = pair.IsResolvedTarget
                ? resolvedItems[pair.TargetIndex]
                : fineGroups[pair.TargetIndex].Members[0];
            var targetCloud = target.Cloud;

            var receiverReported = localReported[receiver];
            var targetProfileEvals = localProfileEvals[targetCloud];
            if (!CanBudgetCrossCloudEvaluation(
                receiverReported, targetProfileEvals, samples.Length, settings.MaximumSamplePointsPerSource))
            {
                continue;
            }

            if (!screenOnly)
            {
                ChargeSamplePoints(samples.Length, settings.MaximumTotalSamplePoints, ref totalSampledPoints);
            }

            var rawTaus = new double[samples.Length];
            for (var m = 0; m < samples.Length; m++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var direction = samples[m].Direction;
                rawTaus[m] = screenOnly
                    ? LookupScreeningDarkCloudTau(targetCloud, direction, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits)
                    : EvaluateDarkCloudOpticalDepth(targetCloud, direction, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            }

            if (screenOnly)
            {
                var peak = pair.IsResolvedTarget
                    ? targetCloud.Profile.PeakOpticalDepth
                    : fineGroups[pair.TargetIndex].Group.CombinedPeakOpticalDepth;
                // Dark radial density has unit slope in q². Outline smoothstep has slope
                // at most 1.5 in normalized clearance. Two buckets cover both and roundoff.
                fineGroup.ScreeningTauError += 2d * peak / DeepSkySourceProfileResolver.DarkDepthTable.BucketCount;
            }

            localReported[receiver] = checked(receiverReported + samples.Length);
            localProfileEvals[targetCloud] = checked(targetProfileEvals + samples.Length);

            if (pair.IsResolvedTarget)
            {
                for (var m = 0; m < samples.Length; m++)
                {
                    fineGroup.PriorTau[m] += rawTaus[m];
                }
                fineGroup.EvaluatedResolved.Add(targetCloud);
            }
            else
            {
                var priorGroup = fineGroups[pair.TargetIndex].Group;
                var invRepPeak = 1d / targetCloud.Profile.PeakOpticalDepth;
                var scale = priorGroup.CombinedPeakOpticalDepth * invRepPeak;
                var unitTau = new double[samples.Length];
                for (var m = 0; m < samples.Length; m++)
                {
                    unitTau[m] = rawTaus[m] * invRepPeak;
                    fineGroup.PriorTau[m] += rawTaus[m] * scale;
                }
                fineGroup.EvaluatedFineGroups.Add(priorGroup);
                fineGroup.UnitTauByPriorFineGroup[priorGroup] = unitTau;
            }
        }

        for (var f = 0; f < fineGroups.Count; f++)
        {
            var fg = fineGroups[f].Group;
            fg.CellDeposits = BuildFineCellDeposits(fg.Samples, fg.CombinedTau, fg.PriorTau, settings.MaximumSparsePixels);
        }

        return (resolvedItems, fineGroups, localReported);
    }

    private static DeepSkySourceDiagnostic? ComposeCanonicalDarkClouds(
        List<PreSampledDarkCloud> admittedPreSampled,
        Dictionary<AdmittedDarkCloud, Dictionary<(int SourceIndex, int SampleIndex), double>> preEvaluatedTemporalTaus,
        Dictionary<AdmittedDarkCloud, SoloScreenedDarkCloud>? soloPrecomputedByCloud,
        double declaredDiffuseBg,
        Dictionary<long, DeepSkyPixelRate> diffusePrePsf,
        List<AdmittedUnresolvedDiffuseSource> admittedUnresolvedDiffuse,
        List<HygStarEntry> hygStars,
        Dictionary<string, AdmittedStellarExposure>? admittedStellarById,
        Dictionary<int, DeepSkyPixelRate> overlapDiffusePreCloud,
        Dictionary<int, DeepSkyPixelRate> overlapDiffuseTemporal,
        Dictionary<int, DeepSkyPixelRate> rates,
        Dictionary<int, DeepSkyPixelRate> temporalRates,
        Dictionary<int, DeepSkyPixelRate> cumulativeAttenuatedConvolved,
        Dictionary<int, DeepSkyPixelRate> cumulativeTemporalAttenuated,
        Dictionary<(int SourceIndex, int SampleIndex), double> unresolvedDarkCumulativeTau,
        Dictionary<long, double> cumulativeTransmission,
        HashSet<long> prePsfSparsePixels,
        DarkAttenuationLayerState darkLayerState,
        VisibleSceneRequest sceneRequest,
        DateTimeOffset exposureStartUtc,
        TimeSpan exposure,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        LinearSceneRenderOptions renderOptions,
        DeepSkyRenderSettings settings,
        int padding,
        long paddedWidth,
        (int Dx, int Dy, double Weight)[] kernel,
        List<DeepSkySourceDiagnostic> diagnostics,
        ref Dictionary<int, DeepSkyPixelRate>? foregroundHygTemporalRates,
        ref HashSet<string>? rasterizedForegroundStars,
        ref int admittedCount,
        ref int darkAttenuationCount,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        ref long totalOutlineEdgeVisits,
        CancellationToken cancellationToken,
        bool screenOnly = false,
        bool admissionScreened = false)
    {
        // Phase 2B: Compose ALL admitted dark clouds in strict canonical order, completely independent
        // of admission priority order. Resolved clouds share exact subpixel directions; coincident fine clouds
        // merge in place with zero extra evaluations; sky-overlapping cross-cloud pairs are prioritized by
        // peak_a * peak_b descending while strictly honoring MaximumSamplePointsPerSource on BOTH sides.
        var (resolvedItems, fineGroups, reportedSamples) = BuildCanonicalDarkLayout(
            admittedPreSampled,
            screenOnly,
            settings,
            ref totalSampledPoints,
            ref totalOutlineEdgeVisits,
            cancellationToken);

        // 3. Step through canonical layers (resolved -> fine groups -> coincident members) to compute exact
        // cumulative transmission and strictly reconciled per-cloud diagnostics with at most one PSF convolution per cloud.
        var resolvedSub = darkLayerState.ResolvedSub;
        var invSubSq = 1d / (resolvedSub * resolvedSub);
        var runningTransmission = new Dictionary<long, double>();
        var resolvedTransmission = new Dictionary<long, double>();
        var resolvedTauByPixel = new Dictionary<long, double[]>();
        var activePriorFineByPixel = new Dictionary<long, List<(AdmittedFineDarkCloudGroup Group, FineCellDeposit Cell)>>();

        var transmissionErrors = screenOnly ? new Dictionary<long, double>() : null;

        void AddFineDarkDeficit(
            long paddedKey,
            FineCellDeposit cell,
            double previousTransmission,
            double nextTransmission,
            double baseError,
            double tauError,
            double conditionalFactorError,
            Dictionary<long, DeepSkyPixelRate> upperDeficits,
            Dictionary<long, DeepSkyPixelRate>? lowerDeficits,
            ref double attenuatedPrePsf)
        {
            var drop = previousTransmission - nextTransmission;
            if (drop > 1e-15)
            {
                runningTransmission[paddedKey] = nextTransmission;
            }

            var upperDrop = drop > 1e-15 ? drop : 0d;
            var lowerDrop = upperDrop;
            if (transmissionErrors is not null)
            {
                var previousError = transmissionErrors.GetValueOrDefault(paddedKey);
                // exp(-tau) is 1-Lipschitz on nonnegative depths. The residual and
                // conditional factor can each vary, bounded by the solo absorption.
                var residualError = Math.Min(1d, tauError + conditionalFactorError) * cell.SoloAlpha;
                var nextError = Math.Max(previousError, baseError + residualError);
                if (nextError > 0d)
                {
                    const double transmissionRoundoff = 2e-15;
                    nextError = Math.Min(1d, nextError + transmissionRoundoff);
                    transmissionErrors[paddedKey] = nextError;
                    var dropError = previousError + nextError;
                    upperDrop = Math.Min(1d, drop + dropError);
                    lowerDrop = Math.Max(0d, drop - dropError - transmissionRoundoff);
                }
            }

            if (upperDrop <= 1e-15) return;
            var existingDiffuse = diffusePrePsf.GetValueOrDefault(paddedKey);
            var rawBgMono = existingDiffuse.Mono + declaredDiffuseBg;
            var rawBgR = existingDiffuse.Red + declaredDiffuseBg;
            var rawBgG = existingDiffuse.Green + declaredDiffuseBg;
            var rawBgB = existingDiffuse.Blue + declaredDiffuseBg;
            if (rawBgMono <= 0 && rawBgR <= 0 && rawBgG <= 0 && rawBgB <= 0) return;

            var delta = new DeepSkyPixelRate(-rawBgMono * upperDrop, -rawBgR * upperDrop, -rawBgG * upperDrop, -rawBgB * upperDrop);
            upperDeficits[paddedKey] = delta;
            attenuatedPrePsf += -delta.Mono;
            if (lowerDeficits is not null && lowerDrop > 0d)
            {
                lowerDeficits[paddedKey] = new DeepSkyPixelRate(
                    -rawBgMono * lowerDrop, -rawBgR * lowerDrop, -rawBgG * lowerDrop, -rawBgB * lowerDrop);
            }
        }

        DeepSkySourceDiagnostic? FinalizeSingleCloudDiagnostic(
            PreSampledDarkCloud sampled,
            Dictionary<long, DeepSkyPixelRate> negPrePsf,
            Dictionary<long, DeepSkyPixelRate>? lowerDeficits,
            double attenuatedPrePsf,
            bool hadNoPriorOverlap,
            ref Dictionary<int, DeepSkyPixelRate>? fgHygRates,
            ref HashSet<string>? fgStars,
            ref int admCount,
            ref int darkCount,
            ref long totSamples,
            ref long totKernel,
            ref long totOutline)
        {
            var cloud = sampled.Cloud;
            if (!preEvaluatedTemporalTaus.TryGetValue(cloud, out var temporalTaus))
            {
                temporalTaus = EvaluateCloudTemporalUnresolvedTaus(
                    sampled, admittedUnresolvedDiffuse, sceneRequest, projector, settings, padding, paddedWidth,
                    ref totSamples, ref totOutline, cancellationToken);
            }
            var temporalPost = new Dictionary<int, DeepSkyPixelRate>();
            var temporalPre = new Dictionary<int, DeepSkyPixelRate>();
            var temporalUpdates = new Dictionary<(int SourceIndex, int SampleIndex), double>();
            if (temporalTaus.Count > 0)
            {
                BuildTemporalDarkDeficits(
                    temporalTaus,
                    unresolvedDarkCumulativeTau,
                    admittedUnresolvedDiffuse,
                    projection,
                    settings,
                    temporalPost,
                    temporalPre,
                    temporalUpdates,
                    ref attenuatedPrePsf,
                    ref totKernel,
                    cancellationToken);
            }

            SoloScreenedDarkCloud? reusableSolo = null;
            if (hadNoPriorOverlap && lowerDeficits is null && soloPrecomputedByCloud is not null &&
                soloPrecomputedByCloud.TryGetValue(cloud, out var pre) &&
                !pre.SoloNativePixels.Any(cumulativeAttenuatedConvolved.ContainsKey) &&
                !pre.SoloNativePixels.Any(cumulativeTemporalAttenuated.ContainsKey))
            {
                reusableSolo = pre;
            }

            var clippedDelta = reusableSolo is not null
                ? reusableSolo.SoloClippedDelta
                : ConvolveDarkNegativePrePsf(
                    negPrePsf, projection, kernel, padding, paddedWidth, settings, ref totKernel, cancellationToken);
            var (attenuatedRetained, activeDarkPixels, darkFootprint) = reusableSolo is not null
                ? (reusableSolo.SoloRetained, reusableSolo.SoloActivePixels, reusableSolo.SoloFootprint)
                : SummarizeDarkDelta(cloud, clippedDelta, temporalPre, projection);

            if (attenuatedRetained <= 0d || activeDarkPixels.Count == 0 || darkFootprint is null)
            {
                var skipped = CreateSkipped(cloud.Item, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-outside-fov", DeepSkyPhotometryBasis.None, cloud.Geometry.Basis, cloud.Profile);
                if (settings.MinimumSignalToNoise > 0d && !admissionScreened)
                {
                    return skipped;
                }
                diagnostics.Add(skipped);
                return null;
            }

            var nativeDarkPixels = reusableSolo?.SoloNativePixels ?? CompleteBinSupport(activeDarkPixels, projection, settings);
            StellarSignalPrediction darkPrediction;
            if (reusableSolo is not null)
            {
                darkPrediction = reusableSolo.SoloPrediction;
            }
            else
            {
                HashSet<int>? variancePixels = null;
                if (lowerDeficits is not null)
                {
                    // The upper deficit supplies all possible signal. Only bins with
                    // guaranteed support contribute shot/read variance to the lower bound.
                    var mandatoryClipped = ConvolveDarkNegativePrePsf(
                        lowerDeficits, projection, kernel, padding, paddedWidth, settings, ref totKernel, cancellationToken);
                    var mandatorySupport = mandatoryClipped.Keys.Concat(temporalPre.Keys).ToHashSet();
                    variancePixels = CompleteBinSupport(mandatorySupport, projection, settings);
                }
                else if (hygStars.Count > 0)
                {
                    fgHygRates ??= new Dictionary<int, DeepSkyPixelRate>();
                    fgStars ??= new HashSet<string>(StringComparer.Ordinal);
                    AccumulateForegroundHygRatesForAperture(
                        hygStars,
                        admittedStellarById,
                        cloud.Geometry,
                        nativeDarkPixels,
                        sceneRequest,
                        exposureStartUtc,
                        exposure,
                        projector,
                        projection,
                        renderOptions,
                        settings,
                        fgStars,
                        fgHygRates,
                        ref totSamples,
                        ref totKernel,
                        cancellationToken);
                }

                darkPrediction = EvaluateDarkSignalPrediction(
                    nativeDarkPixels,
                    clippedDelta,
                    temporalPost,
                    declaredDiffuseBg,
                    overlapDiffusePreCloud,
                    overlapDiffuseTemporal,
                    cumulativeAttenuatedConvolved,
                    cumulativeTemporalAttenuated,
                    rates,
                    temporalRates,
                    variancePixels is null ? fgHygRates : null,
                    projection,
                    renderOptions,
                    settings,
                    cancellationToken,
                    variancePixels);
            }

            // Only a certified sub-threshold prediction may eliminate a trial cloud.
            // Committing a retained set refines its diagnostics and pixels using the full budget.
            var admitted = admissionScreened || darkPrediction.SignalToNoise >= settings.MinimumSignalToNoise;
            var diagnostic = new DeepSkySourceDiagnostic(
                cloud.Item.Id, cloud.Item.DisplayName, cloud.Item.ObjectType, DeepSkyProfileClass.DarkNebula,
                Admitted: admitted, Reason: admitted ? "admitted-dark-attenuation" : "annotation-only-below-snr",
                DeepSkyPhotometryBasis.None, cloud.Geometry.Basis,
                cloud.Profile.ProfileSchemaVersion, cloud.Profile.MorphologyModel, cloud.Profile.Spectral.SpectralModel,
                cloud.Profile.Spectral.Origin, cloud.Profile.IsSimulationParameter,
                0d, 0d, 0d, 0d, 0, 0d, 0, admitted ? attenuatedRetained : 0d, darkPrediction,
                reportedSamples[cloud], activeDarkPixels.Count, false, darkFootprint);
            if (!admitted)
            {
                return diagnostic;
            }

            foreach (var (key, nextTau) in temporalUpdates)
            {
                unresolvedDarkCumulativeTau[key] = nextTau;
            }
            foreach (var (idx, delta) in clippedDelta)
            {
                AddUnboundedSparseRate(cumulativeAttenuatedConvolved, idx, delta);
            }
            foreach (var (idx, delta) in temporalPost)
            {
                AddUnboundedSparseRate(cumulativeTemporalAttenuated, idx, delta);
            }

            admCount++;
            darkCount++;
            diagnostics.Add(diagnostic);
            return null;
        }

        // Apply resolved clouds first (exact sum_k tau_k at shared subpixel directions).
        for (var r = 0; r < resolvedItems.Count; r++)
        {
            var sampled = resolvedItems[r];
            var negPrePsf = new Dictionary<long, DeepSkyPixelRate>();
            var attenuatedPrePsf = 0d;
            var hadPriorOverlap = false;

            if (sampled.ResolvedOwnSubTaus is not null)
            {
                foreach (var (paddedKey, ownSubTaus) in sampled.ResolvedOwnSubTaus)
                {
                    if (!resolvedTauByPixel.TryGetValue(paddedKey, out var summedTaus))
                    {
                        summedTaus = new double[ownSubTaus.Length];
                        resolvedTauByPixel.Add(paddedKey, summedTaus);
                    }
                    var prevT = runningTransmission.GetValueOrDefault(paddedKey, 1d);
                    if (prevT < 1d) hadPriorOverlap = true;
                    var absorbSum = 0d;
                    for (var s = 0; s < ownSubTaus.Length; s++)
                    {
                        var totalTau = summedTaus[s] + ownSubTaus[s];
                        summedTaus[s] = totalTau;
                        if (totalTau > 0d) absorbSum += 1d - Math.Exp(-totalTau);
                    }
                    var nextResT = Math.Clamp(1d - absorbSum * invSubSq, 0d, 1d);
                    if (nextResT >= 1d - 1e-15) nextResT = 1d;
                    resolvedTransmission[paddedKey] = nextResT;

                    var clampedT = Math.Min(prevT, nextResT);
                    var actualDrop = prevT - clampedT;
                    if (actualDrop <= 1e-15) continue;
                    runningTransmission[paddedKey] = clampedT;

                    var existingDiffuse = diffusePrePsf.GetValueOrDefault(paddedKey);
                    var rawBgMono = existingDiffuse.Mono + declaredDiffuseBg;
                    var rawBgR = existingDiffuse.Red + declaredDiffuseBg;
                    var rawBgG = existingDiffuse.Green + declaredDiffuseBg;
                    var rawBgB = existingDiffuse.Blue + declaredDiffuseBg;
                    if (rawBgMono <= 0 && rawBgR <= 0 && rawBgG <= 0 && rawBgB <= 0) continue;

                    var delta = new DeepSkyPixelRate(
                        -rawBgMono * actualDrop,
                        -rawBgR * actualDrop,
                        -rawBgG * actualDrop,
                        -rawBgB * actualDrop);
                    negPrePsf[paddedKey] = delta;
                    attenuatedPrePsf += -delta.Mono;
                }
            }

            var rejected = FinalizeSingleCloudDiagnostic(
                sampled, negPrePsf, lowerDeficits: null, attenuatedPrePsf, hadNoPriorOverlap: !hadPriorOverlap && unresolvedDarkCumulativeTau.Count == 0,
                ref foregroundHygTemporalRates, ref rasterizedForegroundStars,
                ref admittedCount, ref darkAttenuationCount,
                ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits);
            if (rejected is not null) return rejected;
        }

        // Apply canonical fine cloud groups in strict canonical order.
        for (var f = 0; f < fineGroups.Count; f++)
        {
            var canonicalGroup = fineGroups[f];
            var group = canonicalGroup.Group;
            var memberCount = canonicalGroup.Members.Count;

            if (memberCount == 1)
            {
                var sampled = canonicalGroup.Members[0];
                var negPrePsf = new Dictionary<long, DeepSkyPixelRate>();
                var lowerDeficits = screenOnly ? new Dictionary<long, DeepSkyPixelRate>() : null;
                var attenuatedPrePsf = 0d;
                var hadPriorOverlap = false;

                foreach (var (paddedKey, cell) in group.CellDeposits)
                {
                    var prevT = runningTransmission.GetValueOrDefault(paddedKey, 1d);
                    if (prevT < 1d) hadPriorOverlap = true;
                    var tResolved = resolvedTransmission.GetValueOrDefault(paddedKey, 1d);
                    activePriorFineByPixel.TryGetValue(paddedKey, out var activePriorList);
                    var appliedT = ApplyFineGroupTransmissionStep(
                        group,
                        cell,
                        paddedKey,
                        prevT,
                        tResolved,
                        resolvedItems,
                        activePriorList,
                        transmissionErrors?.GetValueOrDefault(paddedKey) ?? 0d,
                        out var conditionalFactorError);
                    var clampedT = Math.Min(prevT, Math.Clamp(appliedT, 0d, 1d));
                    if (clampedT >= 1d - 1e-15) clampedT = 1d;

                    if (cell.SoloAlpha > 0d)
                    {
                        if (activePriorList is null)
                        {
                            activePriorList = [];
                            activePriorFineByPixel[paddedKey] = activePriorList;
                        }
                        activePriorList.Add((group, cell));
                    }

                    AddFineDarkDeficit(paddedKey, cell, prevT, clampedT,
                        transmissionErrors?.GetValueOrDefault(paddedKey) ?? 0d, group.ScreeningTauError, conditionalFactorError,
                        negPrePsf, lowerDeficits, ref attenuatedPrePsf);
                }

                var rejected = FinalizeSingleCloudDiagnostic(
                    sampled, negPrePsf, lowerDeficits, attenuatedPrePsf,
                    hadNoPriorOverlap: !hadPriorOverlap && group.EvaluatedResolved.Count == 0 &&
                        group.EvaluatedFineGroups.Count == 0 && unresolvedDarkCumulativeTau.Count == 0,
                    ref foregroundHygTemporalRates, ref rasterizedForegroundStars,
                    ref admittedCount, ref darkAttenuationCount,
                    ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits);
                if (rejected is not null) return rejected;
            }
            else
            {
                // Coincident merged group: step through members sequentially against tBeforeGroup so
                // their incremental deficits sum exactly to the merged group's committed deficit with
                // zero extra cross-cloud evaluations.
                var tBeforeGroup = new Dictionary<long, double>(group.CellDeposits.Count);
                var errorsBeforeGroup = screenOnly ? new Dictionary<long, double>(group.CellDeposits.Count) : null;
                foreach (var paddedKey in group.CellDeposits.Keys)
                {
                    tBeforeGroup[paddedKey] = runningTransmission.GetValueOrDefault(paddedKey, 1d);
                    if (errorsBeforeGroup is not null)
                    {
                        errorsBeforeGroup[paddedKey] = transmissionErrors!.GetValueOrDefault(paddedKey);
                    }
                }

                var runningGroupTau = new double[group.Samples.Length];
                for (var mIdx = 0; mIdx < memberCount; mIdx++)
                {
                    var member = canonicalGroup.Members[mIdx];
                    var memberSamples = member.FineSamples ?? [];
                    for (var s = 0; s < runningGroupTau.Length && s < memberSamples.Length; s++)
                    {
                        runningGroupTau[s] += memberSamples[s].Tau;
                    }
                    var stepDeposits = mIdx + 1 == memberCount
                        ? group.CellDeposits
                        : BuildFineCellDeposits(group.Samples, runningGroupTau, group.PriorTau, settings.MaximumSparsePixels);

                    var negPrePsf = new Dictionary<long, DeepSkyPixelRate>();
                    var lowerDeficits = screenOnly ? new Dictionary<long, DeepSkyPixelRate>() : null;
                    var attenuatedPrePsf = 0d;
                    var hadPriorOverlap = mIdx > 0;

                    foreach (var (paddedKey, cell) in stepDeposits)
                    {
                        var prevT = runningTransmission.GetValueOrDefault(paddedKey, 1d);
                        var baseT = tBeforeGroup.GetValueOrDefault(paddedKey, 1d);
                        if (baseT < 1d) hadPriorOverlap = true;
                        var tResolved = resolvedTransmission.GetValueOrDefault(paddedKey, 1d);
                        activePriorFineByPixel.TryGetValue(paddedKey, out var activePriorList);
                        var appliedT = ApplyFineGroupTransmissionStep(
                            group,
                            cell,
                            paddedKey,
                            baseT,
                            tResolved,
                            resolvedItems,
                            activePriorList,
                            errorsBeforeGroup?.GetValueOrDefault(paddedKey) ?? 0d,
                            out var conditionalFactorError);
                        var clampedT = Math.Min(prevT, Math.Clamp(appliedT, 0d, 1d));
                        if (clampedT >= 1d - 1e-15) clampedT = 1d;
                        AddFineDarkDeficit(paddedKey, cell, prevT, clampedT,
                            errorsBeforeGroup?.GetValueOrDefault(paddedKey) ?? 0d, group.ScreeningTauError, conditionalFactorError,
                            negPrePsf, lowerDeficits, ref attenuatedPrePsf);
                    }

                    var rejected = FinalizeSingleCloudDiagnostic(
                        member, negPrePsf, lowerDeficits, attenuatedPrePsf,
                        hadNoPriorOverlap: !hadPriorOverlap && group.EvaluatedResolved.Count == 0 &&
                            group.EvaluatedFineGroups.Count == 0 && unresolvedDarkCumulativeTau.Count == 0,
                        ref foregroundHygTemporalRates, ref rasterizedForegroundStars,
                        ref admittedCount, ref darkAttenuationCount,
                        ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits);
                    if (rejected is not null) return rejected;
                }

                foreach (var (paddedKey, cell) in group.CellDeposits)
                {
                    if (cell.SoloAlpha <= 0d) continue;
                    if (!activePriorFineByPixel.TryGetValue(paddedKey, out var activePriorList))
                    {
                        activePriorList = [];
                        activePriorFineByPixel[paddedKey] = activePriorList;
                    }
                    activePriorList.Add((group, cell));
                }
            }
        }

        if (screenOnly)
        {
            return null;
        }

        // Publish the validated transmission and track its pre-PSF sparse pixels.
        foreach (var (paddedKey, nextT) in runningTransmission)
        {
            if (nextT >= 1d - 1e-15)
            {
                cumulativeTransmission.Remove(paddedKey);
                continue;
            }
            if (!prePsfSparsePixels.Contains(paddedKey) &&
                prePsfSparsePixels.Count >= settings.MaximumSparsePixels)
            {
                throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
            }
            prePsfSparsePixels.Add(paddedKey);
            cumulativeTransmission[paddedKey] = nextT;
        }
        return null;
    }

    private static double ApplyFineGroupTransmissionStep(
        AdmittedFineDarkCloudGroup group,
        FineCellDeposit cell,
        long paddedKey,
        double currentT,
        double tResolved,
        List<PreSampledDarkCloud> admittedResolvedClouds,
        List<(AdmittedFineDarkCloudGroup Group, FineCellDeposit Cell)>? activePriorFine,
        double currentTransmissionError,
        out double conditionalFactorError)
    {
        conditionalFactorError = 0d;
        var soloAlpha = cell.SoloAlpha;
        if (soloAlpha <= 0d) return currentT;

        var groupGeometry = group.RepresentativeCloud.Geometry;
        var anyPriorAccountedInPixel = false;
        var anyPriorUnevaluatedOverlap = false;
        var anyResolvedActiveAccounted = false;
        var anyResolvedActiveUneval = false;
        var accountedResolvedCount = 0;
        var singleAccountedResolvedT = 1d;

        if (tResolved < 1d && admittedResolvedClouds.Count > 0)
        {
            for (var r = 0; r < admittedResolvedClouds.Count; r++)
            {
                var rc = admittedResolvedClouds[r].Cloud;
                if (rc.StandaloneTransmission is null ||
                    !rc.StandaloneTransmission.TryGetValue(paddedKey, out var rcT) ||
                    rcT >= 1d)
                {
                    continue;
                }
                if (group.EvaluatedResolved.Contains(rc) ||
                    !DarkCloudsMayOverlapOnSky(rc.Geometry, groupGeometry))
                {
                    anyResolvedActiveAccounted = true;
                    accountedResolvedCount++;
                    singleAccountedResolvedT = rcT;
                }
                else
                {
                    anyResolvedActiveUneval = true;
                }
            }

            if (anyResolvedActiveAccounted) anyPriorAccountedInPixel = true;
            if (anyResolvedActiveUneval) anyPriorUnevaluatedOverlap = true;
        }

        if (activePriorFine is not null)
        {
            for (var p = 0; p < activePriorFine.Count; p++)
            {
                var priorGroup = activePriorFine[p].Group;
                if (group.EvaluatedFineGroups.Contains(priorGroup) ||
                    !DarkCloudsMayOverlapOnSky(priorGroup.RepresentativeCloud.Geometry, groupGeometry))
                {
                    anyPriorAccountedInPixel = true;
                }
                else
                {
                    anyPriorUnevaluatedOverlap = true;
                }
            }
        }

        var residualAlpha = cell.ResidualAlpha;
        if (!anyPriorUnevaluatedOverlap)
        {
            // Every active prior layer in this pixel was cross-evaluated or proven sky-disjoint.
            var exactNextT = Math.Clamp(currentT - residualAlpha, 0d, 1d);
            return Math.Min(exactNextT, 1d - soloAlpha);
        }

        if (!anyPriorAccountedInPixel)
        {
            // No active prior layer in this pixel was cross-evaluated or proven sky-disjoint:
            // compose strictly multiplicatively so budget-capped overlapping pairs never brighten pixels.
            return Math.Clamp(currentT * (1d - soloAlpha), 0d, 1d);
        }

        // Mixed case (both accounted and unevaluated overlapping prior layers active in this pixel):
        // apply exact conditional-independence scaling currentT / tAccounted so adding any unevaluated
        // overlapping layer monotonically darkens the pixel and recovers exact Beer-Lambert transmission
        // for concentric clouds (T(c3,c2) * T(c2,c1) / T(c2) = exp(-(tau1+tau2+tau3))).
        var tAcc = 1d;
        var tAccError = 0d;
        if (anyResolvedActiveAccounted)
        {
            if (!anyResolvedActiveUneval)
            {
                tAcc = tResolved;
            }
            else if (accountedResolvedCount == 1)
            {
                tAcc = singleAccountedResolvedT;
            }
            else
            {
                tAcc = ComputeAccountedResolvedTransmission(group, paddedKey, admittedResolvedClouds);
            }
        }

        if (activePriorFine is not null)
        {
            for (var p = 0; p < activePriorFine.Count; p++)
            {
                var (pg, pgCell) = activePriorFine[p];
                if (!group.EvaluatedFineGroups.Contains(pg) &&
                    DarkCloudsMayOverlapOnSky(pg.RepresentativeCloud.Geometry, groupGeometry))
                {
                    continue;
                }

                // Check whether pg's own ResidualAlpha depends on any prior layer NOT accounted for by group.
                var pgDependsOnUnevalPrior = false;
                if (anyResolvedActiveUneval && pg.EvaluatedResolved.Count > 0)
                {
                    foreach (var rc in pg.EvaluatedResolved)
                    {
                        if (rc.StandaloneTransmission is not null &&
                            rc.StandaloneTransmission.TryGetValue(paddedKey, out var rcT) &&
                            rcT < 1d &&
                            !group.EvaluatedResolved.Contains(rc) &&
                            DarkCloudsMayOverlapOnSky(rc.Geometry, groupGeometry))
                        {
                            pgDependsOnUnevalPrior = true;
                            break;
                        }
                    }
                }
                if (!pgDependsOnUnevalPrior && pg.EvaluatedFineGroups.Count > 0)
                {
                    for (var q = 0; q < p; q++)
                    {
                        var earlier = activePriorFine[q].Group;
                        if (pg.EvaluatedFineGroups.Contains(earlier) &&
                            !group.EvaluatedFineGroups.Contains(earlier) &&
                            DarkCloudsMayOverlapOnSky(earlier.RepresentativeCloud.Geometry, groupGeometry))
                        {
                            pgDependsOnUnevalPrior = true;
                            break;
                        }
                    }
                }

                if (!pgDependsOnUnevalPrior)
                {
                    tAcc = Math.Min(Math.Clamp(tAcc - pgCell.ResidualAlpha, 0d, 1d), 1d - pgCell.SoloAlpha);
                    tAccError += Math.Min(1d, pg.ScreeningTauError) * pgCell.SoloAlpha;
                }
                else
                {
                    tAcc = Math.Clamp(tAcc * (1d - pgCell.SoloAlpha), 0d, 1d);
                }
            }
        }

        tAcc = Math.Clamp(tAcc, currentT, 1d);
        var conditionalFactor = tAcc > 1e-15 ? Math.Clamp(currentT / tAcc, 0d, 1d) : 0d;
        tAccError = Math.Max(tAccError, currentTransmissionError);
        if (tAccError > 0d)
        {
            var minAccounted = Math.Max(0d, tAcc - tAccError);
            var maxAccounted = Math.Min(1d, tAcc + tAccError);
            var minCurrent = Math.Max(0d, currentT - currentTransmissionError);
            var maxCurrent = Math.Min(1d, currentT + currentTransmissionError);
            var lowerFactor = minAccounted > 1e-15 ? Math.Min(1d, minCurrent / maxAccounted) : 0d;
            var upperFactor = maxAccounted <= 1e-15 ? 0d :
                minAccounted <= 1e-15 ? 1d : Math.Min(1d, maxCurrent / minAccounted);
            conditionalFactorError = Math.Max(conditionalFactor - lowerFactor, upperFactor - conditionalFactor);
        }

        var nextT = Math.Clamp(currentT - conditionalFactor * residualAlpha, 0d, 1d);
        return Math.Min(nextT, 1d - soloAlpha);
    }

    private static double ComputeAccountedResolvedTransmission(
        AdmittedFineDarkCloudGroup group,
        long paddedKey,
        List<PreSampledDarkCloud> resolvedClouds)
    {
        // The resolved grid uses at most 8 x 8 common directions per pixel.
        Span<double> summedTaus = stackalloc double[64];
        summedTaus.Clear();
        var subpixelCount = 0;
        foreach (var resolved in resolvedClouds)
        {
            if (resolved.ResolvedOwnSubTaus is not { } tauByPixel ||
                !tauByPixel.TryGetValue(paddedKey, out var ownTaus) ||
                (!group.EvaluatedResolved.Contains(resolved.Cloud) &&
                 DarkCloudsMayOverlapOnSky(resolved.Cloud.Geometry, group.RepresentativeCloud.Geometry)))
            {
                continue;
            }

            subpixelCount = ownTaus.Length;
            for (var i = 0; i < ownTaus.Length; i++)
            {
                summedTaus[i] += ownTaus[i];
            }
        }

        var transmission = 0d;
        for (var i = 0; i < subpixelCount; i++)
        {
            transmission += Math.Exp(-summedTaus[i]);
        }
        return transmission / subpixelCount;
    }

    private static Dictionary<long, FineCellDeposit> BuildFineCellDeposits(
        FineDarkCloudSample[] samples,
        double[] combinedTau,
        double[] priorTau,
        int maximumSparsePixels)
    {
        var deposits = new Dictionary<long, FineCellDeposit>();
        for (var m = 0; m < samples.Length; m++)
        {
            var s = samples[m];
            var ownAbsorbFraction = 1d - Math.Exp(-combinedTau[m]);
            var soloAbsorbArea = s.PixelArea * ownAbsorbFraction;
            var priorAttenAbsorbArea = soloAbsorbArea * Math.Exp(-priorTau[m]);
            if (!deposits.TryGetValue(s.PaddedPixelKey, out var prior))
            {
                if (deposits.Count >= maximumSparsePixels)
                {
                    throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
                }
                deposits[s.PaddedPixelKey] = new FineCellDeposit(s.PixelArea, soloAbsorbArea, priorAttenAbsorbArea);
            }
            else
            {
                deposits[s.PaddedPixelKey] = new FineCellDeposit(
                    prior.AreaSum + s.PixelArea,
                    prior.SoloAbsorbAreaSum + soloAbsorbArea,
                    prior.PriorAttenuatedAbsorbAreaSum + priorAttenAbsorbArea);
            }
        }
        return deposits;
    }

    private static FineDarkCloudSample[] SampleFineDarkCloudOnce(
        AdmittedDarkCloud cloud,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        int ownSampleCap,
        int padding,
        long paddedWidth,
        ref long totalSampledPoints,
        ref long totalOutlineEdgeVisits,
        out int interiorSampleCount,
        CancellationToken cancellationToken)
    {
        var geometry = cloud.Geometry;
        var profile = cloud.Profile;
        var effectiveCap = Math.Clamp(ownSampleCap, 1, settings.MaximumSamplePointsPerSource);
        var angularScale = ProjectionSamplingBounds.MaximumPixelsPerRadian(
            projection, geometry.GeometricCenter, Math.Max(1e-6, geometry.SemiMajorRadians));
        var maxAxis = Math.Max(1, (int)Math.Floor(Math.Sqrt(effectiveCap)));
        var minAxis = Math.Min(20, maxAxis);
        interiorSampleCount = 0;
        var samples = new List<FineDarkCloudSample>();

        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            var frame = cloud.OutlineFrame ?? ComputeOutlineOrientedFrame(
                rings, geometry.OutlineMinU, geometry.OutlineMaxU, geometry.OutlineMinV, geometry.OutlineMaxV,
                settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits, cancellationToken);
            var spanS = frame.SpanS;
            var spanT = frame.SpanT;
            if (spanS <= 0d || spanT <= 0d) return [];

            var nu = (int)Math.Clamp(Math.Ceiling(spanS * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(spanT * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            ClampGridToBudget(ref nu, ref nv, effectiveCap);

            var edgeCount = CountOutlineEdges(rings);
            ChargeOutlineEdgeVisits((long)nu * nv * edgeCount, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);

            var localEdges = BuildLocalOutlineEdges(rings, frame);
            if (localEdges.Count == 0) return [];

            var ds = spanS / nu;
            var dt = spanT / nv;
            var rowEdges = new List<LocalOutlineEdge>(localEdges.Count);
            var cellEdges = new List<LocalOutlineEdge>();
            var criticalT = new List<double>();
            var scanCrossings = new List<ScanlineEdgeCrossing>();
            var interior = new List<(double U, double V, double Dist, double ProjectedPixelArea)>();
            var maxDist = cloud.MaxOutlineDist;

            for (var iy = 0; iy < nv; iy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tLow = frame.MinT + iy * dt;
                var tHigh = iy + 1 == nv ? frame.MaxT : tLow + dt;
                rowEdges.Clear();
                for (var e = 0; e < localEdges.Count; e++)
                {
                    var edge = localEdges[e];
                    if (edge.MinT < tHigh && edge.MaxT > tLow) rowEdges.Add(edge);
                }
                if (rowEdges.Count == 0) continue;

                for (var ix = 0; ix < nu; ix++)
                {
                    var sLow = frame.MinS + ix * ds;
                    var sHigh = ix + 1 == nu ? frame.MaxS : sLow + ds;
                    var coveredArea = ComputeOutlineCellCoverage(
                        rings,
                        frame,
                        geometry.Frame,
                        projector,
                        rowEdges,
                        cellEdges,
                        criticalT,
                        scanCrossings,
                        sLow,
                        sHigh,
                        tLow,
                        tHigh,
                        edgeCount,
                        settings.MaximumOutlineEdgeVisits,
                        ref totalOutlineEdgeVisits,
                        out var projectedPixelArea,
                        out var repU,
                        out var repV);
                    if (coveredArea <= 0d) continue;

                    if (totalSampledPoints >= settings.MaximumTotalSamplePoints)
                    {
                        throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
                    }
                    totalSampledPoints++;
                    interiorSampleCount++;
                    ChargeOutlineEdgeVisits(edgeCount, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                    var dist = DeepSkyOutlineSampler.DistanceToTangentBoundary(rings, repU, repV);
                    if (dist > maxDist) maxDist = dist;
                    interior.Add((repU, repV, dist, projectedPixelArea));
                }
            }
            if (maxDist <= 0d) return [];
            if (maxDist > cloud.MaxOutlineDist) cloud.MaxOutlineDist = maxDist;

            foreach (var (u, v, dist, pixelArea) in interior)
            {
                var tau = profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateOutlineDensity(profile, dist / maxDist, 0d);
                if (tau <= 0d || pixelArea <= 0d) continue;
                var direction = (geometry.Frame.Center + geometry.Frame.CelestialEast * u + geometry.Frame.CelestialNorth * v).Normalize();
                if (!projector.TryProject(direction, out var pixel)) continue;
                var cx = (long)Math.Floor(pixel.X);
                var cy = (long)Math.Floor(pixel.Y);
                if (cx < -padding || cx >= projection.WidthPixels + padding ||
                    cy < -padding || cy >= projection.HeightPixels + padding)
                {
                    continue;
                }
                var key = (cy + padding) * paddedWidth + cx + padding;
                samples.Add(new FineDarkCloudSample(direction, key, pixelArea, tau));
            }
        }
        else
        {
            var nu = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMajorRadians * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMinorRadians * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            ClampGridToBudget(ref nu, ref nv, effectiveCap);
            var du = 2d / nu;
            var dv = 2d / nv;
            var cellArea = du * dv * geometry.SemiMajorRadians * geometry.SemiMinorRadians;
            for (var iy = 0; iy < nv; iy++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var v = -1d + (iy + 0.5) * dv;
                for (var ix = 0; ix < nu; ix++)
                {
                    var u = -1d + (ix + 0.5) * du;
                    var qSq = u * u + v * v;
                    if (qSq >= 1d) continue;
                    if (totalSampledPoints >= settings.MaximumTotalSamplePoints)
                    {
                        throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
                    }
                    totalSampledPoints++;
                    interiorSampleCount++;
                    var tau = profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateRadialDensity(profile, Math.Sqrt(qSq), 0d, 0UL);
                    if (tau <= 0d) continue;
                    var tangent = geometry.MajorAxis * (u * geometry.SemiMajorRadians) +
                        geometry.MinorAxis * (v * geometry.SemiMinorRadians);
                    var direction = geometry.Frame.Direction(tangent);
                    if (!projector.TryProject(direction, out var pixel)) continue;
                    var cx = (long)Math.Floor(pixel.X);
                    var cy = (long)Math.Floor(pixel.Y);
                    if (cx < -padding || cx >= projection.WidthPixels + padding ||
                        cy < -padding || cy >= projection.HeightPixels + padding)
                    {
                        continue;
                    }
                    // Always evaluate local projection area scale at each cell's own sky direction (never reuse catalog center).
                    var px2PerSr = MeasureLocalAreaPixelsPerSteradian(projector, direction);
                    if (px2PerSr is not { } areaScale || areaScale <= 0d) continue;
                    var r = tangent.Length;
                    var coveredFraction = UnitCircleCellFraction(u, v, du, dv);
                    var solidAngle = cellArea * coveredFraction * (r < 1e-8 ? 1d : Math.Sin(r) / r);
                    var pixelArea = solidAngle * areaScale;
                    if (pixelArea <= 0d) continue;
                    var key = (cy + padding) * paddedWidth + cx + padding;
                    samples.Add(new FineDarkCloudSample(direction, key, pixelArea, tau));
                }
            }
        }

        return samples.ToArray();
    }

    private static double UnitCircleCellFraction(double u, double v, double width, double height)
    {
        // The disk is symmetric about both axes. Reflect the cell center into the first quadrant.
        var left = Math.Abs(u) - width / 2d;
        var right = left + width;
        var bottom = Math.Abs(v) - height / 2d;
        var top = bottom + height;
        if (right * right + top * top <= 1d)
        {
            return 1d;
        }

        var area = UnitCircleQuadrantCellArea(Math.Max(0d, left), right, Math.Max(0d, bottom), top);
        if (left < 0d)
        {
            area += UnitCircleQuadrantCellArea(0d, -left, Math.Max(0d, bottom), top);
        }
        if (bottom < 0d)
        {
            area += UnitCircleQuadrantCellArea(Math.Max(0d, left), right, 0d, -bottom);
            if (left < 0d)
            {
                area += UnitCircleQuadrantCellArea(0d, -left, 0d, -bottom);
            }
        }
        return Math.Clamp(area / (width * height), 0d, 1d);
    }

    private static double UnitCircleQuadrantCellArea(double left, double right, double bottom, double top)
    {
        // Inside the first quadrant, the circle's upper boundary decreases with x.
        var end = Math.Min(right, Math.Sqrt(Math.Max(0d, 1d - bottom * bottom)));
        if (end <= left)
        {
            return 0d;
        }

        var fullEnd = Math.Clamp(Math.Sqrt(Math.Max(0d, 1d - top * top)), left, end);
        var fullArea = (fullEnd - left) * (top - bottom);
        return fullArea + CircleIntegral(end) - CircleIntegral(fullEnd) - bottom * (end - fullEnd);

        static double CircleIntegral(double x) => 0.5d * (x * Math.Sqrt(Math.Max(0d, 1d - x * x)) + Math.Asin(x));
    }

    private static List<LocalOutlineEdge> BuildLocalOutlineEdges(
        IReadOnlyList<PixelPoint[]> rings,
        OutlineOrientedFrame frame)
    {
        var edges = new List<LocalOutlineEdge>();
        for (var r = 0; r < rings.Count; r++)
        {
            var ring = rings[r];
            for (var i = 0; i < ring.Length - 1; i++)
            {
                var (s0, t0) = frame.ToLocal(ring[i].X, ring[i].Y);
                var (s1, t1) = frame.ToLocal(ring[i + 1].X, ring[i + 1].Y);
                if (Math.Abs(t1 - t0) <= 1e-18) continue;
                edges.Add(new LocalOutlineEdge(
                    s0, t0, s1, t1,
                    Math.Min(s0, s1), Math.Max(s0, s1),
                    Math.Min(t0, t1), Math.Max(t0, t1)));
            }
        }
        return edges;
    }

    private static bool TryComputeProjectedLocalQuadArea(
        OutlineOrientedFrame frame,
        ResolvedSourceFrame geoFrame,
        ResolvedSourceProjector projector,
        double s0, double t0,
        double s1, double t1,
        double s2, double t2,
        double s3, double t3,
        out double pixelArea)
    {
        pixelArea = 0d;
        var (u0, v0) = frame.ToTangent(s0, t0);
        var (u1, v1) = frame.ToTangent(s1, t1);
        var (u2, v2) = frame.ToTangent(s2, t2);
        var (u3, v3) = frame.ToTangent(s3, t3);
        var d0 = (geoFrame.Center + geoFrame.CelestialEast * u0 + geoFrame.CelestialNorth * v0).Normalize();
        var d1 = (geoFrame.Center + geoFrame.CelestialEast * u1 + geoFrame.CelestialNorth * v1).Normalize();
        var d2 = (geoFrame.Center + geoFrame.CelestialEast * u2 + geoFrame.CelestialNorth * v2).Normalize();
        var d3 = (geoFrame.Center + geoFrame.CelestialEast * u3 + geoFrame.CelestialNorth * v3).Normalize();
        if (!projector.TryProject(d0, out var p0) ||
            !projector.TryProject(d1, out var p1) ||
            !projector.TryProject(d2, out var p2) ||
            !projector.TryProject(d3, out var p3))
        {
            return false;
        }
        var a1 = Math.Abs((p1.X - p0.X) * (p2.Y - p0.Y) - (p1.Y - p0.Y) * (p2.X - p0.X));
        var a2 = Math.Abs((p2.X - p0.X) * (p3.Y - p0.Y) - (p2.Y - p0.Y) * (p3.X - p0.X));
        var area = 0.5 * (a1 + a2);
        if (!double.IsFinite(area) || area < 0d) return false;
        pixelArea = area;
        return true;
    }

    private static double ComputeLocalProjectedStripAreaFallback(
        OutlineOrientedFrame frame,
        ResolvedSourceFrame geoFrame,
        ResolvedSourceProjector projector,
        double stripS,
        double stripT,
        double tangentArea)
    {
        var (u, v) = frame.ToTangent(stripS, stripT);
        var dir = (geoFrame.Center + geoFrame.CelestialEast * u + geoFrame.CelestialNorth * v).Normalize();
        var px2PerSr = MeasureLocalAreaPixelsPerSteradian(projector, dir);
        if (px2PerSr is not { } areaScale || areaScale <= 0d) return 0d;
        var solidAngle = tangentArea * Math.Pow(1d + u * u + v * v, -1.5);
        return solidAngle * areaScale;
    }

    private static double ComputeOutlineCellCoverage(
        IReadOnlyList<PixelPoint[]> rings,
        OutlineOrientedFrame frame,
        ResolvedSourceFrame geoFrame,
        ResolvedSourceProjector projector,
        List<LocalOutlineEdge> rowEdges,
        List<LocalOutlineEdge> cellEdges,
        List<double> criticalT,
        List<ScanlineEdgeCrossing> scanCrossings,
        double sLow,
        double sHigh,
        double tLow,
        double tHigh,
        long edgeCount,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits,
        out double projectedPixelArea,
        out double repU,
        out double repV)
    {
        projectedPixelArea = 0d;
        repU = 0d;
        repV = 0d;
        var ds = sHigh - sLow;
        var dt = tHigh - tLow;
        var cellFullArea = ds * dt;
        if (cellFullArea <= 0d) return 0d;

        var anyLeftOfRight = false;
        var anyRightOfLeft = false;
        cellEdges.Clear();
        for (var i = 0; i < rowEdges.Count; i++)
        {
            var e = rowEdges[i];
            if (e.MinS < sHigh) anyLeftOfRight = true;
            if (e.MaxS > sLow) anyRightOfLeft = true;
            if (e.MinS <= sHigh && e.MaxS >= sLow)
            {
                cellEdges.Add(e);
            }
        }
        if (!anyLeftOfRight || !anyRightOfLeft) return 0d;

        if (cellEdges.Count == 0)
        {
            // No edge intersects this cell: cell is either 100% inside or 100% outside.
            var sMid = 0.5 * (sLow + sHigh);
            var tMid = 0.5 * (tLow + tHigh);
            var inside = false;
            for (var i = 0; i < rowEdges.Count; i++)
            {
                var e = rowEdges[i];
                if ((e.T0 > tMid) != (e.T1 > tMid))
                {
                    var crossS = e.S0 + (tMid - e.T0) * (e.S1 - e.S0) / (e.T1 - e.T0);
                    if (crossS < sMid) inside = !inside;
                }
            }
            if (!inside) return 0d;
            (repU, repV) = frame.ToTangent(sMid, tMid);
            projectedPixelArea = TryComputeProjectedLocalQuadArea(
                frame, geoFrame, projector,
                sLow, tLow, sHigh, tLow, sHigh, tHigh, sLow, tHigh, out var quadArea)
                ? quadArea
                : ComputeLocalProjectedStripAreaFallback(frame, geoFrame, projector, sMid, tMid, cellFullArea);
            return cellFullArea;
        }

        // Exact critical-t scanline even-odd area integration over Polygon ∩ [sLow, sHigh] × [tLow, tHigh],
        // projecting each clipped trapezoid locally to sensor pixels so off-axis perspective stretch is exact.
        criticalT.Clear();
        criticalT.Add(tLow);
        criticalT.Add(tHigh);
        for (var i = 0; i < cellEdges.Count; i++)
        {
            var e = cellEdges[i];
            if (e.T0 > tLow && e.T0 < tHigh) criticalT.Add(e.T0);
            if (e.T1 > tLow && e.T1 < tHigh) criticalT.Add(e.T1);
            var dse = e.S1 - e.S0;
            if (Math.Abs(dse) > 1e-18)
            {
                var alphaLow = (sLow - e.S0) / dse;
                if (alphaLow > 0d && alphaLow < 1d)
                {
                    var tc = e.T0 + alphaLow * (e.T1 - e.T0);
                    if (tc > tLow && tc < tHigh) criticalT.Add(tc);
                }
                var alphaHigh = (sHigh - e.S0) / dse;
                if (alphaHigh > 0d && alphaHigh < 1d)
                {
                    var tc = e.T0 + alphaHigh * (e.T1 - e.T0);
                    if (tc > tLow && tc < tHigh) criticalT.Add(tc);
                }
            }
        }
        if (cellEdges.Count <= 32)
        {
            for (var i = 0; i < cellEdges.Count; i++)
            {
                var a = cellEdges[i];
                var dsa = a.S1 - a.S0;
                var dta = a.T1 - a.T0;
                for (var j = i + 1; j < cellEdges.Count; j++)
                {
                    var b = cellEdges[j];
                    var dsb = b.S1 - b.S0;
                    var dtb = b.T1 - b.T0;
                    var denom = dsa * dtb - dta * dsb;
                    if (Math.Abs(denom) <= 1e-24) continue;
                    var rs = b.S0 - a.S0;
                    var rt = b.T0 - a.T0;
                    var ua = (rs * dtb - rt * dsb) / denom;
                    var ub = (rs * dta - rt * dsa) / denom;
                    if (ua > 0d && ua < 1d && ub > 0d && ub < 1d)
                    {
                        var tx = a.T0 + ua * dta;
                        var sx = a.S0 + ua * dsa;
                        if (tx > tLow && tx < tHigh && sx >= sLow && sx <= sHigh)
                        {
                            criticalT.Add(tx);
                        }
                    }
                }
            }
        }

        criticalT.Sort();
        var coveredArea = 0d;
        var sumAreaS = 0d;
        var sumAreaT = 0d;
        var bestStripArea = 0d;
        var bestS = 0d;
        var bestT = 0d;
        var minSlabHeight = 1e-15 * Math.Max(1d, dt);

        for (var k = 0; k + 1 < criticalT.Count; k++)
        {
            var yA = criticalT[k];
            var yB = criticalT[k + 1];
            var h = yB - yA;
            if (h <= minSlabHeight) continue;
            var yM = 0.5 * (yA + yB);

            ChargeOutlineEdgeVisits(rowEdges.Count, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            scanCrossings.Clear();
            for (var i = 0; i < rowEdges.Count; i++)
            {
                var e = rowEdges[i];
                if ((e.T0 > yM) != (e.T1 > yM))
                {
                    var slope = (e.S1 - e.S0) / (e.T1 - e.T0);
                    scanCrossings.Add(new ScanlineEdgeCrossing(
                        e.S0 + (yM - e.T0) * slope,
                        e.S0 + (yA - e.T0) * slope,
                        e.S0 + (yB - e.T0) * slope));
                }
            }
            if (scanCrossings.Count < 2) continue;
            scanCrossings.Sort(static (left, right) => left.SMid.CompareTo(right.SMid));

            for (var p = 0; p + 1 < scanCrossings.Count; p += 2)
            {
                var leftCross = scanCrossings[p];
                var rightCross = scanCrossings[p + 1];
                var lo = Math.Max(leftCross.SMid, sLow);
                var hi = Math.Min(rightCross.SMid, sHigh);
                if (hi <= lo) continue;
                var stripWidth = hi - lo;
                var stripArea = stripWidth * h;
                var stripS = 0.5 * (lo + hi);
                coveredArea += stripArea;
                sumAreaS += stripArea * stripS;
                sumAreaT += stripArea * yM;
                if (stripArea > bestStripArea)
                {
                    bestStripArea = stripArea;
                    bestS = stripS;
                    bestT = yM;
                }

                var loA = Math.Clamp(leftCross.SA, sLow, sHigh);
                var hiA = Math.Clamp(rightCross.SA, sLow, sHigh);
                var loB = Math.Clamp(leftCross.SB, sLow, sHigh);
                var hiB = Math.Clamp(rightCross.SB, sLow, sHigh);
                var trapConsistent = loA <= hiA + 1e-12 * Math.Max(ds, 1e-12) &&
                    loB <= hiB + 1e-12 * Math.Max(ds, 1e-12) &&
                    Math.Abs(0.5 * ((hiA - loA) + (hiB - loB)) - stripWidth) <= 1e-6 * Math.Max(ds, 1e-12);
                if (trapConsistent)
                {
                    if (loA > hiA) loA = hiA = 0.5 * (loA + hiA);
                    if (loB > hiB) loB = hiB = 0.5 * (loB + hiB);
                    if (TryComputeProjectedLocalQuadArea(
                        frame, geoFrame, projector,
                        loA, yA, hiA, yA, hiB, yB, loB, yB, out var stripPxArea))
                    {
                        projectedPixelArea += stripPxArea;
                        continue;
                    }
                }
                projectedPixelArea += ComputeLocalProjectedStripAreaFallback(
                    frame, geoFrame, projector, stripS, yM, stripArea);
            }
        }

        if (coveredArea <= 1e-15 * cellFullArea || bestStripArea <= 0d)
        {
            projectedPixelArea = 0d;
            return 0d;
        }
        coveredArea = Math.Min(coveredArea, cellFullArea);

        var (candU, candV) = frame.ToTangent(sumAreaS / coveredArea, sumAreaT / coveredArea);
        ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
        if (DeepSkyOutlineSampler.ContainsTangentPoint(rings, candU, candV))
        {
            repU = candU;
            repV = candV;
        }
        else
        {
            (repU, repV) = frame.ToTangent(bestS, bestT);
        }
        return coveredArea;
    }

    private static bool TryUnprojectPadded(
        double px,
        double py,
        ProjectionContext projection,
        CameraBasis basis,
        HorizonPolicy horizonPolicy,
        RefractionOptions refraction,
        out EnuVector geometricDirection)
    {
        geometricDirection = default;
        if (!double.IsFinite(px) || !double.IsFinite(py)) return false;
        var nx = (px - projection.PrincipalPointX) / projection.FocalLengthXPixels;
        var ny = (projection.PrincipalPointY - py) / projection.FocalLengthYPixels;
        var rDist = Math.Sqrt(nx * nx + ny * ny);
        if (projection.RadialDistortionK1 != 0 && rDist > 0)
        {
            var rIdeal = RadialDistortion.Undistort(rDist, projection.Model, projection.RadialDistortionK1);
            if (!double.IsFinite(rIdeal)) return false;
            var scale = rIdeal / rDist;
            nx *= scale;
            ny *= scale;
            rDist = rIdeal;
        }

        EnuVector camera;
        if (projection.Model == ProjectionModel.Perspective)
        {
            camera = new EnuVector(nx, ny, 1d).Normalize();
        }
        else
        {
            double theta;
            switch (projection.Model)
            {
                case ProjectionModel.EquidistantFisheye:
                    if (rDist > Math.PI + 1e-12) return false;
                    theta = Math.Min(rDist, Math.PI);
                    break;
                case ProjectionModel.EquisolidFisheye:
                    if (rDist > 2d + 1e-12) return false;
                    theta = 2d * Math.Asin(Math.Clamp(rDist / 2d, 0d, 1d));
                    break;
                case ProjectionModel.OrthographicFisheye:
                    if (rDist > 1d + 1e-12) return false;
                    theta = Math.Asin(Math.Clamp(rDist, 0d, 1d));
                    break;
                case ProjectionModel.StereographicFisheye:
                    theta = 2d * Math.Atan(rDist / 2d);
                    if (theta >= Math.PI - 1e-12) return false;
                    break;
                default:
                    return false;
            }
            var s = rDist <= 1e-15 ? 0d : Math.Sin(theta) / rDist;
            camera = new EnuVector(nx * s, ny * s, Math.Cos(theta)).Normalize();
        }

        var apparentEnu = basis.ToEnu(camera);
        if (!refraction.Enabled)
        {
            if (horizonPolicy == HorizonPolicy.GeometricHorizon && apparentEnu.Up < 0) return false;
            geometricDirection = apparentEnu;
            return true;
        }

        var apparentAltAz = CameraBasis.ToHorizontal(apparentEnu);
        double? geomAlt;
        if (horizonPolicy == HorizonPolicy.GeometricHorizon)
        {
            geomAlt = AtmosphericRefraction.RemoveAboveHorizon(apparentAltAz.AltitudeDegrees, refraction);
        }
        else
        {
            var appAlt = apparentAltAz.AltitudeDegrees;
            if (appAlt < refraction.MinimumAltitudeDegrees)
            {
                geomAlt = appAlt;
            }
            else
            {
                var low = refraction.MinimumAltitudeDegrees;
                var high = 90d;
                if (appAlt < AtmosphericRefraction.Apply(low, refraction) ||
                    appAlt > AtmosphericRefraction.Apply(high, refraction))
                {
                    geomAlt = null;
                }
                else
                {
                    for (var i = 0; i < 48; i++)
                    {
                        var mid = 0.5 * (low + high);
                        if (AtmosphericRefraction.Apply(mid, refraction) < appAlt) low = mid;
                        else high = mid;
                    }
                    geomAlt = 0.5 * (low + high);
                }
            }
        }
        if (geomAlt is null) return false;
        geometricDirection = CameraBasis.FromHorizontal(apparentAltAz with { AltitudeDegrees = geomAlt.Value });
        return true;
    }

    private static double EvaluateDarkCloudOpticalDepthAtTime(
        AdmittedDarkCloud cloud,
        EquatorialPoint emitterJ2000,
        EnuVector emitterMidpointGeometricDirection,
        DateTimeOffset sampleUtc,
        VisibleSceneRequest sceneRequest,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        if (sampleUtc == sceneRequest.Utc)
        {
            return EvaluateDarkCloudOpticalDepth(
                cloud, emitterMidpointGeometricDirection, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
        }

        var emitterHoriz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(emitterJ2000, sampleUtc),
            sampleUtc,
            sceneRequest.Observer.LatitudeDegrees,
            sceneRequest.Observer.LongitudeDegrees);
        var emitterDir = CameraBasis.FromHorizontal(emitterHoriz);

        var cloudJ2000 = new EquatorialPoint(cloud.Item.RightAscensionHours, cloud.Item.DeclinationDegrees);
        var cloudHoriz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(cloudJ2000, sampleUtc),
            sampleUtc,
            sceneRequest.Observer.LatitudeDegrees,
            sceneRequest.Observer.LongitudeDegrees);
        var cloudFrame = ResolvedSourceFrame.Create(cloudHoriz, sceneRequest.Observer.LatitudeDegrees);

        var geometry = cloud.Geometry;
        var profile = cloud.Profile;
        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            if (cloud.MaxOutlineDist <= 0) return 0d;
            var cosine = EnuVector.Dot(emitterDir, cloudFrame.Center);
            if (cosine <= DeepSkyOutlineSampler.MinimumTangentCosine) return 0d;
            var u = EnuVector.Dot(emitterDir, cloudFrame.CelestialEast) / cosine;
            var v = EnuVector.Dot(emitterDir, cloudFrame.CelestialNorth) / cosine;
            if (u < geometry.OutlineMinU || u > geometry.OutlineMaxU ||
                v < geometry.OutlineMinV || v > geometry.OutlineMaxV)
            {
                return 0d;
            }
            var edgeCount = CountOutlineEdges(rings);
            ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            if (!DeepSkyOutlineSampler.ContainsTangentPoint(rings, u, v)) return 0d;
            ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            var dist = DeepSkyOutlineSampler.DistanceToTangentBoundary(rings, u, v);
            return profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateOutlineDensity(profile, dist / cloud.MaxOutlineDist, 0d);
        }

        if (geometry.SemiMajorRadians <= 0 || geometry.SemiMinorRadians <= 0) return 0d;
        var paOfDate = cloud.IsCircle || cloud.PaJ2000Degrees is null
            ? 0d
            : DeepSkyOutlineSampler.PositionAngleOfDate(
                cloud.Item.RightAscensionHours, cloud.Item.DeclinationDegrees, cloud.PaJ2000Degrees.Value, sampleUtc);
        var paRad = paOfDate * Math.PI / 180d;
        var majorAxis = cloudFrame.CelestialNorth * Math.Cos(paRad) + cloudFrame.CelestialEast * Math.Sin(paRad);
        var minorAxis = cloudFrame.CelestialEast * Math.Cos(paRad) - cloudFrame.CelestialNorth * Math.Sin(paRad);
        var sinMajor = EnuVector.Dot(emitterDir, majorAxis);
        var sinMinor = EnuVector.Dot(emitterDir, minorAxis);
        var sinAngle = double.Hypot(sinMajor, sinMinor);
        var cosAngle = EnuVector.Dot(emitterDir, cloudFrame.Center);
        var angle = Math.Atan2(sinAngle, cosAngle);
        if (angle > geometry.SemiMajorRadians * (1d + 1e-9)) return 0d;
        var scale = sinAngle > 1e-15 ? angle / sinAngle : 1d;
        var eu = (sinMajor * scale) / geometry.SemiMajorRadians;
        var ev = (sinMinor * scale) / geometry.SemiMinorRadians;
        var qSq = eu * eu + ev * ev;
        if (qSq >= 1d) return 0d;
        return profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateRadialDensity(profile, Math.Sqrt(qSq), 0d, 0UL);
    }

    private static void AccumulateForegroundHygRatesForAperture(
        List<HygStarEntry> hygStars,
        Dictionary<string, AdmittedStellarExposure>? admittedStellarById,
        ResolvedSourceGeometry darkGeometry,
        HashSet<int> nativeDarkPixels,
        VisibleSceneRequest sceneRequest,
        DateTimeOffset exposureStartUtc,
        TimeSpan exposure,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        LinearSceneRenderOptions renderOptions,
        DeepSkyRenderSettings settings,
        HashSet<string> rasterizedStarIds,
        Dictionary<int, DeepSkyPixelRate> foregroundRates,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        CancellationToken cancellationToken)
    {
        var apMinX = projection.WidthPixels;
        var apMinY = projection.HeightPixels;
        var apMaxX = -1;
        var apMaxY = -1;
        foreach (var index in nativeDarkPixels)
        {
            var x = index % projection.WidthPixels;
            var y = index / projection.WidthPixels;
            if (x < apMinX) apMinX = x;
            if (x > apMaxX) apMaxX = x;
            if (y < apMinY) apMinY = y;
            if (y > apMaxY) apMaxY = y;
        }
        if (apMaxX < apMinX || apMaxY < apMinY)
        {
            return;
        }

        var minFocalPx = Math.Max(1d, Math.Min(projection.FocalLengthXPixels, projection.FocalLengthYPixels));
        var conservativeMotionRad = exposure.TotalSeconds * (MaximumSiderealDegreesPerSecond * Math.PI / 180d);
        var conservativePsfRad = 2d * (settings.PsfRadiusPixels + Math.Max(settings.DigitalBinX, settings.DigitalBinY) + 4d) / minFocalPx;
        var maxAngularReachRad = darkGeometry.SemiMajorRadians + Math.Max(0.02, conservativeMotionRad + conservativePsfRad);

        var fallbackBv = renderOptions switch
        {
            Rgb24CompatibilityRenderOptions rgb => rgb.FallbackColorIndex,
            BayerRggb16RenderOptions bayer => bayer.FallbackColorIndex,
            _ => 0d
        };

        var useTemporalRaster = exposure > TimeSpan.Zero && !sceneRequest.Refraction.Enabled;
        var maxTemporalSamples = Math.Min(settings.MaximumTemporalSamples, 64);
        var maxStep = settings.MaximumTemporalStepPixels;
        if (settings.PsfRadiusPixels >= 0.75 && settings.PsfSigmaPixels <= settings.PsfRadiusPixels / 4d)
        {
            maxStep = Math.Min(maxStep, TemporalPointSpreadRaster.MaximumTemporalStepPixels(settings.PsfSigmaPixels, settings.PsfRadiusPixels));
        }
        var minTemporalSamples = Math.Min(settings.MinimumTemporalSamples, maxTemporalSamples);

        foreach (var star in hygStars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (star.FluxRate <= 0d || rasterizedStarIds.Contains(star.Id))
            {
                continue;
            }

            if (admittedStellarById is not null && admittedStellarById.TryGetValue(star.Id, out var admittedStar))
            {
                var fp = admittedStar.Prediction.Footprint;
                if (fp.MaximumX < apMinX || fp.MinimumX > apMaxX ||
                    fp.MaximumY < apMinY || fp.MinimumY > apMaxY)
                {
                    continue;
                }

                rasterizedStarIds.Add(star.Id);
                var starColor = Rgb24CompatibilityRenderer.ColorFactors(star.Source.ColorIndex ?? fallbackBv);
                RasterizeForegroundStarSamples(
                    admittedStar.Samples,
                    star.FluxRate,
                    starColor,
                    projection,
                    settings,
                    foregroundRates,
                    ref totalKernelVisits,
                    cancellationToken);
                continue;
            }

            if (maxAngularReachRad < Math.PI &&
                AngularSeparationRadians(star.Direction, darkGeometry.Frame.Center) > maxAngularReachRad)
            {
                continue;
            }

            rasterizedStarIds.Add(star.Id);
            var color = Rgb24CompatibilityRenderer.ColorFactors(star.Source.ColorIndex ?? fallbackBv);

            if (useTemporalRaster)
            {
                var queryStar = new CelestialCatalogObject(
                    star.Id, star.Source.DisplayName, star.Source.RightAscensionHours, star.Source.DeclinationDegrees,
                    0d, star.Source.ColorIndex);
                var singleCatalog = new InMemoryCelestialCatalog([queryStar]);
                var tempRequest = new VisibleSceneRequest(
                    sceneRequest.Utc,
                    sceneRequest.Observer,
                    sceneRequest.Projection,
                    new CatalogQuery(1d, 1),
                    sceneRequest.CatalogMetadata,
                    sceneRequest.Refraction,
                    sceneRequest.HorizonPolicy,
                    projectionVersion: sceneRequest.ProjectionVersion);
                var geomOptions = new StellarExposureGeometryOptions(
                    MaximumCandidates: 1,
                    MaximumSamplesPerSource: maxTemporalSamples,
                    MaximumStepPixels: maxStep,
                    PsfSupportRadiusPixels: settings.PsfRadiusPixels,
                    MinimumSamplesPerSource: minTemporalSamples);
                var temporalGeometryTask = new StellarExposureGeometryBuilder(singleCatalog)
                    .BuildAsync(tempRequest, exposureStartUtc, exposure, geomOptions, cancellationToken);
                var temporalGeometry = temporalGeometryTask.IsCompleted
                    ? temporalGeometryTask.Result
                    : temporalGeometryTask.AsTask().GetAwaiter().GetResult();
                if (temporalGeometry.Sources.Count == 0)
                {
                    if (totalSampledPoints >= settings.MaximumTotalSamplePoints)
                    {
                        throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
                    }
                    totalSampledPoints++;
                    continue;
                }

                var temporalSource = temporalGeometry.Sources[0];
                if (temporalSource.Samples.Count > settings.MaximumTotalSamplePoints - totalSampledPoints)
                {
                    throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
                }
                totalSampledPoints += temporalSource.Samples.Count;

                var weightedSamples = new WeightedPointSpreadSample[temporalSource.Samples.Count];
                for (var i = 0; i < temporalSource.Samples.Count; i++)
                {
                    var sample = temporalSource.Samples[i];
                    var transmission = renderOptions.Cloud?.RequiresEvaluation == true
                        ? renderOptions.Cloud.Field.Evaluate(
                            sample.Horizontal,
                            renderOptions.Cloud.IntegrationStartUtc + (sample.Utc - exposureStartUtc)).Transmission
                        : 1d;
                    weightedSamples[i] = new WeightedPointSpreadSample(sample.Pixel, sample.ExposureFraction, transmission);
                }

                RasterizeForegroundStarSamples(
                    weightedSamples,
                    star.FluxRate,
                    color,
                    projection,
                    settings,
                    foregroundRates,
                    ref totalKernelVisits,
                    cancellationToken);
            }
            else
            {
                if (totalSampledPoints >= settings.MaximumTotalSamplePoints)
                {
                    throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
                }
                totalSampledPoints++;

                if (!projector.TryProject(star.Direction, out var pixel)) continue;
                if (pixel.X < -settings.PsfRadiusPixels || pixel.X > projection.WidthPixels + settings.PsfRadiusPixels ||
                    pixel.Y < -settings.PsfRadiusPixels || pixel.Y > projection.HeightPixels + settings.PsfRadiusPixels)
                {
                    continue;
                }

                var apparent = CameraBasis.ToHorizontal(star.Direction);
                apparent = apparent with
                {
                    AltitudeDegrees = AtmosphericRefraction.Apply(apparent.AltitudeDegrees, sceneRequest.Refraction)
                };
                var cloudTransmission = renderOptions.Cloud?.RequiresEvaluation == true
                    ? renderOptions.Cloud.Field.Evaluate(apparent, renderOptions.Cloud.IntegrationStartUtc).Transmission
                    : 1d;
                var monoDeposits = new Dictionary<int, double>();
                DepositDirectSubpixelPsf(
                    monoDeposits, projection, pixel, star.FluxRate * cloudTransmission,
                    settings.PsfSigmaPixels, settings.PsfRadiusPixels,
                    settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
                foreach (var (pixelIndex, monoRate) in monoDeposits)
                {
                    var rate = new DeepSkyPixelRate(monoRate, monoRate * color[0], monoRate * color[1], monoRate * color[2]);
                    AddSparseRate(foregroundRates, pixelIndex, rate, settings.MaximumSparsePixels);
                }
            }
        }
    }

    private static void RasterizeForegroundStarSamples(
        IReadOnlyList<WeightedPointSpreadSample> samples,
        double fluxRate,
        double[] color,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        Dictionary<int, DeepSkyPixelRate> foregroundRates,
        ref long totalKernelVisits,
        CancellationToken cancellationToken)
    {
        if (samples.Count == 0 || fluxRate <= 0d)
        {
            return;
        }

        if (settings.PsfRadiusPixels >= 0.75 &&
            samples.Count <= 64 &&
            (long)projection.WidthPixels * projection.HeightPixels <= 16_777_216)
        {
            var remainingVisits = settings.MaximumKernelCellVisits - totalKernelVisits;
            if (remainingVisits < 1)
            {
                throw new InvalidOperationException("Deep-sky PSF kernel budget exceeded.");
            }
            var raster = TemporalPointSpreadRaster.Rasterize(
                samples,
                projection,
                settings.PsfSigmaPixels,
                settings.PsfRadiusPixels,
                Math.Min(remainingVisits, 1_000_000_000L),
                Math.Min(settings.MaximumSparsePixels, 65_536),
                cancellationToken);
            totalKernelVisits += raster.KernelCellVisits;
            foreach (var pixel in raster.Pixels)
            {
                var mono = fluxRate * pixel.AfterTransmission;
                if (mono <= 0d) continue;
                var rate = new DeepSkyPixelRate(mono, mono * color[0], mono * color[1], mono * color[2]);
                AddSparseRate(foregroundRates, pixel.Index, rate, settings.MaximumSparsePixels);
            }
            return;
        }

        var monoDeposits = new Dictionary<int, double>();
        foreach (var sample in samples)
        {
            DepositDirectSubpixelPsf(
                monoDeposits, projection, sample.Pixel,
                fluxRate * sample.ExposureFraction * sample.Transmission,
                settings.PsfSigmaPixels, settings.PsfRadiusPixels,
                settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
        }
        foreach (var (pixelIndex, mono) in monoDeposits)
        {
            if (mono <= 0d) continue;
            var rate = new DeepSkyPixelRate(mono, mono * color[0], mono * color[1], mono * color[2]);
            AddSparseRate(foregroundRates, pixelIndex, rate, settings.MaximumSparsePixels);
        }
    }

    private static double EvaluateDarkCloudOpticalDepth(
        AdmittedDarkCloud cloud,
        EnuVector direction,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        var coordinate = DarkCloudDepthCoordinate(cloud, direction, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
        if (double.IsNaN(coordinate)) return 0d;
        var profile = cloud.Profile;
        return profile.PeakOpticalDepth * (cloud.Geometry.TangentRings is { Count: > 0 }
            ? DeepSkySourceProfileResolver.EvaluateOutlineDensity(profile, coordinate, 0d)
            : DeepSkySourceProfileResolver.EvaluateRadialDensity(profile, Math.Sqrt(coordinate), 0d, 0UL));
    }

    private static double LookupScreeningDarkCloudTau(
        AdmittedDarkCloud cloud,
        EnuVector direction,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        var coordinate = DarkCloudDepthCoordinate(cloud, direction, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
        if (double.IsNaN(coordinate)) return 0d;
        var profile = cloud.Profile;
        return profile.PeakOpticalDepth * (cloud.Geometry.TangentRings is { Count: > 0 }
            ? DeepSkySourceProfileResolver.DarkDepthTable.GetOutlineDensity(coordinate / profile.SoftEdgeFraction)
            : DeepSkySourceProfileResolver.DarkDepthTable.GetRadialDensity(coordinate));
    }

    // Return outline clearance or the squared elliptical radius. NaN means outside
    // the footprint. Geometry queries, including trial queries, charge outline edges.
    private static double DarkCloudDepthCoordinate(
        AdmittedDarkCloud cloud,
        EnuVector direction,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        var geometry = cloud.Geometry;
        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            if (cloud.MaxOutlineDist <= 0) return double.NaN;
            var cosine = EnuVector.Dot(direction, geometry.Frame.Center);
            if (cosine <= DeepSkyOutlineSampler.MinimumTangentCosine) return double.NaN;
            var u = EnuVector.Dot(direction, geometry.Frame.CelestialEast) / cosine;
            var v = EnuVector.Dot(direction, geometry.Frame.CelestialNorth) / cosine;
            if (u < geometry.OutlineMinU || u > geometry.OutlineMaxU ||
                v < geometry.OutlineMinV || v > geometry.OutlineMaxV)
            {
                return double.NaN;
            }
            var edgeCount = CountOutlineEdges(rings);
            ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            if (!DeepSkyOutlineSampler.ContainsTangentPoint(rings, u, v)) return double.NaN;
            ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
            var dist = DeepSkyOutlineSampler.DistanceToTangentBoundary(rings, u, v);
            return dist / cloud.MaxOutlineDist;
        }

        if (geometry.SemiMajorRadians <= 0 || geometry.SemiMinorRadians <= 0) return double.NaN;
        var sinMajor = EnuVector.Dot(direction, geometry.MajorAxis);
        var sinMinor = EnuVector.Dot(direction, geometry.MinorAxis);
        var sinAngle = double.Hypot(sinMajor, sinMinor);
        var cosAngle = EnuVector.Dot(direction, geometry.Frame.Center);
        var angle = Math.Atan2(sinAngle, cosAngle);
        if (angle > geometry.SemiMajorRadians * (1d + 1e-9)) return double.NaN;
        var scale = sinAngle > 1e-15 ? angle / sinAngle : 1d;
        var eu = (sinMajor * scale) / geometry.SemiMajorRadians;
        var ev = (sinMinor * scale) / geometry.SemiMinorRadians;
        var qSq = eu * eu + ev * ev;
        return qSq < 1d ? qSq : double.NaN;
    }

    private static double AngularSeparationRadians(EnuVector a, EnuVector b)
    {
        var cross = EnuVector.Cross(a, b).Length;
        var dot = EnuVector.Dot(a, b);
        return Math.Atan2(cross, dot);
    }

    private static (int Count, double TotalFluxRate) MatchHygStarsInFootprint(
        List<HygStarEntry> hygStars,
        ResolvedSourceGeometry geometry,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        var count = 0;
        var flux = 0d;
        var frame = geometry.Frame;
        var edgeCount = geometry.TangentRings is { Count: > 0 } outlineRings ? CountOutlineEdges(outlineRings) : 0L;
        foreach (var star in hygStars)
        {
            var cosine = EnuVector.Dot(star.Direction, frame.Center);
            if (cosine <= DeepSkyOutlineSampler.MinimumTangentCosine) continue;
            if (geometry.TangentRings is { Count: > 0 } rings)
            {
                ChargeOutlineEdgeVisits(edgeCount, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                var u = EnuVector.Dot(star.Direction, frame.CelestialEast) / cosine;
                var v = EnuVector.Dot(star.Direction, frame.CelestialNorth) / cosine;
                if (DeepSkyOutlineSampler.ContainsTangentPoint(rings, u, v))
                {
                    count++;
                    flux += star.FluxRate;
                }
            }
            else if (geometry.SemiMajorRadians > 0 && geometry.SemiMinorRadians > 0)
            {
                var angle = Math.Acos(Math.Clamp(cosine, -1d, 1d));
                if (angle > geometry.SemiMajorRadians * 1.0001) continue;
                if (angle < 1e-12)
                {
                    count++;
                    flux += star.FluxRate;
                    continue;
                }
                var tangent = star.Direction - frame.Center * cosine;
                var len = tangent.Length;
                if (len <= 1e-15) continue;
                var scaled = tangent * (angle / len);
                var alongMajor = EnuVector.Dot(scaled, geometry.MajorAxis) / geometry.SemiMajorRadians;
                var alongMinor = EnuVector.Dot(scaled, geometry.MinorAxis) / geometry.SemiMinorRadians;
                if (alongMajor * alongMajor + alongMinor * alongMinor <= 1d + 1e-9)
                {
                    count++;
                    flux += star.FluxRate;
                }
            }
        }
        return (count, flux);
    }

    private static void DepositDirectSubpixelPsf(
        Dictionary<int, double> output,
        ProjectionContext projection,
        PixelPoint pixel,
        double fluxRate,
        double sigma,
        double cutoff,
        int maximumSparsePixels,
        long maximumKernelCellVisits,
        ref long totalKernelVisits)
    {
        var minX = (int)Math.Ceiling(pixel.X - cutoff - 0.5);
        var maxX = (int)Math.Floor(pixel.X + cutoff - 0.5);
        var minY = (int)Math.Ceiling(pixel.Y - cutoff - 0.5);
        var maxY = (int)Math.Floor(pixel.Y + cutoff - 0.5);
        var cutoffSq = cutoff * cutoff;
        var twoSigmaSq = 2d * sigma * sigma;

        // Normalize over the COMPLETE PSF cutoff BEFORE sensor/aperture clipping.
        var denominator = 0d;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (++totalKernelVisits > maximumKernelCellVisits)
                {
                    throw new InvalidOperationException("Deep-sky PSF kernel budget exceeded.");
                }
                var dx = x + 0.5 - pixel.X;
                var dy = y + 0.5 - pixel.Y;
                var distSq = dx * dx + dy * dy;
                if (distSq <= cutoffSq)
                {
                    denominator += Math.Exp(-distSq / twoSigmaSq);
                }
            }
        }
        if (denominator <= 0 || !double.IsFinite(denominator)) return;

        var startY = Math.Max(0, minY);
        var endY = Math.Min(projection.HeightPixels - 1, maxY);
        var startX = Math.Max(0, minX);
        var endX = Math.Min(projection.WidthPixels - 1, maxX);
        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                var dx = x + 0.5 - pixel.X;
                var dy = y + 0.5 - pixel.Y;
                var distSq = dx * dx + dy * dy;
                if (distSq > cutoffSq || !projection.ContainsSample(x + 0.5, y + 0.5)) continue;
                var value = fluxRate * Math.Exp(-distSq / twoSigmaSq) / denominator;
                var index = y * projection.WidthPixels + x;
                AddScalarRate(output, index, value, maximumSparsePixels);
            }
        }
    }

    private static void DepositBilinear(
        Dictionary<long, double> deposits,
        PixelPoint point,
        double value,
        int width,
        int height,
        int padding,
        long paddedWidth,
        int maximumSparsePixels)
    {
        var x = (long)Math.Floor(point.X - 0.5);
        var y = (long)Math.Floor(point.Y - 0.5);
        var fx = point.X - 0.5 - x;
        var fy = point.Y - 0.5 - y;
        AddCell(x, y, value * (1d - fx) * (1d - fy));
        AddCell(x + 1, y, value * fx * (1d - fy));
        AddCell(x, y + 1, value * (1d - fx) * fy);
        AddCell(x + 1, y + 1, value * fx * fy);

        void AddCell(long cx, long cy, double amount)
        {
            if (amount == 0 || cx < -padding || cx >= width + padding || cy < -padding || cy >= height + padding) return;
            var key = (cy + padding) * paddedWidth + cx + padding;
            if (!deposits.TryGetValue(key, out var prior))
            {
                if (deposits.Count >= maximumSparsePixels)
                {
                    throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
                }
                deposits[key] = amount;
            }
            else
            {
                deposits[key] = prior + amount;
            }
        }
    }

    private static void ConvolveToClipped(
        Dictionary<long, double> deposits,
        Dictionary<int, double> clipped,
        ProjectionContext projection,
        (int Dx, int Dy, double Weight)[] kernel,
        int padding,
        long paddedWidth,
        int maximumSparsePixels,
        long maximumKernelCellVisits,
        ref long totalKernelVisits,
        CancellationToken cancellationToken)
    {
        foreach (var (key, value) in deposits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalKernelVisits += kernel.Length;
            if (totalKernelVisits > maximumKernelCellVisits)
            {
                throw new InvalidOperationException("Deep-sky PSF kernel budget exceeded.");
            }
            var sx = (int)(key % paddedWidth) - padding;
            var sy = (int)(key / paddedWidth) - padding;
            foreach (var (dx, dy, weight) in kernel)
            {
                var px = sx + dx;
                var py = sy + dy;
                if (px < 0 || px >= projection.WidthPixels || py < 0 || py >= projection.HeightPixels ||
                    !projection.ContainsSample(px + 0.5, py + 0.5))
                {
                    continue;
                }
                var add = value * weight;
                if (add == 0) continue;
                AddScalarRate(clipped, py * projection.WidthPixels + px, add, maximumSparsePixels);
            }
        }
    }

    private static (StellarSignalPrediction Prediction, bool CausesSaturation, RenderedObjectGeometry Footprint, double RetainedMonoRate, int ActivePixelCount)
        EvaluateSignalAndFootprint(
            string objectId,
            ResolvedSourceGeometry geometry,
            PixelPoint? temporalReferencePixel,
            DeepSkySpectralFactors spectral,
            Dictionary<int, double> preCloudMonoRates,
            Dictionary<int, double> temporalPostCloudMonoRates,
            Dictionary<int, double> temporalPreCloudMonoRates,
            double declaredDiffuseBg,
            Dictionary<int, DeepSkyPixelRate>? overlapDiffusePreCloud,
            Dictionary<int, DeepSkyPixelRate>? overlapDiffuseTemporal,
            Dictionary<int, DeepSkyPixelRate>? selfDiffusePreCloud,
            Dictionary<int, DeepSkyPixelRate>? selfDiffuseTemporal,
            double expectedSourceRate,
            ProjectionContext projection,
            LinearSceneRenderOptions options,
            DeepSkyRenderSettings settings,
            CancellationToken cancellationToken)
    {
        var response = options switch
        {
            Mono16SceneRenderOptions mono => mono.SensorResponse,
            BayerRggb16RenderOptions bayer => bayer.SensorResponse,
            _ => null
        };
        var readVariance = response is null
            ? Math.Pow(options.Gain > 0 ? options.ReadNoiseStandardDeviation / options.Gain : 0, 2)
            : response.ReadNoiseElectrons * response.ReadNoiseElectrons;
        var darkElectrons = options.DarkCurrentElectronsPerSecond * options.ExposureSeconds;
        var extractionWeight = settings.DigitalAverage ? 1d / (settings.DigitalBinX * settings.DigitalBinY) : 1d;
        var projector = options.Cloud?.RequiresEvaluation == true ? ProjectorFactory.Create(projection) : null;
        var stellarSettings = new StellarExposureRenderSettings(
            DigitalBinX: settings.DigitalBinX,
            DigitalBinY: settings.DigitalBinY,
            DigitalAverage: settings.DigitalAverage,
            CfaOriginX: settings.CfaOriginX,
            CfaOriginY: settings.CfaOriginY);
        var displayWeights = DisplayWeights(options);
        var maximumDisplayWeight = displayWeights.Max();
        var channelCount = options is Rgb24CompatibilityRenderOptions ? 3 : 1;

        var activePixels = new HashSet<int>(preCloudMonoRates.Keys);
        activePixels.UnionWith(temporalPostCloudMonoRates.Keys);
        activePixels.UnionWith(temporalPreCloudMonoRates.Keys);
        var nativePixels = CompleteBinSupport(activePixels, projection, settings);

        var signalTotal = 0d;
        var varianceTotal = 0d;
        var causesSaturation = false;
        var retainedMono = 0d;
        var weightedX = 0d;
        var weightedY = 0d;
        var minX = projection.WidthPixels;
        var minY = projection.HeightPixels;
        var maxX = -1;
        var maxY = -1;

        foreach (var index in nativePixels.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = index % projection.WidthPixels;
            var y = index / projection.WidthPixels;
            var vignette = 1d - options.VignettingStrength * projection.NormalizedRadiusSquared(x + 0.5, y + 0.5);
            var backgroundTransmission = 1d;
            var diskTransmission = 1d;
            if (projector is not null)
            {
                var dir = projector.Unproject(new PixelPoint(x + 0.5, y + 0.5));
                if (dir is not null)
                {
                    var eff = options.Cloud!.Field.Integrate(dir.Value, options.Cloud.IntegrationStartUtc, options.Cloud.IntegrationDuration);
                    backgroundTransmission = (float)eff.Transmission + (float)eff.Scatter;
                    diskTransmission = (float)eff.Transmission;
                }
            }

            var preMono = preCloudMonoRates.GetValueOrDefault(index);
            var tmpPostMono = temporalPostCloudMonoRates.GetValueOrDefault(index);
            var tmpPreMono = temporalPreCloudMonoRates.GetValueOrDefault(index);

            for (var channel = 0; channel < channelCount; channel++)
            {
                var channelMultiplier = options switch
                {
                    BayerRggb16RenderOptions bayer =>
                        spectral.Channel(StellarExposureRenderPlan.CfaChannel(x, y, stellarSettings)) *
                        StellarExposureRenderPlan.Channel(bayer.ChannelResponse, StellarExposureRenderPlan.CfaChannel(x, y, stellarSettings)),
                    Rgb24CompatibilityRenderOptions => spectral.Channel(channel),
                    _ => spectral.Mono
                };
                var skyChannel = options switch
                {
                    BayerRggb16RenderOptions => StellarExposureRenderPlan.CfaChannel(x, y, stellarSettings),
                    Rgb24CompatibilityRenderOptions => channel,
                    _ => -1
                };
                var cfaScale = options is BayerRggb16RenderOptions bayerBg
                    ? StellarExposureRenderPlan.Channel(bayerBg.ChannelResponse, skyChannel)
                    : 1d;
                var overlapPre = overlapDiffusePreCloud is null
                    ? 0d
                    : overlapDiffusePreCloud.GetValueOrDefault(index).Channel(skyChannel);
                if (selfDiffusePreCloud is not null)
                {
                    overlapPre -= selfDiffusePreCloud.GetValueOrDefault(index).Channel(skyChannel);
                }
                var overlapTmp = overlapDiffuseTemporal is null
                    ? 0d
                    : overlapDiffuseTemporal.GetValueOrDefault(index).Channel(skyChannel);
                if (selfDiffuseTemporal is not null)
                {
                    overlapTmp -= selfDiffuseTemporal.GetValueOrDefault(index).Channel(skyChannel);
                }
                var diffuseBgRate = (declaredDiffuseBg + Math.Max(0d, overlapPre)) * cfaScale;
                var diffuseTmpRate = Math.Max(0d, overlapTmp) * cfaScale;
                var sourceElectrons = (preMono * diskTransmission + tmpPostMono) * channelMultiplier * vignette * options.ExposureSeconds;
                var bgElectrons = (options.BackgroundRate(x, y, skyChannel) * backgroundTransmission +
                    (options.DiskRate(x, y, skyChannel) + diffuseBgRate) * diskTransmission +
                    diffuseTmpRate) * vignette * options.ExposureSeconds;
                var channelWeight = maximumDisplayWeight > 0 ? displayWeights[channel] / maximumDisplayWeight : 0d;
                if (response is null && options.Gain == 0)
                {
                    channelWeight = 0d;
                }
                var weight = extractionWeight * channelWeight;
                signalTotal += weight * sourceElectrons;
                varianceTotal += weight * weight * (sourceElectrons + bgElectrons + darkElectrons + readVariance);

                var maxCharge = MaximumCharge(options, response, displayWeights[channel]);
                var totalCharge = sourceElectrons + bgElectrons + darkElectrons;
                if (sourceElectrons > 0 && (sourceElectrons >= maxCharge ||
                    (bgElectrons + darkElectrons < maxCharge && totalCharge >= maxCharge)))
                {
                    causesSaturation = true;
                }
            }

            var opticalMono = preMono + tmpPreMono;
            var footprintWeight = preMono + tmpPostMono;
            retainedMono += opticalMono;
            if (footprintWeight > 0)
            {
                weightedX += footprintWeight * (x + 0.5);
                weightedY += footprintWeight * (y + 0.5);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        var snr = varianceTotal > 0 ? signalTotal / Math.Sqrt(varianceTotal) : 0d;
        var prediction = new StellarSignalPrediction(signalTotal, varianceTotal, snr);
        var footprintSum = 0d;
        foreach (var v in preCloudMonoRates.Values) footprintSum += v;
        foreach (var v in temporalPostCloudMonoRates.Values) footprintSum += v;
        var centroid = footprintSum > 0
            ? new PixelPoint(weightedX / footprintSum, weightedY / footprintSum)
            : temporalReferencePixel ?? geometry.CenterPixel ?? new PixelPoint(0, 0);
        var sourceRef = geometry.CenterPixel ?? temporalReferencePixel ?? centroid;
        if (maxX < minX || maxY < minY)
        {
            minX = maxX = (int)Math.Clamp(Math.Floor(sourceRef.X), 0, projection.WidthPixels - 1);
            minY = maxY = (int)Math.Clamp(Math.Floor(sourceRef.Y), 0, projection.HeightPixels - 1);
        }
        var footprint = new RenderedObjectGeometry(
            ProjectedDeepSkyObject.SceneId(objectId),
            sourceRef,
            centroid,
            expectedSourceRate * options.ExposureSeconds,
            retainedMono * options.ExposureSeconds,
            expectedSourceRate > 0 ? retainedMono / expectedSourceRate : 0d,
            minX, minY, maxX, maxY);
        return (prediction, causesSaturation, footprint, retainedMono, activePixels.Count);
    }

    private static StellarSignalPrediction EvaluateDarkSignalPrediction(
        HashSet<int> nativePixels,
        Dictionary<int, DeepSkyPixelRate> preCloudDarkDelta,
        Dictionary<int, DeepSkyPixelRate> temporalDarkDelta,
        double declaredDiffuseBg,
        Dictionary<int, DeepSkyPixelRate> overlapDiffusePreCloud,
        Dictionary<int, DeepSkyPixelRate> overlapDiffuseTemporal,
        Dictionary<int, DeepSkyPixelRate> priorDarkAttenuatedPreCloud,
        Dictionary<int, DeepSkyPixelRate> priorDarkAttenuatedTemporal,
        Dictionary<int, DeepSkyPixelRate> nonDiffusePreCloudRates,
        Dictionary<int, DeepSkyPixelRate> nonDiffuseTemporalRates,
        Dictionary<int, DeepSkyPixelRate>? foregroundHygTemporalRates,
        ProjectionContext projection,
        LinearSceneRenderOptions options,
        DeepSkyRenderSettings settings,
        CancellationToken cancellationToken,
        HashSet<int>? variancePixels = null)
    {
        var response = options switch
        {
            Mono16SceneRenderOptions mono => mono.SensorResponse,
            BayerRggb16RenderOptions bayer => bayer.SensorResponse,
            _ => null
        };
        var readVariance = response is null
            ? Math.Pow(options.Gain > 0 ? options.ReadNoiseStandardDeviation / options.Gain : 0, 2)
            : response.ReadNoiseElectrons * response.ReadNoiseElectrons;
        var darkElectrons = options.DarkCurrentElectronsPerSecond * options.ExposureSeconds;
        var extractionWeight = settings.DigitalAverage ? 1d / (settings.DigitalBinX * settings.DigitalBinY) : 1d;
        var projector = options.Cloud?.RequiresEvaluation == true ? ProjectorFactory.Create(projection) : null;
        var stellarSettings = new StellarExposureRenderSettings(
            DigitalBinX: settings.DigitalBinX,
            DigitalBinY: settings.DigitalBinY,
            DigitalAverage: settings.DigitalAverage,
            CfaOriginX: settings.CfaOriginX,
            CfaOriginY: settings.CfaOriginY);
        var displayWeights = DisplayWeights(options);
        var maximumDisplayWeight = displayWeights.Max();
        var channelCount = options is Rgb24CompatibilityRenderOptions ? 3 : 1;

        var signalTotal = 0d;
        var varianceTotal = 0d;

        foreach (var index in nativePixels.Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = index % projection.WidthPixels;
            var y = index / projection.WidthPixels;
            var vignette = 1d - options.VignettingStrength * projection.NormalizedRadiusSquared(x + 0.5, y + 0.5);
            var backgroundTransmission = 1d;
            var diskTransmission = 1d;
            if (projector is not null)
            {
                var dir = projector.Unproject(new PixelPoint(x + 0.5, y + 0.5));
                if (dir is not null)
                {
                    var eff = options.Cloud!.Field.Integrate(dir.Value, options.Cloud.IntegrationStartUtc, options.Cloud.IntegrationDuration);
                    backgroundTransmission = (float)eff.Transmission + (float)eff.Scatter;
                    diskTransmission = (float)eff.Transmission;
                }
            }

            var preDelta = preCloudDarkDelta.GetValueOrDefault(index);
            var tmpDelta = temporalDarkDelta.GetValueOrDefault(index);
            var overlapPreRate = overlapDiffusePreCloud.GetValueOrDefault(index);
            var overlapTmpRate = overlapDiffuseTemporal.GetValueOrDefault(index);
            var priorPreAtten = priorDarkAttenuatedPreCloud.GetValueOrDefault(index);
            var priorTmpAtten = priorDarkAttenuatedTemporal.GetValueOrDefault(index);
            var fgPreRate = nonDiffusePreCloudRates.GetValueOrDefault(index);
            var fgTmpRate = nonDiffuseTemporalRates.GetValueOrDefault(index);
            var fgHygTmpRate = foregroundHygTemporalRates?.GetValueOrDefault(index) ?? default;

            var contributesVariance = variancePixels is null || variancePixels.Contains(index);
            for (var channel = 0; channel < channelCount; channel++)
            {
                var skyChannel = options switch
                {
                    BayerRggb16RenderOptions => StellarExposureRenderPlan.CfaChannel(x, y, stellarSettings),
                    Rgb24CompatibilityRenderOptions => channel,
                    _ => -1
                };
                var cfaScale = options is BayerRggb16RenderOptions bayerBg
                    ? StellarExposureRenderPlan.Channel(bayerBg.ChannelResponse, skyChannel)
                    : 1d;
                var preDeficit = Math.Max(0d, -preDelta.Channel(skyChannel)) * cfaScale;
                var tmpDeficit = Math.Max(0d, -tmpDelta.Channel(skyChannel)) * cfaScale;
                var deficitElectrons = (preDeficit * diskTransmission + tmpDeficit) * vignette * options.ExposureSeconds;

                // Null-hypothesis local photon rate before this dark cloud attenuates the extraction footprint,
                // including co-located foreground non-diffuse DSO and exposure-integrated HYG star shot noise
                // (never attenuated by dark clouds).
                var preDiffuseBefore = Math.Max(
                    variancePixels is null ? preDeficit : 0d,
                    Math.Max(0d, declaredDiffuseBg + overlapPreRate.Channel(skyChannel) + priorPreAtten.Channel(skyChannel)) * cfaScale);
                var tmpDiffuseBefore = Math.Max(
                    tmpDeficit,
                    Math.Max(0d, overlapTmpRate.Channel(skyChannel) + priorTmpAtten.Channel(skyChannel)) * cfaScale);
                var foregroundPreBefore = Math.Max(0d, fgPreRate.Channel(skyChannel)) * cfaScale;
                var foregroundTmpBefore = (Math.Max(0d, fgTmpRate.Channel(skyChannel)) + Math.Max(0d, fgHygTmpRate.Channel(skyChannel))) * cfaScale;
                var bgBeforeElectrons = (options.BackgroundRate(x, y, skyChannel) * backgroundTransmission +
                    (options.DiskRate(x, y, skyChannel) + preDiffuseBefore + foregroundPreBefore) * diskTransmission +
                    tmpDiffuseBefore + foregroundTmpBefore) * vignette * options.ExposureSeconds;

                var channelWeight = maximumDisplayWeight > 0 ? displayWeights[channel] / maximumDisplayWeight : 0d;
                if (response is null && options.Gain == 0)
                {
                    channelWeight = 0d;
                }
                var weight = extractionWeight * channelWeight;
                signalTotal += weight * deficitElectrons;
                if (contributesVariance)
                {
                    varianceTotal += weight * weight * (bgBeforeElectrons + darkElectrons + readVariance);
                }
            }
        }

        var snr = varianceTotal > 0
            ? signalTotal / Math.Sqrt(varianceTotal)
            : variancePixels is not null && signalTotal > 0d ? double.PositiveInfinity : 0d;
        return new StellarSignalPrediction(signalTotal, varianceTotal, snr);
    }

    private static HashSet<int> CompleteBinSupport(
        HashSet<int> activePixels,
        ProjectionContext projection,
        DeepSkyRenderSettings settings)
    {
        if (settings.DigitalBinX == 1 && settings.DigitalBinY == 1)
        {
            return activePixels;
        }
        var result = new HashSet<int>();
        foreach (var pixelIndex in activePixels)
        {
            var x0 = pixelIndex % projection.WidthPixels / settings.DigitalBinX * settings.DigitalBinX;
            var y0 = pixelIndex / projection.WidthPixels / settings.DigitalBinY * settings.DigitalBinY;
            for (var y = y0; y < Math.Min(y0 + settings.DigitalBinY, projection.HeightPixels); y++)
            {
                for (var x = x0; x < Math.Min(x0 + settings.DigitalBinX, projection.WidthPixels); x++)
                {
                    if (projection.ContainsSample(x + 0.5, y + 0.5))
                    {
                        result.Add(y * projection.WidthPixels + x);
                        if (result.Count > settings.MaximumSparsePixels)
                        {
                            throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
                        }
                    }
                }
            }
        }
        return result;
    }

    private static LinearSceneRenderOptions Normalize(LinearSceneRenderOptions options)
        => options with { Seed = 0, StellarExposure = null, DeepSkySources = null };

    private static double[] DisplayWeights(LinearSceneRenderOptions options)
        => options is Rgb24CompatibilityRenderOptions rgb
            ? [rgb.ChannelResponse.Red * rgb.WhiteBalance.Red,
               rgb.ChannelResponse.Green * rgb.WhiteBalance.Green,
               rgb.ChannelResponse.Blue * rgb.WhiteBalance.Blue]
            : [1d];

    private static double MaximumCharge(LinearSceneRenderOptions options, MonoSensorResponse? response, double displayWeight)
        => response is null
            ? options.Gain > 0 && displayWeight > 0
                ? Math.Max(0, (options is Rgb24CompatibilityRenderOptions ? byte.MaxValue / displayWeight : ushort.MaxValue) - options.Bias) / options.Gain
                : double.MaxValue
            : Math.Min(response.FullWellElectrons, Math.Max(0, response.MaximumAdu - response.BlackLevelAdu) * response.ElectronsPerAdu);

    private static long CountOutlineEdges(IReadOnlyList<PixelPoint[]> rings)
    {
        var total = 0L;
        foreach (var ring in rings)
        {
            total += Math.Max(0, ring.Length - 1);
        }
        return total;
    }

    private static void ChargeOutlineEdgeVisits(long additional, long limit, ref long totalOutlineEdgeVisits)
    {
        if (additional < 0 || additional > limit - totalOutlineEdgeVisits)
        {
            throw new InvalidOperationException("Deep-sky outline edge-visit budget exceeded.");
        }
        totalOutlineEdgeVisits += additional;
    }

    private static double? MeasureLocalAreaPixelsPerSteradian(
        ResolvedSourceProjector projector,
        EnuVector direction,
        EnuVector tangentA,
        EnuVector tangentB)
    {
        if (!projector.TryProject(direction, out var p0) ||
            !projector.TryProject((direction + tangentA * PlateScaleProbeRadians).Normalize(), out var p1) ||
            !projector.TryProject((direction + tangentB * PlateScaleProbeRadians).Normalize(), out var p2))
        {
            return null;
        }
        var d1x = p1.X - p0.X;
        var d1y = p1.Y - p0.Y;
        var d2x = p2.X - p0.X;
        var d2y = p2.Y - p0.Y;
        var det = Math.Abs(d1x * d2y - d1y * d2x) / (PlateScaleProbeRadians * PlateScaleProbeRadians);
        return double.IsFinite(det) && det > 0 ? det : null;
    }

    private static double? MeasureLocalAreaPixelsPerSteradian(
        ResolvedSourceProjector projector,
        EnuVector direction)
    {
        var reference = Math.Abs(direction.Up) < 0.9 ? new EnuVector(0, 0, 1) : new EnuVector(1, 0, 0);
        var tangentA = (reference - direction * EnuVector.Dot(reference, direction)).Normalize();
        var tangentB = EnuVector.Cross(direction, tangentA).Normalize();
        return MeasureLocalAreaPixelsPerSteradian(projector, direction, tangentA, tangentB);
    }

    private static DeepSkySourceDiagnostic CreateSkipped(
        DeepSkyObject item,
        DeepSkyProfileClass profileClass,
        string reason,
        DeepSkyPhotometryBasis photometryBasis,
        DeepSkyGeometryBasis geometryBasis,
        DeepSkySimulationParameters? profile) =>
        new(
            item.Id,
            item.DisplayName,
            item.ObjectType,
            profileClass,
            Admitted: false,
            Reason: reason,
            photometryBasis,
            geometryBasis,
            profile?.ProfileSchemaVersion ?? DeepSkySimulationParameters.CurrentSchemaVersion,
            profile?.MorphologyModel ?? "none",
            profile?.Spectral.SpectralModel ?? "none",
            profile?.Spectral.Origin ?? DeepSkySpectralOrigin.SimulationClassDefault,
            profile?.IsSimulationParameter ?? true,
            0d, 0d, 0d, 0d, 0, 0d, 0, 0d, default, 0, 0, false, null);

    private static double? MeasureLocalPixelsPerRadian(
        ResolvedSourceProjector projector,
        ResolvedSourceFrame frame)
    {
        if (!projector.TryProject(frame.Center, out var center) ||
            !projector.TryProject(frame.DirectionFromCelestial(PlateScaleProbeRadians, 0), out var north) ||
            !projector.TryProject(frame.DirectionFromCelestial(0, PlateScaleProbeRadians), out var east))
        {
            return null;
        }
        var dn = Math.Sqrt(Math.Pow(north.X - center.X, 2) + Math.Pow(north.Y - center.Y, 2));
        var de = Math.Sqrt(Math.Pow(east.X - center.X, 2) + Math.Pow(east.Y - center.Y, 2));
        var scale = (dn + de) / (2d * PlateScaleProbeRadians);
        return double.IsFinite(scale) && scale > 0 ? scale : null;
    }

    private static void ComputeTangentBounds(
        IReadOnlyList<PixelPoint[]> rings,
        out double minU,
        out double maxU,
        out double minV,
        out double maxV,
        out double maxAngleRadians)
    {
        minU = double.PositiveInfinity;
        maxU = double.NegativeInfinity;
        minV = double.PositiveInfinity;
        maxV = double.NegativeInfinity;
        var maxRSq = 0d;
        foreach (var ring in rings)
        {
            foreach (var p in ring)
            {
                if (p.X < minU) minU = p.X;
                if (p.X > maxU) maxU = p.X;
                if (p.Y < minV) minV = p.Y;
                if (p.Y > maxV) maxV = p.Y;
                var rSq = p.X * p.X + p.Y * p.Y;
                if (rSq > maxRSq) maxRSq = rSq;
            }
        }
        maxAngleRadians = Math.Atan(Math.Sqrt(maxRSq));
    }

    private static void ClampGridToBudget(ref int nu, ref int nv, int maxSamples)
    {
        nu = Math.Max(1, nu);
        nv = Math.Max(1, nv);
        if ((long)nu * nv <= maxSamples) return;
        var scale = Math.Sqrt((double)maxSamples / ((long)nu * nv));
        nu = Math.Max(1, (int)Math.Floor(nu * scale));
        nv = Math.Max(1, (int)Math.Floor(nv * scale));
        while ((long)nu * nv > maxSamples)
        {
            if (nu >= nv && nu > 1) nu--;
            else if (nv > 1) nv--;
            else break;
        }
    }

    private static DeepSkyPixelRate ScaleRate(double monoRate, DeepSkySpectralFactors spectral) =>
        new(monoRate * spectral.Mono, monoRate * spectral.Red, monoRate * spectral.Green, monoRate * spectral.Blue);

    private static void AddScalarRate(Dictionary<int, double> map, int key, double add, int limit)
    {
        if (add == 0) return;
        if (!map.TryGetValue(key, out var prior))
        {
            if (map.Count >= limit)
            {
                throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
            }
            map[key] = add;
        }
        else
        {
            map[key] = prior + add;
        }
    }

    private static Dictionary<int, double> MergeScalarRates(
        Dictionary<int, double> first,
        Dictionary<int, double> second,
        int limit)
    {
        if (second.Count == 0) return new Dictionary<int, double>(first);
        if (first.Count == 0) return new Dictionary<int, double>(second);
        var merged = new Dictionary<int, double>(first);
        foreach (var (key, value) in second)
        {
            AddScalarRate(merged, key, value, limit);
        }
        return merged;
    }

    private static void AddCombinedSparseRate(
        Dictionary<int, DeepSkyPixelRate> map,
        HashSet<int> unionPixels,
        int key,
        DeepSkyPixelRate add,
        int limit)
    {
        if (!unionPixels.Contains(key))
        {
            if (unionPixels.Count >= limit)
            {
                throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
            }
            unionPixels.Add(key);
        }
        if (!map.TryGetValue(key, out var prior))
        {
            map[key] = add;
        }
        else
        {
            map[key] = prior + add;
        }
    }

    private static void AddCombinedPaddedRate(
        Dictionary<long, DeepSkyPixelRate> map,
        HashSet<long> unionPaddedKeys,
        long key,
        DeepSkyPixelRate add,
        int limit)
    {
        if (!unionPaddedKeys.Contains(key))
        {
            if (unionPaddedKeys.Count >= limit)
            {
                throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
            }
            unionPaddedKeys.Add(key);
        }
        if (!map.TryGetValue(key, out var prior))
        {
            map[key] = add;
        }
        else
        {
            map[key] = prior + add;
        }
    }

    private static void AddUnboundedSparseRate(Dictionary<int, DeepSkyPixelRate> map, int key, DeepSkyPixelRate add)
    {
        if (!map.TryGetValue(key, out var prior))
        {
            map[key] = add;
        }
        else
        {
            map[key] = prior + add;
        }
    }

    private static void SubtractUnboundedSparseRates(
        Dictionary<int, DeepSkyPixelRate> target,
        Dictionary<int, DeepSkyPixelRate> remove)
    {
        foreach (var (key, sub) in remove)
        {
            if (!target.TryGetValue(key, out var prior)) continue;
            var next = new DeepSkyPixelRate(
                prior.Mono - sub.Mono,
                prior.Red - sub.Red,
                prior.Green - sub.Green,
                prior.Blue - sub.Blue);
            if (Math.Abs(next.Mono) <= 1e-15 && Math.Abs(next.Red) <= 1e-15 &&
                Math.Abs(next.Green) <= 1e-15 && Math.Abs(next.Blue) <= 1e-15)
            {
                target.Remove(key);
            }
            else
            {
                target[key] = next;
            }
        }
    }

    private static void AddSparseRate(Dictionary<int, DeepSkyPixelRate> map, int key, DeepSkyPixelRate add, int limit)
    {
        if (!map.TryGetValue(key, out var prior))
        {
            if (map.Count >= limit)
            {
                throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
            }
            map[key] = add;
        }
        else
        {
            map[key] = prior + add;
        }
    }

    private static double NextUnit(ref ulong state)
    {
        state = DeepSkySourceProfileResolver.Mix64(state);
        return (state >> 11) * (1d / (1UL << 53));
    }

    private readonly record struct DeepSkyPixelRate(double Mono, double Red, double Green, double Blue)
    {
        public double Channel(int channel) => channel switch
        {
            -1 => Mono,
            0 => Red,
            1 => Green,
            2 => Blue,
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };

        public static DeepSkyPixelRate operator +(DeepSkyPixelRate left, DeepSkyPixelRate right) =>
            new(left.Mono + right.Mono, left.Red + right.Red, left.Green + right.Green, left.Blue + right.Blue);
    }

    private readonly record struct HygStarEntry(
        string Id,
        CelestialCatalogObject Source,
        EnuVector Direction,
        double FluxRate);

    private readonly record struct UnresolvedDiffuseSample(
        EnuVector Direction,
        PixelPoint Pixel,
        double ExposureFraction,
        double CloudTransmission,
        DateTimeOffset SampleUtc);

    private sealed record AdmittedUnresolvedDiffuseSource(
        string ObjectId,
        EquatorialPoint EmitterJ2000,
        EnuVector MidpointGeometricDirection,
        double FluxRate,
        DeepSkySpectralFactors Spectral,
        List<UnresolvedDiffuseSample> Samples,
        Dictionary<int, double> UnattenuatedPostCloudRates);

    private readonly record struct OutlineOrientedFrame(
        double OriginU,
        double OriginV,
        double AxisSU,
        double AxisSV,
        double AxisTU,
        double AxisTV,
        double MinS,
        double MaxS,
        double MinT,
        double MaxT)
    {
        public double SpanS => MaxS - MinS;
        public double SpanT => MaxT - MinT;

        public (double U, double V) ToTangent(double s, double t) =>
            (OriginU + s * AxisSU + t * AxisTU, OriginV + s * AxisSV + t * AxisTV);

        public (double S, double T) ToLocal(double u, double v)
        {
            var du = u - OriginU;
            var dv = v - OriginV;
            return (du * AxisSU + dv * AxisSV, du * AxisTU + dv * AxisTV);
        }
    }

    private readonly record struct LocalOutlineEdge(
        double S0,
        double T0,
        double S1,
        double T1,
        double MinS,
        double MaxS,
        double MinT,
        double MaxT);

    private readonly record struct ScanlineEdgeCrossing(
        double SMid,
        double SA,
        double SB);

    private readonly record struct PaddedPixelBounds(int MinX, int MinY, int MaxX, int MaxY);

    private readonly record struct FineDarkCloudSample(
        EnuVector Direction,
        long PaddedPixelKey,
        double PixelArea,
        double Tau);

    private readonly record struct FineCellDeposit(
        double AreaSum,
        double SoloAbsorbAreaSum,
        double PriorAttenuatedAbsorbAreaSum)
    {
        public double SoloAlpha => AreaSum > 0d
            ? (AreaSum > 1d ? Math.Clamp(SoloAbsorbAreaSum / AreaSum, 0d, 1d) : Math.Clamp(SoloAbsorbAreaSum, 0d, 1d))
            : 0d;

        public double ResidualAlpha => AreaSum > 0d
            ? (AreaSum > 1d ? Math.Clamp(PriorAttenuatedAbsorbAreaSum / AreaSum, 0d, 1d) : Math.Clamp(PriorAttenuatedAbsorbAreaSum, 0d, 1d))
            : 0d;
    }

    private sealed class CachedResolvedPixel(int subCount)
    {
        public EnuVector?[] Directions { get; } = new EnuVector?[subCount];
    }

    private sealed class AdmittedFineDarkCloudGroup(
        AdmittedDarkCloud representativeCloud,
        FineDarkCloudSample[] samples,
        double[] combinedTau,
        double combinedPeakOpticalDepth,
        double[] priorTau,
        Dictionary<long, FineCellDeposit> cellDeposits,
        HashSet<AdmittedDarkCloud> evaluatedResolved,
        HashSet<AdmittedFineDarkCloudGroup> evaluatedFineGroups,
        Dictionary<AdmittedFineDarkCloudGroup, double[]> unitTauByPriorFineGroup)
    {
        public AdmittedDarkCloud RepresentativeCloud { get; } = representativeCloud;
        public FineDarkCloudSample[] Samples { get; } = samples;
        public double[] CombinedTau { get; set; } = combinedTau;
        public double CombinedPeakOpticalDepth { get; set; } = combinedPeakOpticalDepth;
        public double[] PriorTau { get; set; } = priorTau;
        public double ScreeningTauError { get; set; }
        public Dictionary<long, FineCellDeposit> CellDeposits { get; set; } = cellDeposits;
        public HashSet<AdmittedDarkCloud> EvaluatedResolved { get; } = evaluatedResolved;
        public HashSet<AdmittedFineDarkCloudGroup> EvaluatedFineGroups { get; } = evaluatedFineGroups;
        public Dictionary<AdmittedFineDarkCloudGroup, double[]> UnitTauByPriorFineGroup { get; } = unitTauByPriorFineGroup;
    }

    private sealed class DarkAttenuationLayerState(int resolvedSub)
    {
        public int ResolvedSub { get; } = resolvedSub;
        public Dictionary<long, CachedResolvedPixel> ResolvedPixels { get; } = [];
    }

    private sealed record PreSampledDarkCloud(
        AdmittedDarkCloud Cloud,
        bool IsResolved,
        int OwnSamples,
        Dictionary<long, double[]>? ResolvedOwnSubTaus,
        FineDarkCloudSample[]? FineSamples,
        Dictionary<long, FineCellDeposit>? SoloFineDeposits);

    private sealed record SoloScreenedDarkCloud(
        PreSampledDarkCloud Sampled,
        Dictionary<(int SourceIndex, int SampleIndex), double> TemporalTaus,
        Dictionary<int, DeepSkyPixelRate> SoloClippedDelta,
        double SoloRetained,
        HashSet<int> SoloActivePixels,
        HashSet<int> SoloNativePixels,
        RenderedObjectGeometry SoloFootprint,
        StellarSignalPrediction SoloPrediction);

    private sealed class CanonicalFineGroup(AdmittedFineDarkCloudGroup group)
    {
        public AdmittedFineDarkCloudGroup Group { get; } = group;
        public List<PreSampledDarkCloud> Members { get; } = [];
    }

    private readonly record struct CanonicalCrossPair(
        bool IsResolvedTarget,
        int TargetIndex,
        int FineIndex,
        double Strength);

    private sealed class TemporalUnresolvedPositionGroup(
        EquatorialPoint emitterJ2000,
        EnuVector midpointGeometricDirection)
    {
        public EquatorialPoint EmitterJ2000 { get; } = emitterJ2000;
        public EnuVector MidpointGeometricDirection { get; } = midpointGeometricDirection;
        public List<int> SourceIndices { get; } = [];
        public HashSet<long> UniqueSampleTicks { get; } = [];
    }

    private sealed class AdmittedDarkCloud(
        DeepSkyObject item,
        double? paJ2000Degrees,
        bool isCircle,
        DeepSkySimulationParameters profile,
        ResolvedSourceGeometry geometry,
        OutlineOrientedFrame? outlineFrame,
        double maxOutlineDist,
        double minSpanPixels,
        double maxSpanPixels,
        double projectedAreaEstimate,
        PaddedPixelBounds? paddedBounds)
    {
        public DeepSkyObject Item { get; } = item;
        public double? PaJ2000Degrees { get; } = paJ2000Degrees;
        public bool IsCircle { get; } = isCircle;
        public DeepSkySimulationParameters Profile { get; } = profile;
        public ResolvedSourceGeometry Geometry { get; } = geometry;
        public OutlineOrientedFrame? OutlineFrame { get; } = outlineFrame;
        public double MaxOutlineDist { get; set; } = maxOutlineDist;
        public double MinSpanPixels { get; } = minSpanPixels;
        public double MaxSpanPixels { get; } = maxSpanPixels;
        public double ProjectedAreaEstimate { get; } = projectedAreaEstimate;
        public PaddedPixelBounds? PaddedBounds { get; } = paddedBounds;
        public int ProfileEvaluations { get; set; }
        public int ReportedSamples { get; set; }
        public Dictionary<long, double>? StandaloneTransmission { get; set; }
    }

    private sealed class ProvisionalLuminousEntry(
        DeepSkyObject item,
        DeepSkyProfileClass profileClass,
        DeepSkyPhotometryBasis photometryBasis,
        ResolvedSourceGeometry geometry,
        DeepSkySimulationParameters profile,
        PixelPoint? temporalReferencePixel,
        double effectiveSourceRate,
        double visibleRate,
        double retainedMonoRate,
        int matchedHygCount,
        double matchedHygFluxFraction,
        int unresolvedMembers,
        int sampledPoints,
        int activePixelCount,
        bool usedUnresolvedPsf,
        bool routesPrePsfToDiffuseMap,
        Dictionary<long, double> sourceDeposits,
        Dictionary<int, double> nonDiffuseClippedRates,
        Dictionary<int, double> preCloudClippedRates,
        Dictionary<int, double> nonDiffuseTempPostRates,
        Dictionary<int, double> diffuseTempPostRates,
        Dictionary<int, double> combinedTempPostRates,
        Dictionary<int, double> combinedTempPreRates,
        List<UnresolvedDiffuseSample> unresolvedDiffuseSamples,
        double unresolvedDiffuseFluxRate,
        Dictionary<int, DeepSkyPixelRate>? ownDiffusePreCloud,
        Dictionary<int, DeepSkyPixelRate>? ownDiffuseTemporal,
        StellarSignalPrediction prediction,
        bool causesSaturation,
        RenderedObjectGeometry footprint)
    {
        public DeepSkyObject Item { get; } = item;
        public DeepSkyProfileClass ProfileClass { get; } = profileClass;
        public DeepSkyPhotometryBasis PhotometryBasis { get; } = photometryBasis;
        public ResolvedSourceGeometry Geometry { get; } = geometry;
        public DeepSkySimulationParameters Profile { get; } = profile;
        public PixelPoint? TemporalReferencePixel { get; } = temporalReferencePixel;
        public double EffectiveSourceRate { get; } = effectiveSourceRate;
        public double VisibleRate { get; } = visibleRate;
        public double RetainedMonoRate { get; } = retainedMonoRate;
        public int MatchedHygCount { get; } = matchedHygCount;
        public double MatchedHygFluxFraction { get; } = matchedHygFluxFraction;
        public int UnresolvedMembers { get; } = unresolvedMembers;
        public int SampledPoints { get; } = sampledPoints;
        public int ActivePixelCount { get; } = activePixelCount;
        public bool UsedUnresolvedPsf { get; } = usedUnresolvedPsf;
        public bool RoutesPrePsfToDiffuseMap { get; } = routesPrePsfToDiffuseMap;
        public Dictionary<long, double> SourceDeposits { get; } = sourceDeposits;
        public Dictionary<int, double> NonDiffuseClippedRates { get; } = nonDiffuseClippedRates;
        public Dictionary<int, double> PreCloudClippedRates { get; } = preCloudClippedRates;
        public Dictionary<int, double> NonDiffuseTempPostRates { get; } = nonDiffuseTempPostRates;
        public Dictionary<int, double> DiffuseTempPostRates { get; } = diffuseTempPostRates;
        public Dictionary<int, double> CombinedTempPostRates { get; } = combinedTempPostRates;
        public Dictionary<int, double> CombinedTempPreRates { get; } = combinedTempPreRates;
        public List<UnresolvedDiffuseSample> UnresolvedDiffuseSamples { get; } = unresolvedDiffuseSamples;
        public double UnresolvedDiffuseFluxRate { get; } = unresolvedDiffuseFluxRate;
        public Dictionary<int, DeepSkyPixelRate>? OwnDiffusePreCloud { get; } = ownDiffusePreCloud;
        public Dictionary<int, DeepSkyPixelRate>? OwnDiffuseTemporal { get; } = ownDiffuseTemporal;
        public StellarSignalPrediction Prediction { get; set; } = prediction;
        public bool CausesSaturation { get; set; } = causesSaturation;
        public RenderedObjectGeometry Footprint { get; } = footprint;
        public bool Demoted { get; set; }
    }

    private sealed record ResolvedSourceGeometry(
        DeepSkyGeometryBasis Basis,
        ResolvedSourceFrame Frame,
        AltAzPoint GeometricCenter,
        PixelPoint? CenterPixel,
        double SemiMajorRadians,
        double SemiMinorRadians,
        double PositionAngleOfDateDegrees,
        EnuVector MajorAxis,
        EnuVector MinorAxis,
        double MajorAxisPixels,
        double MinorAxisPixels,
        IReadOnlyList<PixelPoint[]>? TangentRings,
        double OutlineMinU,
        double OutlineMaxU,
        double OutlineMinV,
        double OutlineMaxV);
}
