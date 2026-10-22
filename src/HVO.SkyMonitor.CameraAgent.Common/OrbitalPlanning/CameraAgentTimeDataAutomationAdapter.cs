using HVO.SkyMonitor.CameraAgent.Common.Automation;

namespace HVO.SkyMonitor.CameraAgent.Common.OrbitalPlanning;

/// <summary>
/// The reserved daily automation that runs <see cref="CameraAgentTimeDataRefreshJob"/>. The automation runner owns its
/// cadence, run journal, run-now and restart recovery; the job enforces the switch, single flight and the durable
/// one-attempt-a-day floor, so run-now cannot add attempts.
/// </summary>
/// <remarks>
/// Saving the definition while the switch is off is allowed so an operator can prepare it; execution then skips with
/// <c>time-data.disabled</c>. Like the other appended task kinds, retained revisions and runs name
/// <see cref="LocalAutomationTaskKind.TimeDataRefresh"/>, which older binaries cannot read: upgrade forward.
/// </remarks>
public sealed class CameraAgentTimeDataAutomationAdapter(CameraAgentTimeDataRefreshJob job) : ILocalAutomationPeriodicTaskAdapter
{
    public const string DefinitionId = "time-data-refresh";
    public const string Target = "iers-usno";

    /// <summary>Exactly one day, which is also the longest periodic interval the runner accepts.</summary>
    public const int IntervalSeconds = LocalAutomationContract.MaximumPeriodicIntervalSeconds;

    public const string ReservedDefinitionIdReasonCode = "time-data.reserved-definition-id";
    public const string IntervalReasonCode = "time-data.interval-not-daily";

    public LocalAutomationTaskKind TaskKind => LocalAutomationTaskKind.TimeDataRefresh;
    public bool SupportsRunNow => true;
    public bool RunsOffSweep => true;

    public LocalAutomationTaskDescriptor Describe() => new(TaskKind,
        "Refreshes the managed Earth-orientation and leap-second pair from IERS/USNO at most once a day.",
        [LocalAutomationTriggerKind.Periodic], [Target], true, null);

    /// <summary>Pure validation, including while the switch is off; no store I/O.</summary>
    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.DefinitionId != DefinitionId) return new(ReservedDefinitionIdReasonCode, "definition.definitionId");
        if (definition.TaskTarget != Target) return new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
        if (definition.TriggerKind != LocalAutomationTriggerKind.Periodic || definition.SourceWindow is not null)
            return new(LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        return definition.TriggerInterval == IntervalSeconds ? null : new(IntervalReasonCode, "definition.triggerInterval");
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return job.ExecuteAsync(LocalAutomationRunKeys.IsRunNow(occurrence.RunKey), cancellationToken);
    }
}
