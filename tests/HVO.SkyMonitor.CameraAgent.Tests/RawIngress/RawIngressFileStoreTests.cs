using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class RawIngressFileStoreTests
{
    [TestMethod]
    [DataRow("PayloadFlushed", false, 0)]
    [DataRow("PayloadPublished", true, 0)]
    [DataRow("PayloadDirectorySynced", true, 1)]
    public async Task Publication_FaultPreservesVisibilityAndDurabilityCounters(string stopName, bool published, int directorySyncs)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-publish-{Guid.NewGuid():N}");
        var stop = Enum.Parse<RawIngressFaultPoint>(stopName);
        var injected = new IOException("injected durable-boundary failure");
        var phases = new List<RawIngressFaultPoint>();
        var flushes = 0;
        var syncs = 0;
        var store = new RawIngressFileStore(root, new CallbackFaultInjector(point =>
        {
            phases.Add(point);
            if (point == stop) throw injected;
        }), () => flushes++, () => syncs++);
        var paths = store.GetPaths(DateTimeOffset.UnixEpoch, Guid.NewGuid());
        Directory.CreateDirectory(Path.GetDirectoryName(paths.PayloadAbsolutePath)!);
        try
        {
            var actual = await Assert.ThrowsExactlyAsync<IOException>(() =>
                store.PublishPayloadAsync(paths, new byte[] { 1, 2, 3 }, CancellationToken.None));
            Assert.AreSame(injected, actual, "The shared exception wrapper must not change CameraAgent's failure contract.");
            Assert.AreEqual(1, flushes);
            Assert.AreEqual(OperatingSystem.IsLinux() ? directorySyncs : 0, syncs);
            Assert.AreEqual(stop, phases[^1]);
            Assert.AreEqual(published, File.Exists(paths.PayloadAbsolutePath));
            if (published) CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(paths.PayloadAbsolutePath));
            Assert.IsEmpty(Directory.GetFiles(root, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task Publication_CancellationRetainsOriginalTokenAndRemovesOnlyItsTemporary()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-cancel-{Guid.NewGuid():N}");
        using var cancellation = new CancellationTokenSource();
        var injected = new OperationCanceledException(cancellation.Token);
        var store = new RawIngressFileStore(root, new CallbackFaultInjector(point =>
        {
            if (point == RawIngressFaultPoint.PayloadWritten) throw injected;
        }));
        var paths = store.GetPaths(DateTimeOffset.UnixEpoch, Guid.NewGuid());
        var directory = Path.GetDirectoryName(paths.PayloadAbsolutePath)!;
        Directory.CreateDirectory(directory);
        var otherWriter = Path.Combine(directory, "other-writer.tmp");
        await File.WriteAllBytesAsync(otherWriter, new byte[] { 9 });
        try
        {
            var actual = await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
                store.PublishPayloadAsync(paths, new byte[] { 1, 2, 3 }, cancellation.Token));
            Assert.AreSame(injected, actual);
            Assert.AreEqual(cancellation.Token, actual.CancellationToken);
            Assert.IsFalse(File.Exists(paths.PayloadAbsolutePath));
            CollectionAssert.AreEqual(new[] { otherWriter }, Directory.GetFiles(directory, "*.tmp"));
            CollectionAssert.AreEqual(new byte[] { 9 }, await File.ReadAllBytesAsync(otherWriter));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void Confinement_PreservesMissingDescendantsAndRejectsSiblingRoots()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-confinement-{Guid.NewGuid():N}");
        var missingFile = Path.Combine(root, "not-created", "evidence.bin");
        RawIngressFileStore.EnsureNoSymbolicLinks(root, missingFile);
        Assert.IsFalse(Directory.Exists(root), "Path inspection must not create runtime state.");
        Assert.ThrowsExactly<IOException>(() =>
            RawIngressFileStore.EnsureNoSymbolicLinks(root, Path.Combine(root + "-sibling", "evidence.bin")));
    }

    [TestMethod]
    public void Confinement_RejectsSubstitutedDirectoryAndDanglingTargetLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-confinement-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "nested", "evidence.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            RawIngressFileStore.EnsureNoSymbolicLinks(root, target);
            Directory.Delete(Path.GetDirectoryName(target)!);
            Directory.CreateSymbolicLink(Path.GetDirectoryName(target)!, Path.GetTempPath());
            Assert.ThrowsExactly<IOException>(() => RawIngressFileStore.EnsureNoSymbolicLinks(root, target));
            var dangling = Path.Combine(root, "dangling.bin");
            File.CreateSymbolicLink(dangling, Path.Combine(root, "missing.bin"));
            Assert.ThrowsExactly<IOException>(() => RawIngressFileStore.EnsureNoSymbolicLinks(root, dangling));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void DirectoryHierarchy_PreservesTraversalCountAndFileSyncExceptions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-sync-{Guid.NewGuid():N}");
        var nested = Path.Combine(root, "one", "two");
        Directory.CreateDirectory(nested);
        try
        {
            Assert.AreEqual(3, RawIngressFileStore.SyncDirectoryHierarchy(root, nested));
            Assert.ThrowsExactly<FileNotFoundException>(() =>
                RawIngressFileStore.SyncFile(root, Path.Combine(nested, "absent.bin")));
            var file = Path.Combine(nested, "evidence.bin");
            File.WriteAllBytes(file, [1, 2, 3]);
            RawIngressFileStore.SyncFile(root, file);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LinuxDirectorySync_UsesArchitectureAbiFlagAndFlushesDirectory()
    {
        // The table itself is pinned by LinuxOpenFlagsTests; this proves the store delegates to it.
        foreach (var architecture in new[] { Architecture.X64, Architecture.Arm64, Architecture.Ppc64le, Architecture.RiscV64 })
        {
            Assert.AreEqual(LinuxOpenFlags.GetDirectoryFlag(architecture), RawIngressFileStore.GetLinuxDirectoryOnlyFlag(architecture));
            Assert.AreEqual(LinuxOpenFlags.GetNoFollowFlag(architecture), RawIngressFileStore.GetLinuxNoFollowFlag(architecture));
        }
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"hvo-directory-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            RawIngressFileStore.SyncDirectory(directory);
            var file = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(file, "test");
            Assert.ThrowsExactly<IOException>(() => RawIngressFileStore.SyncDirectory(file));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CallbackFaultInjector(Action<RawIngressFaultPoint> callback) : IRawIngressFaultInjector
    {
        public void Inject(RawIngressFaultPoint point) => callback(point);
    }
}
