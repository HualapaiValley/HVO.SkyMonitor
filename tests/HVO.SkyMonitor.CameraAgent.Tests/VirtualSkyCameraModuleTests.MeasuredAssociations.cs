using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Tests.Contracts;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly string[] JupiterBodyNames = ["Jupiter"];
    private static readonly string[] ExpectedScoringStarIds = ["star:s1", "star:s2", "star:s3"];

    [TestMethod]
    public async Task CenteredPreSolveMeasuredAssociationsPublishStarsPlanetsUnclaimedCentroidsAndExpectedDiagnosticAcrossReopenAndReplay()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-526");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);

        var location = profile.DeploymentLocation!;
        var ra = AstronomyTime.LocalMeanSiderealDegrees(start, location.LongitudeDegrees) / 15d;
        var matchedStar = new CelestialCatalogObject("star:matched", "Matched Star", ra, location.LatitudeDegrees, 0.2, 0.65);
        var absentStar = new CelestialCatalogObject("star:absent", "Absent Star", (ra + 1.5) % 24, location.LatitudeDegrees + 15, 0.6, 0.65);
        var unclaimedStar = new CelestialCatalogObject("star:unclaimed", "Unclaimed Source", (ra - 1.5 + 24) % 24, location.LatitudeDegrees - 15, 0.3, 0.65);
        var jupiterPos = new SolarSystemPosition(new EquatorialPoint((ra + 1.5) % 24, location.LatitudeDegrees - 15), -1.8);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = jupiterPos
        }, "centered-526-ephemeris-v1");

        // Prediction catalog knows matchedStar + absentStar + Jupiter; VirtualSky renders matchedStar + unclaimedStar + Jupiter.
        var predictionCatalog = new TestMetadataCatalog([matchedStar, absentStar]);
        var renderCatalog = new InMemoryCelestialCatalog([matchedStar, unclaimedStar]);

        var existingScene = profile.Pipeline.Steps.Single(static step => step.Type == "ProjectedScene");
        var sceneWithPlanet = existingScene with
        {
            Options = JsonSerializer.SerializeToElement(new ProjectedSceneCaptureProcessingStepOptions
            {
                MaximumMagnitude = 6.5,
                MaximumResults = 2000,
                SolarSystemBodies = [SolarSystemBody.Jupiter]
            })
        };
        profile = profile with
        {
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 526,
                illuminationMode = "ControlledNight",
                magnitudeZeroElectronsPerSecond = 3000,
                backgroundElectronsPerSecond = 25,
                readNoiseStandardDeviation = 4.0,
                shotNoiseEnabled = true,
                solarSystemBodies = JupiterBodyNames
            })),
            Pipeline = profile.Pipeline with
            {
                Steps =
                [
                    .. profile.Pipeline.Steps.Where(static step => step.Type != "ProjectedScene"),
                    sceneWithPlanet,
                    // Pre-solve only: deliberately omit MeasuredAstrometry so we verify measured markers publish before any solve.
                    new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable)
                ]
            }
        };

        try
        {
            CenteredViewArchiveBase archived;
            Guid captureId;
            string initialRevisionId;
            string initialPresentationId;
            byte[] initialSvg;
            byte[] originalAssocPayload;
            string originalAssocContentId;
            string originalAssocOutputId;
            ProcessingGraphExecutionState liveExecution;
            var locationSeed = new DeploymentLocationSeed(
                location.LocationId, location.Source, null,
                location.EffectiveFromUtc, location.EffectiveUntilUtc,
                new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, location.TimeZoneId));
            using (var provider = CenteredRuntimeProvider(root, clock, services =>
            {
                services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                services.AddSingleton<ICelestialCatalog>(predictionCatalog);
                services.AddSingleton<IPlanetEphemeris>(ephemeris);
            }))
            {
                await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var initializedLocation = await provider.GetRequiredService<IDeploymentLocationStore>()
                    .InitializeAsync(locationSeed, token).ConfigureAwait(false);
                profile = profile with { DeploymentLocation = initializedLocation };
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, renderCatalog, new ProjectedSceneStore(),
                    planetEphemeris: ephemeris,
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
                var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
                await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "centered-526", token).ConfigureAwait(false);
                var request = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
                {
                    BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                    BoundGraph = reservation.Revision.Pipeline
                };
                var captured = await module.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
                Assert.IsNotNull(captured.Frame);
                clock.UtcNow = captured.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
                var receipt = await ingress.AcceptCenteredAsync(active,
                    new(request, captured, start, request.TargetInterval, request.TargetInterval),
                    reservation, token).ConfigureAwait(false);
                captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

                var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, captureId);
                var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product);
                var stream = new CenteredViewStream(key.Camera, key.RunId, reservation.Revision.State.RevisionId);
                var initial = await provider.GetRequiredService<ICenteredViewProducer>().PublishSingleAsync(stream,
                    linear.Product.Slot.ExecutionId, receipt.Manifest.Descriptor.Capture.CaptureSequence, linear,
                    new(), token).ConfigureAwait(false);
                Assert.IsNotNull(initial);

                var view = await DrainCenteredReferenceAsync(provider, key, token).ConfigureAwait(false);
                var layers = view.Revision.Layers.ToDictionary(layer => layer.Key.Kind);

                // Astrometry is NotConfigured, proving measured catalog associations publish BEFORE any solve.
                Assert.AreEqual(CenteredViewReadiness.NotConfigured, layers[AstrometricDiagnosticPresentation.ResidualsLayer].Readiness);

                var measuredLayer = layers[MeasuredAssociationPresentation.MeasuredLayerKind];
                Assert.AreEqual(CenteredViewReadiness.Ready, measuredLayer.Readiness, measuredLayer.Reason);
                Assert.IsNotNull(measuredLayer.Drawable);
                Assert.IsTrue(measuredLayer.Drawable.EnabledByDefault);

                var expectedLayer = layers[MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind];
                Assert.AreEqual(CenteredViewReadiness.Ready, expectedLayer.Readiness, expectedLayer.Reason);
                Assert.IsNotNull(expectedLayer.Drawable);
                Assert.IsFalse(expectedLayer.Drawable.EnabledByDefault);

                var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var detail = await store.ReadExecutionDetailAsync(linear.Product.Slot.ExecutionId, token).ConfigureAwait(false);
                Assert.IsNotNull(detail);
                liveExecution = detail.Execution;
                var assocNode = detail.Nodes.Single(static n => n.NodeId == "associated");
                var assocOutput = Assert.ContainsSingle(assocNode.Outputs);
                originalAssocOutputId = assocOutput.OutputIdentitySha256;
                var assocArtifact = await persistence.ReadCenteredViewParentAsync(captureId, assocOutput.ArtifactId, token).ConfigureAwait(false);
                originalAssocContentId = assocArtifact.ContentIdentitySha256!;
                originalAssocPayload = assocArtifact.Payload.ToArray();
                var evidence = MeasuredStellarAssociationJson.Parse(originalAssocPayload).Associations!;
                var diag = $"stars=[{string.Join(";", evidence.Associations.Select(a => $"{a.CatalogId}:eligible={a.LabelEligible}/{a.LabelRejectionReason}/r={a.ResidualPixels:F2}/snr={a.SignalToNoise:F1}/sat={a.Saturated}/tr={a.Trailed}"))}] " +
                    $"unmatched=[{string.Join(";", evidence.UnmatchedPredictions.Select(u => $"{u.CatalogId}:{u.ReasonCode}@({u.ExpectedX:F1},{u.ExpectedY:F1})"))}] " +
                    $"planets=[{string.Join(";", (evidence.PointSourcePlanets?.Associations ?? []).Select(p => $"{p.ObjectId}:eligible={p.LabelEligible}/{p.LabelRejectionReason}/r={p.ResidualPixels:F2}/snr={p.SignalToNoise:F1}/sat={p.Saturated}"))}] " +
                    $"unmatchedPlanets=[{string.Join(";", (evidence.PointSourcePlanets?.UnmatchedPredictions ?? []).Select(u => $"{u.ObjectId}:{u.ReasonCode}@({u.ExpectedX:F1},{u.ExpectedY:F1})"))}] " +
                    $"unassoc=[{string.Join(";", evidence.UnassociatedDetections.Select(d => $"({d.X:F1},{d.Y:F1}):{d.ReasonCode}"))}]";

                Assert.AreEqual("star:matched", Assert.ContainsSingle(evidence.Associations, diag).CatalogId, diag);
                Assert.AreEqual("star:absent", Assert.ContainsSingle(evidence.UnmatchedPredictions, diag).CatalogId, diag);
                Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NoMeasuredSource, evidence.UnmatchedPredictions[0].ReasonCode, diag);
                Assert.IsNotNull(evidence.PointSourcePlanets, diag);
                Assert.AreEqual(SolarSystemBody.Jupiter, Assert.ContainsSingle(evidence.PointSourcePlanets.Associations, diag).Body, diag);
                Assert.IsNotEmpty(evidence.UnassociatedDetections, diag);

                var measuredPayloadArtifact = await persistence.ReadCenteredViewParentAsync(
                    captureId, measuredLayer.Drawable.SourceProduct.ArtifactId, token).ConfigureAwait(false);
                var measuredPayload = PresentationLayerPayloadJson.Parse(measuredPayloadArtifact.Payload).Payload!;
                var measuredLabels = string.Join(", ", measuredPayload.TextBlocks.Select(static b => b.Lines[0]));
                Assert.IsGreaterThanOrEqualTo(3, measuredPayload.Markers.Count,
                    $"Expected markers for measured star, measured Jupiter, and unclaimed centroid. markers={measuredPayload.Markers.Count}, labels=[{measuredLabels}], {diag}");
                Assert.IsTrue(measuredPayload.TextBlocks.Any(static b => b.Lines[0] == "Matched Star"),
                    $"Missing Matched Star label. labels=[{measuredLabels}], {diag}");
                Assert.IsTrue(measuredPayload.TextBlocks.Any(static b => b.Lines[0] == "Jupiter"),
                    $"Missing Jupiter label. labels=[{measuredLabels}], {diag}");
                Assert.IsFalse(measuredPayload.TextBlocks.Any(static b => b.Lines[0].Contains("Absent Star", StringComparison.Ordinal)));
                Assert.IsFalse(measuredPayload.TextBlocks.Any(static b => b.Lines[0].Contains("Unclaimed", StringComparison.Ordinal)));

                var expectedPayloadArtifact = await persistence.ReadCenteredViewParentAsync(
                    captureId, expectedLayer.Drawable.SourceProduct.ArtifactId, token).ConfigureAwait(false);
                var expectedPayload = PresentationLayerPayloadJson.Parse(expectedPayloadArtifact.Payload).Payload!;
                var expectedLabels = string.Join(", ", expectedPayload.TextBlocks.Select(static b => b.Lines[0]));
                Assert.ContainsSingle(expectedPayload.Markers, $"expected markers={expectedPayload.Markers.Count}, labels=[{expectedLabels}], {diag}");
                Assert.AreEqual("Absent Star (expected)", Assert.ContainsSingle(expectedPayload.TextBlocks, $"labels=[{expectedLabels}], {diag}").Lines[0]);

                archived = Assert.ContainsSingle(await provider.GetRequiredService<SqliteCenteredViews>()
                    .ReadArchiveAsync(captureId, token).ConfigureAwait(false));
                initialRevisionId = archived.LatestRevisionId;
                var rendered = await provider.GetRequiredService<ICenteredViewPresentationReader>()
                    .ReadAsync(archived.Base.BaseId, initialRevisionId, token).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, rendered.Status, rendered.Reason);
                initialPresentationId = rendered.Presentation!.PresentationIdentitySha256;
                initialSvg = rendered.Presentation.Svg.ToArray();
            }

            // Reopen archive in a fresh host provider and verify byte-identical presentation & replay identity.
            using var reopened = CenteredRuntimeProvider(root, clock, services =>
            {
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                services.AddSingleton<ICelestialCatalog>(predictionCatalog);
                services.AddSingleton<IPlanetEphemeris>(ephemeris);
            });
            await reopened.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            _ = await reopened.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(locationSeed, token).ConfigureAwait(false);
            var reopenedViews = reopened.GetRequiredService<SqliteCenteredViews>();
            var reopenedArchive = Assert.ContainsSingle(await reopenedViews.ReadArchiveAsync(captureId, token).ConfigureAwait(false));
            Assert.AreEqual(initialRevisionId, reopenedArchive.LatestRevisionId);
            var reopenedPresentation = await reopened.GetRequiredService<ICenteredViewPresentationReader>()
                .ReadAsync(reopenedArchive.Base.BaseId, reopenedArchive.LatestRevisionId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, reopenedPresentation.Status, reopenedPresentation.Reason);
            Assert.AreEqual(initialPresentationId, reopenedPresentation.Presentation!.PresentationIdentitySha256);
            CollectionAssert.AreEqual(initialSvg, reopenedPresentation.Presentation.Svg.ToArray());

            // Submit retained-input replay and verify exact analytical identity & overlay parity.
            var operations = reopened.GetRequiredService<IProcessingGraphOperations>();
            var replay = await operations.SubmitReplayAsync(
                new(captureId, liveExecution.GraphRevisionId, liveExecution.PrimaryArtifactId),
                "verify-526-replay", "test-owner", token).ConfigureAwait(false);
            Assert.AreNotEqual(liveExecution.ExecutionId, replay.Execution.ExecutionId);
            var replayStore = reopened.GetRequiredService<SqliteCaptureProcessingStore>();
            var replayLease = await replayStore.ClaimReplayAsync("test-replay-worker", token).ConfigureAwait(false);
            Assert.IsNotNull(replayLease);
            var factory = reopened.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var replayGraph = factory.CreateRetainedGraph(replayLease.Configuration);
            try
            {
                var replayExecution = new ProcessingExecutionContext(
                    replayLease.Execution.ExecutionId, ProcessingGraphExecutionClass.Replay,
                    replayLease.Execution.GraphRevisionId, replayLease.Execution.LocalPlanIdentitySha256,
                    false, replayLease.WorkId, replayLease.LeaseToken, replayLease.LeaseOwner,
                    replayLease.Execution.DeadlineUtc, replayLease.ClaimCount);
                var replayResult = await FrameProcessingWorker.ProcessGraphItemAsync(
                    new(replayLease.Configuration, replayLease.Submission, replayLease.RawCapture,
                        replayLease.WorkId, replayLease.LeaseToken, replayExecution),
                    replayGraph, reopened.GetRequiredService<CaptureProcessingPersistence>(),
                    reopened.GetRequiredService<CaptureProcessingTelemetry>(), replayLease.Execution.AttemptCount,
                    Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, token,
                    durableAttempt: replayLease.ClaimCount,
                    frozenPlanSuperseded: !replayLease.Revision.IsCompiledBy(replayGraph)).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replayResult.Outcome, replayResult.Reason);
                await replayStore.CompleteReplayAsync(replayLease, replayResult, token).ConfigureAwait(false);
            }
            finally
            {
                replayGraph.DisposeSteps();
            }

            var replayedDetail = await replayStore.ReadExecutionDetailAsync(replayLease.Execution.ExecutionId, token).ConfigureAwait(false);
            Assert.IsNotNull(replayedDetail);
            var replayedOutput = Assert.ContainsSingle(replayedDetail.Nodes.Single(static n => n.NodeId == "associated").Outputs);
            Assert.AreEqual(originalAssocOutputId, replayedOutput.OutputIdentitySha256);
            var replayedArtifact = await reopened.GetRequiredService<CaptureProcessingPersistence>()
                .ReadCenteredViewParentAsync(captureId, replayedOutput.ArtifactId, token).ConfigureAwait(false);
            Assert.AreEqual(originalAssocContentId, replayedArtifact.ContentIdentitySha256);
            CollectionAssert.AreEqual(originalAssocPayload, replayedArtifact.Payload.ToArray());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void OptInExpectedNotDetectedAndExposureAssociationOptionsPreserveLegacyDefaults()
    {
        var legacyScene = new ScenePresentationLayerProcessingStepOptions();
        var legacySceneJson = JsonSerializer.SerializeToElement(legacyScene);
        Assert.IsFalse(legacySceneJson.TryGetProperty("IncludeExpectedNotDetectedLayer", out _));
        Assert.IsFalse(legacySceneJson.TryGetProperty("ExpectedNotDetectedOutputVariant", out _));
        Assert.IsFalse(legacySceneJson.TryGetProperty("UseExposureAssociations", out _));

        var legacyManifest = new OverlayManifestProcessingStepOptions();
        var legacyManifestJson = JsonSerializer.SerializeToElement(legacyManifest);
        Assert.IsFalse(legacyManifestJson.TryGetProperty("IncludeExpectedNotDetectedLayer", out _));
        Assert.IsFalse(legacyManifestJson.TryGetProperty("ExpectedNotDetectedVariant", out _));

        var legacyAnnotation = new AnnotationProcessingStepOptions();
        var legacyAnnotationJson = JsonSerializer.SerializeToElement(legacyAnnotation);
        Assert.IsFalse(legacyAnnotationJson.TryGetProperty("UseExposureAssociations", out _));

        var optedScene = new ScenePresentationLayerCaptureProcessingStep(
            new("scene-overlay", "ScenePresentationLayer", 20),
            new() { IncludeExpectedNotDetectedLayer = true, UseExposureAssociations = true });
        Assert.IsTrue(optedScene.Outputs.Any(static o => o.Variant == "expected-not-detected-layer-v1"));
        Assert.IsTrue(optedScene.DependencyRequirements.Any(static r =>
            r.RecipeNames?.Contains(ExposureAssociationProducts.RecipeName) == true));

        var optedManifest = new OverlayManifestCaptureProcessingStep(
            new("manifest", "OverlayManifest", 80),
            new() { IncludeExpectedNotDetectedLayer = true });
        Assert.IsTrue(optedManifest.DependencyRequirements.Any(static r => r.Variant == "expected-not-detected-layer-v1"));

        var optedAnnotation = new AnnotationCaptureProcessingStep(
            new("annotation", "Annotation", 70),
            new() { UseExposureAssociations = true },
            new ProjectedSceneStore(),
            new StubAnnotationSceneProvider(),
            new CameraAgentRecipeExecutionAdapter(new ProcessingRecipeExecutor()));
        Assert.IsTrue(optedAnnotation.DependencyRequirements.Any(static r =>
            r.RecipeNames?.Contains(ExposureAssociationProducts.RecipeName) == true));
    }

    [TestMethod]
    public async Task UseExposureAssociationsExecutesEndToEndOnVirtualSkyForScenePresentationAndAnnotation()
    {
        // Sol M3: ordinary VirtualSky graph where ProjectedScene is VirtualRenderAuthoritative,
        // ExposureCatalogPrediction is Predicted, ExposureStellarAssociations binds ExposureCatalogPrediction,
        // and downstream ScenePresentationLayer + Annotation both opt into UseExposureAssociations = true.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-m3-use-exposure");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var location = profile.DeploymentLocation!;
        var ra = AstronomyTime.LocalMeanSiderealDegrees(start, location.LongitudeDegrees) / 15d;
        var matchedStar = new CelestialCatalogObject("star:m3-matched", "M3 Matched", ra, location.LatitudeDegrees, 0.2, 0.65);
        var absentStar = new CelestialCatalogObject("star:m3-absent", "M3 Absent", (ra + 1.5) % 24, location.LatitudeDegrees + 15, 0.6, 0.65);
        var jupiterPos = new SolarSystemPosition(new EquatorialPoint((ra - 1.0 + 24) % 24, location.LatitudeDegrees - 15), -1.8);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = jupiterPos
        }, "m3-ephemeris-v1");
        var predictionCatalog = new TestMetadataCatalog([matchedStar, absentStar]);
        var renderCatalog = new InMemoryCelestialCatalog([matchedStar]);

        var sceneWithPlanet = profile.Pipeline.Steps.Single(static s => s.Type == "ProjectedScene") with
        {
            Options = JsonSerializer.SerializeToElement(new ProjectedSceneCaptureProcessingStepOptions
            {
                MaximumMagnitude = 6.5,
                MaximumResults = 2000,
                SolarSystemBodies = [SolarSystemBody.Jupiter]
            })
        };
        profile = profile with
        {
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 5263,
                illuminationMode = "ControlledNight",
                magnitudeZeroElectronsPerSecond = 3000,
                backgroundElectronsPerSecond = 25,
                readNoiseStandardDeviation = 4.0,
                shotNoiseEnabled = true,
                solarSystemBodies = JupiterBodyNames
            })),
            Pipeline = profile.Pipeline with
            {
                Steps =
                [
                    .. profile.Pipeline.Steps.Where(static s => s.Type != "ProjectedScene"),
                    sceneWithPlanet,
                    new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable),
                    new("Preview", "preview", DependsOn: ["$raw"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new { outputVariant = "m3-preview" })),
                    new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene", "associated"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                        {
                            UseExposureAssociations = true,
                            IncludeExpectedNotDetectedLayer = true,
                            ExpectedPositionDiagnostics = false,
                            LabelScale = 1,
                            MaximumLabelMagnitude = 6.5
                        })),
                    new("Annotation", "annotated", DependsOn: ["preview", "scene", "associated"], Required: false, Publication: durable,
                        Options: JsonSerializer.SerializeToElement(new
                        {
                            requireProjectedSceneDependency = true,
                            useExposureAssociations = true,
                            expectedPositionDiagnostics = false,
                            maximumLabelMagnitude = 6.5
                        }))
                ]
            }
        };

        try
        {
            var locationSeed = new DeploymentLocationSeed(
                location.LocationId, location.Source, null,
                location.EffectiveFromUtc, location.EffectiveUntilUtc,
                new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, location.TimeZoneId));
            using var provider = CenteredRuntimeProvider(root, clock, services =>
            {
                services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning));
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                services.AddSingleton<ICelestialCatalog>(predictionCatalog);
                services.AddSingleton<IPlanetEphemeris>(ephemeris);
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initializedLocation = await provider.GetRequiredService<IDeploymentLocationStore>()
                .InitializeAsync(locationSeed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initializedLocation };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, renderCatalog, new ProjectedSceneStore(),
                planetEphemeris: ephemeris,
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "m3-test", token).ConfigureAwait(false);
            var request = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var captured = await module.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
            clock.UtcNow = captured.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active,
                new(request, captured, start, request.TargetInterval, request.TargetInterval),
                reservation, token).ConfigureAwait(false);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, captureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var detail = await store.ReadExecutionDetailAsync(linear!.Product!.Slot.ExecutionId, token).ConfigureAwait(false);
            Assert.IsNotNull(detail);

            // Confirm VirtualRenderAuthoritative scene != ExposureCatalogPrediction scene, yet BOTH consumers completed cleanly!
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var renderSceneOutput = Assert.ContainsSingle(detail.Nodes.Single(static n => n.NodeId == "scene").Outputs);
            var renderScene = ProjectedSceneJson.Parse((await persistence.ReadCenteredViewParentAsync(captureId, renderSceneOutput.ArtifactId, token).ConfigureAwait(false)).Payload).Scene!;
            Assert.AreEqual(ProjectedSceneKind.VirtualRenderAuthoritative, renderScene.Kind);

            var sceneOverlayNode = detail.Nodes.Single(static n => n.NodeId == "scene-overlay");
            Assert.AreEqual(nameof(DurableProcessingNodeStatus.Completed), sceneOverlayNode.Status, sceneOverlayNode.Reason);
            var starLayerOutput = sceneOverlayNode.Outputs.Single(static o => o.Variant == "scene-annotation-layer-v1");
            var starPayload = PresentationLayerPayloadJson.Parse(
                (await persistence.ReadCenteredViewParentAsync(captureId, starLayerOutput.ArtifactId, token).ConfigureAwait(false)).Payload).Payload!;
            Assert.IsTrue(starPayload.TextBlocks.Any(static b => b.Lines[0] == "M3 Matched"),
                $"ScenePresentationLayer must label matched star. labels=[{string.Join(", ", starPayload.TextBlocks.Select(b => b.Lines[0]))}]");
            Assert.IsTrue(starPayload.TextBlocks.Any(static b => b.Lines[0] == "Jupiter"),
                $"ScenePresentationLayer must label matched Jupiter. labels=[{string.Join(", ", starPayload.TextBlocks.Select(b => b.Lines[0]))}]");

            var annotatedNode = detail.Nodes.Single(static n => n.NodeId == "annotated");
            Assert.AreEqual(nameof(DurableProcessingNodeStatus.Completed), annotatedNode.Status, annotatedNode.Reason);
            Assert.ContainsSingle(annotatedNode.Outputs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task CenteredExpectedNotDetectedHasSingleAuthoritativeProducerInBothArrivalOrders()
    {
        // Sol M4 + Opus L5: when a centered graph includes both ExposureStellarAssociations and
        // ScenePresentationLayer(IncludeExpectedNotDetectedLayer = true), ExposureStellarAssociations is the
        // sole authoritative centered producer of expected-not-detected regardless of arrival order.
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);

        // Verify ReadLegacySlot rejects arbitrary producers carrying MeasuredStellarAssociationsV1.
        var arbitrarySlot = new ExposureProductSlot(Guid.NewGuid(), "untrusted-step",
            MeasuredStellarAssociationsV1.CurrentSchemaVersion, "custom-variant");
        Assert.IsEmpty(CenteredViewLayerBindings.ReadLegacySlot(arbitrarySlot));
        var exposureSlot = new ExposureProductSlot(Guid.NewGuid(), ExposureAssociationProducts.StepAlias,
            MeasuredStellarAssociationsV1.CurrentSchemaVersion, ExposureAssociationProducts.Variant);
        Assert.HasCount(2, CenteredViewLayerBindings.ReadLegacySlot(exposureSlot));

        // Exercise both arrival orders in SQLite centered publication: scene-overlay before associated AND after associated.
        foreach (var sceneLayerFirst in new[] { false, true })
        {
            var subRoot = FileSystemTestPaths.CreatePhysicalTemporaryDirectory(sceneLayerFirst ? "hvo-m4-order-a" : "hvo-m4-order-b");
            try
            {
                var subClock = new CenteredClock { UtcNow = start };
                var subDeviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
                var subProfile = CenteredRuntimeProfile(subRoot, start, CameraPixelFormat.Mono16);
                var loc = subProfile.DeploymentLocation!;
                var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
                var star1 = new CelestialCatalogObject("star:m4-1", "M4 One", ra, loc.LatitudeDegrees, 0.4, 0.65);
                var star2 = new CelestialCatalogObject("star:m4-2", "M4 Two", (ra + 1.5) % 24, loc.LatitudeDegrees + 15, 0.6, 0.65);
                var predCat = new TestMetadataCatalog([star1, star2]);
                var rendCat = new InMemoryCelestialCatalog([star1]);
                CaptureProcessingStepConfig[] steps = sceneLayerFirst
                    ?
                    [
                        .. subProfile.Pipeline.Steps,
                        new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                IncludeExpectedNotDetectedLayer = true
                            })),
                        new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable)
                    ]
                    :
                    [
                        .. subProfile.Pipeline.Steps,
                        new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable),
                        new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene", "associated"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                UseExposureAssociations = true,
                                IncludeExpectedNotDetectedLayer = true
                            }))
                    ];
                subProfile = subProfile with
                {
                    Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                    {
                        seed = 5264,
                        illuminationMode = "ControlledNight",
                        magnitudeZeroElectronsPerSecond = 3000,
                        backgroundElectronsPerSecond = 25,
                        readNoiseStandardDeviation = 4.0,
                        shotNoiseEnabled = true
                    })),
                    Pipeline = subProfile.Pipeline with { Steps = steps }
                };
                var seed = new DeploymentLocationSeed(loc.LocationId, loc.Source, null,
                    loc.EffectiveFromUtc, loc.EffectiveUntilUtc,
                    new(loc.LatitudeDegrees, loc.LongitudeDegrees, loc.ElevationMeters, loc.TimeZoneId));
                using var subProvider = CenteredRuntimeProvider(subRoot, subClock, services =>
                {
                    services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                    services.AddSingleton<ICelestialCatalog>(predCat);
                });
                await subProvider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var initLoc = await subProvider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
                subProfile = subProfile with { DeploymentLocation = initLoc };
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(subProvider, subProfile).ConfigureAwait(false);
                var ingress = subProvider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(subDeviceClock, rendCat, new ProjectedSceneStore(),
                    stagingStore: subProvider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var cam = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var resources = subProvider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);

                // Verify on the REAL frozen centered graph that ExposureStellarAssociations is the SOLE centered
                // writer of expected-not-detected in BOTH step orders.
                var snapshot = await subProvider.GetRequiredService<SqliteCaptureProcessingStore>()
                    .ReadRevisionAsync(reservation.Revision.State.RevisionId, token).ConfigureAwait(false);
                var configured = CenteredViewLayerBindings.ReadConfigured(
                    ProcessingGraphJson.Parse(snapshot.DefinitionJson).Definition!);
                var expectedWriters = configured
                    .Where(static pair => pair.Value.Any(static b => b.Style.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind))
                    .ToArray();
                var soleWriter = Assert.ContainsSingle(expectedWriters);
                Assert.AreEqual("associated", soleWriter.Key.Producer);
                Assert.IsFalse(configured.Keys.Any(static k =>
                    k.Producer == "scene-overlay" && k.Variant == "expected-not-detected-layer-v1"),
                    "ScenePresentationLayer must not also bind centered expected-not-detected when ExposureStellarAssociations is configured.");

                var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
                await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "m4-order", token).ConfigureAwait(false);
                var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
                {
                    BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                    BoundGraph = reservation.Revision.Pipeline
                };
                var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
                subClock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
                var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
                var capId = receipt.Manifest.Descriptor.Capture.CaptureId;
                await ProcessCenteredRuntimeExposureAsync(subProvider, active, token).ConfigureAwait(false);
                var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, capId);
                var linear = await subProvider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
                var stream = new CenteredViewStream(key.Camera, key.RunId, reservation.Revision.State.RevisionId);
                await subProvider.GetRequiredService<ICenteredViewProducer>().PublishSingleAsync(stream,
                    linear!.Product!.Slot.ExecutionId, receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                var view = await DrainCenteredReferenceAsync(subProvider, key, token).ConfigureAwait(false);
                var measured = view.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind);
                var expected = view.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Ready, expected.Readiness, expected.Reason);
                // Both layers pin the exact same ExposureStellarAssociations artifact (scene-overlay never overwrote it).
                Assert.AreEqual(measured.Evidence!.ArtifactId, expected.Evidence!.ArtifactId);
            }
            finally
            {
                Directory.Delete(subRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("forged-lineage")]
    [DataRow("tampered-measurement-parent")]
    [DataRow("tampered-scene-parent")]
    [DataRow("tampered-unclaimed-centroid")]
    [DataRow("tampered-unclaimed-flux")]
    [DataRow("incomplete-measurement")]
    [DataRow("empty-all-matched")]
    [DataRow("empty-display-filtered")]
    public async Task GalleryAuthenticatesAssociationParentsAndSettlesIncompleteAndTruthfulEmptyStates(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        // Sol M5 + M6 + M7 + Opus L3 + L4 + Astra D6:
        // - Forged lineage / tampered measurement / tampered scene / tampered unclaimed centroid or flux settle
        //   Incompatible ("centered.association-parent-invalid").
        // - MeasurementIncomplete settles measured layer Incompatible ("measurement-incomplete").
        // - Empty expected-not-detected settles AssessedEmpty with "expected.all-predictions-matched" ONLY when all matched,
        //   and "expected.unmatched-filtered-from-display" when an unmatched faint prediction (> 6.5 mag) exists.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-m56-" + scenario[..Math.Min(6, scenario.Length)]);
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var location = profile.DeploymentLocation!;
        var ra = AstronomyTime.LocalMeanSiderealDegrees(start, location.LongitudeDegrees) / 15d;
        var matchedStar = new CelestialCatalogObject("star:m5-1", "M5 One", ra, location.LatitudeDegrees, 0.3, 0.65);
        var faintAbsentStar = new CelestialCatalogObject("star:m5-faint", "M5 Faint", (ra + 1.5) % 24, location.LatitudeDegrees + 15, 7.8, 0.65);
        var unclaimedStar = new CelestialCatalogObject("star:m5-unclaimed", "M5 Unclaimed", (ra - 1.5 + 24) % 24, location.LatitudeDegrees - 15, 0.4, 0.65);
        var predCatalog = scenario == "empty-display-filtered"
            ? new TestMetadataCatalog([matchedStar, faintAbsentStar])
            : new TestMetadataCatalog([matchedStar]);
        var renderCatalog = scenario is "tampered-unclaimed-centroid" or "tampered-unclaimed-flux"
            ? new InMemoryCelestialCatalog([matchedStar, unclaimedStar])
            : new InMemoryCelestialCatalog([matchedStar]);

        var sceneStep = profile.Pipeline.Steps.Single(static s => s.Type == "ProjectedScene") with
        {
            Options = JsonSerializer.SerializeToElement(new ProjectedSceneCaptureProcessingStepOptions
            {
                MaximumMagnitude = scenario == "empty-display-filtered" ? 9.0 : 6.5,
                MaximumResults = 2000
            })
        };
        profile = profile with
        {
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 5265,
                illuminationMode = "ControlledNight",
                magnitudeZeroElectronsPerSecond = 3000,
                backgroundElectronsPerSecond = 25,
                readNoiseStandardDeviation = 4.0,
                shotNoiseEnabled = true
            })),
            Pipeline = profile.Pipeline with
            {
                Steps =
                [
                    .. profile.Pipeline.Steps.Where(static s => s.Type != "ProjectedScene"),
                    sceneStep,
                    new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable)
                ]
            }
        };

        try
        {
            var locationSeed = new DeploymentLocationSeed(location.LocationId, location.Source, null,
                location.EffectiveFromUtc, location.EffectiveUntilUtc,
                new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters, location.TimeZoneId));
            using var provider = CenteredRuntimeProvider(root, clock, services =>
            {
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                services.AddSingleton<ICelestialCatalog>(predCatalog);
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(locationSeed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, renderCatalog, new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "m5-test", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            var capId = receipt.Manifest.Descriptor.Capture.CaptureId;
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, capId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var stream = new CenteredViewStream(key.Camera, key.RunId, reservation.Revision.State.RevisionId);
            var initial = await provider.GetRequiredService<ICenteredViewProducer>().PublishSingleAsync(stream,
                linear!.Product!.Slot.ExecutionId, receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
            Assert.IsNotNull(initial);

            if (scenario is "empty-all-matched" or "empty-display-filtered")
            {
                var view = await DrainCenteredReferenceAsync(provider, key, token).ConfigureAwait(false);
                var expectedLayer = view.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.AssessedEmpty, expectedLayer.Readiness, expectedLayer.Reason);
                Assert.IsNull(expectedLayer.Drawable);
                Assert.AreEqual(
                    scenario == "empty-all-matched"
                        ? MeasuredAssociationPresentation.EmptyExpectedAllMatchedReason
                        : MeasuredAssociationPresentation.EmptyExpectedFilteredReason,
                    expectedLayer.Reason);
                return;
            }

            // Read the genuine committed ExposureStellarAssociations product and construct scenario-specific artifacts.
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var detail = await store.ReadExecutionDetailAsync(linear.Product.Slot.ExecutionId, token).ConfigureAwait(false);
            var assocOut = Assert.ContainsSingle(detail!.Nodes.Single(static n => n.NodeId == "associated").Outputs);
            var origArtifact = await persistence.ReadCenteredViewParentAsync(capId, assocOut.ArtifactId, token).ConfigureAwait(false);
            var origEvidence = MeasuredStellarAssociationJson.Parse(origArtifact.Payload).Associations!;
            var rawArtifactId = receipt.Manifest.Descriptor.Artifact.ArtifactId;
            var measurementId = origArtifact.SourceArtifactIds![1];
            var sceneId = origArtifact.SourceArtifactIds![2];
            var rawStub = new ProcessingArtifact(rawArtifactId, FrameArtifactRole.Raw, receipt.Manifest.Descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(receipt.Manifest.Descriptor.Artifact.Recipe).IdentitySha256,
                receipt.Manifest.Descriptor.Artifact.MediaType, receipt.Manifest.Descriptor.Layout,
                ReadOnlyMemory<byte>.Empty, receipt.Manifest.Descriptor.Artifact.CreatedUtc,
                receipt.Manifest.Descriptor.Controls.EffectiveExposure, origArtifact.Compatibility);
            var measurementArtifact = await persistence.ReadCenteredViewParentAsync(capId, measurementId, token).ConfigureAwait(false);
            var sceneArtifact = await persistence.ReadCenteredViewParentAsync(capId, sceneId, token).ConfigureAwait(false);

            ProcessingProduct forgedProduct;
            if (scenario == "forged-lineage")
            {
                // Only 1 source parent ([raw]) and wrong recipe producer (legacy MeasuredStellarAssociations instead of ExposureAssociationProducts).
                var legacyIdentity = ProcessingIdentity.CreateRecipeIdentity(
                    new ProcessingRecipeDefinition(BuiltInProcessingRecipes.MeasuredStellarAssociations, "1.0.0",
                        MeasuredStellarAssociationRecipe.CurrentImplementationVersion, ProcessingOperationKind.Analyzer),
                    JsonSerializer.SerializeToElement(new { }));
                var bytes = MeasuredStellarAssociationJson.Serialize(origEvidence);
                forgedProduct = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, "forged-assoc",
                    StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, null, bytes, legacyIdentity,
                    [new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion)],
                    [rawStub], rawStub.Integration, rawStub.Compatibility, ProcessingProductKind.Metadata,
                    MeasuredStellarAssociationsV1.CurrentSchemaVersion, origEvidence.AssociationIdentitySha256);
            }
            else if (scenario == "tampered-measurement-parent")
            {
                // Valid ExposureAssociationProducts recipe binding pointing at a bogus measurement parent ID (uses sceneArtifact as measurement).
                var bogusBinding = ProcessingIdentity.CreateRecipeIdentity(ExposureAssociationProducts.Definition,
                    ProcessingRecipeSupport.Normalize(new
                    {
                        rawArtifactId,
                        measurementArtifactId = sceneId,
                        measurementIdentitySha256 = sceneArtifact.ContentIdentitySha256,
                        predictionArtifactId = measurementId,
                        sceneIdentitySha256 = origEvidence.Scene.SceneIdentitySha256,
                        settings = origEvidence.Method.Settings,
                        labelPolicy = origEvidence.Method.LabelPolicy
                    }));
                var bytes = MeasuredStellarAssociationJson.Serialize(origEvidence);
                forgedProduct = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, "tampered-meas",
                    StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, null, bytes, bogusBinding,
                    [new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion)],
                    [rawStub, sceneArtifact, measurementArtifact], rawStub.Integration, rawStub.Compatibility,
                    ProcessingProductKind.Metadata, MeasuredStellarAssociationsV1.CurrentSchemaVersion,
                    origEvidence.AssociationIdentitySha256);
            }
            else if (scenario is "tampered-scene-parent" or "tampered-unclaimed-centroid" or "tampered-unclaimed-flux")
            {
                // Altered measured detection or unclaimed detection coordinates/flux contradicting the committed step-4 parent.
                MeasuredStellarAssociationsV1 altered;
                if (scenario == "tampered-scene-parent")
                {
                    altered = origEvidence with
                    {
                        Associations =
                        [
                            origEvidence.Associations[0] with { MeasuredX = origEvidence.Associations[0].MeasuredX + 0.5 }
                        ]
                    };
                }
                else
                {
                    var unclaimed = origEvidence.UnassociatedDetections[0];
                    var tamperedUnclaimed = scenario == "tampered-unclaimed-centroid"
                        ? unclaimed with { X = Math.Clamp(unclaimed.X + 1.5, 0, origEvidence.Source.WidthPixels) }
                        : unclaimed with { Flux = unclaimed.Flux + 125.0 };
                    altered = origEvidence with
                    {
                        UnassociatedDetections = [tamperedUnclaimed, .. origEvidence.UnassociatedDetections.Skip(1)]
                    };
                }
                var bytes = MeasuredStellarAssociationJson.Serialize(altered);
                forgedProduct = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, "tampered-det-" + scenario,
                    StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, null, bytes,
                    ProcessingIdentity.CreateRecipeIdentity(origArtifact.RecipeDescriptor!),
                    [new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion)],
                    [rawStub, measurementArtifact, sceneArtifact], rawStub.Integration, rawStub.Compatibility,
                    ProcessingProductKind.Metadata, MeasuredStellarAssociationsV1.CurrentSchemaVersion,
                    altered.AssociationIdentitySha256);
            }
            else
            {
                // incomplete-measurement: persist an ExposureMeasurement with 0 detections / CandidateBudgetExceeded
                // and an ExposureAssociationProducts artifact bound cleanly to it with Measurement.Status = CandidateBudgetExceeded.
                var parsedMeas = ExposureMeasurementProducts.Read(measurementArtifact,
                    ExposureMeasurementProducts.Read(measurementArtifact, ExposureMeasurementRecipe.Input(
                        ProcessingIdentity.CreateRecipeIdentity(measurementArtifact.RecipeDescriptor!))).Input);
                var incompleteMeas = parsedMeas with
                {
                    Sources = parsedMeas.Sources with
                    {
                        Status = ExposureMeasurementStatus.Unsuitable,
                        Reason = ExposureMeasurementSourceReasonCodes.CandidateBudgetExceeded,
                        Detections = [],
                        Exclusions = []
                    },
                    Hfd = new ExposureHfdV1(
                        ExposureMeasurementStatus.Unavailable,
                        ExposureMeasurementSourceReasonCodes.CandidateBudgetExceeded,
                        null, null, null, 0, null, [])
                };
                var incompleteMeasBytes = ExposureMeasurementProducts.Serialize(incompleteMeas);
                var measOptions = measurementArtifact.RecipeDescriptor!.Options.EnumerateObject()
                    .ToDictionary(static p => p.Name, static p => p.Value.Clone(), StringComparer.Ordinal);
                measOptions["parameters"] = ExposureMeasurementProducts.SerializeParameters(
                    incompleteMeas.Input, ExposureFocusSamplingPolicy.Brightest);
                var incompleteMeasRecipe = ProcessingIdentity.CreateRecipeIdentity(
                    HVO.SkyMonitor.AgentCore.RecipeIdentityDescriptor.Create(
                        measurementArtifact.RecipeDescriptor.Name,
                        measurementArtifact.RecipeDescriptor.SemanticVersion,
                        measurementArtifact.RecipeDescriptor.ImplementationVersion,
                        JsonSerializer.SerializeToElement(measOptions)));
                var incompleteMeasProd = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata,
                    ExposureMeasurementProducts.Variant, ExposureMeasurementProducts.MediaType, null, incompleteMeasBytes,
                    incompleteMeasRecipe,
                    ExposureMeasurementRecipe.Algorithms(incompleteMeas.Input),
                    [rawStub, await persistence.ReadCenteredViewParentAsync(capId, linear.Product.ArtifactId, token).ConfigureAwait(false)],
                    rawStub.Integration, rawStub.Compatibility, ProcessingProductKind.Metadata,
                    ExposureMeasurementProducts.SchemaVersion, ProcessingIdentity.ComputePayloadSha256(incompleteMeasBytes));
                await persistence.PersistCenteredViewProductAsync(receipt.Manifest.Descriptor, incompleteMeasProd, token).ConfigureAwait(false);
                var incompleteMeasArt = new ProcessingArtifact(
                    ProcessingIdentity.CreateArtifactId(incompleteMeasProd.OutputIdentitySha256),
                    incompleteMeasProd.Role, incompleteMeasProd.Variant, incompleteMeasProd.Recipe.IdentitySha256,
                    incompleteMeasProd.MediaType, null, incompleteMeasProd.Payload, rawStub.CreatedUtc,
                    rawStub.Integration, rawStub.Compatibility, SourceArtifactIds: incompleteMeasProd.SourceArtifactIds)
                {
                    CaptureId = capId,
                    ProductKind = incompleteMeasProd.Kind,
                    SchemaVersion = incompleteMeasProd.SchemaVersion,
                    ContentIdentitySha256 = incompleteMeasProd.ContentIdentitySha256,
                    RecipeDescriptor = incompleteMeasProd.Recipe.Descriptor
                };
                var incompleteEvidence = origEvidence with
                {
                    Measurement = origEvidence.Measurement with
                    {
                        Status = StellarMeasurementStatus.CandidateBudgetExceeded,
                        DetectionCount = 0,
                        ExclusionCount = 0
                    },
                    Associations = [],
                    UnmatchedPredictions =
                    [
                        new(origEvidence.Associations[0].CatalogId, origEvidence.Associations[0].Magnitude,
                            origEvidence.Associations[0].ExpectedX, origEvidence.Associations[0].ExpectedY,
                            MeasuredStellarAssociationReasonCodes.MeasurementIncomplete)
                    ],
                    UnassociatedDetections = []
                };
                var incompleteBinding = ProcessingIdentity.CreateRecipeIdentity(ExposureAssociationProducts.Definition,
                    ProcessingRecipeSupport.Normalize(new
                    {
                        rawArtifactId,
                        measurementArtifactId = incompleteMeasArt.ArtifactId,
                        measurementIdentitySha256 = incompleteMeasArt.ContentIdentitySha256,
                        predictionArtifactId = sceneId,
                        sceneIdentitySha256 = origEvidence.Scene.SceneIdentitySha256,
                        settings = incompleteEvidence.Method.Settings,
                        labelPolicy = incompleteEvidence.Method.LabelPolicy
                    }));
                var bytes = MeasuredStellarAssociationJson.Serialize(incompleteEvidence);
                forgedProduct = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, "incomplete-assoc",
                    StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, null, bytes, incompleteBinding,
                    [new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion)],
                    [rawStub, incompleteMeasArt, sceneArtifact], rawStub.Integration, rawStub.Compatibility,
                    ProcessingProductKind.Metadata, MeasuredStellarAssociationsV1.CurrentSchemaVersion,
                    incompleteEvidence.AssociationIdentitySha256);
            }

            await persistence.PersistCenteredViewProductAsync(receipt.Manifest.Descriptor, forgedProduct, token).ConfigureAwait(false);
            var slot = new ExposureProductSlot(linear.Product.Slot.ExecutionId, "associated",
                forgedProduct.SchemaVersion!, forgedProduct.Variant);
            var lookup = new ExposureProductLookup(key, slot, ExposureProductAvailability.Available, 999,
                new(key, slot, ProcessingIdentity.CreateArtifactId(forgedProduct.OutputIdentitySha256),
                    forgedProduct.OutputIdentitySha256, forgedProduct.ChecksumSha256, forgedProduct.ContentIdentitySha256),
                null);
            var layerProducer = provider.GetRequiredService<ICenteredViewLayerProducer>();
            var published = await layerProducer.PublishAsync(initial.Base.BaseId, CenteredViewLayerScope.Reference,
                lookup, new(MeasuredAssociationPresentation.MeasuredLayerKind, 21, true),
                receipt.Manifest.Descriptor.Timing.ExposureEndedUtc, cancellationToken: token).ConfigureAwait(false);
            Assert.IsNotNull(published);
            var layer = published.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind);
            Assert.AreEqual(CenteredViewReadiness.Incompatible, layer.Readiness);
            Assert.IsNull(layer.Drawable);
            Assert.AreEqual(
                scenario == "incomplete-measurement"
                    ? MeasuredStellarAssociationReasonCodes.MeasurementIncomplete
                    : "centered.association-parent-invalid",
                layer.Reason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(CameraPixelFormat.Mono16)]
    [DataRow(CameraPixelFormat.BayerRggb16)]
    public async Task VirtualSkyEndToEndMeasuredAssociationScoringAcrossMono16AndBayerRggb16WithAdverseCases(
        CameraPixelFormat format)
    {
        // Sol M7: End-to-end VirtualSky Mono16 + BayerRggb16 scoring (recall, zero false IDs including absent
        // planet 1 px from a real catalog star, sub-pixel centroid accuracy, positive SNR, compact payload & bounded latency).
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-m7-" + format);
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, format);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var loc = profile.DeploymentLocation!;
        var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;

        // 3 isolated rendered catalog stars + 1 present planet (Jupiter) + 1 absent star + 1 uncataloged source +
        // 1 adverse pair: absent Mars predicted ~1 px from a real rendered mag-2 star (star:adverse).
        var star1 = new CelestialCatalogObject("star:s1", "Star One", ra, loc.LatitudeDegrees, 0.3, 0.65);
        var star2 = new CelestialCatalogObject("star:s2", "Star Two", (ra + 1.4) % 24, loc.LatitudeDegrees + 12, 0.5, 0.65);
        var star3 = new CelestialCatalogObject("star:s3", "Star Three", (ra - 1.4 + 24) % 24, loc.LatitudeDegrees + 14, 0.6, 0.65);
        var absentStar = new CelestialCatalogObject("star:absent", "Absent Star", (ra + 1.6) % 24, loc.LatitudeDegrees - 16, 0.5, 0.65);
        var adverseStar = new CelestialCatalogObject("star:adverse", "Adverse Star", (ra - 1.5 + 24) % 24, loc.LatitudeDegrees - 14, 1.0, 0.65);
        var unclaimed = new CelestialCatalogObject("star:unclaimed", "Uncataloged", (ra + 0.2) % 24, loc.LatitudeDegrees - 22, 0.4, 0.65);

        // Place absent Mars ~0.8 deg (~1 px on 224x224 fisheye) from adverseStar!
        var jupiterPos = new SolarSystemPosition(new EquatorialPoint((ra + 0.1) % 24, loc.LatitudeDegrees + 22), -1.6);
        var absentMarsPos = new SolarSystemPosition(new EquatorialPoint(adverseStar.RightAscensionHours + 0.05, adverseStar.DeclinationDegrees), -1.2);
        var predictionEphemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = jupiterPos,
            [SolarSystemBody.Mars] = absentMarsPos
        }, "m7-pred-ephem-v1");
        var renderEphemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = jupiterPos
        }, "m7-pred-ephem-v1");

        var predictionCatalog = new TestMetadataCatalog([star1, star2, star3, absentStar, adverseStar]);
        var renderCatalog = new InMemoryCelestialCatalog([star1, star2, star3, adverseStar, unclaimed]);

        var sceneWithPlanets = profile.Pipeline.Steps.Single(static s => s.Type == "ProjectedScene") with
        {
            Options = JsonSerializer.SerializeToElement(new ProjectedSceneCaptureProcessingStepOptions
            {
                MaximumMagnitude = 6.5,
                MaximumResults = 2000,
                SolarSystemBodies = [SolarSystemBody.Jupiter, SolarSystemBody.Mars]
            })
        };
        profile = profile with
        {
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 5267,
                illuminationMode = "ControlledNight",
                magnitudeZeroElectronsPerSecond = format == CameraPixelFormat.BayerRggb16 ? 8000 : 3000,
                backgroundElectronsPerSecond = 25,
                readNoiseStandardDeviation = 4.0,
                shotNoiseEnabled = true,
                asi178Sensor = new { enabled = format == CameraPixelFormat.BayerRggb16 },
                solarSystemBodies = JupiterBodyNames
            })),
            Pipeline = profile.Pipeline with
            {
                Steps =
                [
                    .. profile.Pipeline.Steps.Where(static s => s.Type != "ProjectedScene"),
                    sceneWithPlanets,
                    new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable)
                ]
            }
        };

        try
        {
            var locationSeed = new DeploymentLocationSeed(loc.LocationId, loc.Source, null,
                loc.EffectiveFromUtc, loc.EffectiveUntilUtc,
                new(loc.LatitudeDegrees, loc.LongitudeDegrees, loc.ElevationMeters, loc.TimeZoneId));
            using var provider = CenteredRuntimeProvider(root, clock, services =>
            {
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                services.AddSingleton<ICelestialCatalog>(predictionCatalog);
                services.AddSingleton<IPlanetEphemeris>(predictionEphemeris);
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(locationSeed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, renderCatalog, new ProjectedSceneStore(),
                planetEphemeris: renderEphemeris,
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var camera = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "m7-scoring", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            var capId = receipt.Manifest.Descriptor.Capture.CaptureId;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
            sw.Stop();
            Assert.IsLessThan(30_000, sw.ElapsedMilliseconds, "End-to-end centered processing must remain bounded.");

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, capId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var detail = await store.ReadExecutionDetailAsync(linear!.Product!.Slot.ExecutionId, token).ConfigureAwait(false);
            var assocOut = Assert.ContainsSingle(detail!.Nodes.Single(static n => n.NodeId == "associated").Outputs);
            var assocArt = await provider.GetRequiredService<CaptureProcessingPersistence>()
                .ReadCenteredViewParentAsync(capId, assocOut.ArtifactId, token).ConfigureAwait(false);
            Assert.IsLessThan(64 * 1024, assocArt.Payload.Length, "Analytical metadata payload must remain compact.");
            var evidence = MeasuredStellarAssociationJson.Parse(assocArt.Payload).Associations!;

            // 100% recall on the 3 isolated catalog stars; zero false IDs for absent star or absent Mars.
            var matchedStarIds = evidence.Associations.Select(static a => a.CatalogId).ToArray();
            CollectionAssert.AreEquivalent(ExpectedScoringStarIds, matchedStarIds);
            Assert.IsTrue(evidence.Associations.All(static a => a.LabelEligible && a.ResidualPixels < 0.75 && a.SignalToNoise > 10));
            Assert.AreEqual(MeasuredStellarAssociationReasonCodes.NoMeasuredSource,
                evidence.UnmatchedPredictions.Single(static u => u.CatalogId == "star:absent").ReasonCode);

            // Present Jupiter matched accurately; absent Mars near adverseStar MUST NOT be falsely claimed!
            Assert.IsNotNull(evidence.PointSourcePlanets);
            var jupiter = Assert.ContainsSingle(evidence.PointSourcePlanets.Associations);
            Assert.AreEqual(SolarSystemBody.Jupiter, jupiter.Body);
            Assert.IsTrue(jupiter.LabelEligible, jupiter.LabelRejectionReason);
            Assert.IsLessThan(0.75, jupiter.ResidualPixels);
            Assert.IsTrue(jupiter.SignalToNoise > 10);
            var marsUnmatched = Assert.ContainsSingle(evidence.PointSourcePlanets.UnmatchedPredictions);
            Assert.AreEqual(SolarSystemBody.Mars, marsUnmatched.Body);
            Assert.AreEqual(MeasuredStellarAssociationReasonCodes.CrowdedPrediction, marsUnmatched.ReasonCode);

            // Unclaimed source preserved without catalog identity.
            Assert.IsTrue(evidence.UnassociatedDetections.Any(static d =>
                d.ReasonCode == MeasuredStellarAssociationReasonCodes.NoCatalogPrediction));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingGraphRecoveryEnforcesSingleExpectedNotDetectedWriterInBothArrivalOrders()
    {
        // Astra D5: when the frozen graph is unavailable (ReadCenteredLayerBindingsAsync returns null),
        // CenteredViewSourceLayers and ReadLegacySlot must enforce the exact same single-writer ownership rule so
        // expected-not-detected-layer-v1 never overwrites ExposureStellarAssociations in either arrival order.
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);

        var sceneExpectedSlot = new ExposureProductSlot(Guid.NewGuid(), "scene-overlay",
            PresentationLayerPayloadV1.CurrentSchemaVersion, "expected-not-detected-layer-v1");
        var resolvedWriter = new CenteredAuthoritativeAssociationWriter("associated", "custom-assoc");
        Assert.IsEmpty(CenteredViewLayerBindings.ReadLegacySlot(sceneExpectedSlot, authoritativeWriter: resolvedWriter));
        Assert.ContainsSingle(CenteredViewLayerBindings.ReadLegacySlot(sceneExpectedSlot, authoritativeWriter: null));
        var customAssocSlot = new ExposureProductSlot(Guid.NewGuid(), "associated",
            MeasuredStellarAssociationsV1.CurrentSchemaVersion, "custom-assoc");
        Assert.IsEmpty(CenteredViewLayerBindings.ReadLegacySlot(customAssocSlot, authoritativeWriter: null));
        Assert.HasCount(2, CenteredViewLayerBindings.ReadLegacySlot(customAssocSlot, authoritativeWriter: resolvedWriter));

        // Exercise BOTH default and custom outputVariant ("custom-assoc") with custom node ID "associated"
        // across ALL THREE recovery paths: scene-first, assoc-first, and rebuild-base.
        foreach (var customVariant in new string?[] { null, "custom-assoc" })
            foreach (var orderLabel in new[] { "scene-first", "assoc-first", "rebuild-base" })
            {
                var variantTag = customVariant is null ? "def" : "cst";
                var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-d5-{variantTag}-{orderLabel}");
                try
                {
                    var clock = new CenteredClock { UtcNow = start };
                    var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
                    var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
                    var loc = profile.DeploymentLocation!;
                    var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
                    var star1 = new CelestialCatalogObject("star:d5-1", "D5 One", ra, loc.LatitudeDegrees, 0.4, 0.65);
                    var star2 = new CelestialCatalogObject("star:d5-2", "D5 Two", (ra + 1.5) % 24, loc.LatitudeDegrees + 15, 0.6, 0.65);
                    var predCat = new TestMetadataCatalog([star1, star2]);
                    var rendCat = new InMemoryCelestialCatalog([star1]);
                    JsonElement? assocOptions = customVariant is null
                        ? null
                        : JsonSerializer.SerializeToElement(new { outputVariant = customVariant });
                    profile = profile with
                    {
                        Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                        {
                            seed = 52655,
                            illuminationMode = "ControlledNight",
                            magnitudeZeroElectronsPerSecond = 3000,
                            backgroundElectronsPerSecond = 25,
                            readNoiseStandardDeviation = 4.0,
                            shotNoiseEnabled = true
                        })),
                        Pipeline = profile.Pipeline with
                        {
                            Steps =
                            [
                                .. profile.Pipeline.Steps,
                            new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false,
                                Options: assocOptions, Publication: durable),
                            new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene", "associated"], Required: false, Publication: durable,
                                Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                                {
                                    UseExposureAssociations = true,
                                    IncludeExpectedNotDetectedLayer = true
                                }))
                            ]
                        }
                    };
                    var seed = new DeploymentLocationSeed(loc.LocationId, loc.Source, null,
                        loc.EffectiveFromUtc, loc.EffectiveUntilUtc,
                        new(loc.LatitudeDegrees, loc.LongitudeDegrees, loc.ElevationMeters, loc.TimeZoneId));
                    using var provider = CenteredRuntimeProvider(root, clock, services =>
                    {
                        services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                        services.AddSingleton<ICelestialCatalog>(predCat);
                    });
                    await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                    var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
                    profile = profile with { DeploymentLocation = initLoc };
                    var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                    var ingress = provider.GetRequiredService<RawCaptureIngress>();
                    var module = new VirtualSkyCameraModule(deviceClock, rendCat, new ProjectedSceneStore(),
                        stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                    await using var cam = module.ConfigureAwait(false);
                    await module.InitializeAsync(active, token).ConfigureAwait(false);
                    var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                    var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
                    var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
                    await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d5-test", token).ConfigureAwait(false);
                    var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
                    {
                        BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                        BoundGraph = reservation.Revision.Pipeline
                    };
                    var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
                    clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
                    var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
                    var capId = receipt.Manifest.Descriptor.Capture.CaptureId;
                    await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

                    var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, capId);
                    var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
                    var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                    var views = provider.GetRequiredService<SqliteCenteredViews>();
                    var producer = provider.GetRequiredService<ICenteredViewProducer>();
                    var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();
                    var executionId = linear!.Product!.Slot.ExecutionId;
                    var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
                    var writer = await store.ReadAuthoritativeAssociationWriterAsync(key, executionId, token).ConfigureAwait(false);
                    Assert.IsNotNull(writer, $"{variantTag}/{orderLabel}");
                    Assert.AreEqual("associated", writer.Value.Producer, $"{variantTag}/{orderLabel}");
                    Assert.AreEqual(customVariant ?? "measured-stellar-associations-v1", writer.Value.Variant, $"{variantTag}/{orderLabel}");
                    var assocSource = sources.Single(s => CenteredViewLayerBindings.IsAuthoritativeAssociationSlot(s.Slot, writer));
                    var sceneExpectedSource = sources.Single(static s => CenteredViewLayerBindings.IsSceneExpectedNotDetectedSlot(s.Slot));

                    var missingGeneration = $"missing-graph-{variantTag}-{orderLabel}";
                    Assert.IsNull(await store.ReadCenteredLayerBindingsAsync(missingGeneration, token).ConfigureAwait(false));
                    var stream = new CenteredViewStream(key.Camera, key.RunId, missingGeneration);
                    var published = await producer.PublishSingleAsync(stream, executionId,
                        receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                    Assert.IsNotNull(published);

                    if (orderLabel == "scene-first")
                    {
                        await sourceLayers.RebuildForProductAsync(sceneExpectedSource, missingGeneration, start, token).ConfigureAwait(false);
                        var midView = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                        Assert.IsNotNull(midView);
                        Assert.IsFalse(midView.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                            $"{variantTag}: scene-first must suppress scene-overlay expected-not-detected before ExposureStellarAssociations arrives.");
                        await sourceLayers.RebuildForProductAsync(assocSource, missingGeneration, start, token).ConfigureAwait(false);
                    }
                    else if (orderLabel == "assoc-first")
                    {
                        await sourceLayers.RebuildForProductAsync(assocSource, missingGeneration, start, token).ConfigureAwait(false);
                        await sourceLayers.RebuildForProductAsync(sceneExpectedSource, missingGeneration, start, token).ConfigureAwait(false);
                    }
                    else
                    {
                        await sourceLayers.RebuildForBaseAsync(published.Base, token).ConfigureAwait(false);
                    }

                    var finalView = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                    Assert.IsNotNull(finalView);
                    var measured = finalView.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind);
                    var expected = finalView.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                    Assert.AreEqual(CenteredViewReadiness.Ready, measured.Readiness, $"{variantTag}/{orderLabel}: {measured.Reason}");
                    Assert.AreEqual(CenteredViewReadiness.Ready, expected.Readiness, $"{variantTag}/{orderLabel}: {expected.Reason}");
                    Assert.AreEqual(assocSource.Product!.ArtifactId, measured.Evidence!.ArtifactId, $"{variantTag}/{orderLabel}");
                    Assert.AreEqual(assocSource.Product.ArtifactId, expected.Evidence!.ArtifactId,
                        $"{variantTag}/{orderLabel}: expected-not-detected must remain pinned to ExposureStellarAssociations even without a frozen graph.");
                    Assert.AreNotEqual(sceneExpectedSource.Product!.ArtifactId, expected.Evidence.ArtifactId, $"{variantTag}/{orderLabel}");
                }
                finally
                {
                    Directory.Delete(root, recursive: true);
                }
            }
    }

    [TestMethod]
    public async Task OrdinaryPresentationMaterializerSelectsExpectedNotDetectedEndToEnd()
    {
        // Astra D7: expected-not-detected must be registered in PresentationLayerKindSelection so both explicit
        // materializer selection and manifest layer overrides validate and materialize end-to-end.
        Assert.IsTrue(PresentationLayerKindSelection.IsSupported(MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind));
        var validMatJson = JsonDocument.Parse("""{"enabledLayerKinds":["expected-not-detected"]}""").RootElement.Clone();
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(PresentationMaterializerOptionDescriptors.Descriptor, validMatJson).IsValid);
        var validManifestJson = JsonDocument.Parse("""{"layers":[{"kind":"expected-not-detected","enabled":true}]}""").RootElement.Clone();
        Assert.IsTrue(ProcessingNodeOptionsCodec.Decode(OverlayManifestOptionDescriptors.Descriptor, validManifestJson).IsValid);

        var matOptions = new PresentationMaterializerProcessingStepOptions
        {
            BasePreviewVariant = "preview-v1",
            OutputVariant = "expected-materialized",
            EnabledLayerKinds = [MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind]
        };
        Assert.IsTrue(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            matOptions, new(matOptions), new List<System.ComponentModel.DataAnnotations.ValidationResult>(), true));

        var manifestOverrideOptions = new OverlayManifestProcessingStepOptions
        {
            BasePreviewVariant = "preview-v1",
            IncludeExpectedNotDetectedLayer = true,
            Layers = [new() { Kind = MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, Enabled = true }]
        };
        Assert.IsTrue(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(
            manifestOverrideOptions, new(manifestOverrideOptions), new List<System.ComponentModel.DataAnnotations.ValidationResult>(), true));

        // Execute ScenePresentationLayer -> OverlayManifest -> PresentationMaterializer end-to-end on a real scene.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d7-materializer");
        try
        {
            var token = TestContext.CancellationToken;
            var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var width = profile.Rig.Sensor.WidthPixels;
            var height = profile.Rig.Sensor.HeightPixels;
            var manifest = ReconstructableCaptureContractTests.CreateManifest(
                CameraPixelFormat.Mono8, width, height, width, new byte[width * height]);
            var descriptor = manifest.Descriptor;
            var epoch = descriptor.Timing.ExposureStartedUtc;
            var location = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(epoch, location.LongitudeDegrees) / 15d;
            var star = new CelestialCatalogObject("star:d7", "D7 Star", ra, location.LatitudeDegrees, 0.5, 0.65);
            var proj = RigProjectionContextFactory.Create(profile.Rig);
            var visible = await new VisibleSceneBuilder(new InMemoryCelestialCatalog([star])).BuildAsync(
                new VisibleSceneRequest(epoch, new(location.LatitudeDegrees, location.LongitudeDegrees, location.ElevationMeters),
                    proj, new CatalogQuery(6.5, 100), new CatalogMetadata("d7", "1", new Uri("https://example.test/d7"),
                        new string('C', 64), "test", "v1"), projectionVersion: RigProjectionContextFactory.AlgorithmVersion), token).ConfigureAwait(false);
            var raw = new CameraFrame(epoch, width, height, CameraPixelFormat.Mono8,
                new byte[width * height], new(TimeSpan.FromSeconds(1), 99.5, -9.5), width);
            var artifacts = new FrameArtifactSet(new FrameArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, raw, []));
            var submission = new CaptureLoopSubmission(
                new(descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(1), CaptureMode.Still),
                new(raw, new(TimeSpan.FromSeconds(1), 99.5, null, null), TimeSpan.Zero, CaptureMode.Still, false, Artifacts: artifacts),
                descriptor.Timing.RequestedStartUtc, TimeSpan.FromSeconds(1), TimeSpan.Zero);
            var receipt = new RawCaptureReceipt(RawIngressOutcome.Committed, manifest,
                new StoredFrameReference("raw.bin", "/virtual/raw.bin", raw.TimestampUtc, FrameArtifactRole.Raw),
                CaptureContractJson.ComputeManifestSha256(manifest));
            var context = new CaptureProcessingContext(profile, submission, receipt);
            var compat = CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor);

            context.BeginNode("preview", []);
            var previewRecipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                BuiltInProcessingRecipes.FixedPreview, "1", "synthetic-preview", JsonSerializer.SerializeToElement(new { })));
            var previewDeriv = context.AddDerivative(FrameArtifactRole.Preview, raw, "preview-v1", [descriptor.Artifact.ArtifactId],
                CaptureProcessingContext.CreateArtifactId(new string('D', 64)));
            context.AssociateProcessingProduct(previewDeriv, new(
                FrameArtifactRole.Preview, "preview-v1", new string('D', 64),
                "application/x-hvo-packed-image", descriptor.Layout, raw.PixelData,
                ProcessingIdentity.ComputePayloadSha256(raw.PixelData), previewRecipe, [],
                [descriptor.Artifact.ArtifactId], TimeSpan.FromSeconds(1), compat));

            var scene = ProjectedSceneJson.Create(ProjectedSceneKind.Predicted, visible,
                ProjectedSceneImageTransformV1.Identity(width, height),
                new ProjectedSceneSource(descriptor.Capture.CaptureId, descriptor.Artifact.ArtifactId,
                    CaptureContractJson.ComputeDescriptorSha256(descriptor)),
                profile.Rig.Optics.CalibrationVersion, RigProjectionContextFactory.AlgorithmVersion);
            var sceneBytes = ProjectedSceneJson.Serialize(scene);
            var sceneRecipe = ProcessingIdentity.CreateRecipeIdentity(RecipeIdentityDescriptor.Create(
                BuiltInProcessingRecipes.ProjectedScene, "1", "fixture", JsonSerializer.SerializeToElement(new { })));
            context.BeginNode("scene", []);
            var sceneProduct = new ProcessingProduct(
                FrameArtifactRole.Metadata, "projected-scene-v1", new string('B', 64),
                StructuredProcessingProductContracts.ProjectedSceneMediaType, null, sceneBytes,
                ProcessingIdentity.ComputePayloadSha256(sceneBytes), sceneRecipe, [],
                [descriptor.Artifact.ArtifactId], TimeSpan.Zero, compat)
            {
                Kind = ProcessingProductKind.Metadata,
                SchemaVersion = scene.SchemaVersion,
                ContentIdentitySha256 = scene.SceneIdentitySha256
            };
            context.RegisterProcessingProduct(sceneProduct);

            context.BeginNode("scene-layer", ["scene"]);
            await new ScenePresentationLayerCaptureProcessingStep(new("scene-layer", "ScenePresentationLayer", 20),
                new() { IncludeExpectedNotDetectedLayer = true, ExpectedPositionDiagnostics = false })
                .ProcessAsync(context, token).ConfigureAwait(false);

            var sceneArtifact = CameraAgentRecipeExecutionAdapter.CreateArtifact(context, sceneProduct);
            context.BeginNode("environment", ["scene"]);
            LayeredPresentationCaptureProcessing.Add(context, PresentationProcessingProducts.CreateLayerProduct(
                PresentationLayerPayloadJson.Create(scene.SceneIdentitySha256, width, height), "environment-layer-v1", [sceneArtifact], "fixture-v1"));

            // 1. Default manifest (expected-not-detected disabled by default) + explicit materializer selection draws pixels.
            context.BeginNode("manifest-default", ["preview", "scene-layer", "environment"]);
            await new OverlayManifestCaptureProcessingStep(new("manifest-default", "OverlayManifest", 30),
                new() { BasePreviewVariant = "preview-v1", IncludeExpectedNotDetectedLayer = true })
                .ProcessAsync(context, token).ConfigureAwait(false);
            var defaultManifest = LayeredPresentationJson.ParseManifest(context.ProcessingProducts[^1].Payload).Document!;
            Assert.IsFalse(defaultManifest.Layers.Single(static l => l.LayerKind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind).EnabledByDefault);

            context.BeginNode("mat-explicit", ["preview", "manifest-default", "scene-layer", "environment"]);
            await new PresentationMaterializerCaptureProcessingStep(new("mat-explicit", "PresentationMaterializer", 40), matOptions)
                .ProcessAsync(context, token).ConfigureAwait(false);
            var explicitPixels = context.ProcessingProducts[^1].Payload.ToArray();
            Assert.IsGreaterThanOrEqualTo(0, explicitPixels.AsSpan().IndexOfAnyExcept((byte)0),
                "Explicitly selecting expected-not-detected in PresentationMaterializer must draw its diagnostic pixels.");

            // 2. Manifest layer override enabling expected-not-detected + empty EnabledLayerKinds also draws pixels.
            context.BeginNode("manifest-override", ["preview", "scene-layer", "environment"]);
            await new OverlayManifestCaptureProcessingStep(new("manifest-override", "OverlayManifest", 31),
                new()
                {
                    BasePreviewVariant = "preview-v1",
                    IncludeExpectedNotDetectedLayer = true,
                    Layers =
                    [
                        new() { Kind = "scene-annotation", Enabled = false },
                        new() { Kind = "scene-constellations", Enabled = false },
                        new() { Kind = "environment", Enabled = false },
                        new() { Kind = MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, Enabled = true }
                    ]
                }).ProcessAsync(context, token).ConfigureAwait(false);
            var overrideManifest = LayeredPresentationJson.ParseManifest(context.ProcessingProducts[^1].Payload).Document!;
            Assert.IsTrue(overrideManifest.Layers.Single(static l => l.LayerKind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind).EnabledByDefault);

            context.BeginNode("mat-override", ["preview", "manifest-override", "scene-layer", "environment"]);
            await new PresentationMaterializerCaptureProcessingStep(new("mat-override", "PresentationMaterializer", 41),
                new() { BasePreviewVariant = "preview-v1", OutputVariant = "override-materialized" })
                .ProcessAsync(context, token).ConfigureAwait(false);
            var overridePixels = context.ProcessingProducts[^1].Payload.ToArray();
            CollectionAssert.AreEqual(explicitPixels, overridePixels,
                "Enabling expected-not-detected via manifest override must match explicit materializer selection byte-for-byte.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestMetadataCatalog(IReadOnlyList<CelestialCatalogObject> objects)
        : ICelestialCatalog, ICelestialCatalogMetadataSource
    {
        private readonly InMemoryCelestialCatalog _inner = new(objects);

        public CatalogMetadata Metadata { get; } = new(
            "centered-526-fixture",
            "1",
            new Uri("https://example.test/centered-526"),
            new string('C', 64),
            "test",
            "v1");

        public IReadOnlyList<CelestialCatalogObject> Query(CatalogQuery query) => _inner.Query(query);

        public ValueTask<IReadOnlyList<CelestialCatalogObject>> QueryCandidatesAsync(
            CatalogCandidateQuery query,
            CancellationToken cancellationToken = default) =>
            _inner.QueryCandidatesAsync(query, cancellationToken);
    }

    private sealed class StubAnnotationSceneProvider : IAnnotationSceneProvider
    {
        public ValueTask<AnnotationSceneResult> BuildAsync(
            CameraModuleConfig config,
            ReconstructionDescriptor? descriptor,
            CameraFrame rawFrame,
            IReadOnlyList<string> constellationIds,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
