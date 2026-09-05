using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
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
public sealed class CameraAgentAutomationOperationsEndpointsTests
{
    [TestMethod]
    public void AutomationEndpoints_RequireOwnerPoliciesAndAntiforgeryOnEveryMutation()
    {
        using var app = CreateApp(new RecordingStore());

        var read = FindEndpoint(app, "GetCameraAgentAutomations");
        var save = FindEndpoint(app, "SaveCameraAgentAutomation");
        var remove = FindEndpoint(app, "RemoveCameraAgentAutomation");

        CollectionAssert.Contains(
            read.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray(),
            CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        foreach (var mutation in new[] { save, remove })
        {
            CollectionAssert.Contains(
                mutation.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray(),
                CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
            Assert.IsTrue(mutation.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
        }
        Assert.IsNull(read.Metadata.GetMetadata<IAntiforgeryMetadata>());
        Assert.AreEqual("/api/v1/operations/automations/", read.RoutePattern.RawText);
        Assert.AreEqual("/api/v1/operations/automations/definitions", save.RoutePattern.RawText);
        Assert.AreEqual(
            "/api/v1/operations/automations/definitions/{definitionId}/removal", remove.RoutePattern.RawText);
    }

    [TestMethod]
    public async Task Save_PassesTheOwnerActorAndIdempotencyKeyToTheDurableStoreAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            "SaveCameraAgentAutomation",
            """{"definitionId":"sky-temperature","name":"Sky temperature","enabled":true,"taskKind":"EnvironmentalOnDemandAcquisition","taskTarget":"virtual-sky-temperature","triggerKind":"Periodic","triggerInterval":3600,"expectedVersion":2,"reason":"nightly"}""",
            idempotencyKey: "key-1").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status);
        var request = store.SaveRequests.Single();
        Assert.AreEqual("owner", request.Actor);
        Assert.AreEqual("key-1", request.IdempotencyKey);
        Assert.AreEqual(2L, request.ExpectedVersion);
        Assert.AreEqual("sky-temperature", request.DefinitionId);
        Assert.AreEqual(LocalAutomationTaskKind.EnvironmentalOnDemandAcquisition, request.TaskKind);
        Assert.AreEqual(LocalAutomationTriggerKind.Periodic, request.TriggerKind);
        Assert.AreEqual(3600, request.TriggerInterval);
        Assert.AreEqual("nightly", request.Reason);
    }

