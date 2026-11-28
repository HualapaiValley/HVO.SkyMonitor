using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task MetadataRetryReusesVerifiedPayloadsBeforeNodeCommit()
    {
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var objects = SiloInfrastructureFixture.ProductStore(root).GetRequiredService<SiloTestStore>();
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            var persistence = CreatePersistence(fixture.Options, store, telemetry);
            var step = new MetadataProducingStep();
            var graph = new CaptureProcessingGraph([CreateMetadataNode(step)]);
            var captureId = fixture.Manifest.Descriptor.Capture.CaptureId;

            objects.FailBeforeLanding = key => key.StartsWith("m/", StringComparison.Ordinal);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var blocked = await FrameProcessingWorker.ProcessGraphItemAsync(
                    fixture.Item, graph, persistence, telemetry, attempt, NullLogger.Instance,
                    CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, blocked.Outcome);
                Assert.AreEqual("silo-store-unavailable", blocked.Reason);
                var durable = await store.ReadNodeAsync(captureId, "metadata", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(durable is null || durable.Outputs.Count == 0, "An unsettled Silo publication cannot commit an output.");
            }
            Assert.IsNotNull(step.Product);
            var key = SiloObjectNamespace.PayloadKey(SiloRetentionClass.Metadata, SiloHash.Sha256(step.Product.Payload.Span));
            var version = Assert.ContainsSingle(objects.VersionsOf(key));
            objects.FailBeforeLanding = null;
            var settled = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, graph, persistence, telemetry, 3, NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, settled.Outcome, settled.Reason);
            var committed = await store.ReadNodeAsync(captureId, "metadata", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(committed);
            Assert.AreEqual(step.Product.OutputIdentitySha256, Assert.ContainsSingle(committed.Outputs).OutputIdentitySha256);
            Assert.AreEqual(version, Assert.ContainsSingle(objects.VersionsOf(key)));
            CollectionAssert.AreEqual(step.Product.Payload.ToArray(), objects.Read(key, version));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    internal static string RequireKernelRetryControl()
    {
        var control = Environment.GetEnvironmentVariable("HVO_ISSUE587_KERNEL_RETRY_CONTROL");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(control))
            Assert.Inconclusive("Requires the scoped strace retry-fault controller.");
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "hvo-issue587-retry-"), control);
        return control;
    }

    internal static void ArmKernelRetryFault(string control, string directory)
    {
        File.WriteAllText(control + ".ready", System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId, directory }));
        WaitForKernelController(control + ".attached");
    }

    internal static void WaitForKernelController(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.IsTrue(File.Exists(path), "The external scoped fault controller did not reach " + Path.GetExtension(path));
    }
}
