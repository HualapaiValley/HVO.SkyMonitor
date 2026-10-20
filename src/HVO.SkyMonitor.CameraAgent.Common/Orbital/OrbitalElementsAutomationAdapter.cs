using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Common.Orbital;

/// <summary>
/// The periodic automation task that refreshes the CameraAgent-local orbital snapshot. The durable schedule is the
/// single reserved definition <see cref="DefinitionId"/>; the automation runner owns its cadence, run journal, run-now
/// and restart recovery, and this adapter only routes an occurrence to <see cref="OrbitalElementsRefreshJob"/>.
/// </summary>
/// <remarks>
/// Saving the definition while refresh is disabled is allowed so an operator can prepare it: validation never rejects
/// because the feature is off, since a failing validation at claim time drops the run without a visible record.
/// Execution then skips with <c>orbital.disabled</c>.
/// </remarks>
public sealed class OrbitalElementsAutomationAdapter : ILocalAutomationPeriodicTaskAdapter
{
    /// <summary>The only definition identity this task accepts.</summary>
    public const string DefinitionId = "orbital-elements-refresh";

    /// <summary>The provider floor expressed as the shortest accepted periodic interval, in seconds.</summary>
    public const int MinimumIntervalSeconds = 7_200;

    /// <summary>The longest accepted periodic interval, in seconds.</summary>
    public const int MaximumIntervalSeconds = LocalAutomationContract.MaximumPeriodicIntervalSeconds;

    public const string ReservedDefinitionIdReasonCode = "orbital.reserved-definition-id";
    public const string IntervalBelowFloorReasonCode = "orbital.interval-below-provider-floor";
    public const string IntervalOutOfRangeReasonCode = "orbital.interval-out-of-range";

    private static readonly LocalAutomationTaskDescriptor Descriptor = new(
        LocalAutomationTaskKind.OrbitalElementsRefresh,
        "Refreshes the local CelesTrak GP active-group orbital snapshot within the provider policy.",
        [LocalAutomationTriggerKind.Periodic],
        [CelesTrakGpSource.DatasetKey],
        Available: true,
        UnavailableReason: null);

    private readonly OrbitalElementsAvailability _availability;
    private readonly OrbitalElementsRefreshJob _job;

    public OrbitalElementsAutomationAdapter(OrbitalElementsAvailability availability, OrbitalElementsRefreshJob job)
    {
        ArgumentNullException.ThrowIfNull(availability);
        ArgumentNullException.ThrowIfNull(job);
        _availability = availability;
        _job = job;
    }

    public LocalAutomationTaskKind TaskKind => LocalAutomationTaskKind.OrbitalElementsRefresh;

    public bool SupportsRunNow => true;

    public LocalAutomationTaskDescriptor Describe() => Descriptor;

    /// <summary>Fast and I/O-free: it is called inside the automation store's write transaction.</summary>
    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!string.Equals(definition.DefinitionId, DefinitionId, StringComparison.Ordinal))
        {
            return new(ReservedDefinitionIdReasonCode, "definition.definitionId");
        }
        if (!string.Equals(definition.TaskTarget, CelesTrakGpSource.DatasetKey, StringComparison.Ordinal))
        {
            return new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
        }
        if (definition.TriggerKind != LocalAutomationTriggerKind.Periodic || definition.SourceWindow is not null)
        {
            return new(LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        }
        if (definition.TriggerInterval < MinimumIntervalSeconds)
        {
            return new(IntervalBelowFloorReasonCode, "definition.triggerInterval");
        }
        if (definition.TriggerInterval > MaximumIntervalSeconds)
        {
            return new(IntervalOutOfRangeReasonCode, "definition.triggerInterval");
        }
        return _availability.IsAvailable
            ? null
            : new(OrbitalElementsAvailability.StoreUnavailableReasonCode, "definition.taskKind");
    }

    /// <summary>
    /// The first epoch is now plus the installation's persisted phase offset in [0, 30 min), so independent
    /// CameraAgents do not converge on the provider at the same boundary. It is read from memory only. If the offset
    /// is not available (the store was disposed in a race), the minimum instant is returned so the automation store
    /// rejects the save with its invalid-epoch reason rather than this hook throwing inside its transaction.
    /// </summary>
    public DateTimeOffset ResolveInitialTriggerEpoch(DateTimeOffset nowUtc)
    {
        if (!_availability.TryGetPhaseOffset(out var offset))
        {
            return DateTimeOffset.MinValue;
        }
        var epoch = nowUtc.ToUniversalTime() + offset;
        return new DateTimeOffset(epoch.UtcTicks - (epoch.UtcTicks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(
        LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        var trigger = LocalAutomationRunKeys.IsRunNow(occurrence.RunKey)
            ? OrbitalAttemptTrigger.Manual
            : OrbitalAttemptTrigger.Scheduled;
        return _job.ExecuteAsync(occurrence.RunKey, trigger, occurrence.ScheduledForUtc, cancellationToken);
    }
}
