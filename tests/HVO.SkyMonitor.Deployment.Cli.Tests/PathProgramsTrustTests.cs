using System.Runtime.InteropServices;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class PathProgramsTrustTests
{
    [TestMethod]
    [DataRow("installer:x:456:", "installer:x:123:456::/home/installer:/bin/sh", true)]
    [DataRow("installer:x:456:installer", "installer:x:123:456::/home/installer:/bin/sh", true)]
    [DataRow("installer:x:456:installer,another", "installer:x:123:456::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:456:", "installer:x:123:456::/home/installer:/bin/sh\nanother:x:124:456::/:/bin/sh", false)]
    [DataRow("shared:x:456:", "installer:x:123:456::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:457:", "installer:x:123:456::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:456:", "installer:x:123:457::/home/installer:/bin/sh", false)]
    [DataRow("", "installer:x:123:456::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:456:", "", false)]
    [DataRow("not-a-group", "installer:x:123:456::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:4294967296:", "installer:x:123:456::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:456:", "installer:x:123:invalid::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:456:\nshared:x:456:", "installer:x:123:456::/home/installer:/bin/sh", false)]
    [DataRow("installer:x:456:", "installer:x:123:456::/home/installer:/bin/sh\nalias:x:123:789::/:/bin/sh", false)]
    [DataRow("installer:x:456:installer,", "installer:x:123:456::/home/installer:/bin/sh", false)]
    public void PrivateGroup_RequiresUnambiguousExclusiveMembership(string groups, string passwd, bool expected)
    {
        var database = new UnixAccountDatabase(() => groups.Split('\n'), () => passwd.Split('\n'), () => true);
        Assert.AreEqual(expected, database.IsPrivateGroup(123, 456, 456));
        Assert.IsFalse(database.IsPrivateGroup(123, 456, 789), "another directory group is never private");
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PrivateGroup_UnreadableDatabase_IsRefused(bool groupFailure)
    {
        var database = new UnixAccountDatabase(
            () => groupFailure ? throw new UnauthorizedAccessException() : ["installer:x:456:"],
            () => groupFailure ? ["installer:x:123:456::/:/bin/sh"] : throw new IOException(), () => true);
        Assert.IsFalse(database.IsPrivateGroup(123, 456, 456));
    }

    [TestMethod]
    [DataRow(123, 456, 0x1fd, true)]
    [DataRow(0, 456, 0x1fd, true)]
    [DataRow(124, 456, 0x1ed, false)]
    [DataRow(123, 457, 0x1fd, false)]
    [DataRow(123, 456, 0x1ff, false)]
    [DataRow(123, 456, 0x1ef, false)]
    [DataRow(0, 0, 0x1ed, true)]
    public void DirectoryTrust_OwnerAndOtherWriteChecksRemainRequired(int owner, int group, int mode, bool expected)
    {
        var database = new UnixAccountDatabase(() => ["installer:x:456:"], () => ["installer:x:123:456::/:/bin/sh"], () => true);
        Assert.AreEqual(expected, PathPrograms.IsTrustedDirectoryIdentity(new((uint)owner, (uint)group, (UnixFileMode)mode), 123, 456, database));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Destination_PrivateGroupWritableAncestor_IsAllowed()
    {
        using var accounts = new PathProgramAccountScope();
        var root = Path.Combine(Path.GetTempPath(), $"hvo-private-group-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.SetUnixFileMode(root, (UnixFileMode)0x1fd);
        try
        {
            PathPrograms.EnsureTrustedDestination(Path.Combine(root, "bin", "hvo-sky"), root);
        }
        finally { Directory.Delete(root); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(0x2)]
    [DataRow(0x3f)]
    public void CreatedDirectories_HaveExplicit0755AndPreserveExistingModes(int mask)
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-program-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root, (UnixFileMode)0x1c0);
        var previous = SetUmask((uint)mask);
        try
        {
            var bin = Path.Combine(root, ".local", "bin");
            PathPrograms.CreateProgramDirectories(bin, PathPrograms.ProgramMode);
            Assert.AreEqual(PathPrograms.ProgramMode, File.GetUnixFileMode(bin));
            Assert.AreEqual(PathPrograms.ProgramMode, File.GetUnixFileMode(Path.GetDirectoryName(bin)!));
            Assert.AreEqual((UnixFileMode)0x1c0, File.GetUnixFileMode(root));
        }
        finally
        {
            _ = SetUmask(previous);
            Directory.Delete(root, recursive: true);
        }
    }

#pragma warning disable SYSLIB1054 // Test-only process umask scope; no production interop surface is needed.
    [DllImport("libc", EntryPoint = "umask")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static extern uint SetUmask(uint mask);
#pragma warning restore SYSLIB1054
}

internal sealed class PathProgramAccountScope : IDisposable
{
    private readonly UnixAccountDatabase previous = PathPrograms.Accounts;

    internal PathProgramAccountScope(bool privateGroup = true)
    {
        var uid = NativeLinux.getuid();
        var gid = NativeLinux.getgid();
        PathPrograms.Accounts = new(() => [$"installer:x:{gid}:{(privateGroup ? "" : "another")}"],
            () => [$"installer:x:{uid}:{gid}::/:/bin/sh"], () => true);
    }

    public void Dispose() => PathPrograms.Accounts = previous;
}
