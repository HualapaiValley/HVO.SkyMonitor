using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Endpoints;

internal static class CameraAgentLifecycleEndpoints
{
    private const int InitializationRetryAfterSeconds = 1;

    internal static IEndpointRouteBuilder MapCameraAgentLifecycleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/internal/deployment/lifecycle").AllowAnonymous();
        group.MapGet("/state", ReadStateAsync);
        group.MapPost("/pause", PauseAsync);
        group.MapPost("/resume", ResumeAsync);
        return endpoints;
    }

    private static async Task<IResult> ReadStateAsync(
        HttpContext context,
        IConfiguration configuration,
        CameraAgentOperationsSummaryProvider provider,
        DeploymentContinuityReader continuityReader,
        IOptions<CameraAgentHostOptions> options,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
        var summary = await provider.GetAsync(cancellationToken).ConfigureAwait(false);
        var continuity = await continuityReader.ReadAsync(options.Value.RawIngressRoot, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var captureSequence = continuity.CaptureSequences.Select(static value => value.LastSequence)
            .Append(continuity.FleetMaximumSequence ?? 0)
            .Append(continuity.FleetNextSequence is > 0 ? continuity.FleetNextSequence.Value - 1 : 0)
            .Max();
        return Results.Ok(new LifecycleStateResponse(
            summary.CaptureControl,
            summary.RawIngress,
            summary.CaptureLanes,
            summary.CaptureProcessing,
            summary.ArtifactOutbox,
            captureSequence));
    }

    private static Task<IResult> PauseAsync(
        LifecycleControlRequest request,
        HttpContext context,
        IConfiguration configuration,
        CaptureAdmissionCoordinator coordinator,
        CancellationToken cancellationToken)
        => ExecuteAsync(request, context, configuration, coordinator, coordinator.PauseAsync, "pause", cancellationToken);

    private static Task<IResult> ResumeAsync(
        LifecycleControlRequest request,
        HttpContext context,
        IConfiguration configuration,
        CaptureAdmissionCoordinator coordinator,
        CancellationToken cancellationToken)
        => ExecuteAsync(request, context, configuration, coordinator, coordinator.ResumeAsync, "resume", cancellationToken);

    private static async Task<IResult> ExecuteAsync(
        LifecycleControlRequest request,
        HttpContext context,
        IConfiguration configuration,
        CaptureAdmissionCoordinator coordinator,
        Func<string, long?, string, string?, CancellationToken, Task<CaptureControlCommandResult>> command,
        string action,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
        if (request.OperationId == Guid.Empty) return Results.BadRequest();
        if (!coordinator.Snapshot.IsInitialized)
        {
            context.Response.Headers.RetryAfter = InitializationRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            return Results.Ok(await command(
                $"lifecycle:{request.OperationId:D}:{action}",
                request.ExpectedVersion,
                $"installer-lifecycle:{request.OperationId:D}",
                request.Reason,
                cancellationToken).ConfigureAwait(false));
        }
        catch (CaptureControlValidationException)
        {
            return Results.BadRequest();
        }
        catch (CaptureControlConflictException)
        {
            return Results.Conflict();
        }
    }

    internal static bool IsAuthorized(HttpContext context, IConfiguration configuration)
    {
        var expected = configuration["LifecycleControl:Token"];
        var supplied = context.Request.Headers["X-HVO-Installation-Token"].ToString();
        return !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(supplied) &&
               CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                   SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }

    private sealed record LifecycleControlRequest(Guid OperationId, long? ExpectedVersion = null, string? Reason = null);

    private sealed record LifecycleStateResponse(
        OperationsSection<OperationsCaptureControlState> CaptureControl,
        OperationsSection<OperationsQueueState> RawIngress,
        OperationsSection<OperationsCaptureLanesState> CaptureLanes,
        OperationsSection<OperationsQueueState> CaptureProcessing,
        OperationsSection<OperationsQueueState> ArtifactOutbox,
        long CaptureSequence);
}
