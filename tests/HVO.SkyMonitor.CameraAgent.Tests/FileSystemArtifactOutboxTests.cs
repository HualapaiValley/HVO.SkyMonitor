using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
public sealed class FileSystemArtifactOutboxTests
{
    [TestMethod]
    public async Task EnqueueAndAcknowledge_SurvivesNewOutboxInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            var manifest = CreateManifest();
            await new FileSystemArtifactOutbox().EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);

            var pending = new FileSystemArtifactOutbox().List(root, 10);
            Assert.AreEqual(1, pending.Count);
            Assert.AreEqual(manifest.IdempotencyKey, pending[0].IdempotencyKey);

            await new FileSystemArtifactOutbox().AcknowledgeAsync(root, manifest.IdempotencyKey, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(0, new FileSystemArtifactOutbox().List(root, 10).Count);
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
    public async Task Enqueue_WhenManifestAlreadyPending_DoesNotDuplicate()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            var outbox = new FileSystemArtifactOutbox();
            var manifest = CreateManifest();
            await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
            await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);

            Assert.AreEqual(1, outbox.List(root, 10).Count);
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
    public async Task EnumeratePending_ReturnsEveryManifestWithoutListLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            var outbox = new FileSystemArtifactOutbox();
            for (var index = 0; index < 12; index++)
            {
                await outbox.EnqueueAsync(root, CreateManifest(), CancellationToken.None).ConfigureAwait(false);
            }

            Assert.HasCount(12, outbox.EnumeratePending(root, CancellationToken.None).ToArray());
            Assert.HasCount(10, outbox.List(root, 10));
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
    public async Task EnumeratePending_WithCanceledToken_StopsBeforeReadingManifests()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            var outbox = new FileSystemArtifactOutbox();
            await outbox.EnqueueAsync(root, CreateManifest(), CancellationToken.None).ConfigureAwait(false);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync().ConfigureAwait(false);

            Assert.Throws<OperationCanceledException>(() =>
                outbox.EnumeratePending(root, cancellation.Token).ToArray());
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static ArtifactUploadManifest CreateManifest() => new(
        "v1", "agent-a", Guid.NewGuid(), Guid.NewGuid(), FrameArtifactRole.Raw, "application/octet-stream", 4,
        new string('A', 64), DateTimeOffset.UnixEpoch, "raw-v1", "frames/1970/01/01/Raw/frame.bin");
}
