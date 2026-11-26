using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using static HVO.SkyMonitor.Processing.Tests.MeasuredStellarAssociationFixtures;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// #526 point-source planet identification: exposure-time ephemeris, compatible projection mapping, combined
/// uncertainty, absent/ambiguous negatives, 1-to-1 conflict handling, label gating, and #523 compatibility.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class PointSourcePlanetAssociationTests
{
    private const int Width = 192;
    private const int Height = 192;
    private static readonly string[] ExpectedReferenceStars = ["star:ref1", "star:ref2"];
    // 2026-01-15T06:00:00Z places Jupiter near opposition high (~76 deg altitude) above Site.
    private static readonly DateTimeOffset ExposureMidpointUtc = new(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
    private static readonly ObserverLocation Site = new(35.347, -113.878, 520);

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task ExposureTimeEphemerisPlanetAssociatesWithCompatibleMappingUncertaintyAndLabelGating(
        CameraPixelFormat format)
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var (scene, jupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:ref1", 18, 12, 2.0), ("star:ref2", -20, -14, 2.8)]).ConfigureAwait(false);
        var unclaimedPixel = new PixelPoint(jupiter.Pixel.X + 36, jupiter.Pixel.Y - 28);
        var pixels = RenderSources(Width, Height,
            [.. scene.Objects.Select(static item => (item.Pixel, item.Kind == CelestialObjectKind.SolarSystemBody ? 9000d : 4500d)),
             (unclaimedPixel, 5000d)]);
        var source = RawArtifact(pixels, format, Width, Height);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(source, scene)).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var product = outcome.Products.Single();
        var parsed = MeasuredStellarAssociationJson.Parse(product.Payload);
        Assert.IsTrue(parsed.Validation.IsValid, $"{parsed.Validation.ReasonCode}:{parsed.Validation.FieldPath}");
        var evidence = parsed.Associations!;
        Assert.AreEqual(evidence.AssociationIdentitySha256, product.ContentIdentitySha256);

        // Catalog stars stay in Associations; point-source planets stay in PointSourcePlanets (never polluting #523).
        CollectionAssert.AreEqual(ExpectedReferenceStars,
            evidence.Associations.Select(static item => item.CatalogId).ToArray());
        Assert.IsFalse(evidence.Associations.Any(static item => item.CatalogId.StartsWith("solar-system:", StringComparison.Ordinal)));

        var planets = evidence.PointSourcePlanets;
        Assert.IsNotNull(planets);
        Assert.AreEqual(ExposureMidpointUtc, planets.EffectiveUtc);
        Assert.AreEqual(ephemeris.ModelVersion, planets.EphemerisModelVersion);
        StringAssert.Contains(planets.ProjectionMappingVersion, "Perspective", StringComparison.Ordinal);
        StringAssert.Contains(planets.ProjectionMappingVersion, "calibration-v1", StringComparison.Ordinal);
        Assert.AreEqual(0.5, planets.PredictionSigmaPixels, 1e-6);
        Assert.IsEmpty(planets.UnmatchedPredictions);

        var planet = Assert.ContainsSingle(planets.Associations);
        Assert.AreEqual("solar-system:Jupiter", planet.ObjectId);
        Assert.AreEqual(SolarSystemBody.Jupiter, planet.Body);
        Assert.IsLessThan(0.35, planet.ResidualPixels);
        Assert.IsGreaterThanOrEqualTo(planets.PredictionSigmaPixels, planet.CombinedUncertaintyPixels);
        Assert.IsNotNull(planet.CovarianceXX);
        Assert.IsNotNull(planet.CovarianceYY);
        Assert.IsTrue(planet.SignalToNoise > 10);
        Assert.IsTrue(planet.LabelEligible, planet.LabelRejectionReason);
        Assert.IsNull(planet.LabelRejectionReason);

        // Unclaimed centroid retained without catalog identity; every detection accounted for once.
        var unclaimed = Assert.ContainsSingle(evidence.UnassociatedDetections);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NoCatalogPrediction, unclaimed.ReasonCode);
        Assert.IsLessThan(0.5, double.Hypot(unclaimed.X - unclaimedPixel.X, unclaimed.Y - unclaimedPixel.Y));
        Assert.AreEqual(evidence.Measurement.DetectionCount,
            evidence.Associations.Count + planets.Associations.Count + evidence.UnassociatedDetections.Count);

        // Canonical JSON round-trip preserves exact bytes and SHA-256 identity.
        var reserialized = MeasuredStellarAssociationJson.Serialize(evidence);
        CollectionAssert.AreEqual(product.Payload.ToArray(), reserialized);

        // Default labels require qualified measured evidence for Jupiter as well as stars.
        var gatedWithMeasured = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false,
            associations: evidence, expectedPositionDiagnostics: false).StarAnnotations;
        Assert.IsTrue(gatedWithMeasured.TextBlocks.Any(static block => block.Lines[0] == "Jupiter"));
        var gatedWithoutMeasured = PresentationLayerProducers.FromProjectedSceneGroupsV2(scene,
            includeConstellations: false, includeImageCircle: false, includeCardinalDirections: false,
            associations: null, expectedPositionDiagnostics: false).StarAnnotations;
        Assert.IsFalse(gatedWithoutMeasured.TextBlocks.Any(static block => block.Lines[0].Contains("Jupiter", StringComparison.Ordinal)));

        // Expected-not-detected diagnostic layer is empty because all predictions matched cleanly.
        var expectedLayer = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(evidence, scene);
        Assert.IsEmpty(expectedLayer.Markers);
        Assert.IsEmpty(expectedLayer.TextBlocks);
    }

    [TestMethod]
    public async Task AbsentPlanetSourceIsUnmatchedAndSuppressesDefaultLabelWhileDiagnosticMarksExpected()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var (scene, _) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:ref1", 20, 14, 2.0)]).ConfigureAwait(false);
        // Render ONLY the star; Jupiter is absent (e.g., local cloud or occultation).
        var starsOnly = scene.Objects.Where(static item => item.Kind == CelestialObjectKind.Star)
            .Select(static item => (item.Pixel, 4500d)).ToArray();
        var source = RawArtifact(RenderSources(Width, Height, starsOnly), CameraPixelFormat.Mono16, Width, Height);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(source, scene)).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.ContainsSingle(evidence.Associations);
        Assert.IsNotNull(evidence.PointSourcePlanets);
        Assert.IsEmpty(evidence.PointSourcePlanets.Associations);
        var unmatched = Assert.ContainsSingle(evidence.PointSourcePlanets.UnmatchedPredictions);
        Assert.AreEqual("solar-system:Jupiter", unmatched.ObjectId);
        Assert.AreEqual(SolarSystemBody.Jupiter, unmatched.Body);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NoMeasuredSource, unmatched.ReasonCode);

        // Default label policy suppresses absent Jupiter while keeping the measured star.
        var annotationObjects = ProjectedSceneAnnotation.CreateObjects(scene, 6.5);
        var defaultGated = StellarLabelPolicy.Apply(annotationObjects, evidence, expectedPositionDiagnostics: false);
        Assert.IsFalse(defaultGated.Single(static item => item.Id == "solar-system:Jupiter").DrawLabel);
        Assert.IsTrue(defaultGated.Single(static item => item.Id == "star:ref1").DrawLabel);

        // Opt-in diagnostic marks absent Jupiter explicitly as expected.
        var diagnosticGated = StellarLabelPolicy.Apply(annotationObjects, evidence, expectedPositionDiagnostics: true);
        var jupiterDiagnostic = diagnosticGated.Single(static item => item.Id == "solar-system:Jupiter");
        Assert.IsTrue(jupiterDiagnostic.DrawLabel);
        Assert.IsTrue(jupiterDiagnostic.ExpectedPosition);

        // Dedicated Expected, not detected layer draws Jupiter (expected) and omits the measured star.
        var expectedLayer = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(evidence, scene);
        Assert.ContainsSingle(expectedLayer.Markers);
        Assert.AreEqual("Jupiter (expected)", Assert.ContainsSingle(expectedLayer.TextBlocks).Lines[0]);
        var measuredLayer = MeasuredAssociationPresentation.CreateMeasuredPayload(evidence, scene);
        Assert.IsFalse(measuredLayer.TextBlocks.Any(static block => block.Lines[0].Contains("Jupiter", StringComparison.Ordinal)));
        Assert.IsTrue(measuredLayer.TextBlocks.Any(static block => block.Lines[0] == "star:ref1" || block.Lines[0] == "Ref1"));
    }

    [TestMethod]
    public async Task AmbiguousCompanionNearPlanetRejectsAssociationAndSuppressesDefaultLabel()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var (scene, jupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter, []).ConfigureAwait(false);
        // Two cleanly separated, uncrowded detections straddling the prediction (13.5 px apart):
        // first is 6.0 px from prediction (inside 10.0 px window), second is 7.5 px (< AmbiguityRatio 1.5 * 6.0 = 9.0 px).
        var first = new PixelPoint(jupiter.Pixel.X - 6.0, jupiter.Pixel.Y);
        var second = new PixelPoint(jupiter.Pixel.X + 7.5, jupiter.Pixel.Y);
        var source = RawArtifact(RenderSources(Width, Height, [(first, 4500d), (second, 4500d)], sigma: 1.1),
            CameraPixelFormat.Mono16, Width, Height);
        var settings = new MeasuredStellarAssociationSettingsV1(
            PredictionSigmaPixels: 4.0, WindowSigmas: 2.5, MaximumWindowPixels: 10.0,
            AmbiguityRatio: 1.5, OffsetRadiusPixels: 12.0);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            Request(source, scene, JsonSerializer.Serialize(new MeasuredStellarAssociationRecipeOptions(settings, new()), JsonSerializerOptions.Web))).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.IsNotNull(evidence.PointSourcePlanets);
        Assert.IsEmpty(evidence.PointSourcePlanets.Associations);
        var unmatched = Assert.ContainsSingle(evidence.PointSourcePlanets.UnmatchedPredictions);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.Ambiguous, unmatched.ReasonCode);
        Assert.HasCount(2, evidence.UnassociatedDetections);
        Assert.IsTrue(evidence.UnassociatedDetections.All(static d =>
            d.ReasonCode == MeasuredStellarAssociationReasonCodes.NearIneligiblePrediction));

        var gated = StellarLabelPolicy.Apply(ProjectedSceneAnnotation.CreateObjects(scene, 6.5), evidence);
        Assert.IsFalse(gated.Single(static item => item.Id == "solar-system:Jupiter").DrawLabel);
    }

    [TestMethod]
    public async Task JointOneToOneConflictBetweenStarAndPlanetNeverDoubleAssignsDetection()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        // Place star:close ~2.4 px from Jupiter; disable crowding so both predictions compete for one PSF placed closer to Jupiter.
        var (scene, jupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:close", 2.4, 0, 2.0)]).ConfigureAwait(false);
        var closeStar = scene.Objects.Single(static item => item.Id == "star:close");
        var psf = new PixelPoint(jupiter.Pixel.X + 0.4, jupiter.Pixel.Y);
        var source = RawArtifact(RenderSources(Width, Height, [(psf, 6000d)]), CameraPixelFormat.Mono16, Width, Height);
        var settings = new MeasuredStellarAssociationSettingsV1(
            PredictionSigmaPixels: 1.5, WindowSigmas: 3.0, MaximumWindowPixels: 3.0,
            AmbiguityRatio: 1.0, CrowdingRadiusPixels: 0.5, OffsetRadiusPixels: 4.0);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            Request(source, scene, JsonSerializer.Serialize(new MeasuredStellarAssociationRecipeOptions(settings, new()), JsonSerializerOptions.Web))).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.IsNotNull(evidence.PointSourcePlanets);
        var planet = Assert.ContainsSingle(evidence.PointSourcePlanets.Associations);
        Assert.AreEqual("solar-system:Jupiter", planet.ObjectId);
        Assert.IsEmpty(evidence.Associations);
        var claimed = Assert.ContainsSingle(evidence.UnmatchedPredictions);
        Assert.AreEqual(closeStar.Id, claimed.CatalogId);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.ClaimedByOther, claimed.ReasonCode);
        Assert.IsEmpty(evidence.UnassociatedDetections);
    }

    [TestMethod]
    public async Task TransformedRoiBinMirrorRotateSceneAssociatesPlanetAndStarsInEmittedCoordinates()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, 160, 160, 600, 600, 320, 320,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var starAltAz = ProjectorFactory.Create(projection).Unproject(new(160 + 28, 160 + 24))!.Value;
        var starEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(
                starAltAz,
                ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var visible = await new VisibleSceneBuilder(
            new InMemoryCelestialCatalog([new CelestialCatalogObject("star:xform", "Transform Star", starEq.RightAscensionHours, starEq.DeclinationDegrees, 1.8)]),
            null, ephemeris).BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, projection,
            new CatalogQuery(6.5, 100), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                new string('C', 64), "test", "v1"),
            projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var transform = new ProjectedSceneImageTransformV1(
            ProjectedSceneImageTransformV1.CurrentSchemaVersion,
            320, 320, CropX: 32, CropY: 32, CropWidth: 256, CropHeight: 256,
            BinX: 2, BinY: 2, HorizontalMirror: true, VerticalMirror: false,
            Rotation: ProjectedSceneQuarterRotation.Degrees90, OutputWidthPixels: 128, OutputHeightPixels: 128);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible, transform,
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
        var source = RawArtifact(
            RenderSources(128, 128, scene.Objects.Select(static item => (item.Pixel, 6000d)).ToArray()),
            CameraPixelFormat.Mono16, 128, 128);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(source, scene)).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.ContainsSingle(evidence.Associations);
        Assert.AreEqual("star:xform", evidence.Associations[0].CatalogId);
        Assert.IsNotNull(evidence.PointSourcePlanets);
        var planet = Assert.ContainsSingle(evidence.PointSourcePlanets.Associations);
        Assert.AreEqual(SolarSystemBody.Jupiter, planet.Body);
        Assert.IsTrue(planet.LabelEligible);
        Assert.IsLessThan(0.35, planet.ResidualPixels);
    }

    [TestMethod]
    public async Task StarOnlyProductOmitsPointSourcePlanetsAndTamperedPlanetEvidenceFailsValidation()
    {
        var starOnlyScene = await StarFieldAsync(("star:a", 0, 0, 2)).ConfigureAwait(false);
        var starOnlyOutcome = await new ProcessingRecipeExecutor()
            .ExecuteAsync(Request(RawArtifact(
                RenderSources(MeasuredStellarAssociationFixtures.Width, MeasuredStellarAssociationFixtures.Height,
                    [(starOnlyScene.Objects[0].Pixel, 6000d)]),
                CameraPixelFormat.Mono16,
                MeasuredStellarAssociationFixtures.Width,
                MeasuredStellarAssociationFixtures.Height), starOnlyScene)).ConfigureAwait(false);
        var starOnlyPayload = starOnlyOutcome.Products.Single().Payload;
        Assert.IsFalse(System.Text.Encoding.UTF8.GetString(starOnlyPayload.Span).Contains("pointSourcePlanets", StringComparison.Ordinal));
        Assert.IsNull(MeasuredStellarAssociationJson.Parse(starOnlyPayload).Associations!.PointSourcePlanets);

        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var (planetScene, _) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:a", 20, 12, 2.0)]).ConfigureAwait(false);
        var planetOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(
            RawArtifact(RenderSources(Width, Height, planetScene.Objects.Select(static o => (o.Pixel, 6000d)).ToArray()),
                CameraPixelFormat.Mono16, Width, Height), planetScene)).ConfigureAwait(false);
        var valid = MeasuredStellarAssociationJson.Parse(planetOutcome.Products.Single().Payload).Associations!;
        var planetAssoc = valid.PointSourcePlanets!.Associations[0];

        // Sun/Moon are resolved discs and invalid inside PointSourcePlanets.
        var invalidSun = valid with
        {
            PointSourcePlanets = valid.PointSourcePlanets with
            {
                Associations = [planetAssoc with { ObjectId = "solar-system:Sun", Body = SolarSystemBody.Sun }]
            }
        };
        Assert.IsFalse(MeasuredStellarAssociationJson.Validate(invalidSun).IsValid);

        // Duplicate DetectionIndex across star and planet fails strict validation.
        var duplicateDetection = valid with
        {
            PointSourcePlanets = valid.PointSourcePlanets with
            {
                Associations = [planetAssoc with { DetectionIndex = valid.Associations[0].DetectionIndex }]
            }
        };
        Assert.IsFalse(MeasuredStellarAssociationJson.Validate(duplicateDetection).IsValid);

        // Combined uncertainty cannot drop below declared PredictionSigmaPixels.
        var badUncertainty = valid with
        {
            PointSourcePlanets = valid.PointSourcePlanets with
            {
                Associations = [planetAssoc with { CombinedUncertaintyPixels = 0.1 }]
            }
        };
        Assert.IsFalse(MeasuredStellarAssociationJson.Validate(badUncertainty).IsValid);

        // Empty PointSourcePlanets wrapper is rejected (must be omitted when no point-source planets exist).
        var emptyWrapper = valid with
        {
            PointSourcePlanets = valid.PointSourcePlanets with { Associations = [], UnmatchedPredictions = [] }
        };
        Assert.IsFalse(MeasuredStellarAssociationJson.Validate(emptyWrapper).IsValid);
    }

    [TestMethod]
    public async Task Step4ExposureMeasurementAssociatesStarsAndPlanetsWithoutBreakingAstrometrySelection()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d, 500, 500, Width, Height,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);

        var starOffsets = new (string Id, double Dx, double Dy, double Mag)[]
        {
            ("star:s1", 24, 16, 2.0), ("star:s2", -28, 18, 2.4), ("star:s3", 18, -26, 2.6)
        };
        var catalogStars = starOffsets.Select(item =>
        {
            var altAz = ProjectorFactory.Create(projection).Unproject(new(Width / 2d + item.Dx, Height / 2d + item.Dy))!.Value;
            var eq = EquatorialPrecession.PrecessToJ2000(
                CoordinateTransforms.HorizontalToEquatorial(altAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
                ExposureMidpointUtc);
            return new CelestialCatalogObject(item.Id, item.Id, eq.RightAscensionHours, eq.DeclinationDegrees, item.Mag);
        }).ToArray();

        // Include both Jupiter (present) and a fixture-placed Mars (absent from image) in the prediction.
        var marsAltAz = ProjectorFactory.Create(projection).Unproject(new(Width / 2d - 32, Height / 2d - 28))!.Value;
        var marsEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(marsAltAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var combinedEphemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = position,
            [SolarSystemBody.Mars] = new(marsEq, 0.4)
        }, ephemeris.ModelVersion);

        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(catalogStars), null, combinedEphemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, projection, new CatalogQuery(6.5, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                projectionVersion: "calibration-v1", solarSystemBodies: [SolarSystemBody.Jupiter, SolarSystemBody.Mars]))
            .ConfigureAwait(false);
        var rendered = visible.Objects.Where(static o => o.Id != "solar-system:Mars")
            .Select(static o => (o.Pixel, 6000d)).Append((new PixelPoint(Width / 2d + 50, Height / 2d + 40), 5000d)).ToArray();
        var rawSamples = RenderSources(Width, Height, rendered);
        var rawBase = RawArtifact(rawSamples, CameraPixelFormat.Mono16, Width, Height);
        var raw = rawBase with
        {
            Layout = rawBase.Layout! with
            {
                BlackLevel = 0,
                StoredCodeTransform = FrameStoredCodeTransform.IdentityV1,
                LevelCodeSpace = FrameLevelCodeSpace.StoredContainer
            },
            CreatedUtc = ExposureMidpointUtc,
            ObservationStartedUtc = ExposureMidpointUtc.AddMilliseconds(-500),
            ObservationEndedUtc = ExposureMidpointUtc.AddMilliseconds(500)
        };
        var executor = new ProcessingRecipeExecutor();
        var linearOutcome = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.LinearNormalization,
            JsonSerializer.SerializeToElement(new LinearNormalizationOptions()),
            ProcessingInputSelector.Raw(raw.Variant), [raw], "qualified-linear-v1")).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, linearOutcome.Status, linearOutcome.ReasonCode);
        var linear = ToArtifact(linearOutcome.Products.Single(), raw);
        var rawSource = new ExposureMeasurementSourceV1(CaptureId, raw.ArtifactId, DescriptorIdentity,
            ProcessingIdentity.ComputePayloadSha256(raw.Payload), raw.RecipeIdentitySha256, null, raw.Layout!);
        var linearSource = new ExposureMeasurementSourceV1(CaptureId, linear.ArtifactId, DescriptorIdentity,
            ProcessingIdentity.ComputePayloadSha256(linear.Payload), linear.RecipeIdentitySha256,
            linear.ContentIdentitySha256, linear.Layout!);
        var expectedInput = new ExposureMeasurementInputV1(rawSource, linearSource,
            ExposureCalibrationDisposition.NormalizationOnly, null, "orientation-v1",
            RigOrientation: new(projection.BoresightAltitudeDegrees, projection.BoresightAzimuthDegrees, projection.RollDegrees),
            ApertureProjection: projection,
            Exposure: new(new("agent", "cam"), Guid.NewGuid(), CaptureId),
            OpticalCalibrationVersion: "calibration-v1",
            Step3Disposition: LinearExposureDisposition.QualifiedNormalizationOnly,
            Step3Revision: 1);
        var step4 = await executor.ExecuteAsync(ExposureMeasurementProducts.Request(expectedInput, raw, linear)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, step4.Status, step4.ReasonCode);
        var step4Product = step4.Products.Single();
        var step4Artifact = ToArtifact(step4Product, raw);

        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(CaptureId, raw.ArtifactId, DescriptorIdentity),
            "calibration-v1", "calibration-v1");
        var sceneOutcome = await executor.ExecuteAsync(ExposureCatalogPredictionProducts.Request(raw, scene)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, sceneOutcome.Status, sceneOutcome.ReasonCode);
        var sceneArtifact = ToArtifact(sceneOutcome.Products.Single(), raw);

        var associated = ExposureAssociationProducts.Associate(raw, step4Artifact, expectedInput, sceneArtifact,
            ExposureAssociationProducts.Variant);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, associated.Status, associated.ReasonCode);
        var assocProduct = associated.Products.Single();
        var assocArtifact = ToArtifact(assocProduct, raw);
        var measurement = ExposureMeasurementProducts.Read(step4Artifact, expectedInput);

        // ExposureAssociationProducts.VerifyMeasurement validates both stars AND point-source planets.
        ExposureAssociationProducts.VerifyMeasurement(assocArtifact, raw, step4Artifact, measurement, sceneArtifact);
        var evidence = MeasuredStellarAssociationJson.Parse(assocProduct.Payload).Associations!;
        Assert.HasCount(3, evidence.Associations);
        Assert.IsNotNull(evidence.PointSourcePlanets);
        Assert.AreEqual(SolarSystemBody.Jupiter, Assert.ContainsSingle(evidence.PointSourcePlanets.Associations).Body);
        Assert.AreEqual(SolarSystemBody.Mars, Assert.ContainsSingle(evidence.PointSourcePlanets.UnmatchedPredictions).Body);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NoMeasuredSource,
            evidence.PointSourcePlanets.UnmatchedPredictions[0].ReasonCode);
        Assert.ContainsSingle(evidence.UnassociatedDetections);

        // #523 astrometry consumer extracts only qualified catalog stars and never leaks planets.
        var exposureAssoc = ExposureMeasurementConsumers.Associate(step4Artifact, expectedInput, scene, new(), new());
        var astrometryDetections = ExposureMeasurementConsumers.AstrometryDetections(step4Artifact, expectedInput, exposureAssoc);
        Assert.HasCount(3, astrometryDetections);
        CollectionAssert.AreEquivalent(
            evidence.Associations.Select(static item => item.DetectionIndex).ToArray(),
            astrometryDetections.Select(static item => item.Index).ToArray());
        Assert.DoesNotContain(evidence.PointSourcePlanets.Associations[0].DetectionIndex,
            astrometryDetections.Select(static item => item.Index).ToArray());

        // Repeated association over retained step-4 measurement is byte-identical.
        var replayed = ExposureAssociationProducts.Associate(raw, step4Artifact, expectedInput, sceneArtifact,
            ExposureAssociationProducts.Variant);
        CollectionAssert.AreEqual(assocProduct.Payload.ToArray(), replayed.Products.Single().Payload.ToArray());
        Assert.AreEqual(assocProduct.ContentIdentitySha256, replayed.Products.Single().ContentIdentitySha256);
    }

    [TestMethod]
    public async Task AbsentPlanetNeverStealsNearbyRealCatalogStarUnderDefaultSettings()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        // Sol M1 exact repro: mag-2 catalog star 1 px from predicted Jupiter (mag ~ -2.6); ONLY the star is rendered!
        var (scene, jupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:nearby", 1.0, 0.0, 2.0), ("star:isolated", -30.0, 20.0, 2.0)]).ConfigureAwait(false);
        var nearbyStar = scene.Objects.Single(static item => item.Id == "star:nearby");
        var isolatedStar = scene.Objects.Single(static item => item.Id == "star:isolated");
        var source = RawArtifact(
            RenderSources(Width, Height, [(nearbyStar.Pixel, 4500d), (isolatedStar.Pixel, 4500d)]),
            CameraPixelFormat.Mono16, Width, Height);

        // Default settings (CrowdingRadiusPixels = 4.0): absent Jupiter MUST NOT steal the mag-2 star via crowding exclusion.
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(source, scene)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.IsNotNull(evidence.PointSourcePlanets);
        Assert.IsEmpty(evidence.PointSourcePlanets.Associations, "Absent Jupiter must never claim a nearby catalog star.");
        var unmatchedPlanet = Assert.ContainsSingle(evidence.PointSourcePlanets.UnmatchedPredictions);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.CrowdedPrediction, unmatchedPlanet.ReasonCode);
        Assert.AreEqual("star:isolated", Assert.ContainsSingle(evidence.Associations).CatalogId);
        var gated = StellarLabelPolicy.Apply(ProjectedSceneAnnotation.CreateObjects(scene, 6.5), evidence);
        Assert.IsFalse(gated.Single(static o => o.Id == "solar-system:Jupiter").DrawLabel);

        // Outside CrowdingRadiusPixels (4.5 px apart), even with wide angular pointing uncertainty for Jupiter,
        // NLL ranking ensures the real star wins its own detection and absent Jupiter gets ClaimedByOther.
        var (wideScene, _) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:outside-crowd", 4.5, 0.0, 2.0)]).ConfigureAwait(false);
        var outsideStar = wideScene.Objects.Single(static item => item.Id == "star:outside-crowd");
        var wideSource = RawArtifact(RenderSources(Width, Height, [(outsideStar.Pixel, 4500d)]),
            CameraPixelFormat.Mono16, Width, Height);
        var wideSettings = new MeasuredStellarAssociationSettingsV1(CrowdingRadiusPixels: 4.0)
        {
            PlanetPointingUncertaintyArcseconds = 1200
        };
        var wideOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(wideSource, wideScene,
            JsonSerializer.Serialize(new MeasuredStellarAssociationRecipeOptions(wideSettings, new()), JsonSerializerOptions.Web))).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, wideOutcome.Status, wideOutcome.ReasonCode);
        var wideEvidence = MeasuredStellarAssociationJson.Parse(wideOutcome.Products.Single().Payload).Associations!;
        Assert.AreEqual("star:outside-crowd", Assert.ContainsSingle(wideEvidence.Associations).CatalogId);
        Assert.IsEmpty(wideEvidence.PointSourcePlanets!.Associations);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.ClaimedByOther,
            Assert.ContainsSingle(wideEvidence.PointSourcePlanets.UnmatchedPredictions).ReasonCode);
    }

    [TestMethod]
    public async Task PlanetAngularUncertaintyPropagatesAcrossPixelScalesAndOffAxisProjectionLocations()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);

        async Task<(MeasuredStellarAssociationsV1 Evidence, ProjectedCelestialObject Planet)> RunAtScaleAsync(
            double focalLength, double boresightOffsetAltDeg, double offsetPixels,
            double ephemArcsec, double pointingArcsec)
        {
            var proj = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d,
                focalLength, focalLength, Width, Height, ProjectionAperture.Rectangular,
                BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees - boresightOffsetAltDeg,
                BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
            var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, ephemeris)
                .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, proj, new CatalogQuery(6.5, 100),
                    new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                    projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
            var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
                ProjectedSceneImageTransformV1.Identity(Width, Height),
                new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
            var planet = scene.Objects.Single(static item => item.Id == "solar-system:Jupiter");
            var rendered = new PixelPoint(planet.Pixel.X + offsetPixels, planet.Pixel.Y);
            var raw = RawArtifact(RenderSources(Width, Height, [(rendered, 6000d)]), CameraPixelFormat.Mono16, Width, Height);
            var settings = new MeasuredStellarAssociationSettingsV1(PredictionSigmaPixels: 0.5, WindowSigmas: 3.0, MaximumWindowPixels: 2.0, OffsetRadiusPixels: 6.0)
            {
                PlanetEphemerisUncertaintyArcseconds = ephemArcsec,
                PlanetPointingUncertaintyArcseconds = pointingArcsec
            };
            var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(raw, scene,
                JsonSerializer.Serialize(new MeasuredStellarAssociationRecipeOptions(settings, new()), JsonSerializerOptions.Web))).ConfigureAwait(false);
            Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
            return (MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!, planet);
        }

        // Same angular uncertainty (30" ephem + 120" pointing => 123.69" = 5.997e-4 rad) across 10x pixel scale:
        // At f = 250 px: angular sigma = 0.15 px => propagated ~ 0.522 px; a 3.0 px offset is REJECTED (> 3 sigma).
        // At f = 2500 px: angular sigma = 1.50 px => propagated ~ 1.581 px; the same 3.0 px offset is ACCEPTED (< 3 sigma).
        var (wideScaleZero, _) = await RunAtScaleAsync(250, 0, 0.0, 30, 120).ConfigureAwait(false);
        var (narrowScaleZero, _) = await RunAtScaleAsync(2500, 0, 0.0, 30, 120).ConfigureAwait(false);
        var wideSigma = Assert.ContainsSingle(wideScaleZero.PointSourcePlanets!.Associations).PropagatedPredictionSigmaPixels;
        var narrowSigma = Assert.ContainsSingle(narrowScaleZero.PointSourcePlanets!.Associations).PropagatedPredictionSigmaPixels;
        Assert.IsGreaterThan(wideSigma * 2.5, narrowSigma,
            $"Expected narrow-field propagated sigma ({narrowSigma}) to grow materially over wide-field ({wideSigma}).");
        Assert.AreEqual(30, narrowScaleZero.PointSourcePlanets.EphemerisUncertaintyArcseconds, 1e-6);
        Assert.AreEqual(120, narrowScaleZero.PointSourcePlanets.PointingUncertaintyArcseconds, 1e-6);

        var (wideOffset, _) = await RunAtScaleAsync(250, 0, 3.0, 30, 120).ConfigureAwait(false);
        var (narrowOffset, _) = await RunAtScaleAsync(2500, 0, 3.0, 30, 120).ConfigureAwait(false);
        Assert.IsEmpty(wideOffset.PointSourcePlanets!.Associations, "3 px offset must be rejected at coarse pixel scale.");
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.OffsetMeasuredSource,
            Assert.ContainsSingle(wideOffset.PointSourcePlanets.UnmatchedPredictions).ReasonCode);
        var acceptedNarrow = Assert.ContainsSingle(narrowOffset.PointSourcePlanets!.Associations);
        Assert.IsLessThanOrEqualTo(3.2, acceptedNarrow.ResidualPixels);
        Assert.IsGreaterThanOrEqualTo(acceptedNarrow.PropagatedPredictionSigmaPixels, acceptedNarrow.CombinedUncertaintyPixels);

        // Projection location dependence: in Perspective projection at 32 deg off-axis, local radial scale grows by sec^2(32 deg) ~ 1.39x.
        var (onAxis, _) = await RunAtScaleAsync(120, 0, 0.0, 0, 600).ConfigureAwait(false);
        var (offAxis, _) = await RunAtScaleAsync(120, 32, 0.0, 0, 600).ConfigureAwait(false);
        var onAxisSigma = Assert.ContainsSingle(onAxis.PointSourcePlanets!.Associations).PropagatedPredictionSigmaPixels;
        var offAxisSigma = Assert.ContainsSingle(offAxis.PointSourcePlanets!.Associations).PropagatedPredictionSigmaPixels;
        Assert.IsGreaterThan(onAxisSigma * 1.12, offAxisSigma,
            $"Off-axis Perspective sigma ({offAxisSigma}) must exceed on-axis sigma ({onAxisSigma}) due to tangent Jacobian.");
        var onAxisAngular = Math.Sqrt(onAxisSigma * onAxisSigma - 0.5 * 0.5);
        var offAxisAngular = Math.Sqrt(offAxisSigma * offAxisSigma - 0.5 * 0.5);
        Assert.IsGreaterThan(onAxisAngular * 1.35, offAxisAngular,
            $"Angular Jacobian component must grow by ~sec^2(32 deg) (~1.39x): on={onAxisAngular}, off={offAxisAngular}.");
    }

    [TestMethod]
    public async Task MeasurementIncompleteMarksInAperturePlanetsIncompleteAndOutsideApertureUnmatched()
    {
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d, 500, 500, Width, Height,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees, EnforceSensorBounds: false);
        var marsAltAz = ProjectorFactory.Create(projection).Unproject(new(Width / 2d + 50, Height / 2d))!.Value;
        var marsEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(marsAltAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var combined = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = position,
            [SolarSystemBody.Mars] = new(marsEq, 0.5)
        }, ephemeris.ModelVersion);
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, combined)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, projection,
                new CatalogQuery(6.5, 100), new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"),
                    new string('C', 64), "test", "v1"), projectionVersion: "perspective-v1",
                solarSystemBodies: [SolarSystemBody.Jupiter, SolarSystemBody.Mars])).ConfigureAwait(false);
        var baseScene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
        // Move Mars outside the sensor aperture while keeping Jupiter inside at center.
        var scene = baseScene with
        {
            Objects = baseScene.Objects
                .Select(o => o.Id == "solar-system:Mars" ? o with { Pixel = new(Width + 15, Height / 2d) } : o)
                .ToArray()
        };

        var settings = new MeasuredStellarAssociationSettingsV1();
        var policy = new StellarLabelPolicySettingsV1();
        var busyField = Field(Width, Height);
        AddNoise(busyField, 42, 3.0);
        AddGaussian(busyField, new(40, 40), 1.4, 20_000, Width);
        AddGaussian(busyField, new(80, 80), 1.4, 20_000, Width);
        AddGaussian(busyField, new(120, 120), 1.4, 20_000, Width);
        var validMask = new bool[Width * Height];
        Array.Fill(validMask, true);
        var incompleteMeasurement = StellarSourceMeasurer.Measure(busyField, validMask, [], Width, Height,
            new StellarMeasurementOptions { MaximumCandidateCount = 1 });
        Assert.AreEqual(StellarMeasurementStatus.CandidateBudgetExceeded, incompleteMeasurement.Status);
        var entries = MeasuredStellarAssociator.Associate(scene, incompleteMeasurement, settings, policy);
        var raw = RawArtifact(busyField, CameraPixelFormat.Mono16, Width, Height);
        var evidence = MeasuredStellarAssociationRecipe.CreateEvidence(raw, raw.Layout!, CaptureId, DescriptorIdentity, scene,
            new(StellarSourceMeasurer.AlgorithmVersion, incompleteMeasurement.SettingsIdentitySha256.ToUpperInvariant(),
                MeasuredStellarAssociationRecipe.MonoReconstruction,
                incompleteMeasurement.Status, incompleteMeasurement.MedianBackground, incompleteMeasurement.MedianNoiseSigma,
                incompleteMeasurement.CandidateCount, incompleteMeasurement.Detections.Count, incompleteMeasurement.Exclusions.Count),
            settings, policy, entries);
        var validation = MeasuredStellarAssociationJson.Validate(evidence);
        Assert.IsTrue(validation.IsValid, $"{validation.ReasonCode}:{validation.FieldPath}");
        Assert.IsNotNull(evidence.PointSourcePlanets);
        Assert.IsEmpty(evidence.PointSourcePlanets.Associations);
        Assert.HasCount(2, evidence.PointSourcePlanets.UnmatchedPredictions);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.MeasurementIncomplete,
            evidence.PointSourcePlanets.UnmatchedPredictions.Single(static p => p.Body == SolarSystemBody.Jupiter).ReasonCode);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.OutsideAperture,
            evidence.PointSourcePlanets.UnmatchedPredictions.Single(static p => p.Body == SolarSystemBody.Mars).ReasonCode);
        var gated = StellarLabelPolicy.Apply(ProjectedSceneAnnotation.CreateObjects(scene, 6.5), evidence);
        Assert.IsFalse(gated.Any(static o => o.DrawLabel));
    }

    [TestMethod]
    public async Task MeasuredDeepSkyStackExcludesProfileStarsNearMeasuredPlanetAndVetoesPlanetPeak()
    {
        var fixture = await MeasuredAstrometryFixture.CreateAsync().ConfigureAwait(false);
        var executor = new ProcessingRecipeExecutor();
        var astrometryOutcome = await executor.ExecuteAsync(fixture.Request()).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, astrometryOutcome.Status, astrometryOutcome.ReasonCode);
        var solution = MeasuredAstrometryJson.Parse(astrometryOutcome.Products.Single().Payload);
        var verifiedIds = solution.Residuals.NativeAssociations.Select(static a => a.DetectionIndex).ToHashSet();
        var profileStars = fixture.Associations.Associations
            .Where(a => !a.Saturated && !a.Trailed && verifiedIds.Contains(a.DetectionIndex))
            .Take(2).ToArray();
        Assert.HasCount(2, profileStars);
        var crowdedByPlanet = profileStars[0];
        var isolatedStar = profileStars[1];
        // Place a measured planet 6 px from crowdedByPlanet (< 2 * StellarVetoRadius = 10 px).
        var planetPixel = new PixelPoint(crowdedByPlanet.MeasuredX + 6.0, crowdedByPlanet.MeasuredY);
        var nextIndex = fixture.Associations.Measurement.DetectionCount;
        var planetAssoc = new MeasuredPointSourcePlanetAssociationV1(
            "solar-system:Jupiter", SolarSystemBody.Jupiter, nextIndex, -2.0,
            planetPixel.X, planetPixel.Y, planetPixel.X, planetPixel.Y, 0, 0, 0.5, 0.5,
            5000, 80, 0.01, 0, 0.01, false, 0, false, null, null, true, null);
        var withPlanet = fixture.Associations with
        {
            Measurement = fixture.Associations.Measurement with { DetectionCount = nextIndex + 1 },
            PointSourcePlanets = new(ExposureMidpointUtc, "ephem-v1", "map-v1", 0.5, 30, 60, [planetAssoc], [])
        };
        var diffusePeak = new ExtendedStackDetection(1, new PixelPoint(isolatedStar.MeasuredX + 20, isolatedStar.MeasuredY + 20),
            3.6, 500, 100, 10, 12, 3, null);
        var planetPeak = new ExtendedStackDetection(2, new PixelPoint(planetPixel.X + 0.5, planetPixel.Y),
            2.4, 800, 100, 10, 20, 3, null);
        var extraction = new ExtendedStackExtraction(10, 10, 2, 0, 0, false, [diffusePeak, planetPeak]);

        var queriedCenters = new List<PixelPoint>();
        var result = MeasuredDeepSkyStackRecipe.ApplyStellarProfile(extraction, withPlanet, solution, ReadOnlySpan<float>.Empty,
            (_, center) =>
            {
                queriedCenters.Add(center);
                return new ExtendedStackDetection(99, center, 1.4, 400, 100, 10, 15, 3, null);
            });

        Assert.IsFalse(queriedCenters.Any(c => Math.Abs(c.X - crowdedByPlanet.MeasuredX) < 1e-4 && Math.Abs(c.Y - crowdedByPlanet.MeasuredY) < 1e-4),
            "Profile star within 10 px of a measured planet must not be treated as isolated.");
        Assert.IsTrue(queriedCenters.Any(c => Math.Abs(c.X - isolatedStar.MeasuredX) < 1e-4 && Math.Abs(c.Y - isolatedStar.MeasuredY) < 1e-4));
        Assert.AreEqual(ExtendedStackRejection.StellarStructure, result.Detections.Single(static d => d.Id == 2).Rejection,
            "A peak coincident with a measured point-source planet must be vetoed as point-source structure.");
    }

    [TestMethod]
    public void ResolvedFootprintLabelExemptionAlignsWithFootprintPresence()
    {
        var resolvedBody = new ProjectedAnnotationObject("solar-system:Jupiter", "Jupiter", new PixelPoint(50, 50))
        {
            FootprintParts = [new ResolvedFootprintPart(true, [new PixelPoint(45, 50), new PixelPoint(55, 50), new PixelPoint(50, 55)])]
        };
        var pointPlanet = new ProjectedAnnotationObject("solar-system:Saturn", "Saturn", new PixelPoint(80, 80));

        Assert.IsTrue(StellarLabelPolicy.IsOutsidePolicy(resolvedBody));
        Assert.IsFalse(StellarLabelPolicy.IsOutsidePolicy(pointPlanet));

        var gated = StellarLabelPolicy.Apply([resolvedBody, pointPlanet], associations: null, expectedPositionDiagnostics: false);
        Assert.IsTrue(gated[0].DrawLabel, "Resolved footprint body stays outside point-source label gating.");
        Assert.IsFalse(gated[1].DrawLabel, "Point-source planet without measured evidence is suppressed by default.");
    }

    private static ProcessingArtifact ToArtifact(ProcessingProduct product, ProcessingArtifact raw) => new(
        ProcessingIdentity.CreateArtifactId(product.OutputIdentitySha256), product.Role, product.Variant,
        product.Recipe.IdentitySha256, product.MediaType, product.Layout, product.Payload, raw.CreatedUtc,
        product.TotalIntegration, product.Compatibility, raw.CaptureSequence, product.SourceArtifactIds)
    {
        CaptureId = raw.CaptureId,
        DescriptorIdentitySha256 = DescriptorIdentity,
        ProductKind = product.Kind,
        SchemaVersion = product.SchemaVersion,
        ContentIdentitySha256 = product.ContentIdentitySha256 ?? product.OutputIdentitySha256,
        RecipeDescriptor = product.Recipe.Descriptor
    };

    private static async Task<(ProjectedSceneV1 Scene, ProjectedCelestialObject Planet)> BuildPlanetSceneAsync(
        AstronomyEnginePlanetEphemeris ephemeris,
        SolarSystemBody body,
        (string Id, double OffsetX, double OffsetY, double Magnitude)[] stars)
    {
        var position = ephemeris.GetPosition(body, ExposureMidpointUtc);
        var bodyAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d, 500, 500, Width, Height,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: bodyAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: bodyAltAz.AzimuthDegrees);
        var projector = ProjectorFactory.Create(projection);
        var catalog = new List<CelestialCatalogObject>(stars.Length);
        foreach (var (id, dx, dy, mag) in stars)
        {
            var altAz = projector.Unproject(new(Width / 2d + dx, Height / 2d + dy))!.Value;
            var eq = EquatorialPrecession.PrecessToJ2000(
                CoordinateTransforms.HorizontalToEquatorial(altAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
                ExposureMidpointUtc);
            catalog.Add(new(id, id.StartsWith("star:", StringComparison.Ordinal) ? char.ToUpperInvariant(id[5]) + id[6..] : id,
                eq.RightAscensionHours, eq.DeclinationDegrees, mag));
        }
        var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(catalog), null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, projection, new CatalogQuery(6.5, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1", solarSystemBodies: [body])).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity),
            "calibration-v1", visible.Request.ProjectionVersion);
        var planet = scene.Objects.Single(item => item.Id == $"solar-system:{body}");
        return (scene, planet);
    }

    private static double[] RenderSources(int width, int height, IReadOnlyList<(PixelPoint Pixel, double Amplitude)> sources, double sigma = 1.25)
    {
        var samples = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = 1000d + DeterministicNoise(y * width + x);
                foreach (var (pixel, amplitude) in sources)
                {
                    var dx = (x + 0.5 - pixel.X) / sigma;
                    var dy = (y + 0.5 - pixel.Y) / sigma;
                    if (dx * dx + dy * dy < 36)
                        value += amplitude * Math.Exp(-0.5 * (dx * dx + dy * dy));
                }
                samples[y * width + x] = value;
            }
        }
        return samples;
    }

    private static double DeterministicNoise(int index)
    {
        var bits = (uint)index + 5261u;
        bits ^= bits >> 16;
        bits = unchecked(bits * 0x7FEB352Du);
        bits ^= bits >> 15;
        bits = unchecked(bits * 0x846CA68Bu);
        bits ^= bits >> 16;
        return (int)(bits % 13) - 6;
    }
}
