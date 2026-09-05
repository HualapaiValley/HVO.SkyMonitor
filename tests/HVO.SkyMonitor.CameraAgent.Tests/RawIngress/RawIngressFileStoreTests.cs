using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Tests.RawIngress;

[TestClass]
[TestCategory("Unit")]
public sealed class RawIngressFileStoreTests
{
    [TestMethod]
    public void LinuxDirectorySync_UsesArchitectureAbiFlagAndFlushesDirectory()
    {
        // The table itself is pinned by LinuxOpenFlagsTests; this proves the store delegates to it.
        foreach (var architecture in new[] { Architecture.X64, Architecture.Arm64, Architecture.Ppc64le, Architecture.RiscV64 })
        {
            Assert.AreEqual(LinuxOpenFlags.GetDirectoryFlag(architecture), RawIngressFileStore.GetLinuxDirectoryOnlyFlag(architecture));
            Assert.AreEqual(LinuxOpenFlags.GetNoFollowFlag(architecture), RawIngressFileStore.GetLinuxNoFollowFlag(architecture));
        }
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
