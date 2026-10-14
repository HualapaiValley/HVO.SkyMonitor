using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// Authorized reads of saved manual focus sessions. A record is served only after its checksum and schema verify, as the
/// exact stored bytes with their SHA-256, so an export is the retained evidence rather than a re-serialization.
/// </summary>
internal static class CameraAgentFocusSessionEndpoints
{
    private const string ChecksumHeader = "X-Artifact-SHA256";

    internal static IEndpointRouteBuilder MapCameraAgentFocusSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sessions = endpoints.MapGroup("/api/v1/operations/focus-sessions")
            .WithTags("CameraAgent Focus Sessions")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        sessions.MapGet("/", ListAsync).WithName("ListCameraAgentFocusSessions");
        sessions.MapGet("/{recordId}/export", ExportAsync)
            .WithName("ExportCameraAgentFocusSession")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(IManualFocusSessionStore store, CancellationToken cancellationToken)
        => Results.Ok(await store.ListAsync(store.MaximumRecords, cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ExportAsync(
        string recordId,
        HttpContext context,
        IManualFocusSessionStore store,
        CancellationToken cancellationToken)
    {
        ManualFocusSessionRecordContent? content;
        try
        {
            content = await store.ReadAsync(recordId, cancellationToken).ConfigureAwait(false);
        }
        catch (ManualFocusSessionValidationException)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "The focus session record identifier is invalid.");
        }
        catch (InvalidDataException)
        {
            return Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "The focus session record failed checksum verification and is not exported.");
        }
        if (content is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "The focus session record was not found.");
        }
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.Vary = "Cookie";
        context.Response.Headers[ChecksumHeader] = content.Summary.Sha256;
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(content.Utf8Json.ToArray(), "application/json",
            $"hvo-focus-session-{content.Summary.RecordId}.json");
    }
}
