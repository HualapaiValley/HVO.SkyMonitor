using System.Security.Cryptography;
using HVO.SkyMonitor.Processing;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task PreviewAndJpegDraftRequiresValidationActivationAndRollback()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-preview-options");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var baseline = CreateConfiguration();
            var config = baseline with
            {
                Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 } },
                Pipeline = new CapturePipelineConfig([
                    new("Preview", "preview", 17, JsonSerializer.SerializeToElement(new { future = new { keep = 17 } }), ["$raw"]),
                    new("CloudAssessment", "cloud", 20, JsonSerializer.SerializeToElement(new { gridColumns = 2, gridRows = 1, minimumSamplesPerTile = 1 }), ["$raw"]),
                    new("WeatherCloudOverlay", "weather", 30, DependsOn: ["preview", "cloud"]),
                    new("JpegEncoding", "jpeg", 40, JsonSerializer.SerializeToElement(new { MaximumDimension = (int?)null, future = new[] { 100 } }), ["weather"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            async Task<ProcessingGraphExecutionDetail> Capture(int sequence)
            {
                // Repeat the same retained bytes; only capture identity/time varies.
                var submission = CreateSubmission(0, CameraPixelFormat.Mono16, capturedUtc: new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero).AddSeconds(sequence * 2));
                var receipt = await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, config).ConfigureAwait(false);
                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                return (await operations.ReadExecutionDetailAsync(id!.Value, CancellationToken.None).ConfigureAwait(false))!;
            }
            var original = await Capture(0).ConfigureAwait(false);
            Assert.AreEqual("default", original.Nodes.Single(static node => node.NodeId == "preview").Outputs.Single().Variant);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "preview", "2");
            model.Nodes[0].OptionsForm!.Set("outputVariant", "preview-custom");
            model.Nodes[0].OptionsForm!.Set("asinhStrength", "12");
            model.Nodes[3].OptionsForm!.Set("jpegQuality", "35");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.AreEqual(17, pipeline.Steps[0].Order);
            var draft = await operations.CreateRevisionAsync("preview", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await Capture(1).ConfigureAwait(false)).Execution.GraphRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await Capture(2).ConfigureAwait(false);
            Assert.AreEqual("preview-custom", changed.Nodes.Single(static node => node.NodeId == "preview").Outputs.Single().Variant);
            Assert.AreEqual(draft.RevisionId, changed.Execution.GraphRevisionId);
            Assert.HasCount(1, changed.Nodes.Single(static node => node.NodeId == "jpeg").Outputs);
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(config.Pipeline.Steps[0].Options!.Value.GetRawText(), retained!.Steps[0].Options!.Value.GetRawText());
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await Capture(3).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, restored.Execution.GraphRevisionId);
            Assert.HasCount(1, restored.Nodes.Single(static node => node.NodeId == "jpeg").Outputs);
            Assert.AreEqual("default", restored.Nodes.Single(static node => node.NodeId == "preview").Outputs.Single().Variant);
            Console.WriteLine($"Original={original.Execution.GraphRevisionId}; edited={changed.Execution.GraphRevisionId}; rollback={restored.Execution.GraphRevisionId}; retained options SHA256={Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(retained.Steps[0].Options!.Value.GetRawText())))}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
    [TestMethod]
    [DataRow("Preview", false)]
    [DataRow("Preview", true)]
    [DataRow("CalibratedPreview", false)]
    [DataRow("CalibratedPreview", true)]
    [DataRow("CombinedPreview", false)]
    [DataRow("CombinedPreview", true)]
    public void PreviewPanelsMatchRegisteredVersionsRuntimeDefaultsAndUnchangedPlans(string alias, bool fixedTransfer)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-preview-descriptor");
        try
        {
            using var provider = CreateProvider(root);
            var baseline = CreateConfiguration();
            var options = JsonSerializer.SerializeToElement(new
            {
                FixedTransfer = fixedTransfer ? new HVO.SkyMonitor.Imaging.FixedDisplayTransferOptions() : null,
                future = new[] { 100 }
            });
            var steps = new List<CaptureProcessingStepConfig>();
            var input = "$raw";
            if (alias != "Preview")
            {
                steps.Add(new("Calibration", "calibration", 10, DependsOn: [input]));
                input = "calibration";
            }
            if (alias == "CombinedPreview")
            {
                steps.Add(new("RollingCombination", "combined", 20, DependsOn: [input]));
                input = "combined";
            }
            steps.Add(new(alias, "preview", 50, options, [input]));
            var config = baseline with
            {
                Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 } },
                Pipeline = new CapturePipelineConfig(steps)
            };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var graph = factory.CreateGraph(config);
            var node = graph.SharedPlan!.Nodes.Single(static node => node.Definition.Id == "preview").Definition;
            var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(node.StepAlias, node.StepVersion).Descriptor!;
            Assert.IsNotNull(descriptor);
            Assert.AreEqual(fixedTransfer ? "fixed-preview-v1" : "encoded-preview-v1", node.StepVersion);
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
            PreviewProcessingStepOptions runtimeOptions = alias switch
            {
                "CalibratedPreview" => new CalibratedPreviewProcessingStepOptions(),
                "CombinedPreview" => new CombinedPreviewProcessingStepOptions(),
                _ => new PreviewProcessingStepOptions()
            };
            var runtime = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(runtimeOptions));
            foreach (var option in defaults.Options)
                Assert.AreEqual(option.Value, runtime.Get(option.Descriptor.Key).Value);
            graph.DisposeSteps();
            var draft = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, "source", "preview", "2");
            Assert.AreEqual(descriptor, draft.Nodes[^1].OptionsForm!.Descriptor);
            Assert.IsTrue(draft.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.IsFalse(draft.HasGraphChanges(pipeline));
            Assert.AreEqual(options.GetRawText(), pipeline.Steps[^1].Options!.Value.GetRawText());
            Assert.AreEqual(factory.PreviewPlan(config).EffectiveSha256, factory.PreviewPlan(config with { Pipeline = pipeline }).EffectiveSha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
