using HVO.SkyMonitor.CameraAgent.Common.Automation.Scripts;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class CustomScriptTerminationTests
{
    [TestMethod]
    [DataRow("cancelled", 0)]
    [DataRow("timeout", 17)]
    [DataRow("output-limit", 0)]
    public async Task DelayedDrainPreservesNaturalExitExceptForExcessOutput(string cause, int exitCode)
    {
        var exited = false;
        var kills = 0;
        var termination = new CustomScriptTermination(() => exited, () => kills++);
        termination.Verified();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> ResultAsync()
        {
            await drain.Task.ConfigureAwait(false);
            return termination.Status(exitCode, cause == "output-limit");
        }
        var result = ResultAsync();
        // Native exit is observable before its managed exit notification and redirected EOF.
        exited = true;
        termination.Stop(cause);
        termination.Exited();
        Assert.IsFalse(result.IsCompleted);
        drain.SetResult();
        Assert.AreEqual(cause == "output-limit" ? "output-limit" : exitCode == 0 ? "completed" : "process-failed",
            await result.ConfigureAwait(false));
        Assert.AreEqual(0, kills);
    }

    [TestMethod]
    [DataRow("cancelled", "timeout")]
    [DataRow("timeout", "cancelled")]
    [DataRow("output-limit", "cancelled")]
    public void FirstLiveTerminationSurvivesExitAndLaterDrainSignals(string first, string later)
    {
        var exited = false;
        var kills = 0;
        var termination = new CustomScriptTermination(() => exited, () => { kills++; exited = true; });
        termination.Stop(first);
        termination.Exited();
        termination.Stop(later);
        Assert.AreEqual(first, termination.Status(137));
        Assert.AreEqual(1, kills);
    }
    [TestMethod]
    [DataRow("exited", "output-limit")]
    [DataRow("cancelled", "cancelled")]
    [DataRow("timeout", "timeout")]
    [DataRow("launch-failed", "launch-failed")]
    public void ExcessBytesRemainAFailureUnlessALiveCancellationTimeoutOrLaunchFailureWasLatchedFirst(string cause, string expected)
    {
        var termination = new CustomScriptTermination(() => false, static () => { });
        // A launch failure is latched before budget verification; the other causes model a running script.
        if (cause != "launch-failed") termination.Verified();
        if (cause == "exited") termination.Exited();
        else termination.Stop(cause);
        termination.Exited();
        Assert.AreEqual(expected, termination.Status(0, outputLimitExceeded: true));
    }

    /// <summary>
    /// Before the kernel reports this launch's budgets, no policy has been written and no script has run, so an exit
    /// belongs to the launcher, whatever its code and output (#1768 S-L3, S-L5).
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(125)]
    public void AnExitBeforeBudgetVerificationIsALaunchFailure(int exitCode)
    {
        var termination = new CustomScriptTermination(static () => true, static () => Assert.Fail("An exited launcher is not killed."));
        // The verification loop observes the exit and requests a launch failure.
        termination.Stop("launch-failed");
        termination.Exited();
        Assert.AreEqual("launch-failed", termination.Status(exitCode));
        Assert.AreEqual("launch-failed", termination.Status(exitCode, outputLimitExceeded: true));
    }

    [TestMethod]
    [DataRow(0, "completed")]
    [DataRow(17, "process-failed")]
    public void AnExitAfterBudgetVerificationFollowsTheExitCode(int exitCode, string expected)
    {
        var termination = new CustomScriptTermination(static () => true, static () => Assert.Fail("An exited script is not killed."));
        termination.Verified();
        termination.Exited();
        Assert.AreEqual(expected, termination.Status(exitCode));
    }

    [TestMethod]
    [DataRow(false, "launch-failed")]
    [DataRow(true, "process-failed")]
    public async Task SettlementStopsAnUndecidedLiveTreeUnderItsPhaseLabel(bool verified, string expected)
    {
        var exited = false;
        var kills = 0;
        var termination = new CustomScriptTermination(() => exited, () => { kills++; exited = true; });
        if (verified) termination.Verified();
        await termination.SettleAsync().ConfigureAwait(false);
        termination.Exited();
        Assert.AreEqual(1, kills);
        Assert.AreEqual(expected, termination.Status(137));
    }
}
