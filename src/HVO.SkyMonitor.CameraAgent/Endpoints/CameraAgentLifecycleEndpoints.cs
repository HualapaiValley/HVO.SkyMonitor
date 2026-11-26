using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.CameraAgent.Common.Capture;
using HVO.SkyMonitor.CameraAgent.Common.Deployment;
using HVO.SkyMonitor.CameraAgent.Common.Operations;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using HVO.SkyMonitor.CameraAgent.Common.Silo;
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
        group.MapGet("/resume-owned-upgrade", (HttpContext context, IConfiguration configuration) =>
            IsAuthorized(context, configuration) ? Results.NoContent() : Results.Unauthorized());
        group.MapPost("/resume-owned-upgrade", ResumeOwnedUpgradeAsync);
        group.MapGet("/first-boot", ReadFirstBootAsync);
        group.MapPost("/first-resume", FirstResumeAsync);
        group.MapPost("/silo/verify-history", VerifySiloHistoryAsync);
        return endpoints;
    }

    private static async Task<IResult> VerifySiloHistoryAsync(HttpContext context, IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
        var verifier = context.RequestServices.GetService<ICameraAgentSiloVerification>();
        if (verifier is null) return Results.NotFound();
        try
        {
            var result = await verifier.VerifyAsync(cancellationToken).ConfigureAwait(false);
            return result.UnavailableRoles > 0 || result.FailedRoles > 0
                ? Results.Json(result, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(result);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
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
            summary.ArchivedReplay,
            captureSequence,
            ArchivedReplayCatalogIndependent: true));
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

    private static async Task<IResult> ResumeOwnedUpgradeAsync(
        OwnedUpgradeResumeRequest request, HttpContext context, IConfiguration configuration,
        CaptureAdmissionCoordinator coordinator, CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
        if (request.OperationId == Guid.Empty || request.UpgradeOperationId == Guid.Empty ||
            request.PauseCommandId == Guid.Empty || request.OperationId == request.PauseCommandId ||
            request.ExpectedVersion is not >= 0 or long.MaxValue)
            return Results.BadRequest();
        if (!coordinator.Snapshot.IsInitialized)
        {
            context.Response.Headers.RetryAfter = InitializationRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            return Results.Ok(await coordinator.ResumeOwnedUpgradeAsync(
                request.UpgradeOperationId, request.PauseCommandId, request.OperationId, request.ExpectedVersion.Value,
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
        // Under a Silo-layout first-boot profile (#1966) the pause owner is the installer operation the request names,
        // so a pause and its resume share one owner although each command keeps its own idempotency key. Without that
        // profile the owner stays the command identity, exactly as before.
        var owner = coordinator.FirstBootPause is not null && request.OwnerOperationId is { } stable && stable != Guid.Empty
            ? stable
            : request.OperationId;
        try
        {
            return Results.Ok(await command(
                $"lifecycle:{request.OperationId:D}:{action}",
                request.ExpectedVersion,
                $"installer-lifecycle:{owner:D}",
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

    /// <summary>
    /// The installer-owned first-boot pause of a Silo-layout instance (#1966). 404 when the instance has none, so a
    /// filesystem-layout instance reports that it was never seeded rather than an empty status.
    /// </summary>
    private static async Task<IResult> ReadFirstBootAsync(
        HttpContext context,
        IConfiguration configuration,
        CaptureAdmissionCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
        if (coordinator.FirstBootPause is null) return Results.NotFound();
        if (!coordinator.Snapshot.IsInitialized)
        {
            context.Response.Headers.RetryAfter = InitializationRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        return Results.Ok(await coordinator.ReadFirstBootPauseAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The atomic first resume (#1966, cutover contract 8.1): accepted only while the seeded first-boot pause is the
    /// latest accepted command at the expected version, checked inside the resume's write transaction. A retry of an
    /// accepted first resume replays its result.
    /// </summary>
    private static async Task<IResult> FirstResumeAsync(
        FirstResumeRequest request,
        HttpContext context,
        IConfiguration configuration,
        CaptureAdmissionCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        if (!IsAuthorized(context, configuration)) return Results.Unauthorized();
        if (coordinator.FirstBootPause is not { } seed) return Results.NotFound();
        if (request.FirstBootId != seed.Id || request.ExpectedVersion < 1) return Results.BadRequest();
        if (!coordinator.Snapshot.IsInitialized ||
            context.RequestServices.GetService<ICameraAgentSiloVerification>() is { BindingVerified: false })
        {
            context.Response.Headers.RetryAfter = InitializationRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            return Results.Ok(await coordinator.ResumeAsync(
                seed.ResumeKey,
                request.ExpectedVersion,
                seed.Actor,
                FirstBootPauseIdentity.ResumeReason,
                cancellationToken).ConfigureAwait(false));
        }
        catch (CaptureControlValidationException)
        {
            return Results.BadRequest();
        }
        catch (CaptureControlConflictException exception)
        {
            return Results.Conflict(new FirstResumeRefusal(exception.Message));
        }
    }

    private sealed record FirstResumeRequest(Guid FirstBootId, long ExpectedVersion);

    private sealed record FirstResumeRefusal(string Reason);

    internal static bool IsAuthorized(HttpContext context, IConfiguration configuration)
    {
        var expected = configuration["LifecycleControl:Token"];
        var supplied = context.Request.Headers["X-HVO-Installation-Token"].ToString();
        return !string.IsNullOrEmpty(expected) && !string.IsNullOrEmpty(supplied) &&
               CryptographicOperations.FixedTimeEquals(
                   SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
                   SHA256.HashData(Encoding.UTF8.GetBytes(supplied)));
    }

    private sealed record LifecycleControlRequest(
        Guid OperationId, long? ExpectedVersion = null, string? Reason = null, Guid? OwnerOperationId = null);

    private sealed record OwnedUpgradeResumeRequest(Guid OperationId, Guid UpgradeOperationId, Guid PauseCommandId, long? ExpectedVersion);

    private sealed record LifecycleStateResponse(
        OperationsSection<OperationsCaptureControlState> CaptureControl,
        OperationsSection<OperationsQueueState> RawIngress,
        OperationsSection<OperationsCaptureLanesState> CaptureLanes,
        OperationsSection<OperationsQueueState> CaptureProcessing,
        OperationsSection<OperationsQueueState> ArtifactOutbox,
        OperationsSection<OperationsArchivedReplayState> ArchivedReplay,
        long CaptureSequence,
        // Declares that archived replay never builds a scene from the mounted catalog (the replay guard in
        // AnnotationCaptureProcessingStep). The lifecycle CLI refuses catalog transitions on an image that omits it.
        bool ArchivedReplayCatalogIndependent);
}
