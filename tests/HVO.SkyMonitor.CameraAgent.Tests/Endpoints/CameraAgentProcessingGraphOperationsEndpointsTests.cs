using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "WebApplication must remain available for the full test scope.")]
public sealed class CameraAgentProcessingGraphOperationsEndpointsTests
{
    private static readonly string[] ContentMethods = ["GET", "HEAD"];

    [TestMethod]
    public async Task RoutesRequireOperationsPolicyAndMutationsRequireAntiforgery()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Mock.Of<IProcessingGraphOperations>());
        builder.Services.AddSingleton(Mock.Of<ICameraAgentArtifactService>());
        var app = builder.Build();
        try
        {
            app.MapCameraAgentProcessingGraphOperationsEndpoints();

            var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/v1/operations/processing-graphs", StringComparison.Ordinal) == true)
            .ToArray();
            Assert.HasCount(11, routes);
            foreach (var route in routes)
            {
                var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                Assert.IsNotNull(methods);
                var readOnly = methods.All(static method =>
                    string.Equals(method, "GET", StringComparison.Ordinal) ||
                    string.Equals(method, "HEAD", StringComparison.Ordinal));
                var authorization = route.Metadata.GetMetadata<IAuthorizeData>();
                Assert.IsNotNull(authorization);
                Assert.AreEqual(
                    readOnly
                        ? CameraAgentAuthorizationPolicyNames.OperationsReadV1
                        : CameraAgentAuthorizationPolicyNames.OperationsMutateV1,
                    authorization.Policy);
                Assert.AreEqual(
                    !readOnly,
                    route.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation == true);
            }
            var outputContent = routes.Single(static route => string.Equals(
                route.RoutePattern.RawText,
                "/api/v1/operations/processing-graphs/executions/{executionId:guid}/outputs/{artifactId:guid}/content",
                StringComparison.Ordinal));
            CollectionAssert.AreEquivalent(
                ContentMethods,
                outputContent.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ToArray());
            Assert.AreEqual(
                "GetCameraAgentProcessingExecutionOutputContent",
                outputContent.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task EveryRouteExecutesItsOperationAndUsesExpectedSuccessStatus()
    {
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Loose);
        var artifacts = new Mock<ICameraAgentArtifactService>(MockBehavior.Strict);
        artifacts.Setup(value => value.OpenReplayOutputContentAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromResult(new CameraAgentArtifactContentResult(
                CameraAgentArtifactReadStatus.NotFound)));
        await using var app = CreateApp(operations.Object, artifacts.Object);
        var revisionId = "revision-1";
        var executionId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();

        Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(
            app, "GetCameraAgentProcessingGraphRegistry", HttpMethods.Get).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(
            app,
            "CreateCameraAgentProcessingGraphRevision",
            HttpMethods.Post,
            new
            {
                name = "test",
                revision = "1",
                pipeline = new CapturePipelineConfig([]),
                reason = "test"
            }).ConfigureAwait(false));
        foreach (var endpoint in new[]
        {
            "ActivateCameraAgentProcessingGraphRevision",
            "RollbackCameraAgentProcessingGraphRevision",
            "ValidateCameraAgentProcessingGraphRevision",
            "RetireCameraAgentProcessingGraphRevision"
        })
        {
            Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(
                app,
                endpoint,
                HttpMethods.Post,
                new { expectedVersion = 1, reason = "test" },
                new() { ["revisionId"] = revisionId }).ConfigureAwait(false));
        }
        Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(
            app, "GetCameraAgentProcessingExecutions", HttpMethods.Get).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status404NotFound, await InvokeAsync(
            app,
            "GetCameraAgentProcessingExecution",
            HttpMethods.Get,
            routeValues: new() { ["executionId"] = executionId.ToString() }).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status404NotFound, await InvokeAsync(
            app,
            "GetCameraAgentProcessingExecutionOutputContent",
            HttpMethods.Get,
            routeValues: new()
            {
                ["executionId"] = executionId.ToString(),
                ["artifactId"] = artifactId.ToString()
            }).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status202Accepted, await InvokeAsync(
            app,
            "SubmitCameraAgentProcessingReplay",
            HttpMethods.Post,
            new
            {
                captureId = Guid.NewGuid(),
                graphRevisionId = revisionId,
                primaryArtifactId = artifactId,
                triggerKind = "operator",
                triggerReference = "test",
                priority = 1,
                reason = "test"
            }).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(
            app,
            "CancelCameraAgentProcessingReplay",
            HttpMethods.Post,
            new { reason = "test" },
            new() { ["executionId"] = executionId.ToString() }).ConfigureAwait(false));

        operations.Verify(value => value.GetRegistryAsync(It.IsAny<CancellationToken>()), Times.Once);
        operations.Verify(value => value.CreateRevisionAsync(
            "test", "1", It.IsAny<CapturePipelineConfig>(), It.IsAny<string>(), "owner", "test",
            It.IsAny<CancellationToken>()), Times.Once);
        operations.Verify(value => value.SubmitReplayAsync(
            It.IsAny<ProcessingReplaySubmission>(), It.IsAny<string>(), "owner", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [TestMethod]
    public async Task ReadAndMutationFailuresReturnOnlySanitizedStatuses()
    {
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Loose);
        operations.SetupSequence(value => value.GetRegistryAsync(It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException<ProcessingGraphRegistryState>(new IOException("secret")))
            .Returns(() => ValueTask.FromResult<ProcessingGraphRegistryState>(null!));
        operations.SetupSequence(value => value.ReadExecutionsAsync(
                It.IsAny<ProcessingGraphExecutionClass?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException<IReadOnlyList<ProcessingGraphExecutionState>>(
                new ArgumentException("secret")))
            .Returns(() => ValueTask.FromException<IReadOnlyList<ProcessingGraphExecutionState>>(
                new IOException("secret")));
        operations.SetupSequence(value => value.ReadExecutionDetailAsync(
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException<ProcessingGraphExecutionDetail?>(new IOException("secret")))
            .Returns(() => ValueTask.FromResult<ProcessingGraphExecutionDetail?>(CreateExecutionDetail()));
        operations.SetupSequence(value => value.CreateRevisionAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CapturePipelineConfig>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ValueTask.FromException<ProcessingGraphRevisionState>(new ArgumentException("secret")))
            .Returns(() => ValueTask.FromException<ProcessingGraphRevisionState>(new KeyNotFoundException("secret")))
            .Returns(() => ValueTask.FromException<ProcessingGraphRevisionState>(new ProcessingGraphStoreConflictException("secret")))
            .Returns(() => ValueTask.FromException<ProcessingGraphRevisionState>(new ProcessingReplayCapacityException()))
            .Returns(() => ValueTask.FromException<ProcessingGraphRevisionState>(new InvalidDataException("secret")))
            .Returns(() => ValueTask.FromException<ProcessingGraphRevisionState>(new IOException("secret")));
        await using var app = CreateApp(operations.Object, Mock.Of<ICameraAgentArtifactService>());

        Assert.AreEqual(StatusCodes.Status500InternalServerError, await InvokeAsync(
            app, "GetCameraAgentProcessingGraphRegistry", HttpMethods.Get).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(
            app, "GetCameraAgentProcessingGraphRegistry", HttpMethods.Get).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status400BadRequest, await InvokeAsync(
            app, "GetCameraAgentProcessingExecutions", HttpMethods.Get).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status500InternalServerError, await InvokeAsync(
            app, "GetCameraAgentProcessingExecutions", HttpMethods.Get).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status500InternalServerError, await InvokeAsync(
            app,
            "GetCameraAgentProcessingExecution",
            HttpMethods.Get,
            routeValues: new() { ["executionId"] = Guid.NewGuid().ToString() }).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(
            app,
            "GetCameraAgentProcessingExecution",
            HttpMethods.Get,
            routeValues: new() { ["executionId"] = Guid.NewGuid().ToString() }).ConfigureAwait(false));

        var body = new
        {
            name = "test",
            revision = "1",
            pipeline = new CapturePipelineConfig([]),
            reason = "test"
        };
        foreach (var expected in new[]
        {
            StatusCodes.Status400BadRequest,
            StatusCodes.Status404NotFound,
            StatusCodes.Status409Conflict,
            StatusCodes.Status429TooManyRequests,
            StatusCodes.Status422UnprocessableEntity,
            StatusCodes.Status500InternalServerError
        })
        {
            Assert.AreEqual(expected, await InvokeAsync(
                app, "CreateCameraAgentProcessingGraphRevision", HttpMethods.Post, body).ConfigureAwait(false));
        }
        Assert.AreEqual(StatusCodes.Status403Forbidden, await InvokeAsync(
            app,
            "CreateCameraAgentProcessingGraphRevision",
            HttpMethods.Post,
            body,
            user: new ClaimsPrincipal()).ConfigureAwait(false));

        foreach (var endpoint in new[]
        {
            "ActivateCameraAgentProcessingGraphRevision",
            "RollbackCameraAgentProcessingGraphRevision",
            "RetireCameraAgentProcessingGraphRevision"
        })
        {
            Assert.AreEqual(StatusCodes.Status400BadRequest, await InvokeAsync(
                app,
                endpoint,
                HttpMethods.Post,
                new { reason = "missing-version" },
                new() { ["revisionId"] = "revision-1" }).ConfigureAwait(false));
        }
    }

    private static WebApplication CreateApp(
        IProcessingGraphOperations operations,
        ICameraAgentArtifactService artifacts)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(operations);
        builder.Services.AddSingleton(artifacts);
        var app = builder.Build();
        app.MapCameraAgentProcessingGraphOperationsEndpoints();
        return app;
    }

    private static async Task<int> InvokeAsync(
        WebApplication app,
        string endpointName,
        string method,
        object? body = null,
        RouteValueDictionary? routeValues = null,
        ClaimsPrincipal? user = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => string.Equals(
                candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName,
                endpointName,
                StringComparison.Ordinal));
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = user ?? CreateUser()
        };
        context.Request.Method = method;
        if (string.Equals(endpointName, "GetCameraAgentProcessingExecutions", StringComparison.Ordinal))
        {
            context.Request.QueryString = new QueryString("?maximumCount=0");
        }
        context.Response.Body = new MemoryStream();
        if (routeValues is not null)
        {
            context.Request.RouteValues = routeValues;
        }
        if (body is not null)
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(body);
            context.Request.Body = new MemoryStream(payload);
            context.Request.ContentLength = payload.Length;
            context.Request.ContentType = "application/json";
            context.Features.Set<IHttpRequestBodyDetectionFeature>(RequestBodyDetectionFeature.Instance);
        }

        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return context.Response.StatusCode;
    }

    private static ClaimsPrincipal CreateUser()
        => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "owner"),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ], IdentityConstants.ApplicationScheme));

    private static ProcessingGraphExecutionDetail CreateExecutionDetail()
    {
        var execution = new ProcessingGraphExecutionState(
            Guid.NewGuid(), ProcessingGraphExecutionClass.Replay, ProcessingGraphExecutionStatus.Completed,
            Guid.NewGuid(), Guid.NewGuid(), "revision", new string('A', 64), new string('B', 64), new string('C', 64),
            "operator", null, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, false, 1);
        return new(execution, []);
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        internal static RequestBodyDetectionFeature Instance { get; } = new();

        public bool CanHaveBody => true;
    }
}
