using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Upload;

namespace HVO.SkyMonitor.CameraAgent.Tests;

[TestClass]
[TestCategory("Integration")]
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
    public async Task Enqueue_WhenExistingManifestConflicts_ThrowsInsteadOfAcknowledgingHandoff()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            var outbox = new FileSystemArtifactOutbox();
            var manifest = CreateManifest();
            await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
            var path = Path.Combine(root, "outbox", string.Concat(manifest.IdempotencyKey, ".json"));
            await File.WriteAllTextAsync(path, "{}", CancellationToken.None).ConfigureAwait(false);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
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
    public async Task Enqueue_WhenExistingManifestIsSymbolicLink_RefusesHandoff()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            var outbox = new FileSystemArtifactOutbox();
            var manifest = CreateManifest();
            await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false);
            var path = Path.Combine(root, "outbox", string.Concat(manifest.IdempotencyKey, ".json"));
            var target = Path.Combine(root, "moved-manifest.json");
            File.Move(path, target);
            File.CreateSymbolicLink(path, target);

            await Assert.ThrowsExactlyAsync<IOException>(async () =>
                await outbox.EnqueueAsync(root, manifest, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
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

    [TestMethod]
    public async Task List_WithExcludedKey_SkipsDeferredManifest()
    {
        var root = Path.Combine(Path.GetTempPath(), "skymonitor-outbox", Guid.NewGuid().ToString("N"));
        try
        {
            var outbox = new FileSystemArtifactOutbox();
            var deferred = CreateManifest();
            var ready = CreateManifest();
            await outbox.EnqueueAsync(root, deferred, CancellationToken.None).ConfigureAwait(false);
            await outbox.EnqueueAsync(root, ready, CancellationToken.None).ConfigureAwait(false);

            var results = outbox.List(root, 1, new HashSet<string>(StringComparer.Ordinal) { deferred.IdempotencyKey });

            Assert.HasCount(1, results);
            Assert.AreEqual(ready.IdempotencyKey, results[0].IdempotencyKey);
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
