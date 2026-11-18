using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task OpenNgcQueryFollowsValidatedGraphActivationAndRollback()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-openngc-options");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = PhysicalQueryConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var source = new HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky.VirtualSkyGraphSelectionProvider(operations, enabled: true);
            var original = await source.ReadAsync(config, CancellationToken.None).ConfigureAwait(false);
            var edited = config.Pipeline with
            {
                Steps = [config.Pipeline.Steps[0] with
                { Options = JsonSerializer.SerializeToElement(new { deepSkyEnabled = true, deepSkyNgc = false, deepSkyIc = false, deepSkyMaximumMagnitude = 8 }) }]
            };
            var draft = await operations.CreateRevisionAsync("deep sky", "2", edited, "create-deep", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(original!.RevisionId, (await source.ReadAsync(config, CancellationToken.None).ConfigureAwait(false))!.RevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate-deep", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate-deep", "test", null, CancellationToken.None).ConfigureAwait(false);
            var selected = await source.ReadAsync(config, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, selected!.RevisionId);
            Assert.IsTrue(selected.Pipeline.Steps[0].Options!.Value.GetProperty("deepSkyEnabled").GetBoolean());
            var submission = CreateSubmission(0, CameraPixelFormat.Mono16, DateTimeOffset.UnixEpoch.AddDays(1));
            var frame = submission.Result.Frame!;
            var capturedScene = new SceneProvenance("scene", "rig", "HYG", "fixture", new string('a', 64), "Perspective", "p1", "a1", "s1",
                CaptureGraphRevisionId: initial.ActiveRevisionId);
            submission = submission with { Result = submission.Result with { Frame = frame with { Metadata = frame.Metadata with { Scene = capturedScene } } } };
            var binding = await provider.GetRequiredService<IRawCaptureIngress>().PrepareGraphBindingAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, binding!.Revision.State.RevisionId, "An exposure already rendered under a graph keeps that immutable revision after activation changes.");
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback-deep", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(original.RevisionId, (await source.ReadAsync(config, CancellationToken.None).ConfigureAwait(false))!.RevisionId);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    public void ProjectedSceneDescriptorMatchesRegisteredVersionAndRuntimeDefaultsWithoutChangingPlanIdentity()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-projected-defaults");
        try
        {
            using var provider = CreateProvider(root);
            var config = PhysicalQueryConfiguration();
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var plan = factory.PreviewPlan(config);
            var descriptor = ProjectedSceneOptionDescriptors.Descriptor;
            var graph = factory.CreateGraph(config);
            try { Assert.AreEqual(descriptor.StepVersion, graph.SharedPlan!.Nodes.Single().Definition.StepVersion); }
            finally { graph.DisposeSteps(); }
            var runtime = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new ProjectedSceneCaptureProcessingStepOptions()));
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
            foreach (var option in defaults.Options)
                Assert.AreEqual(option.Value, runtime.Get(option.Descriptor.Key).Value, option.Descriptor.Key);
            var model = HVO.SkyMonitor.CameraAgent.Services.ProcessingGraphDraftModel.FromPipeline(config.Pipeline, "source", "query", "2");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.AreEqual(config.Pipeline.Steps[0].Options!.Value.GetRawText(), pipeline.Steps[0].Options!.Value.GetRawText());
            Assert.AreEqual(plan.EffectiveSha256, factory.PreviewPlan(config with { Pipeline = pipeline }).EffectiveSha256);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }

    [TestMethod]
    [DataRow("{\"maximumMagnitude\":-30.01}")]
    [DataRow("{\"maximumMagnitude\":30.01}")]
    [DataRow("{\"maximumResults\":0}")]
    [DataRow("ceiling")]
    [DataRow("{\"includeConstellationEndpointStars\":null}")]
    [DataRow("{\"maximumResults\":1.5}")]
    public async Task ProjectedSceneServerPreviewAndCreateRejectInvalidQueryOptions(string json)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-projected-invalid");
        try
        {
            using var provider = CreateProvider(root);
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = PhysicalQueryConfiguration();
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            if (json == "ceiling") json = FormattableString.Invariant($"{{\"maximumResults\":{ProjectedSceneJson.MaximumObjectCount + 1}}}");
            var options = JsonDocument.Parse(json).RootElement.Clone();
            var pipeline = config.Pipeline with { Steps = [config.Pipeline.Steps[0] with { Options = options }] };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var exceptionType = options.EnumerateObject().Any(static item => item.Value.ValueKind == JsonValueKind.Null || item.Value.GetRawText() == "1.5")
                ? typeof(JsonException) : typeof(ValidationException);
            var previewError = Assert.Throws<Exception>(() => factory.PreviewPlan(config with { Pipeline = pipeline }));
            Assert.IsInstanceOfType(previewError, exceptionType);
            var createError = await Assert.ThrowsAsync<Exception>(async () =>
                await operations.CreateRevisionAsync("invalid", "2", pipeline, "invalid", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.IsInstanceOfType(createError, exceptionType);
            Assert.AreEqual(initial.ActiveRevisionId, (await operations.GetRegistryAsync(CancellationToken.None).ConfigureAwait(false)).ActiveRevisionId);
        }
        finally { DeletePhysicalQueryRoot(root); }
    }
}
