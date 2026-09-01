using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentProcessingGraphOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentProcessingGraphOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var graphs = endpoints.MapGroup("/api/v1/operations/processing-graphs")
            .WithTags("CameraAgent Processing Graphs");
        graphs.MapGet("/", GetRegistryAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentProcessingGraphRegistry");
        graphs.MapPost("/revisions", CreateRevisionAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("CreateCameraAgentProcessingGraphRevision");
        graphs.MapPost("/revisions/{revisionId}/activate", ActivateRevisionAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ActivateCameraAgentProcessingGraphRevision");
        graphs.MapPost("/revisions/{revisionId}/validate", ValidateRevisionAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("ValidateCameraAgentProcessingGraphRevision");
        graphs.MapPost("/revisions/{revisionId}/retire", RetireRevisionAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("RetireCameraAgentProcessingGraphRevision");
        graphs.MapGet("/executions", ReadExecutionsAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentProcessingExecutions");
        graphs.MapGet("/executions/{executionId:guid}", ReadExecutionAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentProcessingExecution");
        graphs.MapMethods(
                "/executions/{executionId:guid}/outputs/{artifactId:guid}/content",
                [HttpMethods.Get, HttpMethods.Head],
                WriteReplayOutputContentAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .WithName("GetCameraAgentProcessingExecutionOutputContent")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status206PartialContent)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status410Gone)
            .Produces(StatusCodes.Status416RangeNotSatisfiable)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        graphs.MapPost("/replays", SubmitReplayAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("SubmitCameraAgentProcessingReplay");
        graphs.MapPost("/executions/{executionId:guid}/cancel", CancelReplayAsync)
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .WithMetadata(RequiredAntiforgeryMetadata.Instance)
            .WithName("CancelCameraAgentProcessingReplay");
        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated read boundary returns fixed sanitized failures.")]
    private static async Task<IResult> GetRegistryAsync(
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await operations.GetRegistryAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(StatusCodes.Status500InternalServerError, "Processing graph registry data is unavailable.");
        }
    }

    private static Task<IResult> CreateRevisionAsync(
        HttpContext context,
        [FromBody] ProcessingGraphRevisionRequest request,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            (key, actor, token) => operations.CreateRevisionAsync(
                request.Name, request.Revision, request.Pipeline, key, actor, request.Reason, token),
            cancellationToken);

    private static Task<IResult> ActivateRevisionAsync(
        string revisionId,
        HttpContext context,
        [FromBody] ProcessingGraphLifecycleRequest request,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
        => request.ExpectedVersion is not { } expectedVersion
            ? Task.FromResult(Problem(StatusCodes.Status400BadRequest, "The expected registry version is required."))
            : ExecuteMutationAsync(
                context,
                (key, actor, token) => operations.ActivateRevisionAsync(
                    revisionId, expectedVersion, key, actor, request.Reason, token),
                cancellationToken);

    private static Task<IResult> ValidateRevisionAsync(
        string revisionId,
        HttpContext context,
        [FromBody] ProcessingGraphLifecycleRequest request,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            (key, actor, token) => operations.ValidateRevisionAsync(
                revisionId, key, actor, request.Reason, token),
            cancellationToken);

    private static Task<IResult> RetireRevisionAsync(
        string revisionId,
        HttpContext context,
        [FromBody] ProcessingGraphLifecycleRequest request,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
        => request.ExpectedVersion is not { } expectedVersion
            ? Task.FromResult(Problem(StatusCodes.Status400BadRequest, "The expected registry version is required."))
            : ExecuteMutationAsync(
                context,
                (key, actor, token) => operations.RetireRevisionAsync(
                    revisionId, expectedVersion, key, actor, request.Reason, token),
                cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated read boundary returns fixed sanitized failures.")]
    private static async Task<IResult> ReadExecutionsAsync(
        [FromQuery] ProcessingGraphExecutionClass? executionClass,
        [FromQuery] int maximumCount,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
    {
        try
        {
            return Results.Ok(await operations.ReadExecutionsAsync(
                executionClass, maximumCount == 0 ? 50 : maximumCount, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The processing execution query is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(StatusCodes.Status500InternalServerError, "Processing execution data is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated read boundary returns fixed sanitized failures.")]
    private static async Task<IResult> ReadExecutionAsync(
        Guid executionId,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operations.ReadExecutionDetailAsync(executionId, cancellationToken).ConfigureAwait(false) is { } execution
                ? Results.Ok(execution)
                : Problem(StatusCodes.Status404NotFound, "The processing execution was not found.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(StatusCodes.Status500InternalServerError, "Processing execution data is unavailable.");
        }
    }

    private static async Task WriteReplayOutputContentAsync(
        Guid executionId,
        Guid artifactId,
        HttpContext context,
        ICameraAgentArtifactService artifacts,
        CancellationToken cancellationToken)
    {
        var opened = await artifacts.OpenReplayOutputContentAsync(
            executionId, artifactId, cancellationToken).ConfigureAwait(false);
        await CameraAgentArtifactEndpoints.WriteOpenedContentAsync(context, opened, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task<IResult> SubmitReplayAsync(
        HttpContext context,
        [FromBody] ProcessingReplayRequest request,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            async (key, actor, token) =>
            {
                var result = await operations.SubmitReplayAsync(
                    new ProcessingReplaySubmission(
                        request.CaptureId,
                        request.GraphRevisionId,
                        request.PrimaryArtifactId,
                        request.TriggerKind,
                        request.TriggerReference,
                        request.Priority,
                        request.Reason),
                    key,
                    actor,
                    token).ConfigureAwait(false);
                return result;
            },
            cancellationToken,
            accepted: true);

    private static Task<IResult> CancelReplayAsync(
        Guid executionId,
        HttpContext context,
        [FromBody] ProcessingReplayCancellationRequest request,
        IProcessingGraphOperations operations,
        CancellationToken cancellationToken)
        => ExecuteMutationAsync(
            context,
            (key, actor, token) => operations.CancelReplayAsync(
                executionId, key, actor, request.Reason, token),
            cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The authenticated operator boundary returns only fixed failure details.")]
    private static async Task<IResult> ExecuteMutationAsync<T>(
        HttpContext context,
        Func<string, string, CancellationToken, ValueTask<T>> command,
        CancellationToken cancellationToken,
        bool accepted = false)
    {
        var actor = CameraAgentCredentialAccess.GetOwnerId(context.User);
        if (string.IsNullOrWhiteSpace(actor))
            return Problem(StatusCodes.Status403Forbidden, "The processing graph command is not authorized.");
        var idempotencyKey = context.Request.Headers["Idempotency-Key"].ToString();
        try
        {
            var result = await command(idempotencyKey, actor, cancellationToken).ConfigureAwait(false);
            return accepted ? Results.Accepted(value: result) : Results.Ok(result);
        }
        catch (ArgumentException)
        {
            return Problem(StatusCodes.Status400BadRequest, "The processing graph command is invalid.");
        }
        catch (Exception exception) when (exception is KeyNotFoundException or FileNotFoundException)
        {
            return Problem(StatusCodes.Status404NotFound, "The processing graph revision, execution, or replay input was not found.");
        }
        catch (ProcessingGraphStoreConflictException)
        {
            return Problem(StatusCodes.Status409Conflict, "The processing graph command conflicts with durable state.");
        }
        catch (ProcessingReplayCapacityException)
        {
            return Problem(StatusCodes.Status429TooManyRequests, "The local processing replay queue is at capacity.");
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException)
        {
            return Problem(StatusCodes.Status422UnprocessableEntity, "The processing graph or replay input is not usable.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Problem(StatusCodes.Status500InternalServerError, "The processing graph command could not be completed.");
        }
    }

    private static IResult Problem(int statusCode, string title)
        => Results.Problem(statusCode: statusCode, title: title);

    private sealed record ProcessingGraphRevisionRequest(
        string Name,
        string Revision,
        CapturePipelineConfig Pipeline,
        string? Reason = null);

    private sealed record ProcessingGraphLifecycleRequest(long? ExpectedVersion = null, string? Reason = null);

    private sealed record ProcessingReplayRequest(
        Guid CaptureId,
        string GraphRevisionId,
        Guid? PrimaryArtifactId = null,
        string TriggerKind = "operator",
        string? TriggerReference = null,
        int Priority = 0,
        string? Reason = null);

    private sealed record ProcessingReplayCancellationRequest(string? Reason = null);

    private sealed class RequiredAntiforgeryMetadata : IAntiforgeryMetadata
    {
        internal static RequiredAntiforgeryMetadata Instance { get; } = new();

        public bool RequiresValidation => true;
    }
}
