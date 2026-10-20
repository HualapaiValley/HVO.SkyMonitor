using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>The disposition of one operator orbital-elements command.</summary>
public enum OrbitalElementsCommandStatus
{
    /// <summary>A refresh was queued as one run-now occurrence of the reserved automation definition.</summary>
    Accepted,

    /// <summary>The command changed durable state, such as clearing a provider halt.</summary>
    Applied,

    /// <summary>The same idempotency key and payload were already recorded; nothing new was written.</summary>
    Replayed,

    /// <summary>The command conflicts with configuration, provider policy or durable state.</summary>
    Conflict,

    /// <summary>The command failed validation and nothing durable changed.</summary>
    Invalid,

    /// <summary>The orbital store is not available.</summary>
    Unavailable
}

/// <summary>An operator request to refresh now. It never bypasses the provider halt or the two-hour floor.</summary>
public sealed record OrbitalElementsRefreshRequest(string IdempotencyKey, string Actor, string Reason);

/// <summary>
/// The result of one operator command: a stable reason code, the next permitted provider attempt when that is what
/// blocked it, and the refresh read model after the command when the store could be read.
/// </summary>
public sealed record OrbitalElementsCommandResult(
    OrbitalElementsCommandStatus Status,
    string ReasonCode,
    DateTimeOffset? NextPermittedAttemptUtc,
    OrbitalRefreshStatus? Refresh);

/// <summary>
/// Operator status, refresh and resume for CameraAgent-local orbital elements. A refresh is never executed here: it is
/// queued as one run-now occurrence of the reserved automation definition, so the automation runner remains the only
/// execution authority and the store remains the only provider-policy authority.
/// </summary>
public sealed partial class OrbitalElementsOperations
{
    public const string DisabledReasonCode = "orbital.disabled";
    public const string DefinitionMissingReasonCode = "orbital.definition-missing";
    public const string DefinitionDisabledReasonCode = "orbital.definition-disabled";
    public const string ProviderHaltedReasonCode = "orbital.provider-halted";
    public const string NotYetPermittedReasonCode = "orbital.not-yet-permitted";
    public const string RefreshQueuedReasonCode = "orbital.refresh-queued";
    public const string RefreshReplayedReasonCode = "orbital.refresh-replayed";
    public const string InvalidIdempotencyKeyReasonCode = "orbital.refresh-invalid-idempotency-key";
    public const string InvalidActorReasonCode = "orbital.refresh-invalid-actor";
    public const string InvalidReasonReasonCode = "orbital.refresh-invalid-reason";
    public const string AttemptUnsettledReasonCode = "orbital.attempt-unsettled";
    public const string StorageFailedReasonCode = "orbital.storage-failed";
    public const string CandidateQuarantinedReasonCode = "orbital.candidate-quarantined";
    public const string NoActiveSnapshotReasonCode = "orbital.no-active-snapshot";
    public const string ReadyReasonCode = "orbital.ready";

    private readonly IOrbitalElementsStore _store;
    private readonly OrbitalElementsAvailability _availability;
    private readonly OrbitalElementsRefreshJob _job;
    private readonly ILocalAutomationStore _automation;
    private readonly IOptions<CameraAgentHostOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OrbitalElementsOperations> _logger;

    public OrbitalElementsOperations(
        IOrbitalElementsStore store,
        OrbitalElementsAvailability availability,
        OrbitalElementsRefreshJob job,
        ILocalAutomationStore automation,
        IOptions<CameraAgentHostOptions> options,
        TimeProvider timeProvider,
        ILogger<OrbitalElementsOperations> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _availability = availability;
        _job = job;
        _automation = automation;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// The validated refresh read model. An unavailable store is a reported state (<see cref="OrbitalRefreshRecoveryAction.RepairStorage"/>),
    /// not an exception; a failure while reading an available store propagates to the caller.
    /// </summary>
    public async ValueTask<OrbitalRefreshStatus> GetStatusAsync(CancellationToken cancellationToken)
        => (await ReadAsync(cancellationToken).ConfigureAwait(false)).Status;

    /// <summary>The refresh read model plus the facts health evaluation needs and the read model does not carry.</summary>
    internal async ValueTask<OrbitalElementsObservation> ReadAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var enabled = _options.Value.OrbitalElements.Enabled;
        var availability = await _availability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (availability != OrbitalElementsAvailabilityState.Available)
        {
            return new OrbitalElementsObservation(availability, Unavailable(now, enabled), null, null, false);
        }
        var before = _job.ActivityGeneration;
        var state = await _store.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var unsettled = state.LastAttemptOutcome == OrbitalAttemptOutcome.InFlight &&
            OrbitalElementsRefreshJob.IsIdleAcross(before, _job.ActivityGeneration);
        var automation = await _automation.GetStateAsync(cancellationToken).ConfigureAwait(false);
        var definition = FindDefinition(automation);
        var (reasonCode, action) = Classify(now, enabled, state, definition, unsettled);
        var status = new OrbitalRefreshStatus(
            OrbitalRefreshStatus.CurrentVersion,
            now,
            state.Revision,
            enabled,
            state.CurrentManifestSha256,
            state.CurrentPayloadSha256,
            state.LastAttemptUtc,
            state.LastRetrievalSuccessUtc,
            state.LastActivationUtc,
            enabled && definition is { Definition.Enabled: true } ? definition.NextRunUtc : null,
            state.NextPermittedAttemptUtc,
            definition is null
                ? null
                : new OrbitalRefreshAutomationReference(definition.Definition.DefinitionId, definition.RevisionSha256,
                    definition.LastRun?.RunKey),
            state.Halt,
            state.Quarantine,
            reasonCode,
            action);
        status.Validate();
        return new OrbitalElementsObservation(availability, status, state, definition, unsettled);
    }

