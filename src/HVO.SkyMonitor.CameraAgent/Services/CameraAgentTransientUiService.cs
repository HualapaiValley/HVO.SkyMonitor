using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Transients;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentTransientUiService
{
    ValueTask<OperatorUiResult<TransientCaptureStageView>> GetCaptureStagesAsync(
        Guid captureId, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>> GetPageAsync(
        CameraAgentTransientOperatorQuery query,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentTransientOperatorDetail>> GetCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken);
}

/// <summary>One capture's prospectively recorded stage events; an empty list means not recorded.</summary>
internal sealed record TransientCaptureStageView(Guid CaptureId, IReadOnlyList<TransientStageEvent> Events);

internal sealed class CameraAgentTransientUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ICameraAgentTransientOperatorProjection projection,
    ITransientRuntimeManagement runtime,
    CameraAgentOperatorTelemetry telemetry,
    ILogger<CameraAgentTransientUiService> logger) : ICameraAgentTransientUiService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authorized read boundary logs internal failures and returns a fixed sanitized state.")]
    public async ValueTask<OperatorUiResult<TransientCaptureStageView>> GetCaptureStagesAsync(
        Guid captureId, CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<TransientCaptureStageView>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            var events = await runtime.ReadCaptureStageEventsAsync(captureId, cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<TransientCaptureStageView>.Success(new(captureId, events));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent transient capture stage read failed.");
            return OperatorUiResult<TransientCaptureStageView>.Failure(OperatorUiResultKind.Unavailable,
                "Transient stage evidence is temporarily unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns a fixed sanitized state.")]
    public async ValueTask<OperatorUiResult<CameraAgentTransientOperatorPage>> GetPageAsync(
        CameraAgentTransientOperatorQuery query,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = telemetry.StartTransientQuery();
        try
        {
            if (!await IsAuthorizedAsync().ConfigureAwait(false))
            {
                return Complete(
                    OperatorUiResult<CameraAgentTransientOperatorPage>.Failure(
                        OperatorUiResultKind.Unauthorized, "Authorization is required."),
                    started);
            }
            var page = await projection.GetPageAsync(query, cancellationToken).ConfigureAwait(false);
            return Complete(OperatorUiResult<CameraAgentTransientOperatorPage>.Success(page), started);
        }
        catch (CameraAgentTransientOperatorQueryException)
        {
            return Complete(
                OperatorUiResult<CameraAgentTransientOperatorPage>.Failure(
                    OperatorUiResultKind.Invalid, "The transient page request is invalid."),
                started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("outcome", "cancelled");
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            telemetry.RecordTransientQuery(Stopwatch.GetElapsedTime(started), 0, "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent transient operator list read failed.");
            return Complete(
                OperatorUiResult<CameraAgentTransientOperatorPage>.Failure(
                    OperatorUiResultKind.Unavailable, "Transient evidence is unavailable."),
                started);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns a fixed sanitized state.")]
    public async ValueTask<OperatorUiResult<CameraAgentTransientOperatorDetail>> GetCandidateAsync(
        Guid candidateId,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var activity = telemetry.StartTransientQuery();
        try
        {
            if (!await IsAuthorizedAsync().ConfigureAwait(false))
            {
                return Complete(
                    OperatorUiResult<CameraAgentTransientOperatorDetail>.Failure(
                        OperatorUiResultKind.Unauthorized, "Authorization is required."),
                    started);
            }
            var detail = await projection.GetCandidateAsync(candidateId, cancellationToken).ConfigureAwait(false);
            return detail is null
                ? Complete(
                    OperatorUiResult<CameraAgentTransientOperatorDetail>.Failure(
                        OperatorUiResultKind.NotFound, "The transient candidate was not found."),
                    started)
                : Complete(OperatorUiResult<CameraAgentTransientOperatorDetail>.Success(detail), started);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag("outcome", "cancelled");
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            telemetry.RecordTransientQuery(Stopwatch.GetElapsedTime(started), 0, "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent transient operator detail read failed.");
            return Complete(
                OperatorUiResult<CameraAgentTransientOperatorDetail>.Failure(
                    OperatorUiResultKind.Unavailable, "Transient evidence is unavailable."),
                started);
        }
    }

    private OperatorUiResult<T> Complete<T>(OperatorUiResult<T> result, long started)
    {
        var bytes = result.IsSuccess && result.Value is not null
            ? JsonSerializer.SerializeToUtf8Bytes(result.Value).LongLength
            : 0;
        var outcome = result.Kind switch
        {
            OperatorUiResultKind.Success => "success",
            OperatorUiResultKind.Unauthorized => "unauthorized",
            OperatorUiResultKind.NotFound => "not_found",
            OperatorUiResultKind.Invalid => "invalid",
            _ => "unavailable"
        };
        telemetry.RecordTransientQuery(Stopwatch.GetElapsedTime(started), bytes, outcome);
        Activity.Current?.SetTag("outcome", outcome);
        Activity.Current?.SetStatus(
            result.Kind == OperatorUiResultKind.Unavailable ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
            result.Kind == OperatorUiResultKind.Unavailable ? outcome : null);
        return result;
    }

    private async Task<bool> IsAuthorizedAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return (await authorizationService.AuthorizeAsync(
            state.User, resource: null, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded;
    }
}
