using System.Diagnostics.CodeAnalysis;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Processing;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Scheduling;
using HVO.SkyMonitor.CameraAgent.Services;
using Microsoft.AspNetCore.Mvc;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentPipelineOperationsEndpoints
{
    internal static IEndpointRouteBuilder MapCameraAgentPipelineOperationsEndpoints(
        this IEndpointRouteBuilder endpoints)
    {
        var pipeline = endpoints.MapGroup("/api/v1/operations/pipeline")
            .WithTags("CameraAgent Pipeline")
            .RequireAuthorization(CameraAgentAuthorizationPolicyNames.OperationsReadV1);
        pipeline.MapGet("/", GetAsync).WithName("GetCameraAgentPipeline");
        pipeline.MapPost("/preview", PreviewAsync).WithName("PreviewCameraAgentPipeline");
        return endpoints;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated operator boundary returns fixed sanitized failures.")]
    private static async Task<IResult> GetAsync(
        CaptureScheduleRuntimeCoordinator runtime,
        ICaptureProcessingPipelineFactory pipelineFactory,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(runtime, configurationAccessor, cancellationToken).ConfigureAwait(false);
            var current = runtime.Snapshot ?? throw new InvalidOperationException("The pipeline runtime is unavailable.");
            var state = await runtime.GetOperatorStateAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(CameraAgentPipelineOperatorProjection.CreateState(
                state, current.Configuration, pipelineFactory));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "Current processing graph data is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated preview boundary returns only allowlisted validation details.")]
    private static async Task<IResult> PreviewAsync(
        [FromBody] PipelinePreviewRequest request,
        CaptureScheduleRuntimeCoordinator runtime,
        SqliteCaptureScheduleStore store,
        ICaptureProcessingPipelineFactory pipelineFactory,
        ICameraAgentConfigurationAccessor configurationAccessor,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureInitializedAsync(runtime, configurationAccessor, cancellationToken).ConfigureAwait(false);
            var basis = await store.GetRevisionAsync(request.BasisRevisionId, cancellationToken).ConfigureAwait(false);
            var profile = CameraAgentScheduleOperatorProjection.RestoreOpaqueOptions(request.Profile, basis.Profile);
            var current = runtime.Snapshot ?? throw new InvalidOperationException("The pipeline runtime is unavailable.");
            var plan = CameraAgentPipelineOperatorProjection.Preview(
                profile, current.Configuration, pipelineFactory);
            return Results.Ok(plan);
        }
        catch (InvalidOperationException exception)
        {
            return Invalid(CameraAgentPipelineOperatorProjection.SanitizeValidationFailure(exception));
        }
        catch (Exception exception) when (exception is ArgumentException or KeyNotFoundException or
            CaptureProfileCompatibilityException or ValidationException or JsonException)
        {
            return Invalid("The local profile or desired graph is invalid.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status500InternalServerError,
                title: "The processing graph preview could not be completed.");
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
        => Results.Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: title);

    private sealed record PipelinePreviewRequest(
        LocalCaptureProfileDefinition Profile,
        string BasisRevisionId);
}
