using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeSync;

/// <summary>Reads whether the host's time service has the kernel clock synchronized.</summary>
public interface IKernelClockReader
{
    KernelClockState Read();
}

public enum KernelClockStatus
{
    /// <summary>The platform does not report it, or the call was refused.</summary>
    Unknown,

    Synchronized,

    Unsynchronized,
}

/// <summary>The kernel's view of its clock, with its maximum and estimated error when it reports them.</summary>
public sealed record KernelClockState(KernelClockStatus Status, TimeSpan? MaximumError, TimeSpan? EstimatedError)
{
    public static KernelClockState Unknown { get; } = new(KernelClockStatus.Unknown, null, null);
}

/// <summary>
/// Reads the Linux kernel clock state with <c>adjtimex</c> in read-only mode (<c>modes = 0</c>), which needs no
/// capability and is allowed by Docker's default seccomp profile. The kernel reports the clock unsynchronized until a
/// time service (systemd-timesyncd, chrony, ntpd) disciplines it. On any other platform, or when the call is refused,
/// the state is <see cref="KernelClockStatus.Unknown"/>.
/// </summary>
public sealed class LinuxKernelClockReader : IKernelClockReader
{
    // The 64-bit struct timex is 208 bytes; the buffer leaves room for any tail a later kernel adds.
    private const int TimexLength = 256;
    private const int MaximumErrorOffset = 24;
    private const int EstimatedErrorOffset = 32;
    private const int StatusOffset = 40;
    private const int StatusUnsynchronized = 0x0040;
    private const int TimeError = 5;

    public KernelClockState Read()
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess || !BitConverter.IsLittleEndian)
        {
            return KernelClockState.Unknown;
        }
        var buffer = new byte[TimexLength];
        int state;
        try
        {
            state = Adjtimex(buffer);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return KernelClockState.Unknown;
        }
        return Interpret(state, buffer);
    }

    /// <summary>Interprets an <c>adjtimex</c> return value and the <c>struct timex</c> it filled.</summary>
    internal static KernelClockState Interpret(int state, ReadOnlySpan<byte> timex)
    {
        if (state < 0 || timex.Length < StatusOffset + sizeof(int))
        {
            return KernelClockState.Unknown;
        }
        var maximumError = MemoryMarshal.Read<long>(timex[MaximumErrorOffset..]);
        var estimatedError = MemoryMarshal.Read<long>(timex[EstimatedErrorOffset..]);
        var status = MemoryMarshal.Read<int>(timex[StatusOffset..]);
        var synchronized = state != TimeError && (status & StatusUnsynchronized) == 0;
        return new KernelClockState(
            synchronized ? KernelClockStatus.Synchronized : KernelClockStatus.Unsynchronized,
            Microseconds(maximumError),
            Microseconds(estimatedError));
    }

    private static TimeSpan? Microseconds(long value)
        => value is >= 0 and < long.MaxValue / TimeSpan.TicksPerMicrosecond ? TimeSpan.FromMicroseconds(value) : null;

#pragma warning disable SYSLIB1054 // This narrow Unix call avoids enabling unsafe code for source-generated interop.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "adjtimex", ExactSpelling = true)]
    private static extern int Adjtimex([In, Out] byte[] timex);
#pragma warning restore SYSLIB1054
}
