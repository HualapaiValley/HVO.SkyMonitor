using System.Security.Cryptography;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Modules.VirtualSky;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests;

public sealed partial class VirtualSkyCloudScenarioTests
{
    [TestMethod]
    public async Task VirtualSkyFrameTraversesConfiguredAnalysisSatelliteAndStorageGraph()
    {
        var root = FileSystemTestPaths.CreatePhysicalTemporaryDirectory("hvo-virtual-node-execution");
        try
        {
            using var provider = ProcessingGraphOperationsTests.CreateRemainingOptionsProvider(root);
            // The virtual clock completes exposure instantly; keep its readout before real durable ingress.
            var now = DateTimeOffset.UtcNow.AddSeconds(-30);
            var config = Config(CameraPixelFormat.Mono16, null) with
            {
                AgentId = "virtual-node-execution",
                Pipeline = ProcessingGraphOperationsTests.RemainingOptionsPipeline(Path.Combine(root, "artifacts"))
            };
            var module = new VirtualSkyCameraModule(new ExposureTestTimeProvider(now, true), new InMemoryCelestialCatalog([]),
                provider.GetRequiredService<IProjectedSceneStore>(), stagingStore: provider.GetRequiredService<IProjectedSceneStagingStore>());
            await module.InitializeAsync(config, CancellationToken.None).ConfigureAwait(false);
            var request = new CaptureRequest(now, TimeSpan.FromSeconds(5), CaptureMode.Still);
            var capture = await module.CaptureAsync(request, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(capture.Frame);
            var checksum = Convert.ToHexStringLower(SHA256.HashData(capture.Frame.PixelData.Span));
            provider.GetRequiredService<ICameraAgentConfigurationAccessor>().SetConfiguration(config);
            var ingress = provider.GetRequiredService<IRawCaptureIngress>();
            await ingress.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var operations = provider.GetRequiredService<ProcessingGraphOperationsCoordinator>();
            var registry = await operations.EnsureConfiguredBasicAsync(config, CancellationToken.None).ConfigureAwait(false);
            var receipt = await ingress.AcceptAsync(config, new(request, capture, now,
                TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(receipt);
            Assert.IsTrue(string.Equals(checksum, receipt.Manifest.Descriptor.Artifact.ChecksumSha256, StringComparison.OrdinalIgnoreCase));
            var store = provider.GetRequiredService<ICaptureLaneStore>();
            await store.InitializeLanesAsync(CancellationToken.None).ConfigureAwait(false);
            var lane = provider.GetRequiredService<CaptureLanePolicy>().Definitions.Single(item => item.Name == "standard");
            var lease = await store.ClaimAsync(lane, "virtual-node-execution", config, CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(lease);
            var outcome = await provider.GetRequiredService<StandardCaptureLaneHandler>().HandleAsync(lease.Context, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, outcome.Outcome, outcome.Reason);
            await store.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            var executionId = await operations.ReadLiveExecutionIdAsync(receipt.Manifest.Descriptor.Capture.CaptureId, CancellationToken.None).ConfigureAwait(false);
            var detail = await operations.ReadExecutionDetailAsync(executionId!.Value, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(registry.ActiveRevisionId, detail!.Execution.GraphRevisionId);
            Assert.AreEqual(ProcessingGraphExecutionStatus.Completed, detail.Execution.Status);
            Assert.HasCount(config.Pipeline.Steps.Count, detail.Nodes);
            foreach (var node in detail.Nodes)
            {
                TestContext.WriteLine($"{node.NodeId}: {node.Status}; {node.Reason}; outputs={node.Outputs.Count}");
                Assert.IsTrue(node.Status is "Completed" or "Skipped", $"{node.NodeId}: {node.Status} {node.Reason}");
            }
            Assert.IsNotEmpty(detail.Nodes.Single(node => node.NodeId == "scene").Outputs);
            Assert.IsNotEmpty(detail.Nodes.Single(node => node.NodeId == "measured").Outputs);
            Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(root, "artifacts"), "*.json", SearchOption.AllDirectories).Any());
            Assert.AreEqual(checksum, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(receipt.StoredFrame.AbsolutePath).ConfigureAwait(false))));
            TestContext.WriteLine($"VirtualSky raw SHA256={checksum}; immutable revision={registry.ActiveRevisionId}");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
