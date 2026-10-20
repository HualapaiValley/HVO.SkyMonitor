using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>
/// The client's view of CameraAgent's operator settings API (docs/contracts/cameraagent-operator-settings-v1.md).
/// Names that the contract enumerates (type, safety, application, source) are kept as strings, so a value this client
/// does not know yet is shown as it is rather than refused.
/// </summary>
internal static class SkySettingsContract
{
    public const string Route = SkyApiClient.OperationsRoute + "settings";

    public const string CatalogueRoute = Route + "/catalogue";

    /// <summary>The catalogue and values schema this client reads.</summary>
    public const int SchemaVersion = 1;

    public const int MaximumReasonLength = 512;

    public const string IntegerType = "Integer";
    public const string NumberType = "Number";
    public const string StringType = "String";
    public const string EnumType = "Enum";

    public const string CaptureGeometrySafety = "CaptureGeometry";
}

internal sealed record SkySettingChoice(string Value, string Label, string? Description);

internal sealed record SkySettingDescriptor(
    string Key,
    string Group,
    string Title,
    string Description,
    string Type,
    bool Nullable,
    double? Minimum,
    double? Maximum,
    int? MaximumLength,
    IReadOnlyList<SkySettingChoice>? AllowedValues,
    string? Unit,
    JsonElement? Default,
    string ReadRole,
    string WriteRole,
    string Safety,
    string Application,
    bool AppliesAfterRestart,
    bool Secret,
    string? ConfigurationKey);

internal sealed record SkySettingsGroupDescriptor(
    string Name,
    string Title,
    string Description,
    string Store,
    IReadOnlyList<SkySettingDescriptor> Settings);

internal sealed record SkySettingsCatalogue(int SchemaVersion, IReadOnlyList<SkySettingsGroupDescriptor> Groups);

internal sealed record SkySettingState(
    string Key,
    JsonElement? Value,
    bool IsSet,
    string Source,
    string? SourceDetail,
    bool CanWrite,
    string? ReadOnlyReason,
    JsonElement? PendingValue,
    string? Problem,
    IReadOnlyList<SkySettingChoice>? AllowedValues);

internal sealed record SkySettingsGroupState(
    string Group,
    string Version,
    bool CanWrite,
    string? ReadOnlyReason,
    IReadOnlyList<SkySettingState> Settings);

internal sealed record SkySettingsValues(int SchemaVersion, IReadOnlyList<SkySettingsGroupState> Groups);

internal sealed record SkySettingsUpdateRequest(string ExpectedVersion, IReadOnlyDictionary<string, JsonElement> Values, string? Reason);

internal sealed record SkySettingsWriteResponse(string Status, SkySettingsGroupState Group, IReadOnlyList<string> Recorded);
