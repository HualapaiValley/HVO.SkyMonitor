using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentSkyMapEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentSkyMapEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/sky-map")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithTags("CameraAgent Sky Map");

        group.MapGet("/", GetSkyMapAsync)
            .WithName("GetCameraAgentSkyMap")
            .Produces<CameraAgentSkyMapProjectionResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetSkyMapAsync(
        [FromQuery(Name = "atUtc")] DateTimeOffset? atUtc,
        ICameraAgentSkyMapUiService skyMap,
        CancellationToken cancellationToken)
    {
        var result = await skyMap.GetSkyMapAsync(atUtc, cancellationToken).ConfigureAwait(false);
        return result.Kind switch
        {
            OperatorUiResultKind.Success => Results.Ok(result.Value),
            OperatorUiResultKind.Invalid => Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: result.Message ?? CameraAgentSkyMapInstantBounds.RejectionMessage),
            OperatorUiResultKind.Unauthorized => Results.Forbid(),
            _ => Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: result.Message ?? "The sky map projection is unavailable.")
        };
    }
}
