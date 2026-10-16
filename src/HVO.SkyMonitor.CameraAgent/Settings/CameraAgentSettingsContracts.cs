using System.Text.Json;
using System.Text.Json.Serialization;

namespace HVO.SkyMonitor.CameraAgent.Settings;

/// <summary>
/// Wire contract of the operator settings API under <c>/api/v1/operations/settings</c>: the catalogue that describes
/// each setting once, the values with where each comes from, and version-checked group writes.
/// </summary>
/// <remarks>
/// Values travel as JSON in the setting's type: integers and numbers as numbers, strings and enums as strings. A
/// nullable setting accepts null, which clears it so its default or fallback applies. No setting in the catalogue is
/// secret today; a secret setting would be write-only and never returned.
/// </remarks>
internal static class CameraAgentSettingsContract
{
    /// <summary>Base route of the settings endpoints.</summary>
    public const string Route = "/api/v1/operations/settings";

    /// <summary>The version of this contract, so a client can refuse a shape it does not understand.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The longest operator reason a write records.</summary>
    public const int MaximumReasonLength = 512;
}

/// <summary>How a setting's value is written on the wire.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentSettingType>))]
internal enum CameraAgentSettingType
{
    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A finite number.</summary>
    Number,

    /// <summary>Text, bounded by <see cref="CameraAgentSettingDescriptor.MaximumLength"/>.</summary>
    String,

    /// <summary>
    /// One of the allowed values. When the catalogue lists none, the values change with the host's state and each read
    /// of the values lists them.
    /// </summary>
    Enum
}

/// <summary>When a saved change takes effect.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentSettingApplication>))]
internal enum CameraAgentSettingApplication
{
    /// <summary>At once: the next read, projection or capture uses it.</summary>
    Immediate,

    /// <summary>At the next capture boundary: the capture in progress finishes under the previous value.</summary>
    NextCaptureBoundary,

    /// <summary>At the next CameraAgent start; until then the change is pending.</summary>
    Restart
}

/// <summary>What a change can affect, so a client can say so before it is made.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentSettingSafety>))]
internal enum CameraAgentSettingSafety
{
    /// <summary>Labels only: nothing about capture, geometry or LogicHost registration reads it.</summary>
    Descriptive,

    /// <summary>Changes what future captures record. Captures already taken never change.</summary>
    CaptureOutput,

    /// <summary>Changes the location and time basis future captures are stamped with and projected from.</summary>
    CaptureGeometry
}

/// <summary>The access a caller needs, mapped onto the operations authorization policies.</summary>
/// <remarks>
/// The signed-in site owner holds both. An API key with <c>Read</c> access holds <see cref="OperationsRead"/>; one
/// with <c>ReadWrite</c> access holds both.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentSettingRole>))]
internal enum CameraAgentSettingRole
{
    OperationsRead,
    OperationsMutate
}

/// <summary>Where a setting's value in effect comes from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentSettingSource>))]
internal enum CameraAgentSettingSource
{
    /// <summary>The operator settings file, <c>appsettings.local.json</c>.</summary>
    SettingsFile,

    /// <summary>An environment variable, which takes precedence over the settings file.</summary>
    Environment,

    /// <summary>The installer's key-per-file secrets directory, which takes precedence over the settings file.</summary>
    SecretsDirectory,

    /// <summary>The command line, which takes precedence over the settings file.</summary>
    CommandLine,

    /// <summary>The host's own <c>appsettings*.json</c> files in the image.</summary>
    AppSettings,

    /// <summary>Another host configuration source.</summary>
    Host,

    /// <summary>The value the installation configured, used until the operator sets one.</summary>
    Installation,

    /// <summary>No source sets it, so the catalogue default applies.</summary>
    Default,

    /// <summary>The active, versioned deployment location.</summary>
    DeploymentLocation,

    /// <summary>The active rig revision.</summary>
    RigRevision,

    /// <summary>The active capture schedule revision.</summary>
    ScheduleRevision,

    /// <summary>The value could not be read.</summary>
    Unavailable
}

/// <summary>One allowed value of an enum setting.</summary>
internal sealed record CameraAgentSettingChoice(string Value, string Label, string Description);

