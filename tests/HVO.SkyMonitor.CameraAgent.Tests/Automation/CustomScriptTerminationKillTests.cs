using System.ComponentModel;
using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

/// <summary>
/// The callback kill path (#1768 S-L1, S-L2). A tree kill that fails never escapes into the cancelling thread or a
/// timer. It is retried once at settlement and settles the run as a kill failure. Later causes never wait on it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CustomScriptTerminationKillTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [TestMethod]
    [DataRow("cancelled", true)]
    [DataRow("timeout", true)]
    [DataRow("output-limit", true)]
    [DataRow("launch-failed", true)]
    [DataRow("cancelled", false)]
    [DataRow("timeout", false)]
    public async Task AFailedCallbackKillNeverEscapesItsCancellationAndSettlesAsAKillFailure(string cause, bool retryEndsTheTree)
    {
        // The first kill fails like a partial Process.Kill(entireProcessTree: true); the settlement retry may end it.
        var tree = new FakeTree(attempt => attempt > 1 && retryEndsTheTree);
        var termination = new CustomScriptTermination(() => tree.HasExited, tree.Kill);
        if (cause != "launch-failed") termination.Verified();
        using var source = new CancellationTokenSource();
        using var registration = source.Token.Register(() => termination.Stop(cause));

        // Operator stop and host stop await exactly this; a throwing callback faults it.
        await source.CancelAsync().ConfigureAwait(false);
        Assert.AreEqual(1, tree.Kills);
        Assert.IsFalse(tree.HasExited);

        await termination.SettleAsync().WaitAsync(Bound).ConfigureAwait(false);
        Assert.AreEqual(2, tree.Kills, "Settlement retries a failed callback kill exactly once.");
        Assert.AreEqual(retryEndsTheTree, tree.HasExited);
        // A surviving tree is ended by the scope's runtime ceiling; the kill failure stays recorded.
        tree.Exit();
        termination.Exited();
        Assert.AreEqual(CustomScriptTermination.KillFailed, termination.Status(137));
        Assert.AreEqual(CustomScriptTermination.KillFailed, termination.Status(137, outputLimitExceeded: true));
    }

    [TestMethod]
    public async Task AFailedTimeoutKillOnTheTimerThreadDoesNotTerminateTheHost()
    {
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tree = new FakeTree(static _ => false, attempted);
        var termination = new CustomScriptTermination(() => tree.HasExited, tree.Kill);
        termination.Verified();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(10));
        using var registration = timeout.Token.Register(() => termination.Stop("timeout"));

        // An exception escaping this timer callback is unhandled and would end the test host here.
        await attempted.Task.WaitAsync(Bound).ConfigureAwait(false);
        await termination.SettleAsync().WaitAsync(Bound).ConfigureAwait(false);

        tree.Exit();
        termination.Exited();
        Assert.AreEqual(2, tree.Kills);
        Assert.AreEqual(CustomScriptTermination.KillFailed, termination.Status(137));
    }

    [TestMethod]
    public async Task AKillThatFailsBecauseTheTreeHasJustExitedIsNotAKillFailure()
    {
        var tree = new FakeTree(static _ => true);
        var termination = new CustomScriptTermination(() => tree.HasExited, () =>
        {
            tree.Exit();
            throw new InvalidOperationException("No process is associated with this object.");
        });
        termination.Verified();

        termination.Stop("timeout");
        await termination.SettleAsync().WaitAsync(Bound).ConfigureAwait(false);
        termination.Exited();

        Assert.AreEqual("timeout", termination.Status(137));
    }

    [TestMethod]
    public async Task LaterCausesReturnWhileTheFirstKillIsInFlightAndSettlementWaitsForIt()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var tree = new FakeTree(static _ => true);
        var termination = new CustomScriptTermination(() => tree.HasExited, () =>
        {
            entered.Set();
            release.Wait(Bound);
            tree.Kill();
        });
        termination.Verified();
        var first = Task.Run(() => termination.Stop("timeout"));
        Assert.IsTrue(entered.Wait(Bound));

        // Operator stop, host stop and the exit notification arrive while the timeout's kill is still running.
        var later = Task.WhenAll(
            Task.Run(() => termination.Stop("cancelled")),
            Task.Run(() => termination.Stop("cancelled")),
            Task.Run(termination.Exited));
        await later.WaitAsync(Bound).ConfigureAwait(false);
        var settled = termination.SettleAsync();
        Assert.IsFalse(settled.IsCompleted, "Settlement decides on a retry only after the in-flight kill finishes.");

        release.Set();
        await first.WaitAsync(Bound).ConfigureAwait(false);
        await settled.WaitAsync(Bound).ConfigureAwait(false);
        termination.Exited();
        Assert.AreEqual(1, tree.Kills);
        Assert.AreEqual("timeout", termination.Status(137));
    }

    private sealed class FakeTree(Func<int, bool> killEndsTheTree, TaskCompletionSource? attempted = null)
    {
        private int _exited;
        private int _kills;

        internal bool HasExited => Volatile.Read(ref _exited) != 0;

        internal int Kills => Volatile.Read(ref _kills);

        internal void Exit() => Volatile.Write(ref _exited, 1);

        internal void Kill()
        {
            var attempt = Interlocked.Increment(ref _kills);
            attempted?.TrySetResult();
            // EPERM for a signal the policy denies, aggregated the way a tree kill reports it.
            if (!killEndsTheTree(attempt)) throw new AggregateException("One or more errors occurred.", new Win32Exception(1));
            Exit();
        }
    }
}
