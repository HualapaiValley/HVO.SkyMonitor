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
