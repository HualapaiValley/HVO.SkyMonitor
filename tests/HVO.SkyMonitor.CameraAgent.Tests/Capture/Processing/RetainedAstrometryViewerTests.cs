using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    [DataRow("accepted")]
    [DataRow("rejected")]
    [DataRow("unavailable")]
    [DataRow("budget")]
    [DataRow("no-data")]
    public async Task RetainedAstrometry_ViewerPublishesTypedDiagnosticsAndFrozenExportWithoutChangingBase(string scenario)
    {
        // Optional evidence destination belongs to this test assembly only. Production has no fixture switch.
        var evidenceRoot = Environment.GetEnvironmentVariable("HVO_ASTROMETRY_VIEWER_FIXTURE_ROOT");
        var root = evidenceRoot is null ? CreateTestRoot() : Path.Combine(Path.GetFullPath(evidenceRoot), scenario);
        if (evidenceRoot is not null)
        {
            Assert.IsFalse(Directory.Exists(root), "Use a fresh registered scratch directory for evidence fixtures.");
            Directory.CreateDirectory(root);
        }
        try
        {
            var f = await CreateAstrometryDurabilityFixtureAsync(root, scenario == "no-data" ? "accepted" : scenario).ConfigureAwait(false);
            var descriptor = f.Fixture.Manifest.Descriptor;
            var id = descriptor.Capture.CaptureId;
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(f.Fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var numericalGate = new CaptureAstrometryExecutionGate();
            var persistence = CreatePersistence(f.Fixture.Options, store, storage, telemetry);
            var astrometryGraph = AstrometryGraph(f, new(new ProcessingRecipeExecutor()), numericalGate,
                enabled: scenario != "no-data", persistence: persistence);
            var raw = new ProcessingArtifact(descriptor.Artifact.ArtifactId, FrameArtifactRole.Raw, descriptor.Artifact.Variant,
                ProcessingIdentity.CreateRecipeIdentity(descriptor.Artifact.Recipe).IdentitySha256, descriptor.Artifact.MediaType,
                descriptor.Layout, await File.ReadAllBytesAsync(Path.Combine(root, "raw.bin")).ConfigureAwait(false), descriptor.Artifact.CreatedUtc,
                descriptor.Controls.EffectiveExposure, CameraAgentRecipeExecutionAdapter.CreateCompatibility(descriptor), descriptor.Capture.CaptureSequence,
                descriptor.Artifact.SourceArtifactIds, descriptor.Timing.ExposureStartedUtc, descriptor.Timing.ExposureEndedUtc)
            { CaptureId = id, DescriptorIdentitySha256 = CaptureContractJson.ComputeDescriptorSha256(descriptor) };
            var executor = new ProcessingRecipeExecutor();
            var rolling = (await executor.ExecuteAsync(new(BuiltInProcessingRecipes.RollingMean,
                JsonSerializer.SerializeToElement(new { maximumFrameCount = 1 }), ProcessingInputSelector.Raw(), [raw], "rolling-mean")).ConfigureAwait(false)).Products.Single();
            ProcessingArtifact Artifact(ProcessingProduct p) => new(ProcessingIdentity.CreateArtifactId(p.OutputIdentitySha256), p.Role, p.Variant,
                p.Recipe.IdentitySha256, p.MediaType, p.Layout, p.Payload, raw.CreatedUtc, p.TotalIntegration, p.Compatibility,
                raw.CaptureSequence, p.SourceArtifactIds, raw.ObservationStartedUtc, raw.ObservationEndedUtc)
            { CaptureId = id, ProductKind = p.Kind, SchemaVersion = p.SchemaVersion, ContentIdentitySha256 = p.ContentIdentitySha256 ?? p.OutputIdentitySha256 };
            var preview = (await executor.ExecuteAsync(new(BuiltInProcessingRecipes.EncodedPreview,
                JsonSerializer.SerializeToElement(new EncodedPreviewOptions(OutputEncoding: "Packed")), ProcessingInputSelector.Combined(),
                [Artifact(rolling)], "combined-preview")).ConfigureAwait(false)).Products.Single();
            var previewArtifact = Artifact(preview);
            var sceneIdentity = ProjectedSceneJson.Parse(f.Scene.Payload).Scene!.SceneIdentitySha256;
            var manifest = PresentationProcessingProducts.CreateManifestProduct(
                PresentationProcessingProducts.CreateReference(previewArtifact, sceneIdentity), sceneIdentity, [], "overlay-manifest", previewArtifact);
            var wireManifest = LayeredPresentationJson.ParseManifest(manifest.Payload).Document!;
            var display = PresentationMaterializationExecutor.MaterializePacked(previewArtifact, Artifact(manifest), wireManifest,
                [], [], "viewer-display");
            var graph = new CaptureProcessingGraph([
                .. astrometryGraph.Nodes,
                new("viewer-rolling", new FixedArtifactProducingStep(rolling), [], false, BuiltInProcessingRecipes.RollingMean,
                    rolling.Role, rolling.Variant, new string('D', 64)),
                new("viewer-preview", new FixedArtifactProducingStep(preview), ["viewer-rolling"], false, BuiltInProcessingRecipes.EncodedPreview,
                    preview.Role, preview.Variant, new string('E', 64)),
                new("viewer-manifest", new FixedMetadataProducingStep(manifest), ["viewer-preview"], false, PresentationProcessingProducts.ManifestRecipeName,
                    manifest.Role, manifest.Variant, new string('F', 64)),
                new("viewer-display", new FixedArtifactProducingStep(display), ["viewer-manifest"], false, display.Recipe.Descriptor.Name,
                    display.Role, display.Variant, new string('A', 64))
            ]);
            var processed = await FrameProcessingWorker.ProcessGraphItemAsync(f.Fixture.Item, graph, persistence, telemetry,
                1, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, processed.Outcome, processed.Reason);
            using var artifacts = new CameraAgentArtifactService(f.Fixture.Options, store, new CameraAgentPreviewEncoder());
            using var retained = new CameraAgentRetainedAstrometryService(store, artifacts);
            if (scenario == "accepted")
            {
                var verified = await retained.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual("Retained", verified.Availability, verified.Reason);
                Assert.AreEqual(AstrometricAssessmentStatus.Accepted, verified.Solution!.Assessment.Status);
                var composed = await persistence.PublishAstrometricCompositionAsync(id,
                    ProcessingIdentity.CreateArtifactId(manifest.OutputIdentitySha256), verified, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(previewArtifact.ArtifactId, composed.Evidence.CompatibleBaseArtifactId, composed.Evidence.DiagnosticsReason);
            }
            var layers = new CameraAgentLayeredPresentationService(store, artifacts, persistence: persistence, astrometry: retained);
            var first = await layers.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, first.Status, first.Reason);
            Assert.IsNotNull(first.Presentation);
            var shown = first.Presentation;
            Assert.IsNotNull(shown.Astrometry);
            Assert.AreEqual(scenario == "accepted" ? 2 : 0, shown.Layers.Count);
            if (scenario == "accepted")
            {
                Assert.AreEqual(previewArtifact.ArtifactId, shown.Astrometry.CompatibleBaseArtifactId, shown.Astrometry.DiagnosticsReason);
                CollectionAssert.AreEquivalent(new[] { AstrometricDiagnosticPresentation.AssociationsLayer, AstrometricDiagnosticPresentation.ResidualsLayer }, shown.Layers.Select(l => l.Kind).ToArray());
                Assert.IsTrue(shown.Layers.All(l => !l.EnabledByDefault));
                var wrongSource = shown.Astrometry with { SourceDescriptor = descriptor with { Artifact = descriptor.Artifact with { ArtifactId = Guid.NewGuid() } } };
                var unsupported = await persistence.PublishAstrometricCompositionAsync(id, ProcessingIdentity.CreateArtifactId(manifest.OutputIdentitySha256), wrongSource, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNull(unsupported.Evidence.CompatibleBaseArtifactId);

                var selected = shown.Layers.Where(l => l.Kind == AstrometricDiagnosticPresentation.ResidualsLayer).Select(l => l.IdentitySha256).ToArray();
                var materializer = new CameraAgentPresentationMaterializer(store, persistence, layers);
                var saved = await materializer.SaveFrozenAsync(id, shown.ManifestIdentitySha256, selected, "owner", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Saved, saved.Status, saved.Reason);
                var second = await layers.GetAsync(id, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(shown.ManifestIdentitySha256, second.Presentation!.ManifestIdentitySha256);
                Assert.AreEqual(shown.PresentationIdentitySha256, second.Presentation.PresentationIdentitySha256);
                var replayed = await materializer.SaveFrozenAsync(id, shown.ManifestIdentitySha256, selected, "owner", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(saved.Receipt!.ArtifactId, replayed.Receipt!.ArtifactId);
                Assert.IsTrue(replayed.Receipt.Replayed);
                Assert.AreEqual(1, (await store.ReadCaptureProductsAsync(id, OverlayManifestV1.CurrentSchemaVersion, 2, CancellationToken.None).ConfigureAwait(false)).Count);
                var pixel = new HVO.SkyMonitor.Astronomy.PixelPoint(280, 240);
                var sky = shown.Astrometry.Mapping!.PixelToSky(pixel)!;
                var roundtrip = shown.Astrometry.Mapping.SkyToPixel(sky.EquatorialMeanOfDate)!.Value;
                Assert.AreEqual(pixel.X, roundtrip.X, 1e-7); Assert.AreEqual(pixel.Y, roundtrip.Y, 1e-7);
            }
            var baseOutput = await store.ReadOutputByArtifactIdAsync(previewArtifact.ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(baseOutput);
            CollectionAssert.AreEqual(preview.Payload.ToArray(), await File.ReadAllBytesAsync(Path.Combine(root, baseOutput.PayloadRelativePath)).ConfigureAwait(false));
            Assert.AreEqual(ProcessingIdentity.ComputePayloadSha256(raw.Payload), ProcessingIdentity.ComputePayloadSha256(await File.ReadAllBytesAsync(Path.Combine(root, "raw.bin")).ConfigureAwait(false)));
            if (evidenceRoot is not null)
                await File.WriteAllTextAsync(Path.Combine(root, "viewer-fixture.json"), JsonSerializer.Serialize(new
                {
                    Scenario = scenario,
                    CaptureId = id,
                    RawArtifactId = raw.ArtifactId,
                    BaseArtifactId = previewArtifact.ArtifactId,
                    shown.ManifestIdentitySha256,
                    BaseChecksum = baseOutput.Artifact.ChecksumSha256,
                    RawChecksum = descriptor.Artifact.ChecksumSha256,
                    Root = root
                }, AstrometryEvidenceJson)).ConfigureAwait(false);
            else if (scenario == "accepted")
            {
                // A sibling composition (for example refreshed measured trails) changes the parent manifest while
                // the immutable solution and its two diagnostic payloads remain identical.
                var revisedParent = PresentationProcessingProducts.CreateManifestProduct(wireManifest.BaseProduct,
                    new string('B', 64), [], "viewer-parent-revision", previewArtifact) with
                { SchemaVersion = TransientTrailPresentation.CompositionSchemaVersion };
                var parentOutput = await CaptureProcessingPersistence.CopyMetadataProductAsync(root, descriptor,
                    "viewer-parent-revision", revisedParent, CancellationToken.None).ConfigureAwait(false);
                var parentNode = new CaptureProcessingGraphNode("viewer-parent-revision", new FixedMetadataProducingStep(revisedParent),
                    [], false, revisedParent.Recipe.Descriptor.Name, revisedParent.Role, revisedParent.Variant, revisedParent.OutputIdentitySha256);
                var now = DateTimeOffset.UtcNow;
                await store.WriteNodeAsync(id, parentNode, DurableProcessingNodeStatus.Completed, null, 1, null, now, now,
                    TimeSpan.Zero, ProcessingOutcomeStatus.Produced,
                    [new DurableProcessingNodeInput(0, "Artifact", "base", previewArtifact.ArtifactId, previewArtifact.Role,
                        previewArtifact.Variant, previewArtifact.RecipeIdentitySha256, previewArtifact.SchemaVersion, previewArtifact.ContentIdentitySha256, true)],
                    0, null, [parentOutput], null, ProcessingNodeExecutionRoute.Unknown, CancellationToken.None).ConfigureAwait(false);
                var revised = await persistence.PublishAstrometricCompositionAsync(id, parentOutput.ArtifactId,
                    shown.Astrometry, CancellationToken.None).ConfigureAwait(false);
                Assert.AreNotEqual(shown.ManifestArtifactId, revised.ManifestArtifactId);
                Assert.AreEqual(previewArtifact.ArtifactId, revised.Evidence.CompatibleBaseArtifactId);
                Assert.AreEqual(2, (await store.ReadCaptureProductsAsync(id, AstrometricDiagnosticPresentation.CompositionSchema,
                    3, CancellationToken.None).ConfigureAwait(false)).Count);
                Assert.AreEqual(2, (await store.ReadCaptureProductsAsync(id, HVO.SkyMonitor.Imaging.PresentationLayerPayloadV1.CurrentSchemaVersion,
                    3, CancellationToken.None).ConfigureAwait(false)).Count, "A new parent revision reuses the existing diagnostic payloads.");
                await File.AppendAllTextAsync(Path.Combine(root, "raw.bin"), " ").ConfigureAwait(false);
                var invalidated = await layers.GetCurrentAsync(id, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentLayeredPresentationStatus.Found, invalidated.Status);
                Assert.IsEmpty(invalidated.Presentation!.Layers, "A cached measured SVG must disappear after its source becomes invalid.");
                Assert.IsNull(invalidated.Presentation.Astrometry!.Mapping);
                var staleSave = await new CameraAgentPresentationMaterializer(store, persistence, layers).SaveFrozenAsync(id,
                    shown.ManifestIdentitySha256, shown.Layers.Select(l => l.IdentitySha256).ToArray(), "owner", CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentPresentationMaterializationStatus.Conflict, staleSave.Status);
            }
        }
        finally { if (evidenceRoot is null) Directory.Delete(root, true); }
    }
}
