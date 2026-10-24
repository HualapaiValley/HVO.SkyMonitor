using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task ManifestDraftValidatesExplicitlyActivatesRollsBackAndReconstructsOldPlan()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-manifest-options");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = MaterializerConfiguration();
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var before = factory.PreviewPlan(config);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var original = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!;
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "manifest", "2");
            Assert.IsTrue(model.TryBuild(out var unchanged, out _));
            Assert.AreEqual(before.EffectiveSha256, factory.PreviewPlan(config with { Pipeline = unchanged }).EffectiveSha256);
            var form = model.Nodes[^2].OptionsForm!;
            Assert.AreSame(OverlayManifestOptionDescriptors.Descriptor, form.Descriptor);
            form.AddRow("layers");
            form.Rows("layers")[0].Set("kind", "constellations");
            form.Rows("layers")[0].Set("enabled", "false");
            form.Rows("layers")[0].Set("zOrder", "-1024");
            form.RowsChanged("layers");
            form.Set("constellationOpacityMillionths", "0");
            Assert.IsTrue(model.TryBuild(out var edited, out _));
            var draft = await operations.CreateRevisionAsync("manifest", "2", edited, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, active.ActiveRevisionId);
            var oldGraph = factory.CreateGraph(config);
            var newGraph = factory.CreateGraph(config with { Pipeline = edited });
            try
            {
                CollectionAssert.AreEqual(oldGraph.Nodes.SkipLast(2).Select(static node => node.PlanSha256).ToArray(),
                    newGraph.Nodes.SkipLast(2).Select(static node => node.PlanSha256).ToArray());
                Assert.AreEqual("overlay-manifest-v1", newGraph.SharedPlan!.Nodes.Single(static node => node.Definition.Id == "manifest").Definition.StepVersion);
                CollectionAssert.AreEqual(oldGraph.Nodes[^2].Dependencies.ToArray(), newGraph.Nodes[^2].Dependencies.ToArray());
                Assert.AreNotEqual(oldGraph.Nodes[^2].PlanSha256, newGraph.Nodes[^2].PlanSha256);
            }
            finally { oldGraph.DisposeSteps(); newGraph.DisposeSteps(); }
            var retained = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!;
            Assert.AreEqual(original.Steps[^2].Options?.GetRawText(), retained.Steps[^2].Options?.GetRawText());
            Assert.AreEqual(before.EffectiveSha256, factory.PreviewPlan(config with { Pipeline = retained }).EffectiveSha256);
            var rollback = await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, rollback.ActiveRevisionId);
            var missing = edited with { Steps = edited.Steps.Select(step => step.Id == "manifest" ? step with { DependsOn = ["preview"] } : step).ToArray() };
            Assert.Throws<Exception>(() => factory.PreviewPlan(config with { Pipeline = missing }));
            var invalid = edited with
            {
                Steps = edited.Steps.Select(step => step.Id == "manifest" ? step with
                {
                    Options = JsonSerializer.Deserialize<JsonElement>("{\"layers\":[{\"kind\":\"environment\"},{\"kind\":\"corner-annotations\"}]}")
                } : step).ToArray()
            };
            await Assert.ThrowsAsync<Exception>(async () =>
                await operations.CreateRevisionAsync("invalid", "3", invalid, "invalid", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }
}
