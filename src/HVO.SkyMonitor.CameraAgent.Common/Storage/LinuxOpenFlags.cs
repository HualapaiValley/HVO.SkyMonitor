using System.Runtime.InteropServices;
using SharedLinuxOpenFlags = HVO.SkyMonitor.Storage.FileSystem.LinuxOpenFlags;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

/// <summary>
/// Preserves the public CameraAgent flag API while the shared filesystem library owns the
/// architecture-specific values. No native flag table is maintained by CameraAgent.
/// </summary>
public static class LinuxOpenFlags
{
    /// <summary><c>O_CLOEXEC</c>; identical on every Linux architecture.</summary>
    public const int CloseOnExec = SharedLinuxOpenFlags.CloseOnExec;

    /// <summary><c>O_DIRECTORY</c> for the current process architecture.</summary>
    public static int Directory => SharedLinuxOpenFlags.Directory;

    /// <summary><c>O_NOFOLLOW</c> for the current process architecture.</summary>
    public static int NoFollow => SharedLinuxOpenFlags.NoFollow;

    /// <summary><c>O_DIRECTORY</c> for <paramref name="architecture"/>.</summary>
    public static int GetDirectoryFlag(Architecture architecture)
        => SharedLinuxOpenFlags.GetDirectoryFlag(architecture);

    /// <summary><c>O_NOFOLLOW</c> for <paramref name="architecture"/>.</summary>
    public static int GetNoFollowFlag(Architecture architecture)
        => SharedLinuxOpenFlags.GetNoFollowFlag(architecture);
}
