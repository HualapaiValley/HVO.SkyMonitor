using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

/// <summary>
/// The owner-only local automation contract. A command may only name a task kind and a trigger kind
/// the local registry publishes; nothing here accepts a command line, script, path, or URL.
/// </summary>
internal static class CameraAgentAutomationOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentAutomationOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/operations/automations")
            .WithTags("CameraAgent Automations");

        group.MapGet("/", GetAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentAutomations")
            .Produces<LocalAutomationOperatorState>()
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/definitions", SaveAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("SaveCameraAgentAutomation")
            .Produces<LocalAutomationCommandResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost("/definitions/{definitionId}/removal", RemoveAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("RemoveCameraAgentAutomation")
            .Produces<LocalAutomationCommandResult>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> GetAsync(
        ILocalAutomationStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await store.GetStateAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger(typeof(CameraAgentAutomationOperationsEndpoints))
                .LogWarning(exception, "CameraAgent local automation read failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Local automation state is unavailable.");
        }
    }

    private static Task<IResult> SaveAsync(
        HttpContext context,
        [FromBody] AutomationSaveRequestBody body,
        ILocalAutomationStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.ExpectedVersion is not { } expectedVersion ||
            body.Enabled is not { } enabled ||
            body.TriggerInterval is not { } triggerInterval ||
            body.TaskKind is not { } taskKind ||
            body.TriggerKind is not { } triggerKind)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The expected version, enablement, task kind, trigger kind, and interval are required."));
        }
        return ExecuteAsync(
            context,
            loggerFactory,
            actor => store.SaveAsync(
                new LocalAutomationSaveRequest(
                    body.DefinitionId ?? string.Empty,
                    body.Name ?? string.Empty,
                    enabled,
                    taskKind,
                    body.TaskTarget ?? string.Empty,
                    triggerKind,
                    triggerInterval,
                    expectedVersion,
                    ReadIdempotencyKey(context),
                    actor,
                    body.Reason),
                cancellationToken),
            cancellationToken);
    }

    private static Task<IResult> RemoveAsync(
        HttpContext context,
        string definitionId,
        [FromBody] AutomationRemovalRequestBody body,
        ILocalAutomationStore store,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (body.ExpectedVersion is not { } expectedVersion)
        {
            return Task.FromResult(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The expected automation version is required."));
        }
        return ExecuteAsync(
            context,
            loggerFactory,
            actor => store.RemoveAsync(
                new LocalAutomationRemoveRequest(
                    definitionId,
                    expectedVersion,
                    ReadIdempotencyKey(context),
                    actor,
                    body.Reason),
                cancellationToken),
            cancellationToken);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> ExecuteAsync(
        HttpContext context,
        ILoggerFactory loggerFactory,
        Func<string, ValueTask<LocalAutomationCommandResult>> command,
        CancellationToken cancellationToken)
    {
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The automation command is not authorized.");
        }
        try
        {
            var result = await command(actor).ConfigureAwait(false);
            return result.Status switch
            {
                LocalAutomationCommandStatus.Invalid => Rejected(
                    StatusCodes.Status400BadRequest, "The automation command is invalid.", result),
                LocalAutomationCommandStatus.NotFound => Rejected(
                    StatusCodes.Status404NotFound, "The automation definition was not found.", result),
                LocalAutomationCommandStatus.Conflict => Rejected(
                    StatusCodes.Status409Conflict, "The automation command conflicts with durable state.", result),
                _ => Results.Ok(result)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger(typeof(CameraAgentAutomationOperationsEndpoints))
                .LogWarning(exception, "CameraAgent local automation command failed.");
            return Results.Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The automation command could not be completed.");
        }
    }

    private static string ReadIdempotencyKey(HttpContext context)
        => context.Request.Headers["Idempotency-Key"].ToString();

    /// <summary>Returns the rejection with its bounded reason code; neither carries operator content.</summary>
    private static IResult Rejected(int statusCode, string title, LocalAutomationCommandResult result)
        => Results.Problem(
            statusCode: statusCode,
            title: title,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["reasonCode"] = result.ReasonCode,
                ["fieldPath"] = result.FieldPath
            });

    private sealed record AutomationSaveRequestBody(
        string? DefinitionId = null,
        string? Name = null,
        bool? Enabled = null,
        LocalAutomationTaskKind? TaskKind = null,
        string? TaskTarget = null,
        LocalAutomationTriggerKind? TriggerKind = null,
        int? TriggerInterval = null,
        long? ExpectedVersion = null,
        string? Reason = null);

    private sealed record AutomationRemovalRequestBody(
        long? ExpectedVersion = null,
        string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
