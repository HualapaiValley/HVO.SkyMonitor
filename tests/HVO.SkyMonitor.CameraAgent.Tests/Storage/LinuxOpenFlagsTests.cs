using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class LinuxOpenFlagsTests
{
    [TestMethod]
    public void DirectoryAndNoFollow_FollowTheKernelAbiOfEachArchitecture()
    {
        // Generic ABI (asm-generic/fcntl.h): arm, arm64, ppc64le.
        foreach (var generic in new[] { Architecture.Arm, Architecture.Arm64, Architecture.Armv6, Architecture.Ppc64le })
        {
            Assert.AreEqual(0x4000, LinuxOpenFlags.GetDirectoryFlag(generic), $"O_DIRECTORY on {generic}");
            Assert.AreEqual(0x8000, LinuxOpenFlags.GetNoFollowFlag(generic), $"O_NOFOLLOW on {generic}");
        }

        // x86 ABI, shared by loongarch, riscv, and s390.
        foreach (var x86 in new[] { Architecture.X86, Architecture.X64, Architecture.LoongArch64, Architecture.RiscV64, Architecture.S390x })
        {
            Assert.AreEqual(0x10000, LinuxOpenFlags.GetDirectoryFlag(x86), $"O_DIRECTORY on {x86}");
            Assert.AreEqual(0x20000, LinuxOpenFlags.GetNoFollowFlag(x86), $"O_NOFOLLOW on {x86}");
        }

        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LinuxOpenFlags.GetDirectoryFlag(Architecture.Wasm));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LinuxOpenFlags.GetNoFollowFlag(Architecture.Wasm));
    }

    [TestMethod]
    public void CurrentProcessValues_MatchTheRunningArchitecture()
    {
        var architecture = RuntimeInformation.ProcessArchitecture;

        Assert.AreEqual(LinuxOpenFlags.GetDirectoryFlag(architecture), LinuxOpenFlags.Directory);
        Assert.AreEqual(LinuxOpenFlags.GetNoFollowFlag(architecture), LinuxOpenFlags.NoFollow);
    }

    [TestMethod]
    public void OwnerRecoveryParentOpen_UsesTheRunningArchitectureFlags()
    {
        // On aarch64 the x86 O_DIRECTORY value means O_DIRECT, which open(2) refuses for a directory with EINVAL,
        // and the x86 O_NOFOLLOW value means O_LARGEFILE, so a symlinked parent would be followed. This exercise
        // is the one the arm64 workflow lost while the flags were hard-coded.
        if (!OperatingSystem.IsLinux())
        {
            Assert.Inconclusive("Owner recovery directory opening is Linux-only.");
        }

        var root = Directory.CreateTempSubdirectory("hvo-open-flags-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real")).FullName;
            var link = Path.Combine(root.FullName, "link");
            File.CreateSymbolicLink(link, real);

            using var opened = OwnerRecoveryNative.OpenParent(real);
            Assert.IsFalse(opened.IsInvalid, "a real directory must open with O_DIRECTORY | O_NOFOLLOW");

            var refused = Assert.ThrowsExactly<InvalidOperationException>(() => OwnerRecoveryNative.OpenParent(link));
            StringAssert.Contains(refused.Message, "could not be opened safely", StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
