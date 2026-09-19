using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class DurableSyncTests
{
    private string _temp = null!;
    private PhysicalRoot _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
        _root = PhysicalRoot.Open(_temp);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    [TestMethod]
    public void SupportsDirectorySync_IsTrueExactlyOnLinux()
    {
        Assert.AreEqual(OperatingSystem.IsLinux(), DurableSync.SupportsDirectorySync);
    }

    [TestMethod]
    public void File_FlushesAnExistingFileByPathAndHandle()
    {
        var path = Path.Combine(_temp, "data.bin");
        File.WriteAllBytes(path, new byte[64 * 1024]);
        DurableSync.File(path);
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite);
        DurableSync.File(handle, path);
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void File_ReportsPermissionDeniedForAnUnreadableFile()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
        {
            Assert.Inconclusive("Needs a non-root Linux user so that mode 000 is enforced.");
        }
        var path = Path.Combine(_temp, "sealed.bin");
        File.WriteAllBytes(path, [1]);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.File(path));
            Assert.AreEqual(FileSystemFaultKind.PermissionDenied, fault.Kind);
            Assert.IsInstanceOfType<System.ComponentModel.Win32Exception>(fault.InnerException, "the errno is preserved as the inner fact");
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [TestMethod]
    public void File_ReportsNotFoundForAMissingPath()
    {
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.File(Path.Combine(_temp, "absent.bin")));
        Assert.AreEqual(FileSystemFaultKind.NotFound, fault.Kind);
        Assert.AreEqual("sync-file", fault.Operation);
    }

    [TestMethod]
    public void File_RefusesToFollowASymbolicLinkOnLinux()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("O_NOFOLLOW is the Linux mechanism; elsewhere containment is the only guard.");
        }
        var real = Path.Combine(_temp, "real.bin");
        File.WriteAllBytes(real, [1]);
        var link = Path.Combine(_temp, "link.bin");
        File.CreateSymbolicLink(link, real);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.File(link));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
    }

    [TestMethod]
    public void Directory_FlushesARealDirectoryAndRefusesALinkedOne()
    {
        var dir = Path.Combine(_temp, "d");
        Directory.CreateDirectory(dir);
        DurableSync.Directory(dir); // no-op off Linux, real fsync on it; must not throw either way

        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        var link = Path.Combine(_temp, "dlink");
        Directory.CreateSymbolicLink(link, dir);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.Directory(link));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);

        var notADirectory = Path.Combine(_temp, "file.bin");
        File.WriteAllBytes(notADirectory, [1]);
        var notDir = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.Directory(notADirectory));
        Assert.AreEqual(FileSystemFaultKind.Containment, notDir.Kind);
    }

    [TestMethod]
    public void DirectoryChain_FlushesEveryLevelUpToTheRootAndStopsThere()
    {
        var deep = Path.Combine(_temp, "a", "b", "c");
        Directory.CreateDirectory(deep);
        var count = DurableSync.DirectoryChain(_root, deep);
        Assert.AreEqual(OperatingSystem.IsLinux() ? 4 : 0, count, "c, b, a, root");
    }

    [TestMethod]
    public void DirectoryChain_RefusesAPathOutsideTheRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Directory sync is a no-op off Linux, so the chain walk does not run.");
        }
        var outside = Path.Combine(Path.GetTempPath(), "hvo-fs-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => DurableSync.DirectoryChain(_root, outside));
            Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
