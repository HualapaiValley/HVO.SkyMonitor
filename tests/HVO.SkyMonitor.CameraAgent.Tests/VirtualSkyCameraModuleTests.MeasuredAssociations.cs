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
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    private static readonly string[] JupiterBodyNames = ["Jupiter"];

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
