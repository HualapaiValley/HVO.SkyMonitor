using System.Security.Cryptography;
using System.ComponentModel.DataAnnotations;
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
    public async Task CalibrationRouteDraftRequiresValidationAndActivationAndRollbackReconstructsRetainedOutput()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-calibration-options");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var config = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([
                    new("Calibration", "calibration", 17, JsonSerializer.SerializeToElement(new { future = new { keep = 17 } }), ["$raw"])
                ])
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            async Task<ProcessingGraphExecutionDetail> Capture(int sequence)
            {
                // Repeat the same retained bytes; only capture identity/time varies.
                var submission = CreateSubmission(0, capturedUtc: new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero).AddSeconds(sequence * 2));
                var receipt = await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, config).ConfigureAwait(false);
                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                return (await operations.ReadExecutionDetailAsync(id!.Value, CancellationToken.None).ConfigureAwait(false))!;
            }
            var original = await Capture(0).ConfigureAwait(false);
            Assert.AreEqual("none", original.Nodes.Single().Outputs.Single().Variant);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "calibration", "2");
            model.Nodes[0].OptionsForm!.Set("outputVariant", "calibrated-custom");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.AreEqual(17, pipeline.Steps[0].Order);
            var draft = await operations.CreateRevisionAsync("calibration", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await Capture(1).ConfigureAwait(false)).Execution.GraphRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await Capture(2).ConfigureAwait(false);
            Assert.AreEqual("calibrated-custom", changed.Nodes.Single().Outputs.Single().Variant);
            Assert.AreEqual(draft.RevisionId, changed.Execution.GraphRevisionId);
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(config.Pipeline.Steps[0].Options!.Value.GetRawText(), retained!.Steps[0].Options!.Value.GetRawText());
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await Capture(3).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, restored.Execution.GraphRevisionId);
            Assert.AreEqual("none", restored.Nodes.Single().Outputs.Single().Variant);
            Console.WriteLine($"Original={original.Execution.GraphRevisionId}; edited={changed.Execution.GraphRevisionId}; rollback={restored.Execution.GraphRevisionId}; retained options SHA256={Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(retained.Steps[0].Options!.Value.GetRawText())))}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
    [TestMethod]
    [DataRow("None", false, "linear-normalization-none-v1")]
    [DataRow("SyntheticReferences", true, "reference-calibration-linear16-v2")]
    [DataRow("CalibrationLibrary", false, "reference-calibration-linear16-v2")]
    public void CalibrationDescriptorMatchesRuntimeDefaultsVersionsAndUnchangedPlan(string strategy, bool hasModel, string version)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-calibration-descriptor");
        try
        {
            using var provider = CreateProvider(root);
            var options = JsonSerializer.SerializeToElement(new
            {
                Strategy = strategy,
                SyntheticCalibration = hasModel ? new HVO.SkyMonitor.Imaging.SyntheticCalibrationModelV1() : null
            });
            var baseline = CreateConfiguration();
            var config = baseline with
            {
                Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 } },
                Pipeline = new CapturePipelineConfig([new("Calibration", "calibration", 17, options, ["$raw"])])
            };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var graph = factory.CreateGraph(config);
            var node = graph.SharedPlan!.Nodes.Single().Definition;
            Assert.AreEqual(version, node.StepVersion);
            var descriptor = ProcessingNodeOptionsCatalog.BuiltIn.Resolve(node.StepAlias, node.StepVersion).Descriptor!;
            Assert.IsNotNull(descriptor);
            var defaults = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new { }));
            var runtime = ProcessingNodeOptionsCodec.Decode(descriptor, JsonSerializer.SerializeToElement(new CalibrationProcessingStepOptions()));
            foreach (var option in defaults.Options)
                Assert.AreEqual(option.Value, runtime.Get(option.Descriptor.Key).Value);
            graph.DisposeSteps();
            var draft = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, "source", "calibration", "2");
            Assert.IsTrue(draft.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.IsFalse(draft.HasGraphChanges(pipeline));
            Assert.AreEqual(options.GetRawText(), pipeline.Steps[0].Options!.Value.GetRawText());
            Assert.AreEqual(factory.PreviewPlan(config).EffectiveSha256, factory.PreviewPlan(config with { Pipeline = pipeline }).EffectiveSha256);
            if (strategy != "None")
                Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(config with { Rig = baseline.Rig }));
            draft.Nodes[0].OptionsForm!.Set("outputVariant", "corrected");
            Assert.IsTrue(draft.TryBuild(out var edited, out errors), string.Join(' ', errors));
            Assert.AreEqual(options.GetProperty("SyntheticCalibration").GetRawText(), edited.Steps[0].Options!.Value.GetProperty("SyntheticCalibration").GetRawText());
            Assert.AreNotEqual(factory.PreviewPlan(config).EffectiveSha256, factory.PreviewPlan(config with { Pipeline = edited }).EffectiveSha256);

        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("SyntheticReferences", false)]
    [DataRow("None", true)]
    [DataRow("CalibrationLibrary", true)]
    [DataRow("Other", false)]
    [DataRow("none", false)]
    public void CalibrationServerRetainsStrategyModelConsistencyRules(string strategy, bool hasModel)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-calibration-invalid");
        try
        {
            using var provider = CreateProvider(root);
            var config = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([new("Calibration", "calibration", 17,
                    JsonSerializer.SerializeToElement(new { strategy, syntheticCalibration = hasModel ? new HVO.SkyMonitor.Imaging.SyntheticCalibrationModelV1() : null }), ["$raw"])])
            };
            Assert.ThrowsExactly<ValidationException>(() => provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("calibrationPasses", 0)]
    [DataRow("calibrationPasses", 11)]
    [DataRow("maxCalibrationSeconds", 0)]
    [DataRow("maxCalibrationSeconds", 601)]
    public void CalibrationServerStillValidatesOpaqueCompatibilityBounds(string member, int value)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-calibration-bounds");
        try
        {
            using var provider = CreateProvider(root);
            var config = CreateConfiguration() with
            {
                Pipeline = new CapturePipelineConfig([new("Calibration", "calibration", 17,
                    JsonSerializer.SerializeToElement(new Dictionary<string, int> { [member] = value }), ["$raw"])])
            };
            Assert.ThrowsExactly<ValidationException>(() => provider.GetRequiredService<ICaptureProcessingPipelineFactory>().PreviewPlan(config));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
