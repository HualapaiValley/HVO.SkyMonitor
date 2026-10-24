using System.Text.Json;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>Routes of the operations API that <c>hvo-sky</c> uses besides the settings.</summary>
internal static class SkyRoutes
{
    public const string Summary = SkyApiClient.OperationsRoute + "summary";
    public const string Info = SkyApiClient.OperationsRoute + "info";
    public const string Restart = SkyApiClient.OperationsRoute + "restart";
    public const string RigProfiles = SkyApiClient.OperationsRoute + "rig-profiles";
    public const string RigPreview = RigProfiles + "/preview/";
    public const string RigStage = RigProfiles + "/stage";
    public const string Schedule = SkyApiClient.OperationsRoute + "schedule";
    public const string ScheduleStage = Schedule + "/stage";
    public const string ScheduleActivate = Schedule + "/activate";
    public const string ScheduleRollback = Schedule + "/rollback";
    public const string TimeData = SkyApiClient.OperationsRoute + "time-data";
}

/// <summary><c>GET /api/v1/operations/info</c>: CameraAgent's versions, process, catalog, restart support and the caller.</summary>
internal sealed record SkyInfo(
    int SchemaVersion,
    SkyApplicationInfo Application,
    SkyHostInfo Host,
    JsonElement? Catalog,
    SkyRestartInfo Restart,
    SkyCallerInfo Caller);

internal sealed record SkyApplicationInfo(string Name, string Version, string? Revision, string InformationalVersion);

internal sealed record SkyHostInfo(string Runtime, string RuntimeIdentifier, DateTimeOffset ProcessStartedUtc, double UptimeSeconds);

internal sealed record SkyRestartInfo(bool Supervised, bool Requested);

/// <summary>Who CameraAgent takes the caller to be: the owner's session, or one of the owner's API keys.</summary>
internal sealed record SkyCallerInfo(string Credential, string? ApiKeyId, string? Access);

/// <summary><c>POST /api/v1/operations/restart</c>'s answer.</summary>
internal sealed record SkyRestartResponse(string Status, double DelaySeconds);

/// <summary>Reads members of an operations document that this client shows, without failing on a missing one.</summary>
internal static class SkyJson
{
    public static JsonElement? Member(JsonElement element, params string[] path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var current = element;
        foreach (var name in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : current;
    }

    public static string? Text(JsonElement element, params string[] path)
        => Member(element, path) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static long? Integer(JsonElement element, params string[] path)
        => Member(element, path) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number) ? number : null;

    public static double? Number(JsonElement element, params string[] path)
        => Member(element, path) is { ValueKind: JsonValueKind.Number } value ? value.GetDouble() : null;

    public static bool? Boolean(JsonElement element, params string[] path)
        => Member(element, path) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value ? value.GetBoolean() : null;

    public static DateTimeOffset? Time(JsonElement element, params string[] path)
        => Member(element, path) is { ValueKind: JsonValueKind.String } value && value.TryGetDateTimeOffset(out var time) ? time : null;

    public static IReadOnlyList<JsonElement> Items(JsonElement element, params string[] path)
        => Member(element, path) is { ValueKind: JsonValueKind.Array } value ? value.EnumerateArray().ToArray() : [];
}
