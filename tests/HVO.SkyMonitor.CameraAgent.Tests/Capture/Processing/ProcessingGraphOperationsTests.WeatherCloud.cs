using System.Security.Cryptography;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    public async Task WeatherOverlayDraftRequiresExplicitActivationAndRollbackRestoresRetainedRevision()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-weather-options");
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
                    new("Preview", "preview", 10, DependsOn: ["$raw"]),
                    new("CloudAssessment", "cloud", 20, DependsOn: ["$raw"]),
                    new("WeatherCloudOverlay", "weather", 90, JsonSerializer.SerializeToElement(new { future = new { keep = 17 } }), ["preview", "cloud"])
                ])
            };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var plan = factory.CreateGraph(config);
            var node = plan.SharedPlan!.Nodes.Single(static node => node.Definition.Id == "weather").Definition;
            Assert.AreEqual("weather-cloud-overlay-v1", node.StepVersion);
            var defaults = HVO.SkyMonitor.Processing.ProcessingNodeOptionsCodec.Decode(
                HVO.SkyMonitor.Processing.WeatherCloudOverlayOptionDescriptors.Descriptor, node.EffectiveOptions);
            Assert.IsTrue(defaults.IsValid);
            var runtimeDefaults = HVO.SkyMonitor.Processing.ProcessingNodeOptionsCodec.Decode(
                HVO.SkyMonitor.Processing.WeatherCloudOverlayOptionDescriptors.Descriptor,
                JsonSerializer.SerializeToElement(new WeatherCloudOverlayProcessingStepOptions()));
            foreach (var option in defaults.Options)
                Assert.AreEqual(runtimeDefaults.Get(option.Descriptor.Key).Value, option.Value, option.Descriptor.Key);
            plan.DisposeSteps();
            var wrongMetadata = config with { Pipeline = config.Pipeline with
            {
                Steps = [config.Pipeline.Steps[0], new("ImageQuality", "cloud", 20, DependsOn: ["$raw"]), config.Pipeline.Steps[2]]
            }};
            StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(wrongMetadata)).Message,
                "required compound inputs", StringComparison.Ordinal);
            var missingPreview = config with { Pipeline = config.Pipeline with
            {
                Steps = [.. config.Pipeline.Steps.Select(step => step.Id == "weather" ? step with { DependsOn = ["cloud"] } : step)]
            }};
            StringAssert.Contains(Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(missingPreview)).Message,
                "required compound inputs", StringComparison.Ordinal);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            async Task<ProcessingGraphExecutionDetail> Capture(int sequence)
            {
                // Repeat the same retained bytes; only capture identity/time varies.
                var submission = CreateSubmission(0, CameraPixelFormat.Mono16,
                    new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero).AddSeconds(sequence * 2));
                var receipt = await ingress.AcceptAsync(config, submission, CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, config).ConfigureAwait(false);
                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                return (await operations.ReadExecutionDetailAsync(id!.Value, CancellationToken.None).ConfigureAwait(false))!;
            }
            var original = await Capture(0).ConfigureAwait(false);
            Assert.AreEqual("weather-cloud-overlay-v1", original.Nodes.Single(static node => node.NodeId == "weather").Outputs.Single().Variant);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "weather", "2");
            model.Nodes[2].OptionsForm!.Set("outputVariant", "weather-custom");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.AreEqual(90, pipeline.Steps[2].Order);
            var draft = await operations.CreateRevisionAsync("weather", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await Capture(1).ConfigureAwait(false)).Execution.GraphRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await Capture(2).ConfigureAwait(false);
            Assert.AreEqual("weather-custom", changed.Nodes.Single(static node => node.NodeId == "weather").Outputs.Single().Variant);
            Assert.AreEqual(draft.RevisionId, changed.Execution.GraphRevisionId);
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(config.Pipeline.Steps[2].Options!.Value.GetRawText(), retained!.Steps[2].Options!.Value.GetRawText());
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await Capture(3).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, restored.Execution.GraphRevisionId);
            Assert.AreEqual("weather-cloud-overlay-v1", restored.Nodes.Single(static node => node.NodeId == "weather").Outputs.Single().Variant);
            Console.WriteLine($"Original={original.Execution.GraphRevisionId}; edited={changed.Execution.GraphRevisionId}; rollback={restored.Execution.GraphRevisionId}; retained options SHA256={Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(retained.Steps[2].Options!.Value.GetRawText())))}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
