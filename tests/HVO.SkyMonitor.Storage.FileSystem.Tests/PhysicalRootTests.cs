using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class PhysicalRootTests
{
    private string _temp = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-fs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_temp);
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
    public void Open_CanonicalizesAndRequiresAnExistingPhysicalDirectory()
    {
        var root = PhysicalRoot.Open(_temp + Path.DirectorySeparatorChar);
        Assert.AreEqual(Path.TrimEndingDirectorySeparator(Path.GetFullPath(_temp)), root.Path);

        var missing = Assert.ThrowsExactly<FileSystemFaultException>(() => PhysicalRoot.Open(Path.Combine(_temp, "absent")));
        Assert.AreEqual(FileSystemFaultKind.NotFound, missing.Kind);

        var relative = Assert.ThrowsExactly<FileSystemFaultException>(() => PhysicalRoot.Open("relative"));
        Assert.AreEqual(FileSystemFaultKind.Containment, relative.Kind);
    }

    [TestMethod]
    public void Open_RefusesARootThatIsASymbolicLink()
    {
        var real = Path.Combine(_temp, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_temp, "link");
        Directory.CreateSymbolicLink(link, real);

        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => PhysicalRoot.Open(link));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
    }

    [TestMethod]
    public void Resolve_ReturnsCanonicalPathsInsideTheRoot()
    {
        var root = PhysicalRoot.Open(_temp);
        var resolved = root.Resolve(Path.Combine("bucket", "a", "..", "b", "key.bin"));
        Assert.AreEqual(Path.Combine(root.Path, "bucket", "b", "key.bin"), resolved);
        Assert.IsTrue(root.IsInside(resolved));
    }

    [TestMethod]
    [DataRow("../escape")]
    [DataRow("bucket/../../escape")]
    [DataRow("bucket/../../../escape")]
    public void Resolve_RefusesEveryTraversalOutsideTheRoot(string relative)
    {
        var root = PhysicalRoot.Open(_temp);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => root.Resolve(relative.Replace('/', Path.DirectorySeparatorChar)));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
    }

    [TestMethod]
    public void Resolve_RefusesAbsolutePathsAndSiblingPrefixAliases()
    {
        // A sibling whose name starts with the root's name is not inside the root.
        var sibling = _temp + "-alias";
        Directory.CreateDirectory(sibling);
        try
        {
            var root = PhysicalRoot.Open(_temp);
            Assert.IsFalse(root.IsInside(sibling));
            var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => root.Resolve(sibling));
            Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [TestMethod]
    public void Resolve_RefusesTraversalThroughASymbolicLinkDirectory()
    {
        var root = PhysicalRoot.Open(_temp);
        var outside = Path.Combine(Path.GetTempPath(), "hvo-fs-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_temp, "escape"), outside);
            // Lexically inside; physically outside. Must be refused.
            var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => root.Resolve(Path.Combine("escape", "key.bin")));
            Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public void Resolve_RefusesATargetThatIsASymbolicLinkFile()
    {
        var root = PhysicalRoot.Open(_temp);
        var real = Path.Combine(_temp, "real.bin");
        File.WriteAllBytes(real, [1]);
        File.CreateSymbolicLink(Path.Combine(_temp, "link.bin"), real);
        var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => root.Resolve("link.bin"));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
    }

    [TestMethod]
    public void Resolve_AllowsNotYetExistingComponentsSoCallersCanCreateThem()
    {
        var root = PhysicalRoot.Open(_temp);
        var resolved = root.Resolve(Path.Combine("new", "deeper", "key.bin"));
        Assert.IsTrue(resolved.StartsWith(root.Path, StringComparison.Ordinal));
        Assert.IsFalse(File.Exists(resolved));
    }

    [TestMethod]
    public void Verify_DetectsALinkSubstitutedAfterResolution()
    {
        var root = PhysicalRoot.Open(_temp);
        var dir = Path.Combine(_temp, "bucket");
        Directory.CreateDirectory(dir);
        var resolved = root.Resolve(Path.Combine("bucket", "key.bin"));
        root.Verify(resolved, "probe");

        // Race: an actor with write access swaps the directory for a link between resolve and use.
        var outside = Path.Combine(Path.GetTempPath(), "hvo-fs-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.Delete(dir);
            Directory.CreateSymbolicLink(dir, outside);
            var fault = Assert.ThrowsExactly<FileSystemFaultException>(() => root.Verify(resolved, "probe"));
            Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
            Assert.AreEqual("probe", fault.Operation);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
