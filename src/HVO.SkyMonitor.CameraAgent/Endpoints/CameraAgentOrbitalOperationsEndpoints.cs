using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// The owner-only orbital-elements operator contract: the refresh read model, a refresh request that is queued as one
/// run-now occurrence of the reserved automation definition, and the provider-halt resume. Nothing here accepts a
/// provider URL, a dataset, a path or a schedule, and no command bypasses the provider halt or the two-hour floor.
/// </summary>
internal static class CameraAgentOrbitalOperationsEndpoints
{
    internal const string RoutePrefix = "/api/v1/operations/orbital-elements";

    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>The reason code for a resume request that does not name the halt it expects to clear.</summary>
    internal const string ResumeMissingHaltReasonCode = "orbital.resume-invalid-halt-time";

    /// <summary>The reason code for a resume request without exactly one Idempotency-Key header.</summary>
    internal const string ResumeMissingIdempotencyKeyReasonCode = "orbital.resume-invalid-idempotency-key";

    /// <summary>
    /// The reason code for a session command without a valid antiforgery token. An API key needs none, because a browser
    /// never adds the <c>Authorization</c> header to a cross-site request by itself.
    /// </summary>
    internal const string AntiforgeryInvalidReasonCode = "orbital.antiforgery-invalid";

    internal static IEndpointRouteBuilder MapCameraAgentOrbitalOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(RoutePrefix)
            .WithTags("CameraAgent Orbital Elements");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentOrbitalElements")
            .Produces<OrbitalRefreshStatus>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/refresh", RefreshAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("RefreshCameraAgentOrbitalElements")
            .Produces<OrbitalElementsCommandResult>(StatusCodes.Status202Accepted)
            .Produces<OrbitalElementsCommandResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/resume", ResumeAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ResumeCameraAgentOrbitalElements")
            .Produces<OrbitalElementsCommandResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    /// <summary>
    /// The validated read model. An unavailable store is a reported state (its reason and
    /// <see cref="OrbitalRefreshRecoveryAction.RepairStorage"/>), so only a failure while reading becomes a 503.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetAsync(
        OrbitalElementsOperations operations,
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
            loggerFactory.CreateLogger(typeof(CameraAgentOrbitalOperationsEndpoints))
                .LogWarning(exception, "CameraAgent orbital elements status read failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Orbital elements status is unavailable.");
        }
    }

    private static Task<IResult> RefreshAsync(
        HttpContext context,
        [FromBody] OrbitalRefreshRequestBody body,
        OrbitalElementsOperations operations,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteAsync(
            context,
            loggerFactory,
            OrbitalElementsOperations.InvalidIdempotencyKeyReasonCode,
            null,
            (key, actor) => operations.RefreshAsync(
                new OrbitalElementsRefreshRequest(key, actor, body.Reason ?? string.Empty),
                cancellationToken),
            cancellationToken);
    }

    private static Task<IResult> ResumeAsync(
        HttpContext context,
        [FromBody] OrbitalResumeRequestBody body,
        OrbitalElementsOperations operations,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        return ExecuteAsync(
            context,
            loggerFactory,
            ResumeMissingIdempotencyKeyReasonCode,
            body.ExpectedHaltSinceUtc is null ? ResumeMissingHaltReasonCode : null,
            (key, actor) => operations.ResumeAsync(
                new OrbitalResumeRequest(body.ExpectedHaltSinceUtc!.Value, key, actor, body.Reason ?? string.Empty),
                cancellationToken),
            cancellationToken);
    }

    /// <summary>
    /// Requires forgery protection (a valid antiforgery token for a session, or an API key), an owner identity and
    /// exactly one Idempotency-Key header before the command runs, then maps its
    /// disposition: Accepted is 202 because the refresh is queued rather than executed, Applied and Replayed are 200,
    /// Invalid 400, Conflict 409 and Unavailable 503. Every rejection carries only its bounded reason code and, when the
    /// floor blocked it, the next permitted attempt.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        ILoggerFactory loggerFactory,
        string missingKeyReasonCode,
        string? invalidBodyReasonCode,
        Func<string, string, ValueTask<OrbitalElementsCommandResult>> command,
        CancellationToken cancellationToken)
    {
        // The antiforgery middleware only records whether the token was valid; a JSON-bodied minimal API must check it.
        if (!CameraAgentCredentialAccess.IsForgeryProtected(context))
        {
            return Rejected(StatusCodes.Status400BadRequest, "The orbital elements command is invalid.",
                AntiforgeryInvalidReasonCode, null);
        }
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The orbital elements command is not authorized.");
        }
        var keys = context.Request.Headers[IdempotencyKeyHeader];
        if (keys.Count != 1 || string.IsNullOrWhiteSpace(keys[0]))
        {
            return Rejected(StatusCodes.Status400BadRequest, "Send exactly one Idempotency-Key header.",
                missingKeyReasonCode, null);
        }
        if (invalidBodyReasonCode is not null)
        {
            return Rejected(StatusCodes.Status400BadRequest, "The orbital elements command is invalid.",
                invalidBodyReasonCode, null);
        }
        try
        {
            var result = await command(keys[0]!, actor).ConfigureAwait(false);
            return result.Status switch
            {
                OrbitalElementsCommandStatus.Accepted => Results.Accepted(RoutePrefix, result),
                OrbitalElementsCommandStatus.Applied or OrbitalElementsCommandStatus.Replayed => Results.Ok(result),
                OrbitalElementsCommandStatus.Invalid => Rejected(StatusCodes.Status400BadRequest,
                    "The orbital elements command is invalid.", result.ReasonCode, result.NextPermittedAttemptUtc),
                OrbitalElementsCommandStatus.Conflict => Rejected(StatusCodes.Status409Conflict,
                    "The orbital elements command conflicts with provider policy or durable state.", result.ReasonCode,
                    result.NextPermittedAttemptUtc),
                _ => Rejected(StatusCodes.Status503ServiceUnavailable, "Orbital elements storage is unavailable.",
                    result.ReasonCode, result.NextPermittedAttemptUtc)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger(typeof(CameraAgentOrbitalOperationsEndpoints))
                .LogWarning(exception, "CameraAgent orbital elements command failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The orbital elements command could not be completed.");
        }
    }

    /// <summary>Returns the rejection with its bounded reason code; neither carries operator content.</summary>
    private static IResult Rejected(int statusCode, string title, string reasonCode, DateTimeOffset? nextPermittedAttemptUtc)
    {
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["reasonCode"] = reasonCode };
        if (nextPermittedAttemptUtc is { } next)
        {
            extensions["nextPermittedAttemptUtc"] = next;
        }
        return Results.Problem(statusCode: statusCode, title: title, extensions: extensions);
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record OrbitalRefreshRequestBody(string? Reason = null);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record OrbitalResumeRequestBody(DateTimeOffset? ExpectedHaltSinceUtc = null, string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
