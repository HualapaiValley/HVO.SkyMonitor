using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

/// <summary>
/// Linux <c>open(2)</c> flag values that differ by processor architecture. The kernel's generic ABI (used by
/// arm, arm64, and ppc64le) numbers <c>O_DIRECTORY</c> and <c>O_NOFOLLOW</c> differently from the x86, loongarch,
/// riscv, and s390 ABIs; the x86 values mean <c>O_DIRECT</c> and <c>O_LARGEFILE</c> on the generic ABI, so a
/// hard-coded x86 value makes a directory open fail with <c>EINVAL</c> there and silently drops the symlink guard.
/// Every native open in the repository must take its flags from here.
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
