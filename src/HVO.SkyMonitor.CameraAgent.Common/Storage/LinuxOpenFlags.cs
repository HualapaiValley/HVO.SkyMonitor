using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

/// <summary>
/// Linux <c>open(2)</c> flag values that differ by processor architecture. The kernel's default ABI
/// (<c>asm-generic/fcntl.h</c>, used by x86, loongarch, riscv, and s390) numbers <c>O_DIRECTORY</c> as
/// <c>0x10000</c> and <c>O_NOFOLLOW</c> as <c>0x20000</c>; the arm (including arm64) and powerpc ABIs override
/// them to <c>0x4000</c> and <c>0x8000</c>. On those architectures the default values name other flags
/// (<c>O_DIRECT</c> and <c>O_LARGEFILE</c> on arm), so a hard-coded default value makes a directory open fail
/// with <c>EINVAL</c> and silently drops the symlink guard. A new architecture belongs in the default row unless
/// its <c>uapi/asm/fcntl.h</c> overrides these two flags. Every native open in <c>CameraAgent.Common</c> and the
/// CameraAgent host must take its flags from here; the deployment CLI and the SQLite catalog, which do not
/// reference this assembly, carry the same table with their own tests, and an architecture test rejects any
/// other hard-coded value.
/// </summary>
public static class LinuxOpenFlags
{
    /// <summary><c>O_CLOEXEC</c>; identical on every Linux architecture.</summary>
    public const int CloseOnExec = 0x80000;

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
