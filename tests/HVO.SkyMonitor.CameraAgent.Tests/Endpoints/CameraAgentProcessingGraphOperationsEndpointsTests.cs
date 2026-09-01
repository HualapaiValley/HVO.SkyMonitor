using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
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
    private static readonly string[] ContentMethods = ["GET", "HEAD"];

    [TestMethod]
    public async Task RoutesRequireOperationsPolicyAndMutationsRequireAntiforgery()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(Mock.Of<IProcessingGraphOperations>());
        builder.Services.AddSingleton(Mock.Of<ICameraAgentArtifactService>());
        var app = builder.Build();
        try
        {
            app.MapCameraAgentProcessingGraphOperationsEndpoints();

            var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(static endpoint => endpoint.RoutePattern.RawText?.StartsWith(
                "/api/v1/operations/processing-graphs", StringComparison.Ordinal) == true)
            .ToArray();
            Assert.HasCount(10, routes);
            foreach (var route in routes)
            {
                var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                Assert.IsNotNull(methods);
                var readOnly = methods.All(static method =>
                    string.Equals(method, "GET", StringComparison.Ordinal) ||
                    string.Equals(method, "HEAD", StringComparison.Ordinal));
                var authorization = route.Metadata.GetMetadata<IAuthorizeData>();
                Assert.IsNotNull(authorization);
                Assert.AreEqual(
                    readOnly
                        ? CameraAgentAuthorizationPolicyNames.OperationsReadV1
                        : CameraAgentAuthorizationPolicyNames.OperationsMutateV1,
                    authorization.Policy);
                Assert.AreEqual(
                    !readOnly,
                    route.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation == true);
            }
            var outputContent = routes.Single(static route => string.Equals(
                route.RoutePattern.RawText,
                "/api/v1/operations/processing-graphs/executions/{executionId:guid}/outputs/{artifactId:guid}/content",
                StringComparison.Ordinal));
            CollectionAssert.AreEquivalent(
                ContentMethods,
                outputContent.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ToArray());
            Assert.AreEqual(
                "GetCameraAgentProcessingExecutionOutputContent",
                outputContent.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
