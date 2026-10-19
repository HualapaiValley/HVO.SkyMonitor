using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

/// <summary>
/// <c>GET /api/v1/operations/info</c> and <c>POST /api/v1/operations/restart</c>, which hvo-sky (#1317) uses for
/// <c>info</c>, <c>login</c>, <c>restart</c> and <c>rig apply</c>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSystemOperationsEndpointsTests
{
    private const string Info = "GetCameraAgentOperationsInfo";
    private const string Restart = "RestartCameraAgent";

    [TestMethod]
    public void Endpoints_ReadInfoWithTheReadPolicyAndRestartWithTheMutatePolicyAndAntiforgery()
    {
        using var lifetime = new CountingLifetime();
        using var app = CreateApp(lifetime, supervised: true);

        var info = FindEndpoint(app, Info);
        var restart = FindEndpoint(app, Restart);

        Assert.AreEqual(CameraAgentSystemOperationsEndpoints.InfoRoute, info.RoutePattern.RawText);
        CollectionAssert.AreEqual(new[] { CameraAgentAuthorizationPolicyNames.OperationsReadV1 },
            info.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray());
        Assert.IsNull(info.Metadata.GetMetadata<IAntiforgeryMetadata>());
        Assert.AreEqual(CameraAgentSystemOperationsEndpoints.RestartRoute, restart.RoutePattern.RawText);
        CollectionAssert.AreEqual(new[] { CameraAgentAuthorizationPolicyNames.OperationsMutateV1 },
            restart.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(static data => data.Policy).ToArray());
        Assert.IsTrue(restart.Metadata.GetMetadata<IAntiforgeryMetadata>()!.RequiresValidation);
        Assert.IsFalse(info.Metadata.OfType<IAllowAnonymous>().Any() || restart.Metadata.OfType<IAllowAnonymous>().Any());
        StringAssert.StartsWith(info.RoutePattern.RawText, "/api/v1/operations/", StringComparison.Ordinal);
        StringAssert.StartsWith(restart.RoutePattern.RawText, "/api/v1/operations/", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Info_NamesTheBuildTheProcessTheCatalogRestartSupportAndTheKeyAsync()
    {
        using var lifetime = new CountingLifetime();
        using var app = CreateApp(lifetime, supervised: false);

        var response = await InvokeAsync(app, Info, Credential.ReadKey).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status200OK, response.Status, response.Body);
        using var json = JsonDocument.Parse(response.Body);
        var root = json.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        var application = root.GetProperty("application");
        Assert.AreEqual("CameraAgent", application.GetProperty("name").GetString());
        var informational = application.GetProperty("informationalVersion").GetString()!;
        Assert.AreEqual(informational.Split('+')[0], application.GetProperty("version").GetString());
        Assert.IsGreaterThan(0, root.GetProperty("host").GetProperty("processStartedUtc").GetDateTimeOffset().Year);
        Assert.AreEqual("hyg-v42-production", root.GetProperty("catalog").GetProperty("catalogId").GetString());
        Assert.AreEqual("Fixture", root.GetProperty("catalog").GetProperty("packageKind").GetString());
        Assert.IsFalse(root.GetProperty("restart").GetProperty("supervised").GetBoolean());
        Assert.IsFalse(root.GetProperty("restart").GetProperty("requested").GetBoolean());
        var caller = root.GetProperty("caller");
        Assert.AreEqual("ApiKey", caller.GetProperty("credential").GetString());
        Assert.AreEqual("0123456789abcdef", caller.GetProperty("apiKeyId").GetString());
        Assert.AreEqual("Read", caller.GetProperty("access").GetString());
    }

    [TestMethod]
    public async Task Info_ForTheOwnersSession_NamesNoKeyAsync()
    {
        using var lifetime = new CountingLifetime();
        using var app = CreateApp(lifetime, supervised: true);

        var response = await InvokeAsync(app, Info, Credential.Session).ConfigureAwait(false);

        using var json = JsonDocument.Parse(response.Body);
        var caller = json.RootElement.GetProperty("caller");
        Assert.AreEqual("Session", caller.GetProperty("credential").GetString());
        Assert.AreEqual(JsonValueKind.Null, caller.GetProperty("apiKeyId").ValueKind);
        Assert.IsTrue(json.RootElement.GetProperty("restart").GetProperty("supervised").GetBoolean());
    }

    [TestMethod]
    public async Task Restart_ByAReadWriteKey_IsAcceptedOnceAndStopsTheHostAfterAnsweringAsync()
    {
        using var lifetime = new CountingLifetime();
        using var app = CreateApp(lifetime, supervised: true);

        var first = await InvokeAsync(app, Restart, Credential.ReadWriteKey).ConfigureAwait(false);
        var second = await InvokeAsync(app, Restart, Credential.ReadWriteKey).ConfigureAwait(false);
        var info = await InvokeAsync(app, Info, Credential.ReadWriteKey).ConfigureAwait(false);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status202Accepted, first.Status, first.Body);
        using (var json = JsonDocument.Parse(first.Body))
        {
            Assert.AreEqual("Scheduled", json.RootElement.GetProperty("status").GetString());
            Assert.AreEqual(1, json.RootElement.GetProperty("delaySeconds").GetDouble());
        }

        Assert.AreEqual(StatusCodes.Status202Accepted, second.Status);
        StringAssert.Contains(second.Body, "AlreadyRequested", StringComparison.Ordinal);
        using (var json = JsonDocument.Parse(info.Body))
        {
            Assert.IsTrue(json.RootElement.GetProperty("restart").GetProperty("requested").GetBoolean());
        }

        Assert.AreEqual(1, lifetime.Stops);
    }

    [TestMethod]
    public async Task Restart_ShareTheCameraRigPagesLatchAsync()
    {
        using var lifetime = new CountingLifetime();
        var configuration = Configuration(supervised: true);
        using var app = CreateApp(lifetime, supervised: true);

        var page = CameraAgentRestart.Request(lifetime, configuration, "owner", TimeSpan.FromMilliseconds(50), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, static (_, _) => { });
        var api = await InvokeAsync(app, Restart, Credential.Session, antiforgeryValid: true).ConfigureAwait(false);
        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        Assert.AreEqual(CameraAgentRestartDisposition.Scheduled, page);
        StringAssert.Contains(api.Body, "AlreadyRequested", StringComparison.Ordinal);
        Assert.AreEqual(1, lifetime.Stops);
    }

    [TestMethod]
    public async Task Restart_WhenUnsupervised_IsAConflictAndTheHostKeepsRunningAsync()
    {
        using var lifetime = new CountingLifetime();
        using var app = CreateApp(lifetime, supervised: false);

        var response = await InvokeAsync(app, Restart, Credential.ReadWriteKey).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status409Conflict, response.Status, response.Body);
        using var json = JsonDocument.Parse(response.Body);
        Assert.AreEqual(CameraAgentSystemOperationsEndpoints.UnsupervisedCode, json.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(0, lifetime.Stops);
        Assert.IsFalse(CameraAgentRestart.IsRequested(lifetime));
    }

    [TestMethod]
    public async Task Restart_FromASessionWithoutAnAntiforgeryToken_IsRefusedAsync()
    {
        using var lifetime = new CountingLifetime();
        using var app = CreateApp(lifetime, supervised: true);

        var response = await InvokeAsync(app, Restart, Credential.Session, antiforgeryValid: false).ConfigureAwait(false);
        var anonymous = await InvokeAsync(app, Restart, Credential.None, antiforgeryValid: true).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status400BadRequest, response.Status);
        Assert.AreEqual(StatusCodes.Status403Forbidden, anonymous.Status);
        Assert.AreEqual(0, lifetime.Stops);
        Assert.IsFalse(CameraAgentRestart.IsRequested(lifetime));
    }

    private enum Credential
    {
        None,
        Session,
        ReadKey,
        ReadWriteKey
    }

    private static IConfiguration Configuration(bool supervised) => new ConfigurationBuilder()
        .AddInMemoryCollection([new KeyValuePair<string, string?>(CameraAgentRestart.SupervisedKey, supervised ? "true" : "false")])
        .Build();

    private static readonly CatalogSnapshotResult Catalog = new("2026.09.1", CatalogSnapshotPackageKind.Fixture, 2, "hyg-v42-production",
        "4.2", "celestial-v2", "3", "/opt/hvo/catalog/manifest.json", "/opt/hvo/catalog/catalog.db", new string('a', 64), 52_428_800,
        118_218, null!);

    /// <summary>The routes and their metadata, with request services of their own (<see cref="TestApp"/>).</summary>
    private static TestApp CreateApp(IHostApplicationLifetime lifetime, bool supervised)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(Catalog);
        var app = builder.Build();
        app.MapCameraAgentSystemOperationsEndpoints();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Configuration(supervised))
            .AddSingleton(lifetime)
            .AddSingleton(TimeProvider.System)
            .AddSingleton(Catalog)
            .BuildServiceProvider();
        return new TestApp(app, services);
    }

    /// <summary>The app whose routes are invoked, and the services each request resolves, with a lifetime that counts stops.</summary>
    private sealed class TestApp(WebApplication app, ServiceProvider services) : IDisposable
    {
        public WebApplication App { get; } = app;

        public ServiceProvider Services { get; } = services;

        public void Dispose()
        {
            Services.Dispose();
            ((IDisposable)App).Dispose();
        }
    }

    private static RouteEndpoint FindEndpoint(TestApp app, string name)
        => ((IEndpointRouteBuilder)app.App).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);

    private static async Task<(int Status, string Body)> InvokeAsync(
        TestApp app,
        string name,
        Credential credential,
        bool antiforgeryValid = false)
    {
        var endpoint = FindEndpoint(app, name);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = Principal(credential)
        };
        context.Request.Method = name == Restart ? HttpMethods.Post : HttpMethods.Get;
        context.Features.Set<IAntiforgeryValidationFeature>(new AntiforgeryFeature(antiforgeryValid));
        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static ClaimsPrincipal Principal(Credential credential)
    {
        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "owner-id"),
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

    private sealed class CountingLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        private int _stops;

        public int Stops => Volatile.Read(ref _stops);

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        // Counts without cancelling ApplicationStopping, so repeat requests exercise the one-shot latch itself.
        public void StopApplication()
        {
            Interlocked.Increment(ref _stops);
            Stopped.TrySetResult();
        }

        public void Dispose() => _stopping.Dispose();
    }
}
