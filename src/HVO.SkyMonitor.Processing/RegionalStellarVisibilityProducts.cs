using System.Collections.ObjectModel;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing;

/// <summary>Optional calibration defect mask passed to step 30b when reference calibration is active.</summary>
public sealed record RegionalVisibilityDefectMaskInput(
    Guid ArtifactId,
    string PayloadSha256,
    Linear16Frame Frame);

/// <summary>Per-tile locally measured sky background and noise sigma in linear ADU.</summary>
public sealed record RegionalVisibilityTileBackgroundV1(
    bool Available,
    double? BackgroundAdu,
    double? NoiseSigmaAdu);

/// <summary>All explicit inputs required to assess regional stellar visibility for one exposure (step 30b).</summary>
public sealed record RegionalStellarVisibilityRequest(
    ProcessingArtifact Raw,
    ProcessingArtifact? MeasurementArtifact,
    ExposureMeasurementV1? Measurement,
    ProcessingArtifact? PredictionArtifact,
    ProjectedSceneV1? Prediction,
    double ExposureSeconds,
    double Gain,
    RegionalStellarVisibilityOptions Options,
    ProcessingArtifact? AstrometryArtifact = null,
    MeasuredAstrometrySolutionV1? AstrometrySolution = null,
    AstrometricMapping? AcceptedMapping = null,
    HorizonObstructionEvaluator? HorizonEvaluator = null,
    CloudAssessmentEnvironmentV1? Environment = null,
    RegionalVisibilityDefectMaskInput? DefectMask = null,
    SolarDiskAppearance? MoonOverride = null,
    double? SunAltitudeDegreesOverride = null,
    IReadOnlyList<RegionalVisibilityTileBackgroundV1>? RegionalBackground = null,
    string? ResponseMismatchReason = null);

/// <summary>
/// Host-neutral step 30b producer estimating regional cloud evidence from independent catalog star predictions
/// and saved measured detections. Retains provenance separately from the clear-reference method (#105/#1004).
/// </summary>
public static class RegionalStellarVisibilityProducts
{
    public const string StepAlias = "RegionalStellarVisibility";
    public const string RecipeName = "regional-stellar-visibility";
    public const string ImplementationVersion = "regional-stellar-visibility-v1";
    public const string SchemaVersion = RegionalStellarVisibilityV1.CurrentSchemaVersion;
    public const string Variant = SchemaVersion;
    public const string MediaType = "application/vnd.hvo.regional-stellar-visibility+json";
    public const string LayerKind = "regional-stellar-visibility";

    private const int OneMillion = 1_000_000;
    private const double BayerNoiseCorrelationFactor = 3.0;
    private const int TileAreaSampleGrid = 8;
    private static readonly double FwhmPerSigma = 2.0 * Math.Sqrt(2.0 * Math.Log(2.0));
    private static readonly AstronomyEnginePlanetEphemeris SolarEphemeris = new();

    public static ProcessingRecipeDefinition Definition { get; } =
        new(RecipeName, "1.0.0", ImplementationVersion, ProcessingOperationKind.Analyzer);

    public static IReadOnlyList<ProcessingAlgorithmIdentity> Algorithms { get; } =
    [
        new("regional-stellar-visibility-policy", "v1"),
        new("stellar-visibility-model", StellarVisibilityModel.AlgorithmVersion),
        new("stellar-visibility-matching", "one-to-one-rigid-check-v1")
    ];

