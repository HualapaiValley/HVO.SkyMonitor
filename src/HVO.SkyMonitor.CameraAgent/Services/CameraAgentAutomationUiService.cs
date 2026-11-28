using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Claims;
using HVO.SkyMonitor.CameraAgent.Authorization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Gallery;
using HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;

namespace HVO.SkyMonitor.CameraAgent.Services;

internal sealed record CameraAgentAutomationWindowPreview(
    DateTimeOffset ReadAtUtc,
    LocalAutomationSourceWindow? NextWindow,
    string? UnavailableReasonCode);

internal sealed record CameraAgentAutomationSchedulePreview(
    DateTimeOffset ReadAtUtc, DateTimeOffset? DueUtc, long? CaptureSequence);

internal sealed record CameraAgentAutomationSourceChoice(
    LocalAutomationSourceWindowKind Period, LocalAutomationSourceSelection Selection);

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

    ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentAutomationSourceChoice>>> GetSourceChoicesAsync(
        LocalAutomationSaveRequest request, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<CameraAgentAutomationSchedulePreview>> PreviewScheduleAsync(
        LocalAutomationSaveRequest request, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<IReadOnlyList<LocalAutomationSourceWindow>>> PreviewBackfillAsync(
        string definitionId, long version, DateOnly reportDate, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<LocalAutomationCommandResult>> BackfillAsync(
        LocalAutomationBackfillRequest request, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RetryAsync(
        LocalAutomationRetryRequest request, CancellationToken cancellationToken);

    ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RunNowAsync(
        LocalAutomationRunNowRequest request, CancellationToken cancellationToken);
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
    public ValueTask<OperatorUiResult<IReadOnlyList<CameraAgentAutomationSourceChoice>>> GetSourceChoicesAsync(
        LocalAutomationSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadPreviewAsync<IReadOnlyList<CameraAgentAutomationSourceChoice>>(() =>
        {
            var definition = DraftDefinition(request, ReadAt());
            var descriptor = registry.Describe().FirstOrDefault(item => item.TaskKind == request.TaskKind);
            var choices = new List<CameraAgentAutomationSourceChoice>();
            if (request.TriggerKind == LocalAutomationTriggerKind.SourceWindowClosed && descriptor is { Available: true })
            {
                foreach (var period in descriptor.SupportedSourceWindows)
                {
                    foreach (var selection in Enum.GetValues<LocalAutomationSourceSelection>())
                    {
                        var candidate = definition with
                        {
                            SourceWindow = new(
                            LocalAutomationSourceWindowPolicy.CurrentVersion, period, selection, TimeSpan.Zero)
                        };
                        if (Validate(candidate) is null)
                        {
                            choices.Add(new(period, selection));
                        }
                    }
                }
            }
            return ValueTask.FromResult(choices.Count > 0
                ? OperatorUiResult<IReadOnlyList<CameraAgentAutomationSourceChoice>>.Success(choices)
                : OperatorUiResult<IReadOnlyList<CameraAgentAutomationSourceChoice>>.Failure(
                    OperatorUiResultKind.Invalid, "No source period and selection are available for this task target."));
        }, cancellationToken);
    }

    public ValueTask<OperatorUiResult<CameraAgentAutomationSchedulePreview>> PreviewScheduleAsync(
        LocalAutomationSaveRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ReadPreviewAsync<CameraAgentAutomationSchedulePreview>(async () =>
        {
            var current = await store.ReadDefinitionForPreviewAsync(request.DefinitionId, cancellationToken).ConfigureAwait(false);
            if ((current?.Version ?? 0) != request.ExpectedVersion)
            {
                return VersionConflict<CameraAgentAutomationSchedulePreview>();
            }
            var now = ReadAt();
            var definition = DraftDefinition(request, current?.Definition.TriggerEpochUtc ?? now);
            if (definition.TriggerKind is not (LocalAutomationTriggerKind.Periodic or LocalAutomationTriggerKind.CaptureRelative) ||
                definition.TaskKind == LocalAutomationTaskKind.CustomScript)
            {
                return OperatorUiResult<CameraAgentAutomationSchedulePreview>.Failure(OperatorUiResultKind.Invalid,
                    "This task uses a different schedule preview.");
            }
            if (Validate(definition) is { } rejection)
            {
                return OperatorUiResult<CameraAgentAutomationSchedulePreview>.Failure(
                    OperatorUiResultKind.Invalid, DescribeRejection(rejection));
            }
            if (current is null)
            {
                definition = definition with { TriggerEpochUtc = registry.ResolveInitialTriggerEpoch(definition, now) };
                if (!LocalAutomationSchedule.IsInitialTriggerEpoch(definition.TriggerEpochUtc, now, definition) ||
                    Validate(definition) is not null)
                {
                    return OperatorUiResult<CameraAgentAutomationSchedulePreview>.Failure(
                        OperatorUiResultKind.Invalid, "The task's initial schedule could not be resolved.");
                }
            }
            var keepsCursor = current?.Definition is { Enabled: true } retained && retained.TriggerKind == definition.TriggerKind;
            if (definition.TriggerKind == LocalAutomationTriggerKind.CaptureRelative)
            {
                return OperatorUiResult<CameraAgentAutomationSchedulePreview>.Success(new(now, null,
                    LocalAutomationSchedule.NextCaptureSequence(definition.TriggerInterval,
                        keepsCursor ? current?.LastCaptureSequence : null)));
            }
            var cursor = keepsCursor ? current?.LastOccurrenceUtc : current is null ? null :
                LocalAutomationSchedule.LatestPeriodicBoundary(definition.TriggerEpochUtc, definition.TriggerInterval, now);
            var due = LocalAutomationSchedule.ResolvePeriodic(definition.TriggerEpochUtc, definition.TriggerInterval, cursor, now)
                ?.OccurrenceUtc ?? LocalAutomationSchedule.NextPeriodicDueUtc(definition.TriggerEpochUtc, definition.TriggerInterval, cursor);
            return OperatorUiResult<CameraAgentAutomationSchedulePreview>.Success(new(now, due, null));
        }, cancellationToken);
    }

    public ValueTask<OperatorUiResult<IReadOnlyList<LocalAutomationSourceWindow>>> PreviewBackfillAsync(
        string definitionId, long version, DateOnly reportDate, CancellationToken cancellationToken)
        => ReadPreviewAsync<IReadOnlyList<LocalAutomationSourceWindow>>(async () =>
        {
            var current = await store.ReadDefinitionForPreviewAsync(definitionId, cancellationToken).ConfigureAwait(false);
            if (current?.Version != version)
            {
                return VersionConflict<IReadOnlyList<LocalAutomationSourceWindow>>();
            }
            if (!current.Definition.Enabled || current.Definition.TaskKind == LocalAutomationTaskKind.CustomScript ||
                current.Definition.SourceWindow is not { } policy || Validate(current.Definition) is not null)
            {
                return OperatorUiResult<IReadOnlyList<LocalAutomationSourceWindow>>.Failure(
                    OperatorUiResultKind.Invalid, "This source-window automation is unavailable for backfill.");
            }
            var now = ReadAt();
            try
            {
                return OperatorUiResult<IReadOnlyList<LocalAutomationSourceWindow>>.Success(
                    [.. windowPlanner.ResolveWindows(reportDate, policy).Where(window =>
                        window.IsEligibleForFinal(now) && LocalAutomationWindowPlanner.IsWithinBackfillLookback(window.EndUtc, now))]);
            }
            catch (ReportingPeriodUnavailableException)
            {
                return OperatorUiResult<IReadOnlyList<LocalAutomationSourceWindow>>.Failure(
                    OperatorUiResultKind.Invalid, "The selected report date has no available source window.");
            }
        }, cancellationToken);

    private static OperatorUiResult<T> VersionConflict<T>() => OperatorUiResult<T>.Failure(
        OperatorUiResultKind.Conflict, "This automation changed since the page was read. Refresh before retrying.");

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Preview failures return fixed guidance without disclosing host configuration or paths.")]
    private async ValueTask<OperatorUiResult<T>> ReadPreviewAsync<T>(
        Func<ValueTask<OperatorUiResult<T>>> read, CancellationToken cancellationToken)
    {
        if (await GetAuthorizedPrincipalAsync(CameraAgentAuthorizationPolicyNames.OperationsReadV1)
                .ConfigureAwait(false) is null)
        {
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unauthorized, "Authorization is required.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("CameraAgent local automation preview failed ({FailureType}).", exception.GetType().Name);
            return OperatorUiResult<T>.Failure(OperatorUiResultKind.Unavailable, "The automation preview is unavailable. Try again.");
        }
    }

    private DateTimeOffset ReadAt() => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());

    private static LocalAutomationDefinition DraftDefinition(LocalAutomationSaveRequest request, DateTimeOffset epoch)
        => new(request.DefinitionId, request.Name, request.Enabled, request.TaskKind, request.TaskTarget,
            request.TriggerKind, request.TriggerInterval, epoch, request.SourceWindow);

    private LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
        => LocalAutomationDefinitionValidator.Validate(definition) ?? registry.Validate(definition);
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
            var now = ReadAt();
            var definition = DraftDefinition(request, now);
            var descriptor = registry.Describe().FirstOrDefault(item => item.TaskKind == request.TaskKind);
            if (request.TriggerKind != LocalAutomationTriggerKind.SourceWindowClosed ||
                descriptor is not { Available: true } || request.SourceWindow is null ||
                !descriptor.SupportedSourceWindows.Contains(request.SourceWindow.Kind))
            {
                return OperatorUiResult<CameraAgentAutomationWindowPreview>.Failure(
                    OperatorUiResultKind.Invalid, "That source-window task or preset is unavailable on this CameraAgent.");
            }
            if (Validate(definition) is { } invalid)
            {
                return OperatorUiResult<CameraAgentAutomationWindowPreview>.Failure(OperatorUiResultKind.Invalid,
                    invalid.FieldPath == SourceSelectionFieldPath
                        ? DescribeSourceSelectionMismatch(definition)
                        : invalid is { ReasonCode: LocalAutomationContract.UnregisteredCombinationReasonCode, FieldPath: SourcePeriodFieldPath }
                            ? DescribeSourcePeriodMismatch(definition)
                            : DescribeRejection(invalid));
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

    private const string SourceSelectionFieldPath = "definition.sourceWindow.selection";
    private const string SourcePeriodFieldPath = "definition.sourceWindow.kind";

    /// <summary>The source-period names the wizard offers, so a rejection names a period exactly as it was chosen.</summary>
    internal static string SourceWindowLabel(LocalAutomationSourceWindowKind kind)
        => kind == LocalAutomationSourceWindowKind.SunriseDay ? "Sunrise to next sunrise" : "Completed site-local hour";

    /// <summary>
    /// A task target that takes only some source periods names the selected period and the periods it takes. A period
    /// is taken when the registry accepts the definition with it, or then rejects only the source selection, which is
    /// a separate choice with its own message.
    /// </summary>
    private string DescribeSourcePeriodMismatch(LocalAutomationDefinition definition)
    {
        var selected = SourceWindowLabel(definition.SourceWindow!.Kind);
        var accepted = Enum.GetValues<LocalAutomationSourceWindowKind>()
            .Where(kind => kind != definition.SourceWindow.Kind && registry.Validate(definition with
            { SourceWindow = definition.SourceWindow with { Kind = kind } }) is null or { FieldPath: SourceSelectionFieldPath })
            .Select(SourceWindowLabel).ToArray();
        return accepted.Length == 0
            ? $"The source period \"{selected}\" is not accepted by this task target, and it accepts no other source period. Choose a current producer target."
            : $"The source period \"{selected}\" is not accepted by this task target. Choose \"{string.Join("\" or \"", accepted)}\" for this producer.";
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

    public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> BackfillAsync(
        LocalAutomationBackfillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(actor => OccurrenceStore.BackfillAsync(request with { Actor = actor }, cancellationToken), cancellationToken);
    }

    public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RetryAsync(
        LocalAutomationRetryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(actor => OccurrenceStore.RetryAsync(request with { Actor = actor }, cancellationToken), cancellationToken);
    }

    public ValueTask<OperatorUiResult<LocalAutomationCommandResult>> RunNowAsync(
        LocalAutomationRunNowRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteAsync(async actor =>
        {
            var owned = request with { Actor = actor };
            if (await OccurrenceStore.FindRunNowReplayAsync(owned, cancellationToken).ConfigureAwait(false) is { } replay)
            {
                return replay;
            }
            var current = await store.ReadDefinitionForPreviewAsync(request.DefinitionId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return new(LocalAutomationCommandStatus.NotFound, LocalAutomationContract.UnknownDefinitionReasonCode, null,
                    LocalAutomationOperatorState.Empty);
            }
            if (current.Definition.TaskKind != LocalAutomationTaskKind.TimeDataRefresh)
            {
                return new(LocalAutomationCommandStatus.Invalid, LocalAutomationContract.RunNowUnavailableReasonCode, null,
                    LocalAutomationOperatorState.Empty);
            }
            // The store rechecks this version atomically so a replacement cannot change the admitted task kind.
            if (current.Version != request.ExpectedVersion)
            {
                return new(LocalAutomationCommandStatus.Conflict, LocalAutomationContract.ExpectedVersionConflictReasonCode, null,
                    LocalAutomationOperatorState.Empty);
            }
            return await OccurrenceStore.RunNowAsync(owned, cancellationToken).ConfigureAwait(false);
        }, cancellationToken);
    }

    private ILocalAutomationOccurrenceStore OccurrenceStore => store as ILocalAutomationOccurrenceStore
        ?? throw new InvalidOperationException("This store does not support explicit occurrences.");

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
        return DescribeRejection(new(result.ReasonCode ?? string.Empty, result.FieldPath ?? string.Empty));
    }

    internal static string DescribeRejection(LocalAutomationRegistryRejection rejection)
    {
        // A registered task's own validation codes (the orbital refresh schedule's reserved identifier, provider floor,
        // interval ceiling and store availability) have exact operator text shared with the Software & Catalog page.
        if (rejection.ReasonCode is { } code && code.StartsWith("orbital.", StringComparison.Ordinal) &&
            OrbitalOperatorText.IsKnown(code))
        {
            return OrbitalOperatorText.Describe(code);
        }
        // The space-weather refresh schedule's reserved identifier and interval bounds have the same fixed text as the
        // space-weather page; any other code keeps the generic mapping below.
        if (rejection.ReasonCode is { } spaceWeatherCode && spaceWeatherCode.StartsWith("space-weather.", StringComparison.Ordinal) &&
            SpaceWeatherOperatorText.IsKnown(spaceWeatherCode))
        {
            return SpaceWeatherOperatorText.Describe(spaceWeatherCode);
        }
        return rejection.ReasonCode switch
        {
            LocalAutomationScriptCommands.ScriptOnlyReason => "This control accepts registered script tasks only.",
            LocalAutomationScriptCommands.RunNotFoundReason => "That retained script run is no longer available. Refresh run history.",
            LocalAutomationScriptCommands.StopUnavailableReason => "That script run is not currently executing here. Refresh before retrying.",
            LocalAutomationContract.RunNowPendingReasonCode => "A run-now is already queued or running. Refresh run history before queuing another.",
            LocalAutomationContract.RunNowUnavailableReasonCode => "This task or definition is unavailable for Run now. Refresh before retrying.",
            "automation.window-task-unavailable" => "This automation is unavailable for backfill.",
            "automation.source-window-not-found" => "That source window is no longer available for the selected report date.",
            "automation.source-window-changed" => "The source window changed after the preview. Preview the report date again before queuing.",
            "automation.finality-not-reached" => "The source window and its settle allowance must end before it can be queued.",
            "automation.backfill-outside-lookback" => "Backfill is limited to windows that closed within the last seven days.",
            "automation.run-not-found" => "That retained run is no longer available. Refresh run history.",
            "automation.run-not-retryable" => "That run cannot be retried with its retained inputs.",
            "automation.retry-limit-or-stale-attempt" => "Refresh run history. Only the latest attempt can be retried, up to three attempts per occurrence.",
            "automation.queue-capacity" => "This automation's queue is full. Refresh before retrying.",
            CameraAgentTimeDataAutomationAdapter.ReservedDefinitionIdReasonCode =>
                "The time-data refresh uses the reserved identifier time-data-refresh.",
            CameraAgentTimeDataAutomationAdapter.IntervalReasonCode => "The time-data refresh uses a 24-hour interval.",
            "restart.reserved-definition-id" => "The scheduled restart uses the reserved identifier cameraagent-restart.",
            "restart.interval-too-short" => "The scheduled restart interval must be at least 3600 seconds.",
            LocalAutomationContract.UnregisteredCombinationReasonCode when rejection.FieldPath == SourceSelectionFieldPath =>
                "The recorded source selection is no longer accepted by this task target. Edit this automation to review its choices.",
            LocalAutomationContract.UnregisteredCombinationReasonCode when rejection.FieldPath == SourcePeriodFieldPath =>
                "The recorded source period is not supported by this task. Edit this automation to choose a supported period.",
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
            _ when rejection.FieldPath == "definition.triggerInterval" &&
                   rejection.ReasonCode != LocalAutomationContract.InvalidCommandReasonCode =>
                "The interval is not accepted by this registered task. Review the interval for this task.",
            _ => DescribeField(rejection.FieldPath)
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
