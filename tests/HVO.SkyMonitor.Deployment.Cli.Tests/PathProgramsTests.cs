using System.Security.Cryptography;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class PathProgramsTests
{
    [TestMethod]
    [DataRow(true, true, "home")]
    [DataRow(false, true, "shared")]
    [DataRow(true, false, "home")]
    public void FolderOrder_PrefersTheUserFolderThenUnprivilegedSharedFolder(bool homeWritable, bool sharedWritable, string expected)
    {
        var locations = new PathPrograms.Locations("home", "shared", "machine");
        Assert.AreEqual(expected, PathPrograms.SelectClientFolder(locations,
            folder => folder == "home" ? homeWritable : sharedWritable));
    }

    [TestMethod]
    public void FolderOrder_RefusesWhenNeitherFolderIsWritable()
        => Assert.ThrowsExactly<InstallerException>(() => PathPrograms.SelectClientFolder(
            new("home", "shared", "machine"), _ => false));

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Publish_UpgradeIdempotenceAndRollback_PreserveTheReplacedBytesAndMode()
    {
        using var fixture = new ProgramFixture();
        var first = await fixture.SourceAsync("first");
        var next = await fixture.SourceAsync("next");
        await PathPrograms.PublishAsync(first.Path, fixture.Target, first.Sha256, CancellationToken.None);
        await PathPrograms.PublishAsync(next.Path, fixture.Target, next.Sha256, CancellationToken.None);
        await PathPrograms.PublishAsync(next.Path, fixture.Target, next.Sha256, CancellationToken.None);
        Assert.AreEqual("first", await File.ReadAllTextAsync(fixture.Target + ".previous"));
        File.SetUnixFileMode(fixture.Target, UnixFileMode.UserRead);
        await PathPrograms.PublishAsync(next.Path, fixture.Target, next.Sha256, CancellationToken.None);
        Assert.AreEqual((UnixFileMode)0x1ed, File.GetUnixFileMode(fixture.Target));
        await PathPrograms.PublishAsync(first.Path, fixture.Target, first.Sha256, CancellationToken.None);
        Assert.AreEqual("first", await File.ReadAllTextAsync(fixture.Target));
        Assert.AreEqual("next", await File.ReadAllTextAsync(fixture.Target + ".previous"));
        Assert.AreEqual(0, Directory.GetFiles(fixture.Root, "*.new").Length);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Publish_MismatchedSource_PreservesBothExistingCopies()
    {
        using var fixture = new ProgramFixture();
        var source = await fixture.SourceAsync("new release");
        await File.WriteAllTextAsync(fixture.Target, "installed");
        await File.WriteAllTextAsync(fixture.Target + ".previous", "previous");
        await File.WriteAllTextAsync(source.Path, "tampered");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => PathPrograms.PublishAsync(
            source.Path, fixture.Target, source.Sha256, CancellationToken.None));
        Assert.AreEqual("installed", await File.ReadAllTextAsync(fixture.Target));
        Assert.AreEqual("previous", await File.ReadAllTextAsync(fixture.Target + ".previous"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Publish_RefusesLinkedSourceOrTarget(bool linkedSource)
    {
        using var fixture = new ProgramFixture();
        var source = await fixture.SourceAsync("verified");
        var linked = Path.Combine(fixture.Root, "link");
        File.CreateSymbolicLink(linked, source.Path);
        await Assert.ThrowsExactlyAsync<InstallerException>(() => PathPrograms.PublishAsync(
            linkedSource ? linked : source.Path, linkedSource ? fixture.Target : linked, source.Sha256, CancellationToken.None));
        Assert.AreEqual("verified", await File.ReadAllTextAsync(source.Path));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Removal_RefusesChangedBytesAndCanResumeAfterTheyAreMovedAside()
    {
        using var fixture = new ProgramFixture();
        var source = await fixture.SourceAsync("owned");
        await File.WriteAllTextAsync(fixture.Target, "changed outside installer");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => PathPrograms.RemoveMatchingAsync(
            fixture.Target, source.Sha256, CancellationToken.None));
        Assert.IsTrue(File.Exists(fixture.Target));
        File.Move(fixture.Target, fixture.Target + ".aside");
        await PathPrograms.RemoveMatchingAsync(fixture.Target, source.Sha256, CancellationToken.None);
        await PathPrograms.PublishAsync(source.Path, fixture.Target, source.Sha256, CancellationToken.None);
        await PathPrograms.RemoveMatchingAsync(fixture.Target, source.Sha256, CancellationToken.None);
        Assert.IsFalse(File.Exists(fixture.Target));
        Assert.IsTrue(File.Exists(fixture.Target + ".aside"));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Hints_PreferAnExecutableDurableInstallerAndFallBackToTheRunningFile()
    {
        using var fixture = new ProgramFixture();
        var durable = fixture.Target;
        var running = Path.Combine(fixture.Root, "hvo-sky-install-temporary");
        Assert.AreEqual(running, InstallerProgram.DurableOf(running, durable));
        await File.WriteAllTextAsync(durable, "durable installer");
        File.SetUnixFileMode(durable, UnixFileMode.UserRead);
        Assert.AreEqual(running, InstallerProgram.DurableOf(running, durable));
        File.SetUnixFileMode(durable, (UnixFileMode)0x1ed);
        Assert.AreEqual(durable, InstallerProgram.DurableOf(running, durable));
    }

    [TestMethod]
    [DataRow("install", "none", true)]
    [DataRow("remove", "none", true)]
    [DataRow("delete", "none", false)]
    [DataRow("install", "../outside", false)]
    public void PrivilegedHelper_OnlyAcceptsTheFixedOperationAndChecksums(string operation, string previous, bool valid)
    {
        var args = new[] { PrivilegedPathInstaller.CommandName, "/var/lib/hvo-sky", Guid.NewGuid().ToString("D"),
            operation, new string('a', 64), previous };
        Assert.AreEqual(valid, PrivilegedPathInstaller.ValidArguments(args));
        Assert.IsFalse(PrivilegedPathInstaller.ValidArguments([.. args, "/arbitrary/target"]));
        args[2] = Guid.Empty.ToString("D");
        Assert.IsFalse(PrivilegedPathInstaller.ValidArguments(args));
    }

    private sealed class ProgramFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"hvo-path-file-{Guid.NewGuid():N}");
        internal string Target => Path.Combine(Root, "hvo-sky-install");

        internal ProgramFixture() => Directory.CreateDirectory(Root);

        internal async Task<(string Path, string Sha256)> SourceAsync(string content)
        {
            var path = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            await File.WriteAllTextAsync(path, content);
            return (path, Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }

        public void Dispose()
        {
            SafeFileSystem.MakeTreeOwnerWritable(Root);
            Directory.Delete(Root, recursive: true);
        }
    }
}
