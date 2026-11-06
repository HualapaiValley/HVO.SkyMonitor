using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    private static readonly JsonSerializerOptions MaterializerJsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task MaterializerSelectionDraftValidatesActivatesAndRollsBackWithoutChangingProducerPlans()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-materializer-options");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = MaterializerConfiguration();
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var before = factory.PreviewPlan(config);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var storedSource = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!.Steps[^1].Options!.Value.GetRawText();
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "selection", "2");
            Assert.IsTrue(model.TryBuild(out var unchanged, out _));
            Assert.AreEqual(before.EffectiveSha256, factory.PreviewPlan(config with { Pipeline = unchanged }).EffectiveSha256);
            var form = model.Nodes[^1].OptionsForm!;
            Assert.AreSame(PresentationMaterializerOptionDescriptors.Descriptor, form.Descriptor);
            form.AddCollectionItem("enabledLayerKinds");
            form.SetCollectionItem("enabledLayerKinds", 0, "corner-annotations");
            Assert.IsTrue(model.TryBuild(out var edited, out _));
            var draft = await operations.CreateRevisionAsync("selection", "2", edited, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, active.ActiveRevisionId);
            var originalGraph = factory.CreateGraph(config);
            var editedGraph = factory.CreateGraph(config with { Pipeline = edited });
            try
            {
                Assert.AreEqual(PresentationMaterializerOptionDescriptors.Descriptor.StepVersion,
                    editedGraph.SharedPlan!.Nodes.Single(static node => node.Definition.Id == "materialized").Definition.StepVersion);
                CollectionAssert.AreEqual(originalGraph.Nodes.SkipLast(1).Select(static node => node.PlanSha256).ToArray(),
                    editedGraph.Nodes.SkipLast(1).Select(static node => node.PlanSha256).ToArray());
                CollectionAssert.AreEqual(originalGraph.Nodes[^1].Dependencies.ToArray(), editedGraph.Nodes[^1].Dependencies.ToArray());
                Assert.AreNotEqual(originalGraph.Nodes[^1].PlanSha256, editedGraph.Nodes[^1].PlanSha256);
            }
            finally { originalGraph.DisposeSteps(); editedGraph.DisposeSteps(); }
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(storedSource, retained!.Steps[^1].Options!.Value.GetRawText());
            var rollback = await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, rollback.ActiveRevisionId);
            // Selection cannot relax required producer/manifest contracts.
            var missing = edited with { Steps = edited.Steps.Select(step => step.Id == "materialized" ? step with { DependsOn = ["preview", "manifest"] } : step).ToArray() };
            Assert.Throws<Exception>(() => factory.PreviewPlan(config with { Pipeline = missing }));
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow("{}", true)]
    [DataRow("{\"enabledLayerKinds\":[]}", true)]
    [DataRow("{\"enabledLayerKinds\":[\"corner-annotations\",\"image-circle\"]}", true)]
    [DataRow("{\"enabledLayerKinds\":[\"Environment\"]}", false)]
    [DataRow("{\"enabledLayerKinds\":[\"environment\",\"corner-annotations\"]}", false)]
    [DataRow("{\"enabledLayerKinds\":null}", false)]
    public async Task MaterializerSharedAndRuntimeValidationAgreeAndRejectInvalidDrafts(string json, bool valid)
    {
        var source = JsonDocument.Parse(json).RootElement.Clone();
        Assert.AreEqual(valid, ProcessingNodeOptionsCodec.Decode(PresentationMaterializerOptionDescriptors.Descriptor, source).IsValid);
        var runtime = source.Deserialize<PresentationMaterializerProcessingStepOptions>(MaterializerJsonOptions)!;
        if (source.TryGetProperty("enabledLayerKinds", out var token) && token.ValueKind == JsonValueKind.Null)
        {
            // The new shared validation rejects null before the runtime consumer can dereference it.
            Assert.IsFalse(valid);
        }
        else
            Assert.AreEqual(valid, Validator.TryValidateObject(runtime, new(runtime), new List<ValidationResult>(), true));
        if (valid) return;
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-invalid-materializer-options");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = MaterializerConfiguration();
            var bad = config.Pipeline with { Steps = config.Pipeline.Steps.Select(step => step.Id == "materialized" ? step with { Options = source } : step).ToArray() };
            await Assert.ThrowsAsync<Exception>(async () =>
                await provider.GetRequiredService<ProcessingGraphOperationsCoordinator>().CreateRevisionAsync("invalid", "2", bad, "invalid", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    private static CameraModuleConfig MaterializerConfiguration() => CreateProjectedSceneConfiguration() with
    {
        Pipeline = new CapturePipelineConfig([
            new("Calibration", "calibration", 10, DependsOn: ["$raw"]),
            new("RollingCombination", "rolling", 20, DependsOn: ["calibration"]),
            new("CombinedPreview", "preview", 30, DependsOn: ["rolling"]),
            new("ProjectedScene", "scene", 40, DependsOn: ["$raw"]),
            new("ScenePresentationLayer", "scene-layer", 50, DependsOn: ["scene"]),
            new("EnvironmentPresentationLayer", "environment", 60,
                JsonDocument.Parse("""{"widthPixels":2,"heightPixels":2}""").RootElement.Clone(), ["scene", "preview"]),
            new("OverlayManifest", "manifest", 70, DependsOn: ["preview", "scene-layer", "environment"]),
            new("PresentationMaterializer", "materialized", 80,
                JsonDocument.Parse("""{ "UseRichFrameFacts":false, "OutputVariant":"saved", "future":1e+02 }""").RootElement.Clone(),
                ["preview", "manifest", "scene-layer", "environment"])
        ], CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
    };
}