    /// <summary>Produces a canonical, self-validating regional stellar visibility assessment product for one exposure.</summary>
    public static ProcessingOutcome Assess(
        RegionalStellarVisibilityRequest request,
        string outputVariant = Variant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Raw);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVariant);
        cancellationToken.ThrowIfCancellationRequested();

        request.Options.Validate();
        var options = request.Options;
        var raw = request.Raw;
        if (raw.Role != FrameArtifactRole.Raw || raw.Layout is not { } rawLayout || raw.CaptureId is not { } captureId ||
            captureId == Guid.Empty || string.IsNullOrWhiteSpace(raw.DescriptorIdentitySha256))
        {
            return ProcessingOutcome.TerminalFailure("visibility.invalid-raw-input");
        }
        if (rawLayout.Width < 1 || rawLayout.Height < 1 ||
            options.GridColumns > rawLayout.Width || options.GridRows > rawLayout.Height)
        {
            return ProcessingOutcome.TerminalFailure("visibility.invalid-grid-dimensions");
        }

        var sources = BuildSourceArtifactList(request);
        var sourceProvenance = new RegionalVisibilitySourceV1(
            captureId,
            raw.ArtifactId,
            raw.DescriptorIdentitySha256.ToUpperInvariant(),
            request.MeasurementArtifact?.ArtifactId,
            ResolveContentSha256(request.MeasurementArtifact),
            request.PredictionArtifact?.ArtifactId,
            ResolveContentSha256(request.PredictionArtifact),
            request.AstrometryArtifact?.ArtifactId,
            ResolveContentSha256(request.AstrometryArtifact),
            request.DefectMask?.ArtifactId,
            request.DefectMask?.PayloadSha256?.ToUpperInvariant());

        var recipeIdentity = CreateRecipeIdentity(request, options, sourceProvenance);
        var assessment = Evaluate(request, options, sourceProvenance, recipeIdentity.IdentitySha256, cancellationToken);
        var payload = RegionalStellarVisibilityJson.Serialize(assessment);

        return ProcessingOutcome.Produced(ProcessingRecipeSupport.CreateProduct(
            FrameArtifactRole.Metadata,
            outputVariant,
            MediaType,
            null,
            payload,
            recipeIdentity,
            Algorithms,
            sources,
            raw.Integration,
            raw.Compatibility,
            ProcessingProductKind.Metadata,
            SchemaVersion,
            assessment.AssessmentIdentitySha256));
    }

    public static RegionalStellarVisibilityV1 Read(ProcessingProduct product)
    {
        ArgumentNullException.ThrowIfNull(product);
        if (product.Role != FrameArtifactRole.Metadata || product.Kind != ProcessingProductKind.Metadata ||
            product.MediaType != MediaType || product.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException("Invalid regional stellar visibility product envelope.");
        }

        var parsed = RegionalStellarVisibilityJson.Parse(product.Payload);
        if (!parsed.IsValid || parsed.Assessment is not { } assessment ||
            !ExposureMeasurementProducts.Same(assessment.AssessmentIdentitySha256, product.ContentIdentitySha256) ||
            !ExposureMeasurementProducts.Same(assessment.RecipeIdentitySha256, product.Recipe.IdentitySha256))
        {
            throw new InvalidDataException("Invalid regional stellar visibility product content or identity.");
        }

        return assessment;
    }

    public static RegionalStellarVisibilityV1 Read(ProcessingArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Role != FrameArtifactRole.Metadata || artifact.ProductKind != ProcessingProductKind.Metadata ||
            artifact.MediaType != MediaType || artifact.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException("Invalid regional stellar visibility artifact envelope.");
        }

        var parsed = RegionalStellarVisibilityJson.Parse(artifact.Payload);
        if (!parsed.IsValid || parsed.Assessment is not { } assessment ||
            !ExposureMeasurementProducts.Same(assessment.AssessmentIdentitySha256, artifact.ContentIdentitySha256) ||
            !ExposureMeasurementProducts.Same(assessment.RecipeIdentitySha256, artifact.RecipeIdentitySha256))
        {
            throw new InvalidDataException("Invalid regional stellar visibility artifact content or identity.");
        }

        return assessment;
    }

    private static RegionalStellarVisibilityV1 Evaluate(
        RegionalStellarVisibilityRequest request,
        RegionalStellarVisibilityOptions options,
        RegionalVisibilitySourceV1 sourceProvenance,
        string recipeIdentitySha256,
        CancellationToken cancellationToken)
    {
        var raw = request.Raw;
        var rawLayout = raw.Layout!;
        var width = rawLayout.Width;
        var height = rawLayout.Height;
        var reasons = new List<string>();
        var withholdingReasons = new List<string>();

        var environment = request.Environment ?? new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            null,
            EnvironmentalObservationMatchStatus.Missing,
            null,
            null,
            false);

        // Verify input provenance against the admitted raw capture/artifact before trusting measurement or prediction.
        var measurement = VerifyMeasurementInput(raw, rawLayout, request.MeasurementArtifact, request.Measurement);
        var (prediction, simulatorTruthRejected) = VerifyPredictionInput(
            raw, rawLayout, request.PredictionArtifact, request.Prediction, measurement);

        // Compute Sun and Moon geometry when independent prediction supplies observer/time.
        double? sunAltitude = request.SunAltitudeDegreesOverride;
        CaptureSolarRegime? solarRegime = environment.SolarRegime;
        SolarDiskAppearance? moon = request.MoonOverride;
        if (prediction is { } predForAstro)
        {
            var observer = new ObserverLocation(
                predForAstro.Observer.LatitudeDegrees,
                predForAstro.Observer.LongitudeDegrees,
                predForAstro.Observer.ElevationMeters);
            if (sunAltitude is null)
            {
                var solar = SolarAltitudeClassifier.Classify(
                    SolarEphemeris, predForAstro.EffectiveUtc,
                    observer.LatitudeDegrees, observer.LongitudeDegrees, 0, -18);
                sunAltitude = solar.AltitudeDegrees;
                solarRegime ??= solar.Regime switch
                {
                    SolarAltitudeRegime.Day => CaptureSolarRegime.Day,
                    SolarAltitudeRegime.Twilight => CaptureSolarRegime.Twilight,
                    _ => CaptureSolarRegime.Night
                };
            }
            else if (solarRegime is null)
            {
                solarRegime = sunAltitude >= 0
                    ? CaptureSolarRegime.Day
                    : sunAltitude > -18
                        ? CaptureSolarRegime.Twilight
                        : CaptureSolarRegime.Night;
            }

            moon ??= SolarDiskEphemeris.Get(SolarSystemBody.Moon, predForAstro.EffectiveUtc, observer);
        }
        else if (sunAltitude is not null && solarRegime is null)
        {
            solarRegime = sunAltitude >= 0
                ? CaptureSolarRegime.Day
                : sunAltitude > -18
                    ? CaptureSolarRegime.Twilight
                    : CaptureSolarRegime.Night;
        }

        var moonInFov = false;
        if (moon is { Direction.AltitudeDegrees: > 0 } aboveMoon && prediction is { } predForMoon)
        {
            var proj = ProjectedSceneJson.ToProjectionContext(predForMoon.Projection);
            var projector = ProjectorFactory.Create(proj);
            var projectedMoon = projector.Project(aboveMoon.Direction);
            if (projectedMoon is { } moonPixel && new ProjectedSceneAperture(predForMoon).Contains(moonPixel))
            {
                moonInFov = true;
            }
        }

        // Compute saturation fraction from saved step 4 brightness/clipping.
        var saturatedFractionMillionths = ComputeSaturatedFractionMillionths(measurement, rawLayout);

        // Focus inspection from step 4c HFD + step 4b too-broad exclusions.
        var (focusBasis, medianHfd, validFocusSamples, tooBroadCount) = EvaluateFocus(measurement, options);

        // Check structural prerequisites first.
        var unsupportedFormat = rawLayout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16);
        if (unsupportedFormat)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.UnsupportedFormat);
        }

        if (!double.IsFinite(request.ExposureSeconds) || request.ExposureSeconds <= 0)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.ZeroExposure);
        }

        var calibrationDisposition = measurement?.Input.Calibration;
        if (measurement is null)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.MeasurementUnavailable);
        }
        else
        {
            if (measurement.Input.Linear is null ||
                measurement.Input.Calibration is not (ExposureCalibrationDisposition.ReferenceCalibrated or ExposureCalibrationDisposition.NormalizationOnly))
            {
                AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.MissingLinearCalibration);
            }
            if (measurement.Sources.Status == ExposureMeasurementStatus.Unavailable ||
                measurement.Sources.Status == ExposureMeasurementStatus.Unsuitable && measurement.Sources.Reason != "all-candidates-rejected")
            {
                AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.MeasurementUnavailable);
            }
        }

        // Verify optional/required defect mask against dimensions and checksum; require it for reference-calibrated exposures.
        Linear16Frame? verifiedDefectMask = null;
        if (request.DefectMask is { } defectInput)
        {
            if (defectInput.ArtifactId != Guid.Empty &&
                defectInput.Frame.Width == width &&
                defectInput.Frame.Height == height &&
                ExposureMeasurementProducts.Same(
                    PayloadChecksum.ComputeSha256(defectInput.Frame.PixelData.Span),
                    defectInput.PayloadSha256))
            {
                verifiedDefectMask = defectInput.Frame;
            }
            else
            {
                AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.DefectMaskUnavailable);
            }
        }
        else if (calibrationDisposition == ExposureCalibrationDisposition.ReferenceCalibrated)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.DefectMaskUnavailable);
        }

        if (simulatorTruthRejected)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.SimulatorTruthRejected);
        }
        else if (prediction is null)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.PredictionUnavailable);
        }

        if (request.HorizonEvaluator is { IsCompatible: false })
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.HorizonIncompatible);
        }

        // Resolve photometric response profile bound to the actual admitted exposure layout.
        var measuredLayout = measurement?.Input.Linear?.Layout ?? rawLayout;
        MonoSensorResponse? resolvedSensor = null;
        double redResp = 1, greenResp = 1, blueResp = 1;
        var profile = options.PhotometricProfile;
        if (!string.IsNullOrWhiteSpace(request.ResponseMismatchReason))
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.KnownGlobal.Contains(request.ResponseMismatchReason)
                ? request.ResponseMismatchReason
                : RegionalVisibilityReasonCodes.ResponseBasisMismatch);
        }
        else if (profile is null)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.MissingSensorResponse);
        }
        else if (!profile.TryResolve(request.Gain, rawLayout, measuredLayout,
            out resolvedSensor, out redResp, out greenResp, out blueResp, out var responseFailure))
        {
            AddUnique(withholdingReasons, responseFailure ?? RegionalVisibilityReasonCodes.ResponseBasisMismatch);
        }

        // Environmental / confounder gates.
        if (solarRegime == CaptureSolarRegime.Day)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.Daylight);
        }
        else if (solarRegime == CaptureSolarRegime.Twilight)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.Twilight);
        }

        if (environment.PrecipitationDetected)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.PrecipitationContaminated);
        }

        if (saturatedFractionMillionths > options.MaximumSaturatedFractionMillionths)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.SaturationContaminated);
        }

        if (focusBasis == RegionalVisibilityFocusBasis.Poor)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.PoorFocus);
        }

        if (moon is { Direction.AltitudeDegrees: > 0 } activeMoon &&
            ((activeMoon.Direction.AltitudeDegrees >= options.MoonWithholdAltitudeDegrees &&
              activeMoon.IlluminatedFraction >= options.MoonWithholdIlluminatedFraction) ||
             (moonInFov && activeMoon.IlluminatedFraction >= options.MoonInFovWithholdIlluminatedFraction)))
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.MoonlightConfounded);
        }

        // Track non-blocking environmental provenance.
        if (environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Missing || solarRegime is null)
        {
            AddUnique(reasons, RegionalVisibilityReasonCodes.EnvironmentMissing);
        }
        else if (environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Stale)
        {
            AddUnique(reasons, RegionalVisibilityReasonCodes.EnvironmentStale);
        }
        else if (environment.PrecipitationStatus == EnvironmentalObservationMatchStatus.Contradictory)
        {
            AddUnique(reasons, RegionalVisibilityReasonCodes.EnvironmentContradictory);
        }

        // Compute per-tile representative sky background & noise in linear ADU and electron domain.
        var storedScale = ComputeStoredCodeScale(measuredLayout);
        var electronsPerStoredAdu = resolvedSensor is not null ? resolvedSensor.ElectronsPerAdu / storedScale : (double?)null;
        var regionalBackground = ResolveRegionalBackground(
            measurement, measuredLayout, resolvedSensor, storedScale, request.ExposureSeconds,
            options.GridColumns, options.GridRows, request.RegionalBackground);
        var measuredBackgroundAdu = regionalBackground.SummaryBackgroundAdu;
        var measuredNoiseSigmaAdu = regionalBackground.SummaryNoiseSigmaAdu;
        var backgroundElectronsPerSecond = regionalBackground.SummaryBackgroundElectronsPerSec;
        if (measurement is not null &&
            (measurement.Sources.AvailableBackgroundTiles is <= 0 || !regionalBackground.HasAnyAvailableTile))
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.BackgroundNoiseUnavailable);
        }

        var effectivePsfSigma = profile?.NominalPsfSigmaPixels;
        if (effectivePsfSigma is { } nominalSigma && focusBasis == RegionalVisibilityFocusBasis.Usable && medianHfd is { } hfd)
        {
            // Never shrink below nominal PSF, but widen expected PSF when measured HFD indicates softer seeing.
            effectivePsfSigma = Math.Max(nominalSigma, hfd / FwhmPerSigma);
        }

        // Determine geometry basis: accepted astrometry solve vs fixed geometry.
        var acceptedSolve = request.AstrometryArtifact is not null &&
            request.AstrometryArtifact.CaptureId == raw.CaptureId &&
            request.AstrometrySolution is { Assessment.HasMeasuredMapping: true } &&
            request.AcceptedMapping is not null &&
            prediction is not null &&
            ExposureMeasurementProducts.Same(request.AstrometrySolution.SceneIdentitySha256, prediction.SceneIdentitySha256) &&
            request.AstrometrySolution.Assessment.Frame.CaptureId == raw.CaptureId &&
            request.AstrometrySolution.Assessment.Frame.SourceArtifactId == raw.ArtifactId &&
            ExposureMeasurementProducts.Same(
                request.AstrometrySolution.Assessment.Frame.SourceDescriptorSha256,
                raw.DescriptorIdentitySha256);

        var geometryBasis = withholdingReasons.Any(static code => code is RegionalVisibilityReasonCodes.PredictionUnavailable or RegionalVisibilityReasonCodes.SimulatorTruthRejected)
            ? RegionalVisibilityGeometryBasis.Unavailable
            : acceptedSolve
                ? RegionalVisibilityGeometryBasis.AcceptedAstrometrySolve
                : RegionalVisibilityGeometryBasis.FixedGeometry;

        // If already globally withheld, return empty/withheld tiles with zero classifications.
        if (withholdingReasons.Count > 0)
        {
            var allReasons = withholdingReasons.Concat(reasons).Distinct(StringComparer.Ordinal).ToArray();
            var withheldTiles = CreateEmptyTiles(width, height, options, RegionalVisibilityTileStatus.InsufficientEvidence,
                RegionalVisibilityReasonCodes.TileWithheld, prediction, request.HorizonEvaluator, verifiedDefectMask, cancellationToken);
            var withheldGrid = BuildGridSummary(options, withheldTiles);
            var withheldConditions = new RegionalVisibilityExposureConditionsV1(
                Round(request.ExposureSeconds), Round(request.Gain), rawLayout.PixelFormat, width, height,
                calibrationDisposition, Round(resolvedSensor?.ElectronsPerAdu), Round(resolvedSensor?.ReadNoiseElectrons),
                Round(measuredBackgroundAdu), Round(measuredNoiseSigmaAdu), Round(backgroundElectronsPerSecond),
                saturatedFractionMillionths, solarRegime, Round(sunAltitude), environment.PrecipitationStatus,
                environment.PrecipitationDetected, Round(moon?.Direction.AltitudeDegrees), Round(moon?.Direction.AzimuthDegrees),
                Round(moon?.IlluminatedFraction), moonInFov, Round(effectivePsfSigma), Round(medianHfd),
                validFocusSamples, tooBroadCount, 0, null, null, null, profile?.ProfileName,
                request.HorizonEvaluator?.Profile.ComputeIdentitySha256());
            return new RegionalStellarVisibilityV1(
                SchemaVersion, RegionalStellarVisibilityV1.CurrentMethodId,
                RegionalVisibilityAssessmentStatus.Withheld, geometryBasis, focusBasis, 0,
                allReasons, withheldGrid, withheldTiles, null, sourceProvenance, withheldConditions,
                recipeIdentitySha256, Algorithms);
        }

        // Full photometric + geometric star evaluation & 1-to-1 matching!
        return EvaluateSupportedExposure(
            request, options, sourceProvenance, recipeIdentitySha256,
            measurement!, prediction!, measuredLayout, resolvedSensor!, storedScale, electronsPerStoredAdu!.Value,
            redResp, greenResp, blueResp, effectivePsfSigma!.Value,
            regionalBackground, verifiedDefectMask,
            saturatedFractionMillionths, solarRegime, sunAltitude, environment, moon, moonInFov,
            focusBasis, medianHfd, validFocusSamples, tooBroadCount,
            acceptedSolve, geometryBasis, reasons, cancellationToken);
    }

    private static RegionalStellarVisibilityV1 EvaluateSupportedExposure(
        RegionalStellarVisibilityRequest request,
        RegionalStellarVisibilityOptions options,
        RegionalVisibilitySourceV1 sourceProvenance,
        string recipeIdentitySha256,
        ExposureMeasurementV1 measurement,
        ProjectedSceneV1 prediction,
        FrameLayoutDescriptor measuredLayout,
        MonoSensorResponse sensor,
        double storedScale,
        double electronsPerStoredAdu,
        double redResp,
        double greenResp,
        double blueResp,
        double effectivePsfSigma,
        ResolvedRegionalBackground regionalBackground,
        Linear16Frame? defectMask,
        int saturatedFractionMillionths,
        CaptureSolarRegime? solarRegime,
        double? sunAltitude,
        CloudAssessmentEnvironmentV1 environment,
        SolarDiskAppearance? moon,
        bool moonInFov,
        RegionalVisibilityFocusBasis focusBasis,
        double? medianHfd,
        int validFocusSamples,
        int tooBroadCount,
        bool acceptedSolve,
        RegionalVisibilityGeometryBasis geometryBasis,
        List<string> infoReasons,
        CancellationToken cancellationToken)
    {
        var width = measuredLayout.Width;
        var height = measuredLayout.Height;
        var profile = options.PhotometricProfile!;
        var aperture = new ProjectedSceneAperture(prediction);
        var projection = ProjectedSceneJson.ToProjectionContext(prediction.Projection);
        var projector = ProjectorFactory.Create(projection);
        var isBayer = measuredLayout.PixelFormat == CameraPixelFormat.BayerRggb16;
        var detectorOptions = MeasuredStellarAssociationRecipe.MeasurementOptions(measuredLayout.PixelFormat);
        var horizon = request.HorizonEvaluator;
        var excludedRegions = options.ExcludedRegions ?? [];

        var cols = options.GridColumns;
        var rows = options.GridRows;
        var tileCount = cols * rows;
        var measuredBackgroundAdu = regionalBackground.SummaryBackgroundAdu;
        var measuredNoiseSigmaAdu = regionalBackground.SummaryNoiseSigmaAdu;
        var backgroundElectronsPerSecond = regionalBackground.SummaryBackgroundElectronsPerSec ?? 0d;

        // 1. Evaluate geometric mask support per tile.
        var tileSupport = EvaluateTileGeometry(width, height, cols, rows, aperture, projector, horizon, excludedRegions, defectMask, moon, options, cancellationToken);

        // 2. Collect predicted stars and map their positions (accepted solve mapping when available, else fixed geometry).
        var rawStars = prediction.Objects
            .Where(static item => item.Kind == CelestialObjectKind.Star)
            .OrderBy(static item => item.Id, StringComparer.Ordinal)
            .ToArray();

        var starPixels = new PixelPoint[rawStars.Length];
        for (var i = 0; i < rawStars.Length; i++)
        {
            var star = rawStars[i];
            if (acceptedSolve && request.AcceptedMapping!.SkyToPixel(star.EquatorialOfDate) is { } solvedPixel &&
                double.IsFinite(solvedPixel.X) && double.IsFinite(solvedPixel.Y))
            {
                starPixels[i] = solvedPixel;
            }
            else
            {
                starPixels[i] = star.Pixel;
            }
        }

        // Precompute crowding among predicted stars.
        var crowdingSquared = options.CrowdingRadiusPixels * options.CrowdingRadiusPixels;
        var crowded = new bool[rawStars.Length];
        for (var i = 0; i < rawStars.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var j = i + 1; j < rawStars.Length; j++)
            {
                var dx = starPixels[i].X - starPixels[j].X;
                var dy = starPixels[i].Y - starPixels[j].Y;
                if (dx * dx + dy * dy <= crowdingSquared)
                {
                    if (rawStars[j].Magnitude - rawStars[i].Magnitude <= options.CrowdingMagnitudeDifference)
                        crowded[i] = true;
                    if (rawStars[i].Magnitude - rawStars[j].Magnitude <= options.CrowdingMagnitudeDifference)
                        crowded[j] = true;
                }
            }
        }

        // Photometric aperture parameters matching the effective PSF.
        var apertureRadius = Math.Max(2.0, 2.5 * effectivePsfSigma);
        var aperturePixels = Math.PI * apertureRadius * apertureRadius;
        var apertureFraction = 1.0 - Math.Exp(-(apertureRadius * apertureRadius) / (2.0 * effectivePsfSigma * effectivePsfSigma));
        // Central pixel peak fraction of a 2D Gaussian PSF.
        var peakFraction = Math.Min(1.0, 1.0 / (2.0 * Math.PI * effectivePsfSigma * effectivePsfSigma));
        var bayerSnrPenalty = isBayer ? Math.Sqrt(BayerNoiseCorrelationFactor) : 1.0;

        var blackLevelStored = CenteredNormalizationLayout.CreateWorkingLayout(measuredLayout).BlackLevel ?? 0d;
        var whiteLevelStored = FrameWhiteThreshold.TryResolveStored(measuredLayout, out var storedWhite, out _)
            ? (double)storedWhite : ushort.MaxValue;
        var maxChargeElectrons = Math.Min(sensor.FullWellElectrons, Math.Max(0, whiteLevelStored - blackLevelStored) * electronsPerStoredAdu);

        // Classify each predicted star into expected detectable / marginal / masked-out using THAT tile's local background & noise.
        var tilePredicted = new int[tileCount];
        var tileExpected = new int[tileCount];
        var tileMarginal = new int[tileCount];
        var tileMasked = new int[tileCount];
        var expectedStars = new List<(int StarIndex, int TileIndex, PixelPoint Pixel, double Magnitude)>();

        for (var i = 0; i < rawStars.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var star = rawStars[i];
            var pixel = starPixels[i];
            if (!InsideBounds(pixel, width, height))
            {
                continue;
            }

            var tileIndex = TileIndexOf(pixel, width, height, cols, rows);
            tilePredicted[tileIndex]++;

            // Geometric masks or missing local background/noise evidence for this tile.
            if (!regionalBackground.TileAvailable[tileIndex] ||
                !aperture.ContainsWithMargin(pixel, options.EdgeMarginPixels) ||
                IsInsideExcludedRegion(pixel, width, height, excludedRegions) ||
                IsDefectMasked(pixel, defectMask) ||
                (horizon is not null && horizon.Evaluate(star.GeometricHorizontal) != HorizonObstructionEligibility.Clear) ||
                IsInsideMoonGlare(star.GeometricHorizontal, moon, options) ||
                crowded[i])
            {
                tileMasked[tileIndex]++;
                continue;
            }

            // Photometric detectability at actual exposure, gain, local sky background/noise, vignetting, and CFA response.
            var vignette = 1.0 - profile.VignettingStrength * projection.NormalizedRadiusSquared(pixel.X, pixel.Y);
            if (vignette <= 0)
            {
                tileMasked[tileIndex]++;
                continue;
            }

            var tileBgRate = regionalBackground.TileBackgroundElectronsPerSec[tileIndex];
            var tileNoiseMeasuredAdu = regionalBackground.TileNoiseSigmaAdu[tileIndex];
            var tileModelPerPixelNoiseElectrons = Math.Sqrt(
                (tileBgRate + profile.DarkCurrentElectronsPerSecond) * request.ExposureSeconds +
                sensor.ReadNoiseElectrons * sensor.ReadNoiseElectrons);
            var tileModelNoiseAdu = tileModelPerPixelNoiseElectrons / electronsPerStoredAdu;
            var tileEffectiveNoiseAdu = Math.Max(tileModelNoiseAdu, tileNoiseMeasuredAdu);

            var responseMultiplier = 1.0;
            if (isBayer)
            {
                var bv = Math.Clamp(star.ColorIndex ?? 0.65, -0.4, 2.0);
                var redFactor = Math.Exp(0.35 * bv);
                var blueFactor = Math.Exp(-0.65 * bv);
                // Rec.709 luminance response of linearly reconstructed Bayer CFA.
                responseMultiplier = 0.2126 * redFactor * redResp + 0.7152 * greenResp + 0.0722 * blueFactor * blueResp;
            }

            var photometry = new StellarPhotometryModel(
                profile.MagnitudeZeroElectronsPerSecond,
                request.ExposureSeconds,
                tileBgRate,
                sensor.ReadNoiseElectrons,
                profile.DarkCurrentElectronsPerSecond,
                apertureFraction,
                aperturePixels,
                responseMultiplier,
                vignette);

            var predictedSignal = StellarVisibilityModel.Predict(star.Magnitude, photometry);
            var effectiveBackgroundNoiseElectrons = tileEffectiveNoiseAdu * electronsPerStoredAdu;
            var totalVariance = Math.Max(
                predictedSignal.NoiseVariance,
                predictedSignal.SourceElectrons + aperturePixels * effectiveBackgroundNoiseElectrons * effectiveBackgroundNoiseElectrons);
            var effectiveSnr = totalVariance > 0
                ? predictedSignal.SourceElectrons / (Math.Sqrt(totalVariance) * bayerSnrPenalty)
                : 0;

            var totalSourceElectrons = Mono16SceneRenderer.RelativeFlux(star.Magnitude) *
                profile.MagnitudeZeroElectronsPerSecond * request.ExposureSeconds * responseMultiplier * vignette;
            var peakSourceElectrons = totalSourceElectrons * peakFraction;
            var peakExcessAdu = peakSourceElectrons / electronsPerStoredAdu;
            var detectorPeakFloor = Math.Max(detectorOptions.MinimumPeakAboveBackground, detectorOptions.PeakSigma * tileEffectiveNoiseAdu);

            // Check expected excessive saturation (bright star saturating > MaximumSaturatedSamples gets rejected by measurer).
            var bgElectrons = (tileBgRate * vignette + profile.DarkCurrentElectronsPerSecond) * request.ExposureSeconds;
            if (maxChargeElectrons > bgElectrons && peakSourceElectrons > (maxChargeElectrons - bgElectrons))
            {
                var saturationRadiusSq = 2.0 * effectivePsfSigma * effectivePsfSigma *
                    Math.Log(peakSourceElectrons / Math.Max(1.0, maxChargeElectrons - bgElectrons));
                var estimatedSaturatedPixels = Math.PI * saturationRadiusSq;
                if (estimatedSaturatedPixels > detectorOptions.MaximumSaturatedSamples)
                {
                    tileMasked[tileIndex]++;
                    continue;
                }
            }

            if (effectiveSnr >= options.MinimumExpectedSignalToNoise &&
                peakExcessAdu >= options.PeakMarginFactor * detectorPeakFloor)
            {
                tileExpected[tileIndex]++;
                expectedStars.Add((i, tileIndex, pixel, star.Magnitude));
            }
            else if (effectiveSnr >= detectorOptions.MinimumSignalToNoise * 0.7 && peakExcessAdu >= detectorPeakFloor * 0.7)
            {
                // Marginal star near detector threshold: exclude from expected count, record for transparency.
                tileMarginal[tileIndex]++;
            }
            else
            {
                tileMasked[tileIndex]++;
            }
        }

        // 3. One-to-one matching of expected detectable stars against saved measured detections.
        var detections = measurement.Sources.Detections;
        var exclusions = measurement.Sources.Exclusions;
        var matchRadiusSq = options.MatchRadiusPixels * options.MatchRadiusPixels;
        var offsetRadiusSq = options.OffsetSearchRadiusPixels * options.OffsetSearchRadiusPixels;

        var candidates = new List<(int ExpectedIdx, int DetectionIdx, double DistSq)>();
        for (var e = 0; e < expectedStars.Count; e++)
        {
            var expectedPixel = expectedStars[e].Pixel;
            for (var d = 0; d < detections.Count; d++)
            {
                var dx = detections[d].Pixel.X - expectedPixel.X;
                var dy = detections[d].Pixel.Y - expectedPixel.Y;
                var dSq = dx * dx + dy * dy;
                if (dSq <= matchRadiusSq)
                {
                    candidates.Add((e, d, dSq));
                }
            }
        }

        candidates.Sort(static (a, b) =>
        {
            var cmp = a.DistSq.CompareTo(b.DistSq);
            if (cmp != 0) return cmp;
            cmp = a.ExpectedIdx.CompareTo(b.ExpectedIdx);
            return cmp != 0 ? cmp : a.DetectionIdx.CompareTo(b.DetectionIdx);
        });

        var expectedMatched = new int[expectedStars.Count];
        Array.Fill(expectedMatched, -1);
        var detectionMatched = new int[detections.Count];
        Array.Fill(detectionMatched, -1);
        foreach (var pair in candidates)
        {
            if (expectedMatched[pair.ExpectedIdx] >= 0 || detectionMatched[pair.DetectionIdx] >= 0)
            {
                continue;
            }
            expectedMatched[pair.ExpectedIdx] = pair.DetectionIdx;
            detectionMatched[pair.DetectionIdx] = pair.ExpectedIdx;
        }

        // Also match any remaining non-expected predicted stars (marginal/bright) to detections so faint/bright real stars
        // aren't miscounted as "unassociated detections" pointing errors!
        var detectionExplainedByCatalog = new bool[detections.Count];
        for (var d = 0; d < detections.Count; d++)
        {
            if (detectionMatched[d] >= 0)
            {
                detectionExplainedByCatalog[d] = true;
                continue;
            }
            for (var s = 0; s < rawStars.Length; s++)
            {
                var dx = detections[d].Pixel.X - starPixels[s].X;
                var dy = detections[d].Pixel.Y - starPixels[s].Y;
                if (dx * dx + dy * dy <= matchRadiusSq)
                {
                    detectionExplainedByCatalog[d] = true;
                    break;
                }
            }
        }

        // 4. Fixed-geometry pointing & roll uncertainty analysis (when no accepted solve is present).
        var matchedAnchors = new List<(PixelPoint Expected, PixelPoint Measured)>();
        for (var e = 0; e < expectedStars.Count; e++)
        {
            if (expectedMatched[e] is var d and >= 0)
            {
                matchedAnchors.Add((expectedStars[e].Pixel, detections[d].Pixel));
            }
        }

        double? estimatedShiftPixels = null;
        double? estimatedRollDegrees = null;
        double? maxImpliedCornerShiftPixels = null;
        double linearDistortionRate = 0;
        var globalPointingUncertain = false;
        var fixedGeometryUnverified = false;
        var centerX = projection.PrincipalPointX;
        var centerY = projection.PrincipalPointY;
        var maxFrameRadius = Math.Max(
            Math.Max(Hypot(0 - centerX, 0 - centerY), Hypot(width - centerX, 0 - centerY)),
            Math.Max(Hypot(0 - centerX, height - centerY), Hypot(width - centerX, height - centerY)));

        if (!acceptedSolve)
        {
            if (matchedAnchors.Count >= 1)
            {
                var fit = EstimateSimilarityOffset(matchedAnchors, centerX, centerY);
                estimatedShiftPixels = Hypot(fit.Tx, fit.Ty);
                estimatedRollDegrees = fit.RollRadians * (180.0 / Math.PI);
                var scale = 1.0 + fit.ScaleDelta;
                linearDistortionRate = Hypot(scale * Math.Cos(fit.RollRadians) - 1.0, scale * Math.Sin(fit.RollRadians));
                maxImpliedCornerShiftPixels = estimatedShiftPixels.Value + linearDistortionRate * maxFrameRadius;

                if (estimatedShiftPixels.Value > options.MaximumPointingShiftPixels)
                {
                    globalPointingUncertain = true;
                }

                // Reject radial scale mismatch or unexplained rigid residuals under fixed geometry.
                var hasRadialLeverage = (matchedAnchors.Count >= 3 || fit.RmsAnchorRadiusPixels >= 20.0) &&
                    fit.RmsAnchorRadiusPixels >= 8.0;
                var observedRadialShift = Math.Abs(fit.ScaleDelta) * fit.MaxAnchorRadiusPixels;
                var impliedCornerScaleShift = Math.Abs(fit.ScaleDelta) * maxFrameRadius;
                if (hasRadialLeverage &&
                    observedRadialShift >= 0.35 &&
                    Math.Abs(fit.ScaleDelta) >= 0.005 &&
                    (impliedCornerScaleShift >= options.MatchRadiusPixels * options.MaximumImpliedRollShiftFraction ||
                     Math.Abs(fit.ScaleDelta) >= 0.02))
                {
                    globalPointingUncertain = true;
                }

                if (matchedAnchors.Count >= 3 &&
                    fit.RigidRmsPixels > Math.Min(options.MaximumPointingShiftPixels, options.MatchRadiusPixels * 0.45))
                {
                    globalPointingUncertain = true;
                }
            }
            else if (detections.Count >= options.MinimumExpectedStarsPerTile && expectedStars.Count >= options.MinimumExpectedStarsPerTile)
            {
                // Many real stars detected in the frame, yet zero matched at fixed-geometry positions -> pointing/geometry error!
                fixedGeometryUnverified = true;
            }
        }

        // Count per-tile matched, measurement-excluded, unassociated, and offset candidates.
        var tileMatched = new int[tileCount];
        var tileMeasExcluded = new int[tileCount];
        var tileUnassociated = new int[tileCount];
        var tileOffsetCandidates = new int[tileCount];
        var tilePointingUncertain = new bool[tileCount];

        for (var d = 0; d < detections.Count; d++)
        {
            if (!detectionExplainedByCatalog[d] && InsideBounds(detections[d].Pixel, width, height))
            {
                var t = TileIndexOf(detections[d].Pixel, width, height, cols, rows);
                tileUnassociated[t]++;
            }
        }

        var totalOffsetMatches = 0;
        var totalUnmatchedExpected = 0;
        for (var e = 0; e < expectedStars.Count; e++)
        {
            var (_, tileIdx, pixel, _) = expectedStars[e];
            if (expectedMatched[e] >= 0)
            {
                tileMatched[tileIdx]++;
                continue;
            }

            // Check if the star was excluded by a non-cloud measurer exclusion (saturated-excessive, blended, crowded, masked, edge).
            if (HasNonCloudExclusionNear(pixel, exclusions, options.MatchRadiusPixels))
            {
                tileMeasExcluded[tileIdx]++;
                continue;
            }

            totalUnmatchedExpected++;

            if (!acceptedSolve)
            {
                // Check if an unexplained measured detection sits within the outer offset search radius.
                var hasNearbyOffset = false;
                for (var d = 0; d < detections.Count; d++)
                {
                    if (detectionExplainedByCatalog[d]) continue;
                    var dx = detections[d].Pixel.X - pixel.X;
                    var dy = detections[d].Pixel.Y - pixel.Y;
                    var dSq = dx * dx + dy * dy;
                    if (dSq > matchRadiusSq && dSq <= offsetRadiusSq)
                    {
                        hasNearbyOffset = true;
                        break;
                    }
                }
                if (hasNearbyOffset)
                {
                    tileOffsetCandidates[tileIdx]++;
                    totalOffsetMatches++;
                }

                // Check implied shift + roll + scale distortion at this unmatched star's radius from principal point!
                if (estimatedShiftPixels is { } shift)
                {
                    var starRadius = Hypot(pixel.X - centerX, pixel.Y - centerY);
                    var impliedAtStar = shift + linearDistortionRate * starRadius;
                    if (impliedAtStar >= options.MatchRadiusPixels * options.MaximumImpliedRollShiftFraction)
                    {
                        tilePointingUncertain[tileIdx] = true;
                    }
                }
            }
        }

        // Under fixed geometry, if an unmatched tile also has as many unassociated sharp stellar detections as missing stars,
        // geometry cannot explain the observations in that tile (e.g. distortion/scale shift beyond OffsetSearchRadiusPixels).
        var totalUnassociatedInUnmatchedTiles = 0;
        if (!acceptedSolve)
        {
            for (var idx = 0; idx < tileCount; idx++)
            {
                var effectiveExp = Math.Max(0, tileExpected[idx] - tileMeasExcluded[idx]);
                var missingInTile = effectiveExp - Math.Min(tileMatched[idx], effectiveExp);
                if (missingInTile > 0 && tileUnassociated[idx] > 0)
                {
                    totalUnassociatedInUnmatchedTiles += tileUnassociated[idx];
                    if (missingInTile >= options.MinimumMissingStarsForDeficit && tileUnassociated[idx] >= missingInTile)
                    {
                        tilePointingUncertain[idx] = true;
                    }
                }
            }
        }

        // If a substantial fraction of unmatched expected stars have nearby unexplained offset detections under fixed geometry,
        // the pointing is shifted/rolled/scaled — withhold globally!
        if (!acceptedSolve && totalUnmatchedExpected > 0 &&
            (totalOffsetMatches >= 2 && totalOffsetMatches * 2 >= totalUnmatchedExpected ||
             totalUnassociatedInUnmatchedTiles >= 2 && totalUnassociatedInUnmatchedTiles >= totalUnmatchedExpected ||
             estimatedShiftPixels is { } gShift && gShift > options.MaximumPointingShiftPixels))
        {
            globalPointingUncertain = true;
        }

        // Also check tile-level roll uncertainty: if roll causes >= half of unmatched tiles to be pointing-uncertain,
        // withhold or mark those tiles InsufficientEvidence (tile.pointing-uncertain) so NO false cloud deficit is ever reported!
        var totalTileOffsetCandidates = tileOffsetCandidates.Sum();
        if (!acceptedSolve && totalTileOffsetCandidates >= 2 && matchedAnchors.Count > 0 &&
            estimatedRollDegrees is { } absRoll && Math.Abs(absRoll) >= 0.15)
        {
            globalPointingUncertain = true;
        }

        if (globalPointingUncertain || fixedGeometryUnverified)
        {
            var withholdCode = globalPointingUncertain
                ? RegionalVisibilityReasonCodes.PointingUncertain
                : RegionalVisibilityReasonCodes.FixedGeometryUnverified;
            var allReasons = new List<string> { withholdCode };
            if (focusBasis == RegionalVisibilityFocusBasis.Unavailable)
                AddUnique(allReasons, RegionalVisibilityReasonCodes.FocusUnavailable);
            foreach (var r in infoReasons) AddUnique(allReasons, r);

            var withheldTiles = CreateWithheldTilesFromSupport(width, height, cols, rows, tileSupport,
                tilePredicted, tileExpected, tileMarginal, tileMasked, tileMatched, tileMeasExcluded,
                tileUnassociated, tileOffsetCandidates, RegionalVisibilityReasonCodes.TilePointingUncertain);
            var withheldGrid = BuildGridSummary(options, withheldTiles);
            var withheldConditions = BuildConditions(
                request, measuredLayout, measurement, sensor, measuredBackgroundAdu, measuredNoiseSigmaAdu,
                backgroundElectronsPerSecond, saturatedFractionMillionths, solarRegime, sunAltitude, environment,
                moon, moonInFov, effectivePsfSigma, medianHfd, validFocusSamples, tooBroadCount,
                matchedAnchors.Count, estimatedShiftPixels, estimatedRollDegrees, maxImpliedCornerShiftPixels,
                profile.ProfileName, horizon?.Profile.ComputeIdentitySha256());
            return new RegionalStellarVisibilityV1(
                SchemaVersion, RegionalStellarVisibilityV1.CurrentMethodId,
                RegionalVisibilityAssessmentStatus.Withheld, geometryBasis, focusBasis, 0,
                allReasons, withheldGrid, withheldTiles, null, sourceProvenance, withheldConditions,
                recipeIdentitySha256, Algorithms);
        }

        // 5. Classify each tile!
        var tiles = new RegionalVisibilityTileV1[tileCount];
        var deficitMaskBytes = options.IncludeMask ? new byte[(tileCount + 7) / 8] : null;
        var assessedMaskBytes = options.IncludeMask ? new byte[(tileCount + 7) / 8] : null;

        for (var row = 0; row < rows; row++)
        {
            var y0 = (int)((long)row * height / rows);
            var y1 = (int)((long)(row + 1) * height / rows);
            for (var col = 0; col < cols; col++)
            {
                var idx = row * cols + col;
                var x0 = (int)((long)col * width / cols);
                var x1 = (int)((long)(col + 1) * width / cols);
                var support = tileSupport[idx];
                var effectiveExpected = Math.Max(0, tileExpected[idx] - tileMeasExcluded[idx]);
                var matched = Math.Min(tileMatched[idx], effectiveExpected);
                var missing = effectiveExpected - matched;
                int? visRatio = effectiveExpected > 0
                    ? (int)Math.Clamp(Math.Round((double)matched * OneMillion / effectiveExpected, MidpointRounding.AwayFromZero), 0, OneMillion)
                    : null;

                var tileReasons = new List<string>();
                RegionalVisibilityTileStatus tileStatus;

                if (support.SupportedAreaFractionMillionths < options.MinimumTileSupportedAreaMillionths)
                {
                    tileStatus = RegionalVisibilityTileStatus.Excluded;
                    tileReasons.Add(support.DominantExclusionReason);
                }
                else if (!regionalBackground.TileAvailable[idx])
                {
                    tileStatus = RegionalVisibilityTileStatus.InsufficientEvidence;
                    tileReasons.Add(RegionalVisibilityReasonCodes.TileBackgroundUnavailable);
                }
                else if (!acceptedSolve && (tilePointingUncertain[idx] || (tileOffsetCandidates[idx] > 0 && missing > 0)))
                {
                    tileStatus = RegionalVisibilityTileStatus.InsufficientEvidence;
                    tileReasons.Add(RegionalVisibilityReasonCodes.TilePointingUncertain);
                }
                else if (effectiveExpected < options.MinimumExpectedStarsPerTile)
                {
                    tileStatus = RegionalVisibilityTileStatus.InsufficientEvidence;
                    tileReasons.Add(RegionalVisibilityReasonCodes.TileInsufficientExpectedStars);
                    if (tileMarginal[idx] > 0)
                    {
                        tileReasons.Add(RegionalVisibilityReasonCodes.TileMarginalStarsExcluded);
                    }
                }
                else if (visRatio!.Value < options.DeficitVisibilityThresholdMillionths &&
                         missing >= options.MinimumMissingStarsForDeficit)
                {
                    tileStatus = RegionalVisibilityTileStatus.DetectionDeficit;
                    tileReasons.Add(RegionalVisibilityReasonCodes.TileDetectionDeficit);
                    if (tileMarginal[idx] > 0)
                    {
                        tileReasons.Add(RegionalVisibilityReasonCodes.TileMarginalStarsExcluded);
                    }
                }
                else
                {
                    tileStatus = RegionalVisibilityTileStatus.Clear;
                    tileReasons.Add(RegionalVisibilityReasonCodes.TileClear);
                    if (tileMarginal[idx] > 0)
                    {
                        tileReasons.Add(RegionalVisibilityReasonCodes.TileMarginalStarsExcluded);
                    }
                }

                if (tileStatus is RegionalVisibilityTileStatus.Clear or RegionalVisibilityTileStatus.DetectionDeficit)
                {
                    if (assessedMaskBytes is not null)
                    {
                        assessedMaskBytes[idx >> 3] |= (byte)(1 << (idx & 7));
                    }
                    if (tileStatus == RegionalVisibilityTileStatus.DetectionDeficit && deficitMaskBytes is not null)
                    {
                        deficitMaskBytes[idx >> 3] |= (byte)(1 << (idx & 7));
                    }
                }

                tiles[idx] = new RegionalVisibilityTileV1(
                    col, row, x0, y0, x1 - x0, y1 - y0,
                    tileStatus,
                    support.SupportedAreaFractionMillionths,
                    tilePredicted[idx],
                    effectiveExpected,
                    tileMarginal[idx],
                    tileMasked[idx],
                    matched,
                    tileMeasExcluded[idx],
                    tileUnassociated[idx],
                    tileOffsetCandidates[idx],
                    visRatio,
                    tileReasons);
            }
        }

        var grid = BuildGridSummary(options, tiles);
        var finalReasons = new List<string>();
        RegionalVisibilityAssessmentStatus status;
        int confidence;

        if (grid.ExcludedTileCount == grid.TotalTileCount)
        {
            status = RegionalVisibilityAssessmentStatus.InsufficientEvidence;
            confidence = 0;
            AddUnique(finalReasons, RegionalVisibilityReasonCodes.AllRegionsExcluded);
        }
        else if (grid.AssessedTileCount == 0)
        {
            status = RegionalVisibilityAssessmentStatus.InsufficientEvidence;
            confidence = 0;
            AddUnique(finalReasons, RegionalVisibilityReasonCodes.InsufficientSupport);
        }
        else
        {
            status = RegionalVisibilityAssessmentStatus.Assessed;
            if (acceptedSolve)
            {
                AddUnique(finalReasons, RegionalVisibilityReasonCodes.AcceptedSolveVerified);
            }
            else
            {
                AddUnique(finalReasons, RegionalVisibilityReasonCodes.FixedGeometryActive);
            }

            if (focusBasis == RegionalVisibilityFocusBasis.Usable)
            {
                AddUnique(finalReasons, RegionalVisibilityReasonCodes.FocusMetricsVerified);
            }
            else if (focusBasis == RegionalVisibilityFocusBasis.Unavailable)
            {
                AddUnique(finalReasons, RegionalVisibilityReasonCodes.FocusUnavailable);
            }

            confidence = ComputeConfidenceMillionths(grid, acceptedSolve, matchedAnchors.Count, focusBasis, moon, infoReasons);
        }

        foreach (var r in infoReasons)
        {
            AddUnique(finalReasons, r);
        }

        var mask = options.IncludeMask && status == RegionalVisibilityAssessmentStatus.Assessed
            ? new RegionalVisibilityMaskV1(
                RegionalVisibilityMaskV1.RowMajorLsbFirst,
                cols,
                rows,
                deficitMaskBytes!,
                assessedMaskBytes!)
            : null;

        var conditions = BuildConditions(
            request, measuredLayout, measurement, sensor, measuredBackgroundAdu, measuredNoiseSigmaAdu,
            backgroundElectronsPerSecond, saturatedFractionMillionths, solarRegime, sunAltitude, environment,
            moon, moonInFov, effectivePsfSigma, medianHfd, validFocusSamples, tooBroadCount,
            matchedAnchors.Count, estimatedShiftPixels, estimatedRollDegrees, maxImpliedCornerShiftPixels,
            profile.ProfileName, horizon?.Profile.ComputeIdentitySha256());

        return new RegionalStellarVisibilityV1(
            SchemaVersion, RegionalStellarVisibilityV1.CurrentMethodId,
            status, geometryBasis, focusBasis, confidence,
            finalReasons, grid, tiles, mask, sourceProvenance, conditions,
            recipeIdentitySha256, Algorithms);
    }

    private static int ComputeConfidenceMillionths(
        RegionalVisibilityGridV1 grid,
        bool acceptedSolve,
        int fixedGeometryAnchors,
        RegionalVisibilityFocusBasis focusBasis,
        SolarDiskAppearance? moon,
        List<string> envReasons)
    {
        var nonExcluded = Math.Max(1, grid.TotalTileCount - grid.ExcludedTileCount);
        var tileCoverage = (double)grid.AssessedTileCount / nonExcluded;
        var starSupport = Math.Min(1.0, (double)grid.TotalExpectedDetectableStars / Math.Max(1, grid.MinimumExpectedStarsPerTile * Math.Min(4, nonExcluded)));
        var supportFactor = 0.55 + 0.45 * Math.Sqrt(tileCoverage * starSupport);

        var geometryFactor = acceptedSolve
            ? 1.00
            : fixedGeometryAnchors >= 3 ? 0.82 : fixedGeometryAnchors > 0 ? 0.76 : 0.70;

        var focusFactor = focusBasis == RegionalVisibilityFocusBasis.Usable ? 1.00 : 0.85;

        var environmentFactor = 1.00;
        if (moon is { Direction.AltitudeDegrees: > 0, IlluminatedFraction: >= 0.20 })
        {
            environmentFactor *= 0.92;
        }
        if (envReasons.Count > 0)
        {
            environmentFactor *= 0.95;
        }

        var combined = supportFactor * geometryFactor * focusFactor * environmentFactor;
        return Math.Clamp((int)Math.Round(combined * OneMillion, MidpointRounding.AwayFromZero), 1, OneMillion);
    }

    private readonly record struct SimilarityFit(
        double Tx,
        double Ty,
        double RollRadians,
        double ScaleDelta,
        double RigidRmsPixels,
        double RmsAnchorRadiusPixels,
        double MaxAnchorRadiusPixels);

    private static SimilarityFit EstimateSimilarityOffset(
        List<(PixelPoint Expected, PixelPoint Measured)> anchors,
        double centerX,
        double centerY)
    {
        var meanDx = anchors.Average(static a => a.Measured.X - a.Expected.X);
        var meanDy = anchors.Average(static a => a.Measured.Y - a.Expected.Y);
        double sumRadSq = 0;
        double maxRad = 0;
        foreach (var (expected, _) in anchors)
        {
            var r = Hypot(expected.X - centerX, expected.Y - centerY);
            sumRadSq += r * r;
            if (r > maxRad) maxRad = r;
        }
        var rmsRad = Math.Sqrt(sumRadSq / anchors.Count);
        if (anchors.Count < 2)
        {
            return new(meanDx, meanDy, 0, 0, 0, rmsRad, maxRad);
        }

        // Fit 2D similarity (translation, rotation, and radial scale) around the principal point using centered anchors.
        var meanExpX = anchors.Average(static a => a.Expected.X) - centerX;
        var meanExpY = anchors.Average(static a => a.Expected.Y) - centerY;
        double cross = 0, dot = 0, den = 0;
        foreach (var (expected, measured) in anchors)
        {
            var rx = expected.X - centerX - meanExpX;
            var ry = expected.Y - centerY - meanExpY;
            var dx = (measured.X - expected.X) - meanDx;
            var dy = (measured.Y - expected.Y) - meanDy;
            cross += rx * dy - ry * dx;
            dot += rx * dx + ry * dy;
            den += rx * rx + ry * ry;
        }

        double a = 1.0, b = 0.0;
        if (den > 1e-6)
        {
            a = 1.0 + dot / den;
            b = cross / den;
        }
        var scale = Hypot(a, b);
        var scaleDelta = scale - 1.0;
        var rollRad = Math.Atan2(b, a);

        // Compute translation at principal point after removing similarity linear term.
        double txSum = 0, tySum = 0;
        foreach (var (expected, measured) in anchors)
        {
            var rx = expected.X - centerX;
            var ry = expected.Y - centerY;
            txSum += (measured.X - centerX) - (a * rx - b * ry);
            tySum += (measured.Y - centerY) - (b * rx + a * ry);
        }
        var tx = txSum / anchors.Count;
        var ty = tySum / anchors.Count;

        // Also compute rigid (translation + rotation only, scale = 1) RMS residual on the anchors.
        var rigidCos = Math.Cos(rollRad);
        var rigidSin = Math.Sin(rollRad);
        double rigidTxSum = 0, rigidTySum = 0;
        foreach (var (expected, measured) in anchors)
        {
            var rx = expected.X - centerX;
            var ry = expected.Y - centerY;
            rigidTxSum += (measured.X - centerX) - (rigidCos * rx - rigidSin * ry);
            rigidTySum += (measured.Y - centerY) - (rigidSin * rx + rigidCos * ry);
        }
        var rigidTx = rigidTxSum / anchors.Count;
        var rigidTy = rigidTySum / anchors.Count;
        double rigidSqSum = 0;
        foreach (var (expected, measured) in anchors)
        {
            var rx = expected.X - centerX;
            var ry = expected.Y - centerY;
            var predX = centerX + rigidTx + rigidCos * rx - rigidSin * ry;
            var predY = centerY + rigidTy + rigidSin * rx + rigidCos * ry;
            var ex = measured.X - predX;
            var ey = measured.Y - predY;
            rigidSqSum += ex * ex + ey * ey;
        }
        var rigidRms = Math.Sqrt(rigidSqSum / anchors.Count);

        return new(tx, ty, rollRad, scaleDelta, rigidRms, rmsRad, maxRad);
    }

    private static (RegionalVisibilityFocusBasis Basis, double? MedianHfd, int ValidSamples, int TooBroadCount)
        EvaluateFocus(ExposureMeasurementV1? measurement, RegionalStellarVisibilityOptions options)
    {
        if (measurement is null)
        {
            return (RegionalVisibilityFocusBasis.Unavailable, null, 0, 0);
        }

        var tooBroadCount = measurement.Sources.Exclusions.Count(
            static e => string.Equals(e.ReasonCode, StellarExclusionReasons.TooBroad, StringComparison.Ordinal));
        var validCount = measurement.Hfd.ValidCount ?? 0;
        var medianHfd = measurement.Hfd.MedianPixels;

        if (measurement.Hfd.Status == ExposureMeasurementStatus.Usable &&
            validCount >= options.MinimumFocusSamples &&
            medianHfd is { } hfd && double.IsFinite(hfd))
        {
            return hfd > options.MaximumAcceptableHfdPixels
                ? (RegionalVisibilityFocusBasis.Poor, hfd, validCount, tooBroadCount)
                : (RegionalVisibilityFocusBasis.Usable, hfd, validCount, tooBroadCount);
        }

        if (tooBroadCount >= options.MinimumTooBroadExclusionsForPoorFocus &&
            tooBroadCount >= measurement.Sources.Detections.Count)
        {
            return (RegionalVisibilityFocusBasis.Poor, medianHfd, validCount, tooBroadCount);
        }

        return (RegionalVisibilityFocusBasis.Unavailable, medianHfd, validCount, tooBroadCount);
    }

    private sealed record ResolvedRegionalBackground(
        bool[] TileAvailable,
        double[] TileBackgroundAdu,
        double[] TileNoiseSigmaAdu,
        double[] TileBackgroundElectronsPerSec,
        double? SummaryBackgroundAdu,
        double? SummaryNoiseSigmaAdu,
        double? SummaryBackgroundElectronsPerSec)
    {
        public bool HasAnyAvailableTile => TileAvailable.Any(static available => available);
    }

    /// <summary>
    /// Computes deterministic per-tile local sky background and noise sigma directly from a qualified linear frame,
    /// matching StellarSourceMeasurer's clipped median and noise estimator while excluding saturated, out-of-aperture,
    /// and defect-masked pixels.
    /// </summary>
    public static RegionalVisibilityTileBackgroundV1[] EstimateRegionalBackgroundFromLinear(
        FrameLayoutDescriptor linearLayout,
        ReadOnlyMemory<byte> linearPixels,
        FrameLayoutDescriptor rawLayout,
        ReadOnlyMemory<byte> rawPixels,
        int gridColumns,
        int gridRows,
        ProjectionContext? aperture = null,
        Linear16Frame? defectMask = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(linearLayout);
        ArgumentNullException.ThrowIfNull(rawLayout);
        var width = linearLayout.Width;
        var height = linearLayout.Height;
        var tileCount = checked(gridColumns * gridRows);
        var unavailable = Enumerable.Repeat(new RegionalVisibilityTileBackgroundV1(false, null, null), tileCount).ToArray();
        if (width < 1 || height < 1 || gridColumns < 1 || gridRows < 1 ||
            gridColumns > width || gridRows > height ||
            linearLayout.PixelFormat is not (CameraPixelFormat.Mono16 or CameraPixelFormat.BayerRggb16) ||
            rawLayout.Width != width || rawLayout.Height != height ||
            linearPixels.Length < linearLayout.ByteLength || rawPixels.Length < rawLayout.ByteLength)
        {
            return unavailable;
        }

        if (!FrameWhiteThreshold.TryResolveStored(rawLayout, out var rawWhite, out _) ||
            !FrameWhiteThreshold.TryResolveStored(linearLayout, out var linearWhite, out _))
        {
            return unavailable;
        }

        var count = checked(width * height);
        var samples = new double[count];
        var saturated = new bool[count];
        var valid = new bool[count];
        var rawBytes = rawPixels.Span;
        var linearBytes = linearPixels.Span;
        for (var y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rawRow = rawBytes.Slice(y * rawLayout.StrideBytes, width * 2);
            var linearRow = linearBytes.Slice(y * linearLayout.StrideBytes, width * 2);
            for (var x = 0; x < width; x++)
            {
                var linearCode = ExposureSampleCodes.Read16(linearRow[(x * 2)..], linearLayout.ByteOrder);
                var rawCode = ExposureSampleCodes.Read16(rawRow[(x * 2)..], rawLayout.ByteOrder);
                var idx = y * width + x;
                samples[idx] = linearCode;
                saturated[idx] = rawCode >= rawWhite || linearCode >= linearWhite;
                var inAperture = aperture?.ContainsSample(x + 0.5, y + 0.5) ?? true;
                var isDefect = defectMask is not null && IsDefectMasked(new PixelPoint(x + 0.5, y + 0.5), defectMask);
                valid[idx] = inAperture && !isDefect;
            }
        }

        var isBayer = linearLayout.PixelFormat == CameraPixelFormat.BayerRggb16;
        ReadOnlyMemory<double> luminanceSamples = samples;
        ReadOnlyMemory<bool> luminanceValid = valid;
        if (isBayer)
        {
            var pattern = ExposureMeasurementRecipe.LocalPattern(linearLayout);
            if (pattern is null)
            {
                return unavailable;
            }
            var reconstructed = LinearBayerReconstruction.Reconstruct(samples, valid, width, height, pattern.Value, cancellationToken)
                .ToLuminance(cancellationToken);
            luminanceSamples = reconstructed.Pixels;
            luminanceValid = reconstructed.ValidMask;
        }

        var lumSpan = luminanceSamples.Span;
        var valSpan = luminanceValid.Span;
        var results = new RegionalVisibilityTileBackgroundV1[tileCount];
        var maxTileW = (width + gridColumns - 1) / gridColumns + 1;
        var maxTileH = (height + gridRows - 1) / gridRows + 1;
        var buffer = new double[maxTileW * maxTileH];
        var differences = new double[maxTileW * maxTileH];
        var deviations = new double[maxTileW * maxTileH];

        for (var row = 0; row < gridRows; row++)
        {
            var y0 = (int)((long)row * height / gridRows);
            var y1 = (int)((long)(row + 1) * height / gridRows);
            for (var col = 0; col < gridColumns; col++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var x0 = (int)((long)col * width / gridColumns);
                var x1 = (int)((long)(col + 1) * width / gridColumns);
                var tilePixels = (x1 - x0) * (y1 - y0);
                var minSamples = Math.Max(16, Math.Min(64, tilePixels / 4));
                var sampleCount = 0;
                var diffCount = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = x0; x < x1; x++)
                    {
                        var idx = y * width + x;
                        if (valSpan[idx] && !saturated[idx] && double.IsFinite(lumSpan[idx]))
                        {
                            buffer[sampleCount++] = lumSpan[idx];
                            if (!isBayer && x + 1 < x1 && valSpan[idx + 1] && !saturated[idx + 1] && double.IsFinite(lumSpan[idx + 1]))
                            {
                                differences[diffCount++] = lumSpan[idx + 1] - lumSpan[idx];
                            }
                        }
                    }
                }

                var tileIdx = row * gridColumns + col;
                if (sampleCount < minSamples)
                {
                    results[tileIdx] = new(false, null, null);
                    continue;
                }

                var (bg, noise) = ClippedMedianAndMad(buffer.AsSpan(0, sampleCount), deviations, 3.0);
                if (!isBayer && diffCount >= minSamples / 2)
                {
                    noise = ClippedMedianAndMad(differences.AsSpan(0, diffCount), deviations, 3.0).Noise / Math.Sqrt(2.0);
                }

                results[tileIdx] = double.IsFinite(bg) && double.IsFinite(noise) && noise >= 0
                    ? new(true, Round(bg), Round(noise))
                    : new(false, null, null);
            }
        }

        return results;
    }

    private static (double Median, double Noise) ClippedMedianAndMad(Span<double> samples, double[] deviations, double clipSigma)
    {
        var count = samples.Length;
        var span = samples;
        double median = 0;
        double noise = 0;
        for (var iteration = 0; iteration < 3; iteration++)
        {
            span.Sort();
            median = span[count / 2];
            for (var i = 0; i < count; i++)
            {
                deviations[i] = Math.Abs(span[i] - median);
            }
            var devSpan = deviations.AsSpan(0, count);
            devSpan.Sort();
            noise = 1.4826 * devSpan[count / 2];
            if (noise <= 0)
            {
                break;
            }
            var limit = clipSigma * noise;
            var kept = 0;
            for (var i = 0; i < count; i++)
            {
                if (Math.Abs(span[i] - median) <= limit)
                {
                    span[kept++] = span[i];
                }
            }
            if (kept == count || kept < 4)
            {
                break;
            }
            count = kept;
            span = span[..count];
        }

        return (median, noise);
    }

    private static ResolvedRegionalBackground ResolveRegionalBackground(
        ExposureMeasurementV1? measurement,
        FrameLayoutDescriptor layout,
        MonoSensorResponse? sensor,
        double storedScale,
        double exposureSeconds,
        int cols,
        int rows,
        IReadOnlyList<RegionalVisibilityTileBackgroundV1>? suppliedRegional)
    {
        var tileCount = cols * rows;
        var available = new bool[tileCount];
        var bgAdu = new double[tileCount];
        var noiseAdu = new double[tileCount];
        var bgRate = new double[tileCount];

        if (measurement is null)
        {
            return new(available, bgAdu, noiseAdu, bgRate, null, null, null);
        }

        var width = layout.Width;
        var height = layout.Height;
        var workingBlack = CenteredNormalizationLayout.CreateWorkingLayout(layout).BlackLevel ?? 0d;

        // Collect per-tile detection local background and noise measurements.
        var detHasTile = new bool[tileCount];
        var detTileBg = new double[tileCount];
        var detTileNoise = new double[tileCount];
        var allDetBgs = new List<double>();
        var allDetNoises = new List<double>();
        foreach (var detection in measurement.Sources.Detections)
        {
            if (!double.IsFinite(detection.Background) || !double.IsFinite(detection.NoiseSigma) || detection.NoiseSigma < 0)
            {
                continue;
            }
            allDetBgs.Add(detection.Background);
            allDetNoises.Add(detection.NoiseSigma);
            if (!InsideBounds(detection.Pixel, width, height))
            {
                continue;
            }
            var t = TileIndexOf(detection.Pixel, width, height, cols, rows);
            if (!detHasTile[t])
            {
                detHasTile[t] = true;
                detTileBg[t] = detection.Background;
                detTileNoise[t] = detection.NoiseSigma;
            }
            else
            {
                // Conservative local ceiling so a quiet part of a tile cannot hide high local noise in the same tile.
                detTileBg[t] = Math.Max(detTileBg[t], detection.Background);
                detTileNoise[t] = Math.Max(detTileNoise[t], detection.NoiseSigma);
            }
        }

        var hasSupplied = suppliedRegional is not null && suppliedRegional.Count == tileCount;
        for (var t = 0; t < tileCount; t++)
        {
            if (hasSupplied && suppliedRegional![t] is { Available: true, BackgroundAdu: { } rBg, NoiseSigmaAdu: { } rNoise } &&
                double.IsFinite(rBg) && double.IsFinite(rNoise) && rNoise >= 0)
            {
                available[t] = true;
                bgAdu[t] = detHasTile[t] ? Math.Max(rBg, detTileBg[t]) : rBg;
                noiseAdu[t] = detHasTile[t] ? Math.Max(rNoise, detTileNoise[t]) : rNoise;
            }
            else if (detHasTile[t])
            {
                available[t] = true;
                bgAdu[t] = detTileBg[t];
                noiseAdu[t] = detTileNoise[t];
            }
        }

        // For detection-free tiles when no linear-image regional background was supplied (direct Assessments with detections
        // in other tiles), inherit conservative local background/noise from adjacent tiles (never a frame-wide lower quartile).
        if (!hasSupplied && allDetBgs.Count > 0 && allDetNoises.Count > 0)
        {
            allDetBgs.Sort();
            allDetNoises.Sort();
            var fallbackBg = UpperQuartileOrMedian(allDetBgs);
            var fallbackNoise = UpperQuartileOrMedian(allDetNoises);
            var seeded = (bool[])available.Clone();
            var seededBg = (double[])bgAdu.Clone();
            var seededNoise = (double[])noiseAdu.Clone();
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var t = row * cols + col;
                    if (seeded[t])
                    {
                        continue;
                    }
                    double? neighborBg = null;
                    double? neighborNoise = null;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var nx = col + dx;
                            var ny = row + dy;
                            if (nx < 0 || ny < 0 || nx >= cols || ny >= rows) continue;
                            var nt = ny * cols + nx;
                            if (!seeded[nt]) continue;
                            neighborBg = neighborBg is { } curBg ? Math.Max(curBg, seededBg[nt]) : seededBg[nt];
                            neighborNoise = neighborNoise is { } curN ? Math.Max(curN, seededNoise[nt]) : seededNoise[nt];
                        }
                    }
                    available[t] = true;
                    bgAdu[t] = neighborBg ?? fallbackBg;
                    noiseAdu[t] = neighborNoise ?? fallbackNoise;
                }
            }
        }

        var availableBgs = new List<double>();
        var availableNoises = new List<double>();
        var electronsPerStoredAdu = sensor is not null && exposureSeconds > 0
            ? sensor.ElectronsPerAdu / storedScale
            : (double?)null;

        for (var t = 0; t < tileCount; t++)
        {
            if (!available[t])
            {
                continue;
            }
            availableBgs.Add(bgAdu[t]);
            availableNoises.Add(noiseAdu[t]);
            if (electronsPerStoredAdu is { } ePerAdu)
            {
                bgRate[t] = Math.Max(0, bgAdu[t] - workingBlack) * ePerAdu / exposureSeconds;
            }
        }

        double? summaryBg = availableBgs.Count > 0 ? ExposureMeasurementProducts.Median(availableBgs) : null;
        double? summaryNoise = availableNoises.Count > 0 ? ExposureMeasurementProducts.Median(availableNoises) : null;
        double? summaryRate = summaryBg is { } sBg && electronsPerStoredAdu is { } sEPerAdu
            ? Math.Max(0, sBg - workingBlack) * sEPerAdu / exposureSeconds
            : null;

        return new(available, bgAdu, noiseAdu, bgRate, summaryBg, summaryNoise, summaryRate);
    }

    private static double UpperQuartileOrMedian(List<double> sorted)
        => sorted.Count >= 4 ? sorted[(sorted.Count * 3) / 4] : ExposureMeasurementProducts.Median(sorted)!.Value;

    internal static double ComputeStoredCodeScale(FrameLayoutDescriptor layout)
    {
        if (layout.LevelCodeSpace != FrameLevelCodeSpace.NativeSample &&
            layout.StoredCodeTransform is not (FrameStoredCodeTransform.LeftShiftedV1 or FrameStoredCodeTransform.FullRangeScaledV1))
        {
            return 1.0;
        }

        return layout.StoredCodeTransform switch
        {
            FrameStoredCodeTransform.LeftShiftedV1 => 1 << Math.Max(0, layout.ContainerDepthBits - layout.SampleDepthBits),
            FrameStoredCodeTransform.FullRangeScaledV1 when layout.SampleDepthBits < layout.ContainerDepthBits =>
                ((1 << layout.ContainerDepthBits) - 1d) / ((1 << layout.SampleDepthBits) - 1d),
            _ => 1.0
        };
    }

    private static int ComputeSaturatedFractionMillionths(ExposureMeasurementV1? measurement, FrameLayoutDescriptor layout)
    {
        if (measurement?.RawBrightness is not { SampleCount: > 0 } raw)
        {
            return 0;
        }

        long clipped = raw.SensorClipping.Count ?? 0;
        if (measurement.LinearOutputClipping?.Count is { } linearClipped && linearClipped > clipped)
        {
            clipped = linearClipped;
        }

        var total = Math.Max(1L, raw.SampleCount);
        return (int)Math.Clamp((long)(((Int128)clipped * OneMillion + total / 2) / total), 0, OneMillion);
    }

    private static TileGeometrySupport[] EvaluateTileGeometry(
        int width, int height, int cols, int rows,
        ProjectedSceneAperture? aperture, IImageProjector? projector,
        HorizonObstructionEvaluator? horizon,
        IReadOnlyList<NormalizedCloudRectangle> excludedRegions,
        Linear16Frame? defectMask,
        SolarDiskAppearance? moon,
        RegionalStellarVisibilityOptions options,
        CancellationToken cancellationToken)
    {
        var result = new TileGeometrySupport[cols * rows];
        for (var row = 0; row < rows; row++)
        {
            var y0 = (int)((long)row * height / rows);
            var y1 = (int)((long)(row + 1) * height / rows);
            for (var col = 0; col < cols; col++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var x0 = (int)((long)col * width / cols);
                var x1 = (int)((long)(col + 1) * width / cols);
                int total = 0, supported = 0;
                int outAperture = 0, obstructed = 0, unknownHorizon = 0, excludedRegion = 0, defect = 0, moonGlare = 0;

                var tileW = Math.Max(1, x1 - x0);
                var tileH = Math.Max(1, y1 - y0);
                var stepCountX = Math.Min(TileAreaSampleGrid, tileW);
                var stepCountY = Math.Min(TileAreaSampleGrid, tileH);

                for (var sy = 0; sy < stepCountY; sy++)
                {
                    var py = y0 + (sy + 0.5) * tileH / stepCountY;
                    for (var sx = 0; sx < stepCountX; sx++)
                    {
                        var px = x0 + (sx + 0.5) * tileW / stepCountX;
                        var pt = new PixelPoint(px, py);
                        total++;

                        if (aperture is not null && !aperture.Contains(pt))
                        {
                            outAperture++;
                            continue;
                        }
                        if (IsInsideExcludedRegion(pt, width, height, excludedRegions))
                        {
                            excludedRegion++;
                            continue;
                        }
                        if (IsDefectMasked(pt, defectMask))
                        {
                            defect++;
                            continue;
                        }
                        if (projector is not null && (horizon is not null || moon is { Direction.AltitudeDegrees: > 0 }))
                        {
                            var dir = projector.Unproject(pt);
                            if (dir is null)
                            {
                                outAperture++;
                                continue;
                            }
                            if (horizon is not null)
                            {
                                var elig = horizon.Evaluate(dir.Value);
                                if (elig == HorizonObstructionEligibility.Obstructed)
                                {
                                    obstructed++;
                                    continue;
                                }
                                if (elig == HorizonObstructionEligibility.Unknown)
                                {
                                    unknownHorizon++;
                                    continue;
                                }
                                if (elig != HorizonObstructionEligibility.Clear)
                                {
                                    outAperture++;
                                    continue;
                                }
                            }
                            if (IsInsideMoonGlare(dir.Value, moon, options))
                            {
                                moonGlare++;
                                continue;
                            }
                        }

                        supported++;
                    }
                }

                var fraction = total > 0 ? (int)Math.Round((double)supported * OneMillion / total, MidpointRounding.AwayFromZero) : 0;
                var dominant = DominantExclusionReason(outAperture, obstructed, unknownHorizon, excludedRegion, defect, moonGlare);
                result[row * cols + col] = new(fraction, dominant);
            }
        }

        return result;
    }

    private static string DominantExclusionReason(int outAperture, int obstructed, int unknownHorizon, int excludedRegion, int defect, int moonGlare)
    {
        var best = outAperture;
        var reason = RegionalVisibilityReasonCodes.TileOutsideAperture;
        if (obstructed > best) { best = obstructed; reason = RegionalVisibilityReasonCodes.TileHorizonObstructed; }
        if (unknownHorizon > best) { best = unknownHorizon; reason = RegionalVisibilityReasonCodes.TileHorizonUnknown; }
        if (excludedRegion > best) { best = excludedRegion; reason = RegionalVisibilityReasonCodes.TileExcludedRegion; }
        if (defect > best) { best = defect; reason = RegionalVisibilityReasonCodes.TileDefectMasked; }
        if (moonGlare > best) { reason = RegionalVisibilityReasonCodes.TileMoonGlareExcluded; }
        return reason;
    }

    private static RegionalVisibilityTileV1[] CreateEmptyTiles(
        int width, int height,
        RegionalStellarVisibilityOptions options,
        RegionalVisibilityTileStatus nonExcludedStatus,
        string nonExcludedReason,
        ProjectedSceneV1? prediction,
        HorizonObstructionEvaluator? horizon,
        Linear16Frame? defectMask,
        CancellationToken cancellationToken)
    {
        var cols = options.GridColumns;
        var rows = options.GridRows;
        var aperture = prediction is null ? null : new ProjectedSceneAperture(prediction);
        var projector = prediction is null ? null : ProjectorFactory.Create(ProjectedSceneJson.ToProjectionContext(prediction.Projection));
        var support = EvaluateTileGeometry(width, height, cols, rows, aperture, projector,
            horizon is { IsCompatible: true } ? horizon : null,
            options.ExcludedRegions ?? [], defectMask, null, options, cancellationToken);

        var tiles = new RegionalVisibilityTileV1[cols * rows];
        for (var row = 0; row < rows; row++)
        {
            var y0 = (int)((long)row * height / rows);
            var y1 = (int)((long)(row + 1) * height / rows);
            for (var col = 0; col < cols; col++)
            {
                var idx = row * cols + col;
                var x0 = (int)((long)col * width / cols);
                var x1 = (int)((long)(col + 1) * width / cols);
                var isExcluded = support[idx].SupportedAreaFractionMillionths < options.MinimumTileSupportedAreaMillionths;
                tiles[idx] = new RegionalVisibilityTileV1(
                    col, row, x0, y0, x1 - x0, y1 - y0,
                    isExcluded ? RegionalVisibilityTileStatus.Excluded : nonExcludedStatus,
                    support[idx].SupportedAreaFractionMillionths,
                    0, 0, 0, 0, 0, 0, 0, 0, null,
                    [isExcluded ? support[idx].DominantExclusionReason : nonExcludedReason]);
            }
        }
        return tiles;
    }

    private static RegionalVisibilityTileV1[] CreateWithheldTilesFromSupport(
        int width, int height, int cols, int rows,
        TileGeometrySupport[] support,
        int[] predicted, int[] expected, int[] marginal, int[] masked,
        int[] matched, int[] measExcluded, int[] unassociated, int[] offsetCandidates,
        string withheldTileReason)
    {
        var tiles = new RegionalVisibilityTileV1[cols * rows];
        for (var row = 0; row < rows; row++)
        {
            var y0 = (int)((long)row * height / rows);
            var y1 = (int)((long)(row + 1) * height / rows);
            for (var col = 0; col < cols; col++)
            {
                var idx = row * cols + col;
                var x0 = (int)((long)col * width / cols);
                var x1 = (int)((long)(col + 1) * width / cols);
                var effectiveExpected = Math.Max(0, expected[idx] - measExcluded[idx]);
                var m = Math.Min(matched[idx], effectiveExpected);
                tiles[idx] = new RegionalVisibilityTileV1(
                    col, row, x0, y0, x1 - x0, y1 - y0,
                    support[idx].SupportedAreaFractionMillionths == 0
                        ? RegionalVisibilityTileStatus.Excluded
                        : RegionalVisibilityTileStatus.InsufficientEvidence,
                    support[idx].SupportedAreaFractionMillionths,
                    predicted[idx], effectiveExpected, marginal[idx], masked[idx], m, measExcluded[idx],
                    unassociated[idx], offsetCandidates[idx], null,
                    [support[idx].SupportedAreaFractionMillionths == 0 ? support[idx].DominantExclusionReason : withheldTileReason]);
            }
        }
        return tiles;
    }

    private static RegionalVisibilityGridV1 BuildGridSummary(
        RegionalStellarVisibilityOptions options,
        RegionalVisibilityTileV1[] tiles)
    {
        int excluded = 0, insufficient = 0, clear = 0, deficit = 0;
        int totalExpected = 0, totalMatched = 0, totalMarginal = 0, totalMasked = 0, totalUnassociated = 0;
        foreach (var tile in tiles)
        {
            switch (tile.Status)
            {
                case RegionalVisibilityTileStatus.Excluded: excluded++; break;
                case RegionalVisibilityTileStatus.InsufficientEvidence: insufficient++; break;
                case RegionalVisibilityTileStatus.Clear: clear++; break;
                case RegionalVisibilityTileStatus.DetectionDeficit: deficit++; break;
            }
            totalExpected += tile.ExpectedDetectableCount;
            totalMatched += tile.MatchedMeasuredCount;
            totalMarginal += tile.MarginalStarCount;
            totalMasked += tile.MaskedOutStarCount;
            totalUnassociated += tile.UnassociatedMeasuredCount;
        }

        return new RegionalVisibilityGridV1(
            options.GridColumns,
            options.GridRows,
            tiles.Length,
            excluded,
            insufficient,
            clear + deficit,
            clear,
            deficit,
            totalExpected,
            totalMatched,
            totalMarginal,
            totalMasked,
            totalUnassociated,
            options.DeficitVisibilityThresholdMillionths,
            options.MinimumExpectedStarsPerTile);
    }

    private static RegionalVisibilityExposureConditionsV1 BuildConditions(
        RegionalStellarVisibilityRequest request,
        FrameLayoutDescriptor layout,
        ExposureMeasurementV1 measurement,
        MonoSensorResponse sensor,
        double? measuredBackgroundAdu,
        double? measuredNoiseSigmaAdu,
        double backgroundElectronsPerSecond,
        int saturatedFractionMillionths,
        CaptureSolarRegime? solarRegime,
        double? sunAltitude,
        CloudAssessmentEnvironmentV1 environment,
        SolarDiskAppearance? moon,
        bool moonInFov,
        double effectivePsfSigma,
        double? medianHfd,
        int validFocusSamples,
        int tooBroadCount,
        int anchorCount,
        double? estimatedShiftPixels,
        double? estimatedRollDegrees,
        double? maxImpliedCornerShiftPixels,
        string? profileName,
        string? horizonIdentity) => new(
            Round(request.ExposureSeconds),
            Round(request.Gain),
            layout.PixelFormat,
            layout.Width,
            layout.Height,
            measurement.Input.Calibration,
            Round(sensor.ElectronsPerAdu),
            Round(sensor.ReadNoiseElectrons),
            Round(measuredBackgroundAdu),
            Round(measuredNoiseSigmaAdu),
            Round(backgroundElectronsPerSecond),
            saturatedFractionMillionths,
            solarRegime,
            Round(sunAltitude),
            environment.PrecipitationStatus,
            environment.PrecipitationDetected,
            Round(moon?.Direction.AltitudeDegrees),
            Round(moon?.Direction.AzimuthDegrees),
            Round(moon?.IlluminatedFraction),
            moonInFov,
            Round(effectivePsfSigma),
            Round(medianHfd),
            validFocusSamples,
            tooBroadCount,
            anchorCount,
            Round(estimatedShiftPixels),
            Round(estimatedRollDegrees),
            Round(maxImpliedCornerShiftPixels),
            profileName,
            horizonIdentity);

    private static bool HasNonCloudExclusionNear(
        PixelPoint point,
        IReadOnlyList<StellarMeasurementExclusion> exclusions,
        double matchRadiusPixels)
    {
        var rSq = matchRadiusPixels * matchRadiusPixels;
        var maxLocalizedSpan = Math.Max(20, (int)Math.Ceiling(matchRadiusPixels * 6.0));
        foreach (var exclusion in exclusions)
        {
            if (exclusion.ReasonCode is not (StellarExclusionReasons.SaturatedExcessive or
                StellarExclusionReasons.Blended or StellarExclusionReasons.Crowded or
                StellarExclusionReasons.MaskedAperture or StellarExclusionReasons.ImageEdge))
            {
                continue;
            }

            // Extended cloud banks can touch the image edge or aperture boundary and be rejected as ImageEdge/Blended
            // with a huge bounding box; only compact star-scale footprints should excuse an expected star.
            if (exclusion.Footprint is { } span &&
                ((span.MaxX - span.MinX) > maxLocalizedSpan || (span.MaxY - span.MinY) > maxLocalizedSpan))
            {
                continue;
            }

            if (exclusion.Footprint is { } fp && fp.Contains(point, 1.0))
            {
                return true;
            }

            var dx = exclusion.Peak.X - point.X;
            var dy = exclusion.Peak.Y - point.Y;
            if (dx * dx + dy * dy <= rSq)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsInsideMoonGlare(AltAzPoint direction, SolarDiskAppearance? moon, RegionalStellarVisibilityOptions options)
    {
        if (moon is not { Direction.AltitudeDegrees: > 0 } activeMoon ||
            options.MoonGlareExclusionDegrees <= 0 ||
            activeMoon.IlluminatedFraction < 0.25)
        {
            return false;
        }

        return AngularSeparationDegrees(direction, activeMoon.Direction) <= options.MoonGlareExclusionDegrees;
    }

    internal static double AngularSeparationDegrees(AltAzPoint a, AltAzPoint b)
    {
        var alt1 = a.AltitudeDegrees * (Math.PI / 180.0);
        var alt2 = b.AltitudeDegrees * (Math.PI / 180.0);
        var dAz = (a.AzimuthDegrees - b.AzimuthDegrees) * (Math.PI / 180.0);
        var cosSep = Math.Clamp(Math.Sin(alt1) * Math.Sin(alt2) + Math.Cos(alt1) * Math.Cos(alt2) * Math.Cos(dAz), -1.0, 1.0);
        return Math.Acos(cosSep) * (180.0 / Math.PI);
    }

    private static bool IsInsideExcludedRegion(PixelPoint point, int width, int height, IReadOnlyList<NormalizedCloudRectangle> regions)
    {
        if (regions.Count == 0) return false;
        var nx = (int)Math.Clamp(Math.Floor(point.X * OneMillion / width), 0, OneMillion - 1);
        var ny = (int)Math.Clamp(Math.Floor(point.Y * OneMillion / height), 0, OneMillion - 1);
        foreach (var r in regions)
        {
            if (nx >= r.LeftMillionths && nx < r.RightMillionths && ny >= r.TopMillionths && ny < r.BottomMillionths)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsDefectMasked(PixelPoint point, Linear16Frame? defectMask)
    {
        if (defectMask is null) return false;
        var x = (int)Math.Floor(point.X);
        var y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= defectMask.Width || y >= defectMask.Height) return false;
        var offset = y * defectMask.StrideBytes + x * 2;
        var span = defectMask.PixelData.Span;
        return (span[offset] | (span[offset + 1] << 8)) != 0;
    }

    private static bool InsideBounds(PixelPoint point, int width, int height)
        => double.IsFinite(point.X) && double.IsFinite(point.Y) &&
           point.X >= 0 && point.X < width && point.Y >= 0 && point.Y < height;

    private static int TileIndexOf(PixelPoint point, int width, int height, int cols, int rows)
    {
        var col = Math.Clamp((int)Math.Floor(point.X * cols / width), 0, cols - 1);
        var row = Math.Clamp((int)Math.Floor(point.Y * rows / height), 0, rows - 1);
        return row * cols + col;
    }

    private static double Hypot(double dx, double dy) => Math.Sqrt(dx * dx + dy * dy);

    private static double Round(double value)
    {
        if (!double.IsFinite(value)) return 0;
        var rounded = Math.Round(value, 6, MidpointRounding.ToEven);
        return rounded == 0 ? 0 : rounded;
    }

    private static double? Round(double? value)
        => value is { } present && double.IsFinite(present) ? Round(present) : null;

    private static void AddUnique(List<string> list, string code)
    {
        if (!list.Contains(code, StringComparer.Ordinal))
        {
            list.Add(code);
        }
    }

    private static ExposureMeasurementV1? VerifyMeasurementInput(
        ProcessingArtifact raw,
        FrameLayoutDescriptor rawLayout,
        ProcessingArtifact? measurementArtifact,
        ExposureMeasurementV1? measurement)
    {
        if (measurement is null || measurementArtifact is null)
        {
            return null;
        }

        var captureId = raw.CaptureId!.Value;
        if (measurementArtifact.Role != FrameArtifactRole.Metadata ||
            measurementArtifact.SchemaVersion != ExposureMeasurementProducts.SchemaVersion ||
            measurementArtifact.CaptureId != captureId ||
            measurementArtifact.SourceArtifactIds?.Contains(raw.ArtifactId) != true)
        {
            return null;
        }

        var rawSource = measurement.Input.Raw;
        if (rawSource.CaptureId != captureId ||
            rawSource.ArtifactId != raw.ArtifactId ||
            !ExposureMeasurementProducts.Same(rawSource.DescriptorSha256, raw.DescriptorIdentitySha256) ||
            rawSource.Layout != rawLayout)
        {
            return null;
        }

        if (measurement.Input.Linear is { } linearSource &&
            (linearSource.CaptureId != captureId ||
             linearSource.Layout.Width != rawLayout.Width ||
             linearSource.Layout.Height != rawLayout.Height ||
             linearSource.Layout.PixelFormat != rawLayout.PixelFormat))
        {
            return null;
        }

        return measurement;
    }

    private static (ProjectedSceneV1? Prediction, bool SimulatorTruthRejected) VerifyPredictionInput(
        ProcessingArtifact raw,
        FrameLayoutDescriptor rawLayout,
        ProcessingArtifact? predictionArtifact,
        ProjectedSceneV1? prediction,
        ExposureMeasurementV1? verifiedMeasurement)
    {
        if (prediction is null || predictionArtifact is null)
        {
            return (null, false);
        }

        if (prediction.Kind != ProjectedSceneKind.Predicted)
        {
            return (null, true);
        }

        var captureId = raw.CaptureId!.Value;
        if (predictionArtifact.Role != FrameArtifactRole.Metadata ||
            !ProjectedSceneV1.IsSupportedSchemaVersion(predictionArtifact.SchemaVersion) ||
            predictionArtifact.CaptureId != captureId ||
            predictionArtifact.SourceArtifactIds?.Contains(raw.ArtifactId) != true)
        {
            return (null, false);
        }

        try
        {
            ProjectedSceneJson.Validate(prediction);
            var computedIdentity = ProjectedSceneJson.ComputeIdentity(prediction);
            if (!ExposureMeasurementProducts.Same(prediction.SceneIdentitySha256, computedIdentity) ||
                !ExposureMeasurementProducts.Same(ResolveContentSha256(predictionArtifact), prediction.SceneIdentitySha256) ||
                prediction.Source.CaptureId != captureId ||
                prediction.Source.ArtifactId != raw.ArtifactId ||
                !ExposureMeasurementProducts.Same(prediction.Source.ArtifactIdentitySha256, raw.DescriptorIdentitySha256) ||
                prediction.ImageTransform != ProjectedSceneImageTransformV1.Identity(rawLayout.Width, rawLayout.Height))
            {
                return (null, false);
            }

            if (verifiedMeasurement is not null)
            {
                ExposureCatalogPredictionProducts.ValidateProjection(prediction, verifiedMeasurement.Input);
            }

            return (prediction, false);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
        {
            return (null, false);
        }
    }

    private static string? ResolveContentSha256(ProcessingArtifact? artifact)
    {
        if (artifact is null)
        {
            return null;
        }
        if (!string.IsNullOrWhiteSpace(artifact.ContentIdentitySha256))
        {
            return artifact.ContentIdentitySha256.ToUpperInvariant();
        }
        return artifact.Payload.Length > 0
            ? ProcessingIdentity.ComputePayloadSha256(artifact.Payload).ToUpperInvariant()
            : null;
    }

    private static List<ProcessingArtifact> BuildSourceArtifactList(RegionalStellarVisibilityRequest request)
    {
        var list = new List<ProcessingArtifact>(4) { request.Raw };
        if (request.MeasurementArtifact is not null) list.Add(request.MeasurementArtifact);
        if (request.PredictionArtifact is not null) list.Add(request.PredictionArtifact);
        if (request.AstrometryArtifact is not null) list.Add(request.AstrometryArtifact);
        return list;
    }

    private static ProcessingRecipeIdentity CreateRecipeIdentity(
        RegionalStellarVisibilityRequest request,
        RegionalStellarVisibilityOptions options,
        RegionalVisibilitySourceV1 sources)
    {
        string? envContentSha = null;
        if (request.Environment is { } env)
        {
            envContentSha = CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
            {
                env.SchemaVersion,
                env.SolarRegime,
                env.PrecipitationStatus,
                env.PrecipitationObservationId,
                env.PrecipitationContentSha256,
                env.PrecipitationDetected,
                env.InputIdentitySha256
            }));
        }

        string? moonOverrideSha = null;
        if (request.MoonOverride is { } moon)
        {
            moonOverrideSha = CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(new
            {
                moon.Body,
                moon.Utc,
                AltitudeDegrees = Round(moon.Direction.AltitudeDegrees),
                AzimuthDegrees = Round(moon.Direction.AzimuthDegrees),
                IlluminatedFraction = Round(moon.IlluminatedFraction),
                AngularRadiusDegrees = Round(moon.AngularRadiusDegrees),
                VisualMagnitude = Round(moon.VisualMagnitude),
                BrightLimbAngleDegrees = Round(moon.BrightLimbAngleDegrees),
                DistanceKilometers = Round(moon.DistanceKilometers)
            }));
        }

        string? regionalBgSha = null;
        if (request.RegionalBackground is { } regionalBg)
        {
            regionalBgSha = CaptureContractJson.ComputeCanonicalJsonSha256(JsonSerializer.SerializeToElement(
                regionalBg.Select(static tile => new
                {
                    tile.Available,
                    BackgroundAdu = Round(tile.BackgroundAdu),
                    NoiseSigmaAdu = Round(tile.NoiseSigmaAdu)
                }).ToArray()));
        }

        var binding = new RegionalVisibilityRecipeBinding(
            options,
            sources,
            Round(request.ExposureSeconds),
            Round(request.Gain),
            request.HorizonEvaluator?.Profile.ComputeIdentitySha256(),
            request.HorizonEvaluator?.IsCompatible,
            request.Environment?.InputIdentitySha256,
            envContentSha,
            Round(request.SunAltitudeDegreesOverride),
            moonOverrideSha,
            request.AcceptedMapping is not null,
            request.AstrometrySolution?.Assessment.CalibrationIdentitySha256,
            request.AstrometrySolution?.SceneIdentitySha256,
            regionalBgSha,
            request.ResponseMismatchReason);
        return ProcessingIdentity.CreateRecipeIdentity(Definition, ProcessingRecipeSupport.Normalize(binding));
    }

    private readonly record struct TileGeometrySupport(int SupportedAreaFractionMillionths, string DominantExclusionReason);

    private readonly record struct RegionalVisibilityRecipeBinding(
        RegionalStellarVisibilityOptions Options,
        RegionalVisibilitySourceV1 Sources,
        double ExposureSeconds,
        double Gain,
        string? HorizonProfileIdentitySha256,
        bool? HorizonIsCompatible,
        string? EnvironmentIdentitySha256,
        string? EnvironmentContentSha256,
        double? SunAltitudeDegreesOverride,
        string? MoonOverrideContentSha256,
        bool HasAcceptedMapping,
        string? AcceptedMappingCalibrationSha256,
        string? AcceptedMappingAssessmentSha256,
        string? RegionalBackgroundSha256,
        string? ResponseMismatchReason);
}
