extern alias InstallerRuntime;

using System.Runtime.Versioning;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Fixture = HVO.SkyMonitor.CameraAgent.Tests.Endpoints.InstallerLifecycleControlIntegrationTests.Fixture;
using InstallerControlJournal = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlJournal;
using InstallerControlWorker = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlWorker;
using InstallerControlHttpClient = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlHttpClient;
using InstallerControlVerifier = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerControlVerifier;
using InstallerException = InstallerRuntime::HVO.SkyMonitor.Deployment.InstallerException;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Integration")]
[OSCondition(OperatingSystems.Linux)]
[SupportedOSPlatform("linux")]
public sealed class InstallerLifecycleRecoveryIntegrationTests
{
    [TestMethod]
    [DataRow("before", false)]
    [DataRow("lost", false)]
    [DataRow("lost", true)]
    public async Task RealResumeTransportRetriesAcrossWorkerReloadWithoutRepeatingTheNativeEffect(string fault, bool laterOwnerPause)
    {
        var fixture = new Fixture(false) { LoseResumeResponse = fault == "lost" };
        await using var owned = fixture.ConfigureAwait(false);
        fixture.WindowOpening = fixture.WindowClock.GetUtcNow().AddMinutes(5);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var path = Path.Combine(fixture.Root.FullName, "installer.json");
        var journal = new InstallerControlJournal(path);
        await journal.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        fixture.AfterPause = () => fixture.WindowClock.Advance(TimeSpan.FromMinutes(4));
        var failures = fault == "before" ? 3 : 0;
        fixture.BeforeResume = () => failures-- > 0
            ? throw new HttpRequestException("Injected native resume transport outage.") : Task.CompletedTask;
        var pending = await new InstallerControlVerifier(client, journal.SaveAsync, TimeProvider.System)
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.IsFalse(pending.IsTerminal);
        Assert.IsTrue(pending.ResumeRequested);
        Assert.IsFalse(pending.ResumeConfirmed);
        Assert.IsTrue(fixture.Controls.AdmissionHeld);
        var expectedVersion = pending.PausedVersion!.Value + 1;
        if (laterOwnerPause)
            expectedVersion = (await fixture.Capture.PauseAsync("owner-after-unconfirmed-restore", fixture.Capture.Snapshot.Version,
                "owner", "Keep this newer pause", CancellationToken.None).ConfigureAwait(false)).Version;
        for (var poll = 0; poll < 3; poll++)
        {
            journal = new InstallerControlJournal(path);
            await journal.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            await new InstallerControlWorker(client, journal, _ => Task.FromResult<IDisposable>(new TestDeploymentLease()),
                TimeProvider.System, _ => { }).PollAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var terminal = journal.Receipts.Single();
        Assert.IsTrue(terminal.IsTerminal);
        Assert.IsTrue(terminal.ResumeConfirmed);
        Assert.IsEmpty(journal.Unpublished);
        Assert.IsEmpty(journal.Refused);
        Assert.IsFalse(fixture.Controls.AdmissionHeld);
        Assert.AreEqual(laterOwnerPause ? CaptureAdmissionState.Paused : CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        Assert.AreEqual(expectedVersion, fixture.Capture.Snapshot.Version);
        Assert.AreEqual(1, (await fixture.Capture.GetRecentCommandsAsync(10, CancellationToken.None).ConfigureAwait(false))
            .Count(command => command.TargetState == CaptureAdmissionState.Running));
        Assert.AreEqual(0, fixture.RestartCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SettlementPauseUnavailableRetriesAndConcurrentOwnerPauseIsAccepted(bool ownerPause)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var worker = Guid.NewGuid();
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", worker, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "owner", "maintenance",
            fixture.Capture.Snapshot.Version, DateTimeOffset.UtcNow);
        var queued = await fixture.Controls.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
        var draining = queued with
        {
            Phase = CameraAgentControlPhase.Draining,
            OriginalProcessId = fixture.Controls.ProcessId,
            ResumeAfterVerification = true,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, DateTimeOffset.UtcNow, "lifecycle.draining")]
        };
        await client.ExchangeAsync(draining, CancellationToken.None).ConfigureAwait(false);
        var pause = await client.DrainAsync(request, CancellationToken.None).ConfigureAwait(false);
        draining = draining with
        {
            PausedVersion = pause.CaptureVersion,
            DrainedCaptureSequence = pause.CaptureSequence,
            VerifiedProcessId = fixture.Controls.ProcessId,
            ResumeRequested = true
        };
        await client.ExchangeAsync(draining, CancellationToken.None).ConfigureAwait(false);
        await client.ResumeAsync(draining, CancellationToken.None).ConfigureAwait(false);
        var failed = draining with
        {
            Phase = CameraAgentControlPhase.Failed,
            Events = [.. draining.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "restart.resume-unconfirmed")]
        };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var boundary = fixture.Capture.ExecuteCaptureBoundaryAsync(async token =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);
            return true;
        }, watchdog.Token);
        await entered.Task.WaitAsync(watchdog.Token).ConfigureAwait(false);
        try
        {
            var owner = ownerPause ? fixture.Capture.PauseAsync("owner-settlement-race", fixture.Capture.Snapshot.Version,
                "owner", "Keep capture paused", watchdog.Token) : null;
            // Both calls reach the coordinator's command gate before the exclusive boundary is released.
            var settlement = client.ExchangeAsync(failed, watchdog.Token);
            Assert.IsFalse(settlement.IsCompleted);
            if (!ownerPause) fixture.Capture.FailClosed();
            release.SetResult();
            await boundary.ConfigureAwait(false);
            if (owner is not null) await owner.ConfigureAwait(false);
            if (ownerPause)
                await settlement.ConfigureAwait(false);
            else
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => settlement).ConfigureAwait(false);
                Assert.IsTrue(fixture.Controls.AdmissionHeld);
                Assert.IsFalse((await fixture.Controls.ReadAsync(watchdog.Token).ConfigureAwait(false)).Receipts.Single().IsTerminal);
                await fixture.ReloadAsync().ConfigureAwait(false);
                await client.ExchangeAsync(failed, watchdog.Token).ConfigureAwait(false);
            }
            Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
            Assert.IsFalse(fixture.Controls.AdmissionHeld);
            Assert.IsTrue((await fixture.Controls.ReadAsync(watchdog.Token).ConfigureAwait(false)).Receipts.Single().IsTerminal);
            Assert.AreEqual(0, fixture.RestartCalls);
        }
        finally
        {
            release.TrySetResult();
            await boundary.ConfigureAwait(false);
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RestartingPublicationFailureRestoresOnlyItsOwnedPauseWithoutSendingStop(bool timeout, bool originallyPaused)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var journal = new InstallerControlJournal(Path.Combine(fixture.Root.FullName, "installer.json"));
        await journal.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        if (originallyPaused)
            await fixture.Capture.PauseAsync("original-owner-pause", fixture.Capture.Snapshot.Version, "owner", "Preserve this pause", CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        var failedPublication = false;
        fixture.BeforeExchange = receipt =>
        {
            if (receipt?.Phase != CameraAgentControlPhase.Restarting || failedPublication) return Task.CompletedTask;
            failedPublication = true;
            if (timeout) throw new TimeoutException("Injected failure before the stop call.");
            throw new HttpRequestException("Injected failure before the stop call.");
        };
        var terminal = await new InstallerControlVerifier(client, journal.SaveAsync, TimeProvider.System)
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(failedPublication);
        Assert.IsTrue(terminal.RestartNotIssued);
        Assert.IsFalse(terminal.RestartMayHaveOccurred);
        Assert.AreEqual(CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.AreEqual(0, fixture.RestartCalls);
        Assert.AreEqual(originallyPaused ? CaptureAdmissionState.Paused : CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        Assert.AreEqual(!originallyPaused, terminal.ResumeConfirmed);
        Assert.IsFalse(fixture.Controls.StopPending);
        Assert.IsFalse(fixture.Controls.AdmissionHeld);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        var reloaded = new InstallerControlJournal(Path.Combine(fixture.Root.FullName, "installer.json"));
        await reloaded.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(terminal.ResumeConfirmed, reloaded.Receipts.Single().ResumeConfirmed);
        Assert.AreEqual(terminal.Events[^1], reloaded.Receipts.Single().Events[^1]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PreStopRestorePreservesLaterOwnerNoOpPauseIncludingAtResumeBoundary(bool pauseAtResumeBoundary)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        fixture.WindowOpening = fixture.WindowClock.GetUtcNow().AddMinutes(5);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        long? ownerVersion = null;
        async Task LaterPause()
        {
            ownerVersion = fixture.Capture.Snapshot.Version;
            var later = await fixture.Capture.PauseAsync("owner-after-installer-pause", ownerVersion, "owner", "Keep capture paused", CancellationToken.None).ConfigureAwait(false);
            Assert.IsFalse(later.Changed, "An accepted no-op pause must retain ordinary pause semantics.");
            Assert.AreEqual(ownerVersion.Value, later.Version);
        }
        fixture.AfterPauseAsync = async () =>
        {
            fixture.WindowClock.Advance(TimeSpan.FromMinutes(4));
            if (!pauseAtResumeBoundary) await LaterPause().ConfigureAwait(false);
        };
        if (pauseAtResumeBoundary) fixture.BeforeResume = LaterPause;
        var terminal = await new InstallerControlVerifier(client, _ => Task.CompletedTask, TimeProvider.System)
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(ownerVersion);
        Assert.AreEqual(CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.IsFalse(terminal.ResumeConfirmed);
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        Assert.AreEqual(ownerVersion.Value, fixture.Capture.Snapshot.Version);
        Assert.AreEqual(0, fixture.RestartCalls);
        var commands = await fixture.Capture.GetRecentCommandsAsync(10, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("owner", commands[0].Actor);
        Assert.AreEqual("Keep capture paused", commands[0].Reason);
        Assert.IsFalse(commands[0].Changed);
        Assert.IsFalse(commands.Any(command => command.TargetState == CaptureAdmissionState.Running));
    }

    [TestMethod]
    [DataRow("settle")]
    [DataRow("startup")]
    [DataRow("busy-startup")]
    public async Task InterruptedDrainingUsesOwnedRestoreThroughSettlementAndWorkerRetry(string route)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var path = Path.Combine(fixture.Root.FullName, "installer.json");
        var journal = new InstallerControlJournal(path);
        await journal.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        var draining = queued with
        {
            Phase = CameraAgentControlPhase.Draining,
            OriginalProcessId = fixture.Controls.ProcessId,
            ResumeAfterVerification = true,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, DateTimeOffset.UtcNow, "lifecycle.draining")]
        };
        // Seed the actual write-before-pause crash boundary; the real client creates the native owned pause.
        await journal.SaveAsync(draining).ConfigureAwait(false);
        await client.ExchangeAsync(draining, CancellationToken.None).ConfigureAwait(false);
        await client.DrainAsync(queued.Request, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        var busy = route == "busy-startup";
        var messages = new List<string>();
        var worker = new InstallerControlWorker(client, journal,
            _ => busy ? throw InstallerException.Refused("Injected deployment lock contention.") : Task.FromResult<IDisposable>(new TestDeploymentLease()),
            TimeProvider.System, messages.Add);
        if (route == "settle")
        {
            await new InstallerControlVerifier(client, journal.SaveAsync, TimeProvider.System).SettleInterruptedAsync(draining).ConfigureAwait(false);
            await worker.PollAsync(CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await worker.RunAsync(true, CancellationToken.None).ConfigureAwait(false);
            if (busy)
            {
                Assert.IsFalse(journal.Receipts.Single().IsTerminal, "Lock contention must leave recovery pending, not misclassify owned work as orphaned.");
                Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
                Assert.AreEqual(LocalAutomationRunOutcome.Running, (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
                busy = false;
                await worker.PollAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        var terminal = journal.Receipts.Single();
        Assert.AreEqual("lifecycle.installer-interrupted", terminal.Events[^1].Code);
        Assert.IsTrue(terminal.ResumeConfirmed);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        Assert.AreEqual(0, fixture.RestartCalls);
        Assert.IsFalse(fixture.Controls.AdmissionHeld);
        Assert.IsEmpty(journal.Unpublished);
        Assert.IsEmpty(journal.Refused);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed, (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        var reloaded = new InstallerControlJournal(path);
        await reloaded.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(reloaded.Receipts.Single().ResumeConfirmed);
        Assert.IsEmpty(reloaded.Unpublished);
    }

    [TestMethod]
    [DataRow(CameraAgentControlKind.Drain)]
    [DataRow(CameraAgentControlKind.Restart)]
    public async Task InstallerCrashRightAfterPublishingTheDrainedBoundarySettlesWithoutQuarantine(CameraAgentControlKind kind)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var path = Path.Combine(fixture.Root.FullName, "installer.json");
        var journal = new InstallerControlJournal(path);
        await journal.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.Controls.SubmitAsync(new CameraAgentControlRequest(Guid.NewGuid(), kind, "owner", "maintenance",
            fixture.Capture.Snapshot.Version, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
        // The last pre-stop publication: the drained boundary for a Drain, the verification deadline for a Restart. CameraAgent
        // accepts it; the installer then dies before writing anything else.
        var published = false;
        fixture.BeforeExchange = receipt =>
        {
            if (receipt is { Phase: CameraAgentControlPhase.Draining } && (kind == CameraAgentControlKind.Drain
                    ? receipt.PausedVersion is not null : receipt.VerificationDeadlineUtc is not null))
                published = true;
            return Task.CompletedTask;
        };
        Task CrashAfterPublication(CameraAgentControlReceipt receipt) =>
            published ? throw new IOException("Simulated installer crash.") : journal.SaveAsync(receipt);
        var crash = await Assert.ThrowsAsync<IOException>(() => new InstallerControlVerifier(client, CrashAfterPublication, TimeProvider.System)
            .ExecuteAsync(queued, CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual("Simulated installer crash.", crash.Message);
        Assert.IsTrue(published);
        var stranded = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
        Assert.AreEqual(CameraAgentControlPhase.Draining, stranded.Phase);
        Assert.IsNotNull(stranded.PausedVersion);
        Assert.IsTrue(fixture.Controls.AdmissionHeld);

        // A restarted installer worker settles from its durable journal; CameraAgent must accept that terminal receipt.
        fixture.BeforeExchange = null;
        var restarted = new InstallerControlJournal(path);
        await restarted.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var durableAtCrash = restarted.Receipts.Single();
        var messages = new List<string>();
        await new InstallerControlWorker(client, restarted, _ => Task.FromResult<IDisposable>(new TestDeploymentLease()), TimeProvider.System, messages.Add)
            .RunAsync(true, CancellationToken.None).ConfigureAwait(false);

        var terminal = restarted.Receipts.Single();
        Assert.AreEqual("lifecycle.installer-interrupted", terminal.Events[^1].Code);
        Assert.IsEmpty(restarted.Refused, "Settlement must not be quarantined: " + string.Join(" | ", messages));
        Assert.IsEmpty(restarted.Unpublished);
        Assert.AreEqual(stranded.PausedVersion, durableAtCrash.PausedVersion, "The journal must already hold what CameraAgent retained.");
        Assert.AreEqual(stranded.VerificationDeadlineUtc, durableAtCrash.VerificationDeadlineUtc);
        var projected = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
        Assert.AreEqual(CameraAgentControlPhase.Failed, projected.Phase);
        Assert.AreEqual("lifecycle.installer-interrupted", projected.Events[^1].Code);
        Assert.IsFalse(fixture.Controls.AdmissionHeld);
        Assert.AreEqual(0, fixture.RestartCalls);
        // A standalone Drain stays paused; a pre-stop Restart restores its owned pause.
        Assert.AreEqual(kind == CameraAgentControlKind.Restart, terminal.ResumeConfirmed);
        Assert.AreEqual(kind == CameraAgentControlKind.Restart ? CaptureAdmissionState.Running : CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        if (kind == CameraAgentControlKind.Drain)
            await fixture.Capture.ResumeAsync("explicit-resume-after-recovery", fixture.Capture.Snapshot.Version, "owner", "checked receipt",
                CancellationToken.None).ConfigureAwait(false);
        using var admitted = await fixture.Capture.EnterAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    [DataRow(CameraAgentControlKind.Restart, "boundary")]
    [DataRow(CameraAgentControlKind.Restart, "deadline")]
    [DataRow(CameraAgentControlKind.Restart, "refused")]
    [DataRow(CameraAgentControlKind.Drain, "boundary")]
    public async Task PersistentJournalWriteFailureBeforePublicationLeavesTheRequestRecoverable(CameraAgentControlKind kind, string field)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var path = Path.Combine(fixture.Root.FullName, "installer.json");
        var journal = new InstallerControlJournal(path);
        await journal.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.Controls.SubmitAsync(new CameraAgentControlRequest(Guid.NewGuid(), kind, "owner", "maintenance",
            fixture.Capture.Snapshot.Version, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
        if (field == "refused")
        {
            // CameraAgent itself refuses the stop: the capture window closes after Restarting is published, before the stop call.
            fixture.WindowOpening = fixture.WindowClock.GetUtcNow().AddMinutes(10);
            var closed = false;
            fixture.BeforeExchange = receipt =>
            {
                if (receipt?.Phase == CameraAgentControlPhase.Restarting && !closed)
                {
                    closed = true;
                    fixture.WindowClock.Advance(TimeSpan.FromMinutes(9));
                }
                return Task.CompletedTask;
            };
        }
        bool Carries(CameraAgentControlReceipt receipt) => field switch
        {
            "boundary" => receipt.PausedVersion is not null,
            "deadline" => receipt.VerificationDeadlineUtc is not null,
            _ => receipt.RestartRefused
        };
        // From the first write carrying the field, the journal stays unwritable for the rest of this installer run.
        var failedWrites = new List<CameraAgentControlReceipt>();
        Task FailingJournal(CameraAgentControlReceipt next)
        {
            if (failedWrites.Count == 0 && !Carries(next)) return journal.SaveAsync(next);
            failedWrites.Add(next);
            throw new IOException("Injected persistent journal write failure.");
        }
        await Assert.ThrowsAsync<IOException>(() => new InstallerControlVerifier(client, FailingJournal, TimeProvider.System)
            .ExecuteAsync(queued, CancellationToken.None)).ConfigureAwait(false);
        Assert.IsNotEmpty(failedWrites);
        Assert.IsTrue(Carries(failedWrites[0]));
        fixture.BeforeExchange = null;

        // CameraAgent holds nothing the journal lacks, so the restarted worker can settle it.
        var restarted = new InstallerControlJournal(path);
        await restarted.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        var durable = restarted.Receipts.Single();
        var stranded = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
        Assert.IsFalse(Carries(durable));
        Assert.IsFalse(Carries(stranded), "The unjournaled field must not reach CameraAgent.");
        Assert.IsFalse(stranded.IsTerminal);
        var messages = new List<string>();
        await new InstallerControlWorker(client, restarted, _ => Task.FromResult<IDisposable>(new TestDeploymentLease()), TimeProvider.System, messages.Add)
            .RunAsync(true, CancellationToken.None).ConfigureAwait(false);

        var terminal = restarted.Receipts.Single();
        Assert.AreEqual("lifecycle.installer-interrupted", terminal.Events[^1].Code);
        Assert.IsEmpty(restarted.Refused, "Settlement must not be quarantined: " + string.Join(" | ", messages));
        Assert.IsEmpty(restarted.Unpublished);
        var projected = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
        Assert.AreEqual(CameraAgentControlPhase.Failed, projected.Phase);
        Assert.AreEqual("lifecycle.installer-interrupted", projected.Events[^1].Code);
        Assert.IsFalse(fixture.Controls.AdmissionHeld);
        Assert.IsFalse(fixture.Controls.StopPending);
        Assert.AreEqual(0, fixture.RestartCalls);
        // A pre-stop Restart restores its owned pause. A refusal that was never journaled stays ambiguous, so that Restart, like
        // a standalone Drain, stays paused for an explicit owner resume.
        var restored = kind == CameraAgentControlKind.Restart && field != "refused";
        Assert.AreEqual(restored, terminal.ResumeConfirmed);
        Assert.AreEqual(restored ? CaptureAdmissionState.Running : CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        if (!restored)
            await fixture.Capture.ResumeAsync("explicit-resume-after-journal-fault", fixture.Capture.Snapshot.Version, "owner", "checked receipt",
                CancellationToken.None).ConfigureAwait(false);
        using var admitted = await fixture.Capture.EnterAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task CancelledClaimRaceIsAcknowledgedWithoutPinningRefusedJournalEvidence()
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var path = Path.Combine(fixture.Root.FullName, "installer.json");
        var journal = new InstallerControlJournal(path);
        await journal.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", journal.WorkerId, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        var cancelled = false;
        fixture.BeforeExchange = async receipt =>
        {
            if (receipt?.Phase != CameraAgentControlPhase.Draining || cancelled) return;
            cancelled = true;
            await fixture.Controls.CancelAsync(queued.Request.OperationId, CancellationToken.None, "owner-race", "Cancel before installer claim").ConfigureAwait(false);
        };
        var worker = new InstallerControlWorker(client, journal, _ => Task.FromResult<IDisposable>(new TestDeploymentLease()), TimeProvider.System, _ => { });
        await worker.RunAsync(true, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(cancelled);
        Assert.AreEqual("lifecycle.claim-refused", journal.Receipts.Single().Events[^1].Code);
        Assert.IsEmpty(journal.Refused, "A proven no-effect cancellation race must not permanently pin a journal slot.");
        Assert.IsEmpty(journal.Unpublished);
        var projection = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
        Assert.AreEqual("lifecycle.unclaimed-cancelled", projection.Events[^1].Code);
        Assert.AreEqual("owner-race", projection.Cancellation!.Actor);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        Assert.AreEqual(0, fixture.Capture.Snapshot.Version);
        Assert.AreEqual(0, fixture.RestartCalls);
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        var reloaded = new InstallerControlJournal(path);
        await reloaded.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.IsEmpty(reloaded.Refused);
        Assert.IsEmpty(reloaded.Unpublished);
    }

    [TestMethod]
    public async Task CancellationHistoryBusyReturnsUnavailableAndRetainsDurableCancellationForReconciliation()
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner"),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)], IdentityConstants.ApplicationScheme));
        var authentication = new Mock<AuthenticationStateProvider>();
        authentication.Setup(value => value.GetAuthenticationStateAsync()).ReturnsAsync(new AuthenticationState(principal));
        var authorization = new Mock<IAuthorizationService>(MockBehavior.Strict);
        authorization.Setup(value => value.AuthorizeAsync(principal, null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1)).ReturnsAsync(AuthorizationResult.Success());
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = fixture.Root.FullName, RawIngressSqliteBusyTimeoutSeconds = 1 });
        using var schedule = new SqliteCaptureScheduleStore(new Mock<IRawCaptureIngress>().Object, options, TimeProvider.System);
        var service = new CameraAgentSystemUiService(authentication.Object, authorization.Object, new Mock<HealthCheckService>().Object,
            fixture.Capture, schedule, null!, options, TimeProvider.System, NullLogger<CameraAgentSystemUiService>.Instance,
            controls: fixture.Controls, automations: fixture.Automations);
        using (var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root.FullName, ".automation", "local-automations.db")};Pooling=False"))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            using var held = (SqliteTransaction)await connection.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            var result = await service.CancelLifecycleAsync(queued.Request.OperationId, CancellationToken.None, "Cancel while history is busy").ConfigureAwait(false);
            Assert.AreEqual(OperatorUiResultKind.Unavailable, result.Kind);
            var receipt = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
            Assert.AreEqual(CameraAgentControlPhase.Cancelled, receipt.Phase);
            Assert.AreEqual("owner", receipt.Cancellation!.Actor);
            Assert.AreEqual("Cancel while history is busy", receipt.Cancellation.Reason);
            using var inspect = connection.CreateCommand();
            inspect.Transaction = held;
            inspect.CommandText = "SELECT outcome FROM automation_runs WHERE run_key = $key;";
            inspect.Parameters.AddWithValue("$key", queued.Request.AutomationRunKey);
            Assert.AreEqual("Running", await inspect.ExecuteScalarAsync().ConfigureAwait(false));
            await held.RollbackAsync().ConfigureAwait(false);
        }
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted, (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        Assert.AreEqual(0, fixture.RestartCalls);
    }

    private sealed class TestDeploymentLease : IDisposable
    {
        public void Dispose() { }
    }
}
