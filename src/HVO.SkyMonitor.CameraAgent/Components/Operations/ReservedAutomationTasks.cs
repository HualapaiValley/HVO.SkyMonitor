using System.Globalization;
using HVO.SkyMonitor.CameraAgent.Common.Automation;
using HVO.SkyMonitor.CameraAgent.Common.Orbital;
using HVO.SkyMonitor.CameraAgent.Common.SpaceWeather;
using Microsoft.AspNetCore.Components;

namespace HVO.SkyMonitor.CameraAgent.Components.Operations;

/// <summary>
/// A registered task that accepts one reserved definition: one identifier, one target and one trigger, with its own
/// interval bounds. The Automations editor fixes those fields for it, suggests its default interval and offers a
/// guided setup through <c>?setup=</c>. The adapter, not this table, enforces the bounds; the page only previews them.
/// </summary>
/// <param name="Banner">Constant guided-setup markup; it never carries operator, provider or stored text.</param>
internal sealed record ReservedAutomationTask(
    LocalAutomationTaskKind Kind,
    string DefinitionId,
    string Target,
    LocalAutomationTriggerKind TriggerKind,
    int MinimumIntervalSeconds,
    int MaximumIntervalSeconds,
    int DefaultIntervalSeconds,
    string IconClass,
    string IdentifierHint,
    string IntervalHint,
    string SetupToken,
    string SetupName,
    string BannerId,
    MarkupString Banner)
{
    /// <summary>The default interval as the editor's text input holds it.</summary>
    internal string DefaultInterval => DefaultIntervalSeconds.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The reserved tasks the Automations editor knows. Any other task kind uses the generic editor.</summary>
internal static class ReservedAutomationTasks
{
    /// <summary>The orbital refresh schedule's suggested interval: four attempts a day, well above the provider floor.</summary>
    internal const string OrbitalDefaultInterval = "21600";

    internal const string OrbitalIntervalHint =
        "7,200 to 86,400 seconds; the provider allows at most one attempt every two hours";

    internal const string OrbitalIdentifierHint =
        "Reserved for the orbital refresh schedule; this task accepts only this identifier.";

    internal const string SpaceWeatherIntervalHint =
        "60 to 120 seconds. Each run checks every enabled product; the provider's own cadence decides what is fetched";

    internal const string SpaceWeatherIdentifierHint =
        "Reserved for the space-weather refresh schedule; this task accepts only this identifier.";

    private const string OrbitalBanner =
        "Orbital refresh setup: create the reserved automation with a six-hour interval (21,600 seconds; never below 7,200). If it already exists, use its existing card; setup never replaces it. After saving, optionally <a href=\"/operations/software#software-orbital-heading\">open Software &amp; Catalog for the first Refresh now</a>. Creation does not immediately contact the provider. Shared NAT or proxy users share an egress budget; use as few refreshers as needed and intervals well above two hours. <a href=\"https://github.com/HualapaiValley/HVO.SkyMonitor/blob/development/v1/docs/runbooks/orbital-elements-refresh.md#provider-policy\">Read the provider policy before enabling refresh</a>.";

    private const string SpaceWeatherBanner =
        "Space-weather refresh setup: create the reserved automation with a 120-second interval (60 to 120 seconds). If it already exists, use its existing card; setup never replaces it. Each run checks every enabled product, and the provider's own cadence decides what is fetched, so a shorter interval does not fetch more often. Creation does not immediately contact the provider. After saving, optionally <a href=\"/operations/space-weather\">open Space weather</a> to follow the first fetch.";

    internal static readonly ReservedAutomationTask Orbital = new(
        LocalAutomationTaskKind.OrbitalElementsRefresh,
        OrbitalElementsAutomationAdapter.DefinitionId,
        CelesTrakGpSource.DatasetKey,
        LocalAutomationTriggerKind.Periodic,
        OrbitalElementsAutomationAdapter.MinimumIntervalSeconds,
        OrbitalElementsAutomationAdapter.MaximumIntervalSeconds,
        int.Parse(OrbitalDefaultInterval, CultureInfo.InvariantCulture),
        "orbital",
        OrbitalIdentifierHint,
        OrbitalIntervalHint,
        "orbital",
        "Orbital elements refresh",
        "orbital-setup-guide",
        new MarkupString(OrbitalBanner));

    internal static readonly ReservedAutomationTask SpaceWeather = new(
        LocalAutomationTaskKind.SpaceWeatherRefresh,
        SpaceWeatherAutomationAdapter.DefinitionId,
        SpaceWeatherAutomationAdapter.Target,
        LocalAutomationTriggerKind.Periodic,
        SpaceWeatherAutomationAdapter.MinimumIntervalSeconds,
        SpaceWeatherAutomationAdapter.MaximumIntervalSeconds,
        SpaceWeatherAutomationAdapter.DefaultIntervalSeconds,
        "space-weather",
        SpaceWeatherIdentifierHint,
        SpaceWeatherIntervalHint,
        "space-weather",
        "Space weather refresh",
        "space-weather-setup-guide",
        new MarkupString(SpaceWeatherBanner));

    internal static IReadOnlyList<ReservedAutomationTask> All { get; } = [Orbital, SpaceWeather];

    /// <summary>The reserved task of <paramref name="kind"/>, or null for a task the generic editor configures.</summary>
    internal static ReservedAutomationTask? ForKind(LocalAutomationTaskKind kind)
        => All.FirstOrDefault(task => task.Kind == kind);

    /// <summary>The reserved task a <c>?setup=</c> token asks to set up; tokens match exactly.</summary>
    internal static ReservedAutomationTask? ForSetup(string? token)
        => token is null ? null : All.FirstOrDefault(task => string.Equals(task.SetupToken, token, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="definitionId"/> is a reserved task's identifier.</summary>
    internal static bool IsReservedIdentifier(string definitionId)
        => All.Any(task => string.Equals(task.DefinitionId, definitionId, StringComparison.Ordinal));

    /// <summary>Whether <paramref name="interval"/> is a reserved task's untouched default.</summary>
    internal static bool IsDefaultInterval(string interval)
        => All.Any(task => string.Equals(task.DefaultInterval, interval, StringComparison.Ordinal));
}
