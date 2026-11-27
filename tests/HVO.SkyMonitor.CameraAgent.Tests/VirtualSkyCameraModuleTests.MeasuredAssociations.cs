using System.Buffers.Binary;
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
                    ExposureMeasurementRecipe.Algorithms(incompleteMeas.Input, ExposureFocusSamplingPolicy.Brightest),
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
            Rig = format == CameraPixelFormat.Mono16 && profile.Rig.Readout is not null
                ? profile.Rig with { Readout = profile.Rig.Readout with { BlackLevel = 64 } }
                : profile.Rig,
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 5267,
                illuminationMode = "ControlledNight",
                magnitudeZeroElectronsPerSecond = format == CameraPixelFormat.BayerRggb16 ? 8000 : 3000,
                backgroundElectronsPerSecond = 25,
                readNoiseStandardDeviation = 4.0,
                shotNoiseEnabled = true,
                asi174Sensor = new { enabled = format == CameraPixelFormat.Mono16, blackLevelAdu = 64.0 },
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
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), format == CameraPixelFormat.Mono16 ? 150 : 1, null, null);
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

            // Astra D4-M4: Verify candidate-bound SNR accuracy directly on the FINAL rendered Mono16/Bayer VirtualSky
            // pixel buffer against an independent sky-annulus + aperture reference SNR computed from those final pixels.
            var finalFrame = cap.Frame!;
            var finalLayout = finalFrame.Layout!;
            var finalWidth = finalLayout.Width;
            var finalHeight = finalLayout.Height;
            var finalProj = RigProjectionContextFactory.CreateDeliveredView(active.Rig, finalLayout)!.Value;
            var validAperture = new bool[finalWidth * finalHeight];
            var rawDouble = new double[finalWidth * finalHeight];
            var frameSpan = finalFrame.PixelData.Span;
            for (var y = 0; y < finalHeight; y++)
            {
                var row = frameSpan.Slice(y * finalLayout.StrideBytes, finalWidth * 2);
                for (var x = 0; x < finalWidth; x++)
                {
                    var idx = y * finalWidth + x;
                    rawDouble[idx] = BinaryPrimitives.ReadUInt16LittleEndian(row.Slice(x * 2, 2));
                    validAperture[idx] = finalProj.ContainsSample(x + 0.5, y + 0.5);
                }
            }
            double[] evalPixels;
            if (format == CameraPixelFormat.BayerRggb16)
            {
                var reconstructed = LinearBayerReconstruction.Reconstruct(rawDouble, validAperture, finalWidth, finalHeight, BayerPattern.Rggb)
                    .ToLuminance();
                evalPixels = reconstructed.Pixels.ToArray();
                validAperture = reconstructed.ValidMask.ToArray();
            }
            else
            {
                evalPixels = rawDouble;
            }
            var measOptions = MeasuredStellarAssociationRecipe.MeasurementOptions(format);
            var segMinFloor = measOptions.MinimumPeakAboveBackground * measOptions.SegmentationSigma / measOptions.PeakSigma;
            var growRadius = Math.Max(2, (int)Math.Ceiling(2 * measOptions.WindowSigmaPixels));
            var snrTolerance = format == CameraPixelFormat.Mono16 ? 0.15 : 0.20;
            var maxSnrRelErr = 0.0;

            var scoredSources = evidence.Associations
                .Select(static a => (Id: a.CatalogId, X: a.MeasuredX, Y: a.MeasuredY, Flux: a.Flux, Snr: a.SignalToNoise))
                .Append((Id: jupiter.ObjectId, X: jupiter.MeasuredX, Y: jupiter.MeasuredY, Flux: jupiter.Flux, Snr: jupiter.SignalToNoise))
                .ToArray();
            foreach (var src in scoredSources)
            {
                Assert.IsNotNull(src.Snr, $"{format} {src.Id} must report measured SNR.");
                var (refBg, refNoise, refFlux, refPixels, refSnr) = ComputeIndependentVirtualSkyPixelSnr(
                    evalPixels, validAperture, finalWidth, finalHeight, src.X, src.Y,
                    measOptions.SegmentationSigma, segMinFloor, growRadius);
                var snrRelErr = Math.Abs(src.Snr.Value - refSnr) / refSnr;
                maxSnrRelErr = Math.Max(maxSnrRelErr, snrRelErr);
                TestContext.WriteLine(
                    $"[VirtualSky-SNR {format}] {src.Id}: measuredSnr={src.Snr.Value:F2}, refSnr={refSnr:F2}, " +
                    $"relErr={snrRelErr:P2} (refBg={refBg:F2}, refNoise={refNoise:F3}, refFlux={refFlux:F1}, measFlux={src.Flux:F1}, nAp={refPixels})");
                Assert.IsLessThanOrEqualTo(snrTolerance, snrRelErr,
                    $"{format} {src.Id} final-pixel SNR relative error {snrRelErr:P2} (measured={src.Snr.Value:F2}, ref={refSnr:F2}) exceeded {snrTolerance:P0}.");
            }
            TestContext.WriteLine($"[VirtualSky-SNR {format}] SUMMARY maxSnrRelErr={maxSnrRelErr:P2} (tolerance={snrTolerance:P0}, sources={scoredSources.Length})");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Independently computes local sky background, sigma-clipped MAD noise, and grown-aperture SNR directly from
    /// the final rendered VirtualSky pixel buffer around a measured source without consulting StellarSourceMeasurer.
    /// </summary>
    private static (double Background, double NoiseSigma, double Flux, int AperturePixels, double Snr)
        ComputeIndependentVirtualSkyPixelSnr(
            double[] pixels, bool[] valid, int width, int height, double cx, double cy,
            double segmentationSigma, double segMinFloor, int growRadius)
    {
        // 1. Local sky annulus [8..16] px around the source, iteratively 3-sigma clipped for robust background & noise.
        var annulus = new List<double>();
        var xMin = Math.Max(0, (int)Math.Floor(cx) - 16);
        var xMax = Math.Min(width - 1, (int)Math.Ceiling(cx) + 16);
        var yMin = Math.Max(0, (int)Math.Floor(cy) - 16);
        var yMax = Math.Min(height - 1, (int)Math.Ceiling(cy) + 16);
        for (var y = yMin; y <= yMax; y++)
        {
            var dy = y + 0.5 - cy;
            for (var x = xMin; x <= xMax; x++)
            {
                if (!valid[y * width + x]) continue;
                var dx = x + 0.5 - cx;
                var r2 = dx * dx + dy * dy;
                if (r2 >= 64.0 && r2 <= 256.0)
                    annulus.Add(pixels[y * width + x]);
            }
        }
        Assert.IsGreaterThanOrEqualTo(64, annulus.Count, "Independent sky annulus must have sufficient valid samples.");

        var clipped = annulus.ToList();
        double bg = 0, sigma = 0;
        for (var iter = 0; iter < 5; iter++)
        {
            clipped.Sort();
            bg = clipped[clipped.Count / 2];
            var devs = clipped.Select(v => Math.Abs(v - bg)).OrderBy(static d => d).ToArray();
            sigma = 1.4826 * devs[devs.Length / 2];
            if (!(sigma > 0)) break;
            var next = clipped.Where(v => Math.Abs(v - bg) <= 3.0 * sigma).ToList();
            if (next.Count == clipped.Count || next.Count < 32) break;
            clipped = next;
        }
        // Final unbiased clipped sample standard deviation for smooth accuracy even on integer-quantized low-noise tiles.
        var mean = clipped.Average();
        var stdDev = Math.Sqrt(clipped.Sum(v => (v - mean) * (v - mean)) / (clipped.Count - 1));
        var refNoise = sigma > 0 ? stdDev : Math.Max(stdDev, 1e-6);

        // 2. Local connected component above threshold grown by growRadius directly on the final pixels.
        var box = 12;
        var bx0 = Math.Max(0, (int)Math.Floor(cx) - box);
        var by0 = Math.Max(0, (int)Math.Floor(cy) - box);
        var bx1 = Math.Min(width - 1, (int)Math.Ceiling(cx) + box);
        var by1 = Math.Min(height - 1, (int)Math.Ceiling(cy) + box);
        var bw = bx1 - bx0 + 1;
        var bh = by1 - by0 + 1;
        var threshold = Math.Max(segmentationSigma * refNoise, segMinFloor);
        var above = new bool[bw * bh];
        for (var wy = 0; wy < bh; wy++)
            for (var wx = 0; wx < bw; wx++)
            {
                var idx = (by0 + wy) * width + (bx0 + wx);
                above[wy * bw + wx] = valid[idx] && pixels[idx] - bg > threshold;
            }

        // Seed 8-connected flood fill at the local peak within 2 px of (cx, cy).
        var seedWx = -1;
        var seedWy = -1;
        var bestVal = double.NegativeInfinity;
        for (var wy = 0; wy < bh; wy++)
            for (var wx = 0; wx < bw; wx++)
            {
                if (!above[wy * bw + wx]) continue;
                var dx = bx0 + wx + 0.5 - cx;
                var dy = by0 + wy + 0.5 - cy;
                if (dx * dx + dy * dy <= 9.0)
                {
                    var val = pixels[(by0 + wy) * width + (bx0 + wx)];
                    if (val > bestVal) { bestVal = val; seedWx = wx; seedWy = wy; }
                }
            }
        Assert.IsGreaterThanOrEqualTo(0, seedWx, "Source peak must exceed threshold in final pixel buffer.");

        var own = new bool[bw * bh];
        var stack = new Stack<(int X, int Y)>();
        own[seedWy * bw + seedWx] = true;
        stack.Push((seedWx, seedWy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= bw || ny >= bh) continue;
                    var local = ny * bw + nx;
                    if (!own[local] && above[local]) { own[local] = true; stack.Push((nx, ny)); }
                }
        }

        double flux = 0;
        var count = 0;
        for (var wy = 0; wy < bh; wy++)
        {
            for (var wx = 0; wx < bw; wx++)
            {
                var inGrown = false;
                for (var dy = -growRadius; dy <= growRadius && !inGrown; dy++)
                {
                    var ny = wy + dy;
                    if (ny < 0 || ny >= bh) continue;
                    for (var dx = -growRadius; dx <= growRadius; dx++)
                    {
                        var nx = wx + dx;
                        if (nx >= 0 && nx < bw && own[ny * bw + nx]) { inGrown = true; break; }
                    }
                }
                if (!inGrown) continue;
                var idx = (by0 + wy) * width + (bx0 + wx);
                if (!valid[idx]) continue;
                flux += pixels[idx] - bg;
                count++;
            }
        }
        var refSnr = flux / (refNoise * Math.Sqrt(count));
        return (bg, refNoise, flux, count, refSnr);
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
        // across ALL THREE recovery paths (scene-first, assoc-first, rebuild-base), both with intact
        // processing_execution_nodes.outputs_json AND with outputs_json cleared (Astra M2 product-only fallback).
        foreach (var clearNodeOutputs in new[] { false, true })
            foreach (var customVariant in new string?[] { null, "custom-assoc" })
                foreach (var orderLabel in new[] { "scene-first", "assoc-first", "rebuild-base" })
                {
                    var variantTag = (customVariant is null ? "def" : "cst") + (clearNodeOutputs ? "-prodonly" : "");
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
                        if (clearNodeOutputs)
                        {
                            // Astra M2: clear processing_execution_nodes.outputs_json while keeping centered_exposure_products
                            // intact so ReadAuthoritativeAssociationWriterAsync must recover via its product-only fallback.
                            using var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false);
                            using var clearCmd = conn.CreateCommand();
                            clearCmd.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec AND node_id = 'associated';";
                            clearCmd.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                            Assert.AreEqual(1, await clearCmd.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                        }
                        var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
                        var writer = await store.ReadAuthoritativeAssociationWriterAsync(key, executionId, token).ConfigureAwait(false);
                        Assert.IsNotNull(writer, $"{variantTag}/{orderLabel}");
                        Assert.AreEqual("associated", writer.Value.Producer, $"{variantTag}/{orderLabel}");
                        Assert.AreEqual(customVariant ?? "measured-stellar-associations-v1", writer.Value.Variant, $"{variantTag}/{orderLabel}");
                        var assocSource = sources.Single(s => CenteredViewLayerBindings.IsAuthoritativeAssociationSlot(s.Slot, writer));
                        var sceneExpectedSource = sources.Single(static s =>
                            s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "expected-not-detected-layer-v1");

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
    public async Task CenteredPresentationSceneAtShiftedVirtualRenderEpochIsRejectedAsIncompatible()
    {
        // Astra M1 (CameraAgent end-to-end): CenteredExposureSceneProvider.ResolvePresentationAsync supports a
        // separate virtualPresentationEpoch. When that epoch shifts by +2 hours relative to the exposure midpoint,
        // IsCompatibleWithScene must return false even when the sole shared star is near the pole, while the matching
        // exposure-midpoint presentation scene remains compatible.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-m1-epoch");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var loc = profile.DeploymentLocation!;
        // Sole near-polar star (NO off-pole star!) so even over +2 hours its pixel position moves < 0.5 px,
        // proving IsCompatibleWithScene fails closed strictly via the physical epoch check rather than pixel delta.
        var poleEq = EquatorialPrecession.PrecessToJ2000(new EquatorialPoint(0.0, 89.99), start);
        var poleStar = new CelestialCatalogObject("star:m1-polar", "M1 Polar",
            poleEq.RightAscensionHours, poleEq.DeclinationDegrees, 0.3, 0.65);
        var predCatalog = new TestMetadataCatalog([poleStar]);
        var renderCatalog = new InMemoryCelestialCatalog([poleStar]);
        profile = profile with
        {
            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
            {
                seed = 52642,
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
                    new("MeasuredStellarAssociations", "associated", DependsOn: ["scene"], Required: false, Publication: durable)
                ]
            }
        };

        try
        {
            var seed = new DeploymentLocationSeed(loc.LocationId, loc.Source, null,
                loc.EffectiveFromUtc, loc.EffectiveUntilUtc,
                new(loc.LatitudeDegrees, loc.LongitudeDegrees, loc.ElevationMeters, loc.TimeZoneId));
            using var provider = CenteredRuntimeProvider(root, clock, services =>
            {
                services.AddSingleton<IDeploymentLocationProtector, DataProtectionDeploymentLocationProtector>();
                services.AddSingleton<ICelestialCatalog>(predCatalog);
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, renderCatalog, new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cam = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "m1-epoch", token).ConfigureAwait(false);
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
            var detail = await store.ReadExecutionDetailAsync(linear!.Product!.Slot.ExecutionId, token).ConfigureAwait(false);
            var assocOut = Assert.ContainsSingle(detail!.Nodes.Single(static n => n.NodeId == "associated").Outputs);
            var assocArtifact = await provider.GetRequiredService<CaptureProcessingPersistence>()
                .ReadCenteredViewParentAsync(capId, assocOut.ArtifactId, token).ConfigureAwait(false);
            var evidence = MeasuredStellarAssociationJson.Parse(assocArtifact.Payload).Associations!;

            var sceneProvider = provider.GetRequiredService<CenteredExposureSceneProvider>();
            var sameEpochScene = await sceneProvider.ResolvePresentationAsync(
                receipt.Manifest.CenteredExposure!, receipt.Manifest.Descriptor, new(), null, token).ConfigureAwait(false);
            var shiftedEpochScene = await sceneProvider.ResolvePresentationAsync(
                receipt.Manifest.CenteredExposure!, receipt.Manifest.Descriptor, new(), start.AddHours(2), token).ConfigureAwait(false);
            Assert.IsNotNull(sameEpochScene);
            Assert.IsNotNull(shiftedEpochScene);
            // Confirm the sole near-polar star moved < 0.5 px over +2h so coordinate delta alone cannot reject it.
            var samePole = Assert.ContainsSingle(sameEpochScene.Objects);
            var shiftedPole = Assert.ContainsSingle(shiftedEpochScene.Objects);
            Assert.AreEqual("star:m1-polar", samePole.Id);
            Assert.IsLessThan(0.5, double.Hypot(shiftedPole.Pixel.X - samePole.Pixel.X, shiftedPole.Pixel.Y - samePole.Pixel.Y),
                "Sole near-polar star must move < 0.5 px over +2h to isolate the epoch check.");
            Assert.IsTrue(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, sameEpochScene));
            Assert.IsFalse(ExposureAssociationProducts.IsCompatibleWithScene(assocArtifact, evidence, shiftedEpochScene),
                "CenteredExposureSceneProvider presentation scene at a +2h shifted virtual render epoch must be rejected via epoch check.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(137, 0)]
    [DataRow(137, 4321)]
    [DataRow(0, 4321)]
    public async Task UseExposureAssociationsSucceedsWithProductionShutterSkewAndSubMillisecondTiming(
        int shutterDelayMilliseconds,
        int subMillisecondTicks)
    {
        // Opus M1: in production VirtualSky captures, VirtualSkyCameraModule renders at RequestedStartUtc + exposure/2
        // (including any sub-millisecond ticks) while ExposureCatalogPrediction uses the ms-truncated shutter midpoint
        // (differing by e.g. 137 ms and/or sub-ms ticks). Both scene-overlay and annotated MUST succeed (Completed).
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-opm1-{shutterDelayMilliseconds}-{subMillisecondTicks}");
        var token = TestContext.CancellationToken;
        var baseStart = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var requestedStart = baseStart.AddTicks(subMillisecondTicks);
        var shutterStart = requestedStart.AddMilliseconds(shutterDelayMilliseconds);
        var clock = new CenteredClock { UtcNow = requestedStart };
        var deviceClock = new ExposureTestTimeProvider(shutterStart, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, baseStart, CameraPixelFormat.Mono16);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        var location = profile.DeploymentLocation!;
        var ra = AstronomyTime.LocalMeanSiderealDegrees(baseStart, location.LongitudeDegrees) / 15d;
        var matchedStar = new CelestialCatalogObject("star:opm1-matched", "OpM1 Matched", ra, location.LatitudeDegrees, 0.2, 0.65);
        var absentStar = new CelestialCatalogObject("star:opm1-absent", "OpM1 Absent", (ra + 1.5) % 24, location.LatitudeDegrees + 15, 0.6, 0.65);
        var jupiterPos = new SolarSystemPosition(new EquatorialPoint((ra - 1.0 + 24) % 24, location.LatitudeDegrees - 15), -1.8);
        var ephemeris = new FixedPlanetEphemeris(new Dictionary<SolarSystemBody, SolarSystemPosition>
        {
            [SolarSystemBody.Jupiter] = jupiterPos
        }, "opm1-ephem-v1");
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
                seed = 52681,
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
                        Options: JsonSerializer.SerializeToElement(new { outputVariant = "opm1-preview" })),
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
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "opm1-test", token).ConfigureAwait(false);
            var request = new CaptureRequest(requestedStart, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var captured = await module.CaptureBoundAsync(reservation.Pending.Key, request, token).ConfigureAwait(false);
            clock.UtcNow = captured.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active,
                new(request, captured, requestedStart, request.TargetInterval, request.TargetInterval),
                reservation, token).ConfigureAwait(false);
            var captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, captureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var detail = await store.ReadExecutionDetailAsync(linear!.Product!.Slot.ExecutionId, token).ConfigureAwait(false);
            Assert.IsNotNull(detail);

            var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
            var renderSceneOut = Assert.ContainsSingle(detail.Nodes.Single(static n => n.NodeId == "scene").Outputs);
            var renderScene = ProjectedSceneJson.Parse((await persistence.ReadCenteredViewParentAsync(captureId, renderSceneOut.ArtifactId, token).ConfigureAwait(false)).Payload).Scene!;
            var assocOut = Assert.ContainsSingle(detail.Nodes.Single(static n => n.NodeId == "associated").Outputs);
            var assocEvidence = MeasuredStellarAssociationJson.Parse((await persistence.ReadCenteredViewParentAsync(captureId, assocOut.ArtifactId, token).ConfigureAwait(false)).Payload).Associations!;

            // Confirm the two scenes genuinely have distinct EffectiveUtc timestamps due to shutter delay / sub-ms truncation!
            Assert.AreNotEqual(renderScene.EffectiveUtc.UtcTicks, assocEvidence.Scene.EffectiveUtc!.Value.UtcTicks,
                "Production VirtualSky render epoch and ms-truncated ExposureCatalogPrediction epoch must genuinely differ.");
            Assert.AreNotEqual(renderScene.SceneIdentitySha256, assocEvidence.Scene.SceneIdentitySha256);

            // Both scene-overlay and annotated MUST succeed (Completed, NOT TerminalFailure)!
            var sceneOverlayNode = detail.Nodes.Single(static n => n.NodeId == "scene-overlay");
            Assert.AreEqual(nameof(DurableProcessingNodeStatus.Completed), sceneOverlayNode.Status, sceneOverlayNode.Reason);
            var starLayerOut = sceneOverlayNode.Outputs.Single(static o => o.Variant == "scene-annotation-layer-v1");
            var starPayload = PresentationLayerPayloadJson.Parse(
                (await persistence.ReadCenteredViewParentAsync(captureId, starLayerOut.ArtifactId, token).ConfigureAwait(false)).Payload).Payload!;
            Assert.IsTrue(starPayload.TextBlocks.Any(static b => b.Lines[0] == "OpM1 Matched"));
            Assert.IsTrue(starPayload.TextBlocks.Any(static b => b.Lines[0] == "Jupiter"));
            Assert.IsFalse(starPayload.TextBlocks.Any(static b => b.Lines[0].Contains("OpM1 Absent", StringComparison.Ordinal)));

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
    public async Task MissingGraphRecoveryNeverGrantsOrdinaryMeasuredAssociationAuthorityOverCenteredDiagnostics()
    {
        // Astra D4-M1: with the frozen graph unavailable and processing_execution_nodes.outputs_json cleared,
        // an ordinary measured-stellar-associations product (default OR custom variant, retained OR expired after
        // processing_outputs deletion) must NEVER gain authority over centered diagnostics, whereas a genuine custom
        // ExposureStellarAssociations writer (retained OR expired) still recovers cleanly.
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);

        // 1. Ordinary measured-stellar-associations (both default and custom variant; both retained and expired):
        foreach (var ordinaryVariant in new[] { "measured-stellar-associations-v1", "ordinary-custom-assoc" })
            foreach (var expireOutput in new[] { false, true })
                foreach (var clearProvenanceBeforeExpiry in new[] { false, true })
                {
                    if (!expireOutput && clearProvenanceBeforeExpiry)
                        continue; // Also tests pre-upgrade legacy row (provenance_json NULL) preserved at deletion time.
                    var tag = $"{(ordinaryVariant == "measured-stellar-associations-v1" ? "def" : "cst")}-{(expireOutput ? "exp" : "ret")}{(clearProvenanceBeforeExpiry ? "-leg" : "")}";
                    var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-d4m1-ord-{tag}");
                    try
                    {
                        var clock = new CenteredClock { UtcNow = start };
                        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
                        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
                        var loc = profile.DeploymentLocation!;
                        var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
                        var star1 = new CelestialCatalogObject("star:d4m1-1", "D4M1 One", ra, loc.LatitudeDegrees, 0.4, 0.65);
                        var predCat = new TestMetadataCatalog([star1]);
                        var rendCat = new InMemoryCelestialCatalog([]);
                        profile = profile with
                        {
                            Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                            {
                                seed = 52641,
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
                                    new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene"], Required: false, Publication: durable,
                                        Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                                        {
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
                        await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d4m1-ord", token).ConfigureAwait(false);
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
                        var executionId = linear!.Product!.Slot.ExecutionId;
                        var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                        var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
                        var detail = await store.ReadExecutionDetailAsync(executionId, token).ConfigureAwait(false);
                        var sceneOut = Assert.ContainsSingle(detail!.Nodes.Single(static n => n.NodeId == "scene").Outputs);
                        var sceneArt = await persistence.ReadCenteredViewParentAsync(capId, sceneOut.ArtifactId, token).ConfigureAwait(false);
                        var parsedScene = ProjectedSceneJson.Parse(sceneArt.Payload).Scene!;
                        var rawDesc = receipt.Manifest.Descriptor;
                        var rawStub = new ProcessingArtifact(rawDesc.Artifact.ArtifactId, rawDesc.Artifact.Role, rawDesc.Artifact.Variant,
                            ProcessingIdentity.CreateRecipeIdentity(rawDesc.Artifact.Recipe).IdentitySha256, rawDesc.Artifact.MediaType,
                            rawDesc.Layout, ReadOnlyMemory<byte>.Empty, rawDesc.Artifact.CreatedUtc, rawDesc.Controls.EffectiveExposure,
                            CameraAgentRecipeExecutionAdapter.CreateCompatibility(rawDesc))
                        {
                            CaptureId = capId,
                            DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(rawDesc)
                        };
                        var ordinaryEvidence = new MeasuredStellarAssociationsV1(
                            MeasuredStellarAssociationsV1.CurrentSchemaVersion,
                            new(capId, rawDesc.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(rawDesc).ToUpperInvariant(),
                                FrameArtifactRole.Raw, rawDesc.Artifact.Variant, rawStub.RecipeIdentitySha256.ToUpperInvariant(),
                                rawDesc.Layout.PixelFormat, rawDesc.Layout.Width, rawDesc.Layout.Height),
                            new(parsedScene.SceneIdentitySha256.ToUpperInvariant(), parsedScene.Kind, parsedScene.Catalog.Name,
                                parsedScene.Catalog.Version, parsedScene.Catalog.ChecksumSha256.ToUpperInvariant(),
                                parsedScene.Projection.CalibrationVersion, parsedScene.Projection.AlgorithmVersion,
                                parsedScene.AstronomyAlgorithmVersion,
                                MeasuredStellarAssociationJson.ComputeProjectionGeometryIdentitySha256(parsedScene),
                                parsedScene.EffectiveUtc),
                            new(StellarSourceMeasurer.AlgorithmVersion, new string('A', 64), MeasuredStellarAssociationRecipe.MonoReconstruction,
                                StellarMeasurementStatus.Completed, 1000, 4, 0, 0, 0),
                            new(MeasuredStellarAssociator.AlgorithmVersion, new(), StellarLabelPolicy.Version, new()),
                            [], [], []);
                        var ordinaryRecipe = ProcessingIdentity.CreateRecipeIdentity(
                            new ProcessingRecipeDefinition(BuiltInProcessingRecipes.MeasuredStellarAssociations, "1.0.0",
                                MeasuredStellarAssociationRecipe.CurrentImplementationVersion, ProcessingOperationKind.Analyzer),
                            JsonSerializer.SerializeToElement(new { }));
                        var ordinaryBytes = MeasuredStellarAssociationJson.Serialize(ordinaryEvidence);
                        var ordinaryProd = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, ordinaryVariant,
                            StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, null, ordinaryBytes, ordinaryRecipe,
                            [new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion)],
                            [rawStub], rawStub.Integration, rawStub.Compatibility, ProcessingProductKind.Metadata,
                            MeasuredStellarAssociationsV1.CurrentSchemaVersion, ordinaryEvidence.AssociationIdentitySha256);
                        await persistence.PersistCenteredViewProductAsync(rawDesc, ordinaryProd, token).ConfigureAwait(false);
                        var ordinaryArtId = ProcessingIdentity.CreateArtifactId(ordinaryProd.OutputIdentitySha256);
                        var ordinarySemantics = new CenteredProductSemanticProvenanceV1(
                            CenteredProductSemanticProvenanceV1.CurrentSchemaVersion,
                            MeasuredStellarAssociationCaptureProcessingStep.Alias,
                            BuiltInProcessingRecipes.MeasuredStellarAssociations,
                            MeasuredStellarAssociationRecipe.CurrentImplementationVersion,
                            []);

                        using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
                        {
                            using (var ins = conn.CreateCommand())
                            {
                                ins.CommandText = """
                                    INSERT INTO centered_exposure_products(agent_id, camera_id, capture_id, execution_id, producer,
                                        schema_version, variant, output_identity, artifact_id, payload_sha256, content_identity_sha256,
                                        disposition, reason, provenance_json)
                                    VALUES ($agent, $camera, $capture, $exec, 'ordinary-assoc', $schema, $variant, $output, $art,
                                        $payload, $content, 'Available', NULL, $prov);
                                    """;
                                ins.Parameters.AddWithValue("$agent", key.Camera.AgentId);
                                ins.Parameters.AddWithValue("$camera", key.Camera.CameraId);
                                ins.Parameters.AddWithValue("$capture", capId.ToString("N"));
                                ins.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                                ins.Parameters.AddWithValue("$schema", MeasuredStellarAssociationsV1.CurrentSchemaVersion);
                                ins.Parameters.AddWithValue("$variant", ordinaryVariant);
                                ins.Parameters.AddWithValue("$output", ordinaryProd.OutputIdentitySha256);
                                ins.Parameters.AddWithValue("$art", ordinaryArtId.ToString("N"));
                                ins.Parameters.AddWithValue("$payload", ordinaryProd.ChecksumSha256);
                                ins.Parameters.AddWithValue("$content", ordinaryProd.ContentIdentitySha256!);
                                ins.Parameters.AddWithValue("$prov", clearProvenanceBeforeExpiry ? (object)DBNull.Value : ordinarySemantics.Serialize());
                                Assert.AreEqual(1, await ins.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                            }
                            using (var clr = conn.CreateCommand())
                            {
                                clr.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                                clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                                await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                            }
                        }

                        if (expireOutput)
                        {
                            // Run the REAL retention-deletion path (CompleteLifecycleOperationAsync("delete")).
                            var lifecycleOp = new ProcessingLifecycleOperation(
                                $"expire-{tag}", "delete", ordinaryProd.OutputIdentitySha256, null, null,
                                "expired", "retention-expired", ordinaryBytes.Length,
                                clock.UtcNow.ToUnixTimeMilliseconds(), "planned");
                            Assert.IsTrue(await store.PlanLifecycleOperationAsync(lifecycleOp, token).ConfigureAwait(false));
                            await store.CompleteLifecycleOperationAsync(lifecycleOp, token).ConfigureAwait(false);
                        }

                        // Authoritative writer MUST be null; ordinary product MUST NEVER bind or suppress scene diagnostic!
                        var resolvedWriter = await store.ReadAuthoritativeAssociationWriterAsync(key, executionId, token).ConfigureAwait(false);
                        Assert.IsNull(resolvedWriter, $"{tag}: ordinary measured-stellar-associations must NEVER be selected as authoritative writer.");

                        var views = provider.GetRequiredService<SqliteCenteredViews>();
                        var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
                        var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();
                        var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
                        var ordinaryLookup = sources.Single(s => s.Slot.Producer == "ordinary-assoc" && s.Slot.Variant == ordinaryVariant);
                        var sceneExpectedLookup = sources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "expected-not-detected-layer-v1");

                        var missingGen = $"missing-d4m1-ord-{tag}";
                        var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                        var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                            rawDesc.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                        Assert.IsNotNull(baseView);

                        // Exercise BOTH RebuildForProductAsync AND RebuildForBaseAsync independently from empty centered-view state!
                        await sourceLayers.RebuildForProductAsync(ordinaryLookup, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                        await sourceLayers.RebuildForProductAsync(sceneExpectedLookup, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                        var afterProduct = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                        Assert.IsNotNull(afterProduct);
                        Assert.IsFalse(afterProduct.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind),
                            $"{tag}: ordinary association must not bind measured-stellar-associations.");
                        var diagAfterProduct = afterProduct.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                        Assert.AreEqual(sceneExpectedLookup.Product!.ArtifactId, diagAfterProduct.Evidence!.ArtifactId,
                            $"{tag}: legitimate scene-overlay expected-not-detected must remain bound.");

                        await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                        var baseOnlyView = await viewProducer.PublishSingleAsync(stream, executionId,
                            rawDesc.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                        Assert.IsNotNull(baseOnlyView);
                        await sourceLayers.RebuildForBaseAsync(baseOnlyView.Base, token).ConfigureAwait(false);
                        var afterBase = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                        Assert.IsNotNull(afterBase);
                        Assert.IsFalse(afterBase.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind),
                            $"{tag}: RebuildForBaseAsync must not bind ordinary association.");
                        var diagAfterBase = afterBase.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                        Assert.AreEqual(sceneExpectedLookup.Product.ArtifactId, diagAfterBase.Evidence!.ArtifactId,
                            $"{tag}: RebuildForBaseAsync must preserve legitimate scene-overlay diagnostic.");
                    }
                    finally
                    {
                        Directory.Delete(root, recursive: true);
                    }
                }

        // 2. Genuine custom ExposureStellarAssociations writer (custom node + custom variant) AFTER processing_outputs expiry
        //    AND processing_execution_nodes.outputs_json cleared MUST still recover as authoritative writer!
        {
            var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d4m1-genuine-exp");
            try
            {
                var clock = new CenteredClock { UtcNow = start };
                var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
                var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
                var loc = profile.DeploymentLocation!;
                var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
                var star1 = new CelestialCatalogObject("star:d4m1-g1", "D4M1 Genuine", ra, loc.LatitudeDegrees, 0.4, 0.65);
                var predCat = new TestMetadataCatalog([star1]);
                var rendCat = new InMemoryCelestialCatalog([star1]);
                profile = profile with
                {
                    Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                    {
                        seed = 52642,
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
                                Options: JsonSerializer.SerializeToElement(new { outputVariant = "custom-exposure-assoc" }),
                                Publication: durable),
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
                await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d4m1-gen", token).ConfigureAwait(false);
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
                var executionId = linear!.Product!.Slot.ExecutionId;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var sourcesBefore = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
                var assocSource = sourcesBefore.Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-exposure-assoc");

                // Clear processing_execution_nodes.outputs_json AND delete processing_outputs via retention expiry!
                using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
                {
                    using var clr = conn.CreateCommand();
                    clr.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                var expireOp = new ProcessingLifecycleOperation(
                    "expire-genuine-custom", "delete", assocSource.Product!.OutputIdentitySha256, null, null,
                    "expired", "retention-expired", 1024, clock.UtcNow.ToUnixTimeMilliseconds(), "planned");
                Assert.IsTrue(await store.PlanLifecycleOperationAsync(expireOp, token).ConfigureAwait(false));
                await store.CompleteLifecycleOperationAsync(expireOp, token).ConfigureAwait(false);

                // Even after outputs_json cleared AND processing_outputs deleted, durable provenance_json recovers the custom writer!
                var recoveredWriter = await store.ReadAuthoritativeAssociationWriterAsync(key, executionId, token).ConfigureAwait(false);
                Assert.IsNotNull(recoveredWriter);
                Assert.AreEqual("associated", recoveredWriter.Value.Producer);
                Assert.AreEqual("custom-exposure-assoc", recoveredWriter.Value.Variant);

                // Scene diagnostic remains suppressed and expired authoritative slot settles as unavailable/expired (never stolen by scene-overlay).
                var views = provider.GetRequiredService<SqliteCenteredViews>();
                var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
                var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();
                var missingGen = "missing-d4m1-genuine-exp";
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);
                await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                Assert.IsNotNull(rebuilt);
                var expectedLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, expectedLayer.Readiness,
                    "Expired authoritative ExposureStellarAssociations must not be overwritten by scene-overlay diagnostic.");
                Assert.IsNull(expectedLayer.Drawable,
                    "Expired authoritative ExposureStellarAssociations must not expose scene-overlay diagnostic drawable.");
                StringAssert.StartsWith(expectedLayer.Reason, "centered.product-expired", StringComparison.Ordinal,
                    "Expired authoritative ExposureStellarAssociations must record centered.product-expired reason.");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task ClearCenteredViewsAsync(SqliteCaptureProcessingStore store, CancellationToken token)
    {
        using var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM processing_centered_view_selections;
            DELETE FROM processing_centered_view_intervals;
            DELETE FROM processing_centered_view_members;
            DELETE FROM processing_centered_view_revisions;
            DELETE FROM processing_centered_view_changes;
            DELETE FROM processing_centered_view_bases;
            """;
        await cmd.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow("custom-variant", "scene-annotation-layer-v1", "archive-diag", false)]
    [DataRow("custom-variant-legacy-provenance-cleared", "scene-annotation-layer-v1", "archive-diag", true)]
    [DataRow("renamed-diagnostic-reuses-annotation-alias", "custom-stars-layer-v1", "scene-annotation-layer-v1", false)]
    [DataRow("renamed-diagnostic-reuses-annotation-alias-legacy-provenance-cleared", "custom-stars-layer-v1", "scene-annotation-layer-v1", true)]
    public async Task MissingGraphRecoveryPreservesCustomAndLegacyAliasedExpectedNotDetectedDiagnosticRole(
        string scenarioName,
        string annotationVariant,
        string expectedNotDetectedVariant,
        bool clearProvenanceJson)
    {
        // Astra D4-M2: without the frozen graph (and even with outputs_json cleared):
        // (a) a custom diagnostic variant ("archive-diag") must recover cleanly as disabled expected-not-detected;
        // (b) a renamed diagnostic output reusing the legacy alias "scene-annotation-layer-v1" (when annotation output
        //     was renamed to "custom-stars-layer-v1") must reopen as disabled expected-not-detected (NOT enabled star-annotations).
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d4m2-" + scenarioName);
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var star1 = new CelestialCatalogObject("star:d4m2-1", "D4M2 Star", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var predCat = new TestMetadataCatalog([star1]);
            var rendCat = new InMemoryCelestialCatalog([star1]);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52643,
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
                        new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                AnnotationOutputVariant = annotationVariant,
                                IncludeExpectedNotDetectedLayer = true,
                                ExpectedNotDetectedOutputVariant = expectedNotDetectedVariant
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
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d4m2-test", token).ConfigureAwait(false);
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
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();

            // Clear processing_execution_nodes.outputs_json (and optionally provenance_json to exercise descriptor fallback too!).
            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                using (var clr = conn.CreateCommand())
                {
                    clr.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                if (clearProvenanceJson)
                {
                    using var clrProv = conn.CreateCommand();
                    clrProv.CommandText = "UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec AND producer = 'scene-overlay';";
                    clrProv.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clrProv.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var diagSource = sources.Single(s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == expectedNotDetectedVariant);
            var annotSource = sources.Single(s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == annotationVariant);

            foreach (var mode in new[] { "product", "base" })
            {
                await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                var missingGen = $"missing-d4m2-{scenarioName}-{mode}";
                Assert.IsNull(await store.ReadCenteredLayerBindingsAsync(missingGen, token).ConfigureAwait(false));
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);

                if (mode == "product")
                {
                    await sourceLayers.RebuildForProductAsync(diagSource, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(annotSource, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else
                {
                    await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                }

                var view = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                Assert.IsNotNull(view);

                var diagLayer = view.Revision.Layers.SingleOrDefault(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.IsNotNull(diagLayer, $"{scenarioName}/{mode}: custom/renamed diagnostic layer must NOT disappear without graph.");
                Assert.AreEqual(CenteredViewReadiness.Ready, diagLayer.Readiness, $"{scenarioName}/{mode}: {diagLayer.Reason}");
                Assert.IsNotNull(diagLayer.Drawable, $"{scenarioName}/{mode}");
                Assert.IsFalse(diagLayer.Drawable.EnabledByDefault,
                    $"{scenarioName}/{mode}: diagnostic layer must reopen DISABLED by default, NEVER enabled.");
                Assert.AreEqual(diagSource.Product!.ArtifactId, diagLayer.Evidence!.ArtifactId, $"{scenarioName}/{mode}");

                var starLayer = view.Revision.Layers.SingleOrDefault(static l => l.Key.Kind == "star-annotations");
                if (starLayer is not null)
                {
                    Assert.AreNotEqual(diagSource.Product.ArtifactId, starLayer.Evidence!.ArtifactId,
                        $"{scenarioName}/{mode}: renamed diagnostic reusing 'scene-annotation-layer-v1' must NEVER reopen as star-annotations.");
                }
                if (!clearProvenanceJson)
                {
                    // With durable provenance_json intact, even a renamed annotation output ("custom-stars-layer-v1") recovers cleanly!
                    Assert.IsNotNull(starLayer, $"{scenarioName}/{mode}: renamed annotation output must recover cleanly from durable semantics.");
                    Assert.AreEqual(annotSource.Product!.ArtifactId, starLayer.Evidence!.ArtifactId, $"{scenarioName}/{mode}");
                    Assert.IsTrue(starLayer.Drawable!.EnabledByDefault, $"{scenarioName}/{mode}");
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
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

    [TestMethod]
    public async Task MissingGraphRecoveryFailsClosedForAlreadyExpiredLegacyV1ExposureWriter()
    {
        // Astra D5-M2: reconstructing a legacy v1 settlement for associated/custom-exposure-assoc when its
        // processing_outputs row was already deleted, provenance_json is NULL, and processing_execution_nodes.outputs_json
        // was cleared must distinguish the unidentifiable expired writer from proven absence and settle
        // expected-not-detected (and measured-stellar-associations) as Incompatible / centered.product-expired rather
        // than falling back to scene-overlay's AssessedEmpty diagnostic.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d5m2-legacy-v1-exp");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var star1 = new CelestialCatalogObject("star:d5m2-1", "D5M2 Matched", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var predCat = new TestMetadataCatalog([star1]);
            // Render star1 so scene-overlay's expected-not-detected-layer-v1 is EMPTY (AssessedEmpty if wrongly bound!).
            var rendCat = new InMemoryCelestialCatalog([star1]);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52671,
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
                            Options: JsonSerializer.SerializeToElement(new { outputVariant = "custom-exposure-assoc" }),
                            Publication: durable),
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
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d5m2-test", token).ConfigureAwait(false);
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
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var before = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var assocBefore = before.Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-exposure-assoc");

            // Simulate a pre-upgrade legacy v1 settlement whose output was ALREADY deleted before preserve-before-delete
            // existed: schema_version = v1, disposition = Expired, provenance_json = NULL, processing_outputs row gone,
            // and processing_execution_nodes.outputs_json cleared.
            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                using (var delOut = conn.CreateCommand())
                {
                    delOut.CommandText = "DELETE FROM processing_outputs WHERE output_identity_sha256 = $output;";
                    delOut.Parameters.AddWithValue("$output", assocBefore.Product!.OutputIdentitySha256);
                    Assert.AreEqual(1, await delOut.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var updProd = conn.CreateCommand())
                {
                    updProd.CommandText = """
                        UPDATE centered_exposure_products
                        SET schema_version = $v1, disposition = 'Expired', reason = 'retention-expired', provenance_json = NULL
                        WHERE execution_id = $exec AND producer = 'associated' AND variant = 'custom-exposure-assoc';
                        """;
                    updProd.Parameters.AddWithValue("$v1", MeasuredStellarAssociationsV1.V1SchemaVersion);
                    updProd.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    Assert.AreEqual(1, await updProd.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var clrNodes = conn.CreateCommand())
                {
                    clrNodes.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                    clrNodes.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clrNodes.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            // Writer lookup distinguishes unidentifiable expired legacy writer from proven absence!
            Assert.IsNull(await store.ReadAuthoritativeAssociationWriterAsync(key, executionId, token).ConfigureAwait(false));
            var resolution = await store.ReadAuthoritativeAssociationResolutionAsync(key, executionId, token).ConfigureAwait(false);
            Assert.AreEqual(CenteredAuthoritativeAssociationResolutionState.UnidentifiableExpired, resolution.State);
            Assert.IsTrue(resolution.SuppressesSceneExpectedNotDetected);
            Assert.IsNotNull(resolution.Writer);
            Assert.AreEqual("associated", resolution.Writer.Value.Producer);
            Assert.AreEqual("custom-exposure-assoc", resolution.Writer.Value.Variant);

            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();
            var afterSources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var expiredAssoc = afterSources.Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-exposure-assoc");
            var sceneExpected = afterSources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "expected-not-detected-layer-v1");
            Assert.AreEqual(ExposureProductAvailability.Expired, expiredAssoc.Availability);
            Assert.AreEqual(ExposureProductAvailability.Available, sceneExpected.Availability);

            foreach (var order in new[] { "rebuild-base", "scene-first", "assoc-first" })
            {
                await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                var missingGen = $"missing-d5m2-{order}";
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);

                if (order == "scene-first")
                {
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    var mid = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                    Assert.IsFalse(mid!.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                        "scene-first must not emit AssessedEmpty from scene-overlay when an unidentifiable expired legacy exposure writer exists.");
                    await sourceLayers.RebuildForProductAsync(expiredAssoc, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else if (order == "assoc-first")
                {
                    await sourceLayers.RebuildForProductAsync(expiredAssoc, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else
                {
                    await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                }

                var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                Assert.IsNotNull(rebuilt);
                var expectedLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, expectedLayer.Readiness,
                    $"{order}: expected Incompatible/centered.product-expired instead of scene {expectedLayer.Readiness}/{expectedLayer.Reason}.");
                Assert.IsNull(expectedLayer.Drawable, $"{order}: expired legacy exposure writer must not expose a drawable.");
                StringAssert.StartsWith(expectedLayer.Reason, "centered.product-expired", StringComparison.Ordinal, $"{order}");
                var measuredLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, measuredLayer.Readiness, $"{order}");
                StringAssert.StartsWith(measuredLayer.Reason, "centered.product-expired", StringComparison.Ordinal, $"{order}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task RetentionDeletionPreservesLegacyCenteredSemanticsBeforeDeletingProcessingOutputs()
    {
        // Opus D5-L3: directly exercise SqliteCaptureProcessingStore.PreserveCenteredProductSemanticsBeforeDeleteAsync
        // (CompleteLifecycleOperationAsync("delete")) across pre-upgrade NULL-provenance rows:
        // (1) genuine custom ExposureStellarAssociations -> preserves recipeName and recovers as Proven (not UnidentifiableExpired);
        // (2) ordinary measured-stellar-associations even using variant "exposure-stellar-associations-v1" -> preserves
        //     ordinary recipeName and recovers as None so scene-overlay keeps its diagnostic;
        // (3) custom ScenePresentationLayer diagnostic ("archive-diag") -> preserves ExpectedNotDetectedVersion and
        //     settles expected-not-detected as Incompatible / centered.product-expired rather than disappearing.
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);

        // 1. Pre-upgrade genuine custom ExposureStellarAssociations with NULL provenance_json deleted by retention:
        {
            var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d5l3-preserve-gen");
            try
            {
                var clock = new CenteredClock { UtcNow = start };
                var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
                var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
                var loc = profile.DeploymentLocation!;
                var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
                var star1 = new CelestialCatalogObject("star:pbd-1", "PBD One", ra, loc.LatitudeDegrees, 0.4, 0.65);
                profile = profile with
                {
                    Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                    {
                        seed = 52672,
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
                                Options: JsonSerializer.SerializeToElement(new { outputVariant = "custom-exposure-assoc" }),
                                Publication: durable),
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
                    services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([star1]));
                });
                await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
                profile = profile with { DeploymentLocation = initLoc };
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([star1]), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var cam = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
                var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
                await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "pbd-gen", token).ConfigureAwait(false);
                var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
                {
                    BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                    BoundGraph = reservation.Revision.Pipeline
                };
                var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
                clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
                var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

                var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
                var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
                var executionId = linear!.Product!.Slot.ExecutionId;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var assocSlot = (await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false))
                    .Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-exposure-assoc");

                // Clear provenance_json and outputs_json BEFORE retention deletion to simulate a pre-upgrade row.
                using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
                {
                    using var clr = conn.CreateCommand();
                    clr.CommandText = """
                        UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;
                        UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;
                        """;
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                var op = new ProcessingLifecycleOperation("pbd-del-gen", "delete", assocSlot.Product!.OutputIdentitySha256,
                    null, null, "expired", "retention-expired", 1024, clock.UtcNow.ToUnixTimeMilliseconds(), "planned");
                Assert.IsTrue(await store.PlanLifecycleOperationAsync(op, token).ConfigureAwait(false));
                await store.CompleteLifecycleOperationAsync(op, token).ConfigureAwait(false);

                // processing_outputs row is gone, yet provenance_json was populated before delete!
                var preserved = await store.ReadCenteredProductSemanticsAsync(key, assocSlot.Slot, token).ConfigureAwait(false);
                Assert.IsNotNull(preserved, "PreserveCenteredProductSemanticsBeforeDeleteAsync must persist provenance_json before deleting processing_outputs.");
                Assert.AreEqual(ExposureAssociationProducts.RecipeName, preserved.RecipeName);
                var resolution = await store.ReadAuthoritativeAssociationResolutionAsync(key, executionId, token).ConfigureAwait(false);
                Assert.AreEqual(CenteredAuthoritativeAssociationResolutionState.Proven, resolution.State,
                    "Preserved provenance must prove authority (Proven), not fall back to UnidentifiableExpired.");
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        // 2. Pre-upgrade ordinary measured-stellar-associations with variant "exposure-stellar-associations-v1" AND
        //    pre-upgrade custom ScenePresentationLayer diagnostic "archive-diag", both with NULL provenance_json:
        {
            var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d5l3-preserve-ord-diag");
            try
            {
                var clock = new CenteredClock { UtcNow = start };
                var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
                var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
                var loc = profile.DeploymentLocation!;
                var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
                var star1 = new CelestialCatalogObject("star:pbd-2", "PBD Two", ra, loc.LatitudeDegrees, 0.4, 0.65);
                profile = profile with
                {
                    Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                    {
                        seed = 52673,
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
                            new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene"], Required: false, Publication: durable,
                                Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                                {
                                    IncludeExpectedNotDetectedLayer = true,
                                    ExpectedNotDetectedOutputVariant = "archive-diag"
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
                    services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([star1]));
                });
                await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
                var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
                profile = profile with { DeploymentLocation = initLoc };
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([]), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var cam = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
                var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
                var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
                await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "pbd-ord", token).ConfigureAwait(false);
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
                var executionId = linear!.Product!.Slot.ExecutionId;
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
                var detail = await store.ReadExecutionDetailAsync(executionId, token).ConfigureAwait(false);
                var sceneOut = Assert.ContainsSingle(detail!.Nodes.Single(static n => n.NodeId == "scene").Outputs);
                var parsedScene = ProjectedSceneJson.Parse((await persistence.ReadCenteredViewParentAsync(capId, sceneOut.ArtifactId, token).ConfigureAwait(false)).Payload).Scene!;
                var rawDesc = receipt.Manifest.Descriptor;
                var rawStub = new ProcessingArtifact(rawDesc.Artifact.ArtifactId, rawDesc.Artifact.Role, rawDesc.Artifact.Variant,
                    ProcessingIdentity.CreateRecipeIdentity(rawDesc.Artifact.Recipe).IdentitySha256, rawDesc.Artifact.MediaType,
                    rawDesc.Layout, ReadOnlyMemory<byte>.Empty, rawDesc.Artifact.CreatedUtc, rawDesc.Controls.EffectiveExposure,
                    CameraAgentRecipeExecutionAdapter.CreateCompatibility(rawDesc))
                {
                    CaptureId = capId,
                    DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(rawDesc)
                };
                var ordinaryEvidence = new MeasuredStellarAssociationsV1(
                    MeasuredStellarAssociationsV1.CurrentSchemaVersion,
                    new(capId, rawDesc.Artifact.ArtifactId, CaptureContractJson.ComputeDescriptorSha256(rawDesc).ToUpperInvariant(),
                        FrameArtifactRole.Raw, rawDesc.Artifact.Variant, rawStub.RecipeIdentitySha256.ToUpperInvariant(),
                        rawDesc.Layout.PixelFormat, rawDesc.Layout.Width, rawDesc.Layout.Height),
                    new(parsedScene.SceneIdentitySha256.ToUpperInvariant(), parsedScene.Kind, parsedScene.Catalog.Name,
                        parsedScene.Catalog.Version, parsedScene.Catalog.ChecksumSha256.ToUpperInvariant(),
                        parsedScene.Projection.CalibrationVersion, parsedScene.Projection.AlgorithmVersion,
                        parsedScene.AstronomyAlgorithmVersion,
                        MeasuredStellarAssociationJson.ComputeProjectionGeometryIdentitySha256(parsedScene),
                        parsedScene.EffectiveUtc),
                    new(StellarSourceMeasurer.AlgorithmVersion, new string('A', 64), MeasuredStellarAssociationRecipe.MonoReconstruction,
                        StellarMeasurementStatus.Completed, 1000, 4, 0, 0, 0),
                    new(MeasuredStellarAssociator.AlgorithmVersion, new(), StellarLabelPolicy.Version, new()),
                    [], [], []);
                var ordinaryRecipe = ProcessingIdentity.CreateRecipeIdentity(
                    new ProcessingRecipeDefinition(BuiltInProcessingRecipes.MeasuredStellarAssociations, "1.0.0",
                        MeasuredStellarAssociationRecipe.CurrentImplementationVersion, ProcessingOperationKind.Analyzer),
                    JsonSerializer.SerializeToElement(new { }));
                var ordinaryBytes = MeasuredStellarAssociationJson.Serialize(ordinaryEvidence);
                // Intentionally use ExposureAssociationProducts.Variant so ONLY preserved descriptor recipe provenance
                // prevents this ordinary row from masquerading as an authoritative exposure writer after deletion!
                var ordinaryProd = ProcessingRecipeSupport.CreateProduct(FrameArtifactRole.Metadata, ExposureAssociationProducts.Variant,
                    StructuredProcessingProductContracts.MeasuredStellarAssociationsMediaType, null, ordinaryBytes, ordinaryRecipe,
                    [new("measured-stellar-associator", MeasuredStellarAssociator.AlgorithmVersion)],
                    [rawStub], rawStub.Integration, rawStub.Compatibility, ProcessingProductKind.Metadata,
                    MeasuredStellarAssociationsV1.CurrentSchemaVersion, ordinaryEvidence.AssociationIdentitySha256);
                await persistence.PersistCenteredViewProductAsync(rawDesc, ordinaryProd, token).ConfigureAwait(false);
                var ordinaryArtId = ProcessingIdentity.CreateArtifactId(ordinaryProd.OutputIdentitySha256);

                using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
                {
                    using (var ins = conn.CreateCommand())
                    {
                        ins.CommandText = """
                            INSERT INTO centered_exposure_products(agent_id, camera_id, capture_id, execution_id, producer,
                                schema_version, variant, output_identity, artifact_id, payload_sha256, content_identity_sha256,
                                disposition, reason, provenance_json)
                            VALUES ($agent, $camera, $capture, $exec, 'ordinary-assoc', $schema, $variant, $output, $art,
                                $payload, $content, 'Available', NULL, NULL);
                            """;
                        ins.Parameters.AddWithValue("$agent", key.Camera.AgentId);
                        ins.Parameters.AddWithValue("$camera", key.Camera.CameraId);
                        ins.Parameters.AddWithValue("$capture", capId.ToString("N"));
                        ins.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                        ins.Parameters.AddWithValue("$schema", MeasuredStellarAssociationsV1.CurrentSchemaVersion);
                        ins.Parameters.AddWithValue("$variant", ExposureAssociationProducts.Variant);
                        ins.Parameters.AddWithValue("$output", ordinaryProd.OutputIdentitySha256);
                        ins.Parameters.AddWithValue("$art", ordinaryArtId.ToString("N"));
                        ins.Parameters.AddWithValue("$payload", ordinaryProd.ChecksumSha256);
                        ins.Parameters.AddWithValue("$content", ordinaryProd.ContentIdentitySha256!);
                        Assert.AreEqual(1, await ins.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                    }
                    using (var clr = conn.CreateCommand())
                    {
                        clr.CommandText = """
                            UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;
                            UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;
                            """;
                        clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                        await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    }
                }

                // Expire the ordinary association product first: preserve-before-delete must record ordinary recipeName!
                var expireOrd = new ProcessingLifecycleOperation("pbd-del-ord", "delete", ordinaryProd.OutputIdentitySha256,
                    null, null, "expired", "retention-expired", ordinaryBytes.Length, clock.UtcNow.ToUnixTimeMilliseconds(), "planned");
                Assert.IsTrue(await store.PlanLifecycleOperationAsync(expireOrd, token).ConfigureAwait(false));
                await store.CompleteLifecycleOperationAsync(expireOrd, token).ConfigureAwait(false);

                var ordSlot = new ExposureProductSlot(executionId, "ordinary-assoc",
                    MeasuredStellarAssociationsV1.CurrentSchemaVersion, ExposureAssociationProducts.Variant);
                var ordSemantics = await store.ReadCenteredProductSemanticsAsync(key, ordSlot, token).ConfigureAwait(false);
                Assert.IsNotNull(ordSemantics);
                Assert.AreEqual(BuiltInProcessingRecipes.MeasuredStellarAssociations, ordSemantics.RecipeName);
                var resolutionAfterOrd = await store.ReadAuthoritativeAssociationResolutionAsync(key, executionId, token).ConfigureAwait(false);
                Assert.AreEqual(CenteredAuthoritativeAssociationResolutionState.None, resolutionAfterOrd.State,
                    "Preserved ordinary recipeName must prevent expired ordinary product from gaining authority even with exposure variant.");

                // Now expire custom scene diagnostic "archive-diag": preserve-before-delete must record ExpectedNotDetectedVersion!
                var diagLookup = (await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false))
                    .Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "archive-diag");
                var expireDiag = new ProcessingLifecycleOperation("pbd-del-diag", "delete", diagLookup.Product!.OutputIdentitySha256,
                    null, null, "expired", "retention-expired", 512, clock.UtcNow.ToUnixTimeMilliseconds(), "planned");
                Assert.IsTrue(await store.PlanLifecycleOperationAsync(expireDiag, token).ConfigureAwait(false));
                await store.CompleteLifecycleOperationAsync(expireDiag, token).ConfigureAwait(false);

                var diagSemantics = await store.ReadCenteredProductSemanticsAsync(key, diagLookup.Slot, token).ConfigureAwait(false);
                Assert.IsNotNull(diagSemantics);
                Assert.AreEqual(MeasuredAssociationPresentation.ExpectedNotDetectedVersion, diagSemantics.RecipeImplementationVersion);

                var views = provider.GetRequiredService<SqliteCenteredViews>();
                var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
                var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();
                var stream = new CenteredViewStream(key.Camera, key.RunId, "missing-pbd-diag");
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    rawDesc.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                await sourceLayers.RebuildForBaseAsync(baseView!.Base, token).ConfigureAwait(false);
                var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                var expectedLayer = rebuilt!.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, expectedLayer.Readiness);
                StringAssert.StartsWith(expectedLayer.Reason, "centered.product-expired", StringComparison.Ordinal);
                Assert.IsFalse(rebuilt.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    [DataRow("custom-variant-default-order", false, false, false, true)]
    [DataRow("custom-variant-reversed-exec-order", true, false, false, true)]
    [DataRow("custom-variant-legacy-provenance-cleared", false, false, true, true)]
    [DataRow("custom-variant-reversed-exec-order-legacy-provenance-cleared", true, false, true, true)]
    [DataRow("renamed-alias-multi-node", false, true, false, true)]
    [DataRow("renamed-alias-multi-node-legacy-provenance-cleared", false, true, true, true)]
    [DataRow("renamed-alias-reversed-exec-order-legacy-provenance-cleared", true, true, true, true)]
    [DataRow("renamed-alias-provenance-cleared-node-outputs-retained", true, true, true, false)]
    public async Task MissingGraphRecoveryMatchesConfiguredGraphAcrossMultipleScenePresentationNodes(
        string scenarioName,
        bool reverseExecutionOrder,
        bool renamedAlias,
        bool clearProvenanceJson,
        bool clearNodeOutputs)
    {
        // Astra D5-M3 + Opus D6-L1: with two ScenePresentationLayer nodes (scene-overlay vs scene-overlay-z) carrying
        // distinct variants and settings, archive reconstruction without the frozen graph must deterministically select
        // the exact same diagnostic AND star-annotations artifact as ReadConfigured(graph), regardless of execution
        // order, arrival order, custom variants, renamed legacy aliases, cleared provenance_json, or retained/cleared
        // node output contracts.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d5m3-" + scenarioName);
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            // Two scene stars with distinct magnitudes (0.4 vs 3.5) so MaximumLabelMagnitude 2.0 vs 6.5 produces
            // genuinely distinct diagnostic payloads and artifact IDs on the two ScenePresentationLayer nodes.
            var brightStar = new CelestialCatalogObject("star:d5m3-bright", "D5M3 Bright", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var fainterStar = new CelestialCatalogObject("star:d5m3-faint", "D5M3 Faint", (ra + 1.2) % 24, loc.LatitudeDegrees + 12, 3.5, 0.65);
            var predCat = new TestMetadataCatalog([brightStar, fainterStar]);
            var rendCat = new InMemoryCelestialCatalog([brightStar, fainterStar]);

            var firstAnnotVariant = renamedAlias ? "custom-stars-a" : "scene-annotation-layer-v1";
            var firstDiagVariant = renamedAlias ? "scene-annotation-layer-v1" : "archive-diag";
            var secondAnnotVariant = renamedAlias ? "custom-stars-z" : "scene-annotation-z";
            var secondDiagVariant = renamedAlias ? "expected-not-detected-layer-v1" : "second-diag";

            var firstStep = new CaptureProcessingStepConfig(
                "ScenePresentationLayer", "scene-overlay",
                Order: reverseExecutionOrder ? 30 : 25,
                DependsOn: ["scene"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                {
                    AnnotationOutputVariant = firstAnnotVariant,
                    CardinalOutputVariant = "scene-cardinal-a",
                    ImageCircleOutputVariant = "scene-image-circle-a",
                    ConstellationOutputVariant = "scene-constellation-a",
                    IncludeExpectedNotDetectedLayer = true,
                    ExpectedNotDetectedOutputVariant = firstDiagVariant,
                    MaximumLabelMagnitude = 2.0
                }));
            var secondStep = new CaptureProcessingStepConfig(
                "ScenePresentationLayer", "scene-overlay-z",
                Order: reverseExecutionOrder ? 25 : 30,
                DependsOn: ["scene"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                {
                    AnnotationOutputVariant = secondAnnotVariant,
                    CardinalOutputVariant = "scene-cardinal-z",
                    ImageCircleOutputVariant = "scene-image-circle-z",
                    ConstellationOutputVariant = "scene-constellation-z",
                    IncludeExpectedNotDetectedLayer = true,
                    ExpectedNotDetectedOutputVariant = secondDiagVariant,
                    MaximumLabelMagnitude = 6.5
                }));

            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52674,
                    illuminationMode = "ControlledNight",
                    magnitudeZeroElectronsPerSecond = 3000,
                    backgroundElectronsPerSecond = 25,
                    readNoiseStandardDeviation = 4.0,
                    shotNoiseEnabled = true
                })),
                Pipeline = profile.Pipeline with
                {
                    Steps = [.. profile.Pipeline.Steps, firstStep, secondStep]
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
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d5m3-test", token).ConfigureAwait(false);
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
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();

            // Verify configured graph selects scene-overlay's diagnostic and NOT scene-overlay-z's diagnostic.
            var configured = await store.ReadCenteredLayerBindingsAsync(reservation.Revision.State.RevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(configured);
            Assert.IsTrue(configured.TryGetValue(("scene-overlay", PresentationLayerPayloadV1.CurrentSchemaVersion, firstDiagVariant), out var cfgDiag));
            Assert.AreEqual(MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind, Assert.ContainsSingle(cfgDiag).Style.Kind);
            Assert.IsFalse(configured.ContainsKey(("scene-overlay-z", PresentationLayerPayloadV1.CurrentSchemaVersion, secondDiagVariant)));

            var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var firstDiag = sources.Single(s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == firstDiagVariant);
            var secondDiag = sources.Single(s => s.Slot.Producer == "scene-overlay-z" && s.Slot.Variant == secondDiagVariant);
            var firstAnnot = sources.Single(s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == firstAnnotVariant);
            var secondAnnot = sources.Single(s => s.Slot.Producer == "scene-overlay-z" && s.Slot.Variant == secondAnnotVariant);
            Assert.AreNotEqual(firstDiag.Product!.ArtifactId, secondDiag.Product!.ArtifactId,
                "The two ScenePresentationLayer nodes must produce genuinely distinct diagnostic artifacts.");
            Assert.AreNotEqual(firstAnnot.Product!.ArtifactId, secondAnnot.Product!.ArtifactId,
                "The two ScenePresentationLayer nodes must produce genuinely distinct annotation artifacts.");
            if (reverseExecutionOrder)
                Assert.IsLessThan(firstDiag.Revision!.Value, secondDiag.Revision!.Value);

            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                if (clearNodeOutputs)
                {
                    using var clr = conn.CreateCommand();
                    clr.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                if (clearProvenanceJson)
                {
                    using var clrProv = conn.CreateCommand();
                    clrProv.CommandText = "UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;";
                    clrProv.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clrProv.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            foreach (var mode in new[] { "base", "first-then-second", "second-then-first" })
            {
                await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                var missingGen = $"missing-d5m3-{scenarioName}-{mode}";
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);

                if (mode == "base")
                {
                    await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                }
                else if (mode == "first-then-second")
                {
                    await sourceLayers.RebuildForProductAsync(firstDiag, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(firstAnnot, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(secondDiag, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(secondAnnot, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else
                {
                    await sourceLayers.RebuildForProductAsync(secondDiag, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(secondAnnot, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(firstDiag, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(firstAnnot, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }

                var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                Assert.IsNotNull(rebuilt);
                var diagLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Ready, diagLayer.Readiness, $"{scenarioName}/{mode}");
                Assert.IsFalse(diagLayer.Drawable!.EnabledByDefault, $"{scenarioName}/{mode}");
                Assert.AreEqual(firstDiag.Product.ArtifactId, diagLayer.Evidence!.ArtifactId,
                    $"{scenarioName}/{mode}: archive recovery must select scene-overlay/{firstDiagVariant}, not scene-overlay-z/{secondDiagVariant}.");
                Assert.AreNotEqual(secondDiag.Product.ArtifactId, diagLayer.Evidence.ArtifactId, $"{scenarioName}/{mode}");

                var starLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == "star-annotations");
                Assert.AreEqual(CenteredViewReadiness.Ready, starLayer.Readiness, $"{scenarioName}/{mode}");
                Assert.AreEqual(firstAnnot.Product!.ArtifactId, starLayer.Evidence!.ArtifactId,
                    $"{scenarioName}/{mode}: archive recovery must unconditionally pin star-annotations to scene-overlay/{firstAnnotVariant}.");
                Assert.AreNotEqual(secondAnnot.Product.ArtifactId, starLayer.Evidence.ArtifactId, $"{scenarioName}/{mode}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingGraphRecoveryPinsWriteTimeMultiNodeBindingsWhenFirstNodeDiagnosticAbsent()
    {
        // Opus D6-L1: independently pin CenteredExposureProducts write-time configuredBindings on provenance_json.
        // With scene-overlay and scene-overlay-z both configured, deleting scene-overlay's diagnostic row while
        // keeping write-time provenance_json intact must NOT promote scene-overlay-z's diagnostic (whose write-time
        // LayerKinds is explicitly empty), matching ReadConfigured(graph).
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d6l1-absent-first-diag");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var brightStar = new CelestialCatalogObject("star:d6l1-bright", "D6L1 Bright", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var fainterStar = new CelestialCatalogObject("star:d6l1-faint", "D6L1 Faint", (ra + 1.2) % 24, loc.LatitudeDegrees + 12, 3.5, 0.65);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52676,
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
                        new("ScenePresentationLayer", "scene-overlay", Order: 30, DependsOn: ["scene"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                AnnotationOutputVariant = "custom-stars-a",
                                CardinalOutputVariant = "scene-cardinal-a",
                                ImageCircleOutputVariant = "scene-image-circle-a",
                                ConstellationOutputVariant = "scene-constellation-a",
                                IncludeExpectedNotDetectedLayer = true,
                                ExpectedNotDetectedOutputVariant = "scene-annotation-layer-v1",
                                MaximumLabelMagnitude = 2.0
                            })),
                        new("ScenePresentationLayer", "scene-overlay-z", Order: 25, DependsOn: ["scene"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                AnnotationOutputVariant = "custom-stars-z",
                                CardinalOutputVariant = "scene-cardinal-z",
                                ImageCircleOutputVariant = "scene-image-circle-z",
                                ConstellationOutputVariant = "scene-constellation-z",
                                IncludeExpectedNotDetectedLayer = true,
                                ExpectedNotDetectedOutputVariant = "expected-not-detected-layer-v1",
                                MaximumLabelMagnitude = 6.5
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
                services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([brightStar, fainterStar]));
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([brightStar, fainterStar]), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cam = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d6l1-absent", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();

            var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var firstAnnot = sources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "custom-stars-a");
            var secondDiag = sources.Single(static s => s.Slot.Producer == "scene-overlay-z" && s.Slot.Variant == "expected-not-detected-layer-v1");

            // Remove the first node's diagnostic row while keeping write-time provenance_json intact (and clearing outputs_json).
            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = """
                        DELETE FROM centered_exposure_products
                        WHERE execution_id = $exec AND producer = 'scene-overlay' AND variant = 'scene-annotation-layer-v1';
                        """;
                    del.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    Assert.AreEqual(1, await del.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var clr = conn.CreateCommand())
                {
                    clr.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
            var missingGen = "missing-d6l1-absent-first-diag";
            var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
            var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
            await sourceLayers.RebuildForBaseAsync(baseView!.Base, token).ConfigureAwait(false);
            await sourceLayers.RebuildForProductAsync(secondDiag, missingGen, clock.UtcNow, token).ConfigureAwait(false);

            var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
            Assert.IsNotNull(rebuilt);
            Assert.IsFalse(rebuilt.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                "Write-time provenance_json on scene-overlay-z must keep its diagnostic unbound even when scene-overlay's diagnostic row is absent.");
            var starLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == "star-annotations");
            Assert.AreEqual(firstAnnot.Product!.ArtifactId, starLayer.Evidence!.ArtifactId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingGraphRecoveryWithholdsLosingWriterDiagnosticWhenWinningRowMissingAndProvenanceCleared()
    {
        // M3 (D5-M3): When the configured first writer's (scene-overlay) diagnostic row is removed and
        // provenance_json is cleared across all rows while processing_execution_nodes.outputs_json is retained,
        // archive reconstruction must preserve scene-overlay's declared ownership and NOT publish scene-overlay-z's diagnostic.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d8m3-missing-win-diag");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var brightStar = new CelestialCatalogObject("star:d8m3-bright", "D8M3 Bright", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var fainterStar = new CelestialCatalogObject("star:d8m3-faint", "D8M3 Faint", (ra + 1.2) % 24, loc.LatitudeDegrees + 12, 3.5, 0.65);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52683,
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
                        new("ScenePresentationLayer", "scene-overlay", Order: 30, DependsOn: ["scene"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                AnnotationOutputVariant = "custom-stars-a",
                                CardinalOutputVariant = "scene-cardinal-a",
                                ImageCircleOutputVariant = "scene-image-circle-a",
                                ConstellationOutputVariant = "scene-constellation-a",
                                IncludeExpectedNotDetectedLayer = true,
                                ExpectedNotDetectedOutputVariant = "scene-annotation-layer-v1",
                                MaximumLabelMagnitude = 2.0
                            })),
                        new("ScenePresentationLayer", "scene-overlay-z", Order: 25, DependsOn: ["scene"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                AnnotationOutputVariant = "custom-stars-z",
                                CardinalOutputVariant = "scene-cardinal-z",
                                ImageCircleOutputVariant = "scene-image-circle-z",
                                ConstellationOutputVariant = "scene-constellation-z",
                                IncludeExpectedNotDetectedLayer = true,
                                ExpectedNotDetectedOutputVariant = "expected-not-detected-layer-v1",
                                MaximumLabelMagnitude = 6.5
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
                services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([brightStar, fainterStar]));
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([brightStar, fainterStar]), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cam = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d8m3-missing", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();

            var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var firstAnnot = sources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "custom-stars-a");
            var secondDiag = sources.Single(static s => s.Slot.Producer == "scene-overlay-z" && s.Slot.Variant == "expected-not-detected-layer-v1");

            // Remove scene-overlay's diagnostic row AND clear product provenance_json while RETAINING node outputs_json!
            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = """
                        DELETE FROM centered_exposure_products
                        WHERE execution_id = $exec AND producer = 'scene-overlay' AND variant = 'scene-annotation-layer-v1';
                        """;
                    del.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    Assert.AreEqual(1, await del.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var clrProv = conn.CreateCommand())
                {
                    clrProv.CommandText = "UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;";
                    clrProv.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clrProv.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
            var missingGen = "missing-d8m3-winning-row-absent";
            var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
            var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
            await sourceLayers.RebuildForBaseAsync(baseView!.Base, token).ConfigureAwait(false);
            await sourceLayers.RebuildForProductAsync(secondDiag, missingGen, clock.UtcNow, token).ConfigureAwait(false);

            var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
            Assert.IsNotNull(rebuilt);
            Assert.IsFalse(rebuilt.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                "Retained node output contracts must preserve scene-overlay ownership and refuse to publish losing writer scene-overlay-z's diagnostic.");
            var starLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == "star-annotations");
            Assert.AreEqual(firstAnnot.Product!.ArtifactId, starLayer.Evidence!.ArtifactId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("all-node-outputs-cleared", false)]
    [DataRow("partial-only-associated-node-outputs-cleared", true)]
    public async Task MissingGraphRecoveryDoesNotPromoteLowerPriorityAssociationWriterWhenHigherPriorityExpires(
        string metadataLossMode,
        bool clearOnlyAssociatedNodeOutputs)
    {
        // M4 (D6-M2) + D8-M1: When higher-priority associated/custom-assoc expires and provenance_json is cleared,
        // BOTH full node-output loss AND partial metadata loss (clearing ONLY associated's outputs_json while leaving
        // z-fast/z-early-assoc's node output contract intact) must preserve associated/custom-assoc as
        // UnidentifiableExpired and NEVER promote surviving lower-priority z-fast/z-early-assoc.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d8m4-" + metadataLossMode);
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var star1 = new CelestialCatalogObject("star:d8m4-1", "D8M4 One", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var star2 = new CelestialCatalogObject("star:d8m4-2", "D8M4 Two", (ra + 1.3) % 24, loc.LatitudeDegrees + 13, 0.8, 0.65);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52684,
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
                        new("MeasuredStellarAssociations", "z-fast", Order: 25, DependsOn: ["scene"], Required: false,
                            Options: JsonSerializer.SerializeToElement(new { outputVariant = "z-early-assoc" }),
                            Publication: durable),
                        new("MeasuredStellarAssociations", "associated", Order: 30, DependsOn: ["scene"], Required: false,
                            Options: JsonSerializer.SerializeToElement(new { outputVariant = "custom-assoc" }),
                            Publication: durable),
                        new("ScenePresentationLayer", "scene-overlay", Order: 35, DependsOn: ["scene", "associated"], Required: false, Publication: durable,
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
                services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([star1, star2]));
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([star1]), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cam = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d8m4-test", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();

            var before = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var assocBefore = before.Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-assoc");

            // Expire associated/custom-assoc while leaving z-fast/z-early-assoc intact, and clear provenance + outputs_json!
            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                using (var delOut = conn.CreateCommand())
                {
                    delOut.CommandText = "DELETE FROM processing_outputs WHERE output_identity_sha256 = $output;";
                    delOut.Parameters.AddWithValue("$output", assocBefore.Product!.OutputIdentitySha256);
                    Assert.AreEqual(1, await delOut.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var updProd = conn.CreateCommand())
                {
                    updProd.CommandText = """
                        UPDATE centered_exposure_products
                        SET disposition = 'Expired', reason = 'retention-expired'
                        WHERE execution_id = $exec AND producer = 'associated' AND variant = 'custom-assoc';
                        """;
                    updProd.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    Assert.AreEqual(1, await updProd.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var clr = conn.CreateCommand())
                {
                    if (clearOnlyAssociatedNodeOutputs)
                    {
                        clr.CommandText = """
                            UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;
                            UPDATE processing_execution_nodes SET outputs_json = '[]'
                            WHERE execution_id = $exec AND node_id = 'associated';
                            """;
                    }
                    else
                    {
                        clr.CommandText = """
                            UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;
                            UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;
                            """;
                    }
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            // Lookup MUST NOT return z-fast/z-early-assoc; it must preserve associated/custom-assoc as UnidentifiableExpired.
            var provenWriter = await store.ReadAuthoritativeAssociationWriterAsync(key, executionId, token).ConfigureAwait(false);
            Assert.IsNull(provenWriter, "Expired higher-priority writer with cleared metadata must not promote z-fast/z-early-assoc.");
            var resolution = await store.ReadAuthoritativeAssociationResolutionAsync(key, executionId, token).ConfigureAwait(false);
            Assert.AreEqual(CenteredAuthoritativeAssociationResolutionState.UnidentifiableExpired, resolution.State);
            Assert.AreEqual("associated", resolution.Writer!.Value.Producer);
            Assert.AreEqual("custom-assoc", resolution.Writer.Value.Variant);

            var afterSources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var expiredAssoc = afterSources.Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-assoc");
            var survivingZFast = afterSources.Single(static s => s.Slot.Producer == "z-fast" && s.Slot.Variant == "z-early-assoc");
            var sceneExpected = afterSources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "expected-not-detected-layer-v1");

            foreach (var order in new[] { "rebuild-base", "z-fast-first", "associated-first" })
            {
                await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                var missingGen = $"missing-d8m4-{order}";
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);

                if (order == "z-fast-first")
                {
                    await sourceLayers.RebuildForProductAsync(survivingZFast, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    var mid = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                    Assert.IsFalse(mid!.Revision.Layers.Any(static l =>
                        l.Key.Kind is MeasuredAssociationPresentation.MeasuredLayerKind or MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                        "z-fast and scene-overlay must not publish association layers when higher-priority associated expired.");
                    await sourceLayers.RebuildForProductAsync(expiredAssoc, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else if (order == "associated-first")
                {
                    await sourceLayers.RebuildForProductAsync(expiredAssoc, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(survivingZFast, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else
                {
                    await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                }

                var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                Assert.IsNotNull(rebuilt);
                var measuredLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind);
                var expectedLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, measuredLayer.Readiness, order);
                StringAssert.StartsWith(measuredLayer.Reason, "centered.product-expired", StringComparison.Ordinal, order);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, expectedLayer.Readiness, order);
                StringAssert.StartsWith(expectedLayer.Reason, "centered.product-expired", StringComparison.Ordinal, order);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("provenance-retained-outputs-cleared", false, true)]
    [DataRow("provenance-cleared-outputs-cleared", true, true)]
    [DataRow("provenance-cleared-outputs-retained", true, false)]
    public async Task MissingGraphRecoveryMatchesConfiguredGraphAcrossMultipleExposureAssociationWriters(
        string scenarioName,
        bool clearProvenanceJson,
        bool clearNodeOutputs)
    {
        // Astra D6-M2: with two valid exposure association writers where z-fast (variant z-early-assoc) executes BEFORE
        // associated (variant custom-assoc), ReadConfigured(graph) selects associated/custom-assoc by ordinal node ID.
        // Missing-graph authority resolution and archive rebuild MUST also select associated/custom-assoc (never
        // z-fast/z-early-assoc) across both retained and cleared provenance_json / outputs_json and all arrival orders.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d6m2-" + scenarioName);
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var star1 = new CelestialCatalogObject("star:d6m2-1", "D6M2 One", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var star2 = new CelestialCatalogObject("star:d6m2-2", "D6M2 Two", (ra + 1.3) % 24, loc.LatitudeDegrees + 13, 0.8, 0.65);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52678,
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
                        new("MeasuredStellarAssociations", "z-fast", Order: 25, DependsOn: ["scene"], Required: false,
                            Options: JsonSerializer.SerializeToElement(new { outputVariant = "z-early-assoc" }),
                            Publication: durable),
                        new("MeasuredStellarAssociations", "associated", Order: 30, DependsOn: ["scene"], Required: false,
                            Options: JsonSerializer.SerializeToElement(new { outputVariant = "custom-assoc" }),
                            Publication: durable),
                        new("ScenePresentationLayer", "scene-overlay", Order: 35, DependsOn: ["scene", "associated"], Required: false, Publication: durable,
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
                services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([star1, star2]));
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([star1]), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cam = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d6m2-test", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();

            var configured = await store.ReadCenteredLayerBindingsAsync(reservation.Revision.State.RevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(configured);
            Assert.IsTrue(configured.ContainsKey(("associated", MeasuredStellarAssociationsV1.CurrentSchemaVersion, "custom-assoc")));
            Assert.IsFalse(configured.ContainsKey(("z-fast", MeasuredStellarAssociationsV1.CurrentSchemaVersion, "z-early-assoc")));

            var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var zFast = sources.Single(static s => s.Slot.Producer == "z-fast" && s.Slot.Variant == "z-early-assoc");
            var associated = sources.Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-assoc");
            var sceneExpected = sources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "expected-not-detected-layer-v1");
            Assert.IsLessThan(associated.Revision!.Value, zFast.Revision!.Value,
                "z-fast must settle with a lower product revision than associated to exercise rank tie-break.");
            Assert.AreNotEqual(zFast.Product!.ArtifactId, associated.Product!.ArtifactId);

            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                if (clearNodeOutputs)
                {
                    using var clr = conn.CreateCommand();
                    clr.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                if (clearProvenanceJson)
                {
                    using var clrProv = conn.CreateCommand();
                    clrProv.CommandText = "UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;";
                    clrProv.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clrProv.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            var resolvedWriter = await store.ReadAuthoritativeAssociationWriterAsync(key, executionId, token).ConfigureAwait(false);
            Assert.IsNotNull(resolvedWriter, scenarioName);
            Assert.AreEqual("associated", resolvedWriter.Value.Producer, scenarioName);
            Assert.AreEqual("custom-assoc", resolvedWriter.Value.Variant, scenarioName);

            foreach (var order in new[] { "rebuild-base", "z-fast-first", "associated-first" })
            {
                await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                var missingGen = $"missing-d6m2-{scenarioName}-{order}";
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);

                if (order == "z-fast-first")
                {
                    await sourceLayers.RebuildForProductAsync(zFast, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    var mid = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                    Assert.IsFalse(mid!.Revision.Layers.Any(static l =>
                        l.Key.Kind is MeasuredAssociationPresentation.MeasuredLayerKind or MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                        $"{scenarioName}: z-fast and scene-overlay must not publish association layers when associated owns authority.");
                    await sourceLayers.RebuildForProductAsync(associated, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else if (order == "associated-first")
                {
                    await sourceLayers.RebuildForProductAsync(associated, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(zFast, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else
                {
                    await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                }

                var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                Assert.IsNotNull(rebuilt);
                var measuredLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind);
                var expectedLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Ready, measuredLayer.Readiness, $"{scenarioName}/{order}");
                Assert.AreEqual(CenteredViewReadiness.Ready, expectedLayer.Readiness, $"{scenarioName}/{order}");
                Assert.AreEqual(associated.Product.ArtifactId, measuredLayer.Evidence!.ArtifactId,
                    $"{scenarioName}/{order}: archive rebuild must publish associated/custom-assoc, not z-fast/z-early-assoc.");
                Assert.AreEqual(associated.Product.ArtifactId, expectedLayer.Evidence!.ArtifactId,
                    $"{scenarioName}/{order}: archive rebuild must publish associated/custom-assoc for expected-not-detected.");
                Assert.AreNotEqual(zFast.Product.ArtifactId, measuredLayer.Evidence.ArtifactId, $"{scenarioName}/{order}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task MissingGraphRecoverySuppressesSceneDiagnosticForExpiredLegacyWriterSortingAfterSceneNode()
    {
        // Opus D6-L2: with an unidentifiable expired legacy exposure writer named "zz-associated" (sorting AFTER
        // "scene-overlay") and cleared provenance/outputs, BuildRecoveredBindings visits "scene-overlay" FIRST;
        // ReadLegacySlot's UnidentifiableExpired suppression path must suppress scene-overlay's AssessedEmpty
        // diagnostic so expected-not-detected resolves to Incompatible / centered.product-expired.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-d6l2-zz-expired");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var star1 = new CelestialCatalogObject("star:d6l2-1", "D6L2 Matched", ra, loc.LatitudeDegrees, 0.4, 0.65);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52679,
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
                        new("MeasuredStellarAssociations", "zz-associated", DependsOn: ["scene"], Required: false,
                            Options: JsonSerializer.SerializeToElement(new { outputVariant = "custom-zz-assoc" }),
                            Publication: durable),
                        new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene", "zz-associated"], Required: false, Publication: durable,
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
                services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([star1]));
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([star1]), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cam = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "d6l2-zz", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var before = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var assocBefore = before.Single(static s => s.Slot.Producer == "zz-associated" && s.Slot.Variant == "custom-zz-assoc");

            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                using (var delOut = conn.CreateCommand())
                {
                    delOut.CommandText = "DELETE FROM processing_outputs WHERE output_identity_sha256 = $output;";
                    delOut.Parameters.AddWithValue("$output", assocBefore.Product!.OutputIdentitySha256);
                    Assert.AreEqual(1, await delOut.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var updProd = conn.CreateCommand())
                {
                    updProd.CommandText = """
                        UPDATE centered_exposure_products
                        SET schema_version = $v1, disposition = 'Expired', reason = 'retention-expired', provenance_json = NULL
                        WHERE execution_id = $exec AND producer = 'zz-associated' AND variant = 'custom-zz-assoc';
                        """;
                    updProd.Parameters.AddWithValue("$v1", MeasuredStellarAssociationsV1.V1SchemaVersion);
                    updProd.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    Assert.AreEqual(1, await updProd.ExecuteNonQueryAsync(token).ConfigureAwait(false));
                }
                using (var clrProv = conn.CreateCommand())
                {
                    clrProv.CommandText = """
                        UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;
                        UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;
                        """;
                    clrProv.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clrProv.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            var resolution = await store.ReadAuthoritativeAssociationResolutionAsync(key, executionId, token).ConfigureAwait(false);
            Assert.AreEqual(CenteredAuthoritativeAssociationResolutionState.UnidentifiableExpired, resolution.State);
            Assert.AreEqual("zz-associated", resolution.Writer!.Value.Producer);
            Assert.AreEqual("custom-zz-assoc", resolution.Writer.Value.Variant);

            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();
            var afterSources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var expiredAssoc = afterSources.Single(static s => s.Slot.Producer == "zz-associated" && s.Slot.Variant == "custom-zz-assoc");
            var sceneExpected = afterSources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "expected-not-detected-layer-v1");

            foreach (var order in new[] { "rebuild-base", "scene-first", "assoc-first" })
            {
                await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                var missingGen = $"missing-d6l2-{order}";
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);

                if (order == "scene-first")
                {
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    var mid = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                    Assert.IsFalse(mid!.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                        "scene-first must suppress scene-overlay even when zz-associated sorts after scene-overlay.");
                    await sourceLayers.RebuildForProductAsync(expiredAssoc, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else if (order == "assoc-first")
                {
                    await sourceLayers.RebuildForProductAsync(expiredAssoc, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(sceneExpected, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else
                {
                    await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                }

                var rebuilt = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                Assert.IsNotNull(rebuilt);
                var expectedLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, expectedLayer.Readiness,
                    $"{order}: zz-associated suppression path must settle Incompatible/centered.product-expired, not {expectedLayer.Readiness}/{expectedLayer.Reason}.");
                StringAssert.StartsWith(expectedLayer.Reason, "centered.product-expired", StringComparison.Ordinal, order);
                var measuredLayer = rebuilt.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.MeasuredLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, measuredLayer.Readiness, order);
                StringAssert.StartsWith(measuredLayer.Reason, "centered.product-expired", StringComparison.Ordinal, order);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingGraphRecoverySuppressesCustomArchiveDiagSceneLayerWhenExposureAssociationsPresent(
        bool clearProvenanceJson)
    {
        // End-to-end missing-graph suppression of a custom "archive-diag" scene diagnostic variant when
        // ExposureStellarAssociations is present, across both retained and cleared provenance_json and all arrival orders.
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory($"hvo-d5l3-archivediag-{(clearProvenanceJson ? "leg" : "prov")}");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        try
        {
            var clock = new CenteredClock { UtcNow = start };
            var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
            var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
            var loc = profile.DeploymentLocation!;
            var ra = AstronomyTime.LocalMeanSiderealDegrees(start, loc.LongitudeDegrees) / 15d;
            var star1 = new CelestialCatalogObject("star:ad-1", "AD One", ra, loc.LatitudeDegrees, 0.4, 0.65);
            var star2 = new CelestialCatalogObject("star:ad-2", "AD Two", (ra + 1.4) % 24, loc.LatitudeDegrees + 14, 0.6, 0.65);
            profile = profile with
            {
                Module = new("VirtualSky", JsonSerializer.SerializeToElement(new
                {
                    seed = 52675,
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
                            Options: JsonSerializer.SerializeToElement(new { outputVariant = "custom-assoc" }),
                            Publication: durable),
                        new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene", "associated"], Required: false, Publication: durable,
                            Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                            {
                                UseExposureAssociations = true,
                                IncludeExpectedNotDetectedLayer = true,
                                ExpectedNotDetectedOutputVariant = "archive-diag"
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
                services.AddSingleton<ICelestialCatalog>(new TestMetadataCatalog([star1, star2]));
            });
            await provider.GetRequiredService<RawCaptureIngress>().InitializeAsync(token).ConfigureAwait(false);
            var initLoc = await provider.GetRequiredService<IDeploymentLocationStore>().InitializeAsync(seed, token).ConfigureAwait(false);
            profile = profile with { DeploymentLocation = initLoc };
            var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
            var ingress = provider.GetRequiredService<RawCaptureIngress>();
            var module = new VirtualSkyCameraModule(deviceClock, new InMemoryCelestialCatalog([star1]), new ProjectedSceneStore(),
                stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await using var cam = module.ConfigureAwait(false);
            await module.InitializeAsync(active, token).ConfigureAwait(false);
            var resources = provider.GetRequiredService<CaptureProjectedSceneStager>().FrozenResources;
            var reservation = await ingress.ReserveCenteredAsync(active, resources, null, token).ConfigureAwait(false);
            var setpoint = new CaptureSetpoint(TimeSpan.FromSeconds(1), 1, null, null);
            await ingress.DispatchCenteredAsync(reservation.Pending.Key, setpoint, null, "ad-test", token).ConfigureAwait(false);
            var req = new CaptureRequest(start, TimeSpan.FromSeconds(1), CaptureMode.Still, setpoint)
            {
                BoundGraphRevisionId = reservation.Revision.State.RevisionId,
                BoundGraph = reservation.Revision.Pipeline
            };
            var cap = await module.CaptureBoundAsync(reservation.Pending.Key, req, token).ConfigureAwait(false);
            clock.UtcNow = cap.AcquisitionTiming!.ReadoutCompletedUtc.AddMilliseconds(1);
            var receipt = await ingress.AcceptCenteredAsync(active, new(req, cap, start, req.TargetInterval, req.TargetInterval), reservation, token).ConfigureAwait(false);
            await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);

            var key = new ExposureKey(reservation.Pending.Key.Camera, reservation.Pending.Key.RunId, receipt.Manifest.Descriptor.Capture.CaptureId);
            var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
            var executionId = linear!.Product!.Slot.ExecutionId;
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var views = provider.GetRequiredService<SqliteCenteredViews>();
            var viewProducer = provider.GetRequiredService<ICenteredViewProducer>();
            var sourceLayers = provider.GetRequiredService<CenteredViewSourceLayers>();

            using (var conn = await store.OpenCenteredViewsAsync(token).ConfigureAwait(false))
            {
                using (var clr = conn.CreateCommand())
                {
                    clr.CommandText = "UPDATE processing_execution_nodes SET outputs_json = '[]' WHERE execution_id = $exec;";
                    clr.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clr.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                if (clearProvenanceJson)
                {
                    using var clrProv = conn.CreateCommand();
                    clrProv.CommandText = "UPDATE centered_exposure_products SET provenance_json = NULL WHERE execution_id = $exec;";
                    clrProv.Parameters.AddWithValue("$exec", executionId.ToString("N"));
                    await clrProv.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
            }

            var sources = await store.ReadCenteredViewSourcesAsync(key, executionId, 0, 64, token).ConfigureAwait(false);
            var assocSource = sources.Single(static s => s.Slot.Producer == "associated" && s.Slot.Variant == "custom-assoc");
            var archiveDiagSource = sources.Single(static s => s.Slot.Producer == "scene-overlay" && s.Slot.Variant == "archive-diag");
            var diagSemantics = await store.ReadCenteredProductSemanticsAsync(key, archiveDiagSource.Slot, token).ConfigureAwait(false);
            Assert.IsNotNull(diagSemantics);
            Assert.AreEqual(MeasuredAssociationPresentation.ExpectedNotDetectedVersion, diagSemantics.RecipeImplementationVersion);
            Assert.IsEmpty(CenteredViewLayerBindings.ReadLegacySlot(
                archiveDiagSource.Slot, CenteredAuthoritativeAssociationResolution.Resolved(new("associated", "custom-assoc")), diagSemantics));
            if (clearProvenanceJson)
            {
                Assert.AreEqual(MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind,
                    Assert.ContainsSingle(CenteredViewLayerBindings.ReadLegacySlot(
                        archiveDiagSource.Slot, CenteredAuthoritativeAssociationResolution.None, diagSemantics)).Style.Kind);
            }
            else
            {
                // Write-time configuredBindings explicitly recorded LayerKinds = [] on archive-diag because
                // ExposureStellarAssociations owned expected-not-detected in the configured graph.
                Assert.IsEmpty(CenteredViewLayerBindings.ReadLegacySlot(
                    archiveDiagSource.Slot, CenteredAuthoritativeAssociationResolution.None, diagSemantics));
            }

            foreach (var order in new[] { "scene-first", "assoc-first", "rebuild-base" })
            {
                await ClearCenteredViewsAsync(store, token).ConfigureAwait(false);
                var missingGen = $"missing-archivediag-{order}-{clearProvenanceJson}";
                var stream = new CenteredViewStream(key.Camera, key.RunId, missingGen);
                var baseView = await viewProducer.PublishSingleAsync(stream, executionId,
                    receipt.Manifest.Descriptor.Capture.CaptureSequence, linear, new(), token).ConfigureAwait(false);
                Assert.IsNotNull(baseView);

                if (order == "scene-first")
                {
                    await sourceLayers.RebuildForProductAsync(archiveDiagSource, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    var mid = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                    Assert.IsFalse(mid!.Revision.Layers.Any(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind),
                        "Custom archive-diag scene layer must be suppressed before ExposureStellarAssociations arrives.");
                    await sourceLayers.RebuildForProductAsync(assocSource, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else if (order == "assoc-first")
                {
                    await sourceLayers.RebuildForProductAsync(assocSource, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                    await sourceLayers.RebuildForProductAsync(archiveDiagSource, missingGen, clock.UtcNow, token).ConfigureAwait(false);
                }
                else
                {
                    await sourceLayers.RebuildForBaseAsync(baseView.Base, token).ConfigureAwait(false);
                }

                var finalView = await views.ReadLatestSingleAsync(stream, token).ConfigureAwait(false);
                var expected = finalView!.Revision.Layers.Single(static l => l.Key.Kind == MeasuredAssociationPresentation.ExpectedNotDetectedLayerKind);
                Assert.AreEqual(CenteredViewReadiness.Ready, expected.Readiness, $"{order}");
                Assert.AreEqual(assocSource.Product!.ArtifactId, expected.Evidence!.ArtifactId, $"{order}");
                Assert.AreNotEqual(archiveDiagSource.Product!.ArtifactId, expected.Evidence.ArtifactId, $"{order}");
            }
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

        public string CatalogId => Metadata.Name;
        public string CatalogPackageVersion => Metadata.Version;

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
