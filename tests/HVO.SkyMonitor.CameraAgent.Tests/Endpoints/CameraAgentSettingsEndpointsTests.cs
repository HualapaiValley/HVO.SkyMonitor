using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Settings;
using HVO.SkyMonitor.CameraAgent.Tests.Settings;
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
public sealed class CameraAgentSettingsEndpointsTests
{
    private const string Put = "UpdateCameraAgentSettingsGroup";

    [TestMethod]
    public async Task Endpoints_ReadWithTheReadPolicyAndWriteWithTheMutatePolicyAndAntiforgeryAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var app = CreateApp(harness);

        foreach (var name in new[] { "GetCameraAgentSettingsCatalogue", "GetCameraAgentSettings", "GetCameraAgentSettingsGroup" })
        {
            var read = FindEndpoint(app, name);
            CollectionAssert.AreEqual(new[] { CameraAgentAuthorizationPolicyNames.OperationsReadV1 },
                read.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray(), name);
            Assert.IsNull(read.Metadata.GetMetadata<IAntiforgeryMetadata>());
            StringAssert.StartsWith(read.RoutePattern.RawText, CameraAgentSettingsContract.Route, StringComparison.Ordinal);
        }
        var write = FindEndpoint(app, Put);
        CollectionAssert.AreEqual(new[] { CameraAgentAuthorizationPolicyNames.OperationsMutateV1 },
            write.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray());
        Assert.IsTrue(write.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
        Assert.AreEqual("/api/v1/operations/settings/{group}", write.RoutePattern.RawText);
        Assert.IsFalse(write.Metadata.OfType<IAllowAnonymous>().Any());
    }

    [TestMethod]
    public async Task Catalogue_And_Values_AreServedAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var app = CreateApp(harness);

