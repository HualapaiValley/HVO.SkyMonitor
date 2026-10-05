using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using SharedLinuxOpenFlags = HVO.SkyMonitor.Storage.FileSystem.LinuxOpenFlags;

namespace HVO.SkyMonitor.CameraAgent.Tests.Storage;

[TestClass]
[TestCategory("Unit")]
public sealed class LinuxOpenFlagsTests
{
    [TestMethod]
    public void DirectoryAndNoFollow_FollowTheKernelAbiOfEachArchitecture()
    {
        Assert.AreEqual(SharedLinuxOpenFlags.CloseOnExec, LinuxOpenFlags.CloseOnExec);
        Assert.AreEqual(SharedLinuxOpenFlags.Directory, LinuxOpenFlags.Directory);
        Assert.AreEqual(SharedLinuxOpenFlags.NoFollow, LinuxOpenFlags.NoFollow);
        // arm and powerpc override the asm-generic values in their uapi/asm/fcntl.h.
        foreach (var overriding in new[] { Architecture.Arm, Architecture.Arm64, Architecture.Armv6, Architecture.Ppc64le })
        {
            Assert.AreEqual(0x4000, LinuxOpenFlags.GetDirectoryFlag(overriding), $"O_DIRECTORY on {overriding}");
            Assert.AreEqual(0x8000, LinuxOpenFlags.GetNoFollowFlag(overriding), $"O_NOFOLLOW on {overriding}");
            Assert.AreEqual(SharedLinuxOpenFlags.GetDirectoryFlag(overriding), LinuxOpenFlags.GetDirectoryFlag(overriding));
            Assert.AreEqual(SharedLinuxOpenFlags.GetNoFollowFlag(overriding), LinuxOpenFlags.GetNoFollowFlag(overriding));
        }

        // asm-generic defaults: x86, loongarch, riscv, s390.
        foreach (var generic in new[] { Architecture.X86, Architecture.X64, Architecture.LoongArch64, Architecture.RiscV64, Architecture.S390x })
        {
            Assert.AreEqual(0x10000, LinuxOpenFlags.GetDirectoryFlag(generic), $"O_DIRECTORY on {generic}");
            Assert.AreEqual(0x20000, LinuxOpenFlags.GetNoFollowFlag(generic), $"O_NOFOLLOW on {generic}");
            Assert.AreEqual(SharedLinuxOpenFlags.GetDirectoryFlag(generic), LinuxOpenFlags.GetDirectoryFlag(generic));
            Assert.AreEqual(SharedLinuxOpenFlags.GetNoFollowFlag(generic), LinuxOpenFlags.GetNoFollowFlag(generic));
        }

        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LinuxOpenFlags.GetDirectoryFlag(Architecture.Wasm));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LinuxOpenFlags.GetNoFollowFlag(Architecture.Wasm));
    }
}
