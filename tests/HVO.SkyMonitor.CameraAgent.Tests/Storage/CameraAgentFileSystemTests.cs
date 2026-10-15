using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentFileSystemTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"hvo-shared-adapter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    [TestMethod]
    public void PreparedPublication_PreservesConflictEvidenceAndOwnerOnlyMode()
    {
        var temporary = Path.Combine(_root, ".settings.tmp");
        var final = Path.Combine(_root, "settings.json");
        File.WriteAllBytes(temporary, [1, 2, 3]);
        File.WriteAllBytes(final, [4, 5, 6]);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var conflict = Assert.ThrowsExactly<IOException>(() =>
            CameraAgentFileSystem.PublishPrepared(_root, temporary, final, overwrite: false));
        Assert.IsFalse(FileSystemFaultCompatibility.IsContainmentFailure(conflict));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(temporary));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(final));

        CameraAgentFileSystem.PublishPrepared(_root, temporary, final, overwrite: true);
        Assert.IsFalse(File.Exists(temporary));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(final));
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(final));
        }
    }

    [TestMethod]
    public void PreparedPublication_RejectsASymlinkEvenWhenItsTargetMatchesTheRequestedBytes()
    {
        var temporary = Path.Combine(_root, "owned.tmp");
        var real = Path.Combine(_root, "real.bin");
        var link = Path.Combine(_root, "link.bin");
        File.WriteAllBytes(temporary, [1, 2, 3]);
        File.WriteAllBytes(real, [1, 2, 3]);
        File.CreateSymbolicLink(link, real);

        var failure = Assert.ThrowsExactly<IOException>(() =>
            CameraAgentFileSystem.PublishPrepared(_root, temporary, link, overwrite: false));
        Assert.IsTrue(FileSystemFaultCompatibility.IsContainmentFailure(failure),
            "An immutable duplicate handler must not consume a containment failure as successful idempotency.");
        Assert.IsNotNull(new FileInfo(link).LinkTarget);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(real));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(temporary));
    }

    [TestMethod]
    public void FileFlush_PublishesManagedBuffersBeforeTheStreamCloses()
    {
        var path = Path.Combine(_root, "buffered.bin");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096);
        stream.Write([1, 2, 3]);
        CameraAgentFileSystem.FlushFile(stream);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void StorageRoot_PreservesTrustedAncestorLinksButRejectsALinkedRoot()
    {
        var target = Path.Combine(_root, "physical", "parent");
        Directory.CreateDirectory(target);
        var alias = Path.Combine(_root, "alias");
        Directory.CreateSymbolicLink(alias, target);
        var root = Path.Combine(alias, "new", "storage");
        CameraAgentFileSystem.EnsureStorageRootDurable(root);
        CameraAgentFileSystem.EnsureStorageRootDurable(root);
        Assert.IsTrue(Directory.Exists(Path.Combine(target, "new", "storage")));
        var linkedRoot = Path.Combine(_root, "linked-root");
        Directory.CreateSymbolicLink(linkedRoot, root);
        var failure = Assert.ThrowsExactly<IOException>(() => CameraAgentFileSystem.EnsureStorageRootDurable(linkedRoot));
        Assert.IsTrue(FileSystemFaultCompatibility.IsContainmentFailure(failure));
        Assert.IsNotNull(new DirectoryInfo(alias).LinkTarget);
    }
}
