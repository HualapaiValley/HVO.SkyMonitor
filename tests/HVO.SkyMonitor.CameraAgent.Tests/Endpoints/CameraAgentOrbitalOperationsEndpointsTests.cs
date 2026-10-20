using System.Globalization;
using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Tests.Orbital;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>
/// The owner-only orbital operator routes over the real operations, orbital store and automation store. Each test
/// invokes the route's own request delegate, so the policy and antiforgery metadata are asserted separately.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentOrbitalOperationsEndpointsTests : OrbitalStoreTestBase
{
    private const string Get = "GetCameraAgentOrbitalElements";
    private const string Refresh = "RefreshCameraAgentOrbitalElements";
    private const string Resume = "ResumeCameraAgentOrbitalElements";
    private const string RefreshBody = """{"reason":"operator refresh"}""";
    private const string SecretPath = "/secret/orbital/path";

    [TestMethod]
    public async Task EveryRouteRequiresItsOwnerPolicyAndEveryMutationRequiresAntiforgery()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var read = FindEndpoint(app, Get);
        Assert.AreEqual("/api/v1/operations/orbital-elements/", read.RoutePattern.RawText);
        CollectionAssert.Contains(Policies(read), CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        Assert.IsNull(read.Metadata.GetMetadata<IAntiforgeryMetadata>());
        foreach (var (name, route) in new[] { (Refresh, "/refresh"), (Resume, "/resume") })
        {
            var mutation = FindEndpoint(app, name);
            Assert.AreEqual(CameraAgentOrbitalOperationsEndpoints.RoutePrefix + route, mutation.RoutePattern.RawText);
            CollectionAssert.Contains(Policies(mutation), CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
            Assert.IsTrue(mutation.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation, name);
            CollectionAssert.AreEqual(new[] { HttpMethods.Post },
                mutation.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.ToArray());
        }
    }

    [TestMethod]
    public async Task GetReturnsTheValidatedRefreshReadModel()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, Get).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status);
        using var document = JsonDocument.Parse(response.Body);
        Assert.AreEqual(OrbitalRefreshStatus.CurrentVersion, document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(OrbitalElementsOperations.DefinitionMissingReasonCode, document.RootElement.GetProperty("reason").GetString());
        Assert.IsTrue(document.RootElement.GetProperty("enabled").GetBoolean());
    }

    [TestMethod]
    public async Task AnUnavailableStoreIsReportedThroughTheReadModelAndRefusesCommandsWith503()
    {
        var root = CreateRoot();
        var owner = OrbitalElementsStoreTestData.Open(root, new OrbitalElementsStoreTestData.Clock(OrbitalElementsStoreTestData.Start));
        await owner.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        using var lockHolder = owner;
        using var fixture = await OrbitalAutomationFixture.CreateAsync(root, initialize: false).ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var read = await InvokeAsync(app, Get).ConfigureAwait(false);
        var refresh = await InvokeAsync(app, Refresh, RefreshBody, "refresh-locked").ConfigureAwait(false);
        var resume = await InvokeAsync(app, Resume, ResumeBody(OrbitalElementsStoreTestData.Start), "resume-locked")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, read.Status);
        Assert.AreEqual("orbital.storage-locked", Json(read.Body, "reason"));
        Assert.AreEqual(nameof(OrbitalRefreshRecoveryAction.RepairStorage), RecoveryAction(read.Body));
        foreach (var command in new[] { refresh, resume })
        {
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, command.Status);
            Assert.AreEqual("orbital.storage-locked", Json(command.Body, "reasonCode"));
        }
    }

    [TestMethod]
    public async Task AStoreFailureIsSanitizedTo503OnEveryRoute()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        fixture.Orbital.Intercept.ReadFault = new IOException(SecretPath);
        using var app = CreateApp(fixture);

        var responses = new[]
        {
            await InvokeAsync(app, Get).ConfigureAwait(false),
            await InvokeAsync(app, Refresh, RefreshBody, "refresh-fault").ConfigureAwait(false),
            await InvokeAsync(app, Resume, ResumeBody(OrbitalElementsStoreTestData.Start), "resume-fault").ConfigureAwait(false)
        };

        foreach (var response in responses)
        {
            Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, response.Status);
            Assert.DoesNotContain(SecretPath, response.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(nameof(IOException), response.Body, StringComparison.Ordinal);
        }
    }

    [TestMethod]
    [DataRow(Refresh)]
    [DataRow(Resume)]
    public async Task ACommandWithoutAnOwnerIdentityIsForbiddenBeforeAnythingDurableHappens(string name)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var since = await HaltAsync(fixture).ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, name, name == Refresh ? RefreshBody : ResumeBody(since), "anonymous-key",
            anonymous: true).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status403Forbidden, response.Status);
        Assert.IsNotNull((await fixture.Orbital.StateAsync().ConfigureAwait(false)).Halt, "The halt is untouched.");
        Assert.IsEmpty((await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    [DataRow(Refresh, null)]
    [DataRow(Refresh, false)]
    [DataRow(Resume, null)]
    [DataRow(Resume, false)]
    public async Task ASessionCommandWithoutAValidAntiforgeryTokenIsRefusedBeforeAnythingDurableHappens(string name,
        bool? antiforgeryValid)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var since = await HaltAsync(fixture).ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, name, name == Refresh ? RefreshBody : ResumeBody(since), "forged-key",
            antiforgeryValid: antiforgeryValid).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, response.Status);
        Assert.AreEqual(CameraAgentOrbitalOperationsEndpoints.AntiforgeryInvalidReasonCode, Json(response.Body, "reasonCode"));
        Assert.IsNotNull((await fixture.Orbital.StateAsync().ConfigureAwait(false)).Halt, "The halt is untouched.");
        Assert.IsEmpty((await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    [DataRow(Refresh, 0, OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode)]
    [DataRow(Refresh, 1, OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode)]
    [DataRow(Refresh, 2, OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode)]
    [DataRow(Refresh, 3, OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode)]
    [DataRow(Resume, 0, CameraAgentOrbitalOperationsEndpoints.ResumeMissingIdempotencyKeyReasonCode)]
    [DataRow(Resume, 1, CameraAgentOrbitalOperationsEndpoints.ResumeMissingIdempotencyKeyReasonCode)]
    [DataRow(Resume, 3, CameraAgentOrbitalOperationsEndpoints.ResumeMissingIdempotencyKeyReasonCode)]
    public async Task ACommandRequiresExactlyOneNonBlankIdempotencyKey(string name, int variant, string reasonCode)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var since = await HaltAsync(fixture).ConfigureAwait(false);
        using var app = CreateApp(fixture);
        StringValues? keys = variant switch
        {
            0 => (StringValues?)null,
            1 => new StringValues(string.Empty),
            2 => new StringValues("   "),
            _ => new StringValues(["first-key", "second-key"])
        };

        var response = await InvokeAsync(app, name, name == Refresh ? RefreshBody : ResumeBody(since), keys)
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, response.Status);
        Assert.AreEqual(reasonCode, Json(response.Body, "reasonCode"));
        Assert.IsNotNull((await fixture.Orbital.StateAsync().ConfigureAwait(false)).Halt);
        Assert.IsEmpty((await fixture.AutomationStateAsync().ConfigureAwait(false)).Runs);
    }

    [TestMethod]
    public async Task ARefreshIsQueuedAs202AndTheSameKeyReplaysAs200()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var accepted = await InvokeAsync(app, Refresh, RefreshBody, "refresh-once").ConfigureAwait(false);
        var replayed = await InvokeAsync(app, Refresh, RefreshBody, "refresh-once").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status202Accepted, accepted.Status);
        Assert.AreEqual(CameraAgentOrbitalOperationsEndpoints.RoutePrefix, accepted.Location);
        Assert.AreEqual(OrbitalElementsOperations.RefreshQueuedReasonCode, Json(accepted.Body, "reasonCode"));
        Assert.AreEqual(StatusCodes.Status200OK, replayed.Status);
        Assert.AreEqual(OrbitalElementsOperations.RefreshReplayedReasonCode, Json(replayed.Body, "reasonCode"));
        Assert.AreEqual(0, fixture.Orbital.Client.Calls, "The route queues; only the runner executes.");
    }

    [TestMethod]
    public async Task ARefreshInsideTheFloorIsA409CarryingTheNextPermittedAttempt()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        var admission = await fixture.Orbital.Store.TryBeginAttemptAsync("floor-probe", OrbitalAttemptTrigger.Scheduled,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admission.Status);
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, Refresh, RefreshBody, "refresh-early").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status409Conflict, response.Status);
        Assert.AreEqual(OrbitalElementsOperations.NotYetPermittedReasonCode, Json(response.Body, "reasonCode"));
        var next = DateTimeOffset.Parse(Json(response.Body, "nextPermittedAttemptUtc")!, CultureInfo.InvariantCulture);
        Assert.AreEqual(OrbitalElementsStoreTestData.Start + SqliteOrbitalElementsStore.MinimumProviderAttemptInterval, next);
    }

    [TestMethod]
    [DataRow(false, true, false, StatusCodes.Status409Conflict, OrbitalElementsOperations.DisabledReasonCode)]
    [DataRow(true, false, false, StatusCodes.Status409Conflict, OrbitalElementsOperations.DefinitionMissingReasonCode)]
    [DataRow(true, true, true, StatusCodes.Status409Conflict, OrbitalElementsOperations.ProviderHaltedReasonCode)]
    public async Task ARefreshTheConfigurationOrProviderPolicyForbidsIsA409(bool enabled, bool defined, bool halted,
        int expected, string reasonCode)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot(), enabled).ConfigureAwait(false);
        if (defined)
        {
            await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        }
        if (halted)
        {
            await HaltAsync(fixture).ConfigureAwait(false);
        }
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, Refresh, RefreshBody, "refresh-refused").ConfigureAwait(false);

        Assert.AreEqual(expected, response.Status);
        Assert.AreEqual(reasonCode, Json(response.Body, "reasonCode"));
        Assert.IsFalse(response.Body.Contains("nextPermittedAttemptUtc", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("{}", OrbitalElementsOperations.InvalidReasonReasonCode)]
    [DataRow("""{"reason":"   "}""", OrbitalElementsOperations.InvalidReasonReasonCode)]
    public async Task ARefreshWithoutAReasonIsA400(string body, string reasonCode)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, Refresh, body, "refresh-no-reason").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, response.Status);
        Assert.AreEqual(reasonCode, Json(response.Body, "reasonCode"));
    }

    [TestMethod]
    [DataRow(Refresh, """{"reason":"refresh","actor":"untrusted"}""")]
    [DataRow(Refresh, """{"reason":"refresh","requestUri":"https://example.invalid/gp.php"}""")]
    [DataRow(Resume, """{"expectedHaltSinceUtc":"2026-10-19T12:00:00Z","reason":"resume","idempotencyKey":"body-key"}""")]
    public async Task ACommandBodyWithAnUnknownMemberIsRejectedBeforeTheCommandRuns(string name, string body)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        await fixture.SaveDefinitionAsync().ConfigureAwait(false);
        await HaltAsync(fixture).ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, name, body, "unknown-member").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, response.Status);
        Assert.IsNotNull((await fixture.Orbital.StateAsync().ConfigureAwait(false)).Halt);
    }

    [TestMethod]
    public async Task ResumeClearsTheNamedHaltAndTheSameKeyReplays()
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        var since = await HaltAsync(fixture).ConfigureAwait(false);
        using var app = CreateApp(fixture);

        var applied = await InvokeAsync(app, Resume, ResumeBody(since), "resume-once").ConfigureAwait(false);
        var replayed = await InvokeAsync(app, Resume, ResumeBody(since), "resume-once").ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, applied.Status);
        Assert.AreEqual("orbital.resumed", Json(applied.Body, "reasonCode"));
        Assert.AreEqual(StatusCodes.Status200OK, replayed.Status);
        Assert.AreEqual("orbital.resume-replayed", Json(replayed.Body, "reasonCode"));
        var state = await fixture.Orbital.StateAsync().ConfigureAwait(false);
        Assert.IsNull(state.Halt);
        Assert.AreEqual(since + SqliteOrbitalElementsStore.MinimumProviderAttemptInterval, state.NextPermittedAttemptUtc,
            "Resume never moves the floor.");
    }

    [TestMethod]
    [DataRow("{\"reason\":\"resume\"}", true, StatusCodes.Status400BadRequest, CameraAgentOrbitalOperationsEndpoints.ResumeMissingHaltReasonCode)]
    [DataRow("{\"expectedHaltSinceUtc\":\"2026-10-19T11:00:00Z\",\"reason\":\"resume\"}", true, StatusCodes.Status409Conflict, "orbital.halt-mismatch")]
    [DataRow("{\"expectedHaltSinceUtc\":\"2026-10-19T12:00:00Z\",\"reason\":\"resume\"}", false, StatusCodes.Status409Conflict, "orbital.no-halt")]
    [DataRow("{\"expectedHaltSinceUtc\":\"2026-10-19T12:00:00Z\"}", true, StatusCodes.Status400BadRequest, "orbital.resume-invalid-reason")]
    public async Task AResumeThatDoesNotMatchTheDurableHaltIsRefused(string body, bool halted, int expected, string reasonCode)
    {
        using var fixture = await OrbitalAutomationFixture.CreateAsync(CreateRoot()).ConfigureAwait(false);
        if (halted)
        {
            Assert.AreEqual(OrbitalElementsStoreTestData.Start, await HaltAsync(fixture).ConfigureAwait(false));
        }
        using var app = CreateApp(fixture);

        var response = await InvokeAsync(app, Resume, body, "resume-refused").ConfigureAwait(false);

        Assert.AreEqual(expected, response.Status);
        Assert.AreEqual(reasonCode, Json(response.Body, "reasonCode"));
        Assert.AreEqual(halted, (await fixture.Orbital.StateAsync().ConfigureAwait(false)).Halt is not null);
    }

    private static WebApplication CreateApp(OrbitalAutomationFixture fixture)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(fixture.Operations);
        var app = builder.Build();
        app.MapCameraAgentOrbitalOperationsEndpoints();
        return app;
    }

    /// <summary>Records a provider halt at the fixture's current instant, which it returns.</summary>
    private static async Task<DateTimeOffset> HaltAsync(OrbitalAutomationFixture fixture)
    {
        var admission = await fixture.Orbital.Store.TryBeginAttemptAsync("halt-probe", OrbitalAttemptTrigger.Scheduled,
            CancellationToken.None).ConfigureAwait(false);
        Assert.AreEqual(OrbitalAdmissionStatus.Admitted, admission.Status);
        await fixture.Orbital.Store.RecordHaltAsync(admission.AttemptId!, 429, "orbital.provider-http-429",
            CancellationToken.None).ConfigureAwait(false);
        var halt = (await fixture.Orbital.StateAsync().ConfigureAwait(false)).Halt;
        Assert.IsNotNull(halt);
        return halt.SinceUtc;
    }

    private static string ResumeBody(DateTimeOffset since) => string.Create(CultureInfo.InvariantCulture,
        $$"""{"expectedHaltSinceUtc":"{{since.UtcDateTime:O}}","reason":"provider recovered"}""");

    private static string[] Policies(RouteEndpoint endpoint)
        => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy!).ToArray();

    private static RouteEndpoint FindEndpoint(WebApplication app, string name)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static string? Json(string body, string property)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty(property, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;
    }

    private static string RecoveryAction(string body)
    {
        using var document = JsonDocument.Parse(body);
        var value = document.RootElement.GetProperty("recoveryAction");
        return value.ValueKind == JsonValueKind.Number
            ? ((OrbitalRefreshRecoveryAction)value.GetInt32()).ToString()
            : value.GetString()!;
    }

    private static Task<(int Status, string Body, string? Location)> InvokeAsync(WebApplication app, string name,
        string? json = null, string? idempotencyKey = null, bool anonymous = false, bool? antiforgeryValid = true)
        => InvokeAsync(app, name, json, idempotencyKey is null ? (StringValues?)null : new StringValues(idempotencyKey),
            anonymous, antiforgeryValid);

    /// <summary>
    /// Invokes the route's request delegate. <paramref name="antiforgeryValid"/> stands in for <c>UseAntiforgery</c>:
    /// true or false sets its validation feature, and null leaves it absent, as for a request that never reached it.
    /// </summary>
    private static async Task<(int Status, string Body, string? Location)> InvokeAsync(WebApplication app, string name,
        string? json, StringValues? idempotencyKeys, bool anonymous = false, bool? antiforgeryValid = true)
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
                        new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
                    ],
                    IdentityConstants.ApplicationScheme))
        };
        context.Request.Method = name == Get ? HttpMethods.Get : HttpMethods.Post;
        if (name != Get && antiforgeryValid is { } valid)
        {
            context.Features.Set<IAntiforgeryValidationFeature>(new AntiforgeryFeature(valid));
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
        if (idempotencyKeys is { } keys)
        {
            context.Request.Headers[CameraAgentOrbitalOperationsEndpoints.IdempotencyKeyHeader] = keys;
        }
        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        var location = context.Response.Headers.Location;
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()),
            location.Count == 0 ? null : location.ToString());
    }

    private sealed class AntiforgeryFeature(bool valid) : IAntiforgeryValidationFeature
    {
        public bool IsValid => valid;

        public Exception? Error => valid ? null : new AntiforgeryValidationException("No token.");
    }

    private sealed class BodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class BodyPipeFeature(Stream stream) : IRequestBodyPipeFeature
    {
        public PipeReader Reader { get; } = PipeReader.Create(stream);
    }
}
