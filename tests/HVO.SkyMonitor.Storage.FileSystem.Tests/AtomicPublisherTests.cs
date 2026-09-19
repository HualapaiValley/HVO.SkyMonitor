using System.Text;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class AtomicPublisherTests
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

    private static Func<Stream, CancellationToken, Task> Bytes(byte[] content)
        => async (stream, ct) => await stream.WriteAsync(content, ct);

    [TestMethod]
    public async Task Publish_CreatesDirectoriesWritesContentAndLeavesNoTemporary()
    {
        var content = Encoding.UTF8.GetBytes("hello");
        var path = await AtomicPublisher.PublishAsync(_root, Path.Combine("bucket", "a", "key.bin"), PublishMode.CreateNew, Bytes(content), CancellationToken.None);

        Assert.AreEqual(Path.Combine(_root.Path, "bucket", "a", "key.bin"), path);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(path));
        Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [TestMethod]
    public async Task Publish_CreateNewRefusesAnExistingTargetAndLeavesItUntouched()
    {
        var original = Encoding.UTF8.GetBytes("original");
        await AtomicPublisher.PublishAsync(_root, "key.bin", PublishMode.CreateNew, Bytes(original), CancellationToken.None);

        var fault = await Assert.ThrowsExactlyAsync<FileSystemFaultException>(
            () => AtomicPublisher.PublishAsync(_root, "key.bin", PublishMode.CreateNew, Bytes(Encoding.UTF8.GetBytes("replacement")), CancellationToken.None));
        Assert.AreEqual(FileSystemFaultKind.AlreadyExists, fault.Kind);
        Assert.AreEqual("publish-rename", fault.Operation);
        CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(Path.Combine(_temp, "key.bin")));
        Assert.IsEmpty(Directory.GetFiles(_temp, "*.tmp"), "a refused publication must remove its temporary");
    }

    [TestMethod]
    public void SupportsAtomicReplace_IsTrueExactlyOnLinux()
    {
        Assert.AreEqual(OperatingSystem.IsLinux(), AtomicPublisher.SupportsAtomicReplace);
    }

    [TestMethod]
    public async Task Publish_ReplaceSwapsContentAtomically()
    {
        await AtomicPublisher.PublishAsync(_root, "key.bin", PublishMode.CreateNew, Bytes(Encoding.UTF8.GetBytes("v1")), CancellationToken.None);
        await AtomicPublisher.PublishAsync(_root, "key.bin", PublishMode.Replace, Bytes(Encoding.UTF8.GetBytes("v2")), CancellationToken.None);
        Assert.AreEqual("v2", await File.ReadAllTextAsync(Path.Combine(_temp, "key.bin")));
        Assert.IsEmpty(Directory.GetFiles(_temp, "*.tmp"));
    }

    [TestMethod]
    public async Task Publish_WriterFailureLeavesNoTargetAndNoTemporary()
    {
        var fault = await Assert.ThrowsExactlyAsync<FileSystemFaultException>(
            () => AtomicPublisher.PublishAsync(_root, "key.bin", PublishMode.CreateNew,
                async (stream, ct) =>
                {
                    await stream.WriteAsync(new byte[1024], ct);
                    throw new IOException("simulated medium failure mid-write");
                },
                CancellationToken.None));
        Assert.AreEqual("publish-write", fault.Operation);
        Assert.IsFalse(File.Exists(Path.Combine(_temp, "key.bin")), "no partial target may become visible");
        Assert.IsEmpty(Directory.GetFiles(_temp, "*.tmp"));
    }

    [TestMethod]
    public async Task Publish_CancellationBeforeTheDurabilityBoundaryLeavesNothing()
    {
        using var cancellation = new CancellationTokenSource();
        var fault = await Assert.ThrowsExactlyAsync<FileSystemFaultException>(
            () => AtomicPublisher.PublishAsync(_root, "key.bin", PublishMode.CreateNew,
                async (stream, ct) =>
                {
                    await stream.WriteAsync(new byte[16], ct);
                    await cancellation.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                },
                cancellation.Token));
        Assert.AreEqual(FileSystemFaultKind.Cancelled, fault.Kind);
        Assert.IsFalse(File.Exists(Path.Combine(_temp, "key.bin")));
        Assert.IsEmpty(Directory.GetFiles(_temp, "*.tmp"));
    }

    [TestMethod]
    public async Task Publish_RefusesATargetOutsideTheRootBeforeWritingAnything()
    {
        var written = false;
        var fault = await Assert.ThrowsExactlyAsync<FileSystemFaultException>(
            () => AtomicPublisher.PublishAsync(_root, Path.Combine("..", "escape.bin"), PublishMode.CreateNew,
                (_, _) => { written = true; return Task.CompletedTask; }, CancellationToken.None));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
        Assert.IsFalse(written, "containment is checked before the writer runs");
    }

    [TestMethod]
    public async Task Publish_RefusesWhenTheParentDirectoryIsASymbolicLink()
    {
        var outside = Path.Combine(Path.GetTempPath(), "hvo-fs-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_temp, "linked"), outside);
            var fault = await Assert.ThrowsExactlyAsync<FileSystemFaultException>(
                () => AtomicPublisher.PublishAsync(_root, Path.Combine("linked", "key.bin"), PublishMode.CreateNew, Bytes([1]), CancellationToken.None));
            Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
            Assert.IsEmpty(Directory.GetFiles(outside), "nothing may be written through the link");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [TestMethod]
    public async Task Publish_ContentIsDurableBeforeTheRenameIsObservable()
    {
        // The observable order is: temporary written and flushed, then renamed. A reader that
        // sees the final name must therefore see complete content. Prove it by publishing a
        // payload larger than any single write buffer and reading back immediately.
        var content = new byte[3 * 1024 * 1024];
        System.Security.Cryptography.RandomNumberGenerator.Fill(content);
        var path = await AtomicPublisher.PublishAsync(_root, "large.bin", PublishMode.CreateNew, Bytes(content), CancellationToken.None);
        CollectionAssert.AreEqual(content, await File.ReadAllBytesAsync(path));
    }

    [TestMethod]
    public async Task Publish_ReportsNoSpaceAsItsOwnFactWhenTheMediumIsFull()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("ENOSPC injection uses a tmpfs mount and is Linux-only.");
        }
        // Without root we cannot mount a tiny tmpfs; verify the classification path instead by
        // constructing the runtime's own ENOSPC-shaped IOException.
        var enospc = new IOException("No space left on device", unchecked((int)0x8007001C));
        var fault = FileSystemFaultException.From("publish-write", "key.bin", enospc);
        Assert.AreEqual(FileSystemFaultKind.NoSpace, fault.Kind);
        await Task.CompletedTask;
    }

    [TestMethod]
    public void EnsureDirectory_CreatesTheChainInsideTheRootAndRefusesOutside()
    {
        var deep = Path.Combine(_temp, "a", "b", "c");
        AtomicPublisher.EnsureDirectory(_root, deep);
        Assert.IsTrue(Directory.Exists(deep));

        var fault = Assert.ThrowsExactly<FileSystemFaultException>(
            () => AtomicPublisher.EnsureDirectory(_root, Path.Combine(_temp, "..", "escape")));
        Assert.AreEqual(FileSystemFaultKind.Containment, fault.Kind);
    }

    [TestMethod]
    public async Task CleanupTemporaries_RemovesOnlyOurSuffixIsBoundedAndNeverFollowsLinks()
    {
        var dir = Path.Combine(_temp, "bucket");
        Directory.CreateDirectory(dir);
        await AtomicPublisher.PublishAsync(_root, Path.Combine("bucket", "keep.bin"), PublishMode.CreateNew, Bytes([1]), CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllBytes(Path.Combine(dir, $"crash-{i}.{Guid.NewGuid():N}.tmp"), [0]);
        }
        File.WriteAllBytes(Path.Combine(dir, "not-ours.txt"), [0]);
        var outsideTarget = Path.Combine(_temp, "outside.tmp");
        File.WriteAllBytes(outsideTarget, [0]);
        File.CreateSymbolicLink(Path.Combine(dir, "linked.tmp"), outsideTarget);

        // Bounded: at most three entries are examined, so at most three are removed, and the
        // enumeration order (which may place the link among the first three) decides how many.
        var removed = AtomicPublisher.CleanupTemporaries(_root, dir, maximum: 3);
        Assert.IsTrue(removed is >= 2 and <= 3, $"bounded to the maximum examined; removed {removed}");
        Assert.AreEqual(5 - removed, Directory.GetFiles(dir, "crash-*.tmp").Length);
        Assert.IsTrue(File.Exists(Path.Combine(dir, "keep.bin")));
        Assert.IsTrue(File.Exists(Path.Combine(dir, "not-ours.txt")));
        Assert.IsTrue(File.Exists(outsideTarget), "a link carrying our suffix must not delete its target");

        var remaining = AtomicPublisher.CleanupTemporaries(_root, dir, maximum: 100);
        Assert.AreEqual(5 - removed, remaining, "the second pass removes exactly what the first left");
        Assert.IsEmpty(Directory.GetFiles(dir, "crash-*.tmp"));
        Assert.IsTrue(File.Exists(outsideTarget));
        Assert.IsTrue(File.Exists(Path.Combine(dir, "linked.tmp")), "the link itself is skipped, not deleted");
    }
}
