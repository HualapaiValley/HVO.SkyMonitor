using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentProcessingGraphOperationsEndpointsTests
{
    [TestMethod]
    public async Task RoutesRequireOperationsPolicyAndMutationsRequireAntiforgery()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Mock.Of<IProcessingGraphOperations>());
        var app = builder.Build();
        try
        {
            app.MapCameraAgentProcessingGraphOperationsEndpoints();

            var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/v1/operations/processing-graphs", StringComparison.Ordinal) == true)
            .ToArray();
            Assert.HasCount(9, routes);
            foreach (var route in routes)
            {
                var method = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Single();
                var authorization = route.Metadata.GetMetadata<IAuthorizeData>();
                Assert.IsNotNull(authorization);
                Assert.AreEqual(
                    string.Equals(method, "GET", StringComparison.Ordinal)
                        ? CameraAgentAuthorizationPolicyNames.OperationsReadV1
                        : CameraAgentAuthorizationPolicyNames.OperationsMutateV1,
                    authorization.Policy);
                Assert.AreEqual(
                    !string.Equals(method, "GET", StringComparison.Ordinal),
                    route.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation == true);
            }
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
