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
        Assert.AreEqual(1, fixture.RestartCalls);
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
    public async Task LostResumeResponseKeepsAdmissionFenced_RestoresPauseAndCannotUndoLaterExplicitResume()
    {
        var fixture = new Fixture(false) { LoseResumeResponse = true };
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
        await fixture.Capture.PauseAsync("test-pause", fixture.Capture.Snapshot.Version, "owner", "maintenance", CancellationToken.None).ConfigureAwait(false);
        await Assert.ThrowsAsync<HVO.SkyMonitor.CameraAgent.Common.Capture.CaptureControlConflictException>(() =>
            fixture.Capture.ResumeAsync("blocked-resume", fixture.Capture.Snapshot.Version, "owner", "resume", CancellationToken.None)).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Paused, fixture.Capture.Snapshot.State);
        await fixture.Controls.ExchangeAsync(new(worker, queued with
        {
            Phase = CameraAgentControlPhase.Failed,
            Events = [.. queued.Events, new(CameraAgentControlPhase.Failed, DateTimeOffset.UtcNow, "drain.timeout")]
        }), CancellationToken.None).ConfigureAwait(false);
        await fixture.Capture.ResumeAsync("explicit-resume", fixture.Capture.Snapshot.Version, "owner", "checked failure", CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(CaptureAdmissionState.Running, fixture.Capture.Snapshot.State);
    }

    private sealed class Fixture(bool neverReturns) : IAsyncDisposable
    {
        internal DirectoryInfo Root { get; } = Directory.CreateTempSubdirectory("hvo-1711-wire-");
        internal CameraAgentControlStore Controls { get; private set; } = null!;
        internal CaptureAdmissionCoordinator Capture { get; private set; } = null!;
        internal HttpMessageHandler Handler => _handler ??= new EndpointHandler(this);
        internal int RestartCalls;
        internal bool LoseResumeResponse, ResumeFenceHeld;
        private readonly List<WebApplication> _hosts = [];
        private readonly List<IDisposable> _owned = [];
        private EndpointHandler? _handler;
        private volatile bool _stopped;
        private SimulatedLifetime _lifetime = null!;
        private readonly bool _neverReturns = neverReturns;

        internal async Task InitializeAsync()
        {
            var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = Root.FullName, RawIngressSqliteBusyTimeoutSeconds = 1, CentralIntegration = new() { Mode = CentralIntegrationMode.Disabled } });
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
            builder.Services.AddSingleton<TimeProvider>(new FastRestartClock());
            _lifetime = new SimulatedLifetime(() => { Interlocked.Increment(ref RestartCalls); _stopped = true; });
            builder.Services.AddSingleton<ICameraAgentControlRestartWindow>(new SafeWindow());
            var raw = new RawIngressState(TimeProvider.System);
            raw.Set(RawIngressAvailability.Accepting, "observed-empty");
            var lanes = new CaptureLaneState(TimeProvider.System, options);
            lanes.Update([]);
            var processing = new CaptureProcessingState();
            processing.SetDurable(0, 0, 0, 0, null);
            builder.Services.AddSingleton(new CameraAgentOperationsSummaryProvider(TimeProvider.System, Capture,
                raw, new NoQueueRefresh(), lanes,
                processing, new ArtifactOutboxState(), new StoragePressureState(), new FleetRuntimeState(TimeProvider.System),
                new FleetHeartbeatState(), new EnvironmentalObservationDeliveryState(), new ExecutionEvidenceExportState(),
                new TransientWorkerState(TimeProvider.System), new CaptureTelemetrySink(), configuration,
                new CameraAgentStorageResolver(configuration, options), options));
            builder.Services.AddSingleton(new DeploymentContinuityReader());
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(new SqliteLocalAutomationStore(new StubAutomationTaskRegistry(), new StubCaptureSequenceSource(),
                options, TimeProvider.System, NullLogger<SqliteLocalAutomationStore>.Instance));
            var app = builder.Build();
            app.MapCameraAgentLifecycleEndpoints();
            app.MapCameraAgentControlLifecycleEndpoints();
            _hosts.Add(app);
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
                    await owner.InitializeAsync().ConfigureAwait(false);
                }
                var app = owner._hosts[^1];
                var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>()
                    .Single(item => item.RoutePattern.RawText == request.RequestUri!.AbsolutePath);
                using var scope = app.Services.CreateScope();
                var context = new DefaultHttpContext { RequestServices = new LifetimeProvider(scope.ServiceProvider, owner._lifetime), RequestAborted = cancellationToken };
                context.Request.Method = request.Method.Method;
                foreach (var header in request.Headers) context.Request.Headers[header.Key] = header.Value.ToArray();
                var content = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                context.Request.Body = new MemoryStream(content);
                context.Request.ContentLength = content.Length;
                context.Request.ContentType = "application/json";
                context.Features.Set<IHttpRequestBodyDetectionFeature>(new HasBody(content.Length > 0));
                using var output = new MemoryStream();
                context.Response.Body = output;
                await endpoint.RequestDelegate!(context).ConfigureAwait(false);
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
    private sealed class SafeWindow : ICameraAgentControlRestartWindow
    {
        public Task<bool> IsSafeAsync(CancellationToken cancellationToken) => Task.FromResult(true);
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
