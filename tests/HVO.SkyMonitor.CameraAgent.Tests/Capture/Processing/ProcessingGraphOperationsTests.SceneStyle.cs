using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Imaging;
using HVO.SkyMonitor.Processing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public void SceneStyleWholeDraftUpgradeCompilesConsumersAtomicallyAndRejectsMissingOptIn()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scene-style-consumers-1660");
        try
        {
            using var provider = CreateProvider(root);
            var config = MaterializerConfiguration();
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var graph = factory.CreateGraph(config);
            try
            {
                var before = graph.SharedPlan!.DefinitionIdentitySha256;
                var upgradedGraph = ProcessingNodeOptionsCatalog.BuiltIn.ApplyUpgrade(ProcessingGraphJson.Parse(System.Text.Encoding.UTF8.GetBytes(graph.SharedPlan.CanonicalDefinition.GetRawText())).Definition!,
                    ProcessingNodeOptionsCatalog.BuiltIn.PlanUpgrade(ProcessingGraphJson.Parse(System.Text.Encoding.UTF8.GetBytes(graph.SharedPlan.CanonicalDefinition.GetRawText())).Definition!), "style-1");
                Assert.IsTrue(upgradedGraph.IsValid, string.Join(' ', upgradedGraph.Diagnostics));
                Assert.AreEqual(before, graph.SharedPlan.DefinitionIdentitySha256);
                Assert.IsTrue(upgradedGraph.Definition!.Nodes.Where(node => node.StepAlias is "OverlayManifest" or "PresentationMaterializer")
                    .All(node => node.EffectiveOptions.GetProperty("useSceneStyle").GetBoolean()));
            }
            finally { graph.DisposeSteps(); }
            var source = JsonSerializer.Serialize(config.Pipeline);
            var upgraded = SceneStyleDraftUpgrade.Upgrade(config.Pipeline);
            Assert.AreEqual(source, JsonSerializer.Serialize(config.Pipeline));
            Assert.IsTrue(upgraded.Steps.Where(step => step.Type is "OverlayManifest" or "PresentationMaterializer")
                .All(step => step.Options!.Value.GetProperty("useSceneStyle").GetBoolean()));
            Assert.IsNotNull(factory.PreviewPlan(config with { Pipeline = upgraded }));
            var noOptIn = upgraded with
            {
                Steps = upgraded.Steps.Select(step => step.Type is "OverlayManifest" or "PresentationMaterializer"
                ? step with { Options = config.Pipeline.Steps.Single(original => original.Id == step.Id).Options } : step).ToArray()
            };
            Assert.Throws<Exception>(() => factory.PreviewPlan(config with { Pipeline = noOptIn }));
            var bad = config.Pipeline with
            {
                Steps = [.. config.Pipeline.Steps,
                config.Pipeline.Steps[4] with { Id = "bad", Options = JsonSerializer.Deserialize<JsonElement>("{\"labelFont\":\"PlexBold\"}") }]
            };
            var original = JsonSerializer.Serialize(bad);
            Assert.ThrowsExactly<ArgumentException>(() => SceneStyleDraftUpgrade.Upgrade(bad));
            Assert.AreEqual(original, JsonSerializer.Serialize(bad));
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    public async Task SceneStyleActualUpgradeValidatesActivatesRejectsStaleAndRollsBackToActualPredecessor()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scene-style-1660");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateProjectedSceneConfiguration();
            config = config with
            {
                Rig = config.Rig with { Sensor = config.Rig.Sensor with { WidthPixels = 512, HeightPixels = 512 } },
                Pipeline = new CapturePipelineConfig([
                    new("ProjectedScene", "projected-scene", 10, DependsOn: ["$raw"]),
                    new("ScenePresentationLayer", "overlay", 20,
                        JsonSerializer.Deserialize<JsonElement>("{ \"markerValue\":144, \"future\":[1e+02] }"), ["projected-scene"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var original = await CaptureConstellationsAsync(provider, config, 0, 512).ConfigureAwait(false);
            var upgraded = SceneStyleDraftUpgrade.Upgrade(config.Pipeline);
            var model = ProcessingGraphDraftModel.FromPipeline(upgraded, initial.ActiveRevisionId, "sky", "2");
            model.Nodes[1].OptionsForm!.Set("markerColor", "#ff8000");
            model.Nodes[1].OptionsForm!.Set("labelFont", "PlexRegular");
            model.Nodes[1].OptionsForm!.Set("cardinalFont", "MonoRegular");
            model.Nodes[1].OptionsForm!.Set("pointGlyph", "Crosshair");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            var draft = await operations.CreateRevisionAsync("sky", "2", pipeline, "create-style", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "too-early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate-style", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate-style", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.RollbackRevisionAsync(initial.ActiveRevisionId, initial.StateVersion, "stale-style", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            var changed = await CaptureConstellationsAsync(provider, config, 1, 512).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, changed.RevisionId);
            Assert.AreEqual(PresentationLayerPayloadV1.SceneStyleSchemaVersion, changed.Annotations.SchemaVersion);
            Assert.IsNotEmpty(changed.Annotations.Markers);
            Assert.IsTrue(changed.Annotations.Markers.All(marker => marker.Crosshair && marker.Color == new PresentationColor(255, 128, 0)));
            Assert.IsNotEmpty(changed.Annotations.TextBlocks);
            Assert.IsTrue(changed.Annotations.TextBlocks.All(block => block.Appearance!.Body.Face == PresentationFontFaceV3.PlexRegular));
            Assert.IsTrue(changed.Annotations.TextBlocks.SelectMany(block => block.Lines).All(line => line.EndsWith(" (expected)", StringComparison.Ordinal)));
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var retained = await store.ReadOutputByArtifactIdAsync(changed.AnnotationArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(SceneStylePresentation.ProducerVersion, retained!.Artifact.Recipe.ImplementationVersion);
            Assert.IsNotNull(changed.Annotations.SceneStyleIdentitySha256);
            var oldBytes = (await ReadConstellationPayloadAsync(provider, original.AnnotationArtifactId).ConfigureAwait(false)).Bytes;
            CollectionAssert.AreEqual(PresentationLayerPayloadJson.Serialize(original.Annotations), oldBytes);
            var changedBytes = PresentationLayerPayloadJson.Serialize(changed.Annotations);
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback-style", "test", null, CancellationToken.None).ConfigureAwait(false);
            var reproduced = await CaptureConstellationsAsync(provider, config, 2, 512).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, reproduced.RevisionId);
            CollectionAssert.AreEqual(RenderSceneLayer(original.Annotations), RenderSceneLayer(reproduced.Annotations));
            CollectionAssert.AreEqual(changedBytes, (await ReadConstellationPayloadAsync(provider, changed.AnnotationArtifactId).ConfigureAwait(false)).Bytes);
            var invalid = pipeline with
            {
                Steps = pipeline.Steps.Select(step => step.Id == "overlay"
                ? step with { Options = JsonSerializer.Deserialize<JsonElement>("{\"pointGlyph\":\"star\"}") } : step).ToArray()
            };
            await Assert.ThrowsExactlyAsync<ValidationException>(async () =>
                await operations.CreateRevisionAsync("invalid", "3", invalid, "bad-style", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
