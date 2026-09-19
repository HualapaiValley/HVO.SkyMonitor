using System.Runtime.InteropServices;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.Storage.FileSystem.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class LinuxOpenFlagsTests
{
    [TestMethod]
    [DataRow(Architecture.X64, 0x10000, 0x20000)]
    [DataRow(Architecture.X86, 0x10000, 0x20000)]
    [DataRow(Architecture.Arm64, 0x4000, 0x8000)]
    [DataRow(Architecture.Arm, 0x4000, 0x8000)]
    [DataRow(Architecture.Armv6, 0x4000, 0x8000)]
    [DataRow(Architecture.Ppc64le, 0x4000, 0x8000)]
    [DataRow(Architecture.RiscV64, 0x10000, 0x20000)]
    [DataRow(Architecture.LoongArch64, 0x10000, 0x20000)]
    [DataRow(Architecture.S390x, 0x10000, 0x20000)]
    public void FlagsFollowTheKernelAbiForEachArchitecture(Architecture architecture, int directory, int noFollow)
    {
        Assert.AreEqual(directory, LinuxOpenFlags.GetDirectoryFlag(architecture));
        Assert.AreEqual(noFollow, LinuxOpenFlags.GetNoFollowFlag(architecture));
    }

    [TestMethod]
    public void UnknownArchitectureIsRefusedRatherThanDefaulted()
    {
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LinuxOpenFlags.GetDirectoryFlag(Architecture.Wasm));
        Assert.ThrowsExactly<PlatformNotSupportedException>(() => LinuxOpenFlags.GetNoFollowFlag(Architecture.Wasm));
    }

    [TestMethod]
    public void CurrentProcessFlagsMatchTheTableForThisArchitecture()
    {
        Assert.AreEqual(LinuxOpenFlags.GetDirectoryFlag(RuntimeInformation.ProcessArchitecture), LinuxOpenFlags.Directory);
        Assert.AreEqual(LinuxOpenFlags.GetNoFollowFlag(RuntimeInformation.ProcessArchitecture), LinuxOpenFlags.NoFollow);
    }
}
