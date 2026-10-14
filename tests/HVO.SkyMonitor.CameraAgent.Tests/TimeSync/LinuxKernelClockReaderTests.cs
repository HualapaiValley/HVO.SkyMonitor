using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Common.TimeSync;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeSync;

[TestClass]
[TestCategory("Unit")]
public sealed class LinuxKernelClockReaderTests
{
    private const int TimeOk = 0;
    private const int TimeError = 5;
    private const int StatusUnsynchronized = 0x0040;
    private const int StatusPll = 0x0001;

    [TestMethod]
    public void Interpret_ASynchronizedClock_ReportsItsErrorsInMicroseconds()
    {
        var state = LinuxKernelClockReader.Interpret(TimeOk, Timex(maximumError: 16_000, estimatedError: 250, status: StatusPll));

        Assert.AreEqual(
            new KernelClockState(KernelClockStatus.Synchronized, TimeSpan.FromMilliseconds(16), TimeSpan.FromMicroseconds(250)),
            state);
    }

    [TestMethod]
    [DataRow(TimeOk, StatusUnsynchronized | StatusPll, DisplayName = "STA_UNSYNC set")]
    [DataRow(TimeError, StatusPll, DisplayName = "TIME_ERROR returned")]
    public void Interpret_AClockTheTimeServiceHasNotDisciplined_IsUnsynchronized(int returned, int status)
        => Assert.AreEqual(
            KernelClockStatus.Unsynchronized,
            LinuxKernelClockReader.Interpret(returned, Timex(maximumError: 16_000_000, estimatedError: 16_000_000, status: status)).Status);

    [TestMethod]
    public void Interpret_ARefusedCall_IsUnknown()
        => Assert.AreEqual(KernelClockState.Unknown, LinuxKernelClockReader.Interpret(-1, Timex(0, 0, 0)));

    [TestMethod]
    public void Interpret_ABufferTooShortForTheStatus_IsUnknown()
        => Assert.AreEqual(KernelClockState.Unknown, LinuxKernelClockReader.Interpret(TimeOk, new byte[43]));

    [TestMethod]
    public void Interpret_ANegativeError_IsLeftUnreported()
    {
        var state = LinuxKernelClockReader.Interpret(TimeOk, Timex(maximumError: -1, estimatedError: long.MaxValue, status: 0));

        Assert.AreEqual(KernelClockStatus.Synchronized, state.Status);
        Assert.IsNull(state.MaximumError);
        Assert.IsNull(state.EstimatedError);
    }

    [TestMethod]
    public void Read_OnThisHost_NeverThrows()
    {
        var state = new LinuxKernelClockReader().Read();

        // A container may refuse the call; either way the result is a state, never an exception.
        if (!OperatingSystem.IsLinux())
        {
            Assert.AreEqual(KernelClockState.Unknown, state);
        }
        Assert.IsTrue(Enum.IsDefined(state.Status));
    }

    // The 64-bit struct timex: maxerror at 24, esterror at 32 and status at 40.
    private static byte[] Timex(long maximumError, long estimatedError, int status)
    {
        var buffer = new byte[256];
        MemoryMarshal.Write(buffer.AsSpan(24), in maximumError);
        MemoryMarshal.Write(buffer.AsSpan(32), in estimatedError);
        MemoryMarshal.Write(buffer.AsSpan(40), in status);
        return buffer;
    }
}
