using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather.Storage;

namespace HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;

/// <summary>The reserved periodic tick; per-product provider cadence is enforced by policy.</summary>
/// <remarks>
/// Removing this definition cannot guarantee downgrade: retained revisions and run occurrences still
/// name the appended task kind, which older binaries reject while reading automation state. Upgrade
/// forward; this adapter supplies no history rewrite or rollback. See SqliteLocalAutomationStore.ParseEnum.
/// </remarks>
public sealed class SpaceWeatherAutomationAdapter(SpaceWeatherRefreshJob job) : ILocalAutomationPeriodicTaskAdapter
{
    public const string DefinitionId = "space-weather-refresh";
    public const string Target = "swpc";
    public const int MinimumIntervalSeconds = 60;
    public const int MaximumIntervalSeconds = 120;
    public const int DefaultIntervalSeconds = 120;

    public LocalAutomationTaskKind TaskKind => LocalAutomationTaskKind.SpaceWeatherRefresh;
    public bool SupportsRunNow => true;
    public bool RunsOffSweep => true;

    public LocalAutomationTaskDescriptor Describe() => new(TaskKind,
        "Refreshes SWPC on a 60–120 second runner tick; provider cadence is enforced separately for each product.",
        [LocalAutomationTriggerKind.Periodic], [Target], true, null);

    /// <summary>Pure validation, including while disabled; no phase offset or store I/O.</summary>
    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.DefinitionId != DefinitionId) return new("space-weather.reserved-definition-id", "definition.definitionId");
        if (definition.TaskTarget != Target) return new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
        if (definition.TriggerKind != LocalAutomationTriggerKind.Periodic || definition.SourceWindow is not null)
            return new(LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.triggerKind");
        return definition.TriggerInterval is < MinimumIntervalSeconds or > MaximumIntervalSeconds
            ? new("space-weather.interval-out-of-range", "definition.triggerInterval") : null;
    }

    public ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        return job.ExecuteAsync(occurrence.RunKey,
            LocalAutomationRunKeys.IsRunNow(occurrence.RunKey) ? SwpcAttemptTrigger.Manual : SwpcAttemptTrigger.Scheduled,
            occurrence.ScheduledForUtc, cancellationToken);
    }
}
