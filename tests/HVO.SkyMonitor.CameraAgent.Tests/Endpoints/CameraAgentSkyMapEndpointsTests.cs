using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Tests.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentSkyMapEndpointsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task GetSkyMap_MapsSuccessBoundsAndFailuresToSanitizedStatusesAsync()
    {
        using var app = CreateApp(new SequenceProjection());

        var success = await InvokeAsync(app).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status200OK, success.Status);
        StringAssert.Contains(success.Body, "catalog", StringComparison.Ordinal);
        // Exactly at both bounds is accepted; one second beyond either is rejected before projection.
        Assert.AreEqual(StatusCodes.Status400BadRequest, (await InvokeAsync(app, "?atUtc=2026-09-05T12:00:01Z").ConfigureAwait(false)).Status);
        Assert.AreEqual(StatusCodes.Status400BadRequest, (await InvokeAsync(app, "?atUtc=2026-07-03T11:59:59Z").ConfigureAwait(false)).Status);
        var failure = await InvokeAsync(app, "?atUtc=2026-09-05T12:00:00Z").ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, failure.Status);
        StringAssert.Contains(failure.Body, "The sky map projection is unavailable.", StringComparison.Ordinal);
        Assert.IsFalse(failure.Body.Contains("/secret/catalog/path", StringComparison.Ordinal));
        Assert.IsFalse(failure.Body.Contains("IOException", StringComparison.Ordinal));
        var lowerBound = await InvokeAsync(app, "?atUtc=2026-07-04T12:00:00Z").ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, lowerBound.Status, "the lower bound instant must reach the projection");
    }

    [TestMethod]
    public void GetSkyMap_RequiresTheOperationsReadPolicy()
    {
        using var app = CreateApp(new SequenceProjection());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(static candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetCameraAgentSkyMap");

        var policies = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(static data => data.Policy).ToArray();

        CollectionAssert.Contains(policies, HVO.SkyMonitor.CameraAgent.Authorization.CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        Assert.AreEqual("/api/v1/operations/sky-map", endpoint.RoutePattern.RawText?.TrimEnd('/'));
    }

    private static WebApplication CreateApp(ICameraAgentSkyMapProjection projection)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(projection);
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now));
        var app = builder.Build();
        app.MapCameraAgentSkyMapEndpoints();
        return app;
    }

    private static async Task<(int Status, string Body)> InvokeAsync(WebApplication app, string? query = null)
    {
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(static candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetCameraAgentSkyMap");
        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "owner"),
                new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
            ], IdentityConstants.ApplicationScheme))
        };
        context.Request.Method = HttpMethods.Get;
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }
        using var body = new MemoryStream();
        context.Response.Body = body;
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>Succeeds once, then fails with an exception whose text is asserted never to reach the client.</summary>
    private sealed class SequenceProjection : ICameraAgentSkyMapProjection
    {
        private int _calls;

        public ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(DateTimeOffset? atUtc, CancellationToken cancellationToken)
        {
            if (++_calls == 1)
            {
                return ValueTask.FromResult(SkyMapTestData.Result(atUtc ?? Now));
            }
            return ValueTask.FromException<CameraAgentSkyMapProjectionResult>(new IOException("/secret/catalog/path"));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
