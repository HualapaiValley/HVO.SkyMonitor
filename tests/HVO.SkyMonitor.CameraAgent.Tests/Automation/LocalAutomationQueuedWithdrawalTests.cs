using HVO.SkyMonitor.CameraAgent.Common.Automation;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

// Ports F5 R7 and Sol V4's six stub-only characterization rows, with defect assertions inverted.
// Refs #1782. Queued Stop is tested separately so it cannot mask definition withdrawal.
[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class LocalAutomationQueuedWithdrawalTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task R7_DisableOrRemovalWithdrawsBeforeReenableAndReopen(bool remove)
    {
        using var fixture = new ScriptControlsFixture();
        var executions = Record(fixture);
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var original = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        var changed = remove
            ? await fixture.Store.RemoveAsync(new("script", 1, "remove", "owner", null), default).ConfigureAwait(false)
            : await fixture.Store.SaveAsync(ScriptControlsFixture.Save() with
            { Enabled = false, ExpectedVersion = 1, IdempotencyKey = "disable" }, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, changed.Status);
        var withdrawn = changed.State.Runs.Single();
        AssertWithdrawal(original, withdrawn, remove ? "removed" : "disabled");
        // No sweep between disable and re-enable: settlement must be in the mutation itself.
        var restored = await fixture.Store.SaveAsync(ScriptControlsFixture.Save() with
        { ExpectedVersion = remove ? 0 : 2, IdempotencyKey = "restore", Name = "Restored" }, default).ConfigureAwait(false);
        Assert.AreEqual(3L, restored.State.Definitions.Single().Version);
        await Sweep(fixture).ConfigureAwait(false);
        Assert.IsEmpty(executions);
        fixture.Runner.Dispose();
        fixture.Store.Dispose();
        fixture.Clock.Advance(TimeSpan.FromDays(1));
        using var reopened = fixture.NewStore();
        await reopened.InitializeAsync(default).ConfigureAwait(false);
        var entry = (await reopened.GetRunnerViewAsync(default).ConfigureAwait(false)).Single();
        Assert.IsNull(await reopened.TryClaimQueuedAsync(entry, default).ConfigureAwait(false));
        Assert.AreEqual(withdrawn, (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EditWithoutDisableWithdrawsOldOccurrenceIncludingKindReplacement(bool replaceWithBuiltIn)
    {
        using var fixture = new ScriptControlsFixture();
        var executed = Record(fixture);
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var original = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        var edit = ScriptControlsFixture.Save() with { ExpectedVersion = 1, IdempotencyKey = "edit", Name = "Edited" };
        if (replaceWithBuiltIn) edit = edit with { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, TaskTarget = "built-in" };
        var saved = await fixture.Store.SaveAsync(edit, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
        AssertWithdrawal(original, saved.State.Runs.Single(), "edited");
        await Sweep(fixture).ConfigureAwait(false);
        Assert.IsEmpty(executed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BuiltInRunNowOrRetryIsWithdrawnOnDisable(bool retry)
    {
        using var fixture = new ScriptControlsFixture();
        var executed = Record(fixture, retry);
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        var save = ScriptControlsFixture.Save("built-in", "builtin") with { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition };
        await fixture.Store.SaveAsync(save, default).ConfigureAwait(false);
        var original = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow() with { DefinitionId = "builtin" }, default).ConfigureAwait(false)).State.Runs.Single();
        var pending = original;
        LocalAutomationRun? terminal = null;
        if (retry)
        {
            await Sweep(fixture).ConfigureAwait(false);
            terminal = (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
            var result = await fixture.Store.RetryAsync(new("builtin", original.RunKey, 1, "retry", "owner", "retry retained"), default).ConfigureAwait(false);
            pending = result.State.Runs.Single(run => run.Outcome == LocalAutomationRunOutcome.Queued);
            Assert.AreEqual(2, pending.Attempt);
        }
        var disabled = await fixture.Store.SaveAsync(save with { ExpectedVersion = 1, Enabled = false, IdempotencyKey = "disable" }, default).ConfigureAwait(false);
        AssertWithdrawal(pending, disabled.State.Runs.Single(run => run.RunKey == pending.RunKey), "disabled");
        await fixture.Store.SaveAsync(save with { ExpectedVersion = 2, IdempotencyKey = "restore" }, default).ConfigureAwait(false);
        await Sweep(fixture).ConfigureAwait(false);
        Assert.HasCount(retry ? 1 : 0, executed);
        if (terminal is not null) Assert.AreEqual(terminal, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single(run => run.RunKey == terminal.RunKey));
    }

    [TestMethod]
    public async Task DefinitionWriteFailureRollsBackWithdrawalAndPreservesAuthorizationAcrossReopen()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        var queued = await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false);
        var original = queued.State.Runs.Single();
        using (var connection = new SqliteConnection("Data Source=" + fixture.DatabasePath))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER reject_definition_write BEFORE UPDATE ON automation_definitions
                BEGIN SELECT RAISE(ABORT, 'Injected definition write failure'); END;
                """;
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            try
            {
                await Assert.ThrowsExactlyAsync<SqliteException>(() => fixture.Store.SaveAsync(ScriptControlsFixture.Save() with
                { ExpectedVersion = 1, IdempotencyKey = "disable", Enabled = false }, default).AsTask()).ConfigureAwait(false);
                var unchanged = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
                Assert.AreEqual(original, unchanged.Runs.Single());
                Assert.AreEqual(1L, unchanged.Definitions.Single().Version);
                Assert.IsTrue(unchanged.Definitions.Single().Definition.Enabled);
            }
            finally
            {
                command.CommandText = "DROP TRIGGER reject_definition_write;";
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }
        fixture.Runner.Dispose();
        fixture.Store.Dispose();
        using var reopened = fixture.NewStore();
        await reopened.InitializeAsync(default).ConfigureAwait(false);
        Assert.AreEqual(original, (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
        var disabled = await reopened.SaveAsync(ScriptControlsFixture.Save() with
        { ExpectedVersion = 1, IdempotencyKey = "disable", Enabled = false }, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, disabled.Status, "The failed transaction must not consume its command key.");
        AssertWithdrawal(original, disabled.State.Runs.Single(), "disabled");
    }

    [TestMethod]
    public async Task NoOpRejectedAndReplayedSavesPreserveNewQueuedAuthorizationAndSchedule()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        var save = ScriptControlsFixture.Save();
        await fixture.Store.SaveAsync(save, default).ConfigureAwait(false);
        var edit = save with { ExpectedVersion = 1, IdempotencyKey = "edit", Name = "Edited" };
        await fixture.Store.SaveAsync(edit, default).ConfigureAwait(false);
        var queueRequest = ScriptControlsFixture.RunNow() with { ExpectedVersion = 2 };
        var queued = await fixture.Store.RunNowAsync(queueRequest, default).ConfigureAwait(false);
        var before = (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single();
        Assert.AreEqual(LocalAutomationCommandStatus.Unchanged,
            (await fixture.Store.SaveAsync(edit with { ExpectedVersion = 2, IdempotencyKey = "noop" }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Conflict,
            (await fixture.Store.SaveAsync(edit with { IdempotencyKey = "stale", Enabled = false }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Invalid,
            (await fixture.Store.SaveAsync(edit with { ExpectedVersion = 2, IdempotencyKey = "invalid", TriggerInterval = 0 }, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, (await fixture.Store.SaveAsync(edit, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(LocalAutomationCommandStatus.Replayed, (await fixture.Store.RunNowAsync(queueRequest, default).ConfigureAwait(false)).Status);
        Assert.AreEqual(queued.State.Runs.Single(), (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
        Assert.AreEqual(before, (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single());
        await Sweep(fixture).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single().Outcome);
    }

    [TestMethod]
    public async Task KindChangingEditWithdrawsEveryQueuedRetryAndPreservesTerminalHistory()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        fixture.Stub.Execute = (_, _) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Failed, "failed"));
        var failed = new List<LocalAutomationRun>();
        for (var index = 0; index < 2; index++)
        {
            // Multiple periodic occurrences may be retried together; run-now deliberately permits
            // only one pending attempt per definition, including retries of different run-now keys.
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await Sweep(fixture).ConfigureAwait(false);
            failed.Add((await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs[0]);
        }
        foreach (var run in failed)
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, (await fixture.Store.RetryAsync(new("script", run.RunKey, 1, "retry-" + run.RunKey, "owner", "retry"), default).ConfigureAwait(false)).Status);
        var before = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
        Assert.AreEqual(2, before.QueuedRunCount);
        var edit = await fixture.Store.SaveAsync(ScriptControlsFixture.Save("built-in") with
        { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, ExpectedVersion = 1, IdempotencyKey = "replace" }, default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, edit.Status);
        foreach (var run in before.Runs.Where(run => run.Outcome == LocalAutomationRunOutcome.Queued))
            AssertWithdrawal(run, edit.State.Runs.Single(item => item.RunKey == run.RunKey), "edited");
        foreach (var run in failed) Assert.AreEqual(run, edit.State.Runs.Single(item => item.RunKey == run.RunKey));
        var executed = Record(fixture);
        await Sweep(fixture).ConfigureAwait(false);
        Assert.IsEmpty(executed);
    }

    [TestMethod]
    public async Task HistoricalBuiltInRetryAfterScriptReplacementDoesNotNeedTheBusyScriptSlot()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save("built-in") with
        { TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition }, default).ConfigureAwait(false);
        fixture.Stub.Execute = (_, _) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Failed, "failed"));
        var original = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        await Sweep(fixture).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save() with
        { ExpectedVersion = 1, IdempotencyKey = "replace" }, default).ConfigureAwait(false);
        var retry = await fixture.Store.RetryAsync(new("script", original.RunKey, 2, "retry", "owner", "retained built-in"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, retry.Status);
        using var busy = fixture.Worker.TryReserve();
        Assert.IsNotNull(busy);
        var executed = Record(fixture);
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        Assert.AreEqual(original.Occurrence, executed.Single());
        var state = await fixture.Store.GetStateAsync(default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded, state.Runs.Single(run => run.Attempt == 2).Outcome);
        Assert.AreEqual(LocalAutomationScriptCommands.ScriptOnlyReason,
            (await fixture.Commands.StopAsync(new("script", original.RunKey + ":retry2", 1, "owner"), default).ConfigureAwait(false)).ReasonCode);
    }

    [TestMethod]
    public async Task HistoricalScriptRetryAfterKindChangeUsesRetainedAdmissionAndStopAuthority()
    {
        using var fixture = new ScriptControlsFixture();
        await fixture.Store.InitializeAsync(default).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save(), default).ConfigureAwait(false);
        fixture.Stub.Execute = (_, _) => ValueTask.FromResult(new LocalAutomationExecution(LocalAutomationRunOutcome.Failed, "failed"));
        var original = (await fixture.Store.RunNowAsync(ScriptControlsFixture.RunNow(), default).ConfigureAwait(false)).State.Runs.Single();
        await Sweep(fixture).ConfigureAwait(false);
        await fixture.Store.SaveAsync(ScriptControlsFixture.Save("built-in") with
        { ExpectedVersion = 1, IdempotencyKey = "replace", TaskKind = LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition }, default).ConfigureAwait(false);
        var retry = await fixture.Store.RetryAsync(new("script", original.RunKey, 2, "retry", "owner", "retained script"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, retry.Status);
        var current = (await fixture.Store.GetRunnerViewAsync(default).ConfigureAwait(false)).Single();
        Assert.IsNull(await fixture.Store.TryClaimQueuedWithAdmissionAsync(current, false, default).ConfigureAwait(false));
        Assert.AreEqual(LocalAutomationRunOutcome.Queued, (await fixture.Store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single(run => run.Attempt == 2).Outcome);
        var entered = ScriptControlsFixture.Signal();
        fixture.Stub.Execute = async (occurrence, token) =>
        {
            Assert.AreEqual(original.Occurrence, occurrence);
            using var unexpected = fixture.Worker.TryReserve();
            Assert.IsNull(unexpected, "Historical script execution must hold the off-sweep slot.");
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return new(LocalAutomationRunOutcome.Succeeded, "unreachable");
        };
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var stopped = await fixture.Commands.StopAsync(new("script", original.RunKey + ":retry2", 1, "owner"), default).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationCommandStatus.Applied, stopped.Status);
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, stopped.State.Runs.Single(run => run.Attempt == 2).Outcome);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, stopped.State.Runs.Single(run => run.Attempt == 1).Outcome);
    }

    internal static void AssertWithdrawal(LocalAutomationRun original, LocalAutomationRun withdrawn, string cause)
    {
        Assert.AreEqual(LocalAutomationRunOutcome.Skipped, withdrawn.Outcome);
        Assert.IsNull(withdrawn.StartedAtUtc);
        Assert.IsNotNull(withdrawn.CompletedAtUtc);
        Assert.AreEqual($"The definition was {cause} before this prepared occurrence started.", withdrawn.Detail);
        Assert.AreEqual(original with { Outcome = withdrawn.Outcome, CompletedAtUtc = withdrawn.CompletedAtUtc, Detail = withdrawn.Detail }, withdrawn);
    }

    private static List<LocalAutomationOccurrence> Record(ScriptControlsFixture fixture, bool failFirst = false)
    {
        var executed = new List<LocalAutomationOccurrence>();
        fixture.Stub.Execute = (occurrence, _) =>
        {
            executed.Add(occurrence);
            return ValueTask.FromResult(new LocalAutomationExecution(failFirst && executed.Count == 1
                ? LocalAutomationRunOutcome.Failed : LocalAutomationRunOutcome.Succeeded, "stub.completed"));
        };
        return executed;
    }

    private static async Task Sweep(ScriptControlsFixture fixture)
    {
        await fixture.Runner.SweepAsync(default).ConfigureAwait(false);
        await fixture.Worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }
}
