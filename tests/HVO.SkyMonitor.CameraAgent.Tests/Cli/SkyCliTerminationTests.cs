using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Cli;

namespace HVO.SkyMonitor.CameraAgent.Tests.Cli;

/// <summary>SIGINT, SIGTERM and SIGHUP: the first ends the command, the process still ends, and a second ends it at once.</summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SkyCliTerminationTests
{
    [TestMethod]
    [DataRow(PosixSignal.SIGINT)]
    [DataRow(PosixSignal.SIGTERM)]
    [DataRow(PosixSignal.SIGHUP)]
    public void FirstSignal_CancelsTheCommandAndTheProcessEnds130AfterTheGrace(PosixSignal signal)
    {
        var exits = new List<int>();
        var time = new ManualTimeProvider();
        using var termination = new SkyCliTermination(exits.Add, time);
        var context = new PosixSignalContext(signal);

        termination.OnSignal(context);

        Assert.IsTrue(context.Cancel, "The runtime's default would end the process at once.");
        Assert.IsTrue(termination.Token.IsCancellationRequested);
        Assert.IsEmpty(exits);
        Assert.AreEqual(SkyCliTermination.Grace, time.Timers.Single().DueTime);
        time.Timers.Single().Fire();
        Assert.AreEqual(130, exits.Single());
    }

    [TestMethod]
    public void SecondSignal_EndsTheProcessAtOnce()
    {
        var exits = new List<int>();
        using var termination = new SkyCliTermination(exits.Add, new ManualTimeProvider());

        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));
        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGHUP));

        Assert.AreEqual(130, exits.Single());
    }

    [TestMethod]
    public void AfterTheCommandEnded_ASignalOrTheGraceChangesNothing()
    {
        var exits = new List<int>();
        var time = new ManualTimeProvider();
        var termination = new SkyCliTermination(exits.Add, time);
        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGINT));
        termination.Dispose();

        time.Timers.Single().Fire();
        termination.OnSignal(new PosixSignalContext(PosixSignal.SIGINT));

        Assert.IsEmpty(exits);
        Assert.IsTrue(time.Timers.Single().Disposed);
    }

    [TestMethod]
    public void Register_HandlesTheSignalsOfThisProcessUntilDisposed()
    {
        using var termination = SkyCliTermination.Register();

        Assert.IsFalse(termination.Token.IsCancellationRequested);
    }
}
