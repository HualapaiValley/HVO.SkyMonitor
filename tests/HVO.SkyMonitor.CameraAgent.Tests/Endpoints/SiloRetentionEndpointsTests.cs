using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
[TestProperty("Area", "Endpoints")]
public sealed class SiloRetentionEndpointsTests
{
    [TestMethod]
    [DataRow("dry-run", false)]
    [DataRow("dry-run", true)]
    [DataRow("policy", false)]
    [DataRow("policy", true)]
    [DataRow("pin", false)]
    [DataRow("pin", true)]
    public async Task StorageFailuresReturnUnavailableWithoutExceptionDetails(string route, bool sqlite)
    {
        Exception failure = sqlite
            ? new SqliteException("private SQLite error details", 11)
            : new SiloFormatRefusedException("private Silo subject", "old", "current");
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<ICameraAgentSiloRetention>(new FailingRetention(failure));
        var app = builder.Build();
        await using var lifetime = app.ConfigureAwait(false);
        app.MapGroup("/lifecycle").MapSiloRetentionEndpoints(static (_, _) => true);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate => candidate.RoutePattern.RawText == "/lifecycle/silo/retention/" + route);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = route == "dry-run" ? HttpMethods.Get : HttpMethods.Put;
        context.Request.QueryString = QueryString.Create("asOfUtc", "2026-11-28T00:00:00Z")
            .Add("underPressure", "false");
        var json = route == "policy"
            ? """{"expiryHeld":true,"roles":[]}"""
            : """{"pinId":"p1","kind":"member","artifactId":"a1","revision":"r1","active":true,"reason":"test"}""";
        if (context.Request.Method == HttpMethods.Put)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            context.Request.Body = new MemoryStream(payload);
            context.Request.ContentLength = payload.Length;
            context.Request.ContentType = "application/json";
            context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestWithBody());
        }
        using var response = new MemoryStream();
        context.Response.Body = response;

        await endpoint.RequestDelegate!(context).ConfigureAwait(false);

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.AreEqual(0L, response.Length);
    }

    private sealed class RequestWithBody : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed class FailingRetention(Exception failure) : ICameraAgentSiloRetention
    {
        public bool Ready => true;

        public ValueTask SetPolicyAsync(CameraAgentSiloRetentionPolicy policy, CancellationToken cancellationToken) =>
            ValueTask.FromException(failure);

        public ValueTask SetPinAsync(CameraAgentSiloPin pin, CancellationToken cancellationToken) =>
            ValueTask.FromException(failure);

        public ValueTask<IReadOnlyList<CameraAgentSiloRetentionCandidate>> DryRunAsync(
            DateTimeOffset asOfUtc, bool underPressure, CancellationToken cancellationToken) =>
            ValueTask.FromException<IReadOnlyList<CameraAgentSiloRetentionCandidate>>(failure);
    }
}
