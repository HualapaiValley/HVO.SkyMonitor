using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>
/// Linux <c>open(2)</c> flag values that differ by processor architecture. The kernel's default ABI
/// (<c>asm-generic/fcntl.h</c>: x86, loongarch, riscv, s390) numbers <c>O_DIRECTORY</c> as
/// <c>0x10000</c> and <c>O_NOFOLLOW</c> as <c>0x20000</c>; arm (including arm64) and powerpc override
/// them to <c>0x4000</c> and <c>0x8000</c>. On those architectures the default values name other
/// flags (<c>O_DIRECT</c>, <c>O_LARGEFILE</c> on arm), so a hard-coded default makes a directory open
/// fail with <c>EINVAL</c> and silently drops the symlink guard. A new architecture belongs in the
/// default row unless its <c>uapi/asm/fcntl.h</c> overrides these two flags.
/// </summary>
/// <remarks>
/// This table also exists in <c>CameraAgent.Common.Storage.LinuxOpenFlags</c> and in the deployment
/// CLI. #587 retires the CameraAgent copy in favour of this one; the CLI keeps its own because it
/// may not reference this project (it is a delivery tool, not a host).
/// </remarks>
public static class LinuxOpenFlags
{
    /// <summary><c>O_CLOEXEC</c>; identical on every Linux architecture.</summary>
    public const int CloseOnExec = 0x80000;

    /// <summary><c>O_RDONLY</c>.</summary>
    public const int ReadOnly = 0;

    /// <summary><c>O_DIRECTORY</c> for the current process architecture.</summary>
    public static int Directory { get; } = GetDirectoryFlag(RuntimeInformation.ProcessArchitecture);

    /// <summary><c>O_NOFOLLOW</c> for the current process architecture.</summary>
    public static int NoFollow { get; } = GetNoFollowFlag(RuntimeInformation.ProcessArchitecture);

    /// <summary><c>O_DIRECTORY</c> for <paramref name="architecture"/>.</summary>
    public static int GetDirectoryFlag(Architecture architecture)
        => architecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le => 0x4000,
            Architecture.X86 or Architecture.X64 or Architecture.LoongArch64 or Architecture.RiscV64 or Architecture.S390x => 0x10000,
            _ => throw new PlatformNotSupportedException($"Linux O_DIRECTORY is not configured for {architecture}.")
        };

    /// <summary><c>O_NOFOLLOW</c> for <paramref name="architecture"/>.</summary>
    public static int GetNoFollowFlag(Architecture architecture)
        => architecture switch
        {
            Architecture.Arm or Architecture.Arm64 or Architecture.Armv6 or Architecture.Ppc64le => 0x8000,
            Architecture.X86 or Architecture.X64 or Architecture.LoongArch64 or Architecture.RiscV64 or Architecture.S390x => 0x20000,
            _ => throw new PlatformNotSupportedException($"Linux O_NOFOLLOW is not configured for {architecture}.")
        };
}
