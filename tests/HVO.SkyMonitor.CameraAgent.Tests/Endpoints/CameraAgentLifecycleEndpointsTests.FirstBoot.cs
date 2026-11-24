using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Distribution;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>The lifecycle-token routes of the Silo-layout first-boot pause and its atomic first resume (#1966).</summary>
public sealed partial class CameraAgentLifecycleEndpointsTests
{
    [TestMethod]
    public async Task FirstBootRoutes_ReportSeedRefuseWrongIdentityAndResumeAtomicallyAsync()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-endpoint-");
        var id = Guid.NewGuid();
        try
        {
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = CreateSeededCoordinator(root.FullName, id, telemetry);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var app = BuildApp(coordinator);
            await using var appLifetime = app.ConfigureAwait(false);

            var (status, body) = await InvokeAsync(app, HttpMethods.Get, "/api/internal/deployment/lifecycle/first-boot", null, "exact-token")
                .ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status);
            using (var document = JsonDocument.Parse(body))
            {
                Assert.IsTrue(document.RootElement.GetProperty("inForce").GetBoolean());
                Assert.AreEqual(1L, document.RootElement.GetProperty("version").GetInt64());
            }

            (status, _) = await InvokeAsync(app, HttpMethods.Get, "/api/internal/deployment/lifecycle/first-boot", null, "wrong-token")
                .ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, status);

            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/first-resume",
                new { firstBootId = Guid.NewGuid(), expectedVersion = 1 }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status400BadRequest, status);
            Assert.AreEqual(CaptureAdmissionState.Paused, coordinator.Snapshot.State);

            (status, body) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/first-resume",
                new { firstBootId = id, expectedVersion = 1 }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status, body);
            Assert.AreEqual(CaptureAdmissionState.Running, coordinator.Snapshot.State);
        }
        finally
        {
            // Pooled connections stay open beside parallel tests; Linux deletes the directory regardless.
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task FirstResume_AfterOperatorNoOpPause_IsConflictAndChangesNothingAsync()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-endpoint-");
        var id = Guid.NewGuid();
        try
        {
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = CreateSeededCoordinator(root.FullName, id, telemetry);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            _ = await coordinator.PauseAsync("ui-pause", 1, "owner-1", "hold", CancellationToken.None).ConfigureAwait(false);
            var app = BuildApp(coordinator);
            await using var appLifetime = app.ConfigureAwait(false);

            var (status, body) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/first-resume",
                new { firstBootId = id, expectedVersion = 1 }, "exact-token").ConfigureAwait(false);

            Assert.AreEqual(StatusCodes.Status409Conflict, status);
            StringAssert.Contains(body, "superseded", StringComparison.Ordinal);
            Assert.AreEqual(CaptureAdmissionState.Paused, coordinator.Snapshot.State);
            Assert.AreEqual(1L, coordinator.Snapshot.Version);
        }
        finally
        {
            // Pooled connections stay open beside parallel tests; Linux deletes the directory regardless.
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task FirstBootRoutes_WithoutSeed_ReportNotFoundAsync()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-endpoint-");
        try
        {
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = CreateSeededCoordinator(root.FullName, null, telemetry);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var app = BuildApp(coordinator);
            await using var appLifetime = app.ConfigureAwait(false);

            var (status, _) = await InvokeAsync(app, HttpMethods.Get, "/api/internal/deployment/lifecycle/first-boot", null, "exact-token")
                .ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status404NotFound, status);
            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/first-resume",
                new { firstBootId = Guid.NewGuid(), expectedVersion = 1 }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status404NotFound, status);
            Assert.AreEqual(CaptureAdmissionState.Running, coordinator.Snapshot.State);
        }
        finally
        {
            // Pooled connections stay open beside parallel tests; Linux deletes the directory regardless.
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task LifecycleRoutes_UnderFirstBootProfile_ShareTheStableOwnerAcrossCommandIdsAsync()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-endpoint-");
        var id = Guid.NewGuid();
        try
        {
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = CreateSeededCoordinator(root.FullName, id, telemetry);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var app = BuildApp(coordinator);
            await using var appLifetime = app.ConfigureAwait(false);
            var (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/first-resume",
                new { firstBootId = id, expectedVersion = 1 }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status);

            // The installer client's shape: a fresh command identity per request and one stable owner operation.
            var operation = Guid.NewGuid();
            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/pause",
                new { operationId = Guid.NewGuid(), ownerOperationId = operation }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status);
            Assert.AreEqual(CaptureAdmissionState.Paused, coordinator.Snapshot.State);
            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/resume",
                new { operationId = Guid.NewGuid(), ownerOperationId = operation }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status);
            Assert.AreEqual(CaptureAdmissionState.Running, coordinator.Snapshot.State);

            // A pause by another operation, then a later operator pause: neither foreign installer resume succeeds.
            var other = Guid.NewGuid();
            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/pause",
                new { operationId = Guid.NewGuid(), ownerOperationId = other }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status);
            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/resume",
                new { operationId = Guid.NewGuid(), ownerOperationId = operation }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status409Conflict, status);
            _ = await coordinator.PauseAsync("ui-pause", null, "owner-1", "hold", CancellationToken.None).ConfigureAwait(false);
            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/resume",
                new { operationId = Guid.NewGuid(), ownerOperationId = other }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status409Conflict, status);
            Assert.AreEqual(CaptureAdmissionState.Paused, coordinator.Snapshot.State);
        }
        finally
        {
            // Pooled connections stay open beside parallel tests; Linux deletes the directory regardless.
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task LifecycleRoutes_WithoutFirstBootProfile_KeepTheCommandIdentityAsOwnerAsync()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-endpoint-");
        try
        {
            using var telemetry = new CaptureControlTelemetry();
            using var coordinator = CreateSeededCoordinator(root.FullName, null, telemetry);
            await coordinator.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
            var app = BuildApp(coordinator);
            await using var appLifetime = app.ConfigureAwait(false);
            var command = Guid.NewGuid();
            var (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/pause",
                new { operationId = command, ownerOperationId = Guid.NewGuid() }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status);
            (status, _) = await InvokeAsync(app, HttpMethods.Post, "/api/internal/deployment/lifecycle/resume",
                new { operationId = Guid.NewGuid(), ownerOperationId = Guid.NewGuid() }, "exact-token").ConfigureAwait(false);
            Assert.AreEqual(StatusCodes.Status200OK, status, "A filesystem profile resumes as it always has.");
            using var connection = new SqliteConnection($"Data Source={Path.Combine(root.FullName, "journal", "raw-ingress.db")};Pooling=False");
            await connection.OpenAsync().ConfigureAwait(false);
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT actor FROM capture_control_commands WHERE target_state = 'paused';";
            Assert.AreEqual($"installer-lifecycle:{command:D}", await read.ExecuteScalarAsync().ConfigureAwait(false) as string);
        }
        finally
        {
            // Pooled connections stay open beside parallel tests; Linux deletes the directory regardless.
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public async Task FirstBootStateInitializer_PersistsIdempotentlyAndRefusesOtherStateAsync()
    {
        var root = Directory.CreateTempSubdirectory("hvo-first-boot-initializer-");
        var seed = new FirstBootPauseIdentity(Guid.NewGuid());
        try
        {
            var first = await FirstBootStateInitializer.InitializeAsync(root.FullName, 1, seed, CancellationToken.None).ConfigureAwait(false);
            Assert.IsTrue(first.Seeded && first.InForce && !first.Resumed);
            Assert.AreEqual("paused", first.State);
            Assert.AreEqual(1L, first.Version);
            var again = await FirstBootStateInitializer.InitializeAsync(root.FullName, 1, seed, CancellationToken.None).ConfigureAwait(false);
            Assert.AreEqual(first, again);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
                await FirstBootStateInitializer.InitializeAsync(root.FullName, 1, new FirstBootPauseIdentity(Guid.NewGuid()),
                    CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        finally
        {
            // Pooled connections stay open beside parallel tests; Linux deletes the directory regardless.
            root.Delete(recursive: true);
        }
    }

    private static CaptureAdmissionCoordinator CreateSeededCoordinator(string root, Guid? id, CaptureControlTelemetry telemetry)
        => new(
            new SeededJournalIngress(Path.Combine(root, "journal", "raw-ingress.db"), new FirstBootPauseOptions { Id = id }.Identity),
            Options.Create(new CameraAgentHostOptions
            {
                RawIngressRoot = root,
                RawIngressSqliteBusyTimeoutSeconds = 1,
                FirstBootPause = new FirstBootPauseOptions { Id = id }
            }),
            TimeProvider.System,
            telemetry);

    private static WebApplication BuildApp(CaptureAdmissionCoordinator coordinator)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["LifecycleControl:Token"] = "exact-token";
        builder.Services.AddSingleton(coordinator);
        // The group's state route binds these services; the first-boot routes never use them.
        builder.Services.AddSingleton<HVO.SkyMonitor.CameraAgent.Common.Operations.CameraAgentOperationsSummaryProvider>(_ => null!);
        builder.Services.AddSingleton<HVO.SkyMonitor.CameraAgent.Common.Deployment.DeploymentContinuityReader>(_ => null!);
        builder.Services.AddSingleton(Options.Create(new CameraAgentHostOptions()));
        var app = builder.Build();
        app.MapCameraAgentLifecycleEndpoints();
        return app;
    }

    private static async Task<(int Status, string Body)> InvokeAsync(
        WebApplication app, string method, string route, object? payload, string token)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => string.Equals(candidate.RoutePattern.RawText, route, StringComparison.Ordinal) &&
                                 candidate.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains(method));
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = method;
        context.Request.Headers["X-HVO-Installation-Token"] = token;
        if (payload is not null)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
            context.Request.Body = new MemoryStream(bytes);
            context.Request.ContentLength = bytes.Length;
            context.Request.ContentType = "application/json";
            context.Features.Set<IHttpRequestBodyDetectionFeature>(RequestBodyDetectionFeature.Instance);
        }
        var response = new MemoryStream();
        context.Response.Body = response;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(response.ToArray()));
    }

    private sealed class SeededJournalIngress(string path, FirstBootPauseIdentity? firstBootPause) : IRawCaptureIngress
    {
        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
            => await new SqliteRawCaptureJournal(path, 1, firstBootPause: firstBootPause).InitializeAsync(cancellationToken)
                .ConfigureAwait(false);

        public ValueTask<RawCaptureReceipt?> AcceptAsync(
            CameraModuleConfig configuration,
            CaptureLoopSubmission submission,
            CancellationToken cancellationToken)
            => ValueTask.FromResult<RawCaptureReceipt?>(null);
    }
}
