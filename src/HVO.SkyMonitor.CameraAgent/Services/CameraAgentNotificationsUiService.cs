using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>
/// Sanitized Blazor UI service for configuring optional standalone CameraAgent operator notifications and triggering
/// rate-limited test deliveries without ever exposing stored secrets back to the browser.
/// </summary>
internal interface ICameraAgentNotificationsUiService
{
    ValueTask<OperatorUiResult<NotificationsPageView>> GetAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<NotificationsSaveUiView>> SaveAsync(
        OperatorNotificationsUpdateRequest request,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<OperatorNotificationTestResult>> SendTestAsync(
        NotificationTestTarget target,
        CancellationToken cancellationToken);
}

internal sealed record NotificationsPageView(
    OperatorNotificationsConfigurationSnapshot Configuration,
    OperatorNotificationDispatcherSnapshot Status,
    bool CanMutate);

internal sealed record NotificationsSaveUiView(
    OperatorNotificationsConfigurationSnapshot Configuration,
    OperatorNotificationDispatcherSnapshot Status,
    string Message);

internal sealed class CameraAgentNotificationsUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    IOperatorNotificationService notificationService,
    ILogger<CameraAgentNotificationsUiService> logger) : ICameraAgentNotificationsUiService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "UI service logs only sanitized exception type and returns fixed operator-facing errors.")]
    public async ValueTask<OperatorUiResult<NotificationsPageView>> GetAsync(CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
                .ConfigureAwait(false) is null)
        {
            return OperatorUiResult<NotificationsPageView>.Failure(
                OperatorUiResultKind.Unauthorized, "You are not authorized to view operator notifications.");
        }

        try
        {
            var canMutate = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
                .ConfigureAwait(false) is not null;
            var configuration = await notificationService.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            var status = notificationService.GetStatus();
            return OperatorUiResult<NotificationsPageView>.Success(new NotificationsPageView(configuration, status, canMutate));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Operator notifications UI projection failed with {ExceptionType}.", ex.GetType().Name);
            return OperatorUiResult<NotificationsPageView>.Failure(
                OperatorUiResultKind.Unavailable, "Notification configuration could not be loaded.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "UI service logs only sanitized exception type and never echoes entered secrets in errors.")]
    public async ValueTask<OperatorUiResult<NotificationsSaveUiView>> SaveAsync(
        OperatorNotificationsUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var principal = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return OperatorUiResult<NotificationsSaveUiView>.Failure(
                OperatorUiResultKind.Unauthorized, "You are not authorized to change notification settings.");
        }

        try
        {
            var result = await notificationService.UpdateConfigurationAsync(request, actor, cancellationToken).ConfigureAwait(false);
            return result.Outcome switch
            {
                OperatorNotificationsSaveOutcome.Applied when result.Snapshot is not null =>
                    OperatorUiResult<NotificationsSaveUiView>.Success(new NotificationsSaveUiView(
                        result.Snapshot,
                        notificationService.GetStatus(),
                        result.Message ?? "Notification settings saved.")),
                OperatorNotificationsSaveOutcome.Conflict =>
                    OperatorUiResult<NotificationsSaveUiView>.Failure(
                        OperatorUiResultKind.Conflict,
                        result.Message ?? "Notification settings changed since this page was loaded. Refresh and retry."),
                OperatorNotificationsSaveOutcome.Invalid =>
                    OperatorUiResult<NotificationsSaveUiView>.Failure(
                        OperatorUiResultKind.Invalid,
                        FormatValidationMessage(result.Message, result.Problems)),
                _ => OperatorUiResult<NotificationsSaveUiView>.Failure(
                    OperatorUiResultKind.Unavailable,
                    result.Message ?? "Notification settings could not be saved.")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Operator notifications UI save failed with {ExceptionType}.", ex.GetType().Name);
            return OperatorUiResult<NotificationsSaveUiView>.Failure(
                OperatorUiResultKind.Unavailable, "Notification settings could not be saved.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "UI service logs only sanitized exception type and returns fixed operator-facing errors.")]
    public async ValueTask<OperatorUiResult<OperatorNotificationTestResult>> SendTestAsync(
        NotificationTestTarget target,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsMutateV1)
            .ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return OperatorUiResult<OperatorNotificationTestResult>.Failure(
                OperatorUiResultKind.Unauthorized, "You are not authorized to send test notifications.");
        }

        try
        {
            var result = await notificationService.SendTestAsync(target, actor, cancellationToken).ConfigureAwait(false);
            return OperatorUiResult<OperatorNotificationTestResult>.Success(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Operator notifications UI test send failed with {ExceptionType}.", ex.GetType().Name);
            return OperatorUiResult<OperatorNotificationTestResult>.Failure(
                OperatorUiResultKind.Unavailable, "Test notification could not be sent.");
        }
    }

    private static string FormatValidationMessage(string? message, IReadOnlyDictionary<string, string>? problems)
    {
        if (problems is { Count: > 0 })
        {
            return string.Join(" ", problems.Values.Distinct(StringComparer.Ordinal));
        }
        return message ?? "One or more notification settings are invalid.";
    }

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy)
            .ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}
