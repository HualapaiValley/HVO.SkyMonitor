using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>Owner-only SWPC operations. All policy and storage access goes through SpaceWeatherOperations.</summary>
internal static class CameraAgentSpaceWeatherOperationsEndpoints
{
    internal const string RoutePrefix = "/api/v1/operations/space-weather";
    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    internal static IEndpointRouteBuilder MapCameraAgentSpaceWeatherOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(RoutePrefix).WithTags("CameraAgent Space Weather");
        group.MapGet("/", GetAsync).RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentSpaceWeather").Produces<SpaceWeatherOperations.Status>().ProducesProblem(503).Produces(401).Produces(403);
        group.MapGet("/notices", NoticesAsync).RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentSpaceWeatherNotices").Produces<SpaceWeatherOperations.NoticePage>().ProducesProblem(400).ProducesProblem(503).Produces(401).Produces(403);
        Mutation(group.MapPost("/refresh", RefreshAsync), "RefreshCameraAgentSpaceWeather");
        Mutation(group.MapPost("/resume", ResumeAsync), "ResumeCameraAgentSpaceWeather");
        Mutation(group.MapPost("/acknowledge", AcknowledgeAsync), "AcknowledgeCameraAgentSpaceWeather");
        return endpoints;
    }

    private static void Mutation(RouteHandlerBuilder route, string name) => route
        .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
        .WithMetadata(RequiredAntiforgeryMetadata.Instance).WithName(name)
        .Produces<SpaceWeatherOperations.CommandResult>().Produces<SpaceWeatherOperations.CommandResult>(202)
        .ProducesProblem(400).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503).Produces(401).Produces(403);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated boundary returns fixed details and logs only exception type.")]
    private static async Task<IResult> GetAsync(SpaceWeatherOperations operations, CancellationToken ct)
    {
        try { return Results.Ok(await operations.GetStatusAsync(ct).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { operations.RecordBoundaryFailure(exception); return Unavailable(); }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated boundary returns fixed details and logs only exception type.")]
    private static async Task<IResult> NoticesAsync(string? state, string? cursor, int? limit,
        SpaceWeatherOperations operations, CancellationToken ct)
    {
        try { return Results.Ok(await operations.GetNoticesAsync(state, cursor, limit ?? 25, ct).ConfigureAwait(false)); }
        catch (ArgumentException) { return Problem(400, "space-weather.invalid-paging", null); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { operations.RecordBoundaryFailure(exception); return Unavailable(); }
    }

    private static Task<IResult> RefreshAsync(HttpContext context, SpaceWeatherOperations operations, CancellationToken ct)
        => ExecuteAsync(context, operations, false, (key, actor) => operations.RefreshAsync(key, actor, ct), ct);
    private static Task<IResult> ResumeAsync(HttpContext context, [FromBody] ResumeBody body,
        SpaceWeatherOperations operations, CancellationToken ct)
        => ExecuteAsync(context, operations, true,
            (key, _) => operations.ResumeAsync(body.Scope, body.Product, body.ExpectedHaltSinceUtc, key, ct), ct);
    private static Task<IResult> AcknowledgeAsync(HttpContext context, [FromBody] AcknowledgeBody body,
        SpaceWeatherOperations operations, CancellationToken ct)
        => ExecuteAsync(context, operations, true,
            (key, actor) => operations.AcknowledgeAsync(body.Group, actor, key, ct), ct);

    /// <summary>Forgery, owner and single-key checks precede commands; only authorized results carry status.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated boundary returns fixed details and logs only exception type.")]
    private static async Task<IResult> ExecuteAsync(HttpContext context, SpaceWeatherOperations operations,
        bool rejectionEvent,
        Func<string, string, ValueTask<SpaceWeatherOperations.CommandResult>> command, CancellationToken ct)
    {
        if (!CameraAgentCredentialAccess.IsForgeryProtected(context))
        {
            operations.RecordAntiforgeryRejection(rejectionEvent);
            return Problem(400, "space-weather.antiforgery-invalid", null);
        }
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            if (rejectionEvent) operations.RecordInvalidActorRejection();
            return Results.Problem(statusCode: 403, title: "The space-weather command is not authorized.");
        }
        try
        {
            var keys = context.Request.Headers[IdempotencyKeyHeader];
            if (keys.Count != 1 || !SpaceWeatherOperationsKeyIsValid(keys[0]))
            {
                if (rejectionEvent) operations.RecordInvalidKeyRejection();
                return Problem(400, "space-weather.invalid-idempotency-key", await operations.GetStatusAsync(ct).ConfigureAwait(false));
            }
            var result = await command(keys[0]!, actor).ConfigureAwait(false);
            return result.Disposition switch
            {
                SpaceWeatherOperations.Disposition.Accepted => Results.Accepted(RoutePrefix, result),
                SpaceWeatherOperations.Disposition.Applied or SpaceWeatherOperations.Disposition.Replayed => Results.Ok(result),
                SpaceWeatherOperations.Disposition.Invalid => Problem(400, result.ReasonCode, result.Status),
                SpaceWeatherOperations.Disposition.NotFound => Problem(404, result.ReasonCode, result.Status),
                SpaceWeatherOperations.Disposition.Conflict => Problem(409, result.ReasonCode, result.Status),
                _ => Problem(503, result.ReasonCode, result.Status)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { operations.RecordBoundaryFailure(exception, rejectionEvent); return Unavailable(); }
    }

    // The operations service repeats this validation; a malformed header never reaches a command.
    private static bool SpaceWeatherOperationsKeyIsValid(string? key)
        => HVO.SkyMonitor.CameraAgent.Common.Automation.LocalAutomationDefinitionValidator.IsText(key,
            HVO.SkyMonitor.CameraAgent.Common.Automation.LocalAutomationContract.MaximumIdempotencyKeyLength);
    private static IResult Unavailable() => Problem(503, "space-weather.store-unavailable", null);
    private static IResult Problem(int status, string reason, SpaceWeatherOperations.Status? current)
        => Results.Problem(statusCode: status, title: status == 503 ? "Space-weather storage is unavailable." : "The space-weather request could not be completed.",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["reasonCode"] = reason, ["currentStatus"] = current });

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record ResumeBody(string? Scope = null, string? Product = null, DateTimeOffset? ExpectedHaltSinceUtc = null);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record AcknowledgeBody(string? Group = null);
    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();
        public bool RequiresValidation => true;
    }

}
