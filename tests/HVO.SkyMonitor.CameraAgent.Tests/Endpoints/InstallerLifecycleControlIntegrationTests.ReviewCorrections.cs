extern alias InstallerRuntime;

using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.Deployment.Contracts;
using Fixture = HVO.SkyMonitor.CameraAgent.Tests.Endpoints.InstallerLifecycleControlIntegrationTests.Fixture;
using InstallerControlJournal = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlJournal;
using InstallerControlHttpClient = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlHttpClient;
using InstallerControlVerifier = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlVerifier;
using InstallerControlBudgets = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlBudgets;
using InstallerControlWorker = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlWorker;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

public sealed partial class InstallerLifecycleRecoveryIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HeartbeatOutageCannotSettleAnOutstandingPause(bool interruptDrain)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        fixture.WindowOpening = fixture.WindowClock.GetUtcNow().AddMinutes(5);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(watchdog.Token);
        var journal = new InstallerControlJournal(Path.Combine(fixture.Root.FullName, "installer.json"));
        await journal.LoadAsync(watchdog.Token).ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://127.0.0.1"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, watchdog.Token).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        using var exposure = await fixture.Capture.EnterAsync(watchdog.Token).ConfigureAwait(false);
        var outage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.BeforeExchange = _ =>
        {
            if (fixture.Capture.Snapshot.State == CaptureAdmissionState.PauseRequested && outage.TrySetResult())
                throw new HttpRequestException("Injected heartbeat transport outage during an admitted exposure.");
            return Task.CompletedTask;
        };
        var pauseSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.AfterPause = () =>
        {
            fixture.WindowClock.Advance(TimeSpan.FromMinutes(4));
            pauseSettled.TrySetResult();
        };
        var execution = new InstallerControlVerifier(client, journal.SaveAsync, TimeProvider.System,
            new InstallerControlBudgets(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10)))
            .ExecuteAsync(queued, executionCancellation.Token);
        try
        {
            await outage.Task.WaitAsync(watchdog.Token).ConfigureAwait(false);
            Assert.AreEqual(CaptureAdmissionState.PauseRequested, fixture.Capture.Snapshot.State);
            if (interruptDrain)
            {
                await executionCancellation.CancelAsync().ConfigureAwait(false);
                var pending = await execution.WaitAsync(watchdog.Token).ConfigureAwait(false);
                Assert.IsFalse(pending.IsTerminal, "An outstanding durable pause must remain recoverable.");
                await new InstallerControlWorker(client, journal, _ => Task.FromResult<IDisposable>(new TestDeploymentLease()),
                    TimeProvider.System, _ => { }).PollAsync(watchdog.Token).ConfigureAwait(false);
                Assert.IsFalse(journal.Receipts.Single().IsTerminal);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<TimeoutException>(() => execution.WaitAsync(TimeSpan.FromMilliseconds(150)))
                    .ConfigureAwait(false);
            }
            Assert.IsTrue(fixture.Controls.AdmissionHeld);
            exposure.MarkPublished();
            exposure.Dispose();
            if (!interruptDrain) await execution.WaitAsync(watchdog.Token).ConfigureAwait(false);
            for (var poll = 0; poll < 100 && (!journal.Receipts.Single().IsTerminal || journal.Unpublished.Any()); poll++)
            {
                journal = new InstallerControlJournal(Path.Combine(fixture.Root.FullName, "installer.json"));
                await journal.LoadAsync(watchdog.Token).ConfigureAwait(false);
                await new InstallerControlWorker(client, journal, _ => Task.FromResult<IDisposable>(new TestDeploymentLease()),
                    TimeProvider.System, _ => { }).PollAsync(watchdog.Token).ConfigureAwait(false);
                if (!journal.Receipts.Single().IsTerminal) await Task.Delay(10, watchdog.Token).ConfigureAwait(false);
            }
            var terminal = journal.Receipts.Single();
            Assert.IsTrue(terminal.IsTerminal);
            Assert.IsTrue(terminal.ResumeRequested);
            Assert.IsTrue(terminal.ResumeConfirmed);
            Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
            Assert.IsFalse(fixture.Controls.AdmissionHeld);
            Assert.IsEmpty(journal.Unpublished);
            Assert.IsEmpty(journal.Refused);
            Assert.AreEqual(0, fixture.RestartCalls);
            Assert.AreEqual(1, (await fixture.Capture.GetRecentCommandsAsync(10, watchdog.Token).ConfigureAwait(false))
                .Count(command => command.TargetState == CaptureAdmissionState.Running));
        }
        finally
        {
            exposure.MarkPublished();
            exposure.Dispose();
            await executionCancellation.CancelAsync().ConfigureAwait(false);
            await execution.WaitAsync(watchdog.Token).ConfigureAwait(false);
            await pauseSettled.Task.WaitAsync(watchdog.Token).ConfigureAwait(false);
        }
    }
}
