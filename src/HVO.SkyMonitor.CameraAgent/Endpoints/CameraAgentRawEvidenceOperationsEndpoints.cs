using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.RawIngress;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentRawEvidenceOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentRawEvidenceOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/raw-evidence")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        group.MapGet("", ReadAsync).WithName("ReadCameraAgentRawEvidence");
        MapMutation(group, "/revalidate", RawEvidenceReleaseAction.Revalidate, "RevalidateCameraAgentRawEvidence");
        MapMutation(group, "/write-off", RawEvidenceReleaseAction.WriteOff, "WriteOffCameraAgentRawEvidence");
        return endpoints;
    }

    private static void MapMutation(RouteGroupBuilder group, string path, RawEvidenceReleaseAction action, string name)
        => group.MapPost(path, (HttpContext context, [FromBody] ReleaseRequest request,
                [FromServices] IRawEvidenceOperations operations, [FromServices] OutboxOperationsTokenService tokens, CancellationToken cancellationToken)
                => ReleaseAsync(context, request, action, operations, tokens, cancellationToken))
            .WithName(name).RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance);

    private static async Task<IResult> ReadAsync(
        [FromQuery] int? pageSize, [FromQuery] string? cursor, [FromServices] IRawEvidenceOperations operations,
        [FromServices] OutboxOperationsTokenService tokens, CancellationToken cancellationToken)
    {
        var size = pageSize ?? 25;
        long? position = null;
        if (size is < 1 or > 100 || cursor is not null && !tokens.TryReadRawEvidenceCursor(cursor, out position))
            return Results.BadRequest();
        var page = await operations.ReadEvidencePageAsync(size, position, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            Items = page.Items.Select(item => new
            {
                item.AgentId, item.CaptureSequence, CaptureId = item.Target.CaptureId, item.ArtifactId,
                item.PayloadBytes, item.CreatedUtc, item.WrittenOff, item.LatestAudit,
                State = item.Target.State, ReasonCode = item.Target.FailureReason,
                RevalidateToken = item.WrittenOff || item.Target.State == "committed" ? null : tokens.ProtectRawEvidenceAction(RawEvidenceReleaseAction.Revalidate, item.Target),
                WriteOffToken = item.WrittenOff || item.Target.State == "committed" ? null : tokens.ProtectRawEvidenceAction(RawEvidenceReleaseAction.WriteOff, item.Target)
            }),
            NextCursor = page.NextCursor is null ? null : tokens.ProtectRawEvidenceCursor(page.NextCursor.Value)
        });
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Operator responses must not expose durable storage details.")]
    private static async Task<IResult> ReleaseAsync(
        HttpContext context, ReleaseRequest request, RawEvidenceReleaseAction action, IRawEvidenceOperations operations,
        OutboxOperationsTokenService tokens, CancellationToken cancellationToken)
    {
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor)) return Results.Forbid();
        if (!tokens.TryReadRawEvidenceAction(action, request.ActionToken, out var target) || target is null)
            return Results.NotFound();
        try
        {
            return Results.Ok(await operations.ReleaseEvidenceAsync(target, action,
                context.Request.Headers["Idempotency-Key"].ToString(), actor, request.ReasonCode, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return Results.Problem(statusCode: 400, title: "The raw evidence release is invalid.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
        {
            return Results.Problem(statusCode: 409, title: "The raw evidence release conflicts with current evidence or durable state.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return Results.Problem(statusCode: 500, title: "The raw evidence release could not be completed.");
        }
    }

    private sealed record ReleaseRequest(string ActionToken, string ReasonCode);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();
        public bool RequiresValidation => true;
    }
}
