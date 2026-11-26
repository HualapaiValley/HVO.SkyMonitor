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
        // 1. Strict non-steal contract: place star:close ~2.4 px from Jupiter and render ONE lone PSF inside BOTH
        //    the star's declared pointing window and Jupiter's window. Neither double-assigns, and Jupiter is withheld as Ambiguous.
        var (scene, jupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:close", 2.4, 0, 2.0)]).ConfigureAwait(false);
        var closeStar = scene.Objects.Single(static item => item.Id == "star:close");
        var psf = new PixelPoint(jupiter.Pixel.X + 0.4, jupiter.Pixel.Y);
        var source = RawArtifact(RenderSources(Width, Height, [(psf, 6000d)]), CameraPixelFormat.Mono16, Width, Height);
        var settings = new MeasuredStellarAssociationSettingsV1(
            PredictionSigmaPixels: 1.5, WindowSigmas: 3.0, MaximumWindowPixels: 3.0,
            AmbiguityRatio: 1.0, CrowdingRadiusPixels: 0.5, OffsetRadiusPixels: 4.0);
        var optionsJson = JsonSerializer.Serialize(new MeasuredStellarAssociationRecipeOptions(settings, new()), JsonSerializerOptions.Web);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(source, scene, optionsJson)).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.IsNotNull(evidence.PointSourcePlanets);
        Assert.IsEmpty(evidence.PointSourcePlanets.Associations,
            "Jupiter must be withheld when a lone detection also lies inside an unassigned competing star's pointing window.");
        Assert.IsEmpty(evidence.Associations,
            "The worse-fitting competing star must not retroactively claim the contested detection either.");
        var unmatchedPlanet = Assert.ContainsSingle(evidence.PointSourcePlanets.UnmatchedPredictions);
        Assert.AreEqual("solar-system:Jupiter", unmatchedPlanet.ObjectId);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.Ambiguous, unmatchedPlanet.ReasonCode);
        var unmatchedStar = Assert.ContainsSingle(evidence.UnmatchedPredictions);
        Assert.AreEqual(closeStar.Id, unmatchedStar.CatalogId);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.Ambiguous, unmatchedStar.ReasonCode);
        Assert.ContainsSingle(evidence.UnassociatedDetections);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NearIneligiblePrediction, evidence.UnassociatedDetections[0].ReasonCode);

        // 2. Clean win when the competing star sits outside its declared pointing window (separated by 6.0 px > 3*1.5 = 4.5 px).
        var (separatedScene, separatedJupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:far", 6.0, 0, 2.0)]).ConfigureAwait(false);
        var farStar = separatedScene.Objects.Single(static item => item.Id == "star:far");
        var jupiterOnlySource = RawArtifact(
            RenderSources(Width, Height, [(new PixelPoint(separatedJupiter.Pixel.X + 0.4, separatedJupiter.Pixel.Y), 6000d)]),
            CameraPixelFormat.Mono16, Width, Height);
        var separatedOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            Request(jupiterOnlySource, separatedScene, optionsJson)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, separatedOutcome.Status, separatedOutcome.ReasonCode);
        var separatedEvidence = MeasuredStellarAssociationJson.Parse(separatedOutcome.Products.Single().Payload).Associations!;
        var wonJupiter = Assert.ContainsSingle(separatedEvidence.PointSourcePlanets!.Associations);
        Assert.AreEqual("solar-system:Jupiter", wonJupiter.ObjectId);
        Assert.IsTrue(wonJupiter.LabelEligible);
        Assert.IsEmpty(separatedEvidence.Associations);
        Assert.AreEqual(farStar.Id, Assert.ContainsSingle(separatedEvidence.UnmatchedPredictions).CatalogId);
        Assert.IsEmpty(separatedEvidence.UnassociatedDetections);

        // 3. Clean 1-to-1 joint match when BOTH the star and Jupiter are detected (distinct DetectionIndexes):
        //    (a) Full Mono16 pixel pipeline with two cleanly separated PSFs (16 px apart).
        var (pairScene, pairJupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:pair", 16.0, 0, 2.0)]).ConfigureAwait(false);
        var pairStar = pairScene.Objects.Single(static item => item.Id == "star:pair");
        var bothSource = RawArtifact(
            RenderSources(Width, Height, [(pairJupiter.Pixel, 7500d), (pairStar.Pixel, 4500d)]),
            CameraPixelFormat.Mono16, Width, Height);
        var bothOutcome = await new ProcessingRecipeExecutor().ExecuteAsync(
            Request(bothSource, pairScene, optionsJson)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, bothOutcome.Status, bothOutcome.ReasonCode);
        var bothEvidence = MeasuredStellarAssociationJson.Parse(bothOutcome.Products.Single().Payload).Associations!;
        var matchedStar = Assert.ContainsSingle(bothEvidence.Associations);
        var matchedPlanet = Assert.ContainsSingle(bothEvidence.PointSourcePlanets!.Associations);
        Assert.AreEqual(pairStar.Id, matchedStar.CatalogId);
        Assert.AreEqual("solar-system:Jupiter", matchedPlanet.ObjectId);
        Assert.AreNotEqual(matchedStar.DetectionIndex, matchedPlanet.DetectionIndex);
        Assert.IsEmpty(bothEvidence.UnassociatedDetections);

        //    (b) Close overlapping windows (2.4 px apart) when BOTH detections exist: each matches its own detection 1-to-1
        //        and the matched star never falsely blocks the present planet.
        var bothCloseEntries = MeasuredStellarAssociator.Associate(
            scene,
            [
                new StellarDetection(0, jupiter.Pixel, 7500, 500, 1.1, 1.1, 1000, 5) { SignalToNoise = 90, CentroidCovariance = new(0.01, 0, 0.01) },
                new StellarDetection(1, closeStar.Pixel, 4500, 350, 1.1, 1.1, 1000, 5) { SignalToNoise = 60, CentroidCovariance = new(0.01, 0, 0.01) }
            ],
            [], measurementCompleted: true, settings, new(), CancellationToken.None);
        Assert.AreEqual(closeStar.Id, Assert.ContainsSingle(bothCloseEntries.Associations).CatalogId);
        Assert.AreEqual("solar-system:Jupiter", Assert.ContainsSingle(bothCloseEntries.PointSourcePlanets!.Associations).ObjectId);
        Assert.AreNotEqual(bothCloseEntries.Associations[0].DetectionIndex, bothCloseEntries.PointSourcePlanets.Associations[0].DetectionIndex);
        Assert.IsEmpty(bothCloseEntries.UnassociatedDetections);
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

    [TestMethod]
    public async Task AbsentPlanetNeverClaimsCrowdedNearEdgeOffsetOrAmbiguousCatalogStarDetections()
    {
        // Astra D1 + Opus M1: untouched default settings at narrow focal lengths where planet windows widen beyond
        // CrowdingRadiusPixels (4 px). Absent Jupiter MUST NEVER steal a catalog star's detection even when that star
        // is mutually crowded, near the aperture edge, offset > 2 px within pointing uncertainty, or ambiguous.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);

        async Task<(ProjectedSceneV1 Scene, ProjectedCelestialObject Planet)> BuildSceneAtFocalLengthAsync(
            double focalLength, double principalX, double principalY,
            (string Id, double Dx, double Dy, double Mag)[] stars)
        {
            var proj = new ProjectionContext(ProjectionModel.Perspective, principalX, principalY,
                focalLength, focalLength, Width, Height, ProjectionAperture.Rectangular,
                BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
                BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
            var projector = ProjectorFactory.Create(proj);
            var catalog = new List<CelestialCatalogObject>(stars.Length);
            foreach (var (id, dx, dy, mag) in stars)
            {
                var altAz = projector.Unproject(new(principalX + dx, principalY + dy))!.Value;
                var eq = EquatorialPrecession.PrecessToJ2000(
                    CoordinateTransforms.HorizontalToEquatorial(altAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
                    ExposureMidpointUtc);
                catalog.Add(new(id, id, eq.RightAscensionHours, eq.DeclinationDegrees, mag));
            }
            var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog(catalog), null, ephemeris)
                .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, proj, new CatalogQuery(6.5, 100),
                    new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                    projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
            var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
                ProjectedSceneImageTransformV1.Identity(Width, Height),
                new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
            return (scene, scene.Objects.Single(static o => o.Id == "solar-system:Jupiter"));
        }

        // (a) f=7000 default settings: two mutually crowded mag-2 catalog stars 4.5 px and 5.5 px from absent Jupiter.
        var (crowdedScene, _) = await BuildSceneAtFocalLengthAsync(7000, Width / 2d, Height / 2d,
            [("star:crowd1", 4.5, 0.0, 2.0), ("star:crowd2", 5.5, 0.0, 2.0)]).ConfigureAwait(false);
        var crowd1 = crowdedScene.Objects.Single(static o => o.Id == "star:crowd1");
        var crowdedEntries = MeasuredStellarAssociator.Associate(
            crowdedScene,
            [new StellarDetection(0, crowd1.Pixel, 6000, 400, 1.2, 1.2, 1000, 5)
            {
                SignalToNoise = 80,
                CentroidCovariance = new(0.01, 0, 0.01)
            }],
            [], measurementCompleted: true, new(), new(), CancellationToken.None);
        Assert.IsEmpty(crowdedEntries.PointSourcePlanets!.Associations,
            "Absent Jupiter must not steal a mutually crowded catalog star's detection at f=7000.");
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.Ambiguous,
            Assert.ContainsSingle(crowdedEntries.PointSourcePlanets.UnmatchedPredictions).ReasonCode);

        // (b) f=5000 default settings: near-edge catalog star 4.8 px from absent Jupiter (Jupiter at x=8.0, star at x=3.2 < EdgeMargin 6.0).
        var (nearEdgeScene, nearEdgeJupiter) = await BuildSceneAtFocalLengthAsync(5000, 8.0, Height / 2d,
            [("star:edge", -4.8, 0.0, 2.0)]).ConfigureAwait(false);
        var edgeStar = nearEdgeScene.Objects.Single(static o => o.Id == "star:edge");
        var nearEdgeEntries = MeasuredStellarAssociator.Associate(
            nearEdgeScene,
            [new StellarDetection(0, edgeStar.Pixel, 6000, 400, 1.2, 1.2, 1000, 5)
            {
                SignalToNoise = 80,
                CentroidCovariance = new(0.01, 0, 0.01)
            }],
            [], measurementCompleted: true, new(), new(), CancellationToken.None);
        Assert.IsEmpty(nearEdgeEntries.PointSourcePlanets!.Associations,
            "Absent Jupiter must not steal a near-edge catalog star's detection at f=5000.");
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.Ambiguous,
            Assert.ContainsSingle(nearEdgeEntries.PointSourcePlanets.UnmatchedPredictions).ReasonCode);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NearEdge,
            Assert.ContainsSingle(nearEdgeEntries.UnmatchedPredictions).ReasonCode);

        // Positive control on the same f=5000 scene: if real Jupiter IS detected at Jupiter's own pixel, it matches cleanly.
        var presentJupiterEntries = MeasuredStellarAssociator.Associate(
            nearEdgeScene,
            [new StellarDetection(0, nearEdgeJupiter.Pixel, 9000, 600, 1.2, 1.2, 1000, 5)
            {
                SignalToNoise = 95,
                CentroidCovariance = new(0.01, 0, 0.01)
            }],
            [], measurementCompleted: true, new(), new(), CancellationToken.None);
        var presentJupiter = Assert.ContainsSingle(presentJupiterEntries.PointSourcePlanets!.Associations);
        Assert.IsTrue(presentJupiter.LabelEligible);

        // (c) f=5000 default settings: offset catalog star 4.6 px from absent Jupiter with detection 2.2 px off star toward Jupiter.
        var (offsetScene, _) = await BuildSceneAtFocalLengthAsync(5000, Width / 2d, Height / 2d,
            [("star:offset", 4.6, 0.0, 2.0)]).ConfigureAwait(false);
        var offsetStar = offsetScene.Objects.Single(static o => o.Id == "star:offset");
        var shiftedDetection = new PixelPoint(offsetStar.Pixel.X - 2.2, offsetStar.Pixel.Y);
        var offsetEntries = MeasuredStellarAssociator.Associate(
            offsetScene,
            [new StellarDetection(0, shiftedDetection, 6000, 400, 1.2, 1.2, 1000, 5)
            {
                SignalToNoise = 80,
                CentroidCovariance = new(0.01, 0, 0.01)
            }],
            [], measurementCompleted: true, new(), new(), CancellationToken.None);
        Assert.IsEmpty(offsetEntries.PointSourcePlanets!.Associations,
            "Absent Jupiter must not steal an offset catalog star detection (>2 px residual within 60\" pointing error).");
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.Ambiguous,
            Assert.ContainsSingle(offsetEntries.PointSourcePlanets.UnmatchedPredictions).ReasonCode);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.OffsetMeasuredSource,
            Assert.ContainsSingle(offsetEntries.UnmatchedPredictions).ReasonCode);
    }

    [TestMethod]
    public async Task AbsentPlanetNeverClaimsOffsetCatalogStarInMono16FixtureOrAnnotationLayer()
    {
        // Astra D1 exact Mono16 pixel fixture: f=5000 px, one catalog star 4.6 px from predicted Jupiter, zero planets
        // rendered, star PSF centroid shifted 3.1 px toward Jupiter (within the star's 60" pointing window ~4.61 px).
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var proj = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d,
            5000, 5000, Width, Height, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var starAltAz = ProjectorFactory.Create(proj).Unproject(new(Width / 2d + 4.6, Height / 2d))!.Value;
        var starEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(starAltAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var catalog = new InMemoryCelestialCatalog([
            new CelestialCatalogObject("star:offset-mono16", "Offset Star", starEq.RightAscensionHours, starEq.DeclinationDegrees, 2.0)
        ]);
        var visible = await new VisibleSceneBuilder(catalog, null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, proj, new CatalogQuery(6.5, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
        var star = scene.Objects.Single(static o => o.Id == "star:offset-mono16");
        var jupiter = scene.Objects.Single(static o => o.Id == "solar-system:Jupiter");
        var shiftedCentroid = new PixelPoint(star.Pixel.X - 3.1, star.Pixel.Y);
        var source = RawArtifact(RenderSources(Width, Height, [(shiftedCentroid, 6000d)]),
            CameraPixelFormat.Mono16, Width, Height);

        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(source, scene)).ConfigureAwait(false);

        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;
        Assert.IsNotNull(evidence.PointSourcePlanets);
        Assert.IsEmpty(evidence.PointSourcePlanets.Associations,
            "Absent Jupiter must never claim a lone catalog star shifted 3.1 px toward Jupiter inside the star's pointing window.");
        Assert.IsFalse(evidence.PointSourcePlanets.Associations.Any(static p => p.LabelEligible));
        var unmatchedPlanet = Assert.ContainsSingle(evidence.PointSourcePlanets.UnmatchedPredictions);
        Assert.AreEqual("solar-system:Jupiter", unmatchedPlanet.ObjectId);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.Ambiguous, unmatchedPlanet.ReasonCode);
        var unmatchedStar = Assert.ContainsSingle(evidence.UnmatchedPredictions);
        Assert.AreEqual("star:offset-mono16", unmatchedStar.CatalogId);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.OffsetMeasuredSource, unmatchedStar.ReasonCode);
        Assert.ContainsSingle(evidence.UnassociatedDetections);

        // Verify rendered annotation layers never draw a false "Jupiter" label.
        var starAnnotations = PresentationLayerProducers.FromProjectedSceneGroupsV2(
            scene,
            includeConstellations: false,
            includeImageCircle: false,
            includeCardinalDirections: false,
            associations: evidence,
            expectedPositionDiagnostics: false).StarAnnotations;
        Assert.IsFalse(starAnnotations.TextBlocks.Any(static b =>
            b.Lines.Any(static line => line.Contains("Jupiter", StringComparison.OrdinalIgnoreCase))),
            $"Scene annotation layer drew a false Jupiter label: [{string.Join(", ", starAnnotations.TextBlocks.Select(b => b.Lines[0]))}]");

        var measuredLayer = MeasuredAssociationPresentation.CreateMeasuredPayload(evidence, scene);
        Assert.IsFalse(measuredLayer.TextBlocks.Any(static b =>
            b.Lines.Any(static line => line.Contains("Jupiter", StringComparison.OrdinalIgnoreCase))),
            $"Measured presentation layer drew a false Jupiter label: [{string.Join(", ", measuredLayer.TextBlocks.Select(b => b.Lines[0]))}]");
        Assert.IsLessThan(2.0, double.Hypot(shiftedCentroid.X - jupiter.Pixel.X, shiftedCentroid.Y - jupiter.Pixel.Y));
    }

    [TestMethod]
    [DataRow(3000.0)]
    [DataRow(5000.0)]
    [DataRow(8532.0)]
    public async Task ProbeSweepAlongStarToPlanetLineNeverEmitsFalsePlanetLabelInsideCompetingStarWindow(double focalLength)
    {
        // Opus M1 probe sweep: offset 0 -> 3 sigma along the star->planet line at f=3000, f=5000, and f=8532.
        // Nowhere inside the competing star's declared pointing window may absent Jupiter become associated or LabelEligible.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var proj = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d,
            focalLength, focalLength, Width, Height, ProjectionAperture.Rectangular,
            BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var settings = new MeasuredStellarAssociationSettingsV1();
        var pointingSigmaRad = settings.EffectivePlanetPointingUncertaintyArcseconds * Math.PI / (180.0 * 3600.0);
        var starSigmaPx = double.Hypot(settings.PredictionSigmaPixels, focalLength * pointingSigmaRad);
        var starSeparationPx = Math.Max(4.6, Math.Round(settings.WindowSigmas * starSigmaPx + 0.4, 2));
        var starAltAz = ProjectorFactory.Create(proj).Unproject(new(Width / 2d + starSeparationPx, Height / 2d))!.Value;
        var starEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(starAltAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var visible = await new VisibleSceneBuilder(
                new InMemoryCelestialCatalog([new("star:sweep", "Sweep Star", starEq.RightAscensionHours, starEq.DeclinationDegrees, 2.0)]),
                null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, proj, new CatalogQuery(6.5, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
        var star = scene.Objects.Single(static o => o.Id == "star:sweep");
        var jupiter = scene.Objects.Single(static o => o.Id == "solar-system:Jupiter");

        // Sweep both at exact 4.6 px separation (where star pointing window overlaps Jupiter at f=5000/8532)
        // and at starSeparationPx (where positive control at Jupiter sits just outside the star's 3-sigma pointing window).
        foreach (var step in Enumerable.Range(0, 31))
        {
            var sigmaFraction = step * 0.1; // 0.0 sigma .. 3.0 sigma
            var shiftPx = sigmaFraction * starSigmaPx;
            var detectionPixel = new PixelPoint(star.Pixel.X - shiftPx, star.Pixel.Y);
            var detection = new StellarDetection(0, detectionPixel, 6000, 400, 1.2, 1.2, 1000, 5)
            {
                SignalToNoise = 80,
                CentroidCovariance = new(0.01, 0, 0.01)
            };
            var entries = MeasuredStellarAssociator.Associate(
                scene, [detection], [], measurementCompleted: true, settings, new(), CancellationToken.None);
            Assert.IsEmpty(entries.PointSourcePlanets!.Associations,
                $"f={focalLength}, shift={sigmaFraction:F1}σ ({shiftPx:F3} px): absent Jupiter must never steal detection inside star pointing window.");
        }

        // Positive control: a real Jupiter detection at Jupiter's own pixel (starSeparationPx > 3σ from star) matches cleanly.
        var jupiterDetection = new StellarDetection(0, jupiter.Pixel, 9000, 600, 1.2, 1.2, 1000, 5)
        {
            SignalToNoise = 95,
            CentroidCovariance = new(0.01, 0, 0.01)
        };
        var presentEntries = MeasuredStellarAssociator.Associate(
            scene, [jupiterDetection], [], measurementCompleted: true, settings, new(), CancellationToken.None);
        var presentPlanet = Assert.ContainsSingle(presentEntries.PointSourcePlanets!.Associations);
        Assert.IsTrue(presentPlanet.LabelEligible);

        // Also sweep with fixed 4.6 px star-planet separation (Astra/Opus narrow-field scenario).
        var closeAltAz = ProjectorFactory.Create(proj).Unproject(new(Width / 2d + 4.6, Height / 2d))!.Value;
        var closeEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(closeAltAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var closeVisible = await new VisibleSceneBuilder(
                new InMemoryCelestialCatalog([new("star:close46", "Close Star", closeEq.RightAscensionHours, closeEq.DeclinationDegrees, 2.0)]),
                null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, proj, new CatalogQuery(6.5, 100),
                new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1"),
                projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var closeScene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, closeVisible,
            ProjectedSceneImageTransformV1.Identity(Width, Height),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
        var closeStar = closeScene.Objects.Single(static o => o.Id == "star:close46");
        foreach (var step in Enumerable.Range(0, 31))
        {
            var sigmaFraction = step * 0.1;
            var shiftPx = sigmaFraction * starSigmaPx;
            var detectionPixel = new PixelPoint(closeStar.Pixel.X - shiftPx, closeStar.Pixel.Y);
            var detection = new StellarDetection(0, detectionPixel, 6000, 400, 1.2, 1.2, 1000, 5)
            {
                SignalToNoise = 80,
                CentroidCovariance = new(0.01, 0, 0.01)
            };
            var entries = MeasuredStellarAssociator.Associate(
                closeScene, [detection], [], measurementCompleted: true, settings, new(), CancellationToken.None);
            Assert.IsEmpty(entries.PointSourcePlanets!.Associations,
                $"f={focalLength} (4.6 px sep), shift={sigmaFraction:F1}σ ({shiftPx:F3} px): absent Jupiter must never claim detection.");
        }
    }

    [TestMethod]
    public async Task ExcludedBrightPlanetFootprintDoesNotClaimNearbyMoonOrStarInsideSearchWindow()
    {
        // Opus L1: when a saturated bright planet's predicted position is covered by an exclusion footprint
        // (e.g. saturated-excessive), the pre-match loop withholds the planet with measurement-excluded:<reason>
        // so it cannot claim a nearby uncataloged moon or star detection inside its search window.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var (scene, jupiter) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter, []).ConfigureAwait(false);
        var settings = new MeasuredStellarAssociationSettingsV1(
            PredictionSigmaPixels: 0.5, WindowSigmas: 3.0, MaximumWindowPixels: 4.0, OffsetRadiusPixels: 6.0);
        var nearbyMoonPixel = new PixelPoint(jupiter.Pixel.X + 2.5, jupiter.Pixel.Y);
        var moonDetection = new StellarDetection(0, nearbyMoonPixel, 2200, 180, 1.1, 1.1, 1000, 5)
        {
            SignalToNoise = 35,
            CentroidCovariance = new(0.02, 0, 0.02)
        };
        var px = (int)Math.Floor(jupiter.Pixel.X);
        var py = (int)Math.Floor(jupiter.Pixel.Y);
        var saturatedExclusion = new StellarMeasurementExclusion(
            jupiter.Pixel, 64_000, 49, StellarExclusionReasons.SaturatedExcessive)
        {
            Footprint = new StellarFootprintBounds(px - 2, py - 2, px + 2, py + 2)
        };

        var entries = MeasuredStellarAssociator.Associate(
            scene, [moonDetection], [saturatedExclusion], measurementCompleted: true,
            settings, new(), CancellationToken.None);

        Assert.IsNotNull(entries.PointSourcePlanets);
        Assert.IsEmpty(entries.PointSourcePlanets.Associations,
            "An excluded bright planet must never claim a nearby moon/star inside its search window.");
        var unmatched = Assert.ContainsSingle(entries.PointSourcePlanets.UnmatchedPredictions);
        Assert.AreEqual("solar-system:Jupiter", unmatched.ObjectId);
        Assert.AreEqual(
            MeasuredStellarAssociationReasonCodes.MeasurementExcludedPrefix + StellarExclusionReasons.SaturatedExcessive,
            unmatched.ReasonCode);
        var unassoc = Assert.ContainsSingle(entries.UnassociatedDetections);
        Assert.AreEqual(0, unassoc.DetectionIndex);
        Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NearIneligiblePrediction, unassoc.ReasonCode);

        var raw = RawArtifact(Field(Width, Height), CameraPixelFormat.Mono16, Width, Height);
        var evidence = MeasuredStellarAssociationRecipe.CreateEvidence(raw, raw.Layout!, CaptureId, DescriptorIdentity, scene,
            new(StellarSourceMeasurer.AlgorithmVersion, new string('2', 64), MeasuredStellarAssociationRecipe.MonoReconstruction,
                StellarMeasurementStatus.Completed, 1000, 5, 2, 1, 1),
            settings, new(), entries);
        Assert.IsTrue(MeasuredStellarAssociationJson.Validate(evidence).IsValid);
        var gated = StellarLabelPolicy.Apply(ProjectedSceneAnnotation.CreateObjects(scene, 6.5), evidence);
        Assert.IsFalse(gated.Single(static o => o.Id == "solar-system:Jupiter").DrawLabel);
    }

    [TestMethod]
    public async Task CrossSceneCompatibilityRejectsHorizontallyMirroredAndCoordinateMismatchedScenes()
    {
        // Astra D2: IsCompatibleWithScene and AssociationsBindScene must reject horizontally mirrored or
        // coordinate-mismatched scenes even when raw artifact, dimensions, and calibrationVersion agree.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var baseProj = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d, 500, 500, Width, Height,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees, HorizontalFlip: false);
        var mirroredProj = baseProj with { HorizontalFlip = true };
        var starAltAz = ProjectorFactory.Create(baseProj).Unproject(new(120, 80))!.Value;
        var starEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(starAltAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var star = new CelestialCatalogObject("star:mirror-test", "Mirror Star", starEq.RightAscensionHours, starEq.DeclinationDegrees, 1.5);
        var meta = new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1");

        var baseVisible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([star]), null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, baseProj, new CatalogQuery(6.5, 100), meta,
                projectionVersion: "calibration-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var renderVisible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([star]), null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, baseProj, new CatalogQuery(6.5, 100), meta,
                projectionVersion: "rig-projection-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var mirroredVisible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([star]), null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, mirroredProj, new CatalogQuery(6.5, 100), meta,
                projectionVersion: "rig-projection-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);

        var rawSamples = RenderSources(Width, Height, baseVisible.Objects.Select(static o => (o.Pixel, 6000d)).ToArray());
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
        var linear = ToArtifact(linearOutcome.Products.Single(), raw);
        var expectedInput = new ExposureMeasurementInputV1(
            new(CaptureId, raw.ArtifactId, DescriptorIdentity, ProcessingIdentity.ComputePayloadSha256(raw.Payload), raw.RecipeIdentitySha256, null, raw.Layout!),
            new(CaptureId, linear.ArtifactId, DescriptorIdentity, ProcessingIdentity.ComputePayloadSha256(linear.Payload), linear.RecipeIdentitySha256, linear.ContentIdentitySha256, linear.Layout!),
            ExposureCalibrationDisposition.NormalizationOnly, null, "orientation-v1",
            RigOrientation: new(baseProj.BoresightAltitudeDegrees, baseProj.BoresightAzimuthDegrees, baseProj.RollDegrees),
            ApertureProjection: baseProj, Exposure: new(new("agent", "cam"), Guid.NewGuid(), CaptureId),
            OpticalCalibrationVersion: "calibration-v1", Step3Disposition: LinearExposureDisposition.QualifiedNormalizationOnly, Step3Revision: 1);
        var step4 = await executor.ExecuteAsync(ExposureMeasurementProducts.Request(expectedInput, raw, linear)).ConfigureAwait(false);
        var step4Artifact = ToArtifact(step4.Products.Single(), raw);

        var source = new ProjectedSceneSource(CaptureId, raw.ArtifactId, DescriptorIdentity);
        var predScene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, baseVisible,
            ProjectedSceneImageTransformV1.Identity(Width, Height), source, "calibration-v1", "calibration-v1");
        var sceneOutcome = await executor.ExecuteAsync(ExposureCatalogPredictionProducts.Request(raw, predScene)).ConfigureAwait(false);
        var sceneArtifact = ToArtifact(sceneOutcome.Products.Single(), raw);
        var associated = ExposureAssociationProducts.Associate(raw, step4Artifact, expectedInput, sceneArtifact, ExposureAssociationProducts.Variant);
        var assocArtifact = ToArtifact(associated.Products.Single(), raw);
        var evidence = MeasuredStellarAssociationJson.Parse(assocArtifact.Payload).Associations!;

        // Genuine compatible VirtualRenderAuthoritative scene with matching pixel mapping succeeds.
        var compatibleRender = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, renderVisible,
            ProjectedSceneImageTransformV1.Identity(Width, Height), source, "calibration-v1", "rig-projection-v1");
        Assert.IsTrue(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, compatibleRender));

        // 1. Horizontally mirrored via Projection.HorizontalFlip = true (star at x=72 vs retained x=120): REJECTED.
        var flippedRender = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, mirroredVisible,
            ProjectedSceneImageTransformV1.Identity(Width, Height), source, "calibration-v1", "rig-projection-v1");
        Assert.IsGreaterThan(40.0, Math.Abs(flippedRender.Objects.Single(static o => o.Id == "star:mirror-test").Pixel.X - 120.0));
        Assert.IsFalse(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, flippedRender));
        Assert.ThrowsExactly<ArgumentException>(() => PresentationLayerProducers.FromProjectedSceneGroupsV2(
            flippedRender, associations: evidence, expectedPositionDiagnostics: false, allowCompatibleExposureAssociations: true));

        // 2. Horizontally mirrored via ImageTransform.HorizontalMirror = true: REJECTED.
        var mirroredTransform = ProjectedSceneImageTransformV1.Identity(Width, Height) with { HorizontalMirror = true };
        var mirroredTransformRender = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, renderVisible,
            mirroredTransform, source, "calibration-v1", "rig-projection-v1");
        Assert.IsFalse(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, mirroredTransformRender));

        // 3. Coordinate-shifted scene (star displaced by 3 px): REJECTED.
        var shiftedRender = compatibleRender with
        {
            Objects = compatibleRender.Objects.Select(o =>
                o.Id == "star:mirror-test" ? o with { Pixel = new(o.Pixel.X + 3.0, o.Pixel.Y) } : o).ToArray()
        };
        shiftedRender = shiftedRender with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(shiftedRender) };
        Assert.IsFalse(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, shiftedRender));
    }

    [TestMethod]
    public async Task CrossSceneCompatibilityRejectsSymmetryAxisFlipsAndRotationsWithSoleSharedObject()
    {
        // Astra D2 exact repro: 192x192 frame with Jupiter lying on the mirror/symmetry axis (96, 96) as the SOLE
        // shared object between the prediction scene and the render scene, plus a prediction-only star at (120, 80)
        // omitted from the render scene. Shared-object pixel agreement alone leaves Jupiter invariant at (96, 96),
        // so orientation/geometry identity must reject HorizontalFlip, VerticalMirror, projection vertical flip,
        // and 180-degree rotations before any diagnostic marker can be drawn at (120, 80).
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var baseProj = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d, 500, 500, Width, Height,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees, RollDegrees: 0, HorizontalFlip: false);
        var predOnlyAltAz = ProjectorFactory.Create(baseProj).Unproject(new(120, 80))!.Value;
        var predOnlyEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(predOnlyAltAz, ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees),
            ExposureMidpointUtc);
        var predOnlyStar = new CelestialCatalogObject("star:pred-only", "Pred Only Star",
            predOnlyEq.RightAscensionHours, predOnlyEq.DeclinationDegrees, 1.8);
        var meta = new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1");

        // Prediction scene has Jupiter on symmetry axis (96, 96) AND prediction-only star at (120, 80).
        var predVisible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([predOnlyStar]), null, ephemeris)
            .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, baseProj, new CatalogQuery(6.5, 100), meta,
                projectionVersion: "calibration-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
        var predStarObj = predVisible.Objects.Single(static o => o.Id == "star:pred-only");
        Assert.AreEqual(120.0, predStarObj.Pixel.X, 0.05);
        Assert.AreEqual(80.0, predStarObj.Pixel.Y, 0.05);

        // Render raw frame containing ONLY Jupiter (so star:pred-only is unmatched and would appear in expected-not-detected).
        var jupiterObj = predVisible.Objects.Single(static o => o.Id == "solar-system:Jupiter");
        Assert.AreEqual(Width / 2d, jupiterObj.Pixel.X, 0.05);
        Assert.AreEqual(Height / 2d, jupiterObj.Pixel.Y, 0.05);
        var rawSamples = RenderSources(Width, Height, [(jupiterObj.Pixel, 7000d)]);
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
        var linear = ToArtifact(linearOutcome.Products.Single(), raw);
        var expectedInput = new ExposureMeasurementInputV1(
            new(CaptureId, raw.ArtifactId, DescriptorIdentity, ProcessingIdentity.ComputePayloadSha256(raw.Payload), raw.RecipeIdentitySha256, null, raw.Layout!),
            new(CaptureId, linear.ArtifactId, DescriptorIdentity, ProcessingIdentity.ComputePayloadSha256(linear.Payload), linear.RecipeIdentitySha256, linear.ContentIdentitySha256, linear.Layout!),
            ExposureCalibrationDisposition.NormalizationOnly, null, "orientation-v1",
            RigOrientation: new(baseProj.BoresightAltitudeDegrees, baseProj.BoresightAzimuthDegrees, baseProj.RollDegrees),
            ApertureProjection: baseProj, Exposure: new(new("agent", "cam"), Guid.NewGuid(), CaptureId),
            OpticalCalibrationVersion: "calibration-v1", Step3Disposition: LinearExposureDisposition.QualifiedNormalizationOnly, Step3Revision: 1);
        var step4 = await executor.ExecuteAsync(ExposureMeasurementProducts.Request(expectedInput, raw, linear)).ConfigureAwait(false);
        var step4Artifact = ToArtifact(step4.Products.Single(), raw);

        var source = new ProjectedSceneSource(CaptureId, raw.ArtifactId, DescriptorIdentity);
        var predScene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, predVisible,
            ProjectedSceneImageTransformV1.Identity(Width, Height), source, "calibration-v1", "calibration-v1");
        var sceneOutcome = await executor.ExecuteAsync(ExposureCatalogPredictionProducts.Request(raw, predScene)).ConfigureAwait(false);
        var sceneArtifact = ToArtifact(sceneOutcome.Products.Single(), raw);
        var associated = ExposureAssociationProducts.Associate(raw, step4Artifact, expectedInput, sceneArtifact, ExposureAssociationProducts.Variant);
        var assocArtifact = ToArtifact(associated.Products.Single(), raw);
        var evidence = MeasuredStellarAssociationJson.Parse(assocArtifact.Payload).Associations!;
        Assert.ContainsSingle(evidence.PointSourcePlanets!.Associations);
        Assert.AreEqual("star:pred-only", Assert.ContainsSingle(evidence.UnmatchedPredictions).CatalogId);

        async Task<ProjectedSceneV1> BuildSoleJupiterRenderSceneAsync(
            ProjectionContext proj, ProjectedSceneImageTransformV1? transform = null)
        {
            var renderVisible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, ephemeris)
                .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, proj, new CatalogQuery(6.5, 100), meta,
                    projectionVersion: "rig-projection-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
            var renderScene = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, renderVisible,
                transform ?? ProjectedSceneImageTransformV1.Identity(Width, Height), source, "calibration-v1", "rig-projection-v1");
            // Confirm Jupiter is the SOLE object in renderScene AND remains invariant at (96, 96) on the symmetry axis.
            var soleObj = Assert.ContainsSingle(renderScene.Objects);
            Assert.AreEqual("solar-system:Jupiter", soleObj.Id);
            Assert.AreEqual(Width / 2d, soleObj.Pixel.X, 0.05);
            Assert.AreEqual(Height / 2d, soleObj.Pixel.Y, 0.05);
            return renderScene;
        }

        // Control: matching projection & orientation returns true and places the prediction-only star marker at (120, 80).
        var matchingControl = await BuildSoleJupiterRenderSceneAsync(baseProj).ConfigureAwait(false);
        Assert.IsTrue(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, matchingControl));
        var controlDiag = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(
            matchingControl, evidence, allowCompatibleExposureAssociations: true);
        var controlMarker = Assert.ContainsSingle(controlDiag.Markers);
        Assert.AreEqual(120.0, controlMarker.Center.X, 0.05);
        Assert.AreEqual(80.0, controlMarker.Center.Y, 0.05);

        // Symmetry-axis orientation mismatches where Jupiter stays invariant at (96, 96): ALL MUST FAIL CLOSED!
        var mismatchedScenes = new (string Label, ProjectedSceneV1 Scene)[]
        {
            ("Projection.HorizontalFlip=true", await BuildSoleJupiterRenderSceneAsync(baseProj with { HorizontalFlip = true }).ConfigureAwait(false)),
            ("ImageTransform.HorizontalMirror=true", await BuildSoleJupiterRenderSceneAsync(baseProj, ProjectedSceneImageTransformV1.Identity(Width, Height) with { HorizontalMirror = true }).ConfigureAwait(false)),
            ("ImageTransform.VerticalMirror=true", await BuildSoleJupiterRenderSceneAsync(baseProj, ProjectedSceneImageTransformV1.Identity(Width, Height) with { VerticalMirror = true }).ConfigureAwait(false)),
            ("Projection.VerticalFlip(HorizontalFlip+Roll180)", await BuildSoleJupiterRenderSceneAsync(baseProj with { HorizontalFlip = true, RollDegrees = 180 }).ConfigureAwait(false)),
            ("Projection.RollDegrees=180", await BuildSoleJupiterRenderSceneAsync(baseProj with { RollDegrees = 180 }).ConfigureAwait(false)),
            ("ImageTransform.Rotation=Degrees180", await BuildSoleJupiterRenderSceneAsync(baseProj, ProjectedSceneImageTransformV1.Identity(Width, Height) with { Rotation = ProjectedSceneQuarterRotation.Degrees180 }).ConfigureAwait(false))
        };

        foreach (var (label, badScene) in mismatchedScenes)
        {
            Assert.IsFalse(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, badScene),
                $"{label}: IsCompatibleWithScene must reject symmetry-axis orientation mismatch.");
            Assert.ThrowsExactly<ArgumentException>(() =>
                MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(
                    badScene, evidence, allowCompatibleExposureAssociations: true),
                $"{label}: diagnostic layer must refuse to draw prediction-only star at unmirrored (120, 80).");
            Assert.ThrowsExactly<ArgumentException>(() =>
                PresentationLayerProducers.FromProjectedSceneGroupsV2(
                    badScene, associations: evidence, expectedPositionDiagnostics: false,
                    allowCompatibleExposureAssociations: true),
                $"{label}: scene presentation layer must reject incompatible orientation.");
        }
    }

    [TestMethod]
    public async Task VerifyMeasurementRejectsTamperedUnclaimedCentroidAndFlux()
    {
        // Astra D6: ExposureAssociationProducts.VerifyMeasurement must verify unclaimed entries against step-4 detections.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var (scene, _) = await BuildPlanetSceneAsync(ephemeris, SolarSystemBody.Jupiter,
            [("star:ref1", 20, 14, 2.0)]).ConfigureAwait(false);
        var unclaimedPixel = new PixelPoint(Width / 2d - 34, Height / 2d + 28);
        var rawSamples = RenderSources(Width, Height,
            [.. scene.Objects.Select(static o => (o.Pixel, 6000d)), (unclaimedPixel, 5000d)]);
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
        var proj = ProjectedSceneJson.ToProjectionContext(scene.Projection);
        var executor = new ProcessingRecipeExecutor();
        var linearOutcome = await executor.ExecuteAsync(new ProcessingExecutionRequest(
            BuiltInProcessingRecipes.LinearNormalization,
            JsonSerializer.SerializeToElement(new LinearNormalizationOptions()),
            ProcessingInputSelector.Raw(raw.Variant), [raw], "qualified-linear-v1")).ConfigureAwait(false);
        var linear = ToArtifact(linearOutcome.Products.Single(), raw);
        var expectedInput = new ExposureMeasurementInputV1(
            new(CaptureId, raw.ArtifactId, DescriptorIdentity, ProcessingIdentity.ComputePayloadSha256(raw.Payload), raw.RecipeIdentitySha256, null, raw.Layout!),
            new(CaptureId, linear.ArtifactId, DescriptorIdentity, ProcessingIdentity.ComputePayloadSha256(linear.Payload), linear.RecipeIdentitySha256, linear.ContentIdentitySha256, linear.Layout!),
            ExposureCalibrationDisposition.NormalizationOnly, null, "orientation-v1",
            RigOrientation: new(proj.BoresightAltitudeDegrees, proj.BoresightAzimuthDegrees, proj.RollDegrees),
            ApertureProjection: proj, Exposure: new(new("agent", "cam"), Guid.NewGuid(), CaptureId),
            OpticalCalibrationVersion: "calibration-v1", Step3Disposition: LinearExposureDisposition.QualifiedNormalizationOnly, Step3Revision: 1);
        var step4 = await executor.ExecuteAsync(ExposureMeasurementProducts.Request(expectedInput, raw, linear)).ConfigureAwait(false);
        var step4Artifact = ToArtifact(step4.Products.Single(), raw);
        var predScene = scene with
        {
            Projection = scene.Projection with { AlgorithmVersion = "calibration-v1" },
            Objects = scene.Objects.Select(static o => o with { ProjectionVersion = "calibration-v1" }).ToArray(),
            Source = new(CaptureId, raw.ArtifactId, DescriptorIdentity)
        };
        predScene = predScene with { SceneIdentitySha256 = ProjectedSceneJson.ComputeIdentity(predScene) };
        var sceneOutcome = await executor.ExecuteAsync(ExposureCatalogPredictionProducts.Request(raw, predScene)).ConfigureAwait(false);
        var sceneArtifact = ToArtifact(sceneOutcome.Products.Single(), raw);
        var associated = ExposureAssociationProducts.Associate(raw, step4Artifact, expectedInput, sceneArtifact, ExposureAssociationProducts.Variant);
        var assocArtifact = ToArtifact(associated.Products.Single(), raw);
        var measurement = ExposureMeasurementProducts.Read(step4Artifact, expectedInput);
        var evidence = MeasuredStellarAssociationJson.Parse(assocArtifact.Payload).Associations!;
        var unclaimed = Assert.ContainsSingle(evidence.UnassociatedDetections);

        ExposureAssociationProducts.VerifyMeasurement(assocArtifact, raw, step4Artifact, measurement, sceneArtifact);

        foreach (var tampered in new[]
        {
            evidence with { UnassociatedDetections = [unclaimed with { X = unclaimed.X + 1.0 }] },
            evidence with { UnassociatedDetections = [unclaimed with { Y = unclaimed.Y - 1.0 }] },
            evidence with { UnassociatedDetections = [unclaimed with { Flux = unclaimed.Flux + 100.0 }] },
            evidence with { UnassociatedDetections = [unclaimed with { SignalToNoise = (unclaimed.SignalToNoise ?? 20) + 5.0 }] }
        })
        {
            var payload = MeasuredStellarAssociationJson.Serialize(tampered);
            var tamperedArtifact = assocArtifact with
            {
                Payload = payload,
                ContentIdentitySha256 = tampered.AssociationIdentitySha256
            };
            Assert.ThrowsExactly<InvalidDataException>(() =>
                ExposureAssociationProducts.VerifyMeasurement(tamperedArtifact, raw, step4Artifact, measurement, sceneArtifact));
        }
    }

    [TestMethod]
    public async Task PlanetWindowEdgeResidualsNeverFailSerializationAcrossFocalScalesAndSigmas()
    {
        // Opus L1: clamping residual against the persisted 6-decimal rounded propagated-sigma window guarantees
        // window-edge planet associations never fail MeasuredStellarAssociationJson.Serialize / Validate.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var position = ephemeris.GetPosition(SolarSystemBody.Jupiter, ExposureMidpointUtc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(position.EquatorialJ2000, ExposureMidpointUtc),
            ExposureMidpointUtc, Site.LatitudeDegrees, Site.LongitudeDegrees);
        var raw = RawArtifact(Field(Width, Height), CameraPixelFormat.Mono16, Width, Height);
        var catalogMeta = new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1");

        var matchedCount = 0;
        for (var i = 0; i < 24; i++)
        {
            var focal = 1500.0 + i * 287.3;
            var proj = new ProjectionContext(ProjectionModel.Perspective, Width / 2d, Height / 2d, focal, focal, Width, Height,
                ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
                BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
            var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([]), null, ephemeris)
                .BuildAsync(new VisibleSceneRequest(ExposureMidpointUtc, Site, proj, new CatalogQuery(6.5, 100), catalogMeta,
                    projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter])).ConfigureAwait(false);
            var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
                ProjectedSceneImageTransformV1.Identity(Width, Height),
                new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");
            var planet = scene.Objects.Single(static o => o.Id == "solar-system:Jupiter");

            foreach (var windowSigmas in new[] { 3.0, 5.0, 10.0 })
            {
                var settings = new MeasuredStellarAssociationSettingsV1(PredictionSigmaPixels: 0.5, WindowSigmas: windowSigmas, MaximumWindowPixels: 2.0);
                var angularSigmaRad = double.Hypot(30.0, 60.0) * Math.PI / (180.0 * 3600.0);
                var approxSigma = double.Hypot(0.5, focal * angularSigmaRad);
                var edgeDist = Math.Min(63.99, windowSigmas * approxSigma) - 2e-5;
                var detection = new StellarDetection(0, new(planet.Pixel.X + edgeDist, planet.Pixel.Y), 5000, 300, 1.2, 1.2, 1000, 5)
                {
                    SignalToNoise = 50,
                    CentroidCovariance = new(0.05, 0, 0.05)
                };
                var entries = MeasuredStellarAssociator.Associate(scene, [detection], [], true, settings, new(), CancellationToken.None);
                var evidence = MeasuredStellarAssociationRecipe.CreateEvidence(raw, raw.Layout!, CaptureId, DescriptorIdentity, scene,
                    new(StellarSourceMeasurer.AlgorithmVersion, new string('2', 64), "mono-linear-v1",
                        StellarMeasurementStatus.Completed, 1000, 5, 1, 1, 0),
                    settings, new(), entries);
                var bytes = MeasuredStellarAssociationJson.Serialize(evidence);
                var parsed = MeasuredStellarAssociationJson.Parse(bytes);
                Assert.IsTrue(parsed.Validation.IsValid,
                    $"focal={focal}, windowSigmas={windowSigmas}: {parsed.Validation.ReasonCode} at {parsed.Validation.FieldPath}");
                if (evidence.PointSourcePlanets!.Associations.Count == 1)
                    matchedCount++;
            }
        }
        Assert.IsGreaterThan(24, matchedCount, "Boundary sweep must exercise window-edge planet associations.");
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
