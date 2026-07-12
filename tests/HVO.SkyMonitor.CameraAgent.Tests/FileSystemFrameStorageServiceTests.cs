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

            var stored = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

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
            await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Preview, frame), CancellationToken.None).ConfigureAwait(false);

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

    [TestMethod]
    public async Task SaveAsync_WithSameTimestampAndRole_UsesDistinctArtifactPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var first = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var second = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            Assert.AreNotEqual(first.AbsolutePath, second.AbsolutePath);
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
    public async Task SaveAsync_UsesTheRequestedStorageRoot()
    {
        var localRoot = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));

            var local = await service.SaveAsync(localRoot, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var archive = await service.SaveAsync(archiveRoot, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            Assert.IsTrue(local.AbsolutePath.StartsWith(Path.GetFullPath(localRoot), StringComparison.Ordinal));
            Assert.IsTrue(archive.AbsolutePath.StartsWith(Path.GetFullPath(archiveRoot), StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(localRoot))
            {
                Directory.Delete(localRoot, recursive: true);
            }
            if (Directory.Exists(archiveRoot))
            {
                Directory.Delete(archiveRoot, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SaveAsync_NormalizesUnknownTemperatureForJsonMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, double.NaN));

            var stored = await service.SaveAsync(root, new FrameArtifact(Guid.NewGuid(), FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            using var metadata = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(stored.AbsolutePath, ".json")).ConfigureAwait(false));
            Assert.AreEqual(System.Text.Json.JsonValueKind.Null, metadata.RootElement.GetProperty("metadata").GetProperty("temperatureC").ValueKind);
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
    public async Task RemoveAsync_RemovesAcknowledgedPayloadMetadataAndIndexEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var service = new FileSystemFrameStorageService(NullLogger<FileSystemFrameStorageService>.Instance);
            var frame = new CameraFrame(DateTimeOffset.UnixEpoch, 1, 1, CameraPixelFormat.Mono8, new byte[] { 1 },
                new FrameMetadata(TimeSpan.FromSeconds(1), 1, 0));
            var removedArtifactId = Guid.NewGuid();
            var retainedArtifactId = Guid.NewGuid();
            var removed = await service.SaveAsync(root, new FrameArtifact(removedArtifactId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);
            var retained = await service.SaveAsync(root, new FrameArtifact(retainedArtifactId, FrameArtifactRole.Raw, frame), CancellationToken.None).ConfigureAwait(false);

            await service.RemoveAsync(root, removed, removedArtifactId, CancellationToken.None).ConfigureAwait(false);

            Assert.IsFalse(File.Exists(removed.AbsolutePath));
            Assert.IsFalse(File.Exists(Path.ChangeExtension(removed.AbsolutePath, ".json")));
            Assert.IsTrue(File.Exists(retained.AbsolutePath));
            var indexLines = await File.ReadAllLinesAsync(Path.Combine(root, "index", "frames_1970-01-01.jsonl")).ConfigureAwait(false);
            Assert.AreEqual(1, indexLines.Length);
            StringAssert.Contains(indexLines[0], retainedArtifactId.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
