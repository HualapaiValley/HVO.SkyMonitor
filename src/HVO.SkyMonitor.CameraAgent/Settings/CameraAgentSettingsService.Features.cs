using System.Text.Json.Nodes;
using HVO.SkyMonitor.CameraAgent.Common.Configuration;

namespace HVO.SkyMonitor.CameraAgent.Settings;

internal sealed partial class CameraAgentSettingsService
{
    private async Task<CameraAgentSettingsGroupState> ReadFeatureAsync(CameraAgentSettingsGroup group,
        CancellationToken cancellationToken)
    {
        var definition = group.Settings.Single();
        var snapshot = settingsFile is null ? null : await settingsFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        var source = sources.Find(definition.ConfigurationKey!);
        var configured = sources.Boolean(definition.ConfigurationKey!);
        var effective = definition.AppliesAfterRestart ? RunningFeature(group) ?? false : configured;
        var canWrite = snapshot is { Problem: null } && !source.Overridden;
        var reason = snapshot is null ? NoSettingsFileReason
            : snapshot.Problem ?? (source.Overridden ? OverriddenReason(source.Source) : null);
        return new(group.Name, snapshot?.Version ?? UnavailableVersion, canWrite, reason,
        [
            new(definition.Key, CameraAgentSettingDefinition.ToJson(Text(effective)),
                source.Source != CameraAgentSettingSource.Default, source.Source, null, canWrite, reason,
                effective != configured ? CameraAgentSettingDefinition.ToJson(Text(configured)) : null, null, null)
        ]);
    }

    private async Task<CameraAgentSettingsWriteResult> WriteFeatureAsync(CameraAgentSettingsGroup group,
        Dictionary<string, object?> values, string expectedVersion, string actor, CancellationToken cancellationToken)
    {
        var definition = group.Settings.Single();
        var result = await settingsFile!.WriteAsync(expectedVersion,
            new Dictionary<string, JsonNode?> { [definition.ConfigurationKey!] = JsonValue.Create((string)values[definition.Key]! == "true") },
            actor, cancellationToken).ConfigureAwait(false);
        var after = await ReadGroupAsync(group, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            OperatorSettingsWriteStatus.Applied => new(definition.AppliesAfterRestart
                ? CameraAgentSettingsWriteOutcome.PendingRestart : CameraAgentSettingsWriteOutcome.Applied, after),
            OperatorSettingsWriteStatus.Unchanged => new(CameraAgentSettingsWriteOutcome.Unchanged, after),
            OperatorSettingsWriteStatus.Conflict => Conflict(after),
            _ => new(CameraAgentSettingsWriteOutcome.ReadOnly, after, after.ReadOnlyReason ?? result.Snapshot.Problem)
        };
    }

    /// <summary>The value the running host started with, for a switch that applies only after a restart.</summary>
    private bool? RunningFeature(CameraAgentSettingsGroup group) => group.Name switch
    {
        CameraAgentSettingsCatalogue.SpaceWeatherGroup => hostOptions?.Value.SpaceWeather.Enabled,
        _ => hostOptions?.Value.OrbitalElements.Enabled
    };

    private static string Text(bool value) => value ? "true" : "false";
}
