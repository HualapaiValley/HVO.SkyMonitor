using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentProcessingLaneQuarantineEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentProcessingLaneQuarantineEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/processing-lane-quarantine")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        group.MapGet("", ReadAsync).WithName("ReadCameraAgentProcessingLaneQuarantine");
        group.MapPost("/acknowledge", AcknowledgeAsync).WithName("AcknowledgeCameraAgentProcessingLaneQuarantine")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);
        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operator reads must not expose durable storage details.")]
    private static async Task<IResult> ReadAsync(
        [FromQuery] int? pageSize, [FromQuery] string? cursor,
        [FromServices] IProcessingLaneQuarantineOperations operations,
        [FromServices] OutboxOperationsTokenService tokens, CancellationToken cancellationToken)
    {
        var size = pageSize ?? 25;
        long? position = null;
        if (size is < 1 or > 100 || cursor is not null && !tokens.TryReadProcessingLaneCursor(cursor, out position))
        {
            return Results.BadRequest();
        }
        try
        {
            var page = await operations.ReadPageAsync(size, position, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new
            {
                Items = page.Items.Select(item => new
                {
                    item.AgentId,
                    item.CaptureSequence,
                    CaptureId = item.Target.CaptureId,
                    item.ArtifactId,
                    WorkId = item.Target.WorkId,
                    item.PayloadBytes,
                    item.AttemptCount,
                    item.CreatedUtc,
                    item.Acknowledged,
                    item.LatestAudit,
                    item.Target.FailureReason,
                    item.Target.ManifestSha256,
                    AcknowledgeToken = tokens.ProtectProcessingLaneAction(item.Target)
                }).ToArray(),
                NextCursor = page.NextCursor is null ? null : tokens.ProtectProcessingLaneCursor(page.NextCursor.Value)
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: 500, title: "The processing lane quarantine query could not be completed.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operator responses must not expose durable storage details.")]
    private static async Task<IResult> AcknowledgeAsync(
        HttpContext context, [FromBody] AcknowledgeRequest request,
        [FromServices] IProcessingLaneQuarantineOperations operations,
        [FromServices] OutboxOperationsTokenService tokens, CancellationToken cancellationToken)
    {
        if (!CameraAgentCredentialAccess.IsForgeryProtected(context))
        {
            return Results.Problem(statusCode: 400, title: "The processing lane acknowledgement is invalid.");
        }
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Forbid();
        }
        if (!tokens.TryReadProcessingLaneAction(request.ActionToken, out var target) || target is null)
        {
            return Results.NotFound();
        }
        try
        {
            return Results.Ok(await operations.AcknowledgeAsync(
                target, context.Request.Headers["Idempotency-Key"].ToString(), actor, request.ReasonCode,
                cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, title: "The processing lane acknowledgement is invalid.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            return Results.Problem(statusCode: 409, title: "The processing lane acknowledgement conflicts with current durable state.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(statusCode: 500, title: "The processing lane acknowledgement could not be completed.");
        }
    }

    private sealed record AcknowledgeRequest(string ActionToken, string ReasonCode);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();
        public bool RequiresValidation => true;
    }
}
