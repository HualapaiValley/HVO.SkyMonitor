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
    public async Task NaturalExitDuringDelayedDrainCannotBecomeALaterTermination(string cause, int exitCode)
    {
        var exited = false;
        var kills = 0;
        var termination = new CustomScriptTermination(() => exited, () => kills++);
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> ResultAsync()
        {
            await drain.Task.ConfigureAwait(false);
            return termination.Status(exitCode);
        }
        var result = ResultAsync();
        // Native exit is observable before its managed exit notification and redirected EOF.
        exited = true;
        termination.Stop(cause);
        termination.Exited();
        Assert.IsFalse(result.IsCompleted);
        drain.SetResult();
        Assert.AreEqual(exitCode == 0 ? "completed" : "process-failed", await result.ConfigureAwait(false));
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
}
