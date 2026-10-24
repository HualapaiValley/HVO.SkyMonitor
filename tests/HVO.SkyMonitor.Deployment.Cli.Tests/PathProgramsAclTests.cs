using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class PathProgramsAclTests
{
    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void Destination_ExtendedAclWriterOnFolderOrAncestor_IsRefused(bool ancestor, bool namedGroup)
    {
        using var accounts = new PathProgramAccountScope();
        var root = CreateRoot();
        try
        {
            var folder = CreatePrivateGroupFolder(root);
            var acl = WriterAcl(namedGroup);
            Assert.AreEqual(0, SetAccessAcl(Encoding.UTF8.GetBytes(folder + "\0"),
                "system.posix_acl_access\0"u8.ToArray(), acl, (nuint)acl.Length, 0),
                $"Could not create the isolated ACL fixture (errno {Marshal.GetLastPInvokeError()}).");
            Assert.AreEqual((UnixFileMode)0x1fd, File.GetUnixFileMode(folder), "the ACL mask still presents mode 0775");
            if (ancestor)
            {
                var bin = Path.Combine(folder, "bin");
                Directory.CreateDirectory(bin, (UnixFileMode)0x1ed);
                File.SetUnixFileMode(bin, (UnixFileMode)0x1ed);
            }
            var target = Path.Combine(folder, ancestor ? "bin/hvo-sky" : "hvo-sky");

            var failure = Assert.ThrowsExactly<InstallerException>(() => PathPrograms.EnsureTrustedDestination(target, root));

            Assert.IsTrue(failure.IsRefusal);
            Assert.IsFalse(File.Exists(target));
            Assert.AreEqual((UnixFileMode)0x1fd, File.GetUnixFileMode(folder), "refusal preserves the existing ACL/mode");
            Assert.AreEqual(ancestor, Directory.Exists(Path.Combine(folder, "bin")), "refusal preserves existing children");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    [DataRow(-1, 61, true)]
    [DataRow(-1, 13, false)]
    [DataRow(-1, 95, false)]
    [DataRow(0, 0, false)]
    [DataRow(4, 0, false)]
    [DataRow(4, 61, false)]
    public void Destination_AclLookup_RequiresConfirmedAbsence(int size, int error, bool trusted)
    {
        using var accounts = new PathProgramAccountScope();
        var root = CreateRoot();
        var previous = NativeLinux.AccessAclSizeReader;
        try
        {
            var target = Path.Combine(CreatePrivateGroupFolder(root), "hvo-sky");
            NativeLinux.AccessAclSizeReader = _ => ((nint)size, error);
            if (trusted)
                PathPrograms.EnsureTrustedDestination(target, root);
            else
                Assert.IsTrue(Assert.ThrowsExactly<InstallerException>(() => PathPrograms.EnsureTrustedDestination(target, root)).IsRefusal);
            Assert.IsFalse(File.Exists(target));
        }
        finally
        {
            NativeLinux.AccessAclSizeReader = previous;
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void Destination_WithoutGroupWrite_DoesNotNeedAclLookup()
    {
        var root = CreateRoot();
        var previous = NativeLinux.AccessAclSizeReader;
        var calls = 0;
        try
        {
            NativeLinux.AccessAclSizeReader = _ => { calls++; return (-1, 13); };
            PathPrograms.EnsureTrustedDestination(Path.Combine(root, "hvo-sky"), root);
            Assert.AreEqual(0, calls);
        }
        finally
        {
            NativeLinux.AccessAclSizeReader = previous;
            Directory.Delete(root);
        }
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = LinuxOnly.Reason)]
    public void AclLookup_MissingDirectory_IsUnverified()
        => Assert.IsFalse(NativeLinux.HasNoExtendedAccessAcl(Path.Combine(Path.GetTempPath(), $"hvo-no-acl-{Guid.NewGuid():N}")));

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"hvo-path-acl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root, SafeFileSystem.OwnerDirectoryMode);
        return root;
    }

    private static string CreatePrivateGroupFolder(string root)
    {
        var folder = Path.Combine(root, ".local");
        Directory.CreateDirectory(folder, (UnixFileMode)0x1fd);
        File.SetUnixFileMode(folder, (UnixFileMode)0x1fd);
        return folder;
    }

    private static byte[] WriterAcl(bool namedGroup)
    {
        var entries = new List<(ushort Tag, ushort Permissions, uint Id)> { (1, 7, uint.MaxValue) };
        if (!namedGroup) entries.Add((2, 7, NativeLinux.getuid() == 1 ? 2u : 1u));
        entries.Add((4, 5, uint.MaxValue));
        if (namedGroup) entries.Add((8, 7, NativeLinux.getgid() == 1 ? 2u : 1u));
        entries.Add((16, 7, uint.MaxValue));
        entries.Add((32, 5, uint.MaxValue));
        var acl = new byte[4 + entries.Count * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(acl, 2);
        var offset = 4;
        foreach (var entry in entries)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(offset), entry.Tag);
            BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(offset + 2), entry.Permissions);
            BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(offset + 4), entry.Id);
            offset += 8;
        }
        return acl;
    }

#pragma warning disable SYSLIB1054 // Test-only creation of an isolated POSIX ACL fixture.
    [DllImport("libc", EntryPoint = "setxattr", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SetAccessAcl(byte[] path, byte[] name, byte[] value, nuint size, int flags);
#pragma warning restore SYSLIB1054
}
