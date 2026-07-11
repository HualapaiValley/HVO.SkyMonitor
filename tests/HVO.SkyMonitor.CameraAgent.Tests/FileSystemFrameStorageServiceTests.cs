using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class FileSystemFrameStorageServiceTests
{
    [TestMethod]
    public async Task SaveAsync_WritesRawPayloadAndMetadataWithoutTemporaryFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 2, 1, CameraPixelFormat.Mono16, new byte[] { 1, 2, 3, 4 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));

            var stored = await service.SaveAsync(CreateConfig(root), new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            CollectionAssert.AreEqual(frame.PixelData.ToArray(), await File.ReadAllBytesAsync(stored.AbsolutePath).ConfigureAwait(false));
            Assert.IsTrue(File.Exists(Path.ChangeExtension(stored.AbsolutePath, ".json")));
            Assert.AreEqual(FrameArtifactRole.Raw, stored.Role);
            Assert.AreEqual(0, Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Count());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task List_FiltersByRoleAndReturnsPersistedArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            await service.SaveAsync(CreateConfig(root), new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Preview, frame), CancellationToken.None).ConfigureAwait(false);

            var artifacts = service.List(root, DateOnly.FromDateTime(DateTime.UnixEpoch), FrameArtifactRole.Preview, 10);

            Assert.AreEqual(1, artifacts.Count);
            Assert.AreEqual(FrameArtifactRole.Preview, artifacts[0].Role);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static CameraModuleConfig CreateConfig(string root)
    {
        using var optionsDocument = System.Text.Json.JsonDocument.Parse($"{{\"storageRoot\":\"{root.Replace("\\", "\\\\", StringComparison.Ordinal)}\",\"retentionDays\":7}}");
        return new CameraModuleConfig(
            new ObservatoryLocation(0, 0, 0, "UTC"), new CameraModuleDescriptor("VirtualSky"),
            new CameraRigConfig(new SensorProfile("Virtual", 2, 1, 5.86, SensorColorMode.Mono, CameraPixelFormat.Mono16),
                new OpticsProfile("EquidistantFisheye", 3, 180, 0), new RigOrientation(90, 0, 0),
                new PipelineExposureProfile(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 1, 1)),
            [new CaptureProcessingStepConfig("NoOpFileStorageProcessingStep", Options: optionsDocument.RootElement.Clone())]);
    }
}
