using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;

namespace HVO.SkyMonitor.Processing.Tests;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Performance", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "MSTest handles its async tests.")]
public sealed class RegionalStellarVisibilityTests
{
    private static readonly DateTimeOffset NightUtc = new(2026, 11, 9, 6, 0, 0, TimeSpan.Zero); // New moon, astronomical night in Arizona
    private static readonly DateTimeOffset DayUtc = new(2026, 11, 9, 19, 0, 0, TimeSpan.Zero); // Noon local in Arizona
    private static readonly DateTimeOffset TwilightUtc = new(2026, 11, 9, 1, 15, 0, TimeSpan.Zero); // Evening twilight in Arizona
    private static readonly DateTimeOffset BrightMoonUtc = new(2026, 11, 24, 6, 0, 0, TimeSpan.Zero); // Full moon high in Arizona sky
    private static readonly ObserverLocation Observatory = new(35.347, -113.878, 1200);
    private static readonly ProcessingCompatibilityIdentity Compatibility =
        new("rig", "orientation", "calibration", "mask", "sensor", "setpoint", "profile");
    private static readonly JsonSerializerOptions MeasurementJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false) }
    };

    [TestMethod]
    public void ContractCanonicalJsonRoundTripValidatesAndRejectsTampering()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var field = BuildSyntheticField(rig, NightUtc, CreateGridStars(rig, NightUtc, 4, 4, 4, 1.5), dropTile: null);

        var options = new RegionalStellarVisibilityOptions(PhotometricProfile: DefaultMonoProfile());
        var outcome = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            field.Raw, field.MeasurementArtifact, field.Measurement, field.PredictionArtifact, field.Prediction,
            ExposureSeconds: 2.0, Gain: 1.0, Options: options));
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        var product = outcome.Products.Single();

        var parsed = RegionalStellarVisibilityJson.Parse(product.Payload);
        Assert.IsTrue(parsed.IsValid, parsed.Validation.ReasonCode);
        var assessment = parsed.Assessment!;
        Assert.AreEqual(RegionalStellarVisibilityV1.CurrentSchemaVersion, assessment.SchemaVersion);
        Assert.AreEqual(RegionalStellarVisibilityV1.CurrentMethodId, assessment.MethodId);
        Assert.AreNotEqual(CloudAssessmentV1.CurrentSchemaVersion, assessment.SchemaVersion);
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessment.Status);
        Assert.AreEqual(product.ContentIdentitySha256, assessment.AssessmentIdentitySha256);

        var reserialized = RegionalStellarVisibilityJson.Serialize(assessment);
        CollectionAssert.AreEqual(product.Payload.ToArray(), reserialized);

        // Verify deliberate absence of any cloud-cover percentage field or token in serialized JSON.
        var jsonText = Encoding.UTF8.GetString(reserialized);
        Assert.IsFalse(jsonText.Contains("coverage", StringComparison.OrdinalIgnoreCase), jsonText);
        Assert.IsFalse(jsonText.Contains("percent", StringComparison.OrdinalIgnoreCase), jsonText);
        Assert.IsFalse(jsonText.Contains('%', StringComparison.Ordinal), jsonText);

        // Tampered schema, method, status/confidence, and unknown reasons must be rejected.
        Assert.IsFalse(RegionalStellarVisibilityJson.Validate(assessment with { SchemaVersion = "cloud-assessment-v1" }).IsValid);
        Assert.IsFalse(RegionalStellarVisibilityJson.Validate(assessment with { MethodId = "clear-reference-v1" }).IsValid);
        Assert.IsFalse(RegionalStellarVisibilityJson.Validate(assessment with { ConfidenceMillionths = 0 }).IsValid);
        Assert.IsFalse(RegionalStellarVisibilityJson.Validate(assessment with { ReasonCodes = ["unknown.reason"] }).IsValid);
        Assert.IsFalse(RegionalStellarVisibilityJson.Validate(assessment with
        {
            Status = RegionalVisibilityAssessmentStatus.Withheld,
            ConfidenceMillionths = 500_000
        }).IsValid);
        Assert.IsFalse(RegionalStellarVisibilityJson.Parse(Encoding.UTF8.GetBytes("{\"schemaVersion\":\"a\",\"schemaVersion\":\"b\"}")).IsValid);
    }

    [TestMethod]
    public void AssessClearPatchyMasksAndExposureDetectabilityDistinguishDeficitFromInsufficientEvidence()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var stars = new List<SyntheticStar>();
        // Populate 4x4 grid: tiles (1,1), (2,1), (1,2), (2,2) have 4 detectable bright stars each;
        // tile (0,1) has only 1 star (InsufficientEvidence);
        // tile (3,1) is excluded by ExcludedRegions;
        // tile (1,3) is masked by calibration defect mask;
        // tile (2,3) has only faint mag 9.5 marginal stars (excluded from expected detectable count).
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 1, 1, 4, 1.2));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 2, 1, 4, 1.2));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 1, 2, 4, 1.2));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 2, 2, 4, 1.2));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 0, 1, 1, 1.2));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 3, 1, 4, 1.2));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 1, 3, 4, 1.2));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 2, 3, 4, 4.8));

        var excludedRegion = new NormalizedCloudRectangle(750_000, 250_000, 1_000_000, 500_000);
        var defectMask = CreateDefectMask(256, 256, x => x is >= 64 and < 128, y => y is >= 192 and < 256);

        var options = new RegionalStellarVisibilityOptions(
            GridColumns: 4,
            GridRows: 4,
            MinimumExpectedStarsPerTile: 3,
            MinimumMissingStarsForDeficit: 2,
            EdgeMarginPixels: 4.0,
            ExcludedRegions: [excludedRegion],
            PhotometricProfile: DefaultMonoProfile());

        // Clear sky: all detectable stars measured.
        var clearField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null);
        var clear = AssessWith(clearField, options, defectMask: defectMask);

        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, clear.Status);
        Assert.AreEqual(0, clear.Grid.DeficitTileCount);
        Assert.AreEqual(4, clear.Grid.ClearTileCount);
        Assert.AreEqual(RegionalVisibilityTileStatus.Clear, Tile(clear, 1, 1).Status);
        Assert.AreEqual(RegionalVisibilityTileStatus.Clear, Tile(clear, 2, 1).Status);
        Assert.AreEqual(RegionalVisibilityTileStatus.InsufficientEvidence, Tile(clear, 0, 1).Status);
        CollectionAssert.Contains(Tile(clear, 0, 1).ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.TileInsufficientExpectedStars);
        Assert.AreEqual(RegionalVisibilityTileStatus.Excluded, Tile(clear, 3, 1).Status);
        CollectionAssert.Contains(Tile(clear, 3, 1).ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.TileExcludedRegion);
        Assert.AreEqual(RegionalVisibilityTileStatus.Excluded, Tile(clear, 1, 3).Status);
        CollectionAssert.Contains(Tile(clear, 1, 3).ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.TileDefectMasked);
        Assert.AreEqual(RegionalVisibilityTileStatus.InsufficientEvidence, Tile(clear, 2, 3).Status);
        Assert.AreEqual(0, Tile(clear, 2, 3).ExpectedDetectableCount);
        Assert.IsGreaterThanOrEqualTo(3, Tile(clear, 2, 3).MarginalStarCount);

        // Patchy cloud: drop measured detections in tile (2,1) and sparse tile (0,1).
        // Tile (2,1) must become DetectionDeficit, while sparse tile (0,1) stays InsufficientEvidence!
        var patchyField = BuildSyntheticField(rig, NightUtc, stars, dropTile: (col, row) => (col == 2 && row == 1) || (col == 0 && row == 1));
        var patchy = AssessWith(patchyField, options, defectMask: defectMask);

        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, patchy.Status);
        Assert.AreEqual(1, patchy.Grid.DeficitTileCount);
        Assert.AreEqual(3, patchy.Grid.ClearTileCount);
        Assert.AreEqual(RegionalVisibilityTileStatus.DetectionDeficit, Tile(patchy, 2, 1).Status);
        CollectionAssert.Contains(Tile(patchy, 2, 1).ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.TileDetectionDeficit);
        Assert.AreEqual(0, Tile(patchy, 2, 1).MatchedMeasuredCount);
        Assert.AreEqual(4, Tile(patchy, 2, 1).ExpectedDetectableCount);
        Assert.AreEqual(RegionalVisibilityTileStatus.InsufficientEvidence, Tile(patchy, 0, 1).Status);
        Assert.AreEqual(RegionalVisibilityTileStatus.Clear, Tile(patchy, 1, 1).Status);
        Assert.AreEqual(RegionalVisibilityTileStatus.Excluded, Tile(patchy, 3, 1).Status);
        Assert.AreEqual(RegionalVisibilityTileStatus.Excluded, Tile(patchy, 1, 3).Status);
        Assert.IsNotNull(patchy.Mask);

        // Short exposure drops fainter stars below detectability instead of counting them as missing.
        var shortExposureField = BuildSyntheticField(rig, NightUtc, StarsInTile(rig, NightUtc, 4, 4, 1, 1, 4, 5.2), dropTile: (_, _) => true);
        var darkRegionalBg = Enumerable.Repeat(new RegionalVisibilityTileBackgroundV1(true, 100.0, 5.0), 16).ToArray();
        var shortExposure = AssessWith(shortExposureField, options, exposureSeconds: 0.05, regionalBackground: darkRegionalBg);
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.InsufficientEvidence, shortExposure.Status);
        Assert.AreEqual(0, shortExposure.Grid.DeficitTileCount);
        Assert.AreEqual(0, Tile(shortExposure, 1, 1).ExpectedDetectableCount);
    }

    [TestMethod]
    public void AssessHorizonObstructionAndBayerColorResponseExcludeObstructedAndWeightColor()
    {
        var bayerRig = CreateRig(256, 256, CameraPixelFormat.BayerRggb16);
        var stars = CreateGridStars(bayerRig, NightUtc, 4, 4, 4, 1.0);
        var samples = Enumerable.Range(0, 72)
            .Select(index => new HorizonObstructionSample(index * 5.0, index < 36 ? 85.0 : 5.0))
            .ToArray();
        var horizon = new HorizonObstructionProfile(
            HorizonObstructionProfile.CurrentSchemaVersion,
            "horizon-test",
            1,
            "install-1",
            HorizonObstructionProfile.ComputeSiteIdentitySha256(Observatory.LatitudeDegrees, Observatory.LongitudeDegrees, Observatory.ElevationMeters),
            HorizonObstructionProfile.ComputeGeometryIdentitySha256(bayerRig),
            HorizonObstructionSource.Synthetic,
            15.0,
            samples);
        var evaluator = new HorizonObstructionEvaluator(horizon, bayerRig, Observatory);

        var field = BuildSyntheticField(bayerRig, NightUtc, stars, dropTile: null);
        var options = new RegionalStellarVisibilityOptions(
            EdgeMarginPixels: 4.0,
            PhotometricProfile: DefaultBayerProfile());
        var assessed = AssessWith(field, options, horizonEvaluator: evaluator);

        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessed.Status);
        Assert.AreEqual(CameraPixelFormat.BayerRggb16, assessed.Conditions.PixelFormat);
        Assert.AreEqual(horizon.ComputeIdentitySha256(), assessed.Conditions.HorizonProfileIdentitySha256);
        Assert.IsGreaterThan(0, assessed.Grid.ExcludedTileCount);
        Assert.IsTrue(assessed.Tiles.Any(t => t.ReasonCodes.Contains(RegionalVisibilityReasonCodes.TileHorizonObstructed)));
        Assert.AreEqual(0, assessed.Grid.DeficitTileCount);
        Assert.IsGreaterThan(0, assessed.Grid.ClearTileCount);
    }

    [TestMethod]
    public void AssessAcceptedSolveAndUsableFocusStrictlyImproveConfidence()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var stars = CreateGridStars(rig, NightUtc, 4, 4, 4, 1.0);
        var options = new RegionalStellarVisibilityOptions(EdgeMarginPixels: 4.0, PhotometricProfile: DefaultMonoProfile());

        // (a) Fixed geometry + unavailable focus
        var noFocusField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null, hfdMedian: null, validHfdCount: 0);
        var fixedNoFocus = AssessWith(noFocusField, options);

        // (b) Fixed geometry + usable HFD focus
        var goodFocusField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null, hfdMedian: 2.4, validHfdCount: 16);
        var fixedWithFocus = AssessWith(goodFocusField, options);

        // (c) Accepted measured astrometry solve + usable HFD focus
        var (astroArtifact, astroSolution, mapping) = BuildAcceptedAstrometrySolution(
            rig, NightUtc, goodFocusField.Raw, goodFocusField.Prediction, rollOffsetDegrees: 0.0);
        var solvedWithFocus = AssessWith(goodFocusField, options,
            astrometryArtifact: astroArtifact, astrometrySolution: astroSolution, acceptedMapping: mapping);

        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, fixedNoFocus.Status);
        Assert.AreEqual(RegionalVisibilityGeometryBasis.FixedGeometry, fixedNoFocus.GeometryBasis);
        Assert.AreEqual(RegionalVisibilityFocusBasis.Unavailable, fixedNoFocus.FocusBasis);

        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, fixedWithFocus.Status);
        Assert.AreEqual(RegionalVisibilityGeometryBasis.FixedGeometry, fixedWithFocus.GeometryBasis);
        Assert.AreEqual(RegionalVisibilityFocusBasis.Usable, fixedWithFocus.FocusBasis);

        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, solvedWithFocus.Status);
        Assert.AreEqual(RegionalVisibilityGeometryBasis.AcceptedAstrometrySolve, solvedWithFocus.GeometryBasis);
        Assert.AreEqual(RegionalVisibilityFocusBasis.Usable, solvedWithFocus.FocusBasis);
        Assert.AreEqual(astroArtifact.ArtifactId, solvedWithFocus.Sources.AstrometryArtifactId);
        Assert.IsNull(fixedWithFocus.Sources.AstrometryArtifactId);

        Assert.IsGreaterThan(fixedNoFocus.ConfidenceMillionths, fixedWithFocus.ConfidenceMillionths);
        Assert.IsGreaterThan(fixedWithFocus.ConfidenceMillionths, solvedWithFocus.ConfidenceMillionths);
    }

    [TestMethod]
    public void AssessHalfDegreeRollIn1280x960ClearFieldWithholdsUnderFixedGeometryAndAssessesWithAcceptedSolve()
    {
        // Exact #1925 edge case: 1280x960 clear field with 17 measured stars rotated 0.5 deg around center.
        // Inner ~13 stars stay within the 3.5 px match radius while outer ~4 stars shift ~4.5-5.5 px away.
        var rig = CreateRig(1280, 960, CameraPixelFormat.Mono16, opticalRadiusPixels: 800, focalLengthMm: 2.8, pixelPitchUm: 2.4);
        var projection = RigProjectionContextFactory.CreateNative(rig);
        var projector = ProjectorFactory.Create(projection);
        var cx = projection.PrincipalPointX;
        var cy = projection.PrincipalPointY;
        const double rollDegrees = 0.5;
        var rollRad = rollDegrees * Math.PI / 180.0;
        var cos = Math.Cos(rollRad);
        var sin = Math.Sin(rollRad);

        (double Dx, double Dy)[] offsets =
        [
            (-220, -180), (-160, -140), (-90, -200), (110, -180), (190, -150),
            (-240, 40), (-130, 80), (40, -60), (150, 50), (250, 90),
            (-180, 210), (80, 200), (210, 190),
            (-540, -340), (540, -340), (-540, 340), (540, 340)
        ];

        var predictedStars = new List<SyntheticStar>();
        var measuredPixels = new List<PixelPoint>();
        for (var i = 0; i < offsets.Length; i++)
        {
            var (dx, dy) = offsets[i];
            var px = cx + dx;
            var py = cy + dy;
            var dir = projector.Unproject(new(px, py))!.Value;
            var eq = CoordinateTransforms.HorizontalToEquatorial(dir, NightUtc, Observatory.LatitudeDegrees, Observatory.LongitudeDegrees);
            predictedStars.Add(new($"roll-star-{i}", eq, dir, new(px, py), 1.0, 0.65));
            measuredPixels.Add(new(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos));
        }

        var field = BuildSyntheticFieldFromExplicitDetections(rig, NightUtc, predictedStars, measuredPixels, hfdMedian: 2.3, validHfdCount: 17);
        var options = new RegionalStellarVisibilityOptions(
            GridColumns: 4,
            GridRows: 4,
            MinimumExpectedStarsPerTile: 1,
            MinimumMissingStarsForDeficit: 1,
            MatchRadiusPixels: 3.5,
            EdgeMarginPixels: 10.0,
            PhotometricProfile: DefaultMonoProfile());

        // Without solve: must detect roll / pointing uncertainty and WITHHOLD (zero false cloud deficits!).
        var fixedResult = AssessWith(field, options);
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Withheld, fixedResult.Status);
        CollectionAssert.Contains(fixedResult.ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.PointingUncertain);
        Assert.AreEqual(0, fixedResult.Grid.DeficitTileCount);
        Assert.IsNotNull(fixedResult.Conditions.EstimatedRollDegrees);
        Assert.IsGreaterThan(0.3, Math.Abs(fixedResult.Conditions.EstimatedRollDegrees.Value));

        // With an accepted astrometry solve whose orientation includes the 0.5 deg roll: assesses cleanly with 0 deficits!
        var (astroArtifact, astroSolution, mapping) = BuildAcceptedAstrometrySolution(
            rig, NightUtc, field.Raw, field.Prediction, rollOffsetDegrees: rollDegrees);
        var solvedResult = AssessWith(field, options,
            astrometryArtifact: astroArtifact, astrometrySolution: astroSolution, acceptedMapping: mapping);
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, solvedResult.Status);
        Assert.AreEqual(RegionalVisibilityGeometryBasis.AcceptedAstrometrySolve, solvedResult.GeometryBasis);
        Assert.AreEqual(0, solvedResult.Grid.DeficitTileCount);
        Assert.IsGreaterThan(0, solvedResult.Grid.ClearTileCount);
    }

    [TestMethod]
    public void AssessConfoundersAndInvalidInputsWithholdCleanlyWithZeroDeficitTiles()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var stars = CreateGridStars(rig, NightUtc, 4, 4, 4, 1.0);
        var options = new RegionalStellarVisibilityOptions(EdgeMarginPixels: 4.0, PhotometricProfile: DefaultMonoProfile());

        // 1. Simulator truth (VirtualRenderAuthoritative) must be rejected.
        var simField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null, sceneKind: ProjectedSceneKind.VirtualRenderAuthoritative);
        AssertWithheld(AssessWith(simField, options), RegionalVisibilityReasonCodes.SimulatorTruthRejected);

        // 2. Unsupported pixel format (Rgb24).
        var rgbRig = CreateRig(256, 256, CameraPixelFormat.Rgb24);
        var rgbField = BuildSyntheticField(rgbRig, NightUtc, stars, dropTile: null);
        AssertWithheld(AssessWith(rgbField, options), RegionalVisibilityReasonCodes.UnsupportedFormat);

        // 3. Missing linear calibration.
        var uncalibratedField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null, calibrationDisposition: ExposureCalibrationDisposition.Missing);
        AssertWithheld(AssessWith(uncalibratedField, options), RegionalVisibilityReasonCodes.MissingLinearCalibration);

        // 4. Missing sensor response profile.
        var normalField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null);
        AssertWithheld(AssessWith(normalField, options with { PhotometricProfile = null }), RegionalVisibilityReasonCodes.MissingSensorResponse);

        // 5. Poor focus (high median HFD).
        var highHfdField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null, hfdMedian: 9.5, validHfdCount: 10);
        AssertWithheld(AssessWith(highHfdField, options), RegionalVisibilityReasonCodes.PoorFocus);

        // 6. Poor focus (excessive too-broad exclusions even with no valid HFD).
        var tooBroadField = BuildSyntheticField(rig, NightUtc, stars, dropTile: (_, _) => true, hfdMedian: null, validHfdCount: 0, tooBroadExclusions: 5);
        AssertWithheld(AssessWith(tooBroadField, options), RegionalVisibilityReasonCodes.PoorFocus);

        // 7. Daylight and Twilight.
        var dayField = BuildSyntheticField(rig, DayUtc, CreateGridStars(rig, DayUtc, 4, 4, 4, 1.0), dropTile: null);
        AssertWithheld(AssessWith(dayField, options), RegionalVisibilityReasonCodes.Daylight);

        var twilightField = BuildSyntheticField(rig, TwilightUtc, CreateGridStars(rig, TwilightUtc, 4, 4, 4, 1.0), dropTile: null);
        AssertWithheld(AssessWith(twilightField, options), RegionalVisibilityReasonCodes.Twilight);

        // 8. Bright moonlight.
        var moonField = BuildSyntheticField(rig, BrightMoonUtc, CreateGridStars(rig, BrightMoonUtc, 4, 4, 4, 1.0), dropTile: null);
        AssertWithheld(AssessWith(moonField, options), RegionalVisibilityReasonCodes.MoonlightConfounded);

        // 9. Precipitation contamination.
        var precipEnv = new CloudAssessmentEnvironmentV1(
            CloudAssessmentEnvironmentV1.CurrentSchemaVersion,
            CaptureSolarRegime.Night,
            EnvironmentalObservationMatchStatus.Fresh,
            Guid.NewGuid(),
            new string('F', 64),
            PrecipitationDetected: true);
        AssertWithheld(AssessWith(normalField, options, environment: precipEnv), RegionalVisibilityReasonCodes.PrecipitationContaminated);

        // 10. High saturation fraction.
        var saturatedField = BuildSyntheticField(rig, NightUtc, stars, dropTile: null, clippedPixelCount: 20_000);
        AssertWithheld(AssessWith(saturatedField, options), RegionalVisibilityReasonCodes.SaturationContaminated);
    }

    [TestMethod]
    public void OptionDescriptorAndPresentationLayerRoundTripAndOmitCloudCoverPercentage()
    {
        var resolved = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(
            RegionalStellarVisibilityProducts.StepAlias,
            RegionalStellarVisibilityProducts.ImplementationVersion);
        var descriptor = resolved.Descriptor!;
        Assert.AreSame(RegionalStellarVisibilityOptionDescriptors.Descriptor, descriptor);

        var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
        Assert.IsTrue(defaults.IsValid);
        Assert.AreEqual(4L, defaults.Get("gridColumns").Value.AsWholeNumber());
        Assert.AreEqual(4L, defaults.Get("gridRows").Value.AsWholeNumber());

        var encoded = ProcessingNodeOptionsCodec.Encode(
            descriptor,
            JsonSerializer.SerializeToElement(new { futureField = 42 }),
            new Dictionary<string, ProcessingOptionValue>
            {
                ["gridColumns"] = ProcessingOptionValue.FromWholeNumber(6),
                ["minimumExpectedStarsPerTile"] = ProcessingOptionValue.FromWholeNumber(2)
            });
        Assert.IsTrue(encoded.IsValid);
        Assert.AreEqual(42, encoded.Options!.Value.GetProperty("futureField").GetInt32());
        Assert.AreEqual(6, encoded.Options!.Value.GetProperty("gridColumns").GetInt32());

        var invalid = ProcessingNodeOptionsCodec.Decode(
            descriptor,
            JsonSerializer.SerializeToElement(new { gridColumns = 99 }));
        Assert.IsFalse(invalid.IsValid);

        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var field = BuildSyntheticField(rig, NightUtc, CreateGridStars(rig, NightUtc, 4, 4, 4, 1.0), dropTile: (col, row) => col == 1 && row == 1);
        var assessed = AssessWith(field, new RegionalStellarVisibilityOptions(EdgeMarginPixels: 4.0, PhotometricProfile: DefaultMonoProfile()));
        var payload = PresentationLayerProducers.FromRegionalStellarVisibility(assessed, 256, 256);
        Assert.IsNotNull(payload.TileMask);
        Assert.IsNotEmpty(payload.TextBlocks);
        foreach (var line in payload.TextBlocks.SelectMany(static block => block.Lines))
        {
            Assert.IsFalse(line.Contains('%', StringComparison.Ordinal), line);
            Assert.IsFalse(line.Contains("percent", StringComparison.OrdinalIgnoreCase), line);
        }
    }

    [TestMethod]
    public void AssessReferenceCalibratedRequiresVerifiedDefectMaskAndWithholdsWhenMissingOrCorrupt()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var stars = CreateGridStars(rig, NightUtc, 4, 4, 4, 1.2);
        var field = BuildSyntheticField(rig, NightUtc, stars, dropTile: null,
            calibrationDisposition: ExposureCalibrationDisposition.ReferenceCalibrated);
        var options = new RegionalStellarVisibilityOptions(EdgeMarginPixels: 4.0, PhotometricProfile: DefaultMonoProfile());

        // Reference-calibrated without defect mask must withhold cleanly.
        AssertWithheld(AssessWith(field, options, defectMask: null), RegionalVisibilityReasonCodes.DefectMaskUnavailable);

        // Reference-calibrated with tampered digest must withhold cleanly.
        var validMask = CreateDefectMask(256, 256, x => x is >= 64 and < 128, y => y is >= 192 and < 256);
        var tamperedMask = validMask with { PayloadSha256 = new string('0', 64) };
        AssertWithheld(AssessWith(field, options, defectMask: tamperedMask), RegionalVisibilityReasonCodes.DefectMaskUnavailable);

        // Reference-calibrated with verified defect mask assesses and masks the covered tile.
        var assessed = AssessWith(field, options, defectMask: validMask);
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessed.Status);
        Assert.AreEqual(validMask.ArtifactId, assessed.Sources.DefectMaskArtifactId);
        Assert.AreEqual(validMask.PayloadSha256, assessed.Sources.DefectMaskSha256);
        Assert.AreEqual(RegionalVisibilityTileStatus.Excluded, Tile(assessed, 1, 3).Status);
        CollectionAssert.Contains(Tile(assessed, 1, 3).ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.TileDefectMasked);
        Assert.AreEqual(0, assessed.Grid.DeficitTileCount);
    }

    [TestMethod]
    public void AssessClearFieldWithScaleMismatchWithholdsUnderFixedGeometryWithoutFalseDeficit()
    {
        // H2 regression: 5.5% radial scale mismatch leaves 8 central anchors with ~0 estimated translation/roll
        // while outer stars shift radially beyond the match radius. Fixed geometry must withhold, never report 0/4 deficit.
        var rig = CreateRig(1280, 960, CameraPixelFormat.Mono16, opticalRadiusPixels: 800, focalLengthMm: 2.8, pixelPitchUm: 2.4);
        var projection = RigProjectionContextFactory.CreateNative(rig);
        var projector = ProjectorFactory.Create(projection);
        var cx = projection.PrincipalPointX;
        var cy = projection.PrincipalPointY;
        const double scaleFactor = 1.055;

        (double Dx, double Dy)[] offsets =
        [
            // 8 symmetric central anchors (r ~ 45-55 px -> 5.5% shift is ~2.5-3.0 px <= 3.5 px match radius)
            (-45, 0), (45, 0), (0, -45), (0, 45),
            (-35, -35), (35, -35), (-35, 35), (35, 35),
            // 4 outer stars in one outer tile (r ~ 460-520 px -> 5.5% shift is ~25-29 px > OffsetSearchRadiusPixels=20!)
            (380, 260), (430, 260), (380, 310), (430, 310)
        ];

        var predictedStars = new List<SyntheticStar>();
        var measuredPixels = new List<PixelPoint>();
        for (var i = 0; i < offsets.Length; i++)
        {
            var (dx, dy) = offsets[i];
            var px = cx + dx;
            var py = cy + dy;
            var dir = projector.Unproject(new(px, py))!.Value;
            var eq = CoordinateTransforms.HorizontalToEquatorial(dir, NightUtc, Observatory.LatitudeDegrees, Observatory.LongitudeDegrees);
            predictedStars.Add(new($"scale-star-{i}", eq, dir, new(px, py), 1.0, 0.65));
            measuredPixels.Add(new(cx + dx * scaleFactor, cy + dy * scaleFactor));
        }

        var field = BuildSyntheticFieldFromExplicitDetections(rig, NightUtc, predictedStars, measuredPixels, hfdMedian: 2.3, validHfdCount: 12);
        var options = new RegionalStellarVisibilityOptions(
            GridColumns: 4,
            GridRows: 4,
            MinimumExpectedStarsPerTile: 3,
            MinimumMissingStarsForDeficit: 2,
            MatchRadiusPixels: 3.5,
            EdgeMarginPixels: 10.0,
            PhotometricProfile: DefaultMonoProfile());

        var result = AssessWith(field, options);
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Withheld, result.Status, string.Join(',', result.ReasonCodes));
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), RegionalVisibilityReasonCodes.PointingUncertain);
        Assert.AreEqual(0, result.Grid.DeficitTileCount);
        Assert.AreEqual(0, result.Grid.AssessedTileCount);
        Assert.IsTrue(Math.Abs(result.Conditions.EstimatedShiftPixels ?? 0) < 0.5);
        Assert.IsTrue(Math.Abs(result.Conditions.EstimatedRollDegrees ?? 0) < 0.05);
    }

    [TestMethod]
    public void AssessLocalRegionalNoiseOverridesFrameEstimateAndWithholdsWhenUnavailable()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var stars = new List<SyntheticStar>();
        // 3 quiet tiles (1,1), (1,2), (2,2) with 4 stars each at mag 1.5;
        // 1 noisy tile (2,1) with 4 stars at mag 2.2 (1 detected with local noise 250 ADU, 3 drowned by noise).
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 1, 1, 4, 1.5));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 1, 2, 4, 1.5));
        stars.AddRange(StarsInTile(rig, NightUtc, 4, 4, 2, 2, 4, 1.5));
        var noisyTileStars = StarsInTile(rig, NightUtc, 4, 4, 2, 1, 4, 2.2);
        stars.AddRange(noisyTileStars);

        var noisyDetections = new List<StellarDetection>();
        var idx = 0;
        foreach (var star in stars)
        {
            var col = Math.Clamp((int)Math.Floor(star.Pixel.X * 4 / rig.Sensor.WidthPixels), 0, 3);
            var row = Math.Clamp((int)Math.Floor(star.Pixel.Y * 4 / rig.Sensor.HeightPixels), 0, 3);
            if (col == 2 && row == 1)
            {
                if (ReferenceEquals(star, noisyTileStars[0]))
                {
                    noisyDetections.Add(new StellarDetection(idx++, star.Pixel, 1500, 1200, 1.2, 1.2, 500.0, 250.0));
                }
                continue;
            }
            noisyDetections.Add(new StellarDetection(idx++, star.Pixel, 1500, 1200, 1.2, 1.2, 100.0, 5.0));
        }

        var noisyField = BuildSyntheticFieldFromCustomDetections(rig, NightUtc, stars, noisyDetections);
        var options = new RegionalStellarVisibilityOptions(
            GridColumns: 4,
            GridRows: 4,
            MinimumExpectedStarsPerTile: 3,
            MinimumMissingStarsForDeficit: 2,
            EdgeMarginPixels: 4.0,
            PhotometricProfile: DefaultMonoProfile());

        var assessed = AssessWith(noisyField, options);
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessed.Status);
        Assert.AreEqual(0, assessed.Grid.DeficitTileCount);
        Assert.AreEqual(3, assessed.Grid.ClearTileCount);
        Assert.AreEqual(RegionalVisibilityTileStatus.InsufficientEvidence, Tile(assessed, 2, 1).Status);

        // Zero detections and no regional background evidence must withhold with BackgroundNoiseUnavailable.
        var emptyField = BuildSyntheticField(rig, NightUtc, stars, dropTile: (_, _) => true);
        AssertWithheld(AssessWith(emptyField, options), RegionalVisibilityReasonCodes.BackgroundNoiseUnavailable);
    }

    [TestMethod]
    public void AssessIncompatibleAndProvisionalSensorResponseWithholds()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var field = BuildSyntheticField(rig, NightUtc, CreateGridStars(rig, NightUtc, 4, 4, 4, 1.2), dropTile: null);
        var baseOptions = new RegionalStellarVisibilityOptions(EdgeMarginPixels: 4.0);

        // Provisional 16-bit response with black level 512 on a 12-bit exposure with black level 64.
        var provisional16Bit = new ConfiguredSensorResponseProfile(
            ModelVersion: "provisional-16bit-v1",
            AdcBitDepth: 16,
            MinimumGainControl: 0,
            MaximumGainControl: 500,
            ElectronsPerAduAtZeroGain: 1.0,
            GainControlDivisor: 100.0,
            MaximumFullWellElectrons: 50_000,
            ReadNoisePoints: [new SensorReadNoisePoint(0, 2.0), new SensorReadNoisePoint(500, 2.0)],
            BlackLevelAdu: 512,
            GainUnits: "tenth-db",
            CompatibilityLabel: "provisional-16bit-v1",
            CalibrationStatus: "provisional-published-envelope");
        AssertWithheld(
            AssessWith(field, baseOptions with { PhotometricProfile = ProfileFor(provisional16Bit) }),
            RegionalVisibilityReasonCodes.ResponseBasisMismatch);

        // Qualified 12-bit profile with wrong black level (512 vs 64) must withhold.
        var wrongBlack = provisional16Bit with { AdcBitDepth = 12, CalibrationStatus = null };
        AssertWithheld(
            AssessWith(field, baseOptions with { PhotometricProfile = ProfileFor(wrongBlack) }),
            RegionalVisibilityReasonCodes.ResponseBasisMismatch);

        // Qualified 16-bit profile with matching black level 64 still mismatches 12-bit ADC depth.
        var wrongBits = provisional16Bit with { BlackLevelAdu = 64, CalibrationStatus = null };
        AssertWithheld(
            AssessWith(field, baseOptions with { PhotometricProfile = ProfileFor(wrongBits) }),
            RegionalVisibilityReasonCodes.ResponseBasisMismatch);

        // Provisional status alone on otherwise matching 12-bit / black 64 response must withhold.
        var provisionalOnly = provisional16Bit with { AdcBitDepth = 12, BlackLevelAdu = 64 };
        AssertWithheld(
            AssessWith(field, baseOptions with { PhotometricProfile = ProfileFor(provisionalOnly) }),
            RegionalVisibilityReasonCodes.ResponseBasisMismatch);

        // Qualified 12-bit / black 64 response succeeds.
        var matching = provisional16Bit with { AdcBitDepth = 12, BlackLevelAdu = 64, CalibrationStatus = null };
        var ok = AssessWith(field, baseOptions with { PhotometricProfile = ProfileFor(matching) });
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, ok.Status);

        static RegionalVisibilityPhotometricProfileV1 ProfileFor(ConfiguredSensorResponseProfile response) => new(
            ProfileName: response.CompatibilityLabel,
            MagnitudeZeroElectronsPerSecond: 14_000,
            NominalPsfSigmaPixels: 1.2,
            DarkCurrentElectronsPerSecond: 0.05,
            VignettingStrength: 0.0,
            SensorResponse: response,
            CalibrationStatus: response.CalibrationStatus);
    }

    [TestMethod]
    public void AssessVerifiesInputProvenanceAndBindsEveryEffectiveInputIntoIdentity()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var stars = CreateGridStars(rig, NightUtc, 4, 4, 4, 1.2);
        var fieldA = BuildSyntheticField(rig, NightUtc, stars, dropTile: null);
        var fieldB = BuildSyntheticField(rig, NightUtc, stars, dropTile: null);
        var options = new RegionalStellarVisibilityOptions(EdgeMarginPixels: 4.0, PhotometricProfile: DefaultMonoProfile());

        // Cross-wired measurement from another capture must be withheld.
        var mismatchedMeasOutcome = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            fieldA.Raw, fieldB.MeasurementArtifact, fieldB.Measurement, fieldA.PredictionArtifact, fieldA.Prediction,
            2.0, 1.0, options));
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, mismatchedMeasOutcome.Status);
        AssertWithheld(RegionalStellarVisibilityProducts.Read(mismatchedMeasOutcome.Products.Single()),
            RegionalVisibilityReasonCodes.MeasurementUnavailable);

        // Cross-wired prediction from another capture must be withheld.
        var mismatchedPredOutcome = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            fieldA.Raw, fieldA.MeasurementArtifact, fieldA.Measurement, fieldB.PredictionArtifact, fieldB.Prediction,
            2.0, 1.0, options));
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, mismatchedPredOutcome.Status);
        AssertWithheld(RegionalStellarVisibilityProducts.Read(mismatchedPredOutcome.Products.Single()),
            RegionalVisibilityReasonCodes.PredictionUnavailable);

        // Distinct effective inputs (solar override, twilight regime with null InputIdentitySha256, moon override, regional bg)
        // must never share a recipe or output identity.
        var nightProduct = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            fieldA.Raw, fieldA.MeasurementArtifact, fieldA.Measurement, fieldA.PredictionArtifact, fieldA.Prediction,
            2.0, 1.0, options, SunAltitudeDegreesOverride: -25.0)).Products.Single();
        var twilightOverrideProduct = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            fieldA.Raw, fieldA.MeasurementArtifact, fieldA.Measurement, fieldA.PredictionArtifact, fieldA.Prediction,
            2.0, 1.0, options, SunAltitudeDegreesOverride: -10.0)).Products.Single();
        var twilightEnvProduct = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            fieldA.Raw, fieldA.MeasurementArtifact, fieldA.Measurement, fieldA.PredictionArtifact, fieldA.Prediction,
            2.0, 1.0, options,
            Environment: new CloudAssessmentEnvironmentV1(
                CloudAssessmentEnvironmentV1.CurrentSchemaVersion, CaptureSolarRegime.Twilight,
                EnvironmentalObservationMatchStatus.Fresh, null, null, false))).Products.Single();
        var moonOverrideProduct = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            fieldA.Raw, fieldA.MeasurementArtifact, fieldA.Measurement, fieldA.PredictionArtifact, fieldA.Prediction,
            2.0, 1.0, options,
            MoonOverride: new SolarDiskAppearance(
                SolarSystemBody.Moon, NightUtc, new AltAzPoint(45, 180), 0.25, -12.0, 0.95, 0, 384_400))).Products.Single();
        var bgOverrideProduct = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            fieldA.Raw, fieldA.MeasurementArtifact, fieldA.Measurement, fieldA.PredictionArtifact, fieldA.Prediction,
            2.0, 1.0, options,
            RegionalBackground: Enumerable.Repeat(new RegionalVisibilityTileBackgroundV1(true, 110.0, 6.0), 16).ToArray())).Products.Single();

        var outputs = new[] { nightProduct, twilightOverrideProduct, twilightEnvProduct, moonOverrideProduct, bgOverrideProduct };
        Assert.AreEqual(outputs.Length, outputs.Select(static p => p.Recipe.IdentitySha256).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(outputs.Length, outputs.Select(static p => p.OutputIdentitySha256).Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    public void AssessIncludeMaskFalseDerivesOverlayBitsAndRejectsGridExceedingImageDimensions()
    {
        var rig = CreateRig(256, 256, CameraPixelFormat.Mono16);
        var field = BuildSyntheticField(rig, NightUtc, CreateGridStars(rig, NightUtc, 4, 4, 4, 1.0),
            dropTile: (col, row) => col == 1 && row == 1);
        var assessedNoMask = AssessWith(field, new RegionalStellarVisibilityOptions(
            EdgeMarginPixels: 4.0, IncludeMask: false, PhotometricProfile: DefaultMonoProfile()));

        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Assessed, assessedNoMask.Status);
        Assert.IsNull(assessedNoMask.Mask);
        Assert.AreEqual(1, assessedNoMask.Grid.DeficitTileCount);

        var overlay = PresentationLayerProducers.FromRegionalStellarVisibility(assessedNoMask, 256, 256);
        Assert.IsNotNull(overlay.TileMask);
        var expectedIndex = 1 * 4 + 1;
        var bits = overlay.TileMask.Bits.Span;
        Assert.AreNotEqual(0, bits[expectedIndex >> 3] & (1 << (expectedIndex & 7)));

        // 32-column grid on a 16-pixel-wide ROI must fail cleanly rather than producing zero-width tiles or throwing.
        var tinyRig = CreateRig(16, 16, CameraPixelFormat.Mono16);
        var tinyField = BuildSyntheticField(tinyRig, NightUtc, [], dropTile: null);
        var invalidGridOutcome = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            tinyField.Raw, tinyField.MeasurementArtifact, tinyField.Measurement, tinyField.PredictionArtifact, tinyField.Prediction,
            2.0, 1.0, new RegionalStellarVisibilityOptions(GridColumns: 32, GridRows: 4, PhotometricProfile: DefaultMonoProfile())));
        Assert.AreEqual(ProcessingOutcomeStatus.TerminalFailure, invalidGridOutcome.Status);
        Assert.AreEqual("visibility.invalid-grid-dimensions", invalidGridOutcome.ReasonCode);
    }

    private static RegionalStellarVisibilityV1 AssessWith(
        SyntheticField field,
        RegionalStellarVisibilityOptions options,
        double exposureSeconds = 2.0,
        double gain = 1.0,
        ProcessingArtifact? astrometryArtifact = null,
        MeasuredAstrometrySolutionV1? astrometrySolution = null,
        AstrometricMapping? acceptedMapping = null,
        HorizonObstructionEvaluator? horizonEvaluator = null,
        CloudAssessmentEnvironmentV1? environment = null,
        RegionalVisibilityDefectMaskInput? defectMask = null,
        IReadOnlyList<RegionalVisibilityTileBackgroundV1>? regionalBackground = null)
    {
        var outcome = RegionalStellarVisibilityProducts.Assess(new RegionalStellarVisibilityRequest(
            field.Raw,
            field.MeasurementArtifact,
            field.Measurement,
            field.PredictionArtifact,
            field.Prediction,
            exposureSeconds,
            gain,
            options,
            AstrometryArtifact: astrometryArtifact,
            AstrometrySolution: astrometrySolution,
            AcceptedMapping: acceptedMapping,
            HorizonEvaluator: horizonEvaluator,
            Environment: environment,
            DefectMask: defectMask,
            RegionalBackground: regionalBackground));
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status);
        return RegionalStellarVisibilityProducts.Read(outcome.Products.Single());
    }

    private static void AssertWithheld(RegionalStellarVisibilityV1 result, string expectedReason)
    {
        Assert.AreEqual(RegionalVisibilityAssessmentStatus.Withheld, result.Status, $"Expected Withheld/{expectedReason} but got {result.Status}: {string.Join(',', result.ReasonCodes)}");
        CollectionAssert.Contains(result.ReasonCodes.ToArray(), expectedReason);
        Assert.AreEqual(0, result.Grid.DeficitTileCount);
        Assert.AreEqual(0, result.Grid.AssessedTileCount);
    }

    private static RegionalVisibilityTileV1 Tile(RegionalStellarVisibilityV1 assessment, int col, int row)
        => assessment.Tiles.Single(tile => tile.Column == col && tile.Row == row);

    private static CameraRigConfig CreateRig(
        int width,
        int height,
        CameraPixelFormat format,
        double? opticalRadiusPixels = null,
        double focalLengthMm = 1.5,
        double pixelPitchUm = 2.4)
    {
        var radius = opticalRadiusPixels ?? Math.Min(width, height) * 0.48;
        return new CameraRigConfig(
            new SensorProfile(
                "test-sensor", width, height, pixelPitchUm,
                format == CameraPixelFormat.Mono16 ? SensorColorMode.Mono : SensorColorMode.Color,
                format,
                format switch
                {
                    CameraPixelFormat.Mono16 => SensorResponseMode.Monochrome,
                    CameraPixelFormat.BayerRggb16 => SensorResponseMode.BayerRaw,
                    _ => SensorResponseMode.RenderedRgb
                }),
            new OpticsProfile(
                ProjectionModel.EquidistantFisheye.ToString(),
                focalLengthMm,
                180.0,
                0.0,
                LensKind.Fisheye,
                width / 2.0,
                height / 2.0,
                radius),
            new RigOrientation(90, 0, 0),
            new PipelineExposureProfile(
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromSeconds(2),
                1,
                1));
    }

    private static RegionalVisibilityPhotometricProfileV1 DefaultMonoProfile() => new(
        ProfileName: "test-mono-v1",
        MagnitudeZeroElectronsPerSecond: 14_000,
        NominalPsfSigmaPixels: 1.2,
        DarkCurrentElectronsPerSecond: 0.05,
        VignettingStrength: 0.0,
        ElectronsPerAduAtZeroGain: 1.0,
        GainControlDivisor: null,
        ReadNoiseElectrons: 2.0,
        FullWellElectrons: 20_000);

    private static RegionalVisibilityPhotometricProfileV1 DefaultBayerProfile() => new(
        ProfileName: "test-bayer-v1",
        MagnitudeZeroElectronsPerSecond: 14_000,
        NominalPsfSigmaPixels: 1.2,
        DarkCurrentElectronsPerSecond: 0.05,
        VignettingStrength: 0.0,
        ElectronsPerAduAtZeroGain: 1.0,
        GainControlDivisor: null,
        ReadNoiseElectrons: 2.0,
        FullWellElectrons: 20_000,
        ChannelResponseRed: 0.85,
        ChannelResponseGreen: 1.0,
        ChannelResponseBlue: 0.78);

    private static RegionalVisibilityDefectMaskInput CreateDefectMask(
        int width, int height, Func<int, bool> matchX, Func<int, bool> matchY)
    {
        var bytes = new byte[width * height * sizeof(ushort)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (matchX(x) && matchY(y))
                {
                    var offset = (y * width + x) * sizeof(ushort);
                    bytes[offset] = 0xFF;
                    bytes[offset + 1] = 0xFF;
                }
            }
        }

        var sha = PayloadChecksum.ComputeSha256(bytes);
        return new RegionalVisibilityDefectMaskInput(
            Guid.NewGuid(),
            sha,
            new Linear16Frame(width, height, width * sizeof(ushort), CameraPixelFormat.Mono16, bytes));
    }

    private static List<SyntheticStar> CreateGridStars(
        CameraRigConfig rig, DateTimeOffset utc, int cols, int rows, int starsPerTile, double magnitude)
    {
        var result = new List<SyntheticStar>();
        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < cols; col++)
            {
                result.AddRange(StarsInTile(rig, utc, cols, rows, col, row, starsPerTile, magnitude));
            }
        }
        return result;
    }

    private static List<SyntheticStar> StarsInTile(
        CameraRigConfig rig, DateTimeOffset utc, int cols, int rows, int col, int row, int count, double magnitude)
    {
        var projection = RigProjectionContextFactory.CreateNative(rig);
        var projector = ProjectorFactory.Create(projection);
        var radius = projection.ImageCircleRadiusPixels ?? Math.Min(rig.Sensor.WidthPixels, rig.Sensor.HeightPixels) * 0.48;
        var left = (double)col * rig.Sensor.WidthPixels / cols;
        var right = (double)(col + 1) * rig.Sensor.WidthPixels / cols;
        var top = (double)row * rig.Sensor.HeightPixels / rows;
        var bottom = (double)(row + 1) * rig.Sensor.HeightPixels / rows;
        var tileW = right - left;
        var tileH = bottom - top;
        var stars = new List<SyntheticStar>();
        var subCols = (int)Math.Ceiling(Math.Sqrt(count));
        var subRows = (int)Math.Ceiling((double)count / subCols);
        for (var i = 0; i < count; i++)
        {
            var sc = i % subCols;
            var sr = i / subCols;
            var x = left + (sc + 0.5) * tileW / subCols;
            var y = top + (sr + 0.5) * tileH / subRows;
            var dx = x - projection.PrincipalPointX;
            var dy = y - projection.PrincipalPointY;
            if (Math.Sqrt(dx * dx + dy * dy) >= radius - 4)
            {
                continue;
            }

            var horizontal = projector.Unproject(new(x, y));
            if (horizontal is null || horizontal.Value.AltitudeDegrees < 5)
            {
                continue;
            }

            var eq = CoordinateTransforms.HorizontalToEquatorial(horizontal.Value, utc, Observatory.LatitudeDegrees, Observatory.LongitudeDegrees);
            stars.Add(new($"star-{col}-{row}-{i}", eq, horizontal.Value, new(x, y), magnitude, 0.65));
        }
        return stars;
    }

    private static SyntheticField BuildSyntheticField(
        CameraRigConfig rig,
        DateTimeOffset utc,
        IReadOnlyList<SyntheticStar> stars,
        Func<int, int, bool>? dropTile,
        double? hfdMedian = 2.4,
        int validHfdCount = 12,
        int tooBroadExclusions = 0,
        long clippedPixelCount = 0,
        ProjectedSceneKind sceneKind = ProjectedSceneKind.Predicted,
        ExposureCalibrationDisposition calibrationDisposition = ExposureCalibrationDisposition.NormalizationOnly)
    {
        var measured = new List<PixelPoint>();
        foreach (var star in stars)
        {
            var col = Math.Clamp((int)Math.Floor(star.Pixel.X * 4 / rig.Sensor.WidthPixels), 0, 3);
            var row = Math.Clamp((int)Math.Floor(star.Pixel.Y * 4 / rig.Sensor.HeightPixels), 0, 3);
            if (dropTile?.Invoke(col, row) == true)
            {
                continue;
            }
            if (star.Magnitude <= 6.0)
            {
                measured.Add(star.Pixel);
            }
        }
        return BuildSyntheticFieldFromExplicitDetections(
            rig, utc, stars, measured, hfdMedian, validHfdCount, tooBroadExclusions,
            clippedPixelCount, sceneKind, calibrationDisposition);
    }

    private static SyntheticField BuildSyntheticFieldFromExplicitDetections(
        CameraRigConfig rig,
        DateTimeOffset utc,
        IReadOnlyList<SyntheticStar> predictedStars,
        IReadOnlyList<PixelPoint> measuredPixels,
        double? hfdMedian = 2.4,
        int validHfdCount = 12,
        int tooBroadExclusions = 0,
        long clippedPixelCount = 0,
        ProjectedSceneKind sceneKind = ProjectedSceneKind.Predicted,
        ExposureCalibrationDisposition calibrationDisposition = ExposureCalibrationDisposition.NormalizationOnly)
    {
        var detections = measuredPixels.Select((px, idx) => new StellarDetection(
            idx, px, 1500, 1200, 1.2, 1.2, 100.0, 5.0)).ToArray();
        return BuildSyntheticFieldFromCustomDetections(
            rig, utc, predictedStars, detections, hfdMedian, validHfdCount, tooBroadExclusions,
            clippedPixelCount, sceneKind, calibrationDisposition);
    }

    private static SyntheticField BuildSyntheticFieldFromCustomDetections(
        CameraRigConfig rig,
        DateTimeOffset utc,
        IReadOnlyList<SyntheticStar> predictedStars,
        IReadOnlyList<StellarDetection> detections,
        double? hfdMedian = 2.4,
        int validHfdCount = 12,
        int tooBroadExclusions = 0,
        long clippedPixelCount = 0,
        ProjectedSceneKind sceneKind = ProjectedSceneKind.Predicted,
        ExposureCalibrationDisposition calibrationDisposition = ExposureCalibrationDisposition.NormalizationOnly)
    {
        var captureId = Guid.NewGuid();
        var bytesPerPixel = rig.Sensor.PixelFormat == CameraPixelFormat.Rgb24 ? 3 : 2;
        var sampleBits = rig.Sensor.PixelFormat == CameraPixelFormat.Rgb24 ? 8 : 12;
        var containerBits = rig.Sensor.PixelFormat == CameraPixelFormat.Rgb24 ? 8 : 16;
        var layout = new FrameLayoutDescriptor(
            rig.Sensor.WidthPixels,
            rig.Sensor.HeightPixels,
            rig.Sensor.WidthPixels * bytesPerPixel,
            rig.Sensor.PixelFormat,
            containerBits > 8 ? FrameByteOrder.LittleEndian : FrameByteOrder.NotApplicable,
            sampleBits,
            containerBits,
            FrameSamplePacking.ByteAligned,
            rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? ColorFilterArrayPattern.Rggb : ColorFilterArrayPattern.None,
            64,
            4095,
            (long)rig.Sensor.WidthPixels * rig.Sensor.HeightPixels * bytesPerPixel)
        {
            StoredCodeTransform = FrameStoredCodeTransform.RightAlignedV1,
            LevelCodeSpace = FrameLevelCodeSpace.NativeSample
        };

        var rawArtifact = ExposureMeasurementTests.CreateRaw(layout, new byte[16], captureId);
        var rawDescriptorSha = rawArtifact.DescriptorIdentitySha256!;
        var rawPayloadSha = ProcessingIdentity.ComputePayloadSha256(rawArtifact.Payload);
        var rawSource = new ExposureMeasurementSourceV1(
            captureId, rawArtifact.ArtifactId, rawDescriptorSha, rawPayloadSha, rawArtifact.RecipeIdentitySha256, null, layout);
        var linearSource = calibrationDisposition == ExposureCalibrationDisposition.Missing
            ? null
            : new ExposureMeasurementSourceV1(
                captureId, Guid.NewGuid(), new string('L', 64), new string('P', 64),
                rawArtifact.RecipeIdentitySha256, new string('O', 64), layout);

        var detectionArray = detections.ToArray();
        var exclusions = Enumerable.Range(0, tooBroadExclusions)
            .Select(i => new StellarMeasurementExclusion(new(20 + i * 5, 20), 600, 25, StellarExclusionReasons.TooBroad))
            .ToArray();
        var totalSamples = (long)rig.Sensor.WidthPixels * rig.Sensor.HeightPixels;

        var measurement = new ExposureMeasurementV1(
            ExposureMeasurementProducts.SchemaVersion,
            new ExposureMeasurementInputV1(
                rawSource,
                linearSource,
                calibrationDisposition,
                calibrationDisposition == ExposureCalibrationDisposition.Missing ? "missing" : null,
                Compatibility.Orientation,
                rig.Sensor.PixelSizeMicrons,
                rig.Orientation,
                RigProjectionContextFactory.CreateNative(rig),
                new ExposureKey(new("cam-a", "test"), Guid.NewGuid(), captureId),
                OpticalCalibrationVersion: rig.Optics.CalibrationVersion),
            new ExposureBrightnessV1(
                totalSamples, 60, 3800, (ulong)(totalSamples * 100), 0,
                new ExposureClippingV1(4095, clippedPixelCount, null)),
            null,
            new ExposureClippingV1(4095, clippedPixelCount, null),
            new ExposureSourcesV1(
                calibrationDisposition == ExposureCalibrationDisposition.Missing
                    ? ExposureMeasurementStatus.Unavailable
                    : detectionArray.Length > 0 ? ExposureMeasurementStatus.Usable : ExposureMeasurementStatus.Empty,
                null,
                detectionArray.Length + exclusions.Length,
                16,
                new string('S', 64),
                rig.Sensor.PixelFormat == CameraPixelFormat.BayerRggb16 ? BayerPattern.Rggb : (BayerPattern?)null,
                detectionArray,
                exclusions),
            new ExposureHfdV1(
                hfdMedian is not null && validHfdCount > 0 ? ExposureMeasurementStatus.Usable : ExposureMeasurementStatus.Unavailable,
                hfdMedian is null ? "no-hfd" : null,
                validHfdCount,
                hfdMedian,
                hfdMedian,
                0,
                new string('H', 64),
                []));

        var measurementBytes = JsonSerializer.SerializeToUtf8Bytes(
            CaptureContractJson.Canonicalize(JsonSerializer.SerializeToElement(measurement, MeasurementJson)));
        var measurementSha = ProcessingIdentity.ComputePayloadSha256(measurementBytes);
        var measurementArtifact = new ProcessingArtifact(
            Guid.NewGuid(), FrameArtifactRole.Metadata, ExposureMeasurementProducts.Variant,
            rawArtifact.RecipeIdentitySha256, ExposureMeasurementProducts.MediaType, null,
            measurementBytes, utc, TimeSpan.FromSeconds(2), Compatibility)
        {
            CaptureId = captureId,
            SchemaVersion = ExposureMeasurementProducts.SchemaVersion,
            ContentIdentitySha256 = measurementSha,
            SourceArtifactIds = [rawArtifact.ArtifactId]
        };

        var catalogObjects = predictedStars.Select(s =>
        {
            var j2000 = EquatorialPrecession.PrecessToJ2000(s.EquatorialOfDate, utc);
            return new CelestialCatalogObject(s.Id, s.Id, j2000.RightAscensionHours, j2000.DeclinationDegrees, s.Magnitude, s.ColorIndex);
        }).ToArray();
        var projection = RigProjectionContextFactory.CreateNative(rig);
        var catalog = new InMemoryCelestialCatalog(catalogObjects);
        var catalogMeta = new CatalogMetadata("test-catalog", "1", new Uri("https://example.invalid/catalog"), new string('A', 64), "MIT", "1");
        var sceneBuilder = new VisibleSceneBuilder(catalog, new NullConstellationTopology(), new AstronomyEnginePlanetEphemeris());
        var visible = sceneBuilder.BuildAsync(new VisibleSceneRequest(
            utc,
            Observatory,
            projection,
            new CatalogQuery(10.0, 1000),
            catalogMeta,
            horizonPolicy: HorizonPolicy.GeometricHorizon,
            projectionVersion: rig.Optics.CalibrationVersion)).AsTask().GetAwaiter().GetResult();
        var scene = ProjectedSceneJson.Create(
            sceneKind,
            visible,
            ProjectedSceneImageTransformV1.Identity(rig.Sensor.WidthPixels, rig.Sensor.HeightPixels),
            new ProjectedSceneSource(captureId, rawArtifact.ArtifactId, rawDescriptorSha),
            rig.Optics.CalibrationVersion,
            rig.Optics.CalibrationVersion);

        var sceneBytes = ProjectedSceneJson.Serialize(scene);
        var predictionArtifact = new ProcessingArtifact(
            Guid.NewGuid(), FrameArtifactRole.Metadata, "exposure-catalog-prediction-v1",
            rawArtifact.RecipeIdentitySha256, "application/vnd.hvo.projected-scene+json", null,
            sceneBytes, utc, TimeSpan.FromSeconds(2), Compatibility)
        {
            CaptureId = captureId,
            SchemaVersion = ProjectedSceneV1.CurrentSchemaVersion,
            ContentIdentitySha256 = scene.SceneIdentitySha256,
            SourceArtifactIds = [rawArtifact.ArtifactId]
        };

        return new(rawArtifact, measurementArtifact, measurement, predictionArtifact, scene);
    }

    private static (ProcessingArtifact Artifact, MeasuredAstrometrySolutionV1 Solution, AstrometricMapping Mapping) BuildAcceptedAstrometrySolution(
        CameraRigConfig rig,
        DateTimeOffset utc,
        ProcessingArtifact rawArtifact,
        ProjectedSceneV1 scene,
        double rollOffsetDegrees)
    {
        var baseProjection = RigProjectionContextFactory.CreateNative(rig);
        var readoutSha = Sha256Lower(new { readout = rawArtifact.ArtifactId });
        var calibration = new AstrometricCalibration(baseProjection, rig.Optics.CalibrationVersion, readoutSha);
        var frame = new AstrometricFrameContext(
            rawArtifact.CaptureId!.Value,
            rawArtifact.ArtifactId,
            rawArtifact.DescriptorIdentitySha256!,
            ProcessingIdentity.ComputePayloadSha256(rawArtifact.Payload),
            Sha256Lower(Observatory),
            Observatory,
            utc.AddSeconds(-1),
            utc.AddSeconds(1),
            "stellar-detector-v1",
            new string('a', 64));
        var solvedRoll = baseProjection.RollDegrees + rollOffsetDegrees;
        var zenithEq = CoordinateTransforms.HorizontalToEquatorial(
            new AltAzPoint(baseProjection.BoresightAltitudeDegrees, baseProjection.BoresightAzimuthDegrees),
            utc, Observatory.LatitudeDegrees, Observatory.LongitudeDegrees);
        var parameters = new AstrometricFitParameters(
            baseProjection.BoresightAltitudeDegrees,
            baseProjection.BoresightAzimuthDegrees,
            solvedRoll,
            1.0,
            zenithEq.RightAscensionHours,
            zenithEq.DeclinationDegrees);
        var score = Math.Round(100 * (.3 * Math.Exp(-Math.Pow(0.25 / .3, 2)) + .3 * Math.Exp(-Math.Pow(0.30 / .3, 2)) +
            .2 * Math.Min(1, 16 / 40d) + .2 * Math.Min(1, Math.Min(0.85, 0.85) / .7)), 1);
        var quality = new AstrometricFitQuality(
            Score0To100: score,
            InlierCount: 16,
            VerificationCount: 8,
            ExpectedIsolatedCount: 16,
            FittingRmsPixels: 0.25,
            VerificationRmsPixels: 0.30,
            WidthCoverageFraction: 0.85,
            HeightCoverageFraction: 0.85);
        var unsignedAssessment = new AstrometricFrameAssessment(
            AstrometricFrameAssessment.CurrentSchemaVersion,
            string.Empty,
            frame,
            calibration.IdentitySha256,
            new string('b', 64),
            new string('c', 64),
            AstrometricConventions.SolverVersion,
            new string('d', 64),
            AstrometricConventions.CoordinateModel,
            AstrometricConventions.Refraction,
            AstrometricConventions.PixelCoordinates,
            AstrometricSolveMode.Blind,
            AstrometricAssessmentStatus.Accepted,
            "accepted",
            "Accepted fixture solve.",
            parameters,
            quality,
            new string('e', 64),
            null);
        var assessment = unsignedAssessment with { IdentitySha256 = Sha256Lower(unsignedAssessment) };
        var mapping = new AstrometricMapping(calibration, assessment);
        var solution = new MeasuredAstrometrySolutionV1(
            MeasuredAstrometrySolutionV1.CurrentSchemaVersion,
            new string('1', 64),
            new string('2', 64),
            rawArtifact.ArtifactId,
            rawArtifact.DescriptorIdentitySha256!,
            rawArtifact.RecipeIdentitySha256,
            scene.SceneIdentitySha256,
            readoutSha,
            new AstrometricCatalogProvenance("fixture", "1", "fixture", "1"),
            assessment,
            new MeasuredAstrometryResidualSummaryV1(16, [], [], [], 0.25, 0.30, 0.45, null, "unavailable", []),
            null,
            "unavailable");
        var payload = JsonSerializer.SerializeToUtf8Bytes(solution);
        var sha = ProcessingIdentity.ComputePayloadSha256(payload);
        var artifact = new ProcessingArtifact(
            Guid.NewGuid(), FrameArtifactRole.Metadata, "measured-astrometry-v1",
            rawArtifact.RecipeIdentitySha256, MeasuredAstrometryJson.MediaType, null,
            payload, utc, TimeSpan.FromSeconds(2), Compatibility)
        {
            CaptureId = rawArtifact.CaptureId,
            SchemaVersion = MeasuredAstrometrySolutionV1.CurrentSchemaVersion,
            ContentIdentitySha256 = sha,
            SourceArtifactIds = [rawArtifact.ArtifactId]
        };
        return (artifact, solution, mapping);
    }

    private static string Sha256Lower<T>(T value)
        => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private sealed class NullConstellationTopology : IConstellationTopology
    {
        public ConstellationTopologyMetadata Metadata { get; } = new(
            "empty", "1", new Uri("https://example.invalid/topology"), new string('0', 64), "MIT", "1");
        public IReadOnlyList<ConstellationSegment> GetSegments(string constellationId) => [];
    }

    private sealed record SyntheticStar(
        string Id,
        EquatorialPoint EquatorialOfDate,
        AltAzPoint Horizontal,
        PixelPoint Pixel,
        double Magnitude,
        double ColorIndex);

    private sealed record SyntheticField(
        ProcessingArtifact Raw,
        ProcessingArtifact MeasurementArtifact,
        ExposureMeasurementV1 Measurement,
        ProcessingArtifact PredictionArtifact,
        ProjectedSceneV1 Prediction);
}