/// <summary>One setting in <c>GET settings/catalogue</c>.</summary>
/// <param name="Key">The stable setting key, for example <c>skyMap.maximumObjects</c>.</param>
/// <param name="Group">The settings group, which is also the unit of one write.</param>
/// <param name="Minimum">Smallest allowed value of a number.</param>
/// <param name="Maximum">Largest allowed value of a number.</param>
/// <param name="MaximumLength">Longest allowed text.</param>
/// <param name="AllowedValues">An enum's values, or null when each read of the values lists them.</param>
/// <param name="Default">The value in effect when nothing sets it, or null when there is none.</param>
/// <param name="ReadRole">The access needed to read it.</param>
/// <param name="WriteRole">The access needed to change it.</param>
/// <param name="AppliesAfterRestart">True when a change takes effect only after CameraAgent restarts.</param>
/// <param name="Secret">True for a write-only value that is never returned.</param>
/// <param name="ConfigurationKey">
/// The host configuration key, for a setting kept in the settings file; an environment variable or secrets file with
/// this key takes precedence and makes the setting read-only here.
/// </param>
internal sealed record CameraAgentSettingDescriptor(
    string Key,
    string Group,
    string Title,
    string Description,
    CameraAgentSettingType Type,
    bool Nullable,
    double? Minimum,
    double? Maximum,
    int? MaximumLength,
    IReadOnlyList<CameraAgentSettingChoice>? AllowedValues,
    string? Unit,
    JsonElement? Default,
    CameraAgentSettingRole ReadRole,
    CameraAgentSettingRole WriteRole,
    CameraAgentSettingSafety Safety,
    CameraAgentSettingApplication Application,
    bool AppliesAfterRestart,
    bool Secret,
    string? ConfigurationKey);

/// <summary>One settings group in <c>GET settings/catalogue</c>.</summary>
/// <param name="Store">Where the group's values are kept: <c>settingsFile</c>, <c>deploymentLocation</c>,
/// <c>rigRevision</c> or <c>scheduleRevision</c>.</param>
internal sealed record CameraAgentSettingsGroupDescriptor(
    string Name,
    string Title,
    string Description,
    string Store,
    IReadOnlyList<CameraAgentSettingDescriptor> Settings);

/// <summary>Body of <c>GET settings/catalogue</c>.</summary>
internal sealed record CameraAgentSettingsCatalogueResponse(
    int SchemaVersion,
    IReadOnlyList<CameraAgentSettingsGroupDescriptor> Groups);

/// <summary>One setting's current state in <c>GET settings</c>.</summary>
/// <param name="Value">The value in effect; always null for a secret.</param>
/// <param name="IsSet">True when a source sets the value rather than the default applying.</param>
/// <param name="Source">Where the value in effect comes from.</param>
/// <param name="SourceDetail">A short description of that source, for example the revision it is in.</param>
/// <param name="CanWrite">True when the setting can be changed through the settings API now.</param>
/// <param name="ReadOnlyReason">Why it cannot, when <paramref name="CanWrite"/> is false.</param>
/// <param name="PendingValue">A saved value that is not in effect yet, such as one awaiting a restart.</param>
/// <param name="Problem">Why a stored value cannot be used, when it cannot.</param>
/// <param name="AllowedValues">The current values of an enum whose catalogue entry lists none.</param>
internal sealed record CameraAgentSettingState(
    string Key,
    JsonElement? Value,
    bool IsSet,
    CameraAgentSettingSource Source,
    string? SourceDetail,
    bool CanWrite,
    string? ReadOnlyReason,
    JsonElement? PendingValue,
    string? Problem,
    IReadOnlyList<CameraAgentSettingChoice>? AllowedValues);

/// <summary>One group's state in <c>GET settings</c>.</summary>
/// <param name="Version">The group's version; send it back as <c>expectedVersion</c> to change the group.</param>
/// <param name="CanWrite">True when the group can be changed through the settings API now.</param>
/// <param name="ReadOnlyReason">Why it cannot, when <paramref name="CanWrite"/> is false.</param>
internal sealed record CameraAgentSettingsGroupState(
    string Group,
    string Version,
    bool CanWrite,
    string? ReadOnlyReason,
    IReadOnlyList<CameraAgentSettingState> Settings);

/// <summary>Body of <c>GET settings</c>.</summary>
internal sealed record CameraAgentSettingsResponse(
    int SchemaVersion,
    IReadOnlyList<CameraAgentSettingsGroupState> Groups);

/// <summary>
/// Body of <c>PUT settings/{group}</c>: the group settings to change, keyed by setting key. Settings left out keep
/// their value.
/// </summary>
internal sealed record CameraAgentSettingsUpdateRequest(
    string? ExpectedVersion,
    Dictionary<string, JsonElement>? Values,
    string? Reason = null);

/// <summary>What a write did.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CameraAgentSettingsWriteStatus>))]
internal enum CameraAgentSettingsWriteStatus
{
    /// <summary>The change was saved and is in effect.</summary>
    Applied,

    /// <summary>The values already matched; nothing was written.</summary>
    Unchanged,

    /// <summary>The change was saved and takes effect at the next CameraAgent start.</summary>
    PendingRestart
}

/// <summary>Body of a successful <c>PUT settings/{group}</c>.</summary>
/// <param name="Recorded">What durable records the write made, in order, for a write that takes several steps.</param>
internal sealed record CameraAgentSettingsWriteResponse(
    CameraAgentSettingsWriteStatus Status,
    CameraAgentSettingsGroupState Group,
    IReadOnlyList<string> Recorded);
