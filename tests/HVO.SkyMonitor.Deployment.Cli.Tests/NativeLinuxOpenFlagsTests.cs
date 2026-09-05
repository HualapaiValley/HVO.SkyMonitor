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
        foreach (var generic in new[] { Architecture.Arm, Architecture.Arm64, Architecture.Armv6, Architecture.Ppc64le })
        {
            Assert.AreEqual(0x4000, NativeLinux.GetOpenDirectoryFlag(generic), $"O_DIRECTORY on {generic}");
            Assert.AreEqual(0x8000, NativeLinux.GetOpenNoFollowFlag(generic), $"O_NOFOLLOW on {generic}");
        }

        foreach (var x86 in new[] { Architecture.X86, Architecture.X64, Architecture.LoongArch64, Architecture.RiscV64, Architecture.S390x })
        {
            Assert.AreEqual(0x10000, NativeLinux.GetOpenDirectoryFlag(x86), $"O_DIRECTORY on {x86}");
            Assert.AreEqual(0x20000, NativeLinux.GetOpenNoFollowFlag(x86), $"O_NOFOLLOW on {x86}");
        }

        Assert.ThrowsExactly<PlatformNotSupportedException>(() => NativeLinux.GetOpenDirectoryFlag(Architecture.Wasm));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => NativeLinux.GetOpenNoFollowFlag(Architecture.Wasm));
    }

    [TestMethod]
    public void OpenDirectoryNoFollow_OpensARealDirectoryAndRefusesASymlinkOnThisArchitecture()
    {
        // With x86 values on aarch64 the real directory fails with EINVAL (O_DIRECT) and the symlink is followed
        // (O_LARGEFILE instead of O_NOFOLLOW). Both assertions below therefore discriminate on arm64.
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
            Assert.IsNotNull(NativeLinux.TryOpenDirectoryAt(parent, "real"));
            Assert.IsNull(NativeLinux.TryOpenDirectoryAt(parent, "link"), "a symlinked child must not be opened as a directory");
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
