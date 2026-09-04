using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.DeploymentLocation;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentSkyMapUiService
{
    ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualDeploymentLocationState>> GetManualLocationAsync(
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualDeploymentLocationResult>> ApplyManualLocationAsync(
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentSkyMapUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ICameraAgentSkyMapProjection projection,
    IDeploymentLocationStore deploymentLocationStore,
    TimeProvider timeProvider,
    ILogger<CameraAgentSkyMapUiService> logger) : ICameraAgentSkyMapUiService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        if (atUtc is { } requested &&
            !CameraAgentSkyMapInstantBounds.IsWithinBounds(requested, timeProvider.GetUtcNow()))
        {
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Invalid, CameraAgentSkyMapInstantBounds.RejectionMessage);
        }
        try
        {
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Success(
                await projection.ProjectAsync(atUtc, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent sky map UI read failed.");
            return OperatorUiResult<CameraAgentSkyMapProjectionResult>.Failure(
                OperatorUiResultKind.Unavailable, "The sky map projection is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<ManualDeploymentLocationState>> GetManualLocationAsync(
        CancellationToken cancellationToken)
    {
        if (!await IsAuthorizedAsync().ConfigureAwait(false))
        {
            return OperatorUiResult<ManualDeploymentLocationState>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            return OperatorUiResult<ManualDeploymentLocationState>.Success(deploymentLocationStore.Manual);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent manual deployment-location read failed.");
            return OperatorUiResult<ManualDeploymentLocationState>.Failure(
                OperatorUiResultKind.Unavailable, "Manual deployment-location state is unavailable.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<ManualDeploymentLocationResult>> ApplyManualLocationAsync(
        double latitudeDegrees,
        double longitudeDegrees,
        double elevationMeters,
        string timeZoneId,
        long expectedVersion,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            var result = await deploymentLocationStore.ApplyManualAsync(
                new ManualDeploymentLocationRequest(
                    latitudeDegrees,
                    longitudeDegrees,
                    elevationMeters,
                    timeZoneId,
                    expectedVersion,
                    idempotencyKey,
                    actor,
                    reason),
                cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                ManualDeploymentLocationStatus.Conflict => OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Conflict, DescribeFailure(result)),
                ManualDeploymentLocationStatus.Invalid => OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                    OperatorUiResultKind.Invalid, DescribeFailure(result)),
                _ => OperatorUiResult<ManualDeploymentLocationResult>.Success(result)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent manual deployment-location command failed.");
            return OperatorUiResult<ManualDeploymentLocationResult>.Failure(
                OperatorUiResultKind.Unavailable, "The coordinate change could not be completed.");
        }
    }

    /// <summary>Maps a rejected manual command to fixed operator-facing guidance.</summary>
    internal static string DescribeFailure(ManualDeploymentLocationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.Equals(
                result.ReasonCode,
                ManualDeploymentLocationContract.ExpectedVersionConflictReasonCode,
                StringComparison.Ordinal))
        {
            return "The deployment location changed since this page was read. Refresh before retrying.";
        }
        if (string.Equals(
                result.ReasonCode,
                ManualDeploymentLocationContract.IdempotencyKeyConflictReasonCode,
                StringComparison.Ordinal))
        {
            return "This command identifier was already recorded with different coordinates. Refresh before retrying.";
        }
        return result.FieldPath switch
        {
            "location.latitudeDegrees" => "Latitude must be a number between -90 and 90 degrees.",
            "location.longitudeDegrees" => "Longitude must be a number between -180 and 180 degrees, east positive.",
            "location.elevationMeters" =>
                $"Elevation must be a number between {ManualDeploymentLocationContract.MinimumElevationMeters:F0} "
                + $"and {ManualDeploymentLocationContract.MaximumElevationMeters:F0} metres.",
            "location.timeZoneId" =>
                "Time zone must be an IANA identifier this host can resolve, such as America/Phoenix or UTC.",
            _ => "The coordinate entry was rejected before anything durable changed."
        };
    }

    private async Task<bool> IsAuthorizedAsync()
        => await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false) is not null;

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy)
            .ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}

/// <summary>The bounded instant window every sky map read accepts.</summary>
internal static class CameraAgentSkyMapInstantBounds
{
    internal static readonly TimeSpan MaximumFuture = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaximumPast = TimeSpan.FromDays(62);

    internal const string RejectionMessage =
        "The requested instant must be no more than 24 hours ahead of, or 62 days behind, the current time.";

    internal static bool IsWithinBounds(DateTimeOffset atUtc, DateTimeOffset nowUtc)
        => atUtc <= nowUtc + MaximumFuture && atUtc >= nowUtc - MaximumPast;
}
