using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Capture.Focus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal interface ICameraAgentFocusUiService
{
    ValueTask<OperatorUiResult<FocusUiStatus>> GetStatusAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<IReadOnlyList<ManualFocusSessionRecordSummary>>> GetSavedSessionsAsync(
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> StartAsync(
        ManualFocusSessionRequest request,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> AdjustAsync(
        string sessionId,
        ManualFocusAdjustment adjustment,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> ObserveAsync(string sessionId, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> StopAsync(string sessionId, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> SaveAsync(string sessionId, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> DiscardAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>
/// What the focus page shows. Any operations reader sees the session and its latest images; only the session owner,
/// holding the mutate policy, can change, stop, save, or discard it.
/// </summary>
internal sealed record FocusUiStatus(
    ManualFocusSessionSnapshot Session,
    ManualFocusPreviewImages? Images,
    ManualFocusSessionAvailability Availability,
    ManualFocusSessionLimits Limits,
    bool RetentionAvailable,
    bool CanControl,
    bool IsOwner);

/// <summary>
/// Authorizes every focus read and command against the current circuit's principal before it reaches the
/// <see cref="ManualFocusSessionCoordinator"/>. The coordinator identifies the session owner by the canonical owner id,
/// so the same operator can continue a session from another circuit, and a principal that loses authorization can no
/// longer observe it, which ends the session at the observer timeout.
/// </summary>
internal sealed class CameraAgentFocusUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ManualFocusSessionCoordinator coordinator,
    IManualFocusSessionStore store,
    ILogger<CameraAgentFocusUiService> logger) : ICameraAgentFocusUiService
{
    internal const int SavedSessionLimit = 20;

    public async ValueTask<OperatorUiResult<FocusUiStatus>> GetStatusAsync(CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
            .ConfigureAwait(false);
        if (principal is null)
        {
            return Denied<FocusUiStatus>();
        }
        var canControl = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .ConfigureAwait(false) is not null;
        var owner = CameraAgentCredentialAccess.GetOwnerId(principal);
        var session = coordinator.Snapshot;
        return OperatorUiResult<FocusUiStatus>.Success(new FocusUiStatus(
            session,
            coordinator.LatestImages,
            coordinator.Availability,
            coordinator.Limits,
            coordinator.RetentionAvailable,
            canControl,
            canControl && owner is not null && string.Equals(session.OwnerId, owner, StringComparison.Ordinal)));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<IReadOnlyList<ManualFocusSessionRecordSummary>>> GetSavedSessionsAsync(
        CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false) is null)
        {
            return Denied<IReadOnlyList<ManualFocusSessionRecordSummary>>();
        }
        try
        {
            return OperatorUiResult<IReadOnlyList<ManualFocusSessionRecordSummary>>.Success(
                await store.ListAsync(SavedSessionLimit, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent focus session history read failed.");
            return OperatorUiResult<IReadOnlyList<ManualFocusSessionRecordSummary>>.Failure(
                OperatorUiResultKind.Unavailable, "Saved focus sessions are unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> StartAsync(
        ManualFocusSessionRequest request,
        CancellationToken cancellationToken)
        => MutateAsync(actor => coordinator.StartAsync(request, actor, cancellationToken), cancellationToken);

    public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> AdjustAsync(
        string sessionId,
        ManualFocusAdjustment adjustment,
        CancellationToken cancellationToken)
        => MutateAsync(actor => Task.FromResult(coordinator.Adjust(sessionId, actor, adjustment)), cancellationToken);

    public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> ObserveAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(actor => Task.FromResult(coordinator.Observe(sessionId, actor)), cancellationToken);

    public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> StopAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(actor => coordinator.StopAsync(sessionId, actor, cancellationToken), cancellationToken);

    public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> SaveAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(actor => coordinator.SaveAsync(sessionId, actor, cancellationToken), cancellationToken);

    public ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> DiscardAsync(string sessionId, CancellationToken cancellationToken)
        => MutateAsync(actor => Task.FromResult(coordinator.Discard(sessionId, actor)), cancellationToken);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The operator service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<ManualFocusSessionSnapshot>> MutateAsync(
        Func<string, Task<ManualFocusSessionSnapshot>> command,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return Denied<ManualFocusSessionSnapshot>();
        }
        try
        {
            return OperatorUiResult<ManualFocusSessionSnapshot>.Success(await command(actor).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ManualFocusSessionValidationException exception)
        {
            return Failure(OperatorUiResultKind.Invalid, exception.Message);
        }
        catch (ManualFocusSessionConflictException exception)
        {
            return Failure(OperatorUiResultKind.Conflict, exception.Message);
        }
        catch (ManualFocusSessionStateException exception)
        {
            return Failure(OperatorUiResultKind.Conflict, exception.Message);
        }
        catch (ManualFocusSessionUnavailableException exception)
        {
            return Failure(OperatorUiResultKind.Unavailable, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent focus session command failed.");
            return Failure(OperatorUiResultKind.Unavailable, "The focus session command could not be completed.");
        }
    }

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy).ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }

    private static OperatorUiResult<T> Denied<T>()
        => OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");

    private static OperatorUiResult<ManualFocusSessionSnapshot> Failure(OperatorUiResultKind kind, string message)
        => OperatorUiResult<ManualFocusSessionSnapshot>.Failure(kind, message);
}
