using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCameraModuleTests
{
    [TestMethod]
    [DataRow(224, 8_000, true, DisplayName = "CenteredReferenceLayersKeepTheirNativePreview")]
    [DataRow(128, 24_000, true, DisplayName = "CenteredReferenceFactsFitTheResizedPreview")]
    [DataRow(16, 24_000, false, DisplayName = "CenteredReferenceFactsSettleAnUnrepresentablePreview")]
    public async Task CenteredViewPublishesOptionalReferenceLayersFromTheFrozenLiveGraph(int previewSize, int sourceFont, bool fits)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-centered-reference-layers");
        var token = TestContext.CancellationToken;
        var start = new DateTimeOffset(2026, 11, 20, 22, 30, 0, TimeSpan.Zero);
        var clock = new CenteredClock { UtcNow = start };
        var deviceClock = new ExposureTestTimeProvider(start, autoAdvance: true);
        var profile = CenteredRuntimeProfile(root, start, CameraPixelFormat.Mono16);
        var durable = new CaptureProcessingPublicationPolicy(CaptureProcessingPersistenceMode.DurableLocal);
        profile = profile with
        {
            Pipeline = profile.Pipeline with
            {
                Steps = [
            .. profile.Pipeline.Steps,
            new("ScenePresentationLayer", "scene-overlay", DependsOn: ["scene"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new ScenePresentationLayerProcessingStepOptions
                {
                    ImageCircleOutputVariant = "configured-image-circle"
                })),
            new("CloudAssessment", "cloud", DependsOn: ["$raw"], Required: false, Publication: durable),
            new("CloudPresentationLayer", "cloud-overlay", DependsOn: ["cloud"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new CloudPresentationLayerProcessingStepOptions
                {
                    WidthPixels = 224, HeightPixels = 224, RequireQuantifiedAssessment = true
                })),
            new("RollingCombination", "facts-input", DependsOn: ["linear"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new { windowSize = 1 })),
            new("CombinedPreview", "facts-preview", DependsOn: ["facts-input"], Required: false, Publication: durable),
            new(FrameFactsPresentation.StepAlias, "facts", DependsOn: ["scene", "facts-preview"], Required: false, Publication: durable,
                Options: JsonSerializer.SerializeToElement(new FrameFactsPresentationProcessingStepOptions
                {
                    WidthPixels = 224, HeightPixels = 224, Inset = 4, TopLeftSizeMilliPixels = sourceFont,
                    TopLeftTemplate = "Gain {{acquisition.gain}}"
                }))
        ]
            }
        };

        try
        {
            CenteredViewArchiveBase archived;
            Guid captureId;
            CenteredViewLayer publishedFacts;
            using (var provider = CenteredRuntimeProvider(root, clock, services =>
                services.AddLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Warning))))
            {
                var active = await StoragePolicyRawIngressIntegrationTests.InitializeAsync(provider, profile).ConfigureAwait(false);
                var ingress = provider.GetRequiredService<RawCaptureIngress>();
                var module = new VirtualSkyCameraModule(deviceClock, CenteredRuntimeCatalog(start), new ProjectedSceneStore(),
                    stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
                await using var camera = module.ConfigureAwait(false);
                await module.InitializeAsync(active, token).ConfigureAwait(false);
                var attempt = await CaptureCenteredRuntimeAttemptAsync(ingress, module, deviceClock, clock, active, start, 1, token)
                    .ConfigureAwait(false);
                var receipt = await ingress.AcceptCenteredAsync(active, attempt.Submission, attempt.Reservation, token).ConfigureAwait(false);
                captureId = receipt.Manifest.Descriptor.Capture.CaptureId;
                await ProcessCenteredRuntimeExposureAsync(provider, active, token).ConfigureAwait(false);
                var key = new ExposureKey(attempt.Reservation.Pending.Key.Camera, attempt.Reservation.Pending.Key.RunId, captureId);
                var linear = await provider.GetRequiredService<IExposureProductReader>().ReadLinearAsync(key, token).ConfigureAwait(false);
                Assert.IsNotNull(linear?.Product);
                var stream = new CenteredViewStream(key.Camera, key.RunId, attempt.Reservation.Revision.State.RevisionId);
                var initial = await provider.GetRequiredService<ICenteredViewProducer>().PublishSingleAsync(stream,
                    linear.Product.Slot.ExecutionId, receipt.Manifest.Descriptor.Capture.CaptureSequence, linear,
                    new(MaximumDimension: previewSize), token).ConfigureAwait(false);
                Assert.IsNotNull(initial);
                var view = await DrainCenteredReferenceAsync(provider, key, token).ConfigureAwait(false);
                Assert.AreEqual(previewSize, view.Base.Definition.Preview.Transform.PreviewWidth);
                var layers = view.Revision.Layers.ToDictionary(layer => layer.Key.Kind);
                Assert.AreEqual(CenteredViewReadiness.Ready, layers["scene-image-circle"].Readiness);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, layers["cloud-label"].Readiness);
                Assert.AreEqual(CenteredViewReadiness.Incompatible, layers["cloud-mask"].Readiness);
                Assert.IsNull(layers["cloud-mask"].Drawable);
                StringAssert.Contains(layers["cloud-mask"].Reason, CloudAssessmentReasonCodes.MissingClearReference, StringComparison.Ordinal);
                var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
                var execution = await store.ReadExecutionDetailAsync(view.Base.Definition.Reference.ExecutionId, token).ConfigureAwait(false);
                Assert.IsNotNull(execution);
                var nodeOutcomes = string.Join(", ", execution.Nodes.Select(node => $"{node.NodeId}={node.Status}/{node.Reason}"));
                publishedFacts = layers["environment"];
                Assert.AreEqual(fits ? CenteredViewReadiness.Ready : CenteredViewReadiness.Incompatible, publishedFacts.Readiness,
                    $"{layers["environment"].Reason}. {nodeOutcomes}");
                Assert.AreEqual(CenteredViewReadiness.NotConfigured, layers["scene-deep-sky"].Readiness);
                var ports = await store.ReadCenteredViewSourcesAsync(key, view.Base.Definition.Reference.ExecutionId, 0, 64, token)
                    .ConfigureAwait(false);
                foreach (var (kind, variant) in new (string, string)[]
                {
                    ("scene-image-circle", "configured-image-circle"), ("environment", "environment-layer-v2")
                })
                {
                    var layer = layers[kind];
                    var product = Assert.ContainsSingle(ports.Where(source => source.Slot.Variant == variant));
                    Assert.AreEqual(new CenteredViewLayerKey(kind, CenteredViewLayerScope.Reference, captureId), layer.Key);
                    Assert.AreEqual(product.Product!.ArtifactId, layer.Evidence!.ArtifactId);
                    Assert.AreEqual(product.Revision, layer.EvidenceOrder);
                    Assert.AreEqual(ExposureProductAvailability.Available, product.Availability);
                    Assert.AreEqual(kind != "environment" || fits, layer.Drawable is not null);
                }
                var cursor = await provider.GetRequiredService<SqliteCenteredViews>().ReadProductCursorAsync(key.Camera, token).ConfigureAwait(false);
                Assert.IsGreaterThanOrEqualTo(publishedFacts.EvidenceOrder, cursor);
                TestContext.WriteLine($"facts source={publishedFacts.Evidence?.ArtifactId} revision={publishedFacts.EvidenceOrder} cursor={cursor} " +
                    $"preview={previewSize} readiness={publishedFacts.Readiness}/{publishedFacts.Reason}");
                if (fits)
                {
                    var facts = await provider.GetRequiredService<CaptureProcessingPersistence>()
                        .ReadCenteredViewParentAsync(captureId, publishedFacts.Drawable!.SourceProduct.ArtifactId, token)
                        .ConfigureAwait(false);
                    var factPayload = PresentationLayerPayloadJson.Parse(facts.Payload).Payload;
                    Assert.IsNotNull(factPayload?.RichText);
                    Assert.AreEqual((previewSize, previewSize), (factPayload.WidthPixels, factPayload.HeightPixels));
                    var topLeft = factPayload.RichText.Corners.Single(corner => corner.Anchor == PresentationTextAnchor.TopLeft);
                    Assert.AreEqual(sourceFont * previewSize / 224, topLeft.SizeMilliPixels);
                    Assert.AreEqual(4 * previewSize / 224, topLeft.Inset);
                    Assert.AreEqual("Gain 1", string.Concat(Assert.ContainsSingle(topLeft.Lines).Runs.Select(run => run.Text)));
                }
                else
                {
                    Assert.AreEqual("centered.rich-text-preview-too-small", publishedFacts.Reason);
                    clock.UtcNow = clock.UtcNow.AddMinutes(1);
                    var due = await provider.GetRequiredService<SqliteCenteredViews>().ReadDueProductRetriesAsync(key.Camera, 24, token)
                        .ConfigureAwait(false);
                    CollectionAssert.DoesNotContain(due.ToArray(), publishedFacts.EvidenceOrder);
                }
                archived = Assert.ContainsSingle(await provider.GetRequiredService<SqliteCenteredViews>()
                    .ReadArchiveAsync(captureId, token).ConfigureAwait(false));
            }

            using var reopened = CenteredRuntimeProvider(root, clock);
            var views = reopened.GetRequiredService<SqliteCenteredViews>();
            Assert.AreEqual(archived.LatestRevisionId, Assert.ContainsSingle(await views
                .ReadArchiveAsync(captureId, token).ConfigureAwait(false)).LatestRevisionId);
            var exact = await views.ReadRevisionAsync(archived.Base.BaseId, archived.LatestRevisionId, token).ConfigureAwait(false);
            Assert.IsNotNull(exact);
            var retainedFacts = exact.Revision.Layers.Single(layer => layer.Key == publishedFacts.Key);
            Assert.AreEqual(publishedFacts.Readiness, retainedFacts.Readiness);
            Assert.AreEqual(publishedFacts.Evidence, retainedFacts.Evidence);
            Assert.AreEqual(publishedFacts.Drawable?.SourceProduct, retainedFacts.Drawable?.SourceProduct);
            var reader = reopened.GetRequiredService<ICenteredViewPresentationReader>();
            var painted = await reader.ReadAsync(archived.Base.BaseId, archived.LatestRevisionId, token).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, painted.Status, painted.Reason);
            Assert.IsNotNull(painted.Presentation);
            Assert.AreEqual(fits, painted.Presentation.Layers.Any(layer => layer.Kind == "environment"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task AssertCenteredRejectedFitAndMarkersAsync(ServiceProvider provider, ExposureKey exposure, Guid fitArtifactId)
    {
        var token = CancellationToken.None;
        var view = await DrainCenteredReferenceAsync(provider, exposure, token).ConfigureAwait(false);
        var residual = view.Revision.Layers.Single(layer => layer.Key.Kind == AstrometricDiagnosticPresentation.ResidualsLayer);
        Assert.AreEqual(CenteredViewReadiness.Incompatible, residual.Readiness);
        Assert.AreEqual(fitArtifactId, residual.Evidence!.ArtifactId);
        Assert.IsNull(residual.Drawable);
        Assert.IsFalse(string.IsNullOrWhiteSpace(residual.Reason));

        var measured = view.Revision.Layers.Single(layer => layer.Key.Kind == "measured-source-markers");
        Assert.AreEqual(CenteredViewReadiness.Ready, measured.Readiness);
        Assert.IsNotNull(measured.Drawable);
        var persistence = provider.GetRequiredService<CaptureProcessingPersistence>();
        var mapped = await persistence.ReadCenteredViewParentAsync(exposure.CaptureId, measured.Drawable.SourceProduct.ArtifactId, token)
            .ConfigureAwait(false);
        var payload = PresentationLayerPayloadJson.Parse(mapped.Payload).Payload;
        Assert.IsNotNull(payload);
        // The fixture projects a single independently placed zenith star at the sensor center.
        Assert.IsTrue(payload.Markers.Any(marker => Math.Abs(marker.Center.X - 112) < 2 && Math.Abs(marker.Center.Y - 112) < 2));
    }

    private static async Task<CenteredView> DrainCenteredReferenceAsync(ServiceProvider provider, ExposureKey key, CancellationToken token)
    {
        while (await DrainCenteredCameraAsync(provider, key.Camera, null, token).ConfigureAwait(false))
        {
        }
        var views = provider.GetRequiredService<SqliteCenteredViews>();
        var archive = Assert.ContainsSingle(await views.ReadArchiveAsync(key.CaptureId, token).ConfigureAwait(false));
        var view = await views.ReadRevisionAsync(archive.Base.BaseId, archive.LatestRevisionId, token).ConfigureAwait(false);
        Assert.IsNotNull(view);
        return view;
    }
}
