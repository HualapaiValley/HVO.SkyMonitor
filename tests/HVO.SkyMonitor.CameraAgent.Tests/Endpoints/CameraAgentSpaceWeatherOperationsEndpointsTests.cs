using System.IO.Pipelines;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Tests.SpaceWeather;
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

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSpaceWeatherOperationsEndpointsTests
{
    private const string Refresh = "RefreshCameraAgentSpaceWeather";
    private const string Resume = "ResumeCameraAgentSpaceWeather";
    private const string Acknowledge = "AcknowledgeCameraAgentSpaceWeather";

    [TestMethod]
    public async Task EveryRouteHasOwnerPolicyAndMutationsHaveAntiforgeryMetadata()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false); using var app = App(host);
        foreach (var endpoint in Routes(app))
        {
            var mutation = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Contains("POST");
            var policy = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Single().Policy;
            Assert.AreEqual(mutation ? CameraAgentAuthorizationPolicyNames.OperationsMutateV1 : CameraAgentAuthorizationPolicyNames.OperationsReadV1, policy);
            Assert.AreEqual(mutation, endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation ?? false);
        }
        Assert.AreEqual(5, Routes(app).Count());
    }

    [TestMethod]
    [DataRow(Refresh)]
    [DataRow(Resume)]
    [DataRow(Acknowledge)]
    public async Task SessionsRequireAntiforgeryAndExactlyOneBoundedKey(string name)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false); using var app = App(host);
        var forged = await InvokeAsync(app, name, "{}", "key", antiforgery: false).ConfigureAwait(false);
        Assert.AreEqual(400, forged.Code); StringAssert.Contains(forged.Body, "space-weather.antiforgery-invalid", StringComparison.Ordinal);
        string[] duplicateKeys = ["one", "two"];
        foreach (var keys in new[] { new StringValues(), new StringValues(""), new StringValues(" "), new StringValues(new string('x', 129)), new StringValues(duplicateKeys) })
        {
            var result = await InvokeAsync(app, name, "{}", keys).ConfigureAwait(false);
            Assert.AreEqual(400, result.Code); StringAssert.Contains(result.Body, "space-weather.invalid-idempotency-key", StringComparison.Ordinal);
        }
        Assert.AreEqual(0L, host.Count("swpc_receipts")); Assert.AreEqual(0, host.Client.Calls);
    }

    [TestMethod]
    public async Task RefreshIs202AndReplayIs200WithoutExecutingAProviderRequest()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false); await host.SaveAsync().ConfigureAwait(false); using var app = App(host);
        Assert.AreEqual(202, (await InvokeAsync(app, Refresh, null, "once").ConfigureAwait(false)).Code);
        var replay = await InvokeAsync(app, Refresh, null, "once").ConfigureAwait(false);
        Assert.AreEqual(200, replay.Code); StringAssert.Contains(replay.Body, "space-weather.refresh-replayed", StringComparison.Ordinal);
        Assert.AreEqual(0, host.Client.Calls);
    }

    [TestMethod]
    public async Task InvalidResumeAndUnknownNoticeReturnFixedProblemsWithStatus()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false); using var app = App(host);
        var resume = await InvokeAsync(app, Resume, "{}", "resume").ConfigureAwait(false);
        Assert.AreEqual(400, resume.Code); StringAssert.Contains(resume.Body, "space-weather.resume-invalid-halt-time", StringComparison.Ordinal);
        var ack = await InvokeAsync(app, Acknowledge, "{\"group\":\"ALTZZZ/999\"}", "unknown").ConfigureAwait(false);
        Assert.AreEqual(404, ack.Code); StringAssert.Contains(ack.Body, "space-weather.unknown-notice", StringComparison.Ordinal);
        using var json = JsonDocument.Parse(ack.Body); Assert.IsTrue(json.RootElement.GetProperty("currentStatus").GetProperty("storeAvailable").GetBoolean());
        var paging = await InvokeAsync(app, "GetCameraAgentSpaceWeatherNotices", null, default, query: "?limit=51").ConfigureAwait(false);
        Assert.AreEqual(400, paging.Code); StringAssert.Contains(paging.Body, "space-weather.invalid-paging", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task UnexpectedStorageFaultsNeverExposePathsOrMessages()
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false); using var app = App(host);
        host.Store.ReadFailure = new IOException("private-storage-sentinel");
        var response = await InvokeAsync(app, "GetCameraAgentSpaceWeather", null, default).ConfigureAwait(false);
        Assert.AreEqual(503, response.Code); Assert.DoesNotContain("private-storage-sentinel", response.Body, StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("space-weather.revision-conflict")]
    [DataRow("space-weather.idempotency-key-reused")]
    [DataRow("space-weather.resume-halt-changed")]
    public async Task ConflictsHaveFixedHttpProblemsAndCurrentStatus(string reason)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false); using var app = App(host);
        (int Code, string Body) response;
        if (reason == "space-weather.resume-halt-changed")
        {
            var since = await host.HaltAsync().ConfigureAwait(false);
            response = await InvokeAsync(app, Resume, JsonSerializer.Serialize(new { scope = "provider", expectedHaltSinceUtc = since.AddTicks(-1) }), "stale-halt").ConfigureAwait(false);
        }
        else
        {
            await host.RunAsync().ConfigureAwait(false);
            var group = (await host.ViewAsync().ConfigureAwait(false)).Notices.Groups[0].Identity;
            var body = JsonSerializer.Serialize(new { group });
            if (reason == "space-weather.revision-conflict") host.Store.AckConflictOnce = true;
            else
            {
                Assert.AreEqual(200, (await InvokeAsync(app, Acknowledge, body, "conflict-key").ConfigureAwait(false)).Code);
                host.Clock.Advance(TimeSpan.FromSeconds(1));
            }
            response = await InvokeAsync(app, Acknowledge, body, "conflict-key").ConfigureAwait(false);
        }
        AssertProblem(response, 409, reason, available: true);
        using var json = JsonDocument.Parse(response.Body);
        var status = json.RootElement.GetProperty("currentStatus");
        Assert.AreEqual((await host.ViewAsync().ConfigureAwait(false)).Revision, status.GetProperty("revision").GetInt64());
        Assert.AreEqual(host.Clock.GetUtcNow(), status.GetProperty("observedUtc").GetDateTimeOffset());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BusyHttpProblemCarriesStatusOrTheStoreUnavailableFallback(bool readFails)
    {
        using var host = new SpaceWeatherTestHost(); await host.InitializeAsync().ConfigureAwait(false); using var app = App(host);
        await host.Gate.EnterAsync(default).ConfigureAwait(false);
        try
        {
            var pending = InvokeAsync(app, Acknowledge, "{\"group\":\"ALTZZZ/1\"}", "busy-http");
            if (readFails) host.Store.ReadFailure = new HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage.SwpcStorageException("busy-secret");
            host.Clock.Advance(TimeSpan.FromSeconds(5));
            var response = await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            AssertProblem(response, 503, readFails ? "space-weather.store-unavailable" : "space-weather.busy", available: !readFails);
            using var json = JsonDocument.Parse(response.Body);
            Assert.AreEqual(host.Clock.GetUtcNow(), json.RootElement.GetProperty("currentStatus").GetProperty("observedUtc").GetDateTimeOffset());
            Assert.DoesNotContain("busy-secret", response.Body, StringComparison.Ordinal);
            Assert.AreEqual(0L, host.Count("swpc_receipts"));
        }
        finally { host.Gate.Exit(); }
    }

    private static void AssertProblem((int Code, string Body) response, int code, string reason, bool available)
    {
        Assert.AreEqual(code, response.Code, response.Body);
        using var json = JsonDocument.Parse(response.Body);
        var problem = json.RootElement;
        Assert.AreEqual(code, problem.GetProperty("status").GetInt32());
        Assert.AreEqual(code == 409 ? "https://tools.ietf.org/html/rfc9110#section-15.5.10" : "https://tools.ietf.org/html/rfc9110#section-15.6.4", problem.GetProperty("type").GetString());
        Assert.AreEqual(code == 503 ? "Space-weather storage is unavailable." : "The space-weather request could not be completed.", problem.GetProperty("title").GetString());
        Assert.AreEqual(reason, problem.GetProperty("reasonCode").GetString());
        Assert.AreEqual(available, problem.GetProperty("currentStatus").GetProperty("storeAvailable").GetBoolean());
    }

    private static WebApplication App(SpaceWeatherTestHost host)
    {
        var builder = WebApplication.CreateBuilder(); builder.Services.AddSingleton(host.Operations);
        var app = builder.Build(); app.MapCameraAgentSpaceWeatherOperationsEndpoints(); return app;
    }
    private static IEnumerable<RouteEndpoint> Routes(WebApplication app) => ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).OfType<RouteEndpoint>();
    private static async Task<(int Code, string Body)> InvokeAsync(WebApplication app, string name, string? json, StringValues keys,
        bool antiforgery = true, string? query = null)
    {
        var endpoint = Routes(app).Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "owner"), new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)], IdentityConstants.ApplicationScheme))
        };
        context.Request.Method = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single();
        context.Request.QueryString = new QueryString(query); context.Request.Headers["Idempotency-Key"] = keys;
        context.Features.Set<IAntiforgeryValidationFeature>(new Forgery(antiforgery));
        if (json is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(json); var stream = new MemoryStream(bytes);
            context.Request.Body = stream; context.Request.ContentLength = bytes.Length; context.Request.ContentType = "application/json";
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new Detection()); context.Features.Set<IRequestBodyPipeFeature>(new Pipe(stream));
        }
        using var body = new MemoryStream(); context.Response.Body = body;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, Encoding.UTF8.GetString(body.ToArray()));
    }
    private sealed class Forgery(bool valid) : IAntiforgeryValidationFeature { public bool IsValid => valid; public Exception? Error => null; }
    private sealed class Detection : IHttpRequestBodyDetectionFeature { public bool CanHaveBody => true; }
    private sealed class Pipe(Stream stream) : IRequestBodyPipeFeature { public PipeReader Reader { get; } = PipeReader.Create(stream); }
}
