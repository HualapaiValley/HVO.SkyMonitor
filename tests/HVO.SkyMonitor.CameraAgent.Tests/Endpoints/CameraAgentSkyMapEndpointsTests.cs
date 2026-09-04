using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Tests.SkyMap;
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
        await using var app = CreateApp(new SequenceProjection());

        Assert.AreEqual(StatusCodes.Status200OK, await InvokeAsync(app).ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status400BadRequest, await InvokeAsync(app, "?atUtc=2026-09-06T13:00:00Z").ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status400BadRequest, await InvokeAsync(app, "?atUtc=2026-06-01T00:00:00Z").ConfigureAwait(false));
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, await InvokeAsync(app).ConfigureAwait(false));
    }

    [TestMethod]
    public void GetSkyMap_RequiresTheOperationsReadPolicy()
    {
        using var app = CreateApp(new SequenceProjection());
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(static candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetCameraAgentSkyMap");

        var policies = endpoint.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(static data => data.Policy).ToArray();

        CollectionAssert.Contains(policies, Authorization.CameraAgentAuthorizationPolicyNames.OperationsReadV1);
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

    private static async Task<int> InvokeAsync(WebApplication app, string? query = null)
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
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        return context.Response.StatusCode;
    }

    /// <summary>Succeeds once, then fails with an exception whose text must never reach the client.</summary>
    private sealed class SequenceProjection : ICameraAgentSkyMapProjection
    {
        private int _calls;

        public ValueTask<CameraAgentSkyMapProjectionResult> ProjectAsync(DateTimeOffset? atUtc, CancellationToken cancellationToken)
            => ++_calls == 1
                ? ValueTask.FromResult(SkyMapTestData.Result(atUtc ?? Now))
                : ValueTask.FromException<CameraAgentSkyMapProjectionResult>(new IOException("/secret/catalog/path"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
