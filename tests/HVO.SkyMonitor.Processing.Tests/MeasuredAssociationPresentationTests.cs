using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.Imaging;
using static HVO.SkyMonitor.Processing.Tests.MeasuredStellarAssociationFixtures;

namespace HVO.SkyMonitor.Processing.Tests;

/// <summary>
/// #526 presentation layers derived solely from analytical association metadata: pre-solve measured markers/labels
/// (including unclaimed centroids without catalog identity) and separately selectable Expected, not detected diagnostics.
/// </summary>
[TestClass]
[TestCategory("Unit")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making type internal", Justification = "MSTest requires public test classes.")]
public sealed class MeasuredAssociationPresentationTests
{
    [TestMethod]
    public async Task MeasuredAndExpectedNotDetectedLayersSeparateQualifiedDetectionsUnclaimedCentroidsAndUnmatchedPredictions()
    {
        var scene = await StarFieldAsync(
            ("star:eligible", 0, 0, 1.5),
            ("star:low-snr", 6, 4, 2.2),
            ("star:absent", -6, -4, 2.0)).ConfigureAwait(false);
        var baseline = AllEligible(scene);
        var eligible = baseline.Associations.Single(static a => a.CatalogId == "star:eligible") with { DetectionIndex = 0 };
        var lowSnr = baseline.Associations.Single(static a => a.CatalogId == "star:low-snr") with
        {
            DetectionIndex = 1,
            SignalToNoise = 4.0,
            LabelEligible = false,
            LabelRejectionReason = MeasuredStellarAssociationReasonCodes.LabelLowSignalToNoise
        };
        var absent = baseline.Associations.Single(static a => a.CatalogId == "star:absent");
        var evidence = baseline with
        {
            Measurement = baseline.Measurement with { CandidateCount = 4, DetectionCount = 3 },
            Associations = [eligible, lowSnr],
            UnmatchedPredictions =
            [
                new(absent.CatalogId, absent.Magnitude, absent.ExpectedX, absent.ExpectedY,
                    MeasuredStellarAssociationReasonCodes.NoMeasuredSource)
            ],
            UnassociatedDetections =
            [
                new(2, 20.5, 22.5, 1800, 28, MeasuredStellarAssociationReasonCodes.NoCatalogPrediction)
            ]
        };

        var measured = MeasuredAssociationPresentation.CreateMeasuredPayload(evidence, scene);
        var expectedNotDetected = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(evidence, scene);

        // Measured layer marks eligible, ineligible, and unclaimed centroid (3 markers), but labels ONLY eligible.
        Assert.HasCount(3, measured.Markers);
        Assert.IsTrue(measured.Markers.Any(static m => m.Crosshair && Math.Abs(m.Center.X - 20.5) < 1e-6));
        var measuredLabel = Assert.ContainsSingle(measured.TextBlocks);
        Assert.IsFalse(measuredLabel.Lines[0].Contains("(expected)", StringComparison.Ordinal));
        Assert.AreEqual(evidence.AssociationIdentitySha256, measured.SourceIdentitySha256);

        // Expected, not detected marks both absent and low-SNR (ineligible) predictions with "(expected)" labels.
        Assert.HasCount(2, expectedNotDetected.Markers);
        Assert.HasCount(2, expectedNotDetected.TextBlocks);
        Assert.IsTrue(expectedNotDetected.TextBlocks.All(static block =>
            block.Lines[0].EndsWith(AnnotationLabelFormatter.ExpectedSuffix, StringComparison.Ordinal)));
        Assert.IsFalse(expectedNotDetected.TextBlocks.Any(static block =>
            block.Lines[0].Contains("eligible", StringComparison.OrdinalIgnoreCase)));

        // Canonical JSON round-trip preserves exact content identity for both layers.
        foreach (var payload in new[] { measured, expectedNotDetected })
        {
            var bytes = PresentationLayerPayloadJson.Serialize(payload);
            var reparsed = PresentationLayerPayloadJson.Parse(bytes);
            Assert.IsTrue(reparsed.IsValid, reparsed.ErrorPath);
            Assert.AreEqual(payload.ContentIdentitySha256, reparsed.Payload!.ContentIdentitySha256);
        }
    }

    [TestMethod]
    public async Task SceneOnlyExpectedNotDetectedLabelsAllClearPredictionsAsExpectedAndExcludesOutsideAperture()
    {
        var scene = await StarFieldAsync(("star:vega", 0, 0, 1.0), ("star:deneb", 6, -4, 1.5)).ConfigureAwait(false);

        var withoutMeasured = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(scene, associations: null);
        Assert.HasCount(2, withoutMeasured.Markers);
        Assert.HasCount(2, withoutMeasured.TextBlocks);
        Assert.IsTrue(withoutMeasured.TextBlocks.All(static b =>
            b.Lines[0].EndsWith(AnnotationLabelFormatter.ExpectedSuffix, StringComparison.Ordinal)));

        var allMatched = AllEligible(scene);
        var withAllMatched = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(scene, allMatched);
        Assert.IsEmpty(withAllMatched.Markers);
        Assert.IsEmpty(withAllMatched.TextBlocks);

        var outsideOnly = allMatched with
        {
            Measurement = allMatched.Measurement with { DetectionCount = 1 },
            Associations = [allMatched.Associations[0]],
            UnmatchedPredictions =
            [
                new(allMatched.Associations[1].CatalogId, allMatched.Associations[1].Magnitude,
                    allMatched.Associations[1].ExpectedX, allMatched.Associations[1].ExpectedY,
                    MeasuredStellarAssociationReasonCodes.OutsideAperture)
            ],
            UnassociatedDetections = []
        };
        var outsideLayer = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(outsideOnly, scene);
        Assert.IsEmpty(outsideLayer.Markers);
        Assert.IsEmpty(outsideLayer.TextBlocks);
    }

    [TestMethod]
    public async Task ClassifyEmptyExpectedLayerReasonDistinguishesAllMatchedFilteredAndNoPredictions()
    {
        var scene = await StarFieldAsync(("star:bright", 0, 0, 1.5), ("star:faint", 8, -6, 7.8)).ConfigureAwait(false);
        var allMatched = AllEligible(scene);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedAllMatchedReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(allMatched));

        // Faint unmatched prediction (> 6.5 mag) produces zero drawable markers, yet must NOT claim all matched.
        var faintUnmatched = allMatched with
        {
            Measurement = allMatched.Measurement with { DetectionCount = 1 },
            Associations = [allMatched.Associations.Single(static a => a.CatalogId == "star:bright")],
            UnmatchedPredictions =
            [
                new("star:faint", 7.8,
                    allMatched.Associations.Single(static a => a.CatalogId == "star:faint").ExpectedX,
                    allMatched.Associations.Single(static a => a.CatalogId == "star:faint").ExpectedY,
                    MeasuredStellarAssociationReasonCodes.NoMeasuredSource)
            ],
            UnassociatedDetections = []
        };
        var filteredLayer = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(faintUnmatched, scene);
        Assert.IsEmpty(filteredLayer.Markers);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedFilteredReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(faintUnmatched));

        // Outside-aperture unmatched prediction also produces zero drawable markers and must NOT claim all matched.
        var outsideAperture = faintUnmatched with
        {
            UnmatchedPredictions = [faintUnmatched.UnmatchedPredictions[0] with { Magnitude = 2.0, ReasonCode = MeasuredStellarAssociationReasonCodes.OutsideAperture }]
        };
        Assert.IsEmpty(MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(outsideAperture, scene).Markers);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedFilteredReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(outsideAperture));

        // Empty scene with zero predictions reports no-catalog-predictions.
        var emptyScene = await StarFieldAsync().ConfigureAwait(false);
        var emptyAssoc = AllEligible(emptyScene);
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedNoPredictionsReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(emptyAssoc));
    }

    [TestMethod]
    public async Task CompatibleRenderSceneExpectedNotDetectedPreservesUnmatchedObjectsAbsentFromRenderScene()
    {
        // Astra D3: when a compatible VirtualRenderAuthoritative render scene omits a catalog object (e.g. Mars or an
        // absent star) that is present in the bound prediction/association evidence, the diagnostic MUST still emit it.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var utc = new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
        var site = new ObserverLocation(35.347, -113.878, 520);
        var jupiterPos = ephemeris.GetPosition(SolarSystemBody.Jupiter, utc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(jupiterPos.EquatorialJ2000, utc), utc, site.LatitudeDegrees, site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, 96, 96, 500, 500, 192, 192,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var projector = ProjectorFactory.Create(projection);
        CelestialCatalogObject MakeStar(string id, string name, double dx, double dy, double mag)
        {
            var altAz = projector.Unproject(new(96 + dx, 96 + dy))!.Value;
            var eq = EquatorialPrecession.PrecessToJ2000(
                CoordinateTransforms.HorizontalToEquatorial(altAz, utc, site.LatitudeDegrees, site.LongitudeDegrees), utc);
            return new(id, name, eq.RightAscensionHours, eq.DeclinationDegrees, mag);
        }
        var matchedStar = MakeStar("star:matched", "Matched Star", 20, 12, 1.5);
        var absentStar = MakeStar("star:absent", "Absent Star", -26, 18, 2.0);
        var unnamedPresentStar = MakeStar("HIP 99001", "HIP 99001", 34, -22, 2.5);
        var unnamedMissingStar = MakeStar("HIP 99002", "HIP 99002", -34, -30, 2.6);
        var marsAltAz = projector.Unproject(new(96 - 24, 96 - 22))!.Value;
        var marsEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(marsAltAz, utc, site.LatitudeDegrees, site.LongitudeDegrees), utc);
        var predEphemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Mars] = new(marsEq, 0.4)
        }, ephemeris.ModelVersion);
        var renderEphemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>(), ephemeris.ModelVersion);
        var catalogMeta = new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1");

        var predVisible = await new VisibleSceneBuilder(
                new InMemoryCelestialCatalog([matchedStar, absentStar, unnamedPresentStar, unnamedMissingStar]), null, predEphemeris)
            .BuildAsync(new VisibleSceneRequest(utc, site, projection, new CatalogQuery(6.5, 100), catalogMeta,
                projectionVersion: "calibration-v1", solarSystemBodies: [SolarSystemBody.Mars])).ConfigureAwait(false);
        var predScene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, predVisible,
            ProjectedSceneImageTransformV1.Identity(192, 192),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "calibration-v1");

        // Render scene contains matchedStar and unnamedPresentStar (omitting Mars, absentStar, and unnamedMissingStar).
        var renderVisible = await new VisibleSceneBuilder(
                new InMemoryCelestialCatalog([matchedStar, unnamedPresentStar]), null, renderEphemeris)
            .BuildAsync(new VisibleSceneRequest(utc, site, projection, new CatalogQuery(6.5, 100), catalogMeta,
                projectionVersion: "rig-projection-v1")).ConfigureAwait(false);
        var renderScene = ProjectedSceneJson.Create(ProjectedSceneKind.VirtualRenderAuthoritative, renderVisible,
            ProjectedSceneImageTransformV1.Identity(192, 192),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "rig-projection-v1");

        Assert.IsFalse(renderScene.Objects.Any(static o => o.Id == "solar-system:Mars"));
        Assert.IsFalse(renderScene.Objects.Any(static o => o.Id == "star:absent"));
        Assert.IsFalse(renderScene.Objects.Any(static o => o.Id == "HIP 99002"));

        // Run real association on an image containing ONLY matchedStar.
        var samples = Field(192, 192);
        AddNoise(samples, 5263, 2.0);
        AddGaussian(samples, predScene.Objects.Single(static o => o.Id == "star:matched").Pixel, 1.25, 15_000, 192);
        var raw = RawArtifact(samples, HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, 192, 192);
        var outcome = await new ProcessingRecipeExecutor().ExecuteAsync(Request(raw, predScene)).ConfigureAwait(false);
        Assert.AreEqual(ProcessingOutcomeStatus.Produced, outcome.Status, outcome.ReasonCode);
        var evidence = MeasuredStellarAssociationJson.Parse(outcome.Products.Single().Payload).Associations!;

        var exactDiag = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(evidence, predScene);
        var compatibleDiag = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(
            renderScene, evidence, allowCompatibleExposureAssociations: true);

        // All 4 unconfirmed objects (Mars, absentStar, unnamedPresentStar, unnamedMissingStar) retain markers in both.
        Assert.HasCount(4, exactDiag.Markers);
        Assert.HasCount(4, compatibleDiag.Markers, "Compatible render scene must NOT drop Mars or absent stars missing from scene.Objects.");
        Assert.IsTrue(exactDiag.TextBlocks.Any(static b => b.Lines[0] == "Mars (expected)"));
        Assert.IsTrue(exactDiag.TextBlocks.Any(static b => b.Lines[0] == "Absent Star (expected)"));
        Assert.HasCount(2, exactDiag.TextBlocks, "Unnamed stars must not emit raw catalog ID labels.");

        // Opus L3: stars missing from the render scene or unnamed in the scene never fall back to raw catalog IDs,
        // while planet diagnostics still carry their body name ("Mars (expected)").
        var compatLabel = Assert.ContainsSingle(compatibleDiag.TextBlocks,
            $"Unexpected compatible diagnostic labels: [{string.Join(", ", compatibleDiag.TextBlocks.Select(b => b.Lines[0]))}]");
        Assert.AreEqual("Mars (expected)", compatLabel.Lines[0]);
        Assert.IsFalse(compatibleDiag.TextBlocks.Any(static b =>
            b.Lines[0].Contains("star:", StringComparison.OrdinalIgnoreCase) ||
            b.Lines[0].Contains("HIP ", StringComparison.OrdinalIgnoreCase)));

        // Measured layer with no scene also suppresses raw catalog ID labels while preserving markers.
        var measuredWithoutScene = MeasuredAssociationPresentation.CreateMeasuredPayload(evidence, scene: null);
        Assert.ContainsSingle(measuredWithoutScene.Markers);
        Assert.IsEmpty(measuredWithoutScene.TextBlocks);
    }

    [TestMethod]
    public async Task RetainedV1StarOnlyAssociationsWithScenePlanetsNeverClaimsAllPredictionsMatched()
    {
        // Astra D4: a retained v1 star-only association product with all stars matched, paired with a scene containing
        // Jupiter and Mars, must surface unassessed planets and must NEVER report expected.all-predictions-matched.
        var ephemeris = new AstronomyEnginePlanetEphemeris();
        var utc = new DateTimeOffset(2026, 1, 15, 6, 0, 0, TimeSpan.Zero);
        var site = new ObserverLocation(35.347, -113.878, 520);
        var jupiterPos = ephemeris.GetPosition(SolarSystemBody.Jupiter, utc);
        var jupiterAltAz = CoordinateTransforms.EquatorialToHorizontal(
            EquatorialPrecession.PrecessJ2000(jupiterPos.EquatorialJ2000, utc), utc, site.LatitudeDegrees, site.LongitudeDegrees);
        var projection = new ProjectionContext(ProjectionModel.Perspective, 96, 96, 500, 500, 192, 192,
            ProjectionAperture.Rectangular, BoresightAltitudeDegrees: jupiterAltAz.AltitudeDegrees,
            BoresightAzimuthDegrees: jupiterAltAz.AzimuthDegrees);
        var projector = ProjectorFactory.Create(projection);
        var starAltAz = projector.Unproject(new(124, 114))!.Value;
        var starEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(starAltAz, utc, site.LatitudeDegrees, site.LongitudeDegrees), utc);
        var marsAltAz = projector.Unproject(new(68, 74))!.Value;
        var marsEq = EquatorialPrecession.PrecessToJ2000(
            CoordinateTransforms.HorizontalToEquatorial(marsAltAz, utc, site.LatitudeDegrees, site.LongitudeDegrees), utc);
        var fixedEphem = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = jupiterPos,
            [SolarSystemBody.Mars] = new(marsEq, 0.5)
        }, ephemeris.ModelVersion);
        var star = new CelestialCatalogObject("star:v1-matched", "V1 Matched", starEq.RightAscensionHours, starEq.DeclinationDegrees, 1.4);
        var meta = new CatalogMetadata("fixture", "1", new Uri("https://example.test/catalog"), new string('C', 64), "test", "v1");
        var visibleWithPlanets = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([star]), null, fixedEphem)
            .BuildAsync(new VisibleSceneRequest(utc, site, projection, new CatalogQuery(6.5, 100), meta,
                projectionVersion: "perspective-v1", solarSystemBodies: [SolarSystemBody.Jupiter, SolarSystemBody.Mars])).ConfigureAwait(false);
        var sceneWithPlanets = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visibleWithPlanets,
            ProjectedSceneImageTransformV1.Identity(192, 192),
            new ProjectedSceneSource(CaptureId, ArtifactId, DescriptorIdentity), "calibration-v1", "perspective-v1");

        var starObj = sceneWithPlanets.Objects.Single(static o => o.Id == "star:v1-matched");
        var v1StarOnly = new MeasuredStellarAssociationsV1(
            MeasuredStellarAssociationsV1.V1SchemaVersion,
            new(CaptureId, ArtifactId, DescriptorIdentity, HVO.SkyMonitor.AgentCore.FrameArtifactRole.Raw, "raw",
                new string('1', 64), HVO.SkyMonitor.AgentCore.CameraPixelFormat.Mono16, 192, 192),
            new(sceneWithPlanets.SceneIdentitySha256, sceneWithPlanets.Kind, sceneWithPlanets.Catalog.Name,
                sceneWithPlanets.Catalog.Version, sceneWithPlanets.Catalog.ChecksumSha256,
                sceneWithPlanets.Projection.CalibrationVersion, sceneWithPlanets.Projection.AlgorithmVersion,
                sceneWithPlanets.AstronomyAlgorithmVersion),
            new(StellarSourceMeasurer.AlgorithmVersion, new string('2', 64), "mono-linear-v1",
                StellarMeasurementStatus.Completed, 1000, 5, 1, 1, 0),
            new(MeasuredStellarAssociator.V1AlgorithmVersion, new(), StellarLabelPolicy.V1Version, new()),
            [new(starObj.Id, 0, starObj.Magnitude, starObj.Pixel.X, starObj.Pixel.Y, starObj.Pixel.X, starObj.Pixel.Y,
                0, 0, 5000, 50, 0.01, 0, 0.01, false, 0, false, null, null, true, null)],
            [],
            [],
            PointSourcePlanets: null);
        Assert.IsTrue(MeasuredStellarAssociationJson.Validate(v1StarOnly).IsValid);

        // Neither overload may claim expected.all-predictions-matched when v1 did not assess planets.
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedPlanetsUnassessedReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(v1StarOnly));
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedPlanetsUnassessedReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(v1StarOnly, sceneWithPlanets));

        // Expected-not-detected layers surface unassessed Jupiter and Mars as expected diagnostics.
        var fromAssoc = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(v1StarOnly, sceneWithPlanets);
        var fromScene = MeasuredAssociationPresentation.CreateExpectedNotDetectedPayload(sceneWithPlanets, v1StarOnly);
        foreach (var layer in new[] { fromAssoc, fromScene })
        {
            Assert.HasCount(2, layer.Markers);
            Assert.IsTrue(layer.TextBlocks.Any(static b => b.Lines[0] == "Jupiter (expected)"));
            Assert.IsTrue(layer.TextBlocks.Any(static b => b.Lines[0] == "Mars (expected)"));
        }

        // When bound to a genuinely star-only scene without planets, v1 may truthfully report all-predictions-matched.
        var starOnlyScene = await StarFieldAsync(("star:only", 0, 0, 1.5)).ConfigureAwait(false);
        var starOnlyV2 = AllEligible(starOnlyScene);
        var v1AllStarOnly = starOnlyV2 with
        {
            SchemaVersion = MeasuredStellarAssociationsV1.V1SchemaVersion,
            Scene = starOnlyV2.Scene with { ProjectionGeometryIdentitySha256 = null },
            Method = new(MeasuredStellarAssociator.V1AlgorithmVersion, new(), StellarLabelPolicy.V1Version, new()),
            PointSourcePlanets = null
        };
        Assert.AreEqual(MeasuredAssociationPresentation.EmptyExpectedAllMatchedReason,
            MeasuredAssociationPresentation.ClassifyEmptyExpectedLayerReason(v1AllStarOnly, starOnlyScene));
    }
}
