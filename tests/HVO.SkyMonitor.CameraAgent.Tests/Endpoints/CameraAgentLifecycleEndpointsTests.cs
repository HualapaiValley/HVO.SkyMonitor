using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task RecoveryClient_ReplaysDurableReceiptOverHttpWithoutUndoingLaterPause(bool restart)
    {
        var root = Directory.CreateTempSubdirectory("hvo-recovery-http-");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellationToken = deadline.Token;
        using var telemetry = new CaptureControlTelemetry();
        CaptureAdmissionCoordinator? coordinator = null;
        WebApplication? app = null;
        var journalPath = Path.Combine(root.FullName, "journal", "raw-ingress.db");
        var operationId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
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
            await coordinator!.PauseAsync("upgrade-pause", 0, "installer", "upgrade", cancellationToken).ConfigureAwait(false);
            Assert.AreEqual(2L, coordinator.Snapshot.Version);
            var interruptedClient = new CameraAgentLifecycleClient(new Uri(app!.Urls.Single()), lostAcknowledgement);
            await Assert.ThrowsExactlyAsync<InstallerException>(() => interruptedClient.ResumeRecoveryAsync(
                operationId, commandId, 2, "exact-token", cancellationToken)).ConfigureAwait(false);
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
            await Assert.ThrowsExactlyAsync<InstallerException>(() => client.ResumeRecoveryAsync(
                operationId, commandId, 2, "wrong-token", cancellationToken)).ConfigureAwait(false);
            var receipt = await client.ResumeRecoveryAsync(operationId, commandId, 2, "exact-token", cancellationToken).ConfigureAwait(false);
            Assert.IsTrue(receipt.Replayed);
            Assert.IsTrue(receipt.Changed);
            Assert.AreEqual("Running", receipt.State);
            Assert.AreEqual(3L, receipt.Version);
            Assert.AreEqual(CaptureAdmissionState.Paused, coordinator!.Snapshot.State);
            Assert.AreEqual(5L, coordinator.Snapshot.Version);
            Assert.IsTrue(coordinator.Snapshot.IsInitialized);
            await client.ResumeRecoveryAsync(operationId, commandId, 2, "exact-token", cancellationToken).ConfigureAwait(false);
            await Assert.ThrowsExactlyAsync<InstallerException>(() => client.ResumeRecoveryAsync(
                operationId, Guid.NewGuid(), 2, "exact-token", cancellationToken)).ConfigureAwait(false);
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
            SqliteConnection.ClearAllPools();
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
            using var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            throw new HttpRequestException("simulated lost acknowledgement after durable HTTP command");
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

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        internal static RequestBodyDetectionFeature Instance { get; } = new();

        public bool CanHaveBody => true;
    }
}