    /// <summary>
    /// Queues one run-now occurrence of the reserved definition. A recorded idempotency key is answered first, as a
    /// replay of the same request or a conflict with a different one, so a client retry of a request that has already
    /// executed is never refused by the floor or halt its own run created. Otherwise it checks configuration, the store,
    /// the definition, the provider halt and the attempt floor, in that order. The store re-applies the halt and the
    /// floor when the runner claims the occurrence, so a race between this check and execution can never cause an early
    /// request, and a replay never queues anything.
    /// </summary>
    public async ValueTask<OrbitalElementsCommandResult> RefreshAsync(
        OrbitalElementsRefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ValidateRefresh(request) is { } invalid)
        {
            return Rejected(OrbitalElementsCommandStatus.Invalid, invalid, null, null);
        }
        if (await FindRecordedRefreshAsync(request, cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            return recorded;
        }
        if (!_options.Value.OrbitalElements.Enabled)
        {
            return Rejected(OrbitalElementsCommandStatus.Conflict, DisabledReasonCode, null, null);
        }
        var observation = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (observation.State is not { } state)
        {
            return Rejected(OrbitalElementsCommandStatus.Unavailable, observation.Status.Reason, null, observation.Status);
        }
        if (observation.Definition is not { } definition)
        {
            return Rejected(OrbitalElementsCommandStatus.Conflict, DefinitionMissingReasonCode, null, observation.Status);
        }
        if (!definition.Definition.Enabled)
        {
            return Rejected(OrbitalElementsCommandStatus.Conflict, DefinitionDisabledReasonCode, null, observation.Status);
        }
        if (state.Halt is not null)
        {
            return Rejected(OrbitalElementsCommandStatus.Conflict, ProviderHaltedReasonCode, null, observation.Status);
        }
        if (state.NextPermittedAttemptUtc is { } next && observation.Status.ObservedUtc < next)
        {
            return Rejected(OrbitalElementsCommandStatus.Conflict, NotYetPermittedReasonCode, next, observation.Status);
        }
        if (_automation is not ILocalAutomationOccurrenceStore occurrences)
        {
            return Rejected(OrbitalElementsCommandStatus.Unavailable, LocalAutomationContract.RunNowUnavailableReasonCode,
                null, observation.Status);
        }
        var queued = await occurrences.RunNowAsync(RunNowRequest(definition, request), cancellationToken)
            .ConfigureAwait(false);
        var (status, reasonCode) = queued.Status switch
        {
            LocalAutomationCommandStatus.Applied => (OrbitalElementsCommandStatus.Accepted, RefreshQueuedReasonCode),
            LocalAutomationCommandStatus.Replayed or LocalAutomationCommandStatus.Unchanged =>
                (OrbitalElementsCommandStatus.Replayed, RefreshReplayedReasonCode),
            LocalAutomationCommandStatus.NotFound => (OrbitalElementsCommandStatus.Conflict, DefinitionMissingReasonCode),
            LocalAutomationCommandStatus.Invalid => (OrbitalElementsCommandStatus.Invalid,
                queued.ReasonCode ?? LocalAutomationContract.InvalidCommandReasonCode),
            _ => (OrbitalElementsCommandStatus.Conflict, queued.ReasonCode ?? LocalAutomationContract.RunNowUnavailableReasonCode)
        };
        if (status == OrbitalElementsCommandStatus.Accepted)
        {
            RefreshQueued(_logger);
        }
        else if (status != OrbitalElementsCommandStatus.Replayed)
        {
            RefreshRejected(_logger, reasonCode);
        }
        return new OrbitalElementsCommandResult(status, reasonCode, null,
            (await ReadAsync(cancellationToken).ConfigureAwait(false)).Status);
    }

