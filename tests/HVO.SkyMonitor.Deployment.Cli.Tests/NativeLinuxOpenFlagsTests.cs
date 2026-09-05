using System.Runtime.InteropServices;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class NativeLinuxOpenFlagsTests
{
    [TestMethod]
    public void OpenFlags_FollowTheKernelAbiOfEachArchitecture()
    {
        foreach (var overriding in new[] { Architecture.Arm, Architecture.Arm64, Architecture.Armv6, Architecture.Ppc64le })
        {
            Assert.AreEqual(0x4000, NativeLinux.GetOpenDirectoryFlag(overriding), $"O_DIRECTORY on {overriding}");
            Assert.AreEqual(0x8000, NativeLinux.GetOpenNoFollowFlag(overriding), $"O_NOFOLLOW on {overriding}");
        }

        foreach (var generic in new[] { Architecture.X86, Architecture.X64, Architecture.LoongArch64, Architecture.RiscV64, Architecture.S390x })
        {
            Assert.AreEqual(0x10000, NativeLinux.GetOpenDirectoryFlag(generic), $"O_DIRECTORY on {generic}");
            Assert.AreEqual(0x20000, NativeLinux.GetOpenNoFollowFlag(generic), $"O_NOFOLLOW on {generic}");
        }

        Assert.ThrowsExactly<PlatformNotSupportedException>(() => NativeLinux.GetOpenDirectoryFlag(Architecture.Wasm));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => NativeLinux.GetOpenNoFollowFlag(Architecture.Wasm));
    }

    [TestMethod]
    public void OpenDirectoryNoFollow_OpensARealDirectoryAndRefusesASymlinkOnThisArchitecture()
    {
        // The real-directory assertions discriminated on aarch64 while the x86 values were hard-coded (O_DIRECT on a
        // directory fails with EINVAL). The symlink assertions pin the O_NOFOLLOW guard going forward.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Native directory opening is Linux-only.");
        }

        var root = Directory.CreateTempSubdirectory("hvo-native-open-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real")).FullName;
            var link = Path.Combine(root.FullName, "link");
            File.CreateSymbolicLink(link, real);

            using var opened = NativeLinux.OpenDirectoryNoFollow(real);
            Assert.IsFalse(opened.IsInvalid);

            var refused = Assert.ThrowsExactly<InstallerException>(() => NativeLinux.OpenDirectoryNoFollow(link));
            StringAssert.Contains(refused.Message, "without following links", StringComparison.Ordinal);

            using var parent = NativeLinux.OpenDirectoryNoFollow(root.FullName);
            using var child = NativeLinux.TryOpenDirectoryAt(parent, "real");
            Assert.IsNotNull(child);
            using var linked = NativeLinux.TryOpenDirectoryAt(parent, "link");
            Assert.IsNull(linked, "a symlinked child must not be opened as a directory");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void FlushDirectory_FlushesARealDirectoryAndRefusesASymlinkedOne()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Native directory flushing is Linux-only.");
        }

        var root = Directory.CreateTempSubdirectory("hvo-native-flush-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real")).FullName;
            var link = Path.Combine(root.FullName, "link");
            File.CreateSymbolicLink(link, real);

            NativeLinux.FlushDirectory(real);

            var refused = Assert.ThrowsExactly<InstallerException>(() => NativeLinux.FlushDirectory(link));
            StringAssert.Contains(refused.Message, "could not be flushed", StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
