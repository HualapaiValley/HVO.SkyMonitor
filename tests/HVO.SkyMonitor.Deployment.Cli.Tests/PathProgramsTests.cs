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
        await fixture.PublishAsync(first.Path, fixture.Target, first.Sha256, CancellationToken.None);
        await fixture.PublishAsync(next.Path, fixture.Target, next.Sha256, CancellationToken.None);
        await fixture.PublishAsync(next.Path, fixture.Target, next.Sha256, CancellationToken.None);
        Assert.AreEqual("first", await File.ReadAllTextAsync(fixture.Target + ".previous"));
        File.SetUnixFileMode(fixture.Target, UnixFileMode.UserRead);
        await fixture.PublishAsync(next.Path, fixture.Target, next.Sha256, CancellationToken.None);
        Assert.AreEqual((UnixFileMode)0x1ed, File.GetUnixFileMode(fixture.Target));
        await fixture.PublishAsync(first.Path, fixture.Target, first.Sha256, CancellationToken.None);
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
        var previous = await fixture.SourceAsync("previous");
        var installed = await fixture.SourceAsync("installed");
        await fixture.PublishAsync(previous.Path, fixture.Target, previous.Sha256, CancellationToken.None);
        await fixture.PublishAsync(installed.Path, fixture.Target, installed.Sha256, CancellationToken.None);
        await File.WriteAllTextAsync(source.Path, "tampered");
        await Assert.ThrowsExactlyAsync<InstallerException>(() => fixture.PublishAsync(
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
        await Assert.ThrowsExactlyAsync<InstallerException>(() => fixture.PublishAsync(
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
            fixture.Target, source.Sha256, fixture.Root, CancellationToken.None));
        Assert.IsTrue(File.Exists(fixture.Target));
        File.Move(fixture.Target, fixture.Target + ".aside");
        await PathPrograms.RemoveMatchingAsync(fixture.Target, source.Sha256, fixture.Root, CancellationToken.None);
        await fixture.PublishAsync(source.Path, fixture.Target, source.Sha256, CancellationToken.None);
        await PathPrograms.RemoveMatchingAsync(fixture.Target, source.Sha256, fixture.Root, CancellationToken.None);
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

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, 0x1ff)]
    [DataRow(true, 0x1ff)]
    [DataRow(false, 0x1fd)]
    [DataRow(true, 0x1fd)]
    [DataRow(false, 0x1ef)]
    [DataRow(true, 0x1ef)]
    public async Task Correction1_UntrustedDestinationOrAncestor_IsRefusedWithoutChangingFiles(bool ancestor, int mode)
    {
        using var fixture = new ProgramFixture();
        var source = await fixture.SourceAsync("verified release");
        var folder = Path.Combine(fixture.Root, "untrusted");
        var destination = Path.Combine(folder, ancestor ? "bin/hvo-sky" : "hvo-sky");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.SetUnixFileMode(folder, (UnixFileMode)mode);
        await File.WriteAllTextAsync(destination, "keep current");
        await File.WriteAllTextAsync(destination + ".previous", "keep previous");
        var failure = await Assert.ThrowsExactlyAsync<InstallerException>(() =>
            fixture.PublishAsync(source.Path, destination, source.Sha256, CancellationToken.None));
        Assert.IsTrue(failure.IsRefusal);
        StringAssert.Contains(failure.Message, "not writable by other users", StringComparison.Ordinal);
        Assert.AreEqual("keep current", await File.ReadAllTextAsync(destination));
        Assert.AreEqual("keep previous", await File.ReadAllTextAsync(destination + ".previous"));
        Assert.AreEqual(0, Directory.GetFiles(folder, "*.new", SearchOption.AllDirectories).Length);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public async Task Correction1_SubstitutedVerifiedStage_IsRefusedBeforeReplacingTheCommand()
    {
        using var fixture = new ProgramFixture();
        var first = await fixture.SourceAsync("installed release");
        var next = await fixture.SourceAsync("next release");
        await fixture.PublishAsync(first.Path, fixture.Target, first.Sha256, CancellationToken.None);
        var hook = NativeLinux.DirectoryFlushed;
        var substituted = false;
        try
        {
            NativeLinux.DirectoryFlushed = folder =>
            {
                var stage = Directory.GetFiles(fixture.Root, "*.new").SingleOrDefault();
                if (!substituted && folder == fixture.Root && stage is not null)
                {
                    substituted = true;
                    File.Delete(stage);
                    File.WriteAllText(stage, "substituted stage");
                }
            };
            await Assert.ThrowsExactlyAsync<InstallerException>(() =>
                fixture.PublishAsync(next.Path, fixture.Target, next.Sha256, CancellationToken.None));
        }
        finally { NativeLinux.DirectoryFlushed = hook; }
        Assert.IsTrue(substituted);
        Assert.AreEqual("installed release", await File.ReadAllTextAsync(fixture.Target));
        Assert.IsFalse(File.Exists(fixture.Target + ".previous"));
    }

    private sealed class ProgramFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"hvo-path-file-{Guid.NewGuid():N}");
        internal string Target => Path.Combine(Root, "hvo-sky-install");

        private PathProgramRecord? publication;

        internal ProgramFixture() => Directory.CreateDirectory(Root, SafeFileSystem.OwnerDirectoryMode);

        internal async Task PublishAsync(string source, string target, string sha256, CancellationToken cancellationToken)
        {
            await PathPrograms.PublishAsync(source, target, sha256,
                publication is null ? null : publication with { PendingSha256 = sha256 }, Root, cancellationToken);
            publication = new("hvo-sky-install", target, "1.0.0", sha256,
                await PathPrograms.HashAsync(target + ".previous", cancellationToken));
        }

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
