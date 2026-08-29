using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class RawIngressFileStoreTests
{
    [TestMethod]
    public void LinuxDirectorySync_UsesArchitectureAbiFlagAndFlushesDirectory()
    {
        Assert.AreEqual(0x10000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.X86));
        Assert.AreEqual(0x10000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.X64));
        Assert.AreEqual(0x4000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.Arm));
        Assert.AreEqual(0x4000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.Arm64));
        Assert.AreEqual(0x4000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.Armv6));
        Assert.AreEqual(0x4000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.Ppc64le));
        Assert.AreEqual(0x10000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.LoongArch64));
        Assert.AreEqual(0x10000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.RiscV64));
        Assert.AreEqual(0x10000, RawIngressFileStore.GetLinuxDirectoryOnlyFlag(Architecture.S390x));
        Assert.AreEqual(0x8000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.Arm));
        Assert.AreEqual(0x8000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.Arm64));
        Assert.AreEqual(0x8000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.Armv6));
        Assert.AreEqual(0x8000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.Ppc64le));
        Assert.AreEqual(0x20000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.X86));
        Assert.AreEqual(0x20000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.X64));
        Assert.AreEqual(0x20000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.LoongArch64));
        Assert.AreEqual(0x20000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.RiscV64));
        Assert.AreEqual(0x20000, RawIngressFileStore.GetLinuxNoFollowFlag(Architecture.S390x));

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"hvo-directory-sync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            RawIngressFileStore.SyncDirectory(directory);
            var file = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(file, "test");
            Assert.ThrowsExactly<IOException>(() => RawIngressFileStore.SyncDirectory(file));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
