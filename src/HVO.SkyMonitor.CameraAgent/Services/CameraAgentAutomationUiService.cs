using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

/// <summary>The operator surface of the versioned durable local automation contract.</summary>
internal interface ICameraAgentAutomationUiService
{
    ValueTask<OperatorUiResult<LocalAutomationOperatorState>> GetAsync(CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<LocalAutomationCommandResult>> SaveAsync(
        LocalAutomationSaveRequest request,
        CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RemoveAsync(
        LocalAutomationRemoveRequest request,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentAutomationUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ILocalAutomationStore store,
    ILogger<CameraAgentAutomationUiService> logger) : ICameraAgentAutomationUiService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    public async ValueTask<OperatorUiResult<LocalAutomationOperatorState>> GetAsync(
        CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(
                CameraAgentAuthorizationPolicyNames.OperationsReadV1).ConfigureAwait(false) is null)
        {
            return OperatorUiResult<LocalAutomationOperatorState>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            return OperatorUiResult<LocalAutomationOperatorState>.Success(
                await store.GetStateAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent local automation read failed.");
            return OperatorUiResult<LocalAutomationOperatorState>.Failure(
                OperatorUiResultKind.Unavailable, "Local automation state is unavailable.");
        }
    }

    public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> SaveAsync(
        LocalAutomationSaveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            actor => store.SaveAsync(request with { Actor = actor }, cancellationToken), cancellationToken);
    }

    public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RemoveAsync(
        LocalAutomationRemoveRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(
            actor => store.RemoveAsync(request with { Actor = actor }, cancellationToken), cancellationToken);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The UI service logs internal failures and returns fixed sanitized states.")]
    private async ValueTask<OperatorUiResult<LocalAutomationCommandResult>> ExecuteAsync(
        Func<string, ValueTask<LocalAutomationCommandResult>> command,
        CancellationToken cancellationToken)
    {
        var principal = await GetAuthorizedPrincipalAsync(
            CameraAgentAuthorizationPolicyNames.OperationsMutateV1).ConfigureAwait(false);
        var actor = principal is null ? null : CameraAgentCredentialAccess.GetOwnerId(principal);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return OperatorUiResult<LocalAutomationCommandResult>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        try
        {
            var result = await command(actor).ConfigureAwait(false);
            return result.Status switch
            {
                LocalAutomationCommandStatus.Conflict => OperatorUiResult<LocalAutomationCommandResult>.Failure(
                    OperatorUiResultKind.Conflict, DescribeFailure(result)),
                LocalAutomationCommandStatus.NotFound => OperatorUiResult<LocalAutomationCommandResult>.Failure(
                    OperatorUiResultKind.NotFound, DescribeFailure(result)),
                LocalAutomationCommandStatus.Invalid => OperatorUiResult<LocalAutomationCommandResult>.Failure(
                    OperatorUiResultKind.Invalid, DescribeFailure(result)),
                _ => OperatorUiResult<LocalAutomationCommandResult>.Success(result)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "CameraAgent local automation command failed.");
            return OperatorUiResult<LocalAutomationCommandResult>.Failure(
                OperatorUiResultKind.Unavailable, "The automation command could not be completed.");
        }
    }

    /// <summary>Maps a rejected automation command to fixed operator-facing guidance.</summary>
    internal static string DescribeFailure(LocalAutomationCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ReasonCode switch
        {
            LocalAutomationContract.ExpectedVersionConflictReasonCode =>
                "This automation changed since the page was read. Refresh before retrying.",
            LocalAutomationContract.IdempotencyKeyConflictReasonCode =>
                "This command identifier was already recorded with different content. Refresh before retrying.",
            LocalAutomationContract.UnknownDefinitionReasonCode =>
                "This automation no longer exists. Refresh to see the current definitions.",
            LocalAutomationContract.DefinitionLimitReasonCode =>
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"This CameraAgent already holds the maximum of {LocalAutomationContract.MaximumDefinitions} automations."),
            LocalAutomationContract.UnregisteredCombinationReasonCode =>
                "That task and trigger combination is not registered on this CameraAgent.",
            LocalAutomationContract.UnregisteredTargetReasonCode =>
                "That task target is not a registered target of the selected task.",
            _ => DescribeField(result.FieldPath)
        };
    }

    private static string DescribeField(string? fieldPath) => fieldPath switch
    {
        "definition.definitionId" =>
            "The identifier must start with a lower-case letter and use only lower-case letters, digits, "
            + "hyphens, and dots.",
        "definition.name" => string.Create(
            CultureInfo.InvariantCulture,
            $"The name must be a single line of at most {LocalAutomationContract.MaximumNameLength} characters."),
        "definition.taskTarget" => string.Create(
            CultureInfo.InvariantCulture,
            $"The target must be a single line of at most {LocalAutomationContract.MaximumTargetLength} characters."),
        "definition.triggerInterval" => string.Create(
            CultureInfo.InvariantCulture,
            $"A periodic interval must be between {LocalAutomationContract.MinimumPeriodicIntervalSeconds} and "
            + $"{LocalAutomationContract.MaximumPeriodicIntervalSeconds} seconds, and a capture-relative interval "
            + $"between {LocalAutomationContract.MinimumCaptureInterval} and "
            + $"{LocalAutomationContract.MaximumCaptureInterval} captures."),
        _ => "The automation command was rejected before anything durable changed."
    };

    private async Task<ClaimsPrincipal?> GetAuthorizedPrincipalAsync(string policy)
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var result = await authorizationService.AuthorizeAsync(state.User, resource: null, policy)
            .ConfigureAwait(false);
        return result.Succeeded ? state.User : null;
    }
}