    /// <summary>
    /// The answer for an idempotency key the automation store already recorded, or null when it has none. The key's
    /// recorded identity includes the reserved definition's version, so without a definition there is nothing to match.
    /// This reads only the automation store; the orbital store is read afterwards for the result's read model.
    /// </summary>
    private async ValueTask<OrbitalElementsCommandResult?> FindRecordedRefreshAsync(
        OrbitalElementsRefreshRequest request,
        CancellationToken cancellationToken)
    {
        if (_automation is not ILocalAutomationOccurrenceStore occurrences ||
            FindDefinition(await _automation.GetStateAsync(cancellationToken).ConfigureAwait(false)) is not { } definition ||
            await occurrences.FindRunNowReplayAsync(RunNowRequest(definition, request), cancellationToken)
                .ConfigureAwait(false) is not { } recorded)
        {
            return null;
        }
        var (status, reasonCode) = recorded.Status == LocalAutomationCommandStatus.Replayed
            ? (OrbitalElementsCommandStatus.Replayed, RefreshReplayedReasonCode)
            : (OrbitalElementsCommandStatus.Conflict,
                recorded.ReasonCode ?? LocalAutomationContract.IdempotencyKeyConflictReasonCode);
        if (status == OrbitalElementsCommandStatus.Conflict)
        {
            RefreshRejected(_logger, reasonCode);
        }
        return new OrbitalElementsCommandResult(status, reasonCode, null,
            (await ReadAsync(cancellationToken).ConfigureAwait(false)).Status);
    }

    private static LocalAutomationRunNowRequest RunNowRequest(LocalAutomationDefinitionState definition,
        OrbitalElementsRefreshRequest request)
        => new(definition.Definition.DefinitionId, definition.Version, request.IdempotencyKey, request.Actor,
            request.Reason);

    /// <summary>
    /// Clears a provider halt through the store's idempotent resume command. Resume never moves the attempt floor, and it
    /// is permitted while refresh is disabled so an operator can clear a halt before re-enabling. An applied resume
    /// publishes the cleared state to the gauges through the refresh job.
    /// </summary>
    public async ValueTask<OrbitalElementsCommandResult> ResumeAsync(
        OrbitalResumeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var availability = await _availability.EnsureAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (availability != OrbitalElementsAvailabilityState.Available)
        {
            var unavailable = Unavailable(_timeProvider.GetUtcNow(), _options.Value.OrbitalElements.Enabled);
            ResumeRejected(_logger, unavailable.Reason);
            return new OrbitalElementsCommandResult(OrbitalElementsCommandStatus.Unavailable, unavailable.Reason, null,
                unavailable);
        }
        var result = await _store.ResumeProviderAsync(request, cancellationToken).ConfigureAwait(false);
        var status = result.Status switch
        {
            OrbitalCommandStatus.Applied => OrbitalElementsCommandStatus.Applied,
            OrbitalCommandStatus.Unchanged => OrbitalElementsCommandStatus.Replayed,
            OrbitalCommandStatus.Invalid => OrbitalElementsCommandStatus.Invalid,
            _ => OrbitalElementsCommandStatus.Conflict
        };
        var reasonCode = result.ReasonCode ?? "orbital.resume-rejected";
        if (status is OrbitalElementsCommandStatus.Conflict or OrbitalElementsCommandStatus.Invalid)
        {
            ResumeRejected(_logger, reasonCode);
        }
        else if (status == OrbitalElementsCommandStatus.Applied)
        {
            // The halt gauge reads 0 now rather than after the next attempt.
            await _job.PublishStateAsync(cancellationToken).ConfigureAwait(false);
        }
        return new OrbitalElementsCommandResult(status, reasonCode, null,
            (await ReadAsync(cancellationToken).ConfigureAwait(false)).Status);
    }

