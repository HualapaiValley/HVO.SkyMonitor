using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record CameraAgentAutomationWindowPreview(
    DateTimeOffset ReadAtUtc,
    LocalAutomationSourceWindow? NextWindow,
    string? UnavailableReasonCode);

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

    ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RunScriptNowAsync(LocalAutomationRunNowRequest request,
        CancellationToken cancellationToken) => ValueTask.FromResult(OperatorUiResult<LocalAutomationCommandResult>.Failure(
            OperatorUiResultKind.Unavailable, "Script run controls are unavailable."));

    ValueTask<OperatorUiResult<LocalAutomationCommandResult>> StopScriptAsync(LocalAutomationScriptStopRequest request,
        CancellationToken cancellationToken) => ValueTask.FromResult(OperatorUiResult<LocalAutomationCommandResult>.Failure(
            OperatorUiResultKind.Unavailable, "Script run controls are unavailable."));

    ValueTask<OperatorUiResult<CameraAgentAutomationWindowPreview>> PreviewWindowAsync(
        LocalAutomationSaveRequest request,
        CancellationToken cancellationToken);
}

internal sealed class CameraAgentAutomationUiService(
    AuthenticationStateProvider authenticationStateProvider,
    IAuthorizationService authorizationService,
    ILocalAutomationStore store,
    ILogger<CameraAgentAutomationUiService> logger,
    ILocalAutomationTaskRegistry registry,
    LocalAutomationWindowPlanner windowPlanner,
    TimeProvider clock, LocalAutomationScriptCommands? scriptCommands = null) : ICameraAgentAutomationUiService
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Preview failures return fixed guidance without disclosing host configuration or paths.")]
    public async ValueTask<OperatorUiResult<CameraAgentAutomationWindowPreview>> PreviewWindowAsync(
        LocalAutomationSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
                .ConfigureAwait(false) is null)
        {
            return OperatorUiResult<CameraAgentAutomationWindowPreview>.Failure(
                OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            var definition = new LocalAutomationDefinition(request.DefinitionId, request.Name, request.Enabled,
                request.TaskKind, request.TaskTarget, request.TriggerKind, request.TriggerInterval, now, request.SourceWindow);
            var descriptor = registry.Describe().FirstOrDefault(item => item.TaskKind == request.TaskKind);
            if (request.TriggerKind != LocalAutomationTriggerKind.SourceWindowClosed ||
                descriptor is not { Available: true } || request.SourceWindow is null ||
                !descriptor.SupportedSourceWindows.Contains(request.SourceWindow.Kind))
            {
                return OperatorUiResult<CameraAgentAutomationWindowPreview>.Failure(
                    OperatorUiResultKind.Invalid, "That source-window task or preset is unavailable on this CameraAgent.");
            }
            if ((LocalAutomationDefinitionValidator.Validate(definition) ?? registry.Validate(definition)) is { } invalid)
            {
                return OperatorUiResult<CameraAgentAutomationWindowPreview>.Failure(OperatorUiResultKind.Invalid,
                    invalid.FieldPath == "definition.sourceWindow.selection"
                        ? DescribeSourceSelectionMismatch(definition)
                        : DescribeFailure(new(LocalAutomationCommandStatus.Invalid, invalid.ReasonCode,
                            invalid.FieldPath, LocalAutomationOperatorState.Empty)));
            }
            // A prospective future window, never a write, claim, backfill or coverage assertion.
            var plan = windowPlanner.Resolve(new(definition, 1,
                LocalAutomationContract.ComputeRevisionSha256(definition), null, null), now);
            return OperatorUiResult<CameraAgentAutomationWindowPreview>.Success(
                new(now, plan.NextOccurrence?.SourceWindow, plan.UnavailableReasonCode));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent local automation preview failed ({FailureType}).", exception.GetType().Name);
            return OperatorUiResult<CameraAgentAutomationWindowPreview>.Failure(
                OperatorUiResultKind.Unavailable, "The source-window preview is unavailable. Try again.");
        }
    }

    private string DescribeSourceSelectionMismatch(LocalAutomationDefinition definition)
    {
        var accepted = Enum.GetValues<LocalAutomationSourceSelection>()
            .Where(selection => registry.Validate(definition with
            { SourceWindow = definition.SourceWindow! with { Selection = selection } }) is null)
            .Select(static selection => selection == LocalAutomationSourceSelection.DarkNightActualSources
                ? "Dark-night actual sources" : "All actual sources").ToArray();
        return accepted.Length == 0
            ? "No source selection is currently accepted by this task target. Choose a current producer target."
            : $"The source selection does not match this task target. Choose {string.Join(" or ", accepted)} for this producer.";
    }

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
            logger.LogWarning("CameraAgent local automation read failed ({FailureType}).", exception.GetType().Name);
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

    public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RunScriptNowAsync(
        LocalAutomationRunNowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(actor => (scriptCommands ?? throw new InvalidOperationException())
            .RunNowAsync(request with { Actor = actor }, cancellationToken), cancellationToken);
    }

    public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> StopScriptAsync(
        LocalAutomationScriptStopRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(actor => (scriptCommands ?? throw new InvalidOperationException())
            .StopAsync(request with { Actor = actor }, cancellationToken), cancellationToken);
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
        if (actor is not { Length: > 0 and <= 128 } || string.IsNullOrWhiteSpace(actor))
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
            logger.LogWarning("CameraAgent local automation command failed ({FailureType}).", exception.GetType().Name);
            return OperatorUiResult<LocalAutomationCommandResult>.Failure(
                OperatorUiResultKind.Unavailable, "The automation command could not be completed.");
        }
    }

    /// <summary>Maps a rejected automation command to fixed operator-facing guidance.</summary>
    internal static string DescribeFailure(LocalAutomationCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        // A registered task's own validation codes (the orbital refresh schedule's reserved identifier, provider floor,
        // interval ceiling and store availability) have exact operator text shared with the Software & Catalog page.
        if (result.ReasonCode is { } code && code.StartsWith("orbital.", StringComparison.Ordinal) &&
            OrbitalOperatorText.IsKnown(code))
        {
            return OrbitalOperatorText.Describe(code);
        }
        // The space-weather refresh schedule's reserved identifier and interval bounds have the same fixed text as the
        // space-weather page; any other code keeps the generic mapping below.
        if (result.ReasonCode is { } spaceWeatherCode && spaceWeatherCode.StartsWith("space-weather.", StringComparison.Ordinal) &&
            SpaceWeatherOperatorText.IsKnown(spaceWeatherCode))
        {
            return SpaceWeatherOperatorText.Describe(spaceWeatherCode);
        }
        return result.ReasonCode switch
        {
            LocalAutomationScriptCommands.ScriptOnlyReason => "This control accepts registered script tasks only.",
            LocalAutomationScriptCommands.RunNotFoundReason => "That retained script run is no longer available. Refresh run history.",
            LocalAutomationScriptCommands.StopUnavailableReason => "That script run is not currently executing here. Refresh before retrying.",
            LocalAutomationContract.RunNowPendingReasonCode => "A run-now is already queued or running. Refresh run history before queuing another.",
            LocalAutomationContract.RunNowUnavailableReasonCode => "This script registration or definition is unavailable. Refresh before retrying.",
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
        "definition.sourceWindow" =>
            "Select a supported source period and a settle allowance between 0 and 86400 seconds.",
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
