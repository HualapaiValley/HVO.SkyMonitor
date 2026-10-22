using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Settings;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// The operator settings API: the catalogue, the values with their sources, and version-checked group writes. Reads
/// need the operations read policy; a write needs the mutate policy and, for a browser session, an antiforgery token.
/// </summary>
internal static class CameraAgentSettingsEndpoints
{
    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    internal static IEndpointRouteBuilder MapCameraAgentSettingsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var settings = endpoints.MapGroup(CameraAgentSettingsContract.Route)
            .WithTags("CameraAgent Settings");

        settings.MapGet("/catalogue", static () => Results.Ok(CameraAgentSettingsCatalogue.ToResponse()))
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentSettingsCatalogue")
            .Produces<CameraAgentSettingsCatalogueResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        settings.MapGet("/", GetAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentSettings")
            .Produces<CameraAgentSettingsResponse>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        settings.MapGet("/{group}", GetGroupAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentSettingsGroup")
            .Produces<CameraAgentSettingsGroupState>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        settings.MapPut("/{group}", PutAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("UpdateCameraAgentSettingsGroup")
            .Produces<CameraAgentSettingsWriteResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    private static async Task<IResult> GetAsync(CameraAgentSettingsService service, CancellationToken cancellationToken)
        => Results.Ok(await service.GetAsync(cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> GetGroupAsync(
        string group,
        CameraAgentSettingsService service,
        CancellationToken cancellationToken)
        => await service.GetGroupAsync(group, cancellationToken).ConfigureAwait(false) is { } state
            ? Results.Ok(state)
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No settings group has this name.");

    private static async Task<IResult> PutAsync(
        string group,
        HttpContext context,
        [FromBody] CameraAgentSettingsUpdateRequest request,
        CameraAgentSettingsService service,
        CancellationToken cancellationToken)
    {
        if (!CameraAgentCredentialAccess.IsForgeryProtected(context))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The settings request is invalid.");
        }

        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The settings change is not authorized.");
        }

        var keys = context.Request.Headers[IdempotencyKeyHeader];
        if (keys.Count > 1)
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Send at most one Idempotency-Key header.");
        }

        var result = await service.WriteAsync(
            group,
            request,
            actor,
            string.IsNullOrEmpty(keys.ToString()) ? null : keys.ToString(),
            cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            CameraAgentSettingsWriteOutcome.Applied => Ok(CameraAgentSettingsWriteStatus.Applied, result),
            CameraAgentSettingsWriteOutcome.Unchanged => Ok(CameraAgentSettingsWriteStatus.Unchanged, result),
            CameraAgentSettingsWriteOutcome.PendingRestart => Ok(CameraAgentSettingsWriteStatus.PendingRestart, result),
            CameraAgentSettingsWriteOutcome.Invalid when result.Problems is { Count: > 0 } problems => Results.ValidationProblem(
                problems.ToDictionary(problem => problem.Key, problem => new[] { problem.Value }, StringComparer.Ordinal),
                title: result.Message,
                extensions: Extensions("settings.invalid", result)),
            CameraAgentSettingsWriteOutcome.Invalid => Problem(StatusCodes.Status400BadRequest, "settings.invalid", result),
            CameraAgentSettingsWriteOutcome.Conflict => Problem(StatusCodes.Status409Conflict, "settings.conflict", result),
            CameraAgentSettingsWriteOutcome.ReadOnly => Problem(StatusCodes.Status409Conflict, "settings.readOnly", result),
            CameraAgentSettingsWriteOutcome.NotFound => Problem(StatusCodes.Status404NotFound, "settings.notFound", result),
            _ => Problem(StatusCodes.Status503ServiceUnavailable, "settings.unavailable", result)
        };
    }

    private static IResult Ok(CameraAgentSettingsWriteStatus status, CameraAgentSettingsWriteResult result)
        => Results.Ok(new CameraAgentSettingsWriteResponse(status, result.Group!, result.Recorded ?? []));

    private static IResult Problem(int statusCode, string code, CameraAgentSettingsWriteResult result)
        => Results.Problem(
            statusCode: statusCode,
            title: result.Message ?? "The settings change was refused.",
            extensions: Extensions(code, result));

    /// <summary>
    /// The refusal code, the group as it is now so a client can refresh its form, and what a partly completed write
    /// recorded. Values a client sent are never echoed.
    /// </summary>
    private static Dictionary<string, object?> Extensions(string code, CameraAgentSettingsWriteResult result)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = code };
        if (result.Group is not null)
        {
            extensions["group"] = result.Group;
        }

        if (result.Problems is { Count: > 0 } problems && result.Outcome != CameraAgentSettingsWriteOutcome.Invalid)
        {
            extensions["settings"] = problems;
        }

        if (result.Recorded is { Count: > 0 } recorded)
        {
            extensions["recorded"] = recorded;
        }

        if (result.NotDone is { Count: > 0 } notDone)
        {
            extensions["notDone"] = notDone;
        }

        return extensions;
    }

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
