using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class HardLinkPublisherTests
{
    private string _temp = null!;
    private PhysicalRoot _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _temp = Path.Combine(Path.GetTempPath(), "hvo-hardlink-" + Guid.NewGuid().ToString("N"));
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
    public async Task TryPublish_CreatesAnotherEntryForTheSameImmutableBytes()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The qualified hard-link implementation is Linux-only.");
        }
        var source = Path.Combine(_temp, "source.data");
        await File.WriteAllTextAsync(source, "immutable");
        DurableSync.File(source);

        using var publication = await HardLinkPublisher.TryPublishAsync(
            _root, "source.data", Path.Combine("dest", "copy.data"), CancellationToken.None);
        if (publication is null)
        {
            Assert.Inconclusive("The Linux test filesystem or policy does not permit hard links.");
        }
        Assert.AreEqual(9, publication.Length);
        publication.VerifyCurrent();
        var destination = Path.Combine(_temp, "dest", "copy.data");
        await File.WriteAllTextAsync(source, "same-inode");
        Assert.AreEqual("same-inode", await File.ReadAllTextAsync(destination));
        File.Delete(source);
        Assert.AreEqual("same-inode", await File.ReadAllTextAsync(destination), "unlinking one name leaves the inode reachable through the other");
    }

    [TestMethod]
    public async Task TryPublish_RefusesContainmentAndDoesNotTouchTheOutsideTarget()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The qualified hard-link implementation is Linux-only.");
        }
        var outside = Path.Combine(Path.GetTempPath(), "hvo-hardlink-outside-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(outside, "outside");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_temp, "source.data"), outside);
            await Assert.ThrowsExactlyAsync<FileSystemFaultException>(() => HardLinkPublisher.TryPublishAsync(
                _root, "source.data", "copy.data", CancellationToken.None));
            Assert.AreEqual("outside", await File.ReadAllTextAsync(outside));
            Assert.IsFalse(File.Exists(Path.Combine(_temp, "copy.data")));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [TestMethod]
    public async Task TryPublish_ReportsAlreadyExistsAndPreservesTheExistingDestination()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The qualified hard-link implementation is Linux-only.");
        }
        await File.WriteAllTextAsync(Path.Combine(_temp, "source.data"), "source");
        await File.WriteAllTextAsync(Path.Combine(_temp, "copy.data"), "existing");

        var fault = await Assert.ThrowsExactlyAsync<FileSystemFaultException>(() =>
            HardLinkPublisher.TryPublishAsync(
                _root, "source.data", "copy.data", CancellationToken.None));

        Assert.AreEqual(FileSystemFaultKind.AlreadyExists, fault.Kind);
        Assert.AreEqual("hard-link", fault.Operation);
        Assert.AreEqual("existing", await File.ReadAllTextAsync(Path.Combine(_temp, "copy.data")));
    }

    [TestMethod]
    public async Task Publication_DetectsRemovalAndDisposedUseAndDisposesIdempotently()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("The qualified hard-link implementation is Linux-only.");
        }
        await File.WriteAllTextAsync(Path.Combine(_temp, "source.data"), "source");
        var publication = await HardLinkPublisher.TryPublishAsync(
            _root, "source.data", "copy.data", CancellationToken.None);
        if (publication is null)
        {
            Assert.Inconclusive("The Linux test filesystem or policy does not permit hard links.");
        }

        File.Delete(Path.Combine(_temp, "copy.data"));
        var missing = Assert.ThrowsExactly<FileSystemFaultException>(publication.VerifyCurrent);
        publication.DeleteUncommitted();
        publication.Dispose();
        publication.Dispose();

        Assert.AreEqual(FileSystemFaultKind.NotFound, missing.Kind);
        Assert.ThrowsExactly<ObjectDisposedException>(publication.VerifyCurrent);
        Assert.ThrowsExactly<ObjectDisposedException>(publication.DeleteUncommitted);
    }
}