    /// <summary>
    /// Maps durable state to one stable reason and the single recovery action an operator may take, in precedence
    /// order: configuration, halt, quarantine, storage, the attempt floor after a failure, then none.
    /// </summary>
    internal static (string ReasonCode, OrbitalRefreshRecoveryAction Action) Classify(
        DateTimeOffset now,
        bool enabled,
        OrbitalProviderState state,
        LocalAutomationDefinitionState? definition,
        bool unsettled)
    {
        if (!enabled)
        {
            return (DisabledReasonCode, OrbitalRefreshRecoveryAction.Configure);
        }
        if (definition is null)
        {
            return (DefinitionMissingReasonCode, OrbitalRefreshRecoveryAction.Configure);
        }
        if (!definition.Definition.Enabled)
        {
            return (DefinitionDisabledReasonCode, OrbitalRefreshRecoveryAction.Configure);
        }
        if (state.Halt is not null)
        {
            return (ProviderHaltedReasonCode, OrbitalRefreshRecoveryAction.ResumeProvider);
        }
        if (state.LastAttemptOutcome == OrbitalAttemptOutcome.Quarantined)
        {
            return (state.LastAttemptReasonCode ?? CandidateQuarantinedReasonCode, OrbitalRefreshRecoveryAction.InspectQuarantine);
        }
        if (unsettled)
        {
            return (AttemptUnsettledReasonCode, OrbitalRefreshRecoveryAction.RepairStorage);
        }
        if (state.LastAttemptOutcome == OrbitalAttemptOutcome.StorageFailed)
        {
            return (state.LastAttemptReasonCode ?? StorageFailedReasonCode, OrbitalRefreshRecoveryAction.RepairStorage);
        }
        if (state.NextPermittedAttemptUtc is { } next && now < next &&
            state.LastAttemptOutcome is not (OrbitalAttemptOutcome.Activated or OrbitalAttemptOutcome.UnchangedPayload))
        {
            return (NotYetPermittedReasonCode, OrbitalRefreshRecoveryAction.WaitForPermittedAttempt);
        }
        return state.CurrentManifestSha256 is null
            ? (NoActiveSnapshotReasonCode, OrbitalRefreshRecoveryAction.None)
            : (ReadyReasonCode, OrbitalRefreshRecoveryAction.None);
    }

    private OrbitalRefreshStatus Unavailable(DateTimeOffset now, bool enabled)
    {
        var reason = _availability.ReasonCode ?? OrbitalElementsAvailability.StoreUnavailableReasonCode;
        var status = new OrbitalRefreshStatus(OrbitalRefreshStatus.CurrentVersion, now, 0, enabled, null, null, null,
            null, null, null, null, null, null, null, reason, OrbitalRefreshRecoveryAction.RepairStorage);
        status.Validate();
        return status;
    }

    private static LocalAutomationDefinitionState? FindDefinition(LocalAutomationOperatorState automation)
        => automation.Definitions.SingleOrDefault(static item =>
            string.Equals(item.Definition.DefinitionId, OrbitalElementsAutomationAdapter.DefinitionId, StringComparison.Ordinal) &&
            item.Definition.TaskKind == LocalAutomationTaskKind.OrbitalElementsRefresh);

    private static string? ValidateRefresh(OrbitalElementsRefreshRequest request)
    {
        if (!LocalAutomationDefinitionValidator.IsText(request.IdempotencyKey, LocalAutomationContract.MaximumIdempotencyKeyLength))
        {
            return InvalidIdempotencyKeyReasonCode;
        }
        if (!LocalAutomationDefinitionValidator.IsText(request.Actor, LocalAutomationContract.MaximumActorLength))
        {
            return InvalidActorReasonCode;
        }
        return LocalAutomationDefinitionValidator.IsText(request.Reason, LocalAutomationContract.MaximumReasonLength)
            ? null
            : InvalidReasonReasonCode;
    }

    private OrbitalElementsCommandResult Rejected(OrbitalElementsCommandStatus status, string reasonCode,
        DateTimeOffset? nextPermittedAttemptUtc, OrbitalRefreshStatus? refresh)
    {
        RefreshRejected(_logger, reasonCode);
        return new OrbitalElementsCommandResult(status, reasonCode, nextPermittedAttemptUtc, refresh);
    }

    [LoggerMessage(EventId = 7755, Level = LogLevel.Information,
        Message = "Orbital elements refresh was queued as a run-now occurrence by an operator.")]
    private static partial void RefreshQueued(ILogger logger);

    [LoggerMessage(EventId = 7756, Level = LogLevel.Information,
        Message = "Orbital elements refresh request was not queued: {ReasonCode}.")]
    private static partial void RefreshRejected(ILogger logger, string reasonCode);

    [LoggerMessage(EventId = 7757, Level = LogLevel.Information,
        Message = "Orbital elements provider resume was not applied: {ReasonCode}.")]
    private static partial void ResumeRejected(ILogger logger, string reasonCode);
}

/// <summary>One consistent read of availability, the refresh read model, and the durable facts behind it.</summary>
internal sealed record OrbitalElementsObservation(
    OrbitalElementsAvailabilityState Availability,
    OrbitalRefreshStatus Status,
    OrbitalProviderState? State,
    LocalAutomationDefinitionState? Definition,
    bool Unsettled);