        var catalogue = await InvokeAsync(app, "GetCameraAgentSettingsCatalogue", Credential.Session).ConfigureAwait(false);
        var values = await InvokeAsync(app, "GetCameraAgentSettings", Credential.ReadKey).ConfigureAwait(false);
        var unknown = await InvokeAsync(app, "GetCameraAgentSettingsGroup", Credential.Session, group: "network")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, catalogue.Status);
        Assert.AreEqual(9, JsonDocumentLength(catalogue.Body, "groups"));
        Assert.AreEqual(StatusCodes.Status200OK, values.Status);
        Assert.AreEqual(9, JsonDocumentLength(values.Body, "groups"));
        Assert.AreEqual(StatusCodes.Status404NotFound, unknown.Status);
    }

    [TestMethod]
    public async Task Put_FromASessionWithoutAnAntiforgeryToken_IsRefusedBeforeAnythingChangesAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var app = CreateApp(harness);
        var version = (await harness.GroupAsync("skyMap").ConfigureAwait(false)).Version;

        var response = await InvokeAsync(app, Put, Credential.Session, group: "skyMap",
            json: Body(version, 400), antiforgeryValid: false).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, response.Status);
        Assert.AreEqual(string.Empty, harness.ReadSettingsFile());
    }

    [TestMethod]
    [DataRow("session", true)]
    [DataRow("read-write-key", false)]
    public async Task Put_AppliesForTheSessionWithATokenAndForAKeyWithoutOneAsync(string credentialName, bool antiforgeryValid)
    {
        var credential = credentialName == "session" ? Credential.Session : Credential.ReadWriteKey;
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var app = CreateApp(harness);
        var version = (await harness.GroupAsync("skyMap").ConfigureAwait(false)).Version;

        var response = await InvokeAsync(app, Put, credential, group: "skyMap", json: Body(version, 400),
            antiforgeryValid: antiforgeryValid).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status, response.Body);
        using var json = JsonDocument.Parse(response.Body);
        Assert.AreEqual("Applied", json.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(400, json.RootElement.GetProperty("group").GetProperty("settings")[0].GetProperty("value").GetInt32());
        Assert.AreEqual(400, harness.SceneObjectBound.Current);
    }

    [TestMethod]
    public async Task Put_MapsEachRefusalToItsStatusAndCodeWithoutEchoingTheValueAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var app = CreateApp(harness);
        var version = (await harness.GroupAsync("skyMap").ConfigureAwait(false)).Version;

        var invalid = await InvokeAsync(app, Put, Credential.ReadWriteKey, group: "skyMap", json: Body(version, 98765))
            .ConfigureAwait(false);
        var conflict = await InvokeAsync(app, Put, Credential.ReadWriteKey, group: "skyMap", json: Body("stale", 400))
            .ConfigureAwait(false);
        var notFound = await InvokeAsync(app, Put, Credential.ReadWriteKey, group: "network", json: Body(version, 400))
            .ConfigureAwait(false);
        var twoKeys = await InvokeAsync(app, Put, Credential.ReadWriteKey, group: "skyMap", json: Body(version, 400),
            idempotencyKeys: ["one", "two"]).ConfigureAwait(false);
        var anonymous = await InvokeAsync(app, Put, Credential.None, group: "skyMap", json: Body(version, 400),
            antiforgeryValid: true).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, invalid.Status);
        using (var problem = JsonDocument.Parse(invalid.Body))
        {
            Assert.AreEqual("settings.invalid", problem.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(CameraAgentSettingsCatalogue.MaximumSceneObjects.NumberRule,
                problem.RootElement.GetProperty("errors").GetProperty("skyMap.maximumObjects")[0].GetString());
        }
        Assert.DoesNotContain("98765", invalid.Body, StringComparison.Ordinal);
        Assert.AreEqual(StatusCodes.Status409Conflict, conflict.Status);
        using (var problem = JsonDocument.Parse(conflict.Body))
        {
            Assert.AreEqual("settings.conflict", problem.RootElement.GetProperty("code").GetString());
            Assert.AreEqual(version, problem.RootElement.GetProperty("group").GetProperty("version").GetString());
        }
        Assert.AreEqual(StatusCodes.Status404NotFound, notFound.Status);
        Assert.AreEqual(StatusCodes.Status400BadRequest, twoKeys.Status);
        Assert.AreEqual(StatusCodes.Status403Forbidden, anonymous.Status);
        Assert.AreEqual(string.Empty, harness.ReadSettingsFile());
    }

    [TestMethod]
    public async Task Put_OfAReadOnlySetting_IsAConflictNamingTheReasonAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync(moduleType: "test").ConfigureAwait(false);
        using var app = CreateApp(harness);
        var version = (await harness.GroupAsync("camera").ConfigureAwait(false)).Version;

        var response = await InvokeAsync(app, Put, Credential.ReadWriteKey, group: "camera",
            json: $$$"""{"expectedVersion":"{{{version}}}","values":{"camera.skyLighting":"ControlledNight"}}""")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status409Conflict, response.Status);
        using var problem = JsonDocument.Parse(response.Body);
        Assert.AreEqual("settings.readOnly", problem.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("Sky lighting applies only to a virtual sky camera.",
            problem.RootElement.GetProperty("settings").GetProperty("camera.skyLighting").GetString());
    }

    [TestMethod]
    public async Task Put_WhenTheStoreReadFails_ReturnsUnavailableBeforeValidationOrReadOnlyAsync()
    {
        using var harness = await CameraAgentSettingsHarness.CreateAsync().ConfigureAwait(false);
        using var app = CreateApp(harness);
        var version = (await harness.GroupAsync("location").ConfigureAwait(false)).Version;
        harness.Location.FailRead = true;

        var response = await InvokeAsync(app, Put, Credential.ReadWriteKey, group: "location",
            json: $$$"""{"expectedVersion":"{{{version}}}","values":{"location.latitudeDegrees":31.5}}""")
            .ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, response.Status);
        using var problem = JsonDocument.Parse(response.Body);
        Assert.AreEqual("settings.unavailable", problem.RootElement.GetProperty("code").GetString());
        Assert.IsEmpty(harness.Location.Requests);
    }

    private enum Credential
    {
        None,
        Session,
        ReadKey,
        ReadWriteKey
    }

    private static string Body(string version, int limit)
        => $$$"""{"expectedVersion":"{{{version}}}","values":{"skyMap.maximumObjects":{{{limit}}}}}""";

    private static int JsonDocumentLength(string body, string property)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty(property).GetArrayLength();
    }

    private static WebApplication CreateApp(CameraAgentSettingsHarness harness)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(harness.Service);
        var app = builder.Build();
        app.MapCameraAgentSettingsEndpoints();
        return app;
    }

    private static RouteEndpoint FindEndpoint(WebApplication app, string name)
        => ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static async Task<(int Status, string Body)> InvokeAsync(
        WebApplication app,
        string name,
        Credential credential,
        string? group = null,
        string? json = null,
        bool antiforgeryValid = false,
        string[]? idempotencyKeys = null)
    {
        var endpoint = FindEndpoint(app, name);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = Principal(credential)
        };
        context.Request.Method = json is null ? HttpMethods.Get : HttpMethods.Put;
        if (group is not null)
        {
            context.Request.RouteValues["group"] = group;
        }
        if (json is not null)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var stream = new MemoryStream(payload);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = payload.Length;
            context.Request.Body = stream;
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetectionFeature());
            context.Features.Set<IRequestBodyPipeFeature>(new BodyPipeFeature(stream));
        }
        context.Features.Set<IAntiforgeryValidationFeature>(new AntiforgeryFeature(antiforgeryValid));
        if (idempotencyKeys is not null)
        {
            context.Request.Headers["Idempotency-Key"] = idempotencyKeys;
        }
        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static ClaimsPrincipal Principal(Credential credential)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, CameraAgentSettingsHarness.Actor),
            new(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ];
        return credential switch
        {
            Credential.None => new ClaimsPrincipal(new ClaimsIdentity()),
            Credential.Session => new ClaimsPrincipal(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme)),
            _ => new ClaimsPrincipal(new ClaimsIdentity(
                claims.Concat(
                [
                    new Claim(ApiKeyClaims.ApiKeyId, "0123456789abcdef"),
                    new Claim(ApiKeyClaims.AccessLevel, credential == Credential.ReadKey
                        ? nameof(ApiKeyAccessLevel.Read)
                        : nameof(ApiKeyAccessLevel.ReadWrite)),
                    new Claim(ApiKeyClaims.AuthenticationType, ApiKeyAuthenticationOptions.AuthenticationScheme)
                ]),
                ApiKeyAuthenticationOptions.AuthenticationScheme))
        };
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
