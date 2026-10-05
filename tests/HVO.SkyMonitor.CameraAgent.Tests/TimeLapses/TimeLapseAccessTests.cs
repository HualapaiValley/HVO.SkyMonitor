using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.TimeLapses;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Services;
using HVO.SkyMonitor.Common.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.TimeLapses;

[TestClass]
[TestCategory("Unit")]
public sealed class TimeLapseAccessTests
{
    [TestMethod]
    public void EveryVideoRead_RequiresExistingOwnerReadPolicy()
    {
        using var app = App(Mock.Of<ICameraAgentTimeLapseCatalog>());
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).ToArray();
        Assert.HasCount(4, endpoints);
        Assert.IsTrue(endpoints.All(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(policy => policy.Policy == CameraAgentAuthorizationPolicyNames.OperationsReadV1)));
    }

    [TestMethod]
    public async Task DeniedVideoReadAndRetry_NeverTouchCatalogOrCommands()
    {
        var catalog = new Mock<ICameraAgentTimeLapseCatalog>(MockBehavior.Strict);
        var commands = new Mock<ICameraAgentTimeLapseCommands>(MockBehavior.Strict);
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null, It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Failed());
        var service = Service(auth.Object, catalog.Object, commands.Object);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await service.GetTimeLapseAsync(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false)).Kind);
        Assert.AreEqual(OperatorUiResultKind.Unauthorized, (await service.RetryTimeLapseAsync(Guid.NewGuid(), 3, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false)).Kind);
        catalog.VerifyNoOtherCalls();
        commands.VerifyNoOtherCalls();
    }

    [TestMethod]
    public async Task Retry_RequiresMutationPolicyAndRecordsCanonicalOwnerAndExpectedRevision()
    {
        var job = Guid.NewGuid();
        var request = Guid.NewGuid();
        var commands = new Mock<ICameraAgentTimeLapseCommands>(MockBehavior.Strict);
        commands.Setup(value => value.RetryAsync(job, 7, request, "owner-id", It.IsAny<string>(), CancellationToken.None))
            .ReturnsAsync(true);
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(service => service.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null, It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());
        var service = Service(auth.Object, Mock.Of<ICameraAgentTimeLapseCatalog>(), commands.Object);
        Assert.IsTrue((await service.RetryTimeLapseAsync(job, 7, request, CancellationToken.None).ConfigureAwait(false)).IsSuccess);
        auth.Verify(value => value.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null, CameraAgentAuthorizationPolicyNames.OperationsMutateV1), Times.Once);
        commands.VerifyAll();
    }

    internal static async Task VerifyActualMediaAsync(ICameraAgentTimeLapseCatalog catalog, CameraAgentTimeLapseProduct product)
    {
        var window = await catalog.GetWindowAsync(product.ProductId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(window);
        Assert.AreEqual(product.WindowIdentitySha256, window.IdentitySha256);
        Assert.AreEqual(product.ReportingPeriodIdentitySha256, window.ReportingPeriod.IdentitySha256);
        Assert.AreEqual(product.StartUtc, window.StartUtc);
        Assert.AreEqual(product.EndUtc, window.EndUtc);
        using var app = App(catalog);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .Single(value => value.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "GetCameraAgentTimeLapseVideo");
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Method = HttpMethods.Get;
        context.Request.RouteValues["productId"] = product.ProductId.ToString("D");
        context.Request.Headers.Range = "bytes=0-31";
        context.Request.QueryString = new("?download=1");
        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status206PartialContent, context.Response.StatusCode);
        Assert.AreEqual(32L, context.Response.Body.Length);
        Assert.AreEqual("video/mp4", context.Response.ContentType);
        Assert.StartsWith("attachment;", context.Response.Headers.ContentDisposition.ToString());
        Assert.AreEqual(product.Encoding.PayloadSha256, context.Response.Headers["X-Content-SHA256"].ToString());
        using var actual = await catalog.OpenVideoAsync(product.ProductId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsNotNull(actual);
        var expected = new byte[32];
        await actual.ReadExactlyAsync(expected).ConfigureAwait(false);
        CollectionAssert.AreEqual(expected, ((MemoryStream)context.Response.Body).ToArray());
    }

    internal static async Task VerifyCorruptMediaAsync(ICameraAgentTimeLapseCatalog catalog, CameraAgentTimeLapseProduct product)
    {
        var auth = new Mock<IAuthorizationService>();
        auth.Setup(value => value.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), null, It.IsAny<string>()))
            .ReturnsAsync(AuthorizationResult.Success());
        var result = await Service(auth.Object, catalog, Mock.Of<ICameraAgentTimeLapseCommands>())
            .GetTimeLapseAsync(product.ProductId, CancellationToken.None).ConfigureAwait(false);
        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual(product.ProductId, result.Value.Product.ProductId);
        Assert.IsNotNull(result.Value.Window);
        Assert.AreEqual(product.WindowIdentitySha256, result.Value.Window.IdentitySha256);
        Assert.IsFalse(result.Value.VerifiedAvailable);
        using var app = App(catalog);
        foreach (var name in new[] { "GetCameraAgentTimeLapseVideo", "GetCameraAgentTimeLapseProvenance" })
        {
            var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
                .Single(value => value.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
            var context = new DefaultHttpContext { RequestServices = app.Services };
            context.Request.Method = HttpMethods.Get;
            context.Request.RouteValues["productId"] = product.ProductId.ToString("D");
            using var body = new MemoryStream();
            context.Response.Body = body;
            await endpoint.RequestDelegate!(context).ConfigureAwait(false);
            Assert.AreEqual(name.EndsWith("Video", StringComparison.Ordinal) ? 503 : 200, context.Response.StatusCode);
            if (context.Response.StatusCode == 503)
            {
                var message = System.Text.Encoding.UTF8.GetString(body.ToArray());
                Assert.Contains("temporarily unavailable", message);
                Assert.DoesNotContain("immutable checksum", message);
            }
        }
    }

    private static WebApplication App(ICameraAgentTimeLapseCatalog catalog)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(catalog);
        var app = builder.Build();
        app.MapCameraAgentTimeLapseEndpoints();
        return app;
    }

    private static CameraAgentNightlyProductUiService Service(IAuthorizationService authorization,
        ICameraAgentTimeLapseCatalog catalog, ICameraAgentTimeLapseCommands commands)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "owner-id"),
            new Claim(CanonicalCredentialClaims.AccountTypeClaim, CanonicalCredentialClaims.UserAccountType)
        ], IdentityConstants.ApplicationScheme));
        return new(new Authentication(principal), authorization, Mock.Of<INightlyProductCatalog>(),
            Options.Create(new CameraAgentHostOptions()), NullLogger<CameraAgentNightlyProductUiService>.Instance, catalog, commands);
    }

    private sealed class Authentication(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }
}
