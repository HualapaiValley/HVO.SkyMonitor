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
        var outlinesById = BuildOutlineLookup(catalog, settings.SyntheticScenario, candidates);
        var hygStars = BuildHygStarIndex(renderedHygStars, sceneRequest, renderOptions.MagnitudeZeroElectronsPerSecond);

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
        var diagnostics = new List<DeepSkySourceDiagnostic>(candidates.Count);
        var darkCandidates = new List<DeepSkyObject>();

        var provisionalLuminous = new List<ProvisionalLuminousEntry>();
        var overlapDiffusePreCloud = new Dictionary<int, DeepSkyPixelRate>();
        var overlapDiffuseTemporal = new Dictionary<int, DeepSkyPixelRate>();
        var anyDiffuseAddedAfterAnotherSource = false;

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

                var nebulaSampleCap = settings.MaximumSamplePointsPerSource;
                if (nebulaRate > 0 && residualClusterRate > 0 && !isUnresolvedSize)
                {
                    var clusterReserve = Math.Min(
                        profile.MaximumUnresolvedClusterMembers,
                        Math.Max(1, settings.MaximumSamplePointsPerSource / 4));
                    nebulaSampleCap = Math.Max(1, settings.MaximumSamplePointsPerSource - clusterReserve);
                }

                if (nebulaRate > 0)
                {
                    if (isUnresolvedSize)
                    {
                        usedUnresolvedPsf = true;
                        unresolvedDiffuseFluxRate = nebulaRate;
                        var nebVis = RasterizeUnresolvedTemporal(
                            item, nebulaRate, sceneRequest, exposureStartUtc, exposure,
                            geometryInfo, projector, projection, renderOptions, settings,
                            diffuseTempPostRates, diffuseTempPreRates, unresolvedDiffuseSamples,
                            ref totalSampledPoints, ref totalKernelVisits, out var nebSamples, out var nebRef,
                            cancellationToken);
                        visibleRate += nebVis;
                        sampledPoints += nebSamples;
                        temporalReferencePixel = nebRef;
                    }
                    else
                    {
                        var nebVis = RasterizeExtendedProfile(
                            item.Id, profile, nebulaRate, geometryInfo, projector, projection,
                            settings, nebulaSampleCap, padding, paddedWidth, paddedHeight, sourceDeposits, nonDiffuseClippedRates,
                            ref totalSampledPoints, ref totalKernelVisits, ref totalOutlineEdgeVisits,
                            out var nebSamples, cancellationToken);
                        visibleRate += nebVis;
                        sampledPoints += nebSamples;
                    }
                }
                if (residualClusterRate > 0)
                {
                    if (isUnresolvedSize)
                    {
                        usedUnresolvedPsf = true;
                        var clVis = RasterizeUnresolvedTemporal(
                            item, residualClusterRate, sceneRequest, exposureStartUtc, exposure,
                            geometryInfo, projector, projection, renderOptions, settings,
                            nonDiffuseTempPostRates, nonDiffuseTempPreRates, unresolvedDiffuseSamples: null,
                            ref totalSampledPoints, ref totalKernelVisits, out var clSamples, out var clRef,
                            cancellationToken);
                        visibleRate += clVis;
                        sampledPoints += clSamples;
                        temporalReferencePixel ??= clRef;
                    }
                    else
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
            if ((ownDiffusePreCloud is not null || ownDiffuseTemporal is not null) && provisionalLuminous.Count > 0)
            {
                anyDiffuseAddedAfterAnotherSource = true;
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
        }

        // Re-evaluate S/N and variance across all provisionally admitted luminous sources whenever a later-priority
        // diffuse source was admitted, so both admission and published diagnostics include ALL overlapping diffuse noise.
        if (anyDiffuseAddedAfterAnotherSource && provisionalLuminous.Count > 1)
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
        // terrestrial skyglow. Optical depths compose additively at common sky/subpixel positions before pixel averaging
        // (T_total = mean_subpixel(exp(-sum_k tau_k))), and dark sources enforce MinimumSignalToNoise before admission.
        var admittedDarkClouds = new List<AdmittedDarkCloud>();
        var cumulativeAttenuatedConvolved = new Dictionary<int, DeepSkyPixelRate>();
        var cumulativeTemporalAttenuated = new Dictionary<int, DeepSkyPixelRate>();
        var unresolvedDarkCumulativeTau = new Dictionary<(int SourceIndex, int SampleIndex), double>();

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
            if (declaredDiffuseBg <= 0 && diffusePrePsf.Count == 0 && admittedUnresolvedDiffuse.Count == 0)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            eligibleCount++;
            if (admittedCount >= settings.MaximumRenderedSources)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula, "budget-skipped",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            var cellDeposits = new Dictionary<long, (double AreaSum, double AbsorbAreaSum)>();
            var darkSamples = RasterizeOpticalDepth(
                profile, geometryInfo, projector, projection, settings, padding, paddedWidth, paddedHeight,
                admittedDarkClouds, cellDeposits, ref totalSampledPoints, ref totalOutlineEdgeVisits,
                out var maxOutlineDist, out var subpixelSamples, out var tinyOverlapUpdates, cancellationToken);

            var currentDarkCloud = new AdmittedDarkCloud(
                profile,
                geometryInfo,
                maxOutlineDist,
                Math.Max(geometryInfo.MajorAxisPixels, geometryInfo.MinorAxisPixels),
                subpixelSamples);

            var provisionalNegPrePsf = new Dictionary<long, DeepSkyPixelRate>();
            var provisionalTransmission = new Dictionary<long, double>();
            var attenuatedPrePsf = 0d;
            foreach (var (paddedKey, (areaSum, absorbAreaSum)) in cellDeposits)
            {
                if (areaSum <= 0 || absorbAreaSum <= 0) continue;
                // Subpixel clouds (areaSum < 1) preserve uncovered subpixel area; resolved pixels (areaSum >= 1)
                // normalize incremental absorbed area by covered area. Prior optical depth exp(-tau_prev) is already
                // integrated at each sky sample so deltaAlpha = T_prev - T_next exactly.
                var deltaAlpha = areaSum > 1d
                    ? Math.Clamp(absorbAreaSum / areaSum, 0d, 1d)
                    : Math.Clamp(absorbAreaSum, 0d, 1d);
                if (deltaAlpha <= 0) continue;

                var prevT = cumulativeTransmission.GetValueOrDefault(paddedKey, 1d);
                if (prevT <= 0) continue;
                var nextT = Math.Clamp(prevT - deltaAlpha, 0d, 1d);
                var actualDrop = prevT - nextT;
                if (actualDrop <= 0) continue;

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
                provisionalNegPrePsf[paddedKey] = delta;
                provisionalTransmission[paddedKey] = nextT;
                attenuatedPrePsf += -delta.Mono;
            }

            // Evaluate pre-PSF attenuation of admitted unresolved diffuse emitters at their exact sky sample directions.
            var temporalDarkDeficitPostCloud = new Dictionary<int, DeepSkyPixelRate>();
            var temporalDarkDeficitPreCloud = new Dictionary<int, DeepSkyPixelRate>();
            var provisionalUnresolvedTauUpdates = new Dictionary<(int SourceIndex, int SampleIndex), double>();
            for (var srcIdx = 0; srcIdx < admittedUnresolvedDiffuse.Count; srcIdx++)
            {
                var uSrc = admittedUnresolvedDiffuse[srcIdx];
                var samplePostDeficits = new List<WeightedPointSpreadSample>();
                for (var sampIdx = 0; sampIdx < uSrc.Samples.Count; sampIdx++)
                {
                    var sample = uSrc.Samples[sampIdx];
                    var tau = EvaluateDarkCloudOpticalDepth(
                        currentDarkCloud, sample.Direction, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                    if (tau <= 0) continue;
                    var key = (srcIdx, sampIdx);
                    var priorTau = unresolvedDarkCumulativeTau.GetValueOrDefault(key, 0d);
                    var deltaFraction = Math.Exp(-priorTau) * (1d - Math.Exp(-tau));
                    if (deltaFraction <= 0) continue;
                    provisionalUnresolvedTauUpdates[key] = priorTau + tau;
                    samplePostDeficits.Add(new WeightedPointSpreadSample(
                        sample.Pixel, sample.ExposureFraction * deltaFraction, sample.CloudTransmission));
                }
                if (samplePostDeficits.Count > 0)
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

            if ((provisionalNegPrePsf.Count == 0 && temporalDarkDeficitPreCloud.Count == 0) || attenuatedPrePsf <= 0)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            var clippedDelta = new Dictionary<int, DeepSkyPixelRate>();
            foreach (var (paddedKey, negRate) in provisionalNegPrePsf)
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
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-outside-fov", DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            var darkPrediction = EvaluateDarkSignalPrediction(
                activeDarkPixels,
                clippedDelta,
                temporalDarkDeficitPostCloud,
                declaredDiffuseBg,
                overlapDiffusePreCloud,
                overlapDiffuseTemporal,
                cumulativeAttenuatedConvolved,
                cumulativeTemporalAttenuated,
                projection,
                renderOptions,
                settings,
                cancellationToken);

            var centerRef = geometryInfo.CenterPixel ?? new PixelPoint(wx / sumW, wy / sumW);
            var centroid = new PixelPoint(wx / sumW, wy / sumW);
            var darkFootprint = new RenderedObjectGeometry(dark.Id, centerRef, centroid, 0d, 0d, 0d, minX, minY, maxX, maxY);

            var darkAdmitted = settings.MinimumSignalToNoise <= 0 ||
                darkPrediction.SignalToNoise >= settings.MinimumSignalToNoise;
            if (!darkAdmitted)
            {
                diagnostics.Add(new DeepSkySourceDiagnostic(
                    dark.Id, dark.DisplayName, dark.ObjectType, DeepSkyProfileClass.DarkNebula,
                    Admitted: false, Reason: "annotation-only-below-snr",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis,
                    profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                    profile.Spectral.Origin, profile.IsSimulationParameter,
                    0d, 0d, 0d, 0d, 0, 0d, 0, 0d, darkPrediction,
                    darkSamples, activeDarkPixels.Count, false, darkFootprint));
                continue;
            }

            // Commit transmission and optical-depth tracking only for admitted dark nebulae.
            foreach (var (paddedKey, nextT) in provisionalTransmission)
            {
                if (!prePsfSparsePixels.Contains(paddedKey) &&
                    prePsfSparsePixels.Count >= settings.MaximumSparsePixels)
                {
                    throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
                }
                prePsfSparsePixels.Add(paddedKey);
                cumulativeTransmission[paddedKey] = nextT;
            }
            foreach (var (key, nextTau) in provisionalUnresolvedTauUpdates)
            {
                unresolvedDarkCumulativeTau[key] = nextTau;
            }
            foreach (var (idx, delta) in clippedDelta)
            {
                AddUnboundedSparseRate(cumulativeAttenuatedConvolved, idx, delta);
            }
            foreach (var (idx, delta) in temporalDarkDeficitPostCloud)
            {
                AddUnboundedSparseRate(cumulativeTemporalAttenuated, idx, delta);
            }
            if (tinyOverlapUpdates is not null)
            {
                foreach (var (sampleRef, addTau) in tinyOverlapUpdates)
                {
                    sampleRef.CumulativeTau += addTau;
                }
            }
            admittedDarkClouds.Add(currentDarkCloud);

            admittedCount++;
            darkAttenuationCount++;
            diagnostics.Add(new DeepSkySourceDiagnostic(
                dark.Id, dark.DisplayName, dark.ObjectType, DeepSkyProfileClass.DarkNebula,
                Admitted: true, Reason: "admitted-dark-attenuation",
                DeepSkyPhotometryBasis.None, geometryInfo.Basis,
                profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                profile.Spectral.Origin, profile.IsSimulationParameter,
                0d, 0d, 0d, 0d, 0, 0d, 0, attenuatedRetained, darkPrediction,
                darkSamples, activeDarkPixels.Count, false, darkFootprint));
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
            candidates.Count,
            eligibleCount,
            admittedCount,
            candidates.Count - admittedCount,
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

    private static List<DeepSkyObject> CollectCandidates(
        IDeepSkyCatalog? catalog,
        IEnumerable<DeepSkyObject>? explicitCandidates,
        DeepSkyRenderSettings settings)
    {
        var byId = new Dictionary<string, DeepSkyObject>(StringComparer.Ordinal);
        if (explicitCandidates is not null)
        {
            foreach (var item in explicitCandidates)
            {
                ArgumentNullException.ThrowIfNull(item);
                byId.TryAdd(item.Id, item);
            }
        }
        else if (catalog is not null)
        {
            foreach (var item in catalog.Objects)
            {
                byId.TryAdd(item.Id, item);
            }
        }
        if (settings.SyntheticScenario?.AdditionalObjects is { Count: > 0 } synthetic)
        {
            foreach (var item in synthetic)
            {
                byId[item.Id] = item;
            }
        }
        var sorted = byId.Values.ToList();
        sorted.Sort(CompareCandidatePriority);
        if (sorted.Count > settings.MaximumCandidates)
        {
            sorted.RemoveRange(settings.MaximumCandidates, sorted.Count - settings.MaximumCandidates);
        }
        return sorted;
    }

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
            list.Add(new HygStarEntry(star.Id, CameraBasis.FromHorizontal(horizontal), fluxRate));
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
        IEnumerable<DeepSkyObject> candidates)
    {
        var map = new Dictionary<string, IReadOnlyList<DeepSkyOutline>>(StringComparer.Ordinal);
        if (catalog is not null)
        {
            foreach (var item in candidates)
            {
                var catalogOutlines = catalog.GetOutlines(item.Id);
                if (catalogOutlines.Count > 0)
                {
                    map[item.Id] = catalogOutlines;
                }
            }
        }
        if (scenario?.AdditionalOutlines is { Count: > 0 } syntheticOutlines)
        {
            foreach (var group in syntheticOutlines.GroupBy(static o => o.ObjectId, StringComparer.Ordinal))
            {
                map[group.Key] = group.OrderBy(static o => o.Level).ToArray();
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
                    transmission));
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
            cloudTransmission));
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

    private static int RasterizeOpticalDepth(
        DeepSkySimulationParameters profile,
        ResolvedSourceGeometry geometry,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        int padding,
        long paddedWidth,
        long paddedHeight,
        List<AdmittedDarkCloud> admittedDarkClouds,
        Dictionary<long, (double AreaSum, double AbsorbAreaSum)> cellDeposits,
        ref long totalSampledPoints,
        ref long totalOutlineEdgeVisits,
        out double maxOutlineDist,
        out List<SubpixelDarkSample>? subpixelSamples,
        out List<(SubpixelDarkSample Sample, double AddTau)>? tinyOverlapUpdates,
        CancellationToken cancellationToken)
    {
        maxOutlineDist = 0d;
        var currentMaxSpanPixels = Math.Max(geometry.MajorAxisPixels, geometry.MinorAxisPixels);
        subpixelSamples = currentMaxSpanPixels < 0.25 ? new List<SubpixelDarkSample>() : null;
        tinyOverlapUpdates = null;

        var angularScale = ProjectionSamplingBounds.MaximumPixelsPerRadian(
            projection, geometry.GeometricCenter, Math.Max(1e-6, geometry.SemiMajorRadians));
        var maxAxis = Math.Max(1, (int)Math.Floor(Math.Sqrt(settings.MaximumSamplePointsPerSource)));
        var minAxis = Math.Min(20, maxAxis);
        var centerPx2PerSr = geometry.SemiMajorRadians <= 0.05
            ? MeasureLocalAreaPixelsPerSteradian(projector, geometry.Frame.Center, geometry.Frame.CelestialNorth, geometry.Frame.CelestialEast)
            : null;
        var sampled = 0;

        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            var spanU = geometry.OutlineMaxU - geometry.OutlineMinU;
            var spanV = geometry.OutlineMaxV - geometry.OutlineMinV;
            var nu = (int)Math.Clamp(Math.Ceiling(spanU * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(spanV * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            ClampGridToBudget(ref nu, ref nv, settings.MaximumSamplePointsPerSource);

            var edgeCount = CountOutlineEdges(rings);
            ChargeOutlineEdgeVisits((long)nu * nv * edgeCount, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);

            var du = spanU / nu;
            var dv = spanV / nv;
            var cellArea = du * dv;
            var interior = new List<(double U, double V, double Dist)>();
            var maxDist = 0d;
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
                    if (dist > maxDist) maxDist = dist;
                    interior.Add((u, v, dist));
                }
            }
            maxOutlineDist = maxDist;
            if (maxDist <= 0) return 0;
            foreach (var (u, v, dist) in interior)
            {
                sampled++;
                var tau = profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateOutlineDensity(profile, dist / maxDist, 0d);
                var direction = (geometry.Frame.Center + geometry.Frame.CelestialEast * u + geometry.Frame.CelestialNorth * v).Normalize();
                if (!projector.TryProject(direction, out var pixel)) continue;
                var px2PerSr = centerPx2PerSr ?? MeasureLocalAreaPixelsPerSteradian(projector, direction);
                if (px2PerSr is not { } areaScale || areaScale <= 0) continue;
                var rSq = u * u + v * v;
                var solidAngle = cellArea * Math.Pow(1d + rSq, -1.5);
                var pixelArea = solidAngle * areaScale;
                var priorTau = EvaluatePriorDarkOpticalDepth(
                    admittedDarkClouds, direction, currentMaxSpanPixels,
                    settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                var absorbArea = pixelArea * Math.Exp(-priorTau) * (1d - Math.Exp(-tau));
                AccumulateDarkAbsorption(
                    cellDeposits, pixel, pixelArea, absorbArea,
                    projection.WidthPixels, projection.HeightPixels, padding, paddedWidth, settings.MaximumSparsePixels);
                subpixelSamples?.Add(new SubpixelDarkSample(direction, pixel, pixelArea, priorTau + tau));
            }
        }
        else
        {
            var nu = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMajorRadians * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMinorRadians * angularScale * settings.SamplesPerProjectedPixel), minAxis, maxAxis);
            ClampGridToBudget(ref nu, ref nv, settings.MaximumSamplePointsPerSource);
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
                    sampled++;
                    var tau = profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateRadialDensity(profile, Math.Sqrt(qSq), 0d, 0UL);
                    var tangent = geometry.MajorAxis * (u * geometry.SemiMajorRadians) +
                        geometry.MinorAxis * (v * geometry.SemiMinorRadians);
                    var direction = geometry.Frame.Direction(tangent);
                    if (!projector.TryProject(direction, out var pixel)) continue;
                    var px2PerSr = centerPx2PerSr ?? MeasureLocalAreaPixelsPerSteradian(projector, direction);
                    if (px2PerSr is not { } areaScale || areaScale <= 0) continue;
                    var r = tangent.Length;
                    var solidAngle = cellArea * (r < 1e-8 ? 1d : Math.Sin(r) / r);
                    var pixelArea = solidAngle * areaScale;
                    var priorTau = EvaluatePriorDarkOpticalDepth(
                        admittedDarkClouds, direction, currentMaxSpanPixels,
                        settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                    var absorbArea = pixelArea * Math.Exp(-priorTau) * (1d - Math.Exp(-tau));
                    AccumulateDarkAbsorption(
                        cellDeposits, pixel, pixelArea, absorbArea,
                        projection.WidthPixels, projection.HeightPixels, padding, paddedWidth, settings.MaximumSparsePixels);
                    subpixelSamples?.Add(new SubpixelDarkSample(direction, pixel, pixelArea, priorTau + tau));
                }
            }
        }

        // If a coarse resolved dark cloud overlaps an earlier-admitted strictly subpixel dark cloud, subtract
        // the overlap over-count at the subpixel cloud's exact fine samples so optical-depth additivity is
        // preserved regardless of admission order.
        if (currentMaxSpanPixels >= 2.0 && admittedDarkClouds.Count > 0)
        {
            var probeCloud = new AdmittedDarkCloud(profile, geometry, maxOutlineDist, currentMaxSpanPixels, null);
            foreach (var prior in admittedDarkClouds)
            {
                if (prior.SubpixelSamples is not { Count: > 0 } priorSamples) continue;
                var sep = AngularSeparationRadians(geometry.Frame.Center, prior.Geometry.Frame.Center);
                if (sep > geometry.SemiMajorRadians + prior.Geometry.SemiMajorRadians + 1e-6) continue;
                foreach (var s in priorSamples)
                {
                    var tauCurrent = EvaluateDarkCloudOpticalDepth(
                        probeCloud, s.Direction, settings.MaximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
                    if (tauCurrent <= 0) continue;
                    var overlapSubtract = s.PixelArea * (1d - Math.Exp(-s.CumulativeTau)) * (1d - Math.Exp(-tauCurrent));
                    SubtractDarkAbsorption(
                        cellDeposits, s.Pixel, overlapSubtract,
                        projection.WidthPixels, projection.HeightPixels, padding, paddedWidth);
                    (tinyOverlapUpdates ??= []).Add((s, tauCurrent));
                }
            }
        }

        totalSampledPoints += sampled;
        if (totalSampledPoints > settings.MaximumTotalSamplePoints)
        {
            throw new InvalidOperationException("Deep-sky sampling point budget exceeded.");
        }
        return sampled;
    }

    private static double EvaluatePriorDarkOpticalDepth(
        List<AdmittedDarkCloud> admittedDarkClouds,
        EnuVector direction,
        double currentMaxSpanPixels,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        if (admittedDarkClouds.Count == 0) return 0d;
        var totalTau = 0d;
        for (var i = 0; i < admittedDarkClouds.Count; i++)
        {
            var prior = admittedDarkClouds[i];
            if (prior.MaxSpanPixels < 0.25 && currentMaxSpanPixels >= 2.0)
            {
                // Handled via exact fine-sample overlap subtraction above.
                continue;
            }
            totalTau += EvaluateDarkCloudOpticalDepth(prior, direction, maximumOutlineEdgeVisits, ref totalOutlineEdgeVisits);
        }
        return totalTau;
    }

    private static double EvaluateDarkCloudOpticalDepth(
        AdmittedDarkCloud cloud,
        EnuVector direction,
        long maximumOutlineEdgeVisits,
        ref long totalOutlineEdgeVisits)
    {
        var geometry = cloud.Geometry;
        var profile = cloud.Profile;
        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            if (cloud.MaxOutlineDist <= 0) return 0d;
            var cosine = EnuVector.Dot(direction, geometry.Frame.Center);
            if (cosine <= DeepSkyOutlineSampler.MinimumTangentCosine) return 0d;
            var u = EnuVector.Dot(direction, geometry.Frame.CelestialEast) / cosine;
            var v = EnuVector.Dot(direction, geometry.Frame.CelestialNorth) / cosine;
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
        var sinMajor = EnuVector.Dot(direction, geometry.MajorAxis);
        var sinMinor = EnuVector.Dot(direction, geometry.MinorAxis);
        var sinAngle = double.Hypot(sinMajor, sinMinor);
        var cosAngle = EnuVector.Dot(direction, geometry.Frame.Center);
        var angle = Math.Atan2(sinAngle, cosAngle);
        if (angle > geometry.SemiMajorRadians * (1d + 1e-9)) return 0d;
        var scale = sinAngle > 1e-15 ? angle / sinAngle : 1d;
        var eu = (sinMajor * scale) / geometry.SemiMajorRadians;
        var ev = (sinMinor * scale) / geometry.SemiMinorRadians;
        var qSq = eu * eu + ev * ev;
        if (qSq >= 1d) return 0d;
        return profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateRadialDensity(profile, Math.Sqrt(qSq), 0d, 0UL);
    }

    private static double AngularSeparationRadians(EnuVector a, EnuVector b)
    {
        var cross = EnuVector.Cross(a, b).Length;
        var dot = EnuVector.Dot(a, b);
        return Math.Atan2(cross, dot);
    }

    private static void SubtractDarkAbsorption(
        Dictionary<long, (double AreaSum, double AbsorbAreaSum)> deposits,
        PixelPoint point,
        double subtractAbsorbArea,
        int width,
        int height,
        int padding,
        long paddedWidth)
    {
        if (subtractAbsorbArea <= 0) return;
        var x = (long)Math.Floor(point.X - 0.5);
        var y = (long)Math.Floor(point.Y - 0.5);
        var fx = point.X - 0.5 - x;
        var fy = point.Y - 0.5 - y;
        Sub(x, y, (1d - fx) * (1d - fy));
        Sub(x + 1, y, fx * (1d - fy));
        Sub(x, y + 1, (1d - fx) * fy);
        Sub(x + 1, y + 1, fx * fy);

        void Sub(long cx, long cy, double w)
        {
            if (w <= 0 || cx < -padding || cx >= width + padding || cy < -padding || cy >= height + padding) return;
            var key = (cy + padding) * paddedWidth + cx + padding;
            if (deposits.TryGetValue(key, out var prior))
            {
                deposits[key] = (prior.AreaSum, Math.Max(0d, prior.AbsorbAreaSum - w * subtractAbsorbArea));
            }
        }
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

    private static void AccumulateDarkAbsorption(
        Dictionary<long, (double AreaSum, double AbsorbAreaSum)> deposits,
        PixelPoint point,
        double pixelArea,
        double absorbArea,
        int width,
        int height,
        int padding,
        long paddedWidth,
        int maximumSparsePixels)
    {
        if (pixelArea <= 0) return;
        var x = (long)Math.Floor(point.X - 0.5);
        var y = (long)Math.Floor(point.Y - 0.5);
        var fx = point.X - 0.5 - x;
        var fy = point.Y - 0.5 - y;
        Add(x, y, (1d - fx) * (1d - fy));
        Add(x + 1, y, fx * (1d - fy));
        Add(x, y + 1, (1d - fx) * fy);
        Add(x + 1, y + 1, fx * fy);

        void Add(long cx, long cy, double w)
        {
            if (w <= 0 || cx < -padding || cx >= width + padding || cy < -padding || cy >= height + padding) return;
            var key = (cy + padding) * paddedWidth + cx + padding;
            if (!deposits.TryGetValue(key, out var prior))
            {
                if (deposits.Count >= maximumSparsePixels)
                {
                    throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
                }
                deposits[key] = (w * pixelArea, w * absorbArea);
            }
            else
            {
                deposits[key] = (prior.AreaSum + w * pixelArea, prior.AbsorbAreaSum + w * absorbArea);
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
        HashSet<int> activeDarkPixels,
        Dictionary<int, DeepSkyPixelRate> preCloudDarkDelta,
        Dictionary<int, DeepSkyPixelRate> temporalDarkDelta,
        double declaredDiffuseBg,
        Dictionary<int, DeepSkyPixelRate> overlapDiffusePreCloud,
        Dictionary<int, DeepSkyPixelRate> overlapDiffuseTemporal,
        Dictionary<int, DeepSkyPixelRate> priorDarkAttenuatedPreCloud,
        Dictionary<int, DeepSkyPixelRate> priorDarkAttenuatedTemporal,
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

        var nativePixels = CompleteBinSupport(activeDarkPixels, projection, settings);
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

                // Null-hypothesis local photon rate before this dark cloud attenuates the extraction footprint.
                var preDiffuseBefore = Math.Max(
                    preDeficit,
                    Math.Max(0d, declaredDiffuseBg + overlapPreRate.Channel(skyChannel) + priorPreAtten.Channel(skyChannel)) * cfaScale);
                var tmpDiffuseBefore = Math.Max(
                    tmpDeficit,
                    Math.Max(0d, overlapTmpRate.Channel(skyChannel) + priorTmpAtten.Channel(skyChannel)) * cfaScale);
                var bgBeforeElectrons = (options.BackgroundRate(x, y, skyChannel) * backgroundTransmission +
                    (options.DiskRate(x, y, skyChannel) + preDiffuseBefore) * diskTransmission +
                    tmpDiffuseBefore) * vignette * options.ExposureSeconds;

                var channelWeight = maximumDisplayWeight > 0 ? displayWeights[channel] / maximumDisplayWeight : 0d;
                if (response is null && options.Gain == 0)
                {
                    channelWeight = 0d;
                }
                var weight = extractionWeight * channelWeight;
                signalTotal += weight * deficitElectrons;
                varianceTotal += weight * weight * (bgBeforeElectrons + darkElectrons + readVariance);
            }
        }

        var snr = varianceTotal > 0 ? signalTotal / Math.Sqrt(varianceTotal) : 0d;
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

    private readonly record struct HygStarEntry(string Id, EnuVector Direction, double FluxRate);

    private readonly record struct UnresolvedDiffuseSample(
        EnuVector Direction,
        PixelPoint Pixel,
        double ExposureFraction,
        double CloudTransmission);

    private sealed record AdmittedUnresolvedDiffuseSource(
        string ObjectId,
        double FluxRate,
        DeepSkySpectralFactors Spectral,
        List<UnresolvedDiffuseSample> Samples,
        Dictionary<int, double> UnattenuatedPostCloudRates);

    private sealed class SubpixelDarkSample(
        EnuVector direction,
        PixelPoint pixel,
        double pixelArea,
        double cumulativeTau)
    {
        public EnuVector Direction { get; } = direction;
        public PixelPoint Pixel { get; } = pixel;
        public double PixelArea { get; } = pixelArea;
        public double CumulativeTau { get; set; } = cumulativeTau;
    }

    private sealed record AdmittedDarkCloud(
        DeepSkySimulationParameters Profile,
        ResolvedSourceGeometry Geometry,
        double MaxOutlineDist,
        double MaxSpanPixels,
        List<SubpixelDarkSample>? SubpixelSamples);

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
