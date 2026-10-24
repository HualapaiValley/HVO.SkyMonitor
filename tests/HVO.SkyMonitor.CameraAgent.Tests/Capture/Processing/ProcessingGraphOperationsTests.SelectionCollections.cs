using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task SceneSelectionsFlowThroughDraftValidationActivationRollbackAndOldReconstruction()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-scene-selections");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = PhysicalQueryConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var initialPipeline = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            var original = await CapturePhysicalSceneAsync(provider, config, 0, new PhysicalSceneCatalog()).ConfigureAwait(false);
            Assert.IsNotEmpty(original.Scene.Segments);
            Assert.IsTrue(original.Scene.Objects.Any(static item => item.Id == "solar-system:Mars"));
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "selections", "2");
            Assert.IsTrue(model.TryBuild(out var unchanged, out var errors), string.Join(' ', errors));
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            Assert.AreEqual(factory.PreviewPlan(config).EffectiveSha256, factory.PreviewPlan(config with { Pipeline = unchanged }).EffectiveSha256);
            Assert.AreEqual(config.Pipeline.Steps[0].Options!.Value.GetRawText(), unchanged.Steps[0].Options!.Value.GetRawText());
            var form = model.Nodes[0].OptionsForm!;
            form.RemoveCollectionItem("constellationIds", 0);
            form.RemoveCollectionItem("solarSystemBodies", 0);
            Assert.IsTrue(model.TryBuild(out var pipeline, out errors), string.Join(' ', errors));
            var draft = await operations.CreateRevisionAsync("selections", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "too-soon", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await CapturePhysicalSceneAsync(provider, config, 1, new PhysicalSceneCatalog()).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, changed.Execution.Execution.GraphRevisionId);
            Assert.IsEmpty(changed.Scene.Segments);
            Assert.IsFalse(changed.Scene.Objects.Any(static item => item.Kind == CelestialObjectKind.SolarSystemBody));
            Assert.IsEmpty(changed.Scene.Selection.ConstellationIds);
            Assert.IsEmpty(changed.Scene.Selection.SolarSystemBodies);
            Assert.AreNotEqual(original.Scene.SceneIdentitySha256, changed.Scene.SceneIdentitySha256);
            Assert.AreEqual(17, pipeline.Steps[0].Options!.Value.GetProperty("future").GetProperty("keep").GetInt32());
            // This same host-neutral binding/serialization is used by central reconstruction: no current graph lookup.
            CollectionAssert.AreEqual(original.Bytes, ProjectedSceneJson.Serialize(original.Stage.Bind(original.Scene.Source, original.Scene.Kind)));
            var retained = await ReadPhysicalSceneOutputAsync(provider, original.Execution).ConfigureAwait(false);
            CollectionAssert.AreEqual(original.Bytes, retained.Bytes);
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await CapturePhysicalSceneAsync(provider, config, 2, new PhysicalSceneCatalog()).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, restored.Execution.Execution.GraphRevisionId);
            // Later captures have later sidereal coordinates; rollback restores selection/topology, while
            // the retained stage above proves exact reconstruction at the original effective instant.
            CollectionAssert.AreEqual(original.Scene.Segments.Select(static item => (item.ConstellationId, item.FromObjectId, item.ToObjectId)).ToArray(),
                restored.Scene.Segments.Select(static item => (item.ConstellationId, item.FromObjectId, item.ToObjectId)).ToArray());
            CollectionAssert.AreEqual(original.Scene.Selection.ConstellationIds.ToArray(), restored.Scene.Selection.ConstellationIds.ToArray());
            CollectionAssert.AreEqual(original.Scene.Selection.SolarSystemBodies.ToArray(), restored.Scene.Selection.SolarSystemBodies.ToArray());
            CollectionAssert.AreEqual(original.Scene.Objects.Select(static item => item.Id).ToArray(), restored.Scene.Objects.Select(static item => item.Id).ToArray());
            var retainedPipeline = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initialPipeline!.Steps[0].Options!.Value.GetRawText(), retainedPipeline!.Steps[0].Options!.Value.GetRawText());
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow("{\"constellationIds\":[\"ORI\",\"ORI\",\"retained-id\"]}", true)]
    [DataRow("{\"solarSystemBodies\":[4,\"mArS\",\"4\",\"Sun, Mars\"]}", true)]
    [DataRow("{\"constellationIds\":[] ,\"solarSystemBodies\":[]}", true)]
    [DataRow("{}", true)]
    [DataRow("{\"constellationIds\":[\" \"]}", false)]
    [DataRow("{\"solarSystemBodies\":[99]}", false)]
    [DataRow("{\"solarSystemBodies\":[\"Pluto\"]}", false)]
    [DataRow("{\"constellationIds\":null}", false)]
    [DataRow("{\"solarSystemBodies\":null}", false)]
    public async Task SceneSelectionsSharedCodecAndServerAgreeOnLegacyAndInvalidValues(string json, bool valid)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-selection-binding");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration();
            var options = JsonDocument.Parse(json).RootElement.Clone();
            var pipeline = config.Pipeline with { Steps = [config.Pipeline.Steps[0] with { Options = options }] };
            Assert.AreEqual(valid, ProcessingNodeOptionsCodec.Decode(ProjectedSceneOptionDescriptors.Descriptor, options).IsValid);
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            if (valid) Assert.IsNotNull(factory.PreviewPlan(config with { Pipeline = pipeline }));
            else
            {
                Assert.Throws<Exception>(() => factory.PreviewPlan(config with { Pipeline = pipeline }));
                await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
                await Assert.ThrowsAsync<Exception>(async () =>
                    await operations.CreateRevisionAsync("invalid", "2", pipeline, "invalid", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            }
        }
        finally { DeletePhysicalQueryRoot(root); }
    }
}
