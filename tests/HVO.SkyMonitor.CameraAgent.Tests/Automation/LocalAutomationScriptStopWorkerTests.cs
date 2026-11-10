using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class LocalAutomationScriptStopWorkerTests
{
    [TestMethod]
    public async Task OldStopCannotCancelANewerRunOrABuiltInAndTheSlotStaysHeldUntilDrain()
    {
        using var worker = new LocalAutomationOffSweepWorker();
        var occurrence = CustomScriptTaskAdapterTests.Occurrence(CustomScriptTaskAdapterTests.Script());
        var entered = ScriptControlsFixture.Signal();
        var cancelled = ScriptControlsFixture.Signal();
        var release = ScriptControlsFixture.Signal();
        using var first = worker.TryReserve();
        Assert.IsNotNull(first);
        first.Dispatch(async token =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled.SetResult(); }
            await release.Task.ConfigureAwait(false);
        }, occurrence, occurrence.RunKey, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(await worker.StopScriptRunAsync("not-this-run", default).ConfigureAwait(false));
            var stopping = worker.StopScriptRunAsync(occurrence.RunKey, default).AsTask();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(stopping.IsCompleted);
            Assert.IsNull(worker.TryReserve());
            release.SetResult();
            Assert.IsTrue(await stopping.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false));
        }
        finally { release.TrySetResult(); }
        var secondRelease = ScriptControlsFixture.Signal();
        var secondEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var second = worker.TryReserve();
        Assert.IsNotNull(second);
        second.Dispatch(async token => { secondEntered.SetResult(token); await secondRelease.Task.ConfigureAwait(false); },
            occurrence with { RunKey = "new-run" }, "new-run", default);
        try
        {
            var token = await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            Assert.IsFalse(await worker.StopScriptRunAsync(occurrence.RunKey, default).ConfigureAwait(false));
            Assert.IsFalse(token.IsCancellationRequested, "A retry must not cancel the newer reserved run.");
        }
        finally { secondRelease.TrySetResult(); }
        await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        using var builtin = worker.TryReserve();
        Assert.IsNotNull(builtin);
        var builtinRelease = ScriptControlsFixture.Signal();
        builtin.Dispatch(_ => builtinRelease.Task, occurrence with
        { Definition = occurrence.Definition with { TaskKind = LocalAutomationTaskKind.OrbitalElementsRefresh } }, occurrence.RunKey, default);
        try { Assert.IsFalse(await worker.StopScriptRunAsync(occurrence.RunKey, default).ConfigureAwait(false)); }
        finally { builtinRelease.TrySetResult(); }
        await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }
}
