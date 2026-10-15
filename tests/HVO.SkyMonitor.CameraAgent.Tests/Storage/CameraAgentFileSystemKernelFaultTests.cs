using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Tests.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
[TestCategory("Manual")]
public sealed class CameraAgentFileSystemKernelFaultTests
{
    [TestMethod]
    public void BufferedFileFlush_ReportsRealNoSpaceThroughCameraAgentAdapter()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Requires Linux /dev/full.");
        var stream = new FileStream("/dev/full", FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 4096);
        try
        {
            stream.WriteByte(42);
            var failure = Assert.ThrowsExactly<IOException>(() => CameraAgentFileSystem.FlushFile(stream));
            Assert.AreEqual(28, failure.HResult & 0xffff, "The original ENOSPC must survive the shared adapter.");
        }
        finally
        {
            // A failed flush can leave the managed buffer pending; closing retries that same failed write.
            try { stream.Dispose(); }
            catch (IOException exception) when ((exception.HResult & 0xffff) == 28) { }
        }
    }

    [TestMethod]
    public async Task RawPublication_DirectoryFsyncFailurePreservesPublishedEvidenceWithoutSuccess()
    {
        var control = DurableCaptureProcessingTests.RequireKernelRetryControl();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-issue587-fsync-{Guid.NewGuid():N}");
        var flushes = 0;
        var syncs = 0;
        var injector = new PublicationFaultInjector(control);
        var store = new RawIngressFileStore(root, injector, () => flushes++, () => syncs++);
        var paths = store.GetPaths(DateTimeOffset.UnixEpoch, Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PayloadAbsolutePath)!);
        injector.Directory = Path.GetDirectoryName(paths.PayloadAbsolutePath)!;
        try
        {
            var failure = await Assert.ThrowsExactlyAsync<IOException>(() =>
                store.PublishPayloadAsync(paths, new byte[] { 1, 2, 3 }, CancellationToken.None)).ConfigureAwait(false);
            Assert.AreEqual(5, failure.HResult & 0xffff, "The directory fsync EIO must survive the shared adapter.");
            Assert.AreEqual(1, flushes);
            Assert.AreEqual(0, syncs);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 },
                await File.ReadAllBytesAsync(paths.PayloadAbsolutePath).ConfigureAwait(false));
            Assert.IsFalse(File.Exists(paths.SidecarAbsolutePath));
            Assert.IsEmpty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DirectorySync_RetriesInterruptedSystemCall()
    {
        var root = Environment.GetEnvironmentVariable("HVO_ISSUE587_EINTR_ROOT");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(root))
            Assert.Inconclusive("Requires strace fsync EINTR on the first call, filtered to HVO_ISSUE587_EINTR_ROOT.");
        Assert.StartsWith($"{Path.GetTempPath()}hvo-issue587-eintr-", root);
        Directory.CreateDirectory(root);
        try
        {
            RawIngressFileStore.SyncDirectory(root);
        }
        finally
        {
            Directory.Delete(root);
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task RawPublication_RealPermissionDenialDoesNotPublishOrAcknowledge()
    {
        if (!OperatingSystem.IsLinux()) Assert.Inconclusive("Requires Linux owner permissions and an unprivileged test process.");
        var root = Path.Combine(Path.GetTempPath(), $"hvo-permission-{Guid.NewGuid():N}");
        var store = new RawIngressFileStore(root, new NullRawIngressFaultInjector());
        var paths = store.GetPaths(DateTimeOffset.UnixEpoch, Guid.NewGuid());
        var directory = Path.GetDirectoryName(paths.PayloadAbsolutePath)!;
        Directory.CreateDirectory(directory);
        var originalMode = File.GetUnixFileMode(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var probe = Path.Combine(directory, "permission-probe");
            try
            {
                await File.WriteAllTextAsync(probe, "probe").ConfigureAwait(false);
                Assert.Inconclusive("The test process can bypass directory permissions; use an unprivileged identity.");
            }
            catch (UnauthorizedAccessException) { }
            await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
                store.PublishPayloadAsync(paths, new byte[] { 1, 2, 3 }, CancellationToken.None)).ConfigureAwait(false);
            Assert.IsFalse(File.Exists(paths.PayloadAbsolutePath));
            Assert.IsFalse(File.Exists(paths.SidecarAbsolutePath));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            File.SetUnixFileMode(directory, originalMode);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class PublicationFaultInjector(string control) : IRawIngressFaultInjector
    {
        internal string Directory { get; set; } = string.Empty;
        public void Inject(RawIngressFaultPoint point)
        {
            if (point == RawIngressFaultPoint.PayloadPublished)
                DurableCaptureProcessingTests.ArmKernelRetryFault(control, Directory);
        }
    }
}
