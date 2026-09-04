using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentDeploymentLocationOperationsEndpointsTests
{
    [TestMethod]
    public void ManualEndpoints_RequireOwnerPoliciesAndAntiforgeryOnTheMutation()
    {
        using var app = CreateApp(new RecordingStore());

        var read = FindEndpoint(app, "GetCameraAgentManualDeploymentLocation");
        var mutate = FindEndpoint(app, "ApplyCameraAgentManualDeploymentLocation");

        CollectionAssert.Contains(
            read.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray(),
            CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        CollectionAssert.Contains(
            mutate.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray(),
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
        Assert.IsNull(read.Metadata.GetMetadata<IAntiforgeryMetadata>());
        Assert.IsTrue(mutate.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
        Assert.AreEqual("/api/v1/operations/deployment-location/manual", read.RoutePattern.RawText);
        Assert.AreEqual("/api/v1/operations/deployment-location/manual", mutate.RoutePattern.RawText);
    }

    [TestMethod]
    public async Task ApplyManual_PassesTheOwnerActorAndIdempotencyKeyToTheProtectedStoreAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            """{"latitudeDegrees":31.5,"longitudeDegrees":-110.25,"elevationMeters":1400,"timeZoneId":"America/Phoenix","expectedVersion":4,"reason":"relocated"}""",
            idempotencyKey: "key-1").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status);
        var request = store.Requests.Single();
        Assert.AreEqual("owner", request.Actor);
        Assert.AreEqual("key-1", request.IdempotencyKey);
        Assert.AreEqual(4L, request.ExpectedVersion);
        Assert.AreEqual(31.5, request.LatitudeDegrees);
        Assert.AreEqual("America/Phoenix", request.TimeZoneId);
        Assert.AreEqual("relocated", request.Reason);
        StringAssert.Contains(response.Body, "Applied", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ApplyManual_WithoutAnExpectedVersion_IsRejectedBeforeTheStoreAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            """{"latitudeDegrees":31.5,"longitudeDegrees":-110.25,"elevationMeters":1400,"timeZoneId":"UTC"}""")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, response.Status);
        Assert.IsEmpty(store.Requests);
    }

    [TestMethod]
    public async Task ApplyManual_WithoutAnOwnerClaim_IsForbiddenBeforeTheStoreAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            """{"latitudeDegrees":31.5,"longitudeDegrees":-110.25,"elevationMeters":1400,"timeZoneId":"UTC","expectedVersion":4}""",
            anonymous: true).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status403Forbidden, response.Status);
        Assert.IsEmpty(store.Requests);
    }

    [TestMethod]
    [DataRow(ManualDeploymentLocationStatus.Conflict, StatusCodes.Status409Conflict)]
    [DataRow(ManualDeploymentLocationStatus.Invalid, StatusCodes.Status400BadRequest)]
    [DataRow(ManualDeploymentLocationStatus.Unchanged, StatusCodes.Status200OK)]
    [DataRow(ManualDeploymentLocationStatus.Replayed, StatusCodes.Status200OK)]
    public async Task ApplyManual_MapsEveryDispositionToItsStatusCodeAsync(
        ManualDeploymentLocationStatus status,
        int expected)
    {
        var store = new RecordingStore { Status = status };
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            """{"latitudeDegrees":31.5,"longitudeDegrees":-110.25,"elevationMeters":1400,"timeZoneId":"UTC","expectedVersion":4}""")
            .ConfigureAwait(false);

        Assert.AreEqual(expected, response.Status);
    }

    [TestMethod]
    public async Task ApplyManual_WhenTheStoreFails_ReturnsASanitizedUnavailableStateAsync()
    {
        var store = new RecordingStore { Throw = true };
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            """{"latitudeDegrees":31.5,"longitudeDegrees":-110.25,"elevationMeters":1400,"timeZoneId":"UTC","expectedVersion":4}""")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, response.Status);
        Assert.IsFalse(response.Body.Contains("/secret/location/path", StringComparison.Ordinal));
        Assert.IsFalse(response.Body.Contains("IOException", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task GetManual_ReturnsTheProtectedStateAndSanitizesFailuresAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var response = await GetAsync(app).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status);
        StringAssert.Contains(response.Body, "knownVersion", StringComparison.Ordinal);

        store.Throw = true;
        var failure = await GetAsync(app).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, failure.Status);
        Assert.IsFalse(failure.Body.Contains("/secret/location/path", StringComparison.Ordinal));
    }

    private static WebApplication CreateApp(IDeploymentLocationStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(store);
        var app = builder.Build();
        app.MapCameraAgentDeploymentLocationOperationsEndpoints();
        return app;
    }

    private static RouteEndpoint FindEndpoint(WebApplication app, string name)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static Task<(int Status, string Body)> GetAsync(WebApplication app)
        => InvokeAsync(app, "GetCameraAgentManualDeploymentLocation", HttpMethods.Get, null, null, false);

    private static Task<(int Status, string Body)> PostAsync(
        WebApplication app,
        string json,
        string? idempotencyKey = null,
        bool anonymous = false)
        => InvokeAsync(
            app, "ApplyCameraAgentManualDeploymentLocation", HttpMethods.Post, json, idempotencyKey, anonymous);

    private static async Task<(int Status, string Body)> InvokeAsync(
        WebApplication app,
        string name,
        string method,
        string? json,
        string? idempotencyKey,
        bool anonymous)
    {
        var endpoint = FindEndpoint(app, name);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = anonymous
                ? new ClaimsPrincipal(new ClaimsIdentity())
                : new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "owner"),
                        new Claim(
                            CanonicalCredentialClaims.AccountTypeClaim,
                            CanonicalCredentialClaims.UserAccountType)
                    ],
                    IdentityConstants.ApplicationScheme))
        };
        context.Request.Method = method;
        if (json is not null)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var stream = new MemoryStream(payload);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = payload.Length;
            context.Request.Body = stream;
            // DefaultHttpContext carries neither feature, and minimal-API body binding needs both.
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());
            context.Features.Set<IRequestBodyPipeFeature>(new BodyPipeFeature(stream));
        }
        if (idempotencyKey is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKey;
        }
        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class BodyPipeFeature(Stream stream) : IRequestBodyPipeFeature
    {
        public PipeReader Reader { get; } = PipeReader.Create(stream);
    }

    private sealed class RecordingStore : IDeploymentLocationStore
    {
        private static readonly ManualDeploymentLocationState State = new(
            Supported: true,
            LocationId: "cameraagent-deployment",
            KnownVersion: 4,
            NextVersion: 5,
            CentralAcknowledgementRequired: false,
            StagedAcknowledgementPending: false,
            CandidateAwaitingAcknowledgement: false,
            Override: null,
            OverrideSupersededAtUtc: null,
            History: []);

        internal List<ManualDeploymentLocationRequest> Requests { get; } = [];

        internal ManualDeploymentLocationStatus Status { get; set; } = ManualDeploymentLocationStatus.Applied;

        internal bool Throw { get; set; }

        public DeploymentLocationSnapshot? Active => null;

        public ManualDeploymentLocationState Manual => Throw
            ? throw new IOException("/secret/location/path")
            : State;

        public ValueTask<ManualDeploymentLocationResult> ApplyManualAsync(
            ManualDeploymentLocationRequest request,
            CancellationToken cancellationToken)
        {
            if (Throw)
            {
                return ValueTask.FromException<ManualDeploymentLocationResult>(
                    new IOException("/secret/location/path"));
            }
            Requests.Add(request);
            return ValueTask.FromResult(new ManualDeploymentLocationResult(Status, null, null, State));
        }

        public ValueTask<DeploymentLocationSnapshot> InitializeAsync(
            DeploymentLocationSeed seed,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public DeploymentLocationSnapshot Resolve(
            CaptureLocationProvenance provenance,
            DateTimeOffset? effectiveUtc = null)
            => throw new NotSupportedException();
    }
}
