using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.SkyMap;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentSkyMapUiService
{
    ValueTask<OperatorUiResult<CameraAgentSkyMapProjectionResult>> GetSkyMapAsync(
        DateTimeOffset? atUtc,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentSkyMapUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ICameraAgentSkyMapProjection projection,
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

    private async Task<bool> IsAuthorizedAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        return (await authorizationService.AuthorizeAsync(
            state.User, resource: null, CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false)).Succeeded;
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
