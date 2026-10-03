using System.Text;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentStillProductEndpointsTests
{
    [TestMethod]
    public void EveryStillProductRead_RequiresTheExistingOperationsReadPolicy()
    {
        using var app = App(new());
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints).ToArray();
        Assert.HasCount(4, endpoints);
        Assert.IsTrue(endpoints.All(static endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(static item => item.Policy == CameraAgentAuthorizationPolicyNames.OperationsReadV1)));
    }

    [TestMethod]
    [DataRow("GetCameraAgentStillProduct", false)]
    [DataRow("GetCameraAgentStillProductPreview", false)]
    [DataRow("GetCameraAgentStillProductProvenance", false)]
    [DataRow("GetCameraAgentStillProduct", true)]
    [DataRow("GetCameraAgentStillProductPreview", true)]
    [DataRow("GetCameraAgentStillProductProvenance", true)]
    public async Task UnknownProduct_IsNotFoundAndStorageFailureDoesNotExposePrivateDetails(string name, bool denied)
    {
        var catalog = new ProbeCatalog();
        using var app = App(catalog);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .Single(endpoint => endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name);
        var missing = Context(app);
        await endpoint.RequestDelegate!(missing).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status404NotFound, missing.Response.StatusCode);
        catalog.Failure = denied ? new UnauthorizedAccessException("PRIVATE root or credentials") : new IOException("PRIVATE root or credentials");
        var failed = Context(app);
        await endpoint.RequestDelegate!(failed).ConfigureAwait(false);
        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, failed.Response.StatusCode);
        var body = Encoding.UTF8.GetString(((MemoryStream)failed.Response.Body).ToArray());
        Assert.IsFalse(body.Contains("PRIVATE", StringComparison.Ordinal));
    }

    private static DefaultHttpContext Context(WebApplication app)
    {
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.RouteValues["productId"] = Guid.NewGuid().ToString("D");
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static WebApplication App(ProbeCatalog catalog)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton<INightlyProductCatalog>(catalog);
        var app = builder.Build();
        app.MapCameraAgentStillProductEndpoints();
        return app;
    }

    private sealed class ProbeCatalog : INightlyProductCatalog
    {
        internal Exception? Failure { get; set; }
        public ValueTask<IReadOnlyList<NightlyProductSummary>> ListAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<NightlyProductSummary>>([]);
        public ValueTask<IReadOnlyList<NightlyProductWindowStatus>> ListWindowsAsync(DateOnly observingDate, CancellationToken cancellationToken)
            => ValueTask.FromResult<IReadOnlyList<NightlyProductWindowStatus>>([]);
        public ValueTask<NightlyProductDetail?> GetAsync(Guid productId, CancellationToken cancellationToken)
            => Read<NightlyProductDetail>();
        public ValueTask<NightlyProductRendition?> OpenRenditionAsync(Guid productId, CancellationToken cancellationToken)
            => Read<NightlyProductRendition>();
        public ValueTask<NightlyProductProvenance?> OpenProvenanceAsync(Guid productId, CancellationToken cancellationToken)
            => Read<NightlyProductProvenance>();
        private ValueTask<T?> Read<T>() where T : class => Failure is { } failure ? throw failure : ValueTask.FromResult<T?>(null);
    }
}
