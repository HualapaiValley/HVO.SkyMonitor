using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.CameraAgent.Tests.Automation;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>Real endpoint binding, installation authentication, JSON transport and SQLite pause/resume; process and hardware effects are fakes.</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class InstallerLifecycleControlIntegrationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallerOwnsTerminalReceiptAcrossSimulatedProcessLoss(bool neverReturns)
    {
        var fixture = new Fixture(neverReturns);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var worker = Guid.NewGuid();
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", worker, fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var initialProcess = fixture.Controls.ProcessId;
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "test-owner",
            "virtual scheduled restart", fixture.Capture.Snapshot.Version, DateTimeOffset.UtcNow);
        var queued = await fixture.Controls.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
        var localJournal = Path.Combine(fixture.Root.FullName, "installer-receipt.json");
        async Task Persist(CameraAgentControlReceipt receipt) => await File.WriteAllTextAsync(localJournal,
            JsonSerializer.Serialize(receipt, Json)).ConfigureAwait(false);
        var verifier = new InstallerControlVerifier(client, Persist, TimeProvider.System, new(
            TimeSpan.FromSeconds(2), neverReturns ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(3),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(5)));
        var terminal = await verifier.ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        var durable = JsonSerializer.Deserialize<CameraAgentControlReceipt>(await File.ReadAllTextAsync(localJournal).ConfigureAwait(false),
            Json)!;
        Assert.AreEqual(terminal.Phase, durable.Phase);
        Assert.AreEqual(1, fixture.RestartCalls, JsonSerializer.Serialize(durable, Json));
        Assert.AreEqual(neverReturns ? CameraAgentControlPhase.Failed : CameraAgentControlPhase.Succeeded, durable.Phase);
        if (neverReturns)
        {
            Assert.AreEqual("restart.verification-timeout", durable.Events[^1].Code);
            Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        }
        else
        {
            Assert.AreNotEqual(initialProcess, fixture.Controls.ProcessId);
            Assert.AreEqual(fixture.Controls.ProcessId, durable.VerifiedProcessId);
            Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
            Assert.AreEqual(CameraAgentControlPhase.Succeeded,
                (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single().Phase);
        }
    }

    [TestMethod]
    public async Task BrowserCredentialsCannotRegisterVerifierOrInvokeItsRestartMechanism()
    {
        var fixture = new Fixture(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new HttpClient(fixture.Handler, false) { BaseAddress = new Uri("http://virtual-cameraagent") };
        client.DefaultRequestHeaders.Add("Cookie", "local-owner-session=test");
        using var register = await client.PostAsJsonAsync("/api/internal/deployment/lifecycle/control/exchange", new CameraAgentControlExchange(Guid.NewGuid())).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, register.StatusCode);
        using var restart = await client.PostAsJsonAsync("/api/internal/deployment/lifecycle/control/restart",
            new CameraAgentControlRestart(Guid.NewGuid(), Guid.NewGuid(), 0)).ConfigureAwait(false);
        Assert.AreEqual(HttpStatusCode.Unauthorized, restart.StatusCode);
        Assert.IsFalse(fixture.Controls.Available);
        Assert.AreEqual(0, fixture.RestartCalls);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostResumeResponseKeepsAdmissionFenced_RestoresPauseAndCannotUndoLaterExplicitResume(bool timeout)
    {
        var fixture = new Fixture(false) { LoseResumeResponse = true, LoseResumeByTimeout = timeout };
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token",
            Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Restart, "owner", "maintenance",
            fixture.Capture.Snapshot.Version, DateTimeOffset.UtcNow);
        var queued = await fixture.Controls.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
        var verifier = new InstallerControlVerifier(client, _ => Task.CompletedTask, TimeProvider.System,
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5)));
        var terminal = await verifier.ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual("restart.resume-unconfirmed", terminal.Events[^1].Code);
        Assert.IsTrue(terminal.ResumeRequested);
        Assert.IsTrue(fixture.ResumeFenceHeld, "No exposure may start between a native resume and installer terminal publication.");
        Assert.AreEqual(CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        Assert.IsFalse(fixture.Controls.AdmissionHeld);
        await fixture.Capture.ResumeAsync("owner-resume-after-failure", fixture.Capture.Snapshot.Version, "owner", "checked receipt",
            CancellationToken.None).ConfigureAwait(false);
        await client.ExchangeAsync(terminal, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State, "A receipt replay cannot rewind a later owner resume.");
    }

    [TestMethod]
    public async Task ActiveLifecycleFencesResumeUntilTerminalFailure_ExistingExplicitResumeStillWorks()
    {
        var fixture = new Fixture(false);
        await using var fixtureLifetime = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        var worker = Guid.NewGuid();
        await fixture.Controls.ExchangeAsync(new(worker), CancellationToken.None).ConfigureAwait(false);
        var request = new CameraAgentControlRequest(Guid.NewGuid(), CameraAgentControlKind.Drain, "owner", "maintenance",
            fixture.Capture.Snapshot.Version, DateTimeOffset.UtcNow);
        var queued = await fixture.Controls.SubmitAsync(request, CancellationToken.None).ConfigureAwait(false);
        var draining = queued with
        {
            Phase = CameraAgentControlPhase.Draining,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, DateTimeOffset.UtcNow, "lifecycle.draining")]
        };
        await fixture.Controls.ExchangeAsync(new(worker, draining), CancellationToken.None).ConfigureAwait(false);
        await fixture.Capture.PauseAsync("test-pause", fixture.Capture.Snapshot.Version, "owner", "maintenance", CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureControlConflictException>(() =>
            fixture.Capture.ResumeAsync("blocked-resume", fixture.Capture.Snapshot.Version, "owner", "resume", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        await fixture.Controls.ExchangeAsync(new(worker, draining with
        {
            Phase = CameraAgentControlPhase.Failed,
            Events = [.. draining.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "drain.timeout")]
        }), CancellationToken.None).ConfigureAwait(false);
        await fixture.Capture.ResumeAsync("explicit-resume", fixture.Capture.Snapshot.Version, "owner", "checked failure", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
    }

    [TestMethod]
    [DataRow(241)]
    [DataRow(300)]
    [DataRow(360)]
    public async Task RealRunnerRestartWithHistoricalRawQuarantineAndEnabledOutboxUsesRemainingWindowBudget(int marginSeconds)
    {
        var fixture = new Fixture(false) { HistoricalQuarantine = true };
        await using var owned = fixture.ConfigureAwait(false);
        fixture.WindowOpening = fixture.WindowClock.GetUtcNow().AddSeconds(marginSeconds);
        fixture.AfterPause = () => fixture.WindowClock.Advance(TimeSpan.FromMinutes(2));
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        var terminal = await new InstallerControlVerifier(client, _ => Task.CompletedTask, TimeProvider.System,
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5)))
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentControlPhase.Succeeded, terminal.Phase, terminal.Events[^1].Code);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        Assert.AreEqual(1, fixture.RestartCalls);
        Assert.AreEqual(LocalAutomationRunOutcome.Succeeded,
            (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RealScheduledRestartTimeoutRestoresPriorRunningStateAndKeepsAcceptedDelivery(bool initiallyPaused, bool quarantined)
    {
        var fixture = new Fixture(false) { OutboxPending = quarantined ? 0 : 1, OutboxRetry = !quarantined, OutboxQuarantine = quarantined, HistoricalQuarantine = true };
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        if (initiallyPaused)
            await fixture.Capture.PauseAsync("owner-pause", fixture.Capture.Snapshot.Version, "owner", "maintenance", CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        var terminal = await new InstallerControlVerifier(client, _ => Task.CompletedTask, TimeProvider.System,
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5)))
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.AreEqual("drain.timeout", terminal.Events[^1].Code);
        Assert.AreEqual(initiallyPaused ? CaptureAdmissionState.Paused : CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        Assert.AreEqual(!initiallyPaused, terminal.ResumeConfirmed);
        Assert.IsFalse(terminal.RestartRequested);
        Assert.AreEqual(0, fixture.RestartCalls);
        Assert.AreEqual(quarantined ? 0 : 1, fixture.OutboxPending, "Failure must preserve accepted delivery evidence.");
        Assert.AreEqual(quarantined, fixture.OutboxQuarantine);
        Assert.AreEqual(LocalAutomationRunOutcome.Failed,
            (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        if (!initiallyPaused)
        {
            using var lease = await fixture.Capture.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CancelAfterAcceptedRestartKeepsOriginalProcessFencedUntilHeldStopRuns_NewProcessRecovers()
    {
        using var heldClock = new HeldRestartClock();
        var fixture = new Fixture(false) { RestartClock = heldClock };
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        var original = fixture.Controls.ProcessId;
        fixture.AfterRestartAccepted = () => fixture.Controls.CancelAsync(queued.Request.OperationId, CancellationToken.None,
            "owner-held-timer", "Cancel while delayed stop is armed");
        var terminal = await new InstallerControlVerifier(client, _ => Task.CompletedTask, TimeProvider.System,
            new(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5)))
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentControlPhase.Cancelled, terminal.Phase);
        Assert.AreEqual("owner-held-timer", terminal.Cancellation!.Actor);
        Assert.AreEqual(original, fixture.Controls.ProcessId);
        Assert.AreEqual(0, fixture.RestartCalls);
        Assert.IsTrue(heldClock.Pending);
        Assert.IsTrue(fixture.Controls.StopPending);
        Assert.IsTrue(fixture.Controls.AdmissionHeld);
        await Assert.ThrowsAsync<HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureControlConflictException>(() => fixture.Capture.ResumeAsync(
            "unsafe-owner-resume", fixture.Capture.Snapshot.Version, "owner", "force open", CancellationToken.None)).ConfigureAwait(false);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                using var lease = await fixture.Capture.EnterAsync(timeout.Token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        fixture.AfterRestartAccepted = null;
        heldClock.Fire();
        await fixture.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
        await client.ExchangeAsync(terminal, CancellationToken.None).ConfigureAwait(false);
        Assert.AreNotEqual(original, fixture.Controls.ProcessId);
        Assert.IsFalse(fixture.Controls.StopPending);
        await fixture.Capture.ResumeAsync("safe-new-process-resume", fixture.Capture.Snapshot.Version, "owner", "checked receipt", CancellationToken.None).ConfigureAwait(false);
        using var admitted = await fixture.Capture.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted,
            (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
        var projected = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
        Assert.AreEqual("owner-held-timer", projected.Cancellation!.Actor);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnclaimedScheduledIntentCanBeCancelledWithoutWorker_RejectsLateClaimAndAllowsOwnerResume(bool restartBeforeHistoryRead)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        await fixture.Capture.PauseAsync("ordinary-pause", fixture.Capture.Snapshot.Version, "owner", "maintenance", CancellationToken.None).ConfigureAwait(false);
        await fixture.Controls.CancelAsync(queued.Request.OperationId, CancellationToken.None, "owner", "Worker unavailable").ConfigureAwait(false);
        var cancelled = (await fixture.Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single();
        Assert.AreEqual(CameraAgentControlPhase.Cancelled, cancelled.Phase);
        // Simulate a crash/IO failure between durable local cancellation and the separate SQLite completion.
        // No test-side claim or terminal-history mutation repairs the real runner's pending run.
        if (restartBeforeHistoryRead) await fixture.ReloadAsync().ConfigureAwait(false);
        else _ = await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
        using (var inspection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(fixture.Root.FullName, ".automation", "local-automations.db")};Mode=ReadOnly;Pooling=False"))
        {
            await inspection.OpenAsync().ConfigureAwait(false);
            using var command = inspection.CreateCommand();
            command.CommandText = "SELECT outcome FROM automation_runs WHERE run_key = $key;";
            command.Parameters.AddWithValue("$key", queued.Request.AutomationRunKey);
            Assert.AreEqual("Interrupted", await command.ExecuteScalarAsync().ConfigureAwait(false));
        }
        await Assert.ThrowsAsync<InstallerControlRefusalException>(() => client.ExchangeAsync(queued with
        {
            Phase = CameraAgentControlPhase.Draining,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Draining, DateTimeOffset.UtcNow, "lifecycle.draining")]
        }, CancellationToken.None)).ConfigureAwait(false);
        await fixture.Capture.ResumeAsync("ordinary-resume", fixture.Capture.Snapshot.Version, "owner", "recover", CancellationToken.None).ConfigureAwait(false);
        using var admitted = await fixture.Capture.EnterAsync(CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(0, fixture.RestartCalls);
        Assert.AreEqual(LocalAutomationRunOutcome.Interrupted,
            (await fixture.Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single().Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ScheduledWindowChangeOrCancellationBeforeStopRestoresOwnedPause(bool cancel)
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        fixture.WindowOpening = fixture.WindowClock.GetUtcNow().AddMinutes(5);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        fixture.AfterPauseAsync = async () =>
        {
            if (cancel) await fixture.Controls.CancelAsync(queued.Request.OperationId, CancellationToken.None, "owner", "Cancel before stop").ConfigureAwait(false);
            else fixture.WindowClock.Advance(TimeSpan.FromMinutes(4));
        };
        var terminal = await new InstallerControlVerifier(client, _ => Task.CompletedTask, TimeProvider.System)
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(cancel ? CameraAgentControlPhase.Cancelled : CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
        Assert.IsTrue(terminal.ResumeConfirmed);
        Assert.AreEqual(0, fixture.RestartCalls);
        using var lease = await fixture.Capture.EnterAsync(CancellationToken.None).ConfigureAwait(false);
    }

    [TestMethod]
    public async Task FailedScheduledPauseCannotUndoConcurrentOwnerPause()
    {
        var fixture = new Fixture(false);
        await using var owned = fixture.ConfigureAwait(false);
        await fixture.InitializeAsync().ConfigureAwait(false);
        using var client = new InstallerControlHttpClient(new Uri("http://virtual-cameraagent"), "test-installation-token", Guid.NewGuid(), fixture.Handler);
        await client.ExchangeAsync(null, CancellationToken.None).ConfigureAwait(false);
        var queued = await fixture.RunScheduledOccurrenceAsync().ConfigureAwait(false);
        fixture.BeforePause = async () =>
        {
            await fixture.Capture.PauseAsync("later-owner-pause", fixture.Capture.Snapshot.Version, "owner", "Deliberate operator pause", CancellationToken.None).ConfigureAwait(false);
        };
        var terminal = await new InstallerControlVerifier(client, _ => Task.CompletedTask, TimeProvider.System)
            .ExecuteAsync(queued, CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CameraAgentControlPhase.Failed, terminal.Phase);
        Assert.IsFalse(terminal.ResumeRequested, "A version match alone is not proof of owning the pause command.");
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        Assert.AreEqual(0, fixture.RestartCalls);
    }

    internal sealed class Fixture(bool neverReturns) : IAsyncDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("hvo-1711-wire-");
        internal CameraAgentControlStore Controls { get; private set; } = null!;
        internal CaptureAdmissionCoordinator Capture { get; private set; } = null!;
        internal HttpMessageHandler Handler => _handler ??= new EndpointHandler(this);
        internal int RestartCalls;
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool LoseResumeResponse, ResumeFenceHeld, HistoricalQuarantine, OutboxRetry;
        internal bool LoseResumeByTimeout, OutboxQuarantine;
        internal int OutboxPending;
        internal Func<Task>? AfterRestartAccepted;
        internal Action? AfterPause;
        internal Func<Task>? AfterPauseAsync, BeforePause, BeforeResume;
        internal Func<CameraAgentControlReceipt?, Task>? BeforeExchange;
        internal TimeProvider RestartClock { get; set; } = new FastRestartClock();
        internal MutableTimeProvider WindowClock { get; } = new(DateTimeOffset.UtcNow);
        internal DateTimeOffset? WindowOpening;
        internal SqliteLocalAutomationStore Automations { get; private set; } = null!;
        private readonly MutableTimeProvider _automationClock = new(DateTimeOffset.UtcNow);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Each simulated process registry is retained and disposed in the owned collection.")]
        private PeriodicRegistryHarness _registry = null!;
        private IOptions<CameraAgentHostOptions> _options = null!;
        private readonly List<WebApplication> _hosts = [];
        private readonly List<IDisposable> _owned = [];
        private EndpointHandler? _handler;
        private volatile bool _stopped;
        private SimulatedLifetime _lifetime = null!;
        private readonly bool _neverReturns = neverReturns;

        internal async Task InitializeAsync()
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = Root.FullName, RawIngressSqliteBusyTimeoutSeconds = 1, CentralIntegration = new() { Mode = CentralIntegrationMode.Enabled } });
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.Configuration["LifecycleControl:Token"] = "test-installation-token";
            builder.Configuration["CameraAgent:Restart:Supervised"] = "true";
            var telemetry = new CaptureControlTelemetry();
            Controls = new(options, TimeProvider.System);
            Capture = new(new JournalIngress(Path.Combine(Root.FullName, "journal", "raw-ingress.db")), options,
                TimeProvider.System, telemetry, lifecycleControls: Controls);
            await Capture.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            _owned.Add(Controls);
            _owned.Add(Capture);
            _owned.Add(telemetry);
            var configuration = new CameraAgentConfigurationAccessor();
            builder.Services.AddSingleton(Controls);
            builder.Services.AddSingleton(Capture);
            builder.Services.AddSingleton(telemetry);
            builder.Services.AddSingleton(RestartClock);
            _lifetime = new SimulatedLifetime(() => { Interlocked.Increment(ref RestartCalls); _stopped = true; Stopped.TrySetResult(); });
            builder.Services.AddSingleton<ICameraAgentControlRestartWindow>(new SafeWindow(this));
            var raw = new RawIngressState(TimeProvider.System);
            raw.Set(HistoricalQuarantine ? RawIngressAvailability.Degraded : RawIngressAvailability.Accepting, "observed-empty", quarantineCount: HistoricalQuarantine ? 1 : 0);
            var lanes = new CaptureLaneState(TimeProvider.System, options);
            lanes.Update([]);
            var processing = new CaptureProcessingState();
            processing.SetDurable(0, 0, 0, 0, null);
            var outbox = new ArtifactOutboxState();
            outbox.Update(Root.FullName, new(OutboxPending + (OutboxQuarantine ? 1 : 0), 0, null, OutboxPending, 0, OutboxRetry ? 1 : 0, 0,
                OutboxQuarantine ? 1 : 0, 0, DateTimeOffset.UtcNow));
            builder.Services.AddSingleton(new CameraAgentOperationsSummaryProvider(TimeProvider.System, Capture,
                raw, new NoQueueRefresh(), lanes,
                processing, outbox, new StoragePressureState(), new FleetRuntimeState(TimeProvider.System),
                new FleetHeartbeatState(), new EnvironmentalObservationDeliveryState(), new ExecutionEvidenceExportState(),
                new TransientWorkerState(TimeProvider.System), new CaptureTelemetrySink(), configuration,
                new CameraAgentStorageResolver(configuration, options), options));
            builder.Services.AddSingleton(new DeploymentContinuityReader());
            builder.Services.AddSingleton(options);
            _options = options;
            _registry = new PeriodicRegistryHarness(_automationClock, [new CameraAgentRestartAutomationAdapter(Controls, Capture, _automationClock)]);
            Automations = new SqliteLocalAutomationStore(_registry.Registry, new StubCaptureSequenceSource(), options,
                _automationClock, NullLogger<SqliteLocalAutomationStore>.Instance, lifecycleControls: Controls);
            await Automations.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            _owned.Add(_registry);
            _owned.Add(Automations);
            builder.Services.AddSingleton(Automations);
            var app = builder.Build();
            app.MapCameraAgentLifecycleEndpoints();
            app.MapCameraAgentControlLifecycleEndpoints();
            _hosts.Add(app);
        }

        internal async Task ReloadAsync()
        {
            Automations.Dispose();
            _owned.Remove(Automations);
            await InitializeAsync().ConfigureAwait(false);
        }

        internal async Task<CameraAgentControlReceipt> RunScheduledOccurrenceAsync()
        {
            var saved = await Automations.SaveAsync(new(CameraAgentRestartAutomationAdapter.DefinitionId, "Scheduled restart", true,
                LocalAutomationTaskKind.ScheduledCameraAgentRestart, CameraAgentRestartAutomationAdapter.Target,
                LocalAutomationTriggerKind.Periodic, 3600, 0, "save", "owner", null), CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(LocalAutomationCommandStatus.Applied, saved.Status);
            _automationClock.Advance(TimeSpan.FromHours(1));
            using var runner = new LocalAutomationRunnerService(Automations, _registry.Registry, new StubCaptureSequenceSource(),
                _options, _automationClock, NullLogger<LocalAutomationRunnerService>.Instance);
            await runner.SweepAsync(CancellationToken.None).ConfigureAwait(false);
            var run = (await Automations.GetStateAsync(CancellationToken.None).ConfigureAwait(false)).Runs.Single();
            Assert.AreEqual(LocalAutomationRunOutcome.Running, run.Outcome, "The real runner must retain matched installer completion ownership.");
            return (await Controls.ReadAsync(CancellationToken.None).ConfigureAwait(false)).Receipts.Single(item => item.Request.AutomationRunKey == run.RunKey);
        }

        public async ValueTask DisposeAsync()
        {
            _handler?.Dispose();
            foreach (var host in _hosts) await host.DisposeAsync().ConfigureAwait(false);
            foreach (var owned in _owned) owned.Dispose();
            Root.Delete(true);
        }

        private sealed class EndpointHandler(Fixture owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (owner._stopped)
                {
                    if (owner._neverReturns) throw new HttpRequestException("Simulated CameraAgent never returned.");
                    owner._stopped = false;
                    await owner.ReloadAsync().ConfigureAwait(false);
                }
                if (request.RequestUri!.AbsolutePath.EndsWith("/pause", StringComparison.Ordinal) && owner.BeforePause is { } beforePause)
                    await beforePause().ConfigureAwait(false);
                var app = owner._hosts[^1];
                var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                    .Single(item => item.RoutePattern.RawText == request.RequestUri!.AbsolutePath);
                using var scope = app.Services.CreateScope();
                var context = new DefaultHttpContext { RequestServices = new LifetimeProvider(scope.ServiceProvider, owner._lifetime), RequestAborted = cancellationToken };
                context.Request.Method = request.Method.Method;
                foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
                var content = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (request.RequestUri!.AbsolutePath.EndsWith("/control/exchange", StringComparison.Ordinal) && owner.BeforeExchange is { } beforeExchange)
                    await beforeExchange(JsonSerializer.Deserialize<CameraAgentControlExchange>(content, Json)!.Receipt).ConfigureAwait(false);
                if (request.RequestUri!.AbsolutePath.EndsWith("/resume", StringComparison.Ordinal) && owner.BeforeResume is { } beforeResume)
                    await beforeResume().ConfigureAwait(false);
                context.Request.Body = new MemoryStream(content);
                context.Request.ContentLength = content.Length;
                context.Request.ContentType = "application/json";
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody(content.Length > 0));
                using var output = new MemoryStream();
                context.Response.Body = output;
                await endpoint.RequestDelegate!(context).ConfigureAwait(false);
                if (request.RequestUri!.AbsolutePath.EndsWith("/control/restart", StringComparison.Ordinal) && owner.AfterRestartAccepted is { } afterRestart)
                    await afterRestart().ConfigureAwait(false);
                if (request.RequestUri!.AbsolutePath.EndsWith("/pause", StringComparison.Ordinal))
                {
                    owner.AfterPause?.Invoke();
                    if (owner.AfterPauseAsync is { } afterPause) await afterPause().ConfigureAwait(false);
                }
                if (owner.LoseResumeResponse && request.RequestUri!.AbsolutePath.EndsWith("/resume", StringComparison.Ordinal))
                {
                    owner.LoseResumeResponse = false;
                    using var admissionCancellation = new CancellationTokenSource();
                    var admission = owner.Capture.EnterAsync(admissionCancellation.Token).AsTask();
                    owner.ResumeFenceHeld = !admission.IsCompleted;
                    await admissionCancellation.CancelAsync().ConfigureAwait(false);
                    if (owner.ResumeFenceHeld)
                        await Assert.ThrowsAsync<OperationCanceledException>(() => admission).ConfigureAwait(false);
                    else (await admission.ConfigureAwait(false)).Dispose();
                    if (owner.LoseResumeByTimeout) throw new TimeoutException("Simulated resume acknowledgement timeout.");
                    throw new HttpRequestException("Simulated loss after durable resume acknowledgement.");
                }
                return new((HttpStatusCode)context.Response.StatusCode) { Content = new ByteArrayContent(output.ToArray()) };
            }
        }
    }

    private sealed class JournalIngress(string path) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            await new SqliteRawCaptureJournal(path, 1).InitializeAsync(cancellationToken).ConfigureAwait(false);
        public ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No camera acquisition is used in lifecycle transport tests.");
    }

    private sealed class NoQueueRefresh : IOperationsQueueSnapshotRefresher
    {
        public ValueTask RefreshOperationsQueueSnapshotsAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
    private sealed class SafeWindow(Fixture fixture) : ICameraAgentControlRestartWindow
    {
        public Task<bool> IsSafeAsync(CancellationToken cancellationToken, bool beforeRestart = false)
        {
            var now = fixture.WindowClock.GetUtcNow();
            return Task.FromResult(fixture.WindowOpening is not { } opening || CameraAgentControlLifecycleEndpoints.IsRestartWindowSafe(
                new(false, CaptureScheduleAdmissionReason.DefaultClosed, CaptureScheduleSafetyState.Available, now,
                    null, null, null, false, opening), now, beforeRestart));
        }
    }
    private sealed class HeldRestartClock : TimeProvider, IDisposable
    {
        private HeldTimer? _timer;
        internal bool Pending => _timer is not null;
        public void Dispose() => _timer?.Dispose();
        internal void Fire() { var timer = _timer!; _timer = null; timer.Fire(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _timer = new HeldTimer(callback, state);
        private sealed class HeldTimer(TimerCallback callback, object? state) : ITimer
        {
            internal void Fire() => callback(state);
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class FastRestartClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime > TimeSpan.Zero ? TimeSpan.FromMilliseconds(1) : dueTime, period);
    }
    private sealed class LifetimeProvider(IServiceProvider services, IHostApplicationLifetime lifetime) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IHostApplicationLifetime) ? lifetime : services.GetService(serviceType);
    }
    private sealed class SimulatedLifetime(Action stop) : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => stop();
    }
    private sealed class HasBody(bool value) : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => value; }
}
