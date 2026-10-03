using System.ComponentModel.DataAnnotations;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Options;
using Microsoft.Extensions.Options;

namespace HVO.SkyMonitor.CameraAgent.Common.NightlyProducts;

/// <summary>Offers real still-image presets through the delivered durable source-window runner.</summary>
internal sealed class NightlyProductAutomationTaskRegistry(
    NightlyProductGenerator generator, IOptions<CameraAgentHostOptions> options) : ILocalAutomationWindowTaskAdapter
{
    public LocalAutomationTaskDescriptor Describe()
    {
        var preset = options.Value.NightlyProducts;
        var enabled = preset.Enabled && Validator.TryValidateObject(preset, new ValidationContext(preset), [], true);
        return new(LocalAutomationTaskKind.StillImageGeneration,
            "Generates a full planned-window meridian keogram or a dark-night lighten trail from fixed-transfer sources.",
            [LocalAutomationTriggerKind.SourceWindowClosed],
            enabled ? [NightlyProductPreset.Target(NightlyProductKind.Keogram, preset),
                NightlyProductPreset.Target(NightlyProductKind.StarTrail, preset)] : [],
            Available: enabled, UnavailableReason: enabled ? null : "Still generation requires an enabled, pinned source recipe and rig.")
        {
            SupportedSourceWindows = [LocalAutomationSourceWindowKind.CompletedCivilHour, LocalAutomationSourceWindowKind.SunriseDay]
        };
    }

    public LocalAutomationRegistryRejection? Validate(LocalAutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var descriptor = Describe();
        if (!descriptor.Available || definition.TaskKind != descriptor.TaskKind ||
            !descriptor.CompatibleTriggers.Contains(definition.TriggerKind) || definition.SourceWindow is null ||
            !descriptor.Targets.Contains(definition.TaskTarget, StringComparer.Ordinal) ||
            !NightlyProductPreset.TryParseTarget(definition.TaskTarget, out var kind))
            return new(LocalAutomationContract.UnregisteredTargetReasonCode, "definition.taskTarget");
        var expected = kind == NightlyProductKind.Keogram
            ? LocalAutomationSourceSelection.AllActualSources : LocalAutomationSourceSelection.DarkNightActualSources;
        return definition.SourceWindow.Selection == expected ? null
            : new(LocalAutomationContract.UnregisteredCombinationReasonCode, "definition.sourceWindow.selection");
    }

    public async ValueTask<LocalAutomationExecution> ExecuteAsync(LocalAutomationOccurrence occurrence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!occurrence.IsValid() || Validate(occurrence.Definition) is not null)
            return new(LocalAutomationRunOutcome.Failed, "The retained still-product preset is unavailable or invalid.");
        var report = await generator.RunAsync(occurrence, cancellationToken).ConfigureAwait(false);
        return new(Outcome(report), report.Describe());
    }

    internal static LocalAutomationRunOutcome Outcome(NightlyProductRunReport report) =>
        !report.Ready || report.FailedWindows > 0 || report.PendingWindows > 0
            ? LocalAutomationRunOutcome.Failed : LocalAutomationRunOutcome.Succeeded;
}
