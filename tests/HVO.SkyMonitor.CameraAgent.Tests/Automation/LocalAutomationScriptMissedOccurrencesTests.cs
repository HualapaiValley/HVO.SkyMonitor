using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationScriptMissedOccurrencesTests
{
    [TestMethod]
    public async Task ScriptKindMissedPeriodsAreRecordedOnceAndNeverReplayedAfterRestart()
        => await AssertMissedAndRestartAsync("registered.digest", static (_, _) => ValueTask.FromResult(
            new LocalAutomationExecution(LocalAutomationRunOutcome.Failed, "script.process-failed; exit=17; stdoutBytes=0; stderrBytes=0")))
            .ConfigureAwait(false);

    internal static async Task AssertMissedAndRestartAsync(string target,
        Func<LocalAutomationOccurrence, CancellationToken, ValueTask<LocalAutomationExecution>> execute)
    {
        using var fixture = new ScriptControlsFixture();
        var executions = 0;
        fixture.Stub.Execute = async (occurrence, token) =>
        {
            executions++;
            return await execute(occurrence, token).ConfigureAwait(false);
        };
        fixture.Stub.Target = target;
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(target), default).ConfigureAwait(false);
        fixture.Clock.Advance(TimeSpan.FromMinutes(5));
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        await fixture.Worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var runs = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs;
        Assert.HasCount(2, runs);
        Assert.AreEqual(LocalAutomationTaskKind.CustomScript, runs.Single(run => run.Outcome == LocalAutomationRunOutcome.Failed).Occurrence!.Definition.TaskKind);
        var missed = runs.Single(run => run.Outcome == LocalAutomationRunOutcome.Missed);
        Assert.AreEqual("No run was recorded for 4 elapsed occurrence(s); they are not replayed.", missed.Detail);
        Assert.AreEqual(1, executions);
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        Assert.AreEqual(1, executions);
        fixture.Runner.Dispose();
        fixture.Store.Dispose();
        using var reopened = fixture.NewStore();
        using var worker = new LocalAutomationOffSweepWorker();
        using var runner = new LocalAutomationRunnerService(reopened, fixture.Registry, new StubCaptureSequenceSource(),
            Options.Create(new CameraAgentHostOptions()), fixture.Clock, NullLogger<LocalAutomationRunnerService>.Instance,
            offSweepWorker: worker);
        await reopened.InitializeAsync(default).ConfigureAwait(false);
        await runner.SweepAsync(default).ConfigureAwait(false);
        await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(1, executions);
        CollectionAssert.AreEqual(runs.ToArray(), (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.ToArray());
    }
}
