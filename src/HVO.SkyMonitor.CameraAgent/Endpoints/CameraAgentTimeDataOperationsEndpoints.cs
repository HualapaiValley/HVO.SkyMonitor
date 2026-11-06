using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// The read-only time-data status: the active source, coverage and expiry, the last refresh attempt and the last
/// adoption. It accepts nothing and starts nothing; the refresh runs only as the reserved automation.
/// </summary>
internal static class CameraAgentTimeDataOperationsEndpoints
{
    internal const string Route = "/api/v1/operations/time-data";

    internal static IEndpointRouteBuilder MapCameraAgentTimeDataOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(Route, GetAsync)
            .WithTags("CameraAgent Time Data")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentTimeData")
            .Produces<CameraAgentTimeDataStatus>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetAsync(
        CameraAgentTimeDataOperations operations,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await operations.GetStatusAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger(typeof(CameraAgentTimeDataOperationsEndpoints))
                .LogWarning(exception, "CameraAgent time-data status read failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Time-data status is unavailable.");
        }
    }
}
