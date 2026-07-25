using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentScheduleOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentScheduleOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var schedule = endpoints.MapGroup("/api/v1/operations/schedule")
            .WithTags("CameraAgent Schedule");

        schedule.MapGet("/", GetAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentSchedule");
        schedule.MapPost("/preview", PreviewAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("PreviewCameraAgentSchedule");
        schedule.MapPost("/stage", StageAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("StageCameraAgentSchedule");
        schedule.MapPost("/activate", ActivateAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ActivateCameraAgentSchedule");
        schedule.MapPost("/rollback", ActivateAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("RollbackCameraAgentSchedule");
        schedule.MapPost("/overrides", AddOverrideAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("AddCameraAgentScheduleOverride");
        schedule.MapPost("/overrides/{overrideId}/clear", ClearOverrideAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ClearCameraAgentScheduleOverride");
        return endpoints;
    }

    private static async Task<IResult> GetAsync(
        CaptureScheduleRuntimeCoordinator runtime,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(runtime, configurationAccessor, cancellationToken).ConfigureAwait(false);
        return Results.Ok(await runtime.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PreviewAsync(
        [FromBody] SchedulePreviewRequest request,
        CaptureScheduleRuntimeCoordinator runtime,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(runtime, configurationAccessor, cancellationToken).ConfigureAwait(false);
        try
        {
            return Results.Ok(runtime.Preview(request.Profile, request.DayCount));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Invalid("The local profile or schedule preview is invalid.");
        }
    }

    private static Task<IResult> StageAsync(
        HttpContext context,
        [FromBody] ScheduleStageRequest request,
        CaptureScheduleRuntimeCoordinator runtime,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            runtime,
            configurationAccessor,
            (key, actor, token) => runtime.StageAsync(
                request.Profile, key, request.ExpectedVersion, actor, request.Reason, token),
            cancellationToken);

    private static Task<IResult> ActivateAsync(
        HttpContext context,
        [FromBody] ScheduleActivationRequest request,
        CaptureScheduleRuntimeCoordinator runtime,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            runtime,
            configurationAccessor,
            (key, actor, token) => runtime.ActivateAsync(
                request.RevisionId, key, request.ExpectedVersion, actor, request.Reason, token),
            cancellationToken);

    private static Task<IResult> AddOverrideAsync(
        HttpContext context,
        [FromBody] ScheduleOverrideRequest request,
        CaptureScheduleRuntimeCoordinator runtime,
        SqliteCaptureScheduleStore store,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            runtime,
            configurationAccessor,
            (key, actor, token) => store.AddOverrideAsync(
                request.Override, key, request.ExpectedVersion, actor, request.Reason, token),
            cancellationToken);

    private static Task<IResult> ClearOverrideAsync(
        string overrideId,
        HttpContext context,
        [FromBody] ScheduleClearOverrideRequest request,
        CaptureScheduleRuntimeCoordinator runtime,
        SqliteCaptureScheduleStore store,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            runtime,
            configurationAccessor,
            (key, actor, token) => store.ClearOverrideAsync(
                overrideId, key, request.ExpectedVersion, actor, request.Reason, token),
            cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> ExecuteMutationAsync(
        HttpContext context,
        CaptureScheduleRuntimeCoordinator runtime,
        ICameraAgentConfigurationAccessor configurationAccessor,
        Func<string, string, CancellationToken, Task<CaptureScheduleStoreSnapshot>> command,
        CancellationToken cancellationToken)
    {
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        var actor = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "The schedule command is not authorized.");
        }
        try
        {
            await EnsureInitializedAsync(runtime, configurationAccessor, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await command(idempotencyKey, actor, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return Invalid("The schedule command is invalid.");
        }
        catch (KeyNotFoundException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "The schedule revision or override was not found.");
        }
        catch (CaptureScheduleStoreConflictException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "The schedule command conflicts with durable state.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The schedule command could not be completed.");
        }
    }

    private static async Task EnsureInitializedAsync(
        CaptureScheduleRuntimeCoordinator runtime,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
    {
        if (runtime.Snapshot is null)
        {
            var configuration = await configurationAccessor.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
            _ = await runtime.InitializeAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IResult Invalid(string title)
        => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title);

    private sealed record SchedulePreviewRequest(LocalCaptureProfileDefinition Profile, int DayCount = 7);

    private sealed record ScheduleStageRequest(
        LocalCaptureProfileDefinition Profile,
        long? ExpectedVersion = null,
        string? Reason = null);

    private sealed record ScheduleActivationRequest(
        string RevisionId,
        long? ExpectedVersion = null,
        string? Reason = null);

    private sealed record ScheduleOverrideRequest(
        CaptureScheduleOverride Override,
        long? ExpectedVersion = null,
        string? Reason = null);

    private sealed record ScheduleClearOverrideRequest(long? ExpectedVersion = null, string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
