using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.Options;
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
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
    Justification = "WebApplication must remain available for the full test scope.")]
public sealed class CameraAgentProcessingGraphOperationsEndpointsTests
{
    private static readonly string[] ContentMethods = ["GET", "HEAD"];
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

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
            Assert.HasCount(12, routes);
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

    /// <summary>
    /// The route exists so a deployment check can read the resolved replay execution configuration
    /// instead of the container environment, which reports what the process was started with rather
    /// than what it resolved (#804). Every value asserted here therefore differs from the
    /// <see cref="ProcessingGraphExecutionOptions"/> default, so a handler that returned defaults, or
    /// that read a different options object, fails rather than passing on a coincidence.
    /// </summary>
    [TestMethod]
    public async Task ReplayCapacityReportsTheResolvedConfigurationRatherThanTheDefaults()
    {
        var hostOptions = new CameraAgentHostOptions
        {
            ProcessingGraphs = new ProcessingGraphExecutionOptions
            {
                ReplayProfile = ReplayExecutionProfile.LocalRunner,
                ReplayMaximumConcurrency = 3,
                ReplayMaximumPendingCount = 17,
                ReplayDeadlineSeconds = 120,
                ReplayMaximumQueueAgeSeconds = 900
            }
        };
        await using var app = CreateApp(
            Mock.Of<IProcessingGraphOperations>(), Mock.Of<ICameraAgentArtifactService>(), hostOptions);

        var (status, payload) = await InvokeForBodyAsync(
            app, "GetCameraAgentReplayCapacity", HttpMethods.Get).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var capacity = JsonSerializer.Deserialize<CameraAgentReplayCapacityResponse>(
            payload, WebJson);
        Assert.IsNotNull(capacity);
        Assert.AreEqual("LocalRunner", capacity.ReplayProfile);
        Assert.AreEqual(3, capacity.MaximumConcurrency);
        Assert.AreEqual(17, capacity.MaximumPendingCount);
        Assert.AreEqual(120, capacity.DeadlineSeconds);
        Assert.AreEqual(900, capacity.MaximumQueueAgeSeconds);
    }

    /// <summary>
    /// The read is configuration and not a probe, so it answers identically whether or not the local
    /// replay runner is reachable and without touching <see cref="IProcessingGraphOperations"/>. The
    /// strict mock is the assertion: any call to the operations boundary throws.
    /// </summary>
    [TestMethod]
    public async Task ReplayCapacityDoesNotTouchTheOperationsBoundary()
    {
        var operations = new Mock<IProcessingGraphOperations>(MockBehavior.Strict);
        await using var app = CreateApp(
            operations.Object,
            Mock.Of<ICameraAgentArtifactService>(),
            new CameraAgentHostOptions());

        var (status, payload) = await InvokeForBodyAsync(
            app, "GetCameraAgentReplayCapacity", HttpMethods.Get).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, status);
        var capacity = JsonSerializer.Deserialize<CameraAgentReplayCapacityResponse>(
            payload, WebJson);
        Assert.IsNotNull(capacity);
        Assert.AreEqual("InProcess", capacity.ReplayProfile);
        operations.VerifyNoOtherCalls();
    }

    private static WebApplication CreateApp(
        IProcessingGraphOperations operations,
        ICameraAgentArtifactService artifacts,
        CameraAgentHostOptions? hostOptions = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(operations);
        builder.Services.AddSingleton(artifacts);
        if (hostOptions is not null)
        {
            builder.Services.AddSingleton<IOptions<CameraAgentHostOptions>>(
                new OptionsWrapper<CameraAgentHostOptions>(hostOptions));
        }
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

    private static async Task<(int Status, string Body)> InvokeForBodyAsync(
        WebApplication app,
        string endpointName,
        string method)
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
            User = CreateUser()
        };
        context.Request.Method = method;
        var body = new MemoryStream();
        context.Response.Body = body;

        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
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
