using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    private static readonly int[] ManifestDefaultLayerOrders = [10, 15, 20, 25, 30, 40, 50];
    [TestMethod]
    [TestCategory("Unit")]
    public async Task ManifestOverridesKeepProducerDefaultsApplyOrderAndOpacityAndReconstructRetainedOutput()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var graph = await CreateProductionLayeredGraphAsync(fixture, root).ConfigureAwait(false);
            var context = new CaptureProcessingContext(fixture.Item.Config, fixture.Item.Submission, fixture.Item.RawCapture);
            try
            {
                foreach (var node in graph.Nodes)
                {
                    context.BeginNode(node.Id, node.Dependencies);
                    var previous = context.ProcessingProducts.Count;
                    await node.Step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                    foreach (var product in context.ProcessingProducts.Skip(previous)) context.RegisterProcessingProduct(product);
                }
                var original = context.ProcessingProducts[^1];
                var oldManifestProduct = context.ProcessingProducts.Single(static product => product.SchemaVersion == OverlayManifestV1.CurrentSchemaVersion);
                var oldManifest = LayeredPresentationJson.ParseManifest(oldManifestProduct.Payload).Document!;
                CollectionAssert.AreEqual(ManifestDefaultLayerOrders, oldManifest.Layers.Select(static layer => layer.ZOrder).ToArray());
                Assert.IsTrue(oldManifest.Layers.All(static layer => layer.EnabledByDefault));
                Assert.AreEqual(800_000, oldManifest.Layers.Single(static layer => layer.LayerKind == "scene-constellations").OpacityMillionths);
                var upstream = context.ProcessingProducts.SkipLast(2).ToArray();
                var upstreamBytes = upstream.Select(static product => product.Payload.ToArray()).ToArray();
                var source = JsonSerializer.SerializeToElement(new OverlayManifestProcessingStepOptions
                {
                    OutputVariant = "overlay-manifest",
                    BasePreviewVariant = "combined-preview",
                    SceneAnnotationVariant = "scene-layer",
                    SceneCardinalVariant = "cardinal-layer",
                    SceneImageCircleVariant = "image-circle-layer",
                    SceneConstellationVariant = "constellation-layer",
                    CloudMaskVariant = "cloud-mask",
                    CloudLabelVariant = "cloud-label",
                    EnvironmentFactsVariant = "environment-facts",
                    EnvironmentVariant = "environment-layer"
                });
                var form = new ProcessingNodeOptionsForm(OverlayManifestOptionDescriptors.Descriptor, source);
                foreach (var (kind, enabled, order) in new[]
                {
                    ("scene-annotation", "false", "100"), ("image-circle", "true", ""),
                    ("cloud-mask", "false", "-1024"), (OrbitalTrackPresentation.LayerKind, "true", "1024")
                })
                {
                    form.AddRow("layers");
                    var row = form.Rows("layers")[^1];
                    row.Set("kind", kind); row.Set("enabled", enabled); row.Set("zOrder", order);
                    form.RowsChanged("layers");
                }
                form.Set("constellationOpacityMillionths", "0");
                Assert.IsTrue(form.TryBuild(out var edited, out _));
                var manifestStep = new OverlayManifestCaptureProcessingStep(new("edited-manifest", "OverlayManifest", 80),
                    edited!.Value.Deserialize<OverlayManifestProcessingStepOptions>(WebEnumJsonOptions)!);
                context.BeginNode("edited-manifest", graph.Nodes[^2].Dependencies);
                await manifestStep.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                var newManifestProduct = context.ProcessingProducts[^1];
                var manifest = LayeredPresentationJson.ParseManifest(newManifestProduct.Payload).Document!;
                Assert.HasCount(7, manifest.Layers, "An override for an absent optional kind cannot create it.");
                Assert.AreEqual(-1024, manifest.Layers.Single(static layer => layer.LayerKind == "cloud-mask").ZOrder);
                Assert.IsFalse(manifest.Layers.Single(static layer => layer.LayerKind == "cloud-mask").EnabledByDefault);
                Assert.AreEqual(100, manifest.Layers.Single(static layer => layer.LayerKind == "scene-annotation").ZOrder);
                Assert.AreEqual(101, manifest.Layers.Single(static layer => layer.LayerKind == "scene-cardinals").ZOrder);
                Assert.AreEqual(15, manifest.Layers.Single(static layer => layer.LayerKind == "scene-image-circle").ZOrder);
                Assert.IsTrue(manifest.Layers.Single(static layer => layer.LayerKind == "scene-image-circle").EnabledByDefault);
                Assert.AreEqual(0, manifest.Layers.Single(static layer => layer.LayerKind == "scene-constellations").OpacityMillionths);
                Assert.AreNotEqual(oldManifestProduct.OutputIdentitySha256, newManifestProduct.OutputIdentitySha256);
                var dependencies = graph.Nodes[^1].Dependencies.Select(static id => id == "overlay-manifest" ? "edited-manifest" : id).ToArray();
                context.BeginNode("edited-materialization", dependencies);
                await graph.Nodes[^1].Step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                var changed = context.ProcessingProducts[^1];
                Assert.AreNotEqual(original.ChecksumSha256, changed.ChecksumSha256);
                for (var i = 0; i < upstream.Length; i++) CollectionAssert.AreEqual(upstreamBytes[i], upstream[i].Payload.ToArray());
                // LogicHost uses this same executor over retained products and the manifest defaults.
                context.BeginNode("central-equivalent", dependencies);
                var baseProduct = context.GetDependencyProducts().Single(static product => product.Role == FrameArtifactRole.Preview);
                var byId = context.GetDependencyProducts().ToDictionary(static product => CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
                var layers = manifest.Layers.Select(layer => new PresentationLayerProductInput(layer,
                    CameraAgentRecipeExecutionAdapter.CreateArtifact(context, byId[layer.SourceProduct.ArtifactId]))).ToArray();
                var equivalent = PresentationMaterializationExecutor.MaterializePacked(
                    CameraAgentRecipeExecutionAdapter.CreateArtifact(context, baseProduct),
                    CameraAgentRecipeExecutionAdapter.CreateArtifact(context, newManifestProduct), manifest, layers,
                    manifest.Layers.Where(static layer => layer.EnabledByDefault).Select(static layer => layer.LayerIdentitySha256), "annotated-preview");
                Assert.AreEqual(changed.OutputIdentitySha256, equivalent.OutputIdentitySha256);
                CollectionAssert.AreEqual(changed.Payload.ToArray(), equivalent.Payload.ToArray());
                context.BeginNode("old-revision", graph.Nodes[^1].Dependencies);
                await graph.Nodes[^1].Step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                var restored = context.ProcessingProducts[^1];
                Assert.AreEqual(original.OutputIdentitySha256, restored.OutputIdentitySha256);
                CollectionAssert.AreEqual(original.Payload.ToArray(), restored.Payload.ToArray());
                CollectionAssert.AreEqual(original.SourceArtifactIds.ToArray(), restored.SourceArtifactIds.ToArray());
            }
            finally { graph.DisposeSteps(); }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
