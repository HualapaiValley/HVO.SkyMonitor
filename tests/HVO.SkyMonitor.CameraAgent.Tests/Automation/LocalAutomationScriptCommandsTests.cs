using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Services;
using Moq;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationScriptCommandsTests
{
    [TestMethod]
    public async Task QueuedStopPreservesOtherQueuedRunningAndTerminalRowsAndAllSchedules()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        foreach (var id in new[] { "script", "queued", "running", "terminal" })
        {
            await fixture.Store.SaveAsync(ScriptControlsFixture.Save(id: id), default).ConfigureAwait(false);
            await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow("run-" + id) with { DefinitionId = id }, default).ConfigureAwait(false);
        }
        var entries = await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false);
        var running = await fixture.Store.TryClaimQueuedAsync(entries.Single(entry => entry.Definition.DefinitionId == "running"), default).ConfigureAwait(false);
        Assert.IsNotNull(running);
        var terminal = await fixture.Store.TryClaimQueuedAsync(entries.Single(entry => entry.Definition.DefinitionId == "terminal"), default).ConfigureAwait(false);
        Assert.IsNotNull(terminal);
        await fixture.Store.CompleteRunAsync(terminal.RunKey, LocalAutomationRunOutcome.Succeeded, "done", default).ConfigureAwait(false);
        var before = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
        var target = before.Runs.Single(run => run.DefinitionId == "script");
        var stopped = await fixture.Commands.StopAsync(new("script", target.RunKey, 1, "owner"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, stopped.Status);
        foreach (var run in before.Runs.Where(run => run.DefinitionId != "script"))
            Assert.AreEqual(run, stopped.State.Runs.Single(item => item.RunKey == run.RunKey));
        CollectionAssert.AreEqual(entries.ToArray(), (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).ToArray());
        CollectionAssert.AreEqual(before.ActiveRuns.ToArray(), stopped.State.ActiveRuns.ToArray());
        foreach (var definition in before.Definitions)
            Assert.AreEqual(definition.NextRunUtc, stopped.State.Definitions.Single(item => item.Definition.DefinitionId == definition.Definition.DefinitionId).NextRunUtc);
    }

    [TestMethod]
    public async Task NaturalCompletionWinningStopReturnsTruthfulTerminalReplay()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        var entry = (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single();
        var running = await fixture.Store.TryClaimQueuedAsync(entry, default).ConfigureAwait(false);
        Assert.IsNotNull(running);
        var stale = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
        await fixture.Store.CompleteRunAsync(running.RunKey, LocalAutomationRunOutcome.Succeeded, "natural result", default).ConfigureAwait(false);
        var completed = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
        var projection = new Mock<ILocalAutomationStore>(MockBehavior.Strict);
        projection.SetupSequence(store => store.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(stale).ReturnsAsync(completed);
        var commands = new LocalAutomationScriptCommands(projection.Object, fixture.Worker);
        var result = await commands.StopAsync(new("script", running.RunKey, 1, "owner"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Unchanged, result.Status);
        Assert.AreEqual(completed, result.State);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, result.State.Runs.Single().Outcome);
        Assert.AreEqual("natural result", result.State.Runs.Single().Detail);
    }

    [TestMethod]
    public async Task QueuedStopValidatesExactIdentityAndOwnerAndPersistsWithoutAProcessResult()
    {
        using var fixture = new ScriptControlsFixture();
        var executions = 0;
        fixture.Stub.Execute = (_, _) =>
        {
            executions++;
            return ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Succeeded, "done"));
        };
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var queued = await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        var original = queued.Value!.State.Runs.Single();
        var before = (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single();
        var stop = new LocalAutomationScriptStopRequest("script", original.RunKey, 1, "owner");
        fixture.Authorized = false;
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await fixture.Service.StopScriptAsync(stop, default).ConfigureAwait(false)).Kind);
        fixture.Authorized = true;
        Assert.AreEqual(LocalAutomationCommandStatus.NotFound, (await fixture.Commands.StopAsync(stop with { DefinitionId = "other" }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.NotFound, (await fixture.Commands.StopAsync(stop with { RunKey = original.RunKey + ":retry2" }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, (await fixture.Commands.StopAsync(stop with { ExpectedDefinitionVersion = 2 }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Invalid, (await fixture.Commands.StopAsync(stop with { Actor = "" }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(original, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync().ConfigureAwait(false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Commands.StopAsync(stop, cancelled.Token).AsTask()).ConfigureAwait(false);
        var stopped = await fixture.Service.StopScriptAsync(stop, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, stopped.Value!.Status);
        var terminal = stopped.Value.State.Runs.Single();
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, terminal.Outcome);
        Assert.AreEqual("The owner stopped this prepared script occurrence before it started.", terminal.Detail);
        Assert.IsNull(terminal.StartedAtUtc);
        Assert.IsNotNull(terminal.CompletedAtUtc);
        Assert.AreEqual(original with { Outcome = terminal.Outcome, CompletedAtUtc = terminal.CompletedAtUtc, Detail = terminal.Detail }, terminal);
        Assert.AreEqual(before, (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single());
        Assert.AreEqual(LocalAutomationCommandStatus.Unchanged, (await fixture.Commands.StopAsync(stop, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow() with { Actor = "script-owner" }, default).ConfigureAwait(false)).Status);
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        Assert.AreEqual(0, executions);
        fixture.Runner.Dispose();
        fixture.Store.Dispose();
        using var reopened = fixture.NewStore();
        await reopened.InitializeAsync(default).ConfigureAwait(false);
        Assert.IsNull(await reopened.TryClaimQueuedAsync((await reopened.GetRunnerViewAsync(default).ConfigureAwait(false)).Single(), default).ConfigureAwait(false));
        Assert.AreEqual(terminal, (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task StopAtClaimBoundariesNeverLosesTheAttempt(int stage)
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var run = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        var claimCommitted = ScriptControlsFixture.Signal();
        var releaseDispatch = ScriptControlsFixture.Signal();
        var entered = ScriptControlsFixture.Signal();
        var executions = 0;
        fixture.Stub.Execute = async (_, token) =>
        {
            executions++;
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "unreachable");
        };
        var forwarding = new Mock<ILocalAutomationStore>(MockBehavior.Strict);
        var occurrences = forwarding.As<ILocalAutomationOccurrenceStore>();
        forwarding.Setup(item => item.GetRunnerViewAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken token) => fixture.Store.GetRunnerViewAsync(token));
        forwarding.Setup(item => item.CompleteDrainedRunAsync(It.IsAny<string>(), It.IsAny<LocalAutomationRunOutcome>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns((string key, LocalAutomationRunOutcome outcome, string detail, DateTimeOffset completed, CancellationToken token) => fixture.Store.CompleteDrainedRunAsync(key, outcome, detail, completed, token));
        occurrences.Setup(item => item.GetQueuedDefinitionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string id, CancellationToken token) => fixture.Store.GetQueuedDefinitionAsync(id, token));
        occurrences.Setup(item => item.TryClaimQueuedWithAdmissionAsync(It.IsAny<LocalAutomationRunnerEntry>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(async (LocalAutomationRunnerEntry entry, bool admission, CancellationToken token) =>
            {
                var claimed = await fixture.Store.TryClaimQueuedWithAdmissionAsync(entry, admission, token).ConfigureAwait(false);
                claimCommitted.TrySetResult();
                if (stage == 1) await releaseDispatch.Task.ConfigureAwait(false);
                return claimed;
            });
        using var runner = new LocalAutomationRunnerService(forwarding.Object, fixture.Registry, new StubCaptureSequenceSource(),
            Options.Create(new CameraAgentHostOptions()), fixture.Clock, NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: fixture.Worker);
        var request = new LocalAutomationScriptStopRequest("script", run.RunKey, 1, "owner");
        try
        {
            if (stage == 0)
            {
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await fixture.Commands.StopAsync(request, default).ConfigureAwait(false)).Status);
                // Use the real runner: a withdrawn row must not be claimed by the next sweep.
                await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
                Assert.AreEqual(0, executions);
            }
            else
            {
                var sweep = runner.SweepAsync(default);
                await claimCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (stage == 2) await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var stop = fixture.Commands.StopAsync(request, default).AsTask();
                if (stage == 1)
                {
                    Assert.IsFalse(stop.IsCompleted, "Stop must wait through the committed-claim/registration gap.");
                    Assert.AreEqual(LocalAutomationRunOutcome.Running, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).ActiveRuns.Single().Outcome);
                    releaseDispatch.SetResult();
                }
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await stop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false)).Status);
                await sweep.ConfigureAwait(false);
                Assert.AreEqual(1, executions);
            }
            var terminal = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
            Assert.AreEqual(stage == 0 ? LocalAutomationRunOutcome.Skipped : LocalAutomationRunOutcome.Interrupted, terminal.Outcome);
            Assert.AreEqual(run.Occurrence, terminal.Occurrence);
            Assert.IsNotNull(terminal.CompletedAtUtc);
            Assert.IsTrue(fixture.Worker.WhenIdleAsync().IsCompleted);
            Assert.AreEqual(LocalAutomationCommandStatus.Unchanged, (await fixture.Commands.StopAsync(request, default).ConfigureAwait(false)).Status);
        }
        finally { releaseDispatch.TrySetResult(); }
    }

    [TestMethod]
    public async Task RunNowRequiresOwnerBindsActorAndVersionReplaysAndPreservesSchedule()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        Assert.IsTrue((await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false)).IsSuccess);
        var due = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Definitions.Single().NextRunUtc;
        fixture.Authorized = false;
        Assert.AreEqual(OperatorUiResultKind.Unauthorized,
            (await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).Kind);
        Assert.IsEmpty((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs);
        fixture.Authorized = true;
        var stale = await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow() with { ExpectedVersion = 2 }, default).ConfigureAwait(false);
        Assert.AreEqual(OperatorUiResultKind.Conflict, stale.Kind);
        var applied = await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        Assert.IsTrue(applied.IsSuccess, applied.Message);
        var run = applied.Value!.State.Runs.Single();
        Assert.AreEqual("script-owner", run.Preparation!.Actor);
        Assert.AreEqual(LocalAutomationRunOutcome.Queued, run.Outcome);
        Assert.AreEqual(due, applied.Value.State.Definitions.Single().NextRunUtc);
        var replay = await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, replay.Value!.Status);
        Assert.AreEqual(run.RunKey, replay.Value.State.Runs.Single().RunKey);
        Assert.AreEqual(OperatorUiResultKind.Conflict,
            (await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow("other"), default).ConfigureAwait(false)).Kind);
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        await fixture.Worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single().Outcome);
    }

    [TestMethod]
    [DataRow(1L)]
    [DataRow(2L)]
    public async Task ScriptRunNowCannotCrossAConcurrentReplacementWithABuiltIn(long expectedVersion)
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var store = new Mock<ILocalAutomationStore>(MockBehavior.Strict);
        var occurrences = store.As<ILocalAutomationOccurrenceStore>();
        async ValueTask<LocalAutomationOperatorState> ReadThenReplaceAsync(CancellationToken token)
        {
            var oldState = await fixture.Store.GetStateAsync(token).ConfigureAwait(false);
            var replacement = await fixture.Store.SaveAsync(ScriptControlsFixture.Save("built-in") with
            { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, ExpectedVersion = 1, IdempotencyKey = "replace" }, token).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, replacement.Status);
            return oldState;
        }
        store.Setup(item => item.GetStateAsync(It.IsAny<CancellationToken>())).Returns(ReadThenReplaceAsync);
        occurrences.Setup(item => item.FindRunNowReplayAsync(It.IsAny<LocalAutomationRunNowRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LocalAutomationRunNowRequest request, CancellationToken token) => fixture.Store.FindRunNowReplayAsync(request, token));
        occurrences.Setup(item => item.RunNowAsync(It.IsAny<LocalAutomationRunNowRequest>(), It.IsAny<CancellationToken>()))
            .Returns((LocalAutomationRunNowRequest request, CancellationToken token) => fixture.Store.RunNowAsync(request, token));
        var commands = new LocalAutomationScriptCommands(store.Object, fixture.Worker);
        var result = await commands.RunNowAsync(ScriptControlsFixture.RunNow() with { ExpectedVersion = expectedVersion }, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, result.Status);
        Assert.AreEqual(LocalAutomationContract.ExpectedVersionConflictReasonCode, result.ReasonCode);
        var state = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
        Assert.AreEqual(2L, state.Definitions.Single().Version);
        Assert.AreEqual(LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, state.Definitions.Single().Definition.TaskKind);
        Assert.IsEmpty(state.Runs, "Neither a stale script revision nor a newer built-in revision may be queued through this script control.");
        occurrences.Verify(item => item.RunNowAsync(It.IsAny<LocalAutomationRunNowRequest>(), It.IsAny<CancellationToken>()),
            expectedVersion == 2 ? Times.Never() : Times.Once());
        var positiveControl = await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow("builtin-control") with
        { ExpectedVersion = 2 }, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, positiveControl.Status,
            "The fixture must prove the replacement really supports run-now through its owning store contract.");
        Assert.AreEqual(LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, positiveControl.State.Runs.Single().Occurrence!.Definition.TaskKind);
    }

    [TestMethod]
    public async Task ScriptControlsRejectBuiltInMalformedMissingAndUnavailableTargets()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        var saved = await fixture.Store.SaveAsync(ScriptControlsFixture.Save("built-in", "builtin") with
        { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition }, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
        var rejected = await fixture.Commands.RunNowAsync(ScriptControlsFixture.RunNow() with { DefinitionId = "builtin" }, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationScriptCommands.ScriptOnlyReason, rejected.ReasonCode);
        Assert.AreEqual(LocalAutomationCommandStatus.NotFound,
            (await fixture.Commands.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Invalid,
            (await fixture.Commands.StopAsync(new("script", "bad\nkey", 1, "owner"), default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.NotFound,
            (await fixture.Commands.StopAsync(new("script", "missing", 1, "owner"), default).ConfigureAwait(false)).Status);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var queued = await fixture.Commands.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied,
            (await fixture.Commands.StopAsync(new("script", queued.State.Runs.Single().RunKey, 1, "owner"), default).ConfigureAwait(false)).Status);
        var builtinQueued = await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow("builtin-now") with { DefinitionId = "builtin" }, default).ConfigureAwait(false);
        var pendingBuiltIn = builtinQueued.State.Runs.Single(run => run.DefinitionId == "builtin");
        Assert.AreEqual(LocalAutomationScriptCommands.ScriptOnlyReason,
            (await fixture.Commands.StopAsync(new("builtin", pendingBuiltIn.RunKey, 1, "owner"), default).ConfigureAwait(false)).ReasonCode);
        Assert.AreEqual(pendingBuiltIn, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single(run => run.RunKey == pendingBuiltIn.RunKey));
        var builtinEntry = (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single(item => item.Definition.DefinitionId == "builtin");
        var builtinRun = await fixture.Store.TryClaimQueuedAsync(builtinEntry, default).ConfigureAwait(false);
        Assert.IsNotNull(builtinRun);
        Assert.AreEqual(LocalAutomationScriptCommands.ScriptOnlyReason,
            (await fixture.Commands.StopAsync(new("builtin", builtinRun.RunKey, 1, "owner"), default).ConfigureAwait(false)).ReasonCode);
        fixture.Stub.Available = false;
        var unavailable = await fixture.Commands.RunNowAsync(ScriptControlsFixture.RunNow("unavailable"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict, unavailable.Status);
        Assert.AreEqual(LocalAutomationContract.RunNowUnavailableReasonCode, unavailable.ReasonCode);
        Assert.HasCount(2, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StopUsesRetainedIdentityAfterEditOrRemovalAndWaitsForDurableDrain(bool remove)
    {
        using var fixture = new ScriptControlsFixture();
        var entered = ScriptControlsFixture.Signal();
        var draining = ScriptControlsFixture.Signal();
        var release = ScriptControlsFixture.Signal();
        fixture.Stub.Execute = async (_, token) =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            draining.TrySetResult();
            await release.Task.ConfigureAwait(false);
            throw new LocalAutomationDrainedCancellationException(
                new(LocalAutomationRunOutcome.Interrupted, "script.cancelled; exit=137; stdoutBytes=2; stderrBytes=3"), fixture.Clock.GetUtcNow(), token);
        };
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var queued = await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        var run = queued.Value!.State.Runs.Single();
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            if (remove) await fixture.Store.RemoveAsync(new("script", 1, "remove", "owner", null), default).ConfigureAwait(false);
            else await fixture.Store.SaveAsync(ScriptControlsFixture.Save() with { ExpectedVersion = 1, IdempotencyKey = "edit", Name = "Changed" }, default).ConfigureAwait(false);
            fixture.Authorized = false;
            Assert.AreEqual(OperatorUiResultKind.Unauthorized,
                (await fixture.Service.StopScriptAsync(new("script", run.RunKey, 1, "spoof"), default).ConfigureAwait(false)).Kind);
            fixture.Authorized = true;
            Assert.AreEqual(LocalAutomationCommandStatus.Conflict,
                (await fixture.Commands.StopAsync(new("script", run.RunKey, 2, "owner"), default).ConfigureAwait(false)).Status);
            Assert.IsFalse(await fixture.Worker.StopScriptRunAsync("other-run", default).ConfigureAwait(false));
            var stop = fixture.Service.StopScriptAsync(new("script", run.RunKey, 1, "spoof"), default).AsTask();
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsFalse(stop.IsCompleted, "The command cannot acknowledge before drain and durable settlement.");
            Assert.IsNull(fixture.Worker.TryReserve(), "Authority/slot remain held during drain.");
            fixture.Clock.Advance(TimeSpan.FromSeconds(7));
            release.SetResult();
            var stopped = await stop.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsTrue(stopped.IsSuccess, stopped.Message);
            var terminal = stopped.Value!.State.Runs.Single();
            Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, terminal.Outcome);
            Assert.AreEqual(fixture.Clock.GetUtcNow(), terminal.CompletedAtUtc);
            Assert.AreEqual("script.cancelled; exit=137; stdoutBytes=2; stderrBytes=3", terminal.Detail);
            Assert.AreEqual(run.Occurrence, terminal.Occurrence);
            Assert.AreEqual(LocalAutomationCommandStatus.Unchanged,
                (await fixture.Commands.StopAsync(new("script", run.RunKey, 1, "owner"), default).ConfigureAwait(false)).Status);
            Assert.IsFalse(await fixture.Worker.StopScriptRunAsync(run.RunKey, default).ConfigureAwait(false));
            fixture.Runner.Dispose();
            fixture.Store.Dispose();
            fixture.Clock.Advance(TimeSpan.FromDays(1));
            using var reopened = fixture.NewStore();
            await reopened.InitializeAsync(default).ConfigureAwait(false);
            Assert.AreEqual(terminal, (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(3)]
    public async Task RetriedScriptStopNamesOnlyTheSavedAttemptAndPersistsTheDrainedResult(int attempt)
    {
        using var fixture = new ScriptControlsFixture();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = ScriptControlsFixture.Signal();
        var release = ScriptControlsFixture.Signal();
        var occurrences = new List<LocalAutomationOccurrence>();
        fixture.Stub.Execute = async (occurrence, token) =>
        {
            occurrences.Add(occurrence);
            if (occurrences.Count < attempt)
                return new(LocalAutomationRunOutcome.Failed, "script.completed; exit=17; stdoutBytes=0; stderrBytes=0");
            entered.SetResult(token);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            draining.SetResult();
            await release.Task.ConfigureAwait(false);
            throw new LocalAutomationDrainedCancellationException(
                new(LocalAutomationRunOutcome.Interrupted, "script.cancelled; exit=137; stdoutBytes=2; stderrBytes=3"), fixture.Clock.GetUtcNow(), token);
        };
        try
        {
            await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
            await fixture.Service.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
            var queued = await fixture.Service.RunScriptNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
            var original = queued.Value!.State.Runs.Single();
            var previousKey = original.RunKey;
            for (var current = 1; current < attempt; current++)
            {
                await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
                await fixture.Worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                var failed = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single(run => run.RunKey == previousKey);
                Assert.AreEqual(LocalAutomationRunOutcome.Failed, failed.Outcome);
                var retry = await fixture.Store.RetryAsync(new("script", previousKey, 1, "retry-" + current,
                    "script-owner", "Retry the retained script"), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, retry.Status);
                previousKey = original.RunKey + ":retry" + (current + 1);
            }
            await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
            var token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var runningState = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
            var running = runningState.ActiveRuns.Single();
            Assert.AreEqual(previousKey, running.RunKey);
            Assert.AreEqual(attempt, running.Attempt);
            Assert.AreEqual(LocalAutomationRunOutcome.Running, running.Outcome);
            Assert.AreEqual(original.Occurrence, running.Occurrence);
            Assert.IsTrue(occurrences.All(item => item == original.Occurrence), "Adapter inputs retain the original occurrence identity.");
            foreach (var stale in runningState.Runs.Where(run => run.RunKey != running.RunKey))
            {
                // Reproduce a stop that read Running just before the earlier attempt settled.
                var staleStore = new Mock<ILocalAutomationStore>(MockBehavior.Strict);
                staleStore.Setup(store => store.GetStateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(
                    runningState with { Runs = [stale with { Outcome = LocalAutomationRunOutcome.Running }], ActiveRuns = [] });
                var staleCommands = new LocalAutomationScriptCommands(staleStore.Object, fixture.Worker);
                using var staleDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var rejected = await staleCommands.StopAsync(new("script", stale.RunKey, 1, "owner"), staleDeadline.Token).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Conflict, rejected.Status);
                Assert.AreEqual(LocalAutomationScriptCommands.StopUnavailableReason, rejected.ReasonCode);
                Assert.IsFalse(token.IsCancellationRequested, "A stale attempt's stop must never cancel the live retry.");
            }
            CollectionAssert.AreEqual(runningState.Runs.ToArray(),
                (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.ToArray());
            var stopping = fixture.Service.StopScriptAsync(new("script", running.RunKey, 1, "spoof"), default).AsTask();
            await draining.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsFalse(stopping.IsCompleted, "Stop must await real drain and terminal persistence.");
            Assert.IsNull(fixture.Worker.TryReserve());
            fixture.Clock.Advance(TimeSpan.FromSeconds(7));
            release.SetResult();
            var stopped = await stopping.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            Assert.IsTrue(stopped.IsSuccess, stopped.Message);
            var terminal = stopped.Value!.State.Runs.Single(run => run.RunKey == running.RunKey);
            Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, terminal.Outcome);
            Assert.AreEqual(fixture.Clock.GetUtcNow(), terminal.CompletedAtUtc);
            Assert.AreEqual("script.cancelled; exit=137; stdoutBytes=2; stderrBytes=3", terminal.Detail);
            Assert.AreEqual(running.Occurrence, terminal.Occurrence);
            Assert.AreEqual(running.Preparation, terminal.Preparation);
            Assert.AreEqual(running.StartedAtUtc, terminal.StartedAtUtc);
            Assert.AreEqual(attempt, terminal.Attempt);
            foreach (var earlier in runningState.Runs.Where(run => run.RunKey != running.RunKey))
                Assert.AreEqual(earlier, stopped.Value.State.Runs.Single(run => run.RunKey == earlier.RunKey));
            Assert.IsEmpty(stopped.Value.State.ActiveRuns);
            fixture.Runner.Dispose();
            fixture.Store.Dispose();
            fixture.Clock.Advance(TimeSpan.FromDays(1));
            using var reopened = fixture.NewStore();
            await reopened.InitializeAsync(default).ConfigureAwait(false);
            CollectionAssert.AreEqual(stopped.Value.State.Runs.ToArray(),
                (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.ToArray());
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task ActiveScriptIsStillVisibleBeyondTheRecentHistoryPage()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var queued = await fixture.Commands.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        var entry = (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single();
        var active = await fixture.Store.TryClaimQueuedAsync(entry, default).ConfigureAwait(false);
        Assert.IsNotNull(active);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save("built-in", "builtin") with
        { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition }, default).ConfigureAwait(false);
        var builtin = (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single(item => item.Definition.DefinitionId == "builtin");
        for (var i = 0; i <= LocalAutomationContract.MaximumProjectedRuns; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await fixture.Store.RecordTerminalRunAsync(builtin, "builtin-" + i, fixture.Clock.GetUtcNow(),
                LocalAutomationRunOutcome.Succeeded, "done", null, default).ConfigureAwait(false);
        }
        var state = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
        Assert.HasCount(LocalAutomationContract.MaximumProjectedRuns, state.Runs);
        Assert.IsFalse(state.Runs.Any(item => item.RunKey == active.RunKey));
        Assert.AreEqual(queued.State.Runs.Single().Occurrence, state.ActiveRuns.Single().Occurrence);
        Assert.AreEqual(active.RunKey, state.ActiveRuns.Single().RunKey);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict,
            (await fixture.Commands.StopAsync(new("script", active.RunKey, 1, "owner"), default).ConfigureAwait(false)).Status,
            "A foreign/absent worker must not acknowledge or cancel another run.");
    }
}
