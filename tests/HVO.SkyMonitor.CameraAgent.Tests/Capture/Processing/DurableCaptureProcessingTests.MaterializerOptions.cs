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
    [TestMethod]
    [TestCategory("Unit")]
    public async Task MaterializerTypedSelectionChangesOnlyCompositionAndReconstructsOldRetainedOutput()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var graph = await CreateProductionLayeredGraphAsync(fixture, root, [new() { Kind = "cloud-mask", Enabled = false }]).ConfigureAwait(false);
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
                var upstream = context.ProcessingProducts.Where(static product => product.Role != FrameArtifactRole.AnnotatedPreview).ToArray();
                var upstreamBytes = upstream.Select(static product => product.Payload.ToArray()).ToArray();
                var original = context.ProcessingProducts[^1];
                var manifestProduct = upstream.Single(static product => product.SchemaVersion == OverlayManifestV1.CurrentSchemaVersion);
                var manifest = LayeredPresentationJson.ParseManifest(manifestProduct.Payload).Document!;
                Assert.IsFalse(manifest.Layers.Single(static layer => layer.LayerKind == "cloud-mask").EnabledByDefault);
                var source = JsonSerializer.SerializeToElement(new PresentationMaterializerProcessingStepOptions
                {
                    OutputVariant = "annotated-preview",
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
                var form = new ProcessingNodeOptionsForm(PresentationMaterializerOptionDescriptors.Descriptor, source);
                form.AddCollectionItem("enabledLayerKinds");
                form.SetCollectionItem("enabledLayerKinds", 0, "cloud-mask");
                Assert.IsTrue(form.TryBuild(out var edited, out _));
                var options = edited!.Value.Deserialize<PresentationMaterializerProcessingStepOptions>(WebEnumJsonOptions)!;
                context.BeginNode("selection", graph.Nodes[^1].Dependencies);
                var step = new PresentationMaterializerCaptureProcessingStep(new("selection", "PresentationMaterializer", 81), options);
                await step.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                var selected = context.ProcessingProducts[^1];
                Assert.AreNotEqual(original.ChecksumSha256, selected.ChecksumSha256);
                Assert.AreNotEqual(original.Recipe.IdentitySha256, selected.Recipe.IdentitySha256);
                Assert.AreNotEqual(original.OutputIdentitySha256, selected.OutputIdentitySha256);
                for (var i = 0; i < upstream.Length; i++) CollectionAssert.AreEqual(upstreamBytes[i], upstream[i].Payload.ToArray());
                // Run the same shared executor central materialization uses over the exact retained dependencies.
                context.BeginNode("central-equivalent", graph.Nodes[^1].Dependencies);
                var baseProduct = context.GetDependencyProducts().Single(static product => product.Role == FrameArtifactRole.Preview);
                var byId = context.GetDependencyProducts().ToDictionary(static product => CaptureProcessingContext.CreateArtifactId(product.OutputIdentitySha256));
                var layers = manifest.Layers.Select(layer => new PresentationLayerProductInput(layer,
                    CameraAgentRecipeExecutionAdapter.CreateArtifact(context, byId[layer.SourceProduct.ArtifactId]))).ToArray();
                var equivalent = PresentationMaterializationExecutor.MaterializePacked(
                    CameraAgentRecipeExecutionAdapter.CreateArtifact(context, baseProduct),
                    CameraAgentRecipeExecutionAdapter.CreateArtifact(context, manifestProduct), manifest, layers,
                    manifest.Layers.Where(static layer => layer.LayerKind == "cloud-mask").Select(static layer => layer.LayerIdentitySha256), "annotated-preview");
                CollectionAssert.AreEqual(selected.Payload.ToArray(), equivalent.Payload.ToArray());
                Assert.AreEqual(selected.OutputIdentitySha256, equivalent.OutputIdentitySha256);
                CollectionAssert.AreEqual(selected.SourceArtifactIds.ToArray(), equivalent.SourceArtifactIds.ToArray());
                // An absent supported optional kind remains absent; selection does not synthesize a layer.
                var missing = ProcessingNodeOptionsCodec.Encode(PresentationMaterializerOptionDescriptors.Descriptor, source,
                    new Dictionary<string, ProcessingOptionValue> { ["enabledLayerKinds"] = ProcessingOptionValue.FromChoiceCollection([OrbitalTrackPresentation.LayerKind]) });
                Assert.IsTrue(missing.IsValid);
                var missingStep = new PresentationMaterializerCaptureProcessingStep(new("missing", "PresentationMaterializer", 81),
                    missing.Options!.Value.Deserialize<PresentationMaterializerProcessingStepOptions>(WebEnumJsonOptions)!);
                context.BeginNode("missing", graph.Nodes[^1].Dependencies);
                await missingStep.ProcessAsync(context, CancellationToken.None).ConfigureAwait(false);
                CollectionAssert.AreEqual(baseProduct.Payload.ToArray(), context.ProcessingProducts[^1].Payload.ToArray());
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
