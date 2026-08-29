using System.Security.Claims;
using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var operations = endpoints.MapGroup("/api/v1/operations")
            .WithTags("CameraAgent Operations");

        operations.MapGet("/summary", GetSummaryAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentOperationsSummary")
            .Produces<CameraAgentOperationsSummary>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        operations.MapPost("/capture/pause", PauseAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("PauseCameraAgentCapture")
            .Produces<CaptureControlCommandResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        operations.MapPost("/capture/resume", ResumeAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ResumeCameraAgentCapture")
            .Produces<CaptureControlCommandResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetSummaryAsync(
        CameraAgentOperationsSummaryProvider provider,
        CancellationToken cancellationToken)
        => Results.Ok(await provider.GetAsync(cancellationToken).ConfigureAwait(false));

    private static Task<IResult> PauseAsync(
        HttpContext context,
        [FromBody] CaptureControlEndpointRequest request,
        CaptureAdmissionCoordinator coordinator,
        CancellationToken cancellationToken)
        => ExecuteAsync(context, request, coordinator.PauseAsync, cancellationToken);

    private static Task<IResult> ResumeAsync(
        HttpContext context,
        [FromBody] CaptureControlEndpointRequest request,
        CaptureAdmissionCoordinator coordinator,
        CancellationToken cancellationToken)
        => ExecuteAsync(context, request, coordinator.ResumeAsync, cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operations endpoints must not disclose persistence or runtime failure details.")]
    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        CaptureControlEndpointRequest request,
        Func<string, long?, string, string?, CancellationToken, Task<CaptureControlCommandResult>> command,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The capture-control command is not authorized.");
        }

        try
        {
            return Results.Ok(await command(
                idempotencyKey,
                request.ExpectedVersion,
                actor,
                request.Reason,
                cancellationToken).ConfigureAwait(false));
        }
        catch (CaptureControlValidationException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The capture-control command is invalid.");
        }
        catch (CaptureControlConflictException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The capture-control command conflicts with durable state.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The capture-control command could not be completed.");
        }
    }

    private sealed record CaptureControlEndpointRequest(long? ExpectedVersion = null, string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
