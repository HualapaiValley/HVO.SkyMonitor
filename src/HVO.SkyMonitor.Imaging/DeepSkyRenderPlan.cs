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
    DeepSkySyntheticScenario? SyntheticScenario = null)
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
            SamplesPerProjectedPixel is < 1 or > 8 ||
            !double.IsFinite(PsfSigmaPixels) || PsfSigmaPixels is <= 0 or > 64 ||
            !double.IsFinite(PsfRadiusPixels) || PsfRadiusPixels is <= 0 or > 64 ||
            !Enum.IsDefined(HorizonPolicy) ||
            PreferredOutlineLevel is < DeepSkyOutline.WidestLevel or > DeepSkyOutline.NarrowestLevel ||
            !double.IsFinite(CelestialDiffuseBackgroundElectronsPerSecond) ||
            CelestialDiffuseBackgroundElectronsPerSecond is < 0 or > 1e12 ||
            DigitalBinX is < 1 or > 64 || DigitalBinY is < 1 or > 64 ||
            CfaOriginX is < 0 or > 1 || CfaOriginY is < 0 or > 1)
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

    /// <summary>Sources with projected major axis at or below this multiple of PSF sigma take the stellar PSF path.</summary>
    public const double UnresolvedDiameterPsfSigmaRatio = 0.5;

    private const double CompactDirectPsfDiameterSigmaRatio = 4.0;
    private const double PlateScaleProbeRadians = 1e-5;
    private const double ArcsecondsPerArcminute = 60d;

    private readonly Dictionary<int, DeepSkyPixelRate> _rates;

    private DeepSkyRenderPlan(
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        DateTimeOffset utc,
        ObserverLocation observer,
        Dictionary<int, DeepSkyPixelRate> rates,
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
        _rates = rates;
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
    public double ElectronRate(int x, int y, int channel = -1)
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
        var diffusePrePsf = new Dictionary<long, DeepSkyPixelRate>();
        var diagnostics = new List<DeepSkySourceDiagnostic>(candidates.Count);
        var darkCandidates = new List<DeepSkyObject>();

        var declaredDiffuseBg = settings.CelestialDiffuseBackgroundElectronsPerSecond +
            (settings.SyntheticScenario?.CelestialDiffuseBackgroundElectronsPerSecond ?? 0d);

        var totalSourceRate = 0d;
        var totalVisibleRate = 0d;
        var totalSampledPoints = 0L;
        var totalKernelVisits = 0L;
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
                projector, outlinesById, settings, out var skipReason);
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

            var isUnresolved = geometryInfo.Basis == DeepSkyGeometryBasis.UnresolvedPointSource ||
                (profileClass is not (DeepSkyProfileClass.OpenCluster or DeepSkyProfileClass.CompositeClusterNebula) &&
                 geometryInfo.MajorAxisPixels <= UnresolvedDiameterPsfSigmaRatio * settings.PsfSigmaPixels);

            var sourceDeposits = new Dictionary<long, double>();
            var directClippedRates = new Dictionary<int, double>();
            var visibleRate = 0d;
            var sampledPoints = 0;
            var matchedHygCount = 0;
            var matchedHygFluxFraction = 0d;
            var unresolvedMembers = 0;
            var usedUnresolvedPsf = false;
            var effectiveSourceRate = sourceRate;

            if (isUnresolved)
            {
                usedUnresolvedPsf = true;
                sampledPoints = 1;
                totalSampledPoints += 1;
                if (projector.TryProject(geometryInfo.Frame.Center, out var centerPixel))
                {
                    visibleRate = sourceRate;
                    DepositDirectSubpixelPsf(
                        directClippedRates, projection, centerPixel, sourceRate,
                        settings.PsfSigmaPixels, settings.PsfRadiusPixels,
                        settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits);
                }
            }
            else if (profileClass == DeepSkyProfileClass.OpenCluster)
            {
                var (matchedCount, matchedFlux) = MatchHygStarsInFootprint(hygStars, geometryInfo);
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

                unresolvedMembers = DepositUnresolvedClusterMembers(
                    item.Id, profile.MaximumUnresolvedClusterMembers, residualRate, geometryInfo, projector,
                    directClippedRates, projection, settings, ref totalSampledPoints, ref totalKernelVisits, out visibleRate);
                sampledPoints = unresolvedMembers;
            }
            else if (profileClass == DeepSkyProfileClass.CompositeClusterNebula)
            {
                var nebulaRate = sourceRate * profile.CompositeNebulaFluxFraction;
                var clusterBudget = sourceRate * (1d - profile.CompositeNebulaFluxFraction);
                var (matchedCount, matchedFlux) = MatchHygStarsInFootprint(hygStars, geometryInfo);
                matchedHygCount = matchedCount;
                matchedHygFluxFraction = clusterBudget > 0 ? matchedFlux / clusterBudget : 0d;
                var residualClusterRate = Math.Max(0d, clusterBudget - matchedFlux);
                effectiveSourceRate = nebulaRate + residualClusterRate;

                if (nebulaRate > 0)
                {
                    var nebVis = RasterizeExtendedProfile(
                        item.Id, profile, nebulaRate, geometryInfo, projector, projection,
                        settings, padding, paddedWidth, paddedHeight, sourceDeposits, directClippedRates,
                        ref totalSampledPoints, ref totalKernelVisits, out var nebSamples, cancellationToken);
                    visibleRate += nebVis;
                    sampledPoints += nebSamples;
                }
                if (residualClusterRate > 0)
                {
                    unresolvedMembers = DepositUnresolvedClusterMembers(
                        item.Id, profile.MaximumUnresolvedClusterMembers, residualClusterRate, geometryInfo, projector,
                        directClippedRates, projection, settings, ref totalSampledPoints, ref totalKernelVisits, out var clVis);
                    visibleRate += clVis;
                    sampledPoints += unresolvedMembers;
                }
            }
            else
            {
                visibleRate = RasterizeExtendedProfile(
                    item.Id, profile, sourceRate, geometryInfo, projector, projection,
                    settings, padding, paddedWidth, paddedHeight, sourceDeposits, directClippedRates,
                    ref totalSampledPoints, ref totalKernelVisits, out sampledPoints, cancellationToken);
            }

            // Convolve any padded bilinear deposits with the configured PSF and clip to sensor/aperture.
            if (sourceDeposits.Count > 0)
            {
                ConvolveToClipped(
                    sourceDeposits, directClippedRates, projection, kernel, padding, paddedWidth,
                    settings.MaximumSparsePixels, settings.MaximumKernelCellVisits, ref totalKernelVisits, cancellationToken);
            }

            if (directClippedRates.Count == 0)
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

            var (prediction, causesSaturation, footprint, retainedMonoRate) = EvaluateSignalAndFootprint(
                item.Id, geometryInfo, profile.Spectral, directClippedRates, effectiveSourceRate,
                projection, renderOptions, settings, cancellationToken);

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
                    sampledPoints, directClippedRates.Count, usedUnresolvedPsf, footprint));
                continue;
            }

            admittedCount++;
            if (usedUnresolvedPsf) unresolvedPsfCount++;
            totalSourceRate += effectiveSourceRate;
            totalVisibleRate += visibleRate;

            // Commit clipped per-channel rates into the shared sparse rate map.
            foreach (var (pixelIndex, monoRate) in directClippedRates)
            {
                var addition = ScaleRate(monoRate, profile.Spectral);
                AddSparseRate(rates, pixelIndex, addition, settings.MaximumSparsePixels);
            }

            // Record pre-PSF diffuse nebula light so overlapping dark nebulae can attenuate it cleanly.
            if (profileClass is DeepSkyProfileClass.DiffuseNebula or DeepSkyProfileClass.CompositeClusterNebula or DeepSkyProfileClass.SupernovaRemnant)
            {
                foreach (var (paddedKey, monoVal) in sourceDeposits)
                {
                    AddPaddedRate(diffusePrePsf, paddedKey, ScaleRate(monoVal, profile.Spectral), settings.MaximumSparsePixels);
                }
            }

            diagnostics.Add(new DeepSkySourceDiagnostic(
                item.Id, item.DisplayName, item.ObjectType, profileClass,
                Admitted: true,
                Reason: usedUnresolvedPsf ? "admitted-unresolved-psf" : "admitted",
                photometryBasis, geometryInfo.Basis,
                profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                profile.Spectral.Origin, profile.IsSimulationParameter,
                effectiveSourceRate, visibleRate, retainedMonoRate,
                effectiveSourceRate > 0 ? retainedMonoRate / effectiveSourceRate : 0d,
                matchedHygCount, matchedHygFluxFraction, unresolvedMembers, 0d, prediction,
                sampledPoints, directClippedRates.Count, usedUnresolvedPsf, footprint));
        }

        // Pass 2: Dark nebulae attenuate ONLY compatible celestial diffuse background (overlapping diffuse nebulae or
        // explicitly declared celestial diffuse background), never foreground terrestrial skyglow.
        foreach (var dark in darkCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            overridesById.TryGetValue(dark.Id, out var syntheticOverride);
            var profile = DeepSkySourceProfileResolver.Resolve(dark, syntheticOverride);
            var geometryInfo = ResolveGeometry(
                dark, syntheticOverride, DeepSkyProfileClass.DarkNebula, sceneRequest, basis, boresight, opticalRadiusDegrees,
                projector, outlinesById, settings, out var skipReason);
            if (skipReason is not null)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula, skipReason,
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }
            if (declaredDiffuseBg <= 0 && diffusePrePsf.Count == 0)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            eligibleCount++;
            var tauDeposits = new Dictionary<long, (double WeightSum, double TauSum)>();
            var darkSamples = RasterizeOpticalDepth(
                profile, geometryInfo, projector, projection, settings, padding, paddedWidth, paddedHeight,
                tauDeposits, ref totalSampledPoints, cancellationToken);

            var negativePrePsf = new Dictionary<long, DeepSkyPixelRate>();
            var attenuatedPrePsf = 0d;
            foreach (var (paddedKey, (weightSum, tauSum)) in tauDeposits)
            {
                if (weightSum <= 0) continue;
                var meanTau = Math.Clamp(tauSum / weightSum, 0d, profile.PeakOpticalDepth);
                var absorbedFraction = 1d - Math.Exp(-meanTau);
                if (absorbedFraction <= 0) continue;

                var existingDiffuse = diffusePrePsf.GetValueOrDefault(paddedKey);
                var bgMono = existingDiffuse.Mono + declaredDiffuseBg;
                var bgR = existingDiffuse.Red + declaredDiffuseBg;
                var bgG = existingDiffuse.Green + declaredDiffuseBg;
                var bgB = existingDiffuse.Blue + declaredDiffuseBg;
                if (bgMono <= 0 && bgR <= 0 && bgG <= 0 && bgB <= 0) continue;

                var delta = new DeepSkyPixelRate(
                    -bgMono * absorbedFraction,
                    -bgR * absorbedFraction,
                    -bgG * absorbedFraction,
                    -bgB * absorbedFraction);
                negativePrePsf[paddedKey] = delta;
                attenuatedPrePsf += -delta.Mono;
                if (diffusePrePsf.ContainsKey(paddedKey))
                {
                    diffusePrePsf[paddedKey] = new DeepSkyPixelRate(
                        existingDiffuse.Mono * (1d - absorbedFraction),
                        existingDiffuse.Red * (1d - absorbedFraction),
                        existingDiffuse.Green * (1d - absorbedFraction),
                        existingDiffuse.Blue * (1d - absorbedFraction));
                }
            }

            if (negativePrePsf.Count == 0 || attenuatedPrePsf <= 0)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-dark-no-compatible-background",
                    DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            var clippedDelta = new Dictionary<int, DeepSkyPixelRate>();
            foreach (var (paddedKey, negRate) in negativePrePsf)
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
                    AddSparseRate(rates, idx, scaled, settings.MaximumSparsePixels);
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
            foreach (var (idx, delta) in clippedDelta)
            {
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

            if (attenuatedRetained <= 0)
            {
                diagnostics.Add(CreateSkipped(dark, DeepSkyProfileClass.DarkNebula,
                    "annotation-only-outside-fov", DeepSkyPhotometryBasis.None, geometryInfo.Basis, profile));
                continue;
            }

            admittedCount++;
            darkAttenuationCount++;
            var centerRef = geometryInfo.CenterPixel ?? new PixelPoint(wx / sumW, wy / sumW);
            var centroid = new PixelPoint(wx / sumW, wy / sumW);
            diagnostics.Add(new DeepSkySourceDiagnostic(
                dark.Id, dark.DisplayName, dark.ObjectType, DeepSkyProfileClass.DarkNebula,
                Admitted: true, Reason: "admitted-dark-attenuation",
                DeepSkyPhotometryBasis.None, geometryInfo.Basis,
                profile.ProfileSchemaVersion, profile.MorphologyModel, profile.Spectral.SpectralModel,
                profile.Spectral.Origin, profile.IsSimulationParameter,
                0d, 0d, 0d, 0d, 0, 0d, 0, attenuatedRetained, default,
                darkSamples, clippedDelta.Count, false,
                new RenderedObjectGeometry(dark.Id, centerRef, centroid, 0d, 0d, 0d, minX, minY, maxX, maxY)));
        }

        diagnostics.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.ObjectId, right.ObjectId));
        var frozenDiagnostics = new ReadOnlyCollection<DeepSkySourceDiagnostic>(diagnostics);

        var retainedTotal = 0d;
        var maxRate = declaredDiffuseBg;
        foreach (var pixel in rates.Values)
        {
            var mono = Math.Max(0d, declaredDiffuseBg + pixel.Mono);
            var peak = Math.Max(mono, Math.Max(
                Math.Max(0d, declaredDiffuseBg + pixel.Red),
                Math.Max(Math.Max(0d, declaredDiffuseBg + pixel.Green), Math.Max(0d, declaredDiffuseBg + pixel.Blue))));
            retainedTotal += Math.Max(0d, pixel.Mono);
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
            rates.Count,
            totalKernelVisits,
            diagnosticsSha256);

        return new DeepSkyRenderPlan(
            projection, settings, sceneRequest.Utc, sceneRequest.Observer, rates,
            declaredDiffuseBg, totalSourceRate, totalVisibleRate, retainedTotal, maxRate,
            frozenDiagnostics, statistics);
    }

    private static string ComputeDiagnosticsSha256(
        IReadOnlyList<DeepSkySourceDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
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
                if (byId.Count >= settings.MaximumCandidates) break;
                byId.TryAdd(item.Id, item);
            }
        }
        else if (catalog is not null)
        {
            foreach (var item in catalog.Objects.Take(settings.MaximumCandidates))
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
        return byId.Values.OrderBy(static item => item.Id, StringComparer.Ordinal).ToList();
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
                opticalRadiusDegrees + reachDegrees + 0.5)
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
            opticalRadiusDegrees + semiMajorDeg + 0.5)
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

    private static double RasterizeExtendedProfile(
        string objectId,
        DeepSkySimulationParameters profile,
        double sourceRate,
        ResolvedSourceGeometry geometry,
        ResolvedSourceProjector projector,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        int padding,
        long paddedWidth,
        long paddedHeight,
        Dictionary<long, double> paddedDeposits,
        Dictionary<int, double> directClippedRates,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
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
            var maxAxis = (int)Math.Floor(Math.Sqrt(settings.MaximumSamplePointsPerSource));
            var minGrid = useDirectSubpixelPsf ? 10 : 20;
            var nu = (int)Math.Clamp(Math.Ceiling(spanU * angularScale * settings.SamplesPerProjectedPixel), minGrid, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(spanV * angularScale * settings.SamplesPerProjectedPixel), minGrid, maxAxis);
            ClampGridToBudget(ref nu, ref nv, settings.MaximumSamplePointsPerSource);

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
            var maxAxis = useDirectSubpixelPsf ? 16 : (int)Math.Floor(Math.Sqrt(settings.MaximumSamplePointsPerSource));
            var minAxis = useDirectSubpixelPsf ? 8 : 20;
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
        double residualRate,
        ResolvedSourceGeometry geometry,
        ResolvedSourceProjector projector,
        Dictionary<int, double> directClippedRates,
        ProjectionContext projection,
        DeepSkyRenderSettings settings,
        ref long totalSampledPoints,
        ref long totalKernelVisits,
        out double visibleRate)
    {
        var memberCount = Math.Clamp(
            (int)Math.Round(Math.Sqrt(Math.Max(16d, geometry.MajorAxisPixels * Math.Max(1d, geometry.MinorAxisPixels)))),
            4, maxMembers);
        var state = DeepSkySourceProfileResolver.ComputeStableObjectHash(objectId + ":unresolved-members");
        var members = new List<(EnuVector Direction, double Weight)>(memberCount);
        var totalWeight = 0d;

        for (var index = 0; index < memberCount; index++)
        {
            // Deterministic identity per unresolved member: "{objectId}:member:{index:D3}".
            var memberHash = DeepSkySourceProfileResolver.ComputeStableObjectHash($"{objectId}:member:{index:D3}");
            var direction = SampleMemberDirection(geometry, memberHash, ref state);
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

    private static EnuVector SampleMemberDirection(ResolvedSourceGeometry geometry, ulong seed, ref ulong state)
    {
        var local = seed ^ state;
        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            for (var attempt = 0; attempt < 64; attempt++)
            {
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
        Dictionary<long, (double WeightSum, double TauSum)> tauDeposits,
        ref long totalSampledPoints,
        CancellationToken cancellationToken)
    {
        var angularScale = ProjectionSamplingBounds.MaximumPixelsPerRadian(
            projection, geometry.GeometricCenter, Math.Max(1e-6, geometry.SemiMajorRadians));
        var maxAxis = (int)Math.Floor(Math.Sqrt(settings.MaximumSamplePointsPerSource));
        var sampled = 0;

        if (geometry.TangentRings is { Count: > 0 } rings)
        {
            var spanU = geometry.OutlineMaxU - geometry.OutlineMinU;
            var spanV = geometry.OutlineMaxV - geometry.OutlineMinV;
            var nu = (int)Math.Clamp(Math.Ceiling(spanU * angularScale * settings.SamplesPerProjectedPixel), 20, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(spanV * angularScale * settings.SamplesPerProjectedPixel), 20, maxAxis);
            ClampGridToBudget(ref nu, ref nv, settings.MaximumSamplePointsPerSource);
            var du = spanU / nu;
            var dv = spanV / nv;
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
                    var dist = DeepSkyOutlineSampler.DistanceToTangentBoundary(rings, u, v);
                    if (dist > maxDist) maxDist = dist;
                    interior.Add((u, v, dist));
                }
            }
            if (maxDist <= 0) return 0;
            foreach (var (u, v, dist) in interior)
            {
                sampled++;
                var tau = profile.PeakOpticalDepth * DeepSkySourceProfileResolver.EvaluateOutlineDensity(profile, dist / maxDist, 0d);
                var direction = (geometry.Frame.Center + geometry.Frame.CelestialEast * u + geometry.Frame.CelestialNorth * v).Normalize();
                if (projector.TryProject(direction, out var pixel))
                {
                    AccumulateTau(tauDeposits, pixel, tau, projection.WidthPixels, projection.HeightPixels, padding, paddedWidth, settings.MaximumSparsePixels);
                }
            }
        }
        else
        {
            var nu = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMajorRadians * angularScale * settings.SamplesPerProjectedPixel), 20, maxAxis);
            var nv = (int)Math.Clamp(Math.Ceiling(2d * geometry.SemiMinorRadians * angularScale * settings.SamplesPerProjectedPixel), 20, maxAxis);
            ClampGridToBudget(ref nu, ref nv, settings.MaximumSamplePointsPerSource);
            var du = 2d / nu;
            var dv = 2d / nv;
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
                    if (projector.TryProject(geometry.Frame.Direction(tangent), out var pixel))
                    {
                        AccumulateTau(tauDeposits, pixel, tau, projection.WidthPixels, projection.HeightPixels, padding, paddedWidth, settings.MaximumSparsePixels);
                    }
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

    private static (int Count, double TotalFluxRate) MatchHygStarsInFootprint(
        List<HygStarEntry> hygStars,
        ResolvedSourceGeometry geometry)
    {
        var count = 0;
        var flux = 0d;
        var frame = geometry.Frame;
        foreach (var star in hygStars)
        {
            var cosine = EnuVector.Dot(star.Direction, frame.Center);
            if (cosine <= DeepSkyOutlineSampler.MinimumTangentCosine) continue;
            if (geometry.TangentRings is { Count: > 0 } rings)
            {
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
                if (!output.TryGetValue(index, out var existing))
                {
                    if (output.Count >= maximumSparsePixels)
                    {
                        throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
                    }
                    output[index] = value;
                }
                else
                {
                    output[index] = existing + value;
                }
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

    private static void AccumulateTau(
        Dictionary<long, (double WeightSum, double TauSum)> deposits,
        PixelPoint point,
        double tau,
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
                deposits[key] = (w, w * tau);
            }
            else
            {
                deposits[key] = (prior.WeightSum + w, prior.TauSum + w * tau);
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
                var idx = py * projection.WidthPixels + px;
                if (!clipped.TryGetValue(idx, out var prior))
                {
                    if (clipped.Count >= maximumSparsePixels)
                    {
                        throw new InvalidOperationException("Deep-sky sparse pixel budget exceeded.");
                    }
                    clipped[idx] = add;
                }
                else
                {
                    clipped[idx] = prior + add;
                }
            }
        }
    }

    private static (StellarSignalPrediction Prediction, bool CausesSaturation, RenderedObjectGeometry Footprint, double RetainedMonoRate)
        EvaluateSignalAndFootprint(
            string objectId,
            ResolvedSourceGeometry geometry,
            DeepSkySpectralFactors spectral,
            Dictionary<int, double> clippedMonoRates,
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

        var maxCharge = response is null
            ? options.Gain > 0
                ? Math.Max(0, (options is Rgb24CompatibilityRenderOptions ? byte.MaxValue : ushort.MaxValue) - options.Bias) / options.Gain
                : double.MaxValue
            : Math.Min(response.FullWellElectrons, Math.Max(0, response.MaximumAdu - response.BlackLevelAdu) * response.ElectronsPerAdu);

        foreach (var (index, monoRate) in clippedMonoRates.OrderBy(static pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var x = index % projection.WidthPixels;
            var y = index / projection.WidthPixels;
            var vignette = 1d - options.VignettingStrength * projection.NormalizedRadiusSquared(x + 0.5, y + 0.5);
            var transmission = 1d;
            var scatter = 0d;
            if (projector is not null)
            {
                var dir = projector.Unproject(new PixelPoint(x + 0.5, y + 0.5));
                if (dir is not null)
                {
                    var eff = options.Cloud!.Field.Integrate(dir.Value, options.Cloud.IntegrationStartUtc, options.Cloud.IntegrationDuration);
                    transmission = eff.Transmission;
                    scatter = eff.Scatter;
                }
            }

            var channelMultiplier = options switch
            {
                BayerRggb16RenderOptions bayer =>
                    spectral.Channel(StellarExposureRenderPlan.CfaChannel(x, y, stellarSettings)) *
                    StellarExposureRenderPlan.Channel(bayer.ChannelResponse, StellarExposureRenderPlan.CfaChannel(x, y, stellarSettings)),
                _ => spectral.Mono
            };
            var skyChannel = options switch
            {
                BayerRggb16RenderOptions => StellarExposureRenderPlan.CfaChannel(x, y, stellarSettings),
                Rgb24CompatibilityRenderOptions => 1,
                _ => -1
            };

            var sourceElectrons = monoRate * channelMultiplier * transmission * vignette * options.ExposureSeconds;
            var bgElectrons = (options.BackgroundRate(x, y, skyChannel) * (transmission + scatter) +
                options.DiskRate(x, y, skyChannel) * transmission) * vignette * options.ExposureSeconds;
            signalTotal += extractionWeight * sourceElectrons;
            varianceTotal += extractionWeight * extractionWeight * (sourceElectrons + bgElectrons + darkElectrons + readVariance);
            if (sourceElectrons > 0 && bgElectrons + darkElectrons < maxCharge &&
                sourceElectrons + bgElectrons + darkElectrons >= maxCharge)
            {
                causesSaturation = true;
            }

            retainedMono += monoRate;
            weightedX += monoRate * (x + 0.5);
            weightedY += monoRate * (y + 0.5);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }

        var snr = varianceTotal > 0 ? signalTotal / Math.Sqrt(varianceTotal) : 0d;
        var prediction = new StellarSignalPrediction(signalTotal, varianceTotal, snr);
        var centroid = retainedMono > 0
            ? new PixelPoint(weightedX / retainedMono, weightedY / retainedMono)
            : geometry.CenterPixel ?? new PixelPoint(0, 0);
        var sourceRef = geometry.CenterPixel ?? centroid;
        var footprint = new RenderedObjectGeometry(
            ProjectedDeepSkyObject.SceneId(objectId),
            sourceRef,
            centroid,
            expectedSourceRate * options.ExposureSeconds,
            retainedMono * options.ExposureSeconds,
            expectedSourceRate > 0 ? retainedMono / expectedSourceRate : 0d,
            minX, minY, maxX, maxY);
        return (prediction, causesSaturation, footprint, retainedMono);
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
        if ((long)nu * nv <= maxSamples) return;
        var scale = Math.Sqrt((double)maxSamples / ((long)nu * nv));
        nu = Math.Max(4, (int)Math.Floor(nu * scale));
        nv = Math.Max(4, (int)Math.Floor(nv * scale));
    }

    private static DeepSkyPixelRate ScaleRate(double monoRate, DeepSkySpectralFactors spectral) =>
        new(monoRate * spectral.Mono, monoRate * spectral.Red, monoRate * spectral.Green, monoRate * spectral.Blue);

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

    private static void AddPaddedRate(Dictionary<long, DeepSkyPixelRate> map, long key, DeepSkyPixelRate add, int limit)
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
