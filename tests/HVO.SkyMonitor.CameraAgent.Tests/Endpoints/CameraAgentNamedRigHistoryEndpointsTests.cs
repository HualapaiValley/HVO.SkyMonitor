using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Endpoints;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace HVO.SkyMonitor.CameraAgent.Tests.Endpoints;

[TestClass]
[TestCategory("Unit")]
public sealed class CameraAgentNamedRigHistoryEndpointsTests
{
    [TestMethod]
    public void HistoryRequiresOwnerReadPolicy()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<SqliteNamedRigProfileStore>(_ => null!);
        builder.Services.AddSingleton<CaptureScheduleRuntimeCoordinator>(_ => null!);
        using var app = builder.Build();
        app.MapCameraAgentNamedRigEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/operations/rig-profiles/profiles/{profileId}/revisions");
        CollectionAssert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy).ToArray(), CameraAgentAuthorizationPolicyNames.OperationsReadV1);
    }

    [TestMethod]
    public void ZwoStarterRequiresMutatePolicyAndAntiforgery()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<SqliteNamedRigProfileStore>(_ => null!);
        builder.Services.AddSingleton<CaptureScheduleRuntimeCoordinator>(_ => null!);
        using var app = builder.Build();
        app.MapCameraAgentNamedRigEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(candidate =>
                candidate.RoutePattern.RawText == "/api/v1/operations/rig-profiles/equipment/zwo-starter");
        CollectionAssert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy).ToArray(), CameraAgentAuthorizationPolicyNames.OperationsMutateV1);
        Assert.IsTrue(endpoint.Metadata.GetMetadata<IAntiforgeryMetadata>()?.RequiresValidation);
    }
}
