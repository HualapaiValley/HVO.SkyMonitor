using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
public sealed class FileSystemFrameStorageServiceTests
{
    [TestMethod]
    public async Task SaveAsync_WritesPayloadAndMetadata()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"camera-agent-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var storageOptions = JsonSerializer.SerializeToElement(new
        {
            storageRoot = tempRoot,
            retentionDays = 7,
            updateLatestFrame = true
        });
        var processingSteps = new[]
        {
            new CaptureProcessingStepConfig(
                typeof(NoOpFileStorageProcessingStep).FullName ?? nameof(NoOpFileStorageProcessingStep),
                "LocalStorage",
                100,
                storageOptions)
        };
        var rig = TestCameraModuleConfigFactory.CreateRig(widthPixels: 32, heightPixels: 16);
        var config = TestCameraModuleConfigFactory.Create(rig: rig, processingSteps: processingSteps);

        var frame = new CameraFrame(
            DateTimeOffset.UtcNow,
            32,
            16,
            CameraPixelFormat.Mono8,
            new byte[32 * 16],
            new FrameMetadata(TimeSpan.FromMilliseconds(10), 1, 20));

        var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
        try
        {
            var stored = await service.SaveAsync(config, frame, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(File.Exists(stored.AbsolutePath));
            var bytes = await File.ReadAllBytesAsync(stored.AbsolutePath).ConfigureAwait(false);
            Assert.AreEqual(32 * 16, bytes.Length);
            var metadataPath = Path.ChangeExtension(stored.AbsolutePath, ".json");
            Assert.IsTrue(File.Exists(metadataPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

}
