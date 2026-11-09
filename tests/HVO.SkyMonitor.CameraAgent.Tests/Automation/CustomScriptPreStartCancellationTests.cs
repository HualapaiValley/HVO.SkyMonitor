using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class CustomScriptPreStartCancellationTests
{
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ScriptCancellationAtTheExecutionBoundaryRetainsActualTerminalEvidenceAfterRestart(bool beforeLaunch)
    {
        var root = Path.Combine(Path.GetTempPath(), "hvo-script-prestart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 9, 12, 0, 0, TimeSpan.Zero));
        var registry = new PausedRegistry(beforeLaunch);
        var captures = new StubCaptureSequenceSource();
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root });
        SqliteLocalAutomationStore NewStore() => new(registry, captures, options, clock, NullLogger<SqliteLocalAutomationStore>.Instance);
        LocalAutomationRun terminal;
        try
        {
            using (var store = NewStore())
            using (var worker = new LocalAutomationOffSweepWorker())
            using (var runner = new LocalAutomationRunnerService(store, registry, captures, options, clock,
                NullLogger<LocalAutomationRunnerService>.Instance, offSweepWorker: worker))
            {
                await store.InitializeAsync(default).ConfigureAwait(false);
                var saved = await store.SaveAsync(new("script", "Script", true, LocalAutomationTaskKind.CustomScript,
                    "synthetic-target", LocalAutomationTriggerKind.Periodic, 60, 0, "save", "owner", null), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
                var queued = await store.RunNowAsync(new("script", 1, "run", "owner", "test"), default).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, queued.Status);
                using var cancel = new CancellationTokenSource();
                await runner.SweepAsync(cancel.Token).ConfigureAwait(false);
                try
                {
                    await registry.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    Assert.AreEqual(LocalAutomationRunOutcome.Running, (await store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single().Outcome);
                    clock.Advance(TimeSpan.FromSeconds(7));
                    await cancel.CancelAsync().ConfigureAwait(false);
                }
                finally { registry.Release.TrySetResult(); }
                await worker.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                terminal = (await store.GetStateAsync(default).ConfigureAwait(false)).Runs.Single();
                Assert.AreEqual(beforeLaunch ? LocalAutomationRunOutcome.Interrupted : LocalAutomationRunOutcome.Succeeded, terminal.Outcome);
                Assert.AreEqual(clock.GetUtcNow(), terminal.CompletedAtUtc);
                Assert.AreEqual(beforeLaunch ? "script.cancelled-before-start; exit=; stdoutBytes=0; stderrBytes=0" :
                    "script.completed; exit=0; stdoutBytes=2; stderrBytes=0", terminal.Detail);
                Assert.AreEqual(queued.State.Runs.Single().Occurrence, terminal.Occurrence);
                Assert.AreEqual(!beforeLaunch, registry.Launched, "Pre-start cancellation must not launch an adapter/process.");
            }
            clock.Advance(TimeSpan.FromDays(1));
            using var reopened = NewStore();
            await reopened.InitializeAsync(default).ConfigureAwait(false);
            Assert.AreEqual(terminal, (await reopened.GetStateAsync(default).ConfigureAwait(false)).Runs.Single());
        }
        finally
        {
            registry.Release.TrySetResult();
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private sealed class PausedRegistry(bool beforeLaunch) : ILocalAutomationTaskRegistry
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Launched { get; private set; }
        public IReadOnlyList<LocalAutomationTaskDescriptor> Describe() =>
            [new(LocalAutomationTaskKind.CustomScript, "Synthetic", [LocalAutomationTriggerKind.Periodic], ["synthetic-target"], true, null)];
        public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition) => null;
        public bool RunsOffSweep(LocalAutomationDefinition definition) => true;
        public bool SupportsRunNow(LocalAutomationDefinition definition) => true;
        public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationDefinition definition, string runKey, CancellationToken cancellationToken)
            => throw new InvalidOperationException("The durable occurrence must be retained.");
        public async ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            if (beforeLaunch) cancellationToken.ThrowIfCancellationRequested();
            Launched = true;
            return new(LocalAutomationRunOutcome.Succeeded, "script.completed; exit=0; stdoutBytes=2; stderrBytes=0");
        }
    }
}
