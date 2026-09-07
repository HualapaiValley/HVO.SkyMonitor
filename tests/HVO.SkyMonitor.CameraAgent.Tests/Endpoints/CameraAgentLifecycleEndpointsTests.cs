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
