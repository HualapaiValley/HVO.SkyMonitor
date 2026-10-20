using System.Diagnostics.CodeAnalysis;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentSatellitePlanningUiService
{
    ValueTask<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>> SearchAsync(
        CameraAgentSatellitePassSearchRequest request, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>> GetArcAsync(
        string searchIdentitySha256, string parentPassSha256, CancellationToken cancellationToken);
}

/// <summary>Authorizes every local planning read; no alternate unauthenticated facade route exists.</summary>
internal sealed class CameraAgentSatellitePlanningUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ILogger<CameraAgentSatellitePlanningUiService> logger,
    ICameraAgentSatellitePlanner? planner = null) : ICameraAgentSatellitePlanningUiService
{
    public ValueTask<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassSearchView>>> SearchAsync(
        CameraAgentSatellitePassSearchRequest request, CancellationToken cancellationToken)
        => ReadAsync(() => planner!.SearchAsync(request, cancellationToken), cancellationToken);

    public ValueTask<OperatorUiResult<CameraAgentSatellitePlanningResponse<CameraAgentSatellitePassArcView>>> GetArcAsync(
        string searchIdentitySha256, string parentPassSha256, CancellationToken cancellationToken)
        => ReadAsync(() => planner!.GetArcAsync(searchIdentitySha256, parentPassSha256, cancellationToken), cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The authenticated UI boundary returns fixed sanitized failures and logs only the failure type.")]
    private async ValueTask<OperatorUiResult<CameraAgentSatellitePlanningResponse<T>>> ReadAsync<T>(
        Func<ValueTask<CameraAgentSatellitePlanningResponse<T>>> read, CancellationToken cancellationToken) where T : class
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
            var authorized = await authorizationService.AuthorizeAsync(state.User, resource: null,
                CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false);
            if (!authorized.Succeeded)
            {
                return OperatorUiResult<CameraAgentSatellitePlanningResponse<T>>.Failure(
                    OperatorUiResultKind.Unauthorized, "Authorization is required to read satellite planning.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (planner is null)
            {
                return OperatorUiResult<CameraAgentSatellitePlanningResponse<T>>.Failure(
                    OperatorUiResultKind.Unavailable, "Satellite planning is unavailable on this host.");
            }
            var result = await read().ConfigureAwait(false);
            return OperatorUiResult<CameraAgentSatellitePlanningResponse<T>>.Success(
                result with { Message = Describe(result.Outcome) });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent satellite planning UI read failed ({FailureType}).", exception.GetType().Name);
            return OperatorUiResult<CameraAgentSatellitePlanningResponse<T>>.Failure(
                OperatorUiResultKind.Unavailable, "Satellite planning could not be read. Retry the search.");
        }
    }

    internal static string Describe(CameraAgentSatellitePlanningOutcome outcome) => outcome switch
    {
        CameraAgentSatellitePlanningOutcome.Ready => "Planning result available.",
        CameraAgentSatellitePlanningOutcome.NoSnapshot => "Validated local orbital data is unavailable or could not be read. Retry the search.",
        CameraAgentSatellitePlanningOutcome.TimeInputsUnavailable => "Pinned Earth orientation or leap-second inputs are unavailable for this window.",
        CameraAgentSatellitePlanningOutcome.Invalid => "The planning request is outside the supported bounds.",
        CameraAgentSatellitePlanningOutcome.NoMatches => "No matching objects in this snapshot (matching 0; selected 0). This does not establish global absence.",
        CameraAgentSatellitePlanningOutcome.Busy => "Satellite planning is busy. Retry the search.",
        CameraAgentSatellitePlanningOutcome.Expired => "This planning session expired. Search again before selecting a pass.",
        CameraAgentSatellitePlanningOutcome.ContextChanged => "The observer, rig, time inputs or orbital data context changed. Refresh the scene and search again.",
        _ => "Satellite planning is unavailable. Retry the search."
    };
}

/// <summary>Optional host composition for the protected UI adapter; the Common planner is registered separately.</summary>
internal static class CameraAgentSatellitePlanningUiServiceCollectionExtensions
{
    internal static IServiceCollection AddCameraAgentSatellitePlanningUi(this IServiceCollection services)
    {
        services.TryAddScoped<ICameraAgentSatellitePlanningUiService, CameraAgentSatellitePlanningUiService>();
        return services;
    }
}
