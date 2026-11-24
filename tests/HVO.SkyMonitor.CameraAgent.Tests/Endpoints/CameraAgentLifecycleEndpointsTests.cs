using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Environmental;
using HVO.SkyMonitor.CameraAgent.Common.Evidence;
using HVO.SkyMonitor.CameraAgent.Common.Fleet;
using HVO.SkyMonitor.CameraAgent.Common.Storage;
using HVO.SkyMonitor.CameraAgent.Common.Telemetry;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using HVO.SkyMonitor.CameraAgent.Common.Upload;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using HVO.SkyMonitor.Deployment;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentLifecycleEndpointsTests
{
    [TestMethod]
    [DataRow(null, false)]
    [DataRow("wrong-token", false)]
    [DataRow("exact-token", true)]
    public void IsAuthorized_RequiresExactConfiguredToken(string? supplied, bool expected)
    {
        var context = new DefaultHttpContext();
        if (supplied is not null) context.Request.Headers["X-HVO-Installation-Token"] = supplied;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LifecycleControl:Token"] = "exact-token" })
            .Build();

        Assert.AreEqual(expected, CameraAgentLifecycleEndpoints.IsAuthorized(context, configuration));
    }

    [TestMethod]
    public async Task Pause_WhenCaptureAdmissionIsStillInitializing_ReturnsRetryableUnavailable()
    {
        var root = Directory.CreateTempSubdirectory("hvo-lifecycle-endpoint-");
        try
        {
            var ingress = new RecordingIngress();
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = new CaptureAdmissionCoordinator(
                ingress,
                Options.Create(new CameraAgentHostOptions
                {
                    RawIngressRoot = root.FullName,
                    RawIngressSqliteBusyTimeoutSeconds = 1
                }),
                TimeProvider.System,
                telemetry);
            var builder = WebApplication.CreateBuilder();
            builder.Configuration["LifecycleControl:Token"] = "exact-token";
            builder.Services.AddSingleton(coordinator);
            builder.Services.AddSingleton<CameraAgentOperationsSummaryProvider>(_ => null!);
            builder.Services.AddSingleton<DeploymentContinuityReader>(_ => null!);
            builder.Services.AddSingleton(Options.Create(new CameraAgentHostOptions()));
            using var app = builder.Build();
            app.MapCameraAgentLifecycleEndpoints();
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Single(static candidate => string.Equals(
                    candidate.RoutePattern.RawText,
                    "/api/internal/deployment/lifecycle/pause",
                    StringComparison.Ordinal));
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.Request.Method = HttpMethods.Post;
            context.Request.Headers["X-HVO-Installation-Token"] = "exact-token";
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { operationId = Guid.NewGuid() });
            context.Request.Body = new MemoryStream(payload);
            context.Request.ContentLength = payload.Length;
            context.Request.ContentType = "application/json";
            context.Response.Body = new MemoryStream();
            context.Features.Set<IHttpRequestBodyDetectionFeature>(RequestBodyDetectionFeature.Instance);

            await endpoint.RequestDelegate!(context).ConfigureAwait(false);

            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
            Assert.AreEqual("1", context.Response.Headers.RetryAfter.ToString());
            Assert.AreEqual(0, ingress.InitializeAttempts);
            Assert.IsFalse(coordinator.Snapshot.IsInitialized);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    [DataRow(true, "exact-token", true)]
    [DataRow(false, "exact-token", false)]
    [DataRow(true, "wrong-token", false)]
    public void OwnerRecovery_RequiresExactUnixSocketAndLifecycleCredential(
        bool localSocket,
        string suppliedToken,
        bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-HVO-Installation-Token"] = suppliedToken;
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["LifecycleControl:Token"] = "exact-token" })
            .Build();
        const string socketPath = "/tmp/hvo-owner-recovery-test.sock";
        var transport = new OwnerRecoveryTransport(socketPath);
        EndPoint endpoint = localSocket
            ? new UnixDomainSocketEndPoint(socketPath)
            : new IPEndPoint(IPAddress.Loopback, 5130);

        Assert.AreEqual(
            expected,
            IdentityComponentsEndpointRouteBuilderExtensions.IsRecoveryAuthorized(
                context,
                configuration,
                transport,
                endpoint));
    }

    [TestMethod]
    [DataRow(false, "operator-image")]
    [DataRow(true, "operator-image")]
    [DataRow(false, "signed")]
    [DataRow(true, "signed")]
    [DataRow(false, "signed-race")]
    [DataRow(false, "signed-wrong-operation")]
    [DataRow(false, "legacy-race-control")]
    [DataRow(false, "committed-race")]
    [DataRow(true, "committed")]
    public async Task RecoveryClient_FencesPauseOwnershipAndReplaysDurableReceipts(bool restart, string mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        var root = Directory.CreateTempSubdirectory("hvo-recovery-http-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        using var telemetry = new CaptureControlTelemetry();
        CaptureAdmissionCoordinator? coordinator = null;
        WebApplication? app = null;
        var journalPath = Path.Combine(root.FullName, "journal", "raw-ingress.db");
        var operationId = Guid.NewGuid();
        var pauseCommandId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var committed = mode.StartsWith("committed", StringComparison.Ordinal);
        var signed = committed || mode.StartsWith("signed", StringComparison.Ordinal);
        using var lostAcknowledgement = new LostAcknowledgementHandler();
        try
        {
            async Task<CameraAgentLifecycleClient> StartAsync()
            {
                coordinator = new CaptureAdmissionCoordinator(new JournalIngress(journalPath),
                    Options.Create(new CameraAgentHostOptions { RawIngressRoot = root.FullName, RawIngressSqliteBusyTimeoutSeconds = 1 }),
                    TimeProvider.System, telemetry);
                await coordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);
                var builder = WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Configuration["LifecycleControl:Token"] = "exact-token";
                builder.Services.AddSingleton(coordinator);
                builder.Services.AddSingleton<CameraAgentOperationsSummaryProvider>(_ => null!);
                builder.Services.AddSingleton<DeploymentContinuityReader>(_ => null!);
                builder.Services.AddSingleton(Options.Create(new CameraAgentHostOptions()));
                app = builder.Build();
                app.MapCameraAgentLifecycleEndpoints();
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
                return new CameraAgentLifecycleClient(new Uri(app.Urls.Single()), budgets: new LifecycleBudgets(
                    TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10)));
            }

            var client = await StartAsync().ConfigureAwait(false);
            if (signed)
            {
                await coordinator!.PauseAsync($"lifecycle:{pauseCommandId:D}:pause", null, $"installer-lifecycle:{pauseCommandId:D}",
                    $"transactional lifecycle operation {operationId:D} pause", cancellationToken).ConfigureAwait(false);
                await Assert.ThrowsExactlyAsync<InstallerException>(() => client.VerifyOwnedUpgradeResumeAsync("wrong-token", cancellationToken))
                    .ConfigureAwait(false);
                await client.VerifyOwnedUpgradeResumeAsync("exact-token", cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await coordinator!.PauseAsync("upgrade-pause", 0, "installer", "upgrade", cancellationToken).ConfigureAwait(false);
            }
            Assert.AreEqual(2L, coordinator.Snapshot.Version);
            if (mode is "signed-race" or "legacy-race-control" or "committed-race")
            {
                // Land an accepted no-op in the exact interval between CLI admission checks and the resume request.
                var later = await coordinator.PauseAsync("operator-no-op", 2, "owner", "retain the pause", cancellationToken)
                    .ConfigureAwait(false);
                Assert.IsFalse(later.Changed);
                Assert.AreEqual(2L, later.Version);
            }
            if (mode == "legacy-race-control")
            {
                var resumed = await client.ResumeRecoveryAsync(operationId, commandId, 2, "exact-token", cancellationToken)
                    .ConfigureAwait(false);
                Assert.IsTrue(resumed.Changed);
                Assert.AreEqual(CaptureAdmissionState.Running, coordinator.Snapshot.State);
                Assert.AreEqual(3L, coordinator.Snapshot.Version);
                return;
            }
            if (mode is "signed-race" or "signed-wrong-operation" or "committed-race")
            {
                if (committed)
                {
                    var disposition = await client.TryResumeOwnedUpgradeAsync(
                        operationId, pauseCommandId, commandId, 2, "exact-token", cancellationToken).ConfigureAwait(false);
                    Assert.IsTrue(disposition.Supported);
                    Assert.IsNull(disposition.Receipt);
                }
                else
                {
                    var attemptedOperation = mode == "signed-race" ? operationId : Guid.NewGuid();
                    await Assert.ThrowsExactlyAsync<InstallerException>(() => client.ResumeOwnedUpgradeAsync(
                        attemptedOperation, pauseCommandId, commandId, 2, "exact-token", cancellationToken)).ConfigureAwait(false);
                }
                Assert.AreEqual(2L, coordinator.Snapshot.Version);
                Assert.AreEqual(CaptureAdmissionState.Paused, coordinator.Snapshot.State);
                var commands = await coordinator.GetRecentCommandsAsync(5, cancellationToken).ConfigureAwait(false);
                Assert.AreEqual(mode == "signed-wrong-operation" ? 1 : 2, commands.Count);
                Assert.IsTrue(commands.All(command => command.TargetState == CaptureAdmissionState.Paused));
                return;
            }

            async Task<LifecycleResumeReceipt> ResumeAsync(CameraAgentLifecycleClient transport, Guid id, string token)
            {
                if (committed)
                {
                    var disposition = await transport.TryResumeOwnedUpgradeAsync(
                        operationId, pauseCommandId, id, 2, token, cancellationToken).ConfigureAwait(false);
                    return disposition.Receipt ?? throw new AssertFailedException("Expected the committed resume receipt.");
                }
                return signed
                    ? await transport.ResumeOwnedUpgradeAsync(operationId, pauseCommandId, id, 2, token, cancellationToken).ConfigureAwait(false)
                    : await transport.ResumeRecoveryAsync(operationId, id, 2, token, cancellationToken).ConfigureAwait(false);
            }

            var interruptedClient = new CameraAgentLifecycleClient(new Uri(app!.Urls.Single()), lostAcknowledgement);
            await Assert.ThrowsExactlyAsync<InstallerException>(() => ResumeAsync(interruptedClient, commandId, "exact-token")).ConfigureAwait(false);
            Assert.AreEqual(3L, coordinator.Snapshot.Version);
            await coordinator.PauseAsync("operator-pause", 3, "owner", "remain paused", cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(5L, coordinator.Snapshot.Version);
            if (restart)
            {
                await app!.StopAsync(cancellationToken).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
                app = null;
                coordinator.Dispose();
                client = await StartAsync().ConfigureAwait(false);
            }
            await Assert.ThrowsExactlyAsync<InstallerException>(() => ResumeAsync(client, commandId, "wrong-token")).ConfigureAwait(false);
            var receipt = await ResumeAsync(client, commandId, "exact-token").ConfigureAwait(false);
            Assert.IsTrue(receipt.Replayed);
            Assert.IsTrue(receipt.Changed);
            Assert.AreEqual("Running", receipt.State);
            Assert.AreEqual(3L, receipt.Version);
            Assert.AreEqual(CaptureAdmissionState.Paused, coordinator!.Snapshot.State);
            Assert.AreEqual(5L, coordinator.Snapshot.Version);
            Assert.IsTrue(coordinator.Snapshot.IsInitialized);
            await ResumeAsync(client, commandId, "exact-token").ConfigureAwait(false);
            if (committed)
            {
                var differentRequest = await client.TryResumeOwnedUpgradeAsync(operationId, pauseCommandId, Guid.NewGuid(),
                    2, "exact-token", cancellationToken).ConfigureAwait(false);
                Assert.IsTrue(differentRequest.Supported);
                Assert.IsNull(differentRequest.Receipt);
            }
            else
            {
                await Assert.ThrowsExactlyAsync<InstallerException>(() => ResumeAsync(client, Guid.NewGuid(), "exact-token")).ConfigureAwait(false);
            }
            using var connection = new SqliteConnection($"Data Source={journalPath};Pooling=False");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM capture_control_commands;";
            Assert.AreEqual(3L, await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT state || ':' || version FROM capture_control_state;";
            Assert.AreEqual("paused:5", await verify.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
            coordinator?.Dispose();
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task State_DeclaresCatalogIndependentReplayToTheLifecycleClient()
    {
        var root = Directory.CreateTempSubdirectory("hvo-lifecycle-state-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        using var telemetry = new CaptureControlTelemetry();
        var options = Options.Create(new CameraAgentHostOptions { RawIngressRoot = root.FullName, RawIngressSqliteBusyTimeoutSeconds = 1 });
        using var coordinator = new CaptureAdmissionCoordinator(
            new JournalIngress(Path.Combine(root.FullName, "journal", "raw-ingress.db")), options, TimeProvider.System, telemetry);
        WebApplication? app = null;
        try
        {
            await coordinator.InitializeAsync(cancellationToken).ConfigureAwait(false);
            var configuration = new CameraAgentConfigurationAccessor();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration["LifecycleControl:Token"] = "exact-token";
            builder.Services.AddSingleton(coordinator);
            builder.Services.AddSingleton(new CameraAgentOperationsSummaryProvider(
                TimeProvider.System,
                coordinator,
                new RawIngressState(TimeProvider.System),
                new NoQueueSnapshotRefresh(),
                new CaptureLaneState(TimeProvider.System, options),
                new CaptureProcessingState(),
                new ArtifactOutboxState(),
                new StoragePressureState(),
                new FleetRuntimeState(TimeProvider.System),
                new FleetHeartbeatState(),
                new EnvironmentalObservationDeliveryState(),
                new ExecutionEvidenceExportState(),
                new TransientWorkerState(TimeProvider.System),
                new CaptureTelemetrySink(),
                configuration,
                new CameraAgentStorageResolver(configuration, options),
                options));
            builder.Services.AddSingleton(new DeploymentContinuityReader());
            builder.Services.AddSingleton(options);
            app = builder.Build();
            app.MapCameraAgentLifecycleEndpoints();
            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            // The lifecycle CLI refuses catalog transitions unless it reads this declaration, so the wire name the host
            // serializes and the name the client parses must agree.
            var state = await new CameraAgentLifecycleClient(new Uri(app.Urls.Single()))
                .ReadContinuityAsync("exact-token", cancellationToken).ConfigureAwait(false);

            Assert.IsTrue(state.ReplayCatalogIndependent);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
            root.Delete(recursive: true);
        }
    }

    private sealed class LostAcknowledgementHandler : DelegatingHandler
    {
        public LostAcknowledgementHandler() : base(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.Method != HttpMethod.Post) return response;
            using (response)
            {
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException("simulated lost acknowledgement after durable HTTP command");
            }
        }
    }

    private sealed class JournalIngress(string path) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(path, 1).InitializeAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(CameraModuleConfig configuration, CaptureLoopSubmission submission, CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class RecordingIngress : IRawCaptureIngress
    {
        internal int InitializeAttempts { get; private set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            InitializeAttempts++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }

    private sealed class NoQueueSnapshotRefresh : IOperationsQueueSnapshotRefresher
    {
        public ValueTask RefreshOperationsQueueSnapshotsAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        internal static RequestBodyDetectionFeature Instance { get; } = new();

        public bool CanHaveBody => true;
    }
}
