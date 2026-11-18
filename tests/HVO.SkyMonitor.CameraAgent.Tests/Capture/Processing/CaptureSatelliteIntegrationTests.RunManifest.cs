using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Processing.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class CaptureSatelliteIntegrationTests
{
    [TestMethod]
    [DataRow(null, 3, 2)]
    [DataRow("time", 2, 1)]
    [DataRow("snapshot", 3, 1)]
    public async Task RunManifestKeepsActualConditionalOutputsAvailableAndRefusesRealDeletion(string? unavailable, int trackCount, int layerCount)
    {
        var root = CreateRoot();
        try
        {
            var fixture = new CaptureSatelliteFixture();
            using var provider = CreateProvider(root, fixture, new Clock(fixture.Descriptor.Timing.DurableIngressUtc), unavailable);
            var source = Configuration(root, fixture);
            var config = source with
            {
                Pipeline = new(source.Pipeline.Steps.Where(step => step.Type != "SatelliteFixtureManifest")
                    .Select(step => step.Type == "Storage" ? step with { DependsOn = ["$raw", "preview", "satellite", "satellite-layer"] } : step).ToArray(),
                    CapturePipelineSchemaVersions.ExplicitV2, CapturePipelineDependencyPolicy.RejectEnabledDependent)
            };
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            await provider.GetRequiredService<IRawCaptureIngress>().InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var receipt = await CaptureAsync(provider, config, fixture).ConfigureAwait(false);
            var completed = await ProcessAsync(provider, config).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, completed.Outcome, completed.Reason);
            var id = (await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false))!.Value;
            var detail = await operations.ReadRunManifestAsync(id, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(detail);
            var tracks = detail.Nodes.Single(node => node.NodeId == "satellite");
            var layer = detail.Nodes.Single(node => node.NodeId == "satellite-layer");
            Assert.HasCount(trackCount, tracks.Outputs);
            Assert.HasCount(layerCount, layer.Outputs);
            Assert.AreEqual(trackCount, tracks.CommittedOutputCount);
            Assert.AreEqual(layerCount, layer.CommittedOutputCount);
            Assert.IsTrue(detail.Nodes.All(node => node.ArtifactBindingsRetained));
            await RunManifestTestAssertions.ResponseAsync(operations, id).ConfigureAwait(false);

            // Removing the last real companion must still refuse the run, even when the
            // remaining cardinality resembles a legitimate conditional output above.
            var store = provider.GetRequiredService<SqliteCaptureProcessingStore>();
            var target = await store.ReadOutputByArtifactIdAsync(layer.Outputs[^1].ArtifactId, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(target);
            var operation = new ProcessingLifecycleOperation($"delete:{Guid.NewGuid():N}", "delete", target.OutputIdentitySha256,
                target.PayloadRelativePath, target.SidecarRelativePath, $"processing-deletion-tombstones/{Guid.NewGuid():N}",
                "retention-expired", new FileInfo(Path.Combine(root, target.PayloadRelativePath)).Length + new FileInfo(Path.Combine(root, target.SidecarRelativePath)).Length);
            await store.PlanLifecycleOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
            await new DerivedProductReconciler(root, store).ResumeOperationAsync(operation, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNull(await store.ReadOutputByArtifactIdAsync(target.Artifact.ArtifactId, CancellationToken.None).ConfigureAwait(false));
            await RunManifestTestAssertions.ResponseAsync(operations, id, CameraAgentRunManifest.ArtifactEvidenceUnavailableReason).ConfigureAwait(false);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
