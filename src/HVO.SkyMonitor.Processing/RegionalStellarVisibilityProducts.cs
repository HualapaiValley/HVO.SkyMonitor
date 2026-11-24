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
    double? SunAltitudeDegreesOverride = null);

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

        var sources = BuildSourceArtifactList(request);
        var sourceProvenance = new RegionalVisibilitySourceV1(
            captureId,
            raw.ArtifactId,
            raw.DescriptorIdentitySha256.ToUpperInvariant(),
            request.MeasurementArtifact?.ArtifactId,
            request.MeasurementArtifact?.ContentIdentitySha256?.ToUpperInvariant(),
            request.PredictionArtifact?.ArtifactId,
            request.PredictionArtifact?.ContentIdentitySha256?.ToUpperInvariant(),
            request.AstrometryArtifact?.ArtifactId,
            request.AstrometryArtifact?.ContentIdentitySha256?.ToUpperInvariant(),
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

        // Compute Sun and Moon geometry when independent prediction supplies observer/time.
        double? sunAltitude = request.SunAltitudeDegreesOverride;
        CaptureSolarRegime? solarRegime = environment.SolarRegime;
        SolarDiskAppearance? moon = request.MoonOverride;
        if (request.Prediction is { } predForAstro)
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

        var moonInFov = false;
        if (moon is { Direction.AltitudeDegrees: > 0 } aboveMoon && request.Prediction is { } predForMoon)
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
        var saturatedFractionMillionths = ComputeSaturatedFractionMillionths(request.Measurement, rawLayout);

        // Focus inspection from step 4c HFD + step 4b too-broad exclusions.
        var (focusBasis, medianHfd, validFocusSamples, tooBroadCount) = EvaluateFocus(request.Measurement, options);

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

        var measurement = request.Measurement;
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

        var prediction = request.Prediction;
        if (prediction is null)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.PredictionUnavailable);
        }
        else if (prediction.Kind != ProjectedSceneKind.Predicted)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.SimulatorTruthRejected);
        }
        else if (measurement is not null)
        {
            try
            {
                ExposureCatalogPredictionProducts.ValidateProjection(prediction, measurement.Input);
            }
            catch (InvalidDataException)
            {
                AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.PredictionUnavailable);
            }
        }

        if (request.HorizonEvaluator is { IsCompatible: false })
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.HorizonIncompatible);
        }

        // Resolve photometric response profile.
        MonoSensorResponse? resolvedSensor = null;
        double redResp = 1, greenResp = 1, blueResp = 1;
        var profile = options.PhotometricProfile;
        if (profile is null)
        {
            AddUnique(withholdingReasons, RegionalVisibilityReasonCodes.MissingSensorResponse);
        }
        else if (!profile.TryResolve(request.Gain, measurement?.Input.Linear?.Layout ?? rawLayout,
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

        // Compute measured sky background & noise in linear ADU and electron domain.
        var measuredLayout = measurement?.Input.Linear?.Layout ?? rawLayout;
        var storedScale = ComputeStoredCodeScale(measuredLayout);
        var electronsPerStoredAdu = resolvedSensor is not null ? resolvedSensor.ElectronsPerAdu / storedScale : (double?)null;
        var (measuredBackgroundAdu, measuredNoiseSigmaAdu, backgroundElectronsPerSecond) =
            EstimateSkyBackground(measurement, measuredLayout, resolvedSensor, storedScale, request.ExposureSeconds);

        var effectivePsfSigma = profile?.NominalPsfSigmaPixels;
        if (effectivePsfSigma is { } nominalSigma && focusBasis == RegionalVisibilityFocusBasis.Usable && medianHfd is { } hfd)
        {
            // Never shrink below nominal PSF, but widen expected PSF when measured HFD indicates softer seeing.
            effectivePsfSigma = Math.Max(nominalSigma, hfd / FwhmPerSigma);
        }

        // Determine geometry basis: accepted astrometry solve vs fixed geometry.
        var acceptedSolve = request.AstrometrySolution is { Assessment.HasMeasuredMapping: true } &&
            request.AcceptedMapping is not null &&
            prediction is not null &&
            ExposureMeasurementProducts.Same(request.AstrometrySolution.SceneIdentitySha256, prediction.SceneIdentitySha256) &&
            request.AstrometrySolution.Assessment.Frame.CaptureId == raw.CaptureId &&
            request.AstrometrySolution.Assessment.Frame.SourceArtifactId == raw.ArtifactId;

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
                RegionalVisibilityReasonCodes.TileWithheld, prediction, request.HorizonEvaluator, request.DefectMask?.Frame, cancellationToken);
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
            measuredBackgroundAdu, measuredNoiseSigmaAdu, backgroundElectronsPerSecond!.Value,
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
        double? measuredBackgroundAdu,
        double? measuredNoiseSigmaAdu,
        double backgroundElectronsPerSecond,
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
        var defectMask = request.DefectMask?.Frame;
        var horizon = request.HorizonEvaluator;
        var excludedRegions = options.ExcludedRegions ?? [];

        var cols = options.GridColumns;
        var rows = options.GridRows;
        var tileCount = cols * rows;

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
        // Expected noise floor in ADU (never underestimate below measured noise sigma).
        var modelPerPixelNoiseElectrons = Math.Sqrt(
            (backgroundElectronsPerSecond + profile.DarkCurrentElectronsPerSecond) * request.ExposureSeconds +
            sensor.ReadNoiseElectrons * sensor.ReadNoiseElectrons);
        var modelNoiseAdu = modelPerPixelNoiseElectrons / electronsPerStoredAdu;
        var effectiveNoiseAdu = measuredNoiseSigmaAdu is { } measNoise && measNoise > 0
            ? Math.Max(modelNoiseAdu, measNoise)
            : modelNoiseAdu;
        var bayerSnrPenalty = isBayer ? Math.Sqrt(BayerNoiseCorrelationFactor) : 1.0;

        var blackLevelStored = CenteredNormalizationLayout.CreateWorkingLayout(measuredLayout).BlackLevel ?? 0d;
        var whiteLevelStored = FrameWhiteThreshold.TryResolveStored(measuredLayout, out var storedWhite, out _)
            ? (double)storedWhite : ushort.MaxValue;
        var maxChargeElectrons = Math.Min(sensor.FullWellElectrons, Math.Max(0, whiteLevelStored - blackLevelStored) * electronsPerStoredAdu);

        // Classify each predicted star into expected detectable / marginal / masked-out.
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

            // Geometric masks.
            if (!aperture.ContainsWithMargin(pixel, options.EdgeMarginPixels) ||
                IsInsideExcludedRegion(pixel, width, height, excludedRegions) ||
                IsDefectMasked(pixel, defectMask) ||
                (horizon is not null && horizon.Evaluate(star.GeometricHorizontal) != HorizonObstructionEligibility.Clear) ||
                IsInsideMoonGlare(star.GeometricHorizontal, moon, options) ||
                crowded[i])
            {
                tileMasked[tileIndex]++;
                continue;
            }

            // Photometric detectability at actual exposure, gain, sky background, vignetting, and CFA response.
            var vignette = 1.0 - profile.VignettingStrength * projection.NormalizedRadiusSquared(pixel.X, pixel.Y);
            if (vignette <= 0)
            {
                tileMasked[tileIndex]++;
                continue;
            }

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
                backgroundElectronsPerSecond,
                sensor.ReadNoiseElectrons,
                profile.DarkCurrentElectronsPerSecond,
                apertureFraction,
                aperturePixels,
                responseMultiplier,
                vignette);

            var predictedSignal = StellarVisibilityModel.Predict(star.Magnitude, photometry);
            var effectiveBackgroundNoiseElectrons = effectiveNoiseAdu * electronsPerStoredAdu;
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
            var detectorPeakFloor = Math.Max(detectorOptions.MinimumPeakAboveBackground, detectorOptions.PeakSigma * effectiveNoiseAdu);

            // Check expected excessive saturation (bright star saturating > MaximumSaturatedSamples gets rejected by measurer).
            var bgElectrons = (backgroundElectronsPerSecond * vignette + profile.DarkCurrentElectronsPerSecond) * request.ExposureSeconds;
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
                var (tx, ty, rollRad) = EstimateRigidOffset(matchedAnchors, centerX, centerY);
                estimatedShiftPixels = Hypot(tx, ty);
                estimatedRollDegrees = rollRad * (180.0 / Math.PI);
                maxImpliedCornerShiftPixels = estimatedShiftPixels.Value + Math.Abs(rollRad) * maxFrameRadius;

                if (estimatedShiftPixels.Value > options.MaximumPointingShiftPixels)
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

                // Check implied rigid shift+roll at this unmatched star's radius from principal point!
                if (estimatedShiftPixels is { } shift && estimatedRollDegrees is { } rollDeg)
                {
                    var starRadius = Hypot(pixel.X - centerX, pixel.Y - centerY);
                    var impliedAtStar = shift + Math.Abs(rollDeg * (Math.PI / 180.0)) * starRadius;
                    if (impliedAtStar >= options.MatchRadiusPixels * options.MaximumImpliedRollShiftFraction)
                    {
                        tilePointingUncertain[tileIdx] = true;
                    }
                }
            }
        }

        // If a substantial fraction of unmatched expected stars have nearby unexplained offset detections under fixed geometry,
        // the pointing is shifted/rolled — withhold globally!
        if (!acceptedSolve && totalUnmatchedExpected > 0 &&
            (totalOffsetMatches >= 2 && totalOffsetMatches * 2 >= totalUnmatchedExpected ||
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

    private static (double Tx, double Ty, double RollRadians) EstimateRigidOffset(
        List<(PixelPoint Expected, PixelPoint Measured)> anchors,
        double centerX,
        double centerY)
    {
        var meanDx = anchors.Average(static a => a.Measured.X - a.Expected.X);
        var meanDy = anchors.Average(static a => a.Measured.Y - a.Expected.Y);
        if (anchors.Count < 2)
        {
            return (meanDx, meanDy, 0);
        }

        // Fit small-angle 2D rotation around principal point using centered anchor residuals.
        var meanExpX = anchors.Average(static a => a.Expected.X) - centerX;
        var meanExpY = anchors.Average(static a => a.Expected.Y) - centerY;
        double num = 0, den = 0;
        foreach (var (expected, measured) in anchors)
        {
            var rx = expected.X - centerX - meanExpX;
            var ry = expected.Y - centerY - meanExpY;
            var dx = (measured.X - expected.X) - meanDx;
            var dy = (measured.Y - expected.Y) - meanDy;
            // Small rotation by theta: d(rx, ry) = (-ry * theta, rx * theta)
            num += rx * dy - ry * dx;
            den += rx * rx + ry * ry;
        }

        var rollRad = den > 1e-6 ? Math.Atan2(num, den) : 0;
        // Recompute translation at principal point after removing rotation component.
        double txSum = 0, tySum = 0;
        foreach (var (expected, measured) in anchors)
        {
            var rx = expected.X - centerX;
            var ry = expected.Y - centerY;
            txSum += (measured.X - expected.X) - (-ry * rollRad);
            tySum += (measured.Y - expected.Y) - (rx * rollRad);
        }

        return (txSum / anchors.Count, tySum / anchors.Count, rollRad);
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

    private static (double? BackgroundAdu, double? NoiseSigmaAdu, double? BackgroundElectronsPerSec)
        EstimateSkyBackground(
            ExposureMeasurementV1? measurement,
            FrameLayoutDescriptor layout,
            MonoSensorResponse? sensor,
            double storedScale,
            double exposureSeconds)
    {
        if (measurement is null)
        {
            return (null, null, null);
        }

        var workingBlack = CenteredNormalizationLayout.CreateWorkingLayout(layout).BlackLevel ?? 0d;
        double? bgAdu = null;
        double? noiseAdu = null;

        if (measurement.Sources.Detections.Count > 0)
        {
            var bgs = measurement.Sources.Detections.Select(static d => d.Background).Where(double.IsFinite).Order().ToList();
            var noises = measurement.Sources.Detections.Select(static d => d.NoiseSigma).Where(static n => double.IsFinite(n) && n >= 0).Order().ToList();
            bgAdu = LowerQuartileOrMedian(bgs);
            noiseAdu = LowerQuartileOrMedian(noises);
        }

        if (bgAdu is null && measurement.RawBrightness is { } brightness)
        {
            bgAdu = brightness.Mean;
        }

        double? bgRate = null;
        if (bgAdu is { } measuredBg && sensor is not null && exposureSeconds > 0)
        {
            var electronsPerStoredAdu = sensor.ElectronsPerAdu / storedScale;
            var excessAdu = Math.Max(0, measuredBg - workingBlack);
            bgRate = excessAdu * electronsPerStoredAdu / exposureSeconds;
        }

        return (bgAdu, noiseAdu, bgRate);
    }

    private static double? LowerQuartileOrMedian(List<double> sorted)
    {
        if (sorted.Count == 0)
        {
            return null;
        }
        return sorted.Count >= 4 ? sorted[sorted.Count / 4] : ExposureMeasurementProducts.Median(sorted);
    }

    private static double ComputeStoredCodeScale(FrameLayoutDescriptor layout)
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
        var binding = new RegionalVisibilityRecipeBinding(
            options,
            sources,
            Round(request.ExposureSeconds),
            Round(request.Gain),
            request.HorizonEvaluator?.Profile.ComputeIdentitySha256(),
            request.Environment?.InputIdentitySha256);
        return ProcessingIdentity.CreateRecipeIdentity(Definition, ProcessingRecipeSupport.Normalize(binding));
    }

    private readonly record struct TileGeometrySupport(int SupportedAreaFractionMillionths, string DominantExclusionReason);

    private readonly record struct RegionalVisibilityRecipeBinding(
        RegionalStellarVisibilityOptions Options,
        RegionalVisibilitySourceV1 Sources,
        double ExposureSeconds,
        double Gain,
        string? HorizonProfileIdentitySha256,
        string? EnvironmentIdentitySha256);
}
