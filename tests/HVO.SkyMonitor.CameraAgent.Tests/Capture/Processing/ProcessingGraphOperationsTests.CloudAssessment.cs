using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class ProcessingGraphOperationsTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CloudAssessmentDraftRequiresExplicitActivationAndRollbackKeepsRetainedReference(bool configuredUnavailableReference)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-cloud-options");
        try
        {
            using var provider = CreateProvider(root);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var baseline = CreateConfiguration();
            var source = configuredUnavailableReference
                ? """{ "gridColumns":2, "gridRows":1, "ClearReferenceManifestPath":"retained/unavailable.manifest.json", "future":[1e+02] }"""
                : """{ "gridColumns":2, "gridRows":1, "future":[1e+02] }""";
            var config = baseline with
            {
                Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 } },
                Pipeline = new CapturePipelineConfig([new("CloudAssessment", "cloud", 17,
                    JsonDocument.Parse(source).RootElement.Clone(), ["$raw"])])
            };
            var factory = provider.GetRequiredService<ICaptureProcessingPipelineFactory>();
            var plan = factory.CreateGraph(config);
            var node = plan.SharedPlan!.Nodes.Single().Definition;
            Assert.AreEqual(CloudAssessmentOptionDescriptors.Descriptor.StepVersion, node.StepVersion);
            Assert.AreSame(CloudAssessmentOptionDescriptors.Descriptor,
                ProcessingNodeOptionsCatalog.BuiltIn.Resolve(node.StepAlias, node.StepVersion).Descriptor);
            var defaults = ProcessingNodeOptionsCodec.Decode(CloudAssessmentOptionDescriptors.Descriptor,
                JsonSerializer.SerializeToElement(new { }));
            var runtime = ProcessingNodeOptionsCodec.Decode(CloudAssessmentOptionDescriptors.Descriptor,
                JsonSerializer.SerializeToElement(new CloudAssessmentProcessingStepOptions()));
            foreach (var option in defaults.Options)
                Assert.AreEqual(option.Value, runtime.Get(option.Descriptor.Key).Value, option.Descriptor.Key);
            plan.DisposeSteps();
            Assert.ThrowsExactly<InvalidOperationException>(() => factory.PreviewPlan(config with
            {
                Rig = baseline.Rig
            }), "The existing Mono16/RGGB16 source requirement still applies.");
            var unchanged = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, "source", "cloud", "2");
            Assert.IsTrue(unchanged.TryBuild(out var unchangedPipeline, out _));
            Assert.IsFalse(unchanged.HasGraphChanges(unchangedPipeline));
            Assert.AreEqual(source, unchangedPipeline.Steps[0].Options!.Value.GetRawText());
            Assert.AreEqual(factory.PreviewPlan(config).EffectiveSha256,
                factory.PreviewPlan(config with { Pipeline = unchangedPipeline }).EffectiveSha256);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var initial = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var retainedSource = (await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false))!;
            var retainedOptions = retainedSource.Steps[0].Options!.Value.GetRawText();
            async Task<ProcessingGraphExecutionDetail> Capture(int sequence)
            {
                var receipt = await ingress.AcceptAsync(config, CreateSubmission(0, CameraPixelFormat.Mono16,
                    new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero).AddSeconds(sequence * 2)), CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(receipt);
                await CompleteLiveAsync(provider, config).ConfigureAwait(false);
                var id = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
                var detail = (await operations.ReadExecutionDetailAsync(id!.Value, CancellationToken.None).ConfigureAwait(false))!;
                var output = detail.Nodes.Single().Outputs.Single();
                var opened = await provider.GetRequiredService<ICameraAgentArtifactService>().OpenContentAsync(output.ArtifactId, CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CameraAgentArtifactReadStatus.Found, opened.Status);
                using var content = opened.Content!;
                using var bytes = new MemoryStream();
                await content.CopyToAsync(bytes).ConfigureAwait(false);
                var payload = bytes.ToArray();
                Assert.AreEqual(content.ByteLength, payload.LongLength);
                Assert.AreEqual(content.ChecksumSha256, PayloadChecksum.ComputeSha256(payload), ignoreCase: true);
                var parsed = CloudAssessmentJson.Parse(payload);
                Assert.IsTrue(parsed.Validation.IsValid, parsed.Validation.ReasonCode);
                Assert.AreEqual(CloudAssessmentStatus.InsufficientEvidence, parsed.Assessment!.Status);
                Assert.IsNull(parsed.Assessment.CoverageMillionths);
                Assert.Contains(CloudAssessmentReasonCodes.MissingClearReference, parsed.Assessment.ReasonCodes);
                return detail;
            }
            var original = await Capture(0).ConfigureAwait(false);
            var model = ProcessingGraphDraftModel.FromPipeline(config.Pipeline, initial.ActiveRevisionId, "cloud", "2");
            model.Nodes[0].OptionsForm!.Set("outputVariant", "cloud-custom");
            model.Nodes[0].OptionsForm!.Set("gridColumns", "1");
            model.Nodes[0].OptionsForm!.Set("gridRows", "2");
            model.Nodes[0].OptionsForm!.Set("transmissionThresholdMillionths", "750000");
            model.Nodes[0].OptionsForm!.Set("minimumReferenceSignal", "1");
            model.Nodes[0].OptionsForm!.Set("minimumSamplesPerTile", "1");
            model.Nodes[0].OptionsForm!.Set("maximumSaturatedFractionMillionths", "200000");
            model.Nodes[0].OptionsForm!.Set("includeMask", "False");
            Assert.IsTrue(model.TryBuild(out var pipeline, out var errors), string.Join(' ', errors));
            Assert.AreEqual(17, pipeline.Steps[0].Order);
            var editedGraph = factory.CreateGraph(config with { Pipeline = pipeline });
            var effective = editedGraph.SharedPlan!.Nodes.Single().Definition.EffectiveOptions;
            foreach (var option in ProcessingNodeOptionsCodec.Decode(CloudAssessmentOptionDescriptors.Descriptor, pipeline.Steps[0].Options!.Value).Options)
                Assert.AreEqual(option.Value, ProcessingNodeOptionsCodec.Decode(CloudAssessmentOptionDescriptors.Descriptor, effective).Get(option.Descriptor.Key).Value);
            editedGraph.DisposeSteps();
            var draft = await operations.CreateRevisionAsync("cloud", "2", pipeline, "create", "test", null, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(ProcessingGraphRevisionLifecycle.Draft, draft.Lifecycle);
            await Assert.ThrowsExactlyAsync<ProcessingGraphStoreConflictException>(async () =>
                await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "early", "test", null, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
            Assert.AreEqual(initial.ActiveRevisionId, (await Capture(1).ConfigureAwait(false)).Execution.GraphRevisionId);
            await operations.ValidateRevisionAsync(draft.RevisionId, "validate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var active = await operations.ActivateRevisionAsync(draft.RevisionId, initial.StateVersion, "activate", "test", null, CancellationToken.None).ConfigureAwait(false);
            var changed = await Capture(2).ConfigureAwait(false);
            Assert.AreEqual(draft.RevisionId, changed.Execution.GraphRevisionId);
            Assert.AreEqual("cloud-custom", changed.Nodes.Single().Outputs.Single().Variant);
            var retained = await operations.ReadRevisionPipelineAsync(initial.ActiveRevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(retainedOptions, retained!.Steps[0].Options!.Value.GetRawText());
            var saved = await operations.ReadRevisionPipelineAsync(draft.RevisionId, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureContractJson.Canonicalize(pipeline.Steps[0].Options!.Value).GetRawText(), saved!.Steps[0].Options!.Value.GetRawText());
            await operations.RollbackRevisionAsync(initial.ActiveRevisionId, active.StateVersion, "rollback", "test", null, CancellationToken.None).ConfigureAwait(false);
            var restored = await Capture(3).ConfigureAwait(false);
            Assert.AreEqual(original.Execution.GraphRevisionId, restored.Execution.GraphRevisionId);
            Assert.AreEqual("cloud-assessment-v1", restored.Nodes.Single().Outputs.Single().Variant);
            Console.WriteLine($"Unavailable reference configured={configuredUnavailableReference}; original={original.Execution.GraphRevisionId}; edited={changed.Execution.GraphRevisionId}; rollback={restored.Execution.GraphRevisionId}");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow("gridColumns", 0)]
    [DataRow("gridColumns", 65)]
    [DataRow("gridRows", 65)]
    [DataRow("transmissionThresholdMillionths", 0)]
    [DataRow("transmissionThresholdMillionths", 1_000_000)]
    [DataRow("minimumSamplesPerTile", 0)]
    [DataRow("maximumSaturatedFractionMillionths", -1)]
    [DataRow("maximumSaturatedFractionMillionths", 1_000_001)]
    public void CloudAssessmentServerRejectsOptionsOutsideExistingBounds(string member, int value)
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-cloud-options-invalid");
        try
        {
            using var provider = CreateProvider(root);
            var baseline = CreateConfiguration();
            var config = baseline with
            {
                Rig = baseline.Rig with { Sensor = baseline.Rig.Sensor with { PixelFormat = CameraPixelFormat.Mono16 } },
                Pipeline = new CapturePipelineConfig([new("CloudAssessment", "cloud", 17,
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
