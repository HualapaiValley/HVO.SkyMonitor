using System.Diagnostics.CodeAnalysis;
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

    // The group policy is the authorization boundary for HTTP callers; the Blazor UI service
    // adds the circuit's authentication state check and is not usable outside a circuit.
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The endpoint returns a fixed sanitized 503 for every projection failure.")]
    private static async Task<IResult> GetSkyMapAsync(
        [FromQuery(Name = "atUtc")] DateTimeOffset? atUtc,
        ICameraAgentSkyMapProjection projection,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (atUtc is { } requested &&
            !CameraAgentSkyMapInstantBounds.IsWithinBounds(requested, timeProvider.GetUtcNow()))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: CameraAgentSkyMapInstantBounds.RejectionMessage);
        }
        try
        {
            return Results.Ok(await projection.ProjectAsync(atUtc, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger(typeof(CameraAgentSkyMapEndpoints)).LogWarning(exception, "CameraAgent sky map endpoint read failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The sky map projection is unavailable.");
        }
    }
}
