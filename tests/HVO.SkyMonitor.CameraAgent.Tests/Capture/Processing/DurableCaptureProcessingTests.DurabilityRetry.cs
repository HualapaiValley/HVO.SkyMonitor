using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Integration")]
    public async Task MetadataRetryMustSynchronizeVisibleEvidenceBeforeNodeCommit()
        => await VerifyMetadataDurabilityRetryAsync(kernelFault: false).ConfigureAwait(false);

    [TestMethod]
    [TestCategory("Manual")]
    public async Task MetadataRetryWithRealDirectoryFsyncFailure()
        => await VerifyMetadataDurabilityRetryAsync(kernelFault: true).ConfigureAwait(false);

    private static async Task VerifyMetadataDurabilityRetryAsync(bool kernelFault)
    {
        var control = kernelFault ? RequireKernelRetryControl() : null;
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            using var storage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var persistence = CreatePersistence(fixture.Options, store, storage, telemetry);
            var step = new MetadataProducingStep();
            var graph = new CaptureProcessingGraph([CreateMetadataNode(step)]);
            var failures = 0;
            var fail = true;
            persistence.BeforeMetadataDirectorySyncForTest = directory =>
            {
                if (fail && Directory.GetFiles(directory).Count(path => !path.EndsWith(".tmp", StringComparison.Ordinal)) == 2)
                {
                    failures++;
                    if (control is null)
                        throw new IOException("Injected directory fsync EIO after both immutable names are visible.", 5);
                    if (failures == 1) ArmKernelRetryFault(control, directory);
                }
            };
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var failure = await Assert.ThrowsExactlyAsync<IOException>(async () =>
                    await FrameProcessingWorker.ProcessGraphItemAsync(
                        fixture.Item, graph, persistence, telemetry, attempt, NullLogger.Instance,
                        CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
                Assert.AreEqual(5, failure.HResult & 0xffff,
                    "The original directory fsync EIO must survive the CameraAgent adapter.");
                var durable = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                    "metadata", CancellationToken.None).ConfigureAwait(false);
                Assert.IsTrue(durable is null || durable.Outputs.Count == 0, "No output may be committed after failed sync.");
            }
            Assert.AreEqual(2, failures, "Each attempt must reach the directory durability boundary.");
            var files = Directory.GetFiles(Path.Combine(root, "derived"), "*", SearchOption.AllDirectories);
            Assert.HasCount(2, files);
            var before = files.ToDictionary(path => path, File.ReadAllBytes);
            if (control is not null) WaitForKernelController(control + ".released");
            fail = false;
            var settled = await FrameProcessingWorker.ProcessGraphItemAsync(
                fixture.Item, graph, persistence, telemetry, 3, NullLogger.Instance,
                CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, settled.Outcome, settled.Reason);
            var committed = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                "metadata", CancellationToken.None).ConfigureAwait(false);
            Assert.IsNotNull(committed);
            Assert.HasCount(1, committed.Outputs);
            Assert.AreEqual(step.Product!.OutputIdentitySha256, committed.Outputs[0].OutputIdentitySha256);
            foreach (var pair in before)
                CollectionAssert.AreEqual(pair.Value, await File.ReadAllBytesAsync(pair.Key).ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RequireKernelRetryControl()
    {
        var control = Environment.GetEnvironmentVariable("HVO_ISSUE587_KERNEL_RETRY_CONTROL");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(control))
            Assert.Inconclusive("Requires the scoped strace retry-fault controller.");
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "hvo-issue587-retry-"), control);
        return control;
    }

    private static void ArmKernelRetryFault(string control, string directory)
    {
        File.WriteAllText(control + ".ready", System.Text.Json.JsonSerializer.Serialize(new { pid = Environment.ProcessId, directory }));
        WaitForKernelController(control + ".attached");
    }

    private static void WaitForKernelController(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!File.Exists(path) && DateTime.UtcNow < deadline) Thread.Sleep(20);
        Assert.IsTrue(File.Exists(path), "The external scoped fault controller did not reach " + Path.GetExtension(path));
    }
}