    [TestMethod]
    public async Task Save_WithoutTheRequiredCommandFields_IsRejectedBeforeTheStoreAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var missingVersion = await PostAsync(
            app,
            "SaveCameraAgentAutomation",
            """{"definitionId":"sky-temperature","name":"Sky temperature","enabled":true,"taskKind":"EnvironmentalOnDemandAcquisition","taskTarget":"t","triggerKind":"Periodic","triggerInterval":3600}""")
            .ConfigureAwait(false);
        var missingEnabled = await PostAsync(
            app,
            "SaveCameraAgentAutomation",
            """{"definitionId":"sky-temperature","name":"Sky temperature","taskKind":"EnvironmentalOnDemandAcquisition","taskTarget":"t","triggerKind":"Periodic","triggerInterval":3600,"expectedVersion":0}""")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, missingVersion.Status);
        // Enablement is required explicitly: a missing field must never silently disable an automation.
        Assert.AreEqual(StatusCodes.Status400BadRequest, missingEnabled.Status);
        Assert.IsEmpty(store.SaveRequests);
    }

    [TestMethod]
    public async Task Remove_PassesThePathIdentifierAndExpectedVersionAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            "RemoveCameraAgentAutomation",
            """{"expectedVersion":3,"reason":"retired"}""",
            idempotencyKey: "key-2",
            routeValues: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["definitionId"] = "sky-temperature"
            }).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status);
        var request = store.RemoveRequests.Single();
        Assert.AreEqual("sky-temperature", request.DefinitionId);
        Assert.AreEqual(3L, request.ExpectedVersion);
        Assert.AreEqual("key-2", request.IdempotencyKey);
        Assert.AreEqual("owner", request.Actor);
    }

    [TestMethod]
    [DataRow(LocalAutomationCommandStatus.Applied, StatusCodes.Status200OK)]
    [DataRow(LocalAutomationCommandStatus.Replayed, StatusCodes.Status200OK)]
    [DataRow(LocalAutomationCommandStatus.Unchanged, StatusCodes.Status200OK)]
    [DataRow(LocalAutomationCommandStatus.Invalid, StatusCodes.Status400BadRequest)]
    [DataRow(LocalAutomationCommandStatus.NotFound, StatusCodes.Status404NotFound)]
    [DataRow(LocalAutomationCommandStatus.Conflict, StatusCodes.Status409Conflict)]
    public async Task Save_MapsEveryDispositionOntoItsStatusCodeAsync(
        LocalAutomationCommandStatus status,
        int expected)
    {
        var store = new RecordingStore
        {
            Status = status,
            ReasonCode = LocalAutomationContract.ExpectedVersionConflictReasonCode
        };
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            "SaveCameraAgentAutomation",
            """{"definitionId":"sky-temperature","name":"Sky temperature","enabled":true,"taskKind":"EnvironmentalOnDemandAcquisition","taskTarget":"t","triggerKind":"Periodic","triggerInterval":3600,"expectedVersion":2}""")
            .ConfigureAwait(false);

        Assert.AreEqual(expected, response.Status);
        if (expected != StatusCodes.Status200OK)
        {
            StringAssert.Contains(response.Body, "reasonCode", StringComparison.Ordinal);
            StringAssert.Contains(
                response.Body,
                LocalAutomationContract.ExpectedVersionConflictReasonCode,
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public async Task Endpoints_SanitizeAStoreFailureAsync()
    {
        var store = new RecordingStore { Throw = true };
        using var app = CreateApp(store);

        var read = await GetAsync(app).ConfigureAwait(false);
        var save = await PostAsync(
            app,
            "SaveCameraAgentAutomation",
            """{"definitionId":"sky-temperature","name":"Sky temperature","enabled":true,"taskKind":"EnvironmentalOnDemandAcquisition","taskTarget":"t","triggerKind":"Periodic","triggerInterval":3600,"expectedVersion":2}""")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, read.Status);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, save.Status);
        Assert.IsFalse(read.Body.Contains("/secret/automation/path", StringComparison.Ordinal));
        Assert.IsFalse(save.Body.Contains("IOException", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Get_ReturnsTheDurableProjectionAsync()
    {
        using var app = CreateApp(new RecordingStore());

        var response = await GetAsync(app).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status);
        StringAssert.Contains(response.Body, "storeVersion", StringComparison.Ordinal);
        StringAssert.Contains(response.Body, "calendar", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Save_WithoutAnOwnerIdentity_IsForbiddenBeforeTheStoreAsync()
    {
        var store = new RecordingStore();
        using var app = CreateApp(store);

        var response = await PostAsync(
            app,
            "SaveCameraAgentAutomation",
            """{"definitionId":"sky-temperature","name":"Sky temperature","enabled":true,"taskKind":"EnvironmentalOnDemandAcquisition","taskTarget":"t","triggerKind":"Periodic","triggerInterval":3600,"expectedVersion":2}""",
            anonymous: true).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status403Forbidden, response.Status);
        Assert.IsEmpty(store.SaveRequests);
    }

    private static WebApplication CreateApp(ILocalAutomationStore store)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(store);
        var app = builder.Build();
        app.MapCameraAgentAutomationOperationsEndpoints();
        return app;
    }

    private static RouteEndpoint FindEndpoint(WebApplication app, string name)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static Task<(int Status, string Body)> GetAsync(WebApplication app)
        => InvokeAsync(app, "GetCameraAgentAutomations", HttpMethods.Get, null, null, false, null);

    private static Task<(int Status, string Body)> PostAsync(
        WebApplication app,
        string name,
        string json,
        string? idempotencyKey = null,
        bool anonymous = false,
        IReadOnlyDictionary<string, object?>? routeValues = null)
        => InvokeAsync(app, name, HttpMethods.Post, json, idempotencyKey, anonymous, routeValues);

    private static async Task<(int Status, string Body)> InvokeAsync(
        WebApplication app,
        string name,
        string method,
        string? json,
        string? idempotencyKey,
        bool anonymous,
        IReadOnlyDictionary<string, object?>? routeValues)
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
        if (routeValues is not null)
        {
            foreach (var (key, value) in routeValues)
            {
                context.Request.RouteValues[key] = value;
            }
        }
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

    private sealed class RecordingStore : ILocalAutomationStore
    {
        private static readonly LocalAutomationOperatorState State = new(
            StoreVersion: 3,
            ReadAtUtc: DateTimeOffset.UnixEpoch,
            ObservedCaptureSequence: 42,
            Registry: [],
            Definitions: [],
            Calendar: [],
            Runs: []);

        internal List<LocalAutomationSaveRequest> SaveRequests { get; } = [];

        internal List<LocalAutomationRemoveRequest> RemoveRequests { get; } = [];

        internal LocalAutomationCommandStatus Status { get; set; } = LocalAutomationCommandStatus.Applied;

        internal string? ReasonCode { get; set; }

        internal bool Throw { get; set; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<LocalAutomationOperatorState> GetStateAsync(CancellationToken cancellationToken)
            => Throw
                ? ValueTask.FromException<LocalAutomationOperatorState>(new IOException("/secret/automation/path"))
                : ValueTask.FromResult(State);

        public ValueTask<LocalAutomationCommandResult> SaveAsync(
            LocalAutomationSaveRequest request,
            CancellationToken cancellationToken)
        {
            if (Throw)
            {
                return ValueTask.FromException<LocalAutomationCommandResult>(
                    new IOException("/secret/automation/path"));
            }
            SaveRequests.Add(request);
            return ValueTask.FromResult(new LocalAutomationCommandResult(Status, ReasonCode, null, State));
        }

        public ValueTask<LocalAutomationCommandResult> RemoveAsync(
            LocalAutomationRemoveRequest request,
            CancellationToken cancellationToken)
        {
            if (Throw)
            {
                return ValueTask.FromException<LocalAutomationCommandResult>(
                    new IOException("/secret/automation/path"));
            }
            RemoveRequests.Add(request);
            return ValueTask.FromResult(new LocalAutomationCommandResult(Status, ReasonCode, null, State));
        }

        public ValueTask<IReadOnlyList<LocalAutomationRunnerEntry>> GetRunnerViewAsync(
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask<bool> TryBeginRunAsync(
            LocalAutomationRunnerEntry entry,
            string runKey,
            DateTimeOffset scheduledForUtc,
            long? observedCaptureSequence,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask CompleteRunAsync(
            string runKey,
            LocalAutomationRunOutcome outcome,
            string detail,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask RecordTerminalRunAsync(
            LocalAutomationRunnerEntry entry,
            string runKey,
            DateTimeOffset scheduledForUtc,
            LocalAutomationRunOutcome outcome,
            string detail,
            long? observedCaptureSequence,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public ValueTask SetCaptureBaselineAsync(
            string definitionId,
            long captureSequence,
            CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
