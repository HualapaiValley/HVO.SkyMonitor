using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Astronomy;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.TimeLapses;

/// <summary>Automation commits a request quickly; the dedicated worker performs the bounded encoding later.</summary>
internal sealed class TimeLapseAutomationAdapter(IOptions<CameraAgentHostOptions> options,
    ICameraAgentConfigurationAccessor configuration, LocalAutomationWindowPlanner planner, SqliteTimeLapseStore store,
    TimeProvider clock) : ILocalAutomationWindowTaskAdapter
{
    public LocalAutomationTaskDescriptor Describe()
    {
        var preset = options.Value.TimeLapses;
        var enabled = preset.Enabled && Validator.TryValidateObject(preset, new(preset), [], true);
        return new(LocalAutomationTaskKind.TimeLapseGeneration,
            "Queues a local-only hourly or sunrise-day video with a frozen display and timing preset.",
            [LocalAutomationTriggerKind.SourceWindowClosed], enabled ? [preset.Target] : [],
            Available: enabled, UnavailableReason: enabled ? null : "Time-lapse generation requires an enabled, pinned rig preset.")
        {
            SupportedSourceWindows = [LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceWindowKind.SunriseDay]
        };
    }

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var descriptor = Describe();
        return descriptor.Available && definition.TaskKind == descriptor.TaskKind &&
            definition.TriggerKind == LocalAutomationTriggerKind.SourceWindowClosed && definition.SourceWindow?.IsValid() == true &&
            descriptor.Targets.Contains(definition.TaskTarget, StringComparer.Ordinal)
            ? null : new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
    }

    public async ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!occurrence.IsValid() || Validate(occurrence.Definition) is not null || occurrence.SourceWindow is not { } window ||
            !window.IsEligibleForFinal(clock.GetUtcNow()) || !configuration.IsConfigured)
            return new(LocalAutomationRunOutcome.Failed, "The retained video preset or settled source window is unavailable.");
        var config = await configuration.WaitForConfigurationAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(options.Value.TimeLapses.RigProfileSha256, RigProjectionContextFactory.CreateProfileHashSha256(config.Rig),
                StringComparison.OrdinalIgnoreCase))
            return new(LocalAutomationRunOutcome.Failed, "The retained video rig does not match the configured geometry.");
        var hours = window.Policy.Kind == LocalAutomationSourceWindowKind.SunriseDay
            ? planner.ResolveWindows(window.ReportingPeriod, window.Policy with { Kind = LocalAutomationSourceWindowKind.CompletedCivilHour })
            : [];
        await store.EnqueueAsync(occurrence, options.Value.TimeLapses.Freeze(config), hours, cancellationToken).ConfigureAwait(false);
        return new(LocalAutomationRunOutcome.Succeeded, "The immutable video request is queued. Generation status is shown on its observing day.");
    }
}
