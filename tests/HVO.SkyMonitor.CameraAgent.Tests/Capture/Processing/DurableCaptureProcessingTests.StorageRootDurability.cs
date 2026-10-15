using HVO.SkyMonitor.CameraAgent.Common.Background;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Frames;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;

public sealed partial class DurableCaptureProcessingTests
{
    [TestMethod]
    [TestCategory("Manual")]
    public async Task AlternateMetadataStorageRootRequiresParentBarrier()
        => await VerifyAlternateStorageRootBarrierAsync(pixels: false).ConfigureAwait(false);

    [TestMethod]
    [TestCategory("Manual")]
    public async Task AlternateFrameStorageRootRequiresParentBarrier()
        => await VerifyAlternateStorageRootBarrierAsync(pixels: true).ConfigureAwait(false);

    private static async Task VerifyAlternateStorageRootBarrierAsync(bool pixels)
    {
        var control = RequireKernelRetryControl();
        var root = CreateTestRoot();
        try
        {
            var fixture = await CreateFixtureAsync(root).ConfigureAwait(false);
            var parent = Path.Combine(root, "alternate-parent");
            Directory.CreateDirectory(parent);
            var destination = Path.Combine(parent, "new-ancestor", "storage");
            using var frameStorage = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            using var outbox = new SqliteArtifactOutbox();
            using var telemetry = new CaptureProcessingTelemetry();
            using var store = new SqliteCaptureProcessingStore(fixture.Options);
            var persistence = CreatePersistence(fixture.Options, store, frameStorage, telemetry);
            var storageStep = new FileStorageCaptureProcessingStep(
                new CaptureProcessingStepMetadata("storage", "Storage", 90),
                new FileStorageCaptureProcessingStepOptions { StorageRoot = destination, RetentionDays = 1, QueueForUpload = false },
                new LatestFrameAccessor(), frameStorage, outbox,
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root,
                    CentralIntegration = new CentralIntegrationOptions { Mode = CentralIntegrationMode.Disabled } }),
                NullLogger<FileStorageCaptureProcessingStep>.Instance, persistence);
            var annotation = new PackedAnnotationProducingStep();
            var producer = pixels
                ? new CaptureProcessingGraphNode("annotation", annotation, [], true, annotation.RecipeName,
                    annotation.OutputRole, annotation.OutputVariant, new string('A', 64))
                : CreateMetadataNode(new MetadataProducingStep());
            var graph = new CaptureProcessingGraph([producer,
                new CaptureProcessingGraphNode("storage", storageStep, [producer.Id], true, null, null, null, new string('S', 64))]);
            ArmKernelRetryFault(control, parent);
            // Prove the scoped real fault is armed before testing whether the production path reaches it.
            var probe = Assert.ThrowsExactly<IOException>(() => RawIngressFileStore.SyncDirectory(parent));
            Assert.AreEqual(5, probe.HResult & 0xffff);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var failed = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence,
                    telemetry, attempt, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
                if (failed.Outcome == CaptureLaneHandlerOutcome.Completed)
                {
                    Assert.IsTrue(Directory.Exists(destination), "The witness must reach the alternate storage path.");
                    Console.WriteLine("Premature storage acknowledgement: " + string.Join(";", Issue587RetryCostEvidence.Hashes(destination)));
                }
                Assert.AreEqual(CaptureLaneHandlerOutcome.RetryableFailure, failed.Outcome,
                    "Storage must not acknowledge a root whose parent namespace barrier failed.");
                var node = await store.ReadNodeAsync(fixture.Manifest.Descriptor.Capture.CaptureId,
                    "storage", CancellationToken.None).ConfigureAwait(false);
                Assert.IsNotNull(node);
                Assert.AreEqual(DurableProcessingNodeStatus.RetryableFailure, node.Status);
            }
            WaitForKernelController(control + ".released");
            var settled = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence,
                telemetry, 3, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, settled.Outcome, settled.Reason);
            Assert.IsNotEmpty(Directory.GetFiles(destination, "*", SearchOption.AllDirectories));
            var hashes = Issue587RetryCostEvidence.Hashes(destination);
            var replay = await FrameProcessingWorker.ProcessGraphItemAsync(fixture.Item, graph, persistence,
                telemetry, 4, NullLogger.Instance, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(CaptureLaneHandlerOutcome.Completed, replay.Outcome, replay.Reason);
            CollectionAssert.AreEqual(hashes, Issue587RetryCostEvidence.Hashes(destination));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
