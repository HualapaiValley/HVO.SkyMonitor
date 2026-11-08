using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Automation;

[TestClass]
[TestCategory("Unit")]
public sealed class InstallerLifecycleAutomationTests
{
    [TestMethod]
    [DataRow(CameraAgentControlPhase.Succeeded, LocalAutomationRunOutcome.Succeeded)]
    [DataRow(CameraAgentControlPhase.Failed, LocalAutomationRunOutcome.Failed)]
    [DataRow(CameraAgentControlPhase.Cancelled, LocalAutomationRunOutcome.Interrupted)]
    [DataRow(CameraAgentControlPhase.Skipped, LocalAutomationRunOutcome.Skipped)]
    public async Task DelegatedRunSurvivesRestartAndSettlesOnlyFromItsInstallerReceipt(
        CameraAgentControlPhase phase, LocalAutomationRunOutcome expected)
    {
        var root = Directory.CreateTempSubdirectory("hvo-1711-automation-");
        try
        {
            var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));
            var adapter = new FakePeriodicTaskAdapter(LocalAutomationTaskKind.ScheduledCameraAgentRestart) { MinimumIntervalSeconds = 3600 };
            using var registry = new PeriodicRegistryHarness(clock, [adapter]);
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root.FullName });
            var sequence = new StubCaptureSequenceSource();
            SqliteLocalAutomationStore Open() => new(registry.Registry, sequence, options, clock, NullLogger<SqliteLocalAutomationStore>.Instance);
            using (var store = Open())
            {
                await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
                var saved = await store.SaveAsync(new(CameraAgentRestartAutomationAdapter.DefinitionId, "Scheduled restart", true,
                    LocalAutomationTaskKind.ScheduledCameraAgentRestart, FakePeriodicTaskAdapter.Target,
                    LocalAutomationTriggerKind.Periodic, 3600, 0, "save", "owner", null), CancellationToken.None).ConfigureAwait(false);
                Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
                var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
                Assert.IsTrue(await store.TryBeginRunAsync(entry, "installer-restart-run", clock.GetUtcNow(), null, CancellationToken.None).ConfigureAwait(false));
            }
            using var restarted = Open();
            await restarted.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var run = (await restarted.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single();
            Assert.AreEqual(LocalAutomationRunOutcome.Running, run.Outcome, "CameraAgent restart must not falsely settle the installer's pending run.");
            var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart,
                "automation:cameraagent-restart", "Scheduled restart", 7, clock.GetUtcNow(), run.RunKey);
            var receipt = new CameraAgentControlReceipt(request, phase,
                [new(CameraAgentControlPhase.Queued, clock.GetUtcNow(), "lifecycle.queued"), new(phase, clock.GetUtcNow(), "installer.terminal")]);
            await restarted.CompleteInstallerLifecycleAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            var settled = (await restarted.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single();
            Assert.AreEqual(expected, settled.Outcome);
            Assert.IsNotNull(settled.CompletedAtUtc);
            // Publication retries cannot rewrite a terminal history row.
            await restarted.CompleteInstallerLifecycleAsync(receipt with { Phase = CameraAgentControlPhase.Failed }, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(expected, (await restarted.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        }
        finally { root.Delete(true); }
    }

    [TestMethod]
    public async Task OrdinaryAutomationCannotBeSettledByAnInstallerLifecycleReceipt()
    {
        var root = Directory.CreateTempSubdirectory("hvo-1711-unrelated-run-");
        try
        {
            var registry = new StubAutomationTaskRegistry();
            var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
            using var store = new SqliteLocalAutomationStore(registry, new StubCaptureSequenceSource(),
                Options.Create(new CameraAgentHostOptions { RawIngressRoot = root.FullName }), clock, NullLogger<SqliteLocalAutomationStore>.Instance);
            await store.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            await store.SaveAsync(new("environment", "Observation", true, LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition,
                "virtual-sky-temperature", LocalAutomationTriggerKind.Periodic, 3600, 0, "save", "owner", null), CancellationToken.None).ConfigureAwait(false);
            var entry = (await store.GetRunnerViewAsync(CancellationToken.None).ConfigureAwait(false)).Single();
            await store.TryBeginRunAsync(entry, "ordinary-run", clock.GetUtcNow(), null, CancellationToken.None).ConfigureAwait(false);
            var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "installer", "reason", 0,
                clock.GetUtcNow(), "ordinary-run");
            await store.CompleteInstallerLifecycleAsync(new(request, CameraAgentControlPhase.Failed,
                [new(CameraAgentControlPhase.Failed, clock.GetUtcNow(), "lifecycle.failed")]), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationRunOutcome.Running, (await store.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        }
        finally { root.Delete(true); }
    }
}
