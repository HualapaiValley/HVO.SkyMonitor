using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// The audited local mutation contract for manual observer coordinates. The command appends a new
/// deployment-location version through the protected local owner; it never geocodes, reads browser
/// location, or contacts any network service.
/// </summary>
internal static class CameraAgentDeploymentLocationOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentDeploymentLocationOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/deployment-location")
            .WithTags("CameraAgent Deployment Location");

        group.MapGet("/manual", GetManual)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentManualDeploymentLocation")
            .Produces<ManualDeploymentLocationState>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/manual", ApplyManualAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ApplyCameraAgentManualDeploymentLocation")
            .Produces<ManualDeploymentLocationResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static IResult GetManual(IDeploymentLocationStore store, ILoggerFactory loggerFactory)
    {
        try
        {
            return Results.Ok(store.Manual);
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger(typeof(CameraAgentDeploymentLocationOperationsEndpoints))
                .LogWarning(exception, "CameraAgent manual deployment-location read failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Manual deployment-location state is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> ApplyManualAsync(
        HttpContext context,
        [FromBody] ManualDeploymentLocationRequestBody body,
        IDeploymentLocationStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (body.ExpectedVersion is not { } expectedVersion ||
            body.ExpectedManualSequence is not { } expectedManualSequence)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The expected deployment-location version and manual sequence are required.");
        }
        // Every coordinate is required explicitly: zero is a valid coordinate, so an omitted field
        // must not silently record the null island.
        if (body.LatitudeDegrees is not { } latitudeDegrees ||
            body.LongitudeDegrees is not { } longitudeDegrees ||
            body.ElevationMeters is not { } elevationMeters)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Latitude, longitude, and elevation are required.");
        }
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The manual deployment-location command is not authorized.");
        }
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var result = await store.ApplyManualAsync(
                new ManualDeploymentLocationRequest(
                    latitudeDegrees,
                    longitudeDegrees,
                    elevationMeters,
                    body.TimeZoneId ?? string.Empty,
                    expectedVersion,
                    expectedManualSequence,
                    idempotencyKey,
                    actor,
                    body.Reason),
                cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                ManualDeploymentLocationStatus.Invalid => Rejected(
                    StatusCodes.Status400BadRequest,
                    "The manual deployment-location command is invalid.",
                    result),
                ManualDeploymentLocationStatus.Conflict => Rejected(
                    StatusCodes.Status409Conflict,
                    "The manual deployment-location command conflicts with durable state.",
                    result),
                _ => Results.Ok(result)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger(typeof(CameraAgentDeploymentLocationOperationsEndpoints))
                .LogWarning(exception, "CameraAgent manual deployment-location command failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The manual deployment-location command could not be completed.");
        }
    }

    /// <summary>Returns the rejection with its bounded reason code; neither carries coordinates or a path.</summary>
    private static IResult Rejected(int statusCode, string title, ManualDeploymentLocationResult result)
        => Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reasonCode"] = result.ReasonCode,
                ["fieldPath"] = result.FieldPath
            });

    private sealed record ManualDeploymentLocationRequestBody(
        double? LatitudeDegrees = null,
        double? LongitudeDegrees = null,
        double? ElevationMeters = null,
        string? TimeZoneId = null,
        long? ExpectedVersion = null,
        long? ExpectedManualSequence = null,
        string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
