using System.Text.Json;
using Microsoft.Data.Sqlite;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Every concrete CameraAgent SQLite store an instance has, relative to its state root, found without reading bulk data.
/// </summary>
/// <remarks>
/// The fixed stores live at fixed places (<see cref="CameraAgentStateLayout.SqliteStores"/>). The artifact outbox lives
/// in every storage root a FileStorage step writes to, and those roots come from configuration: the deployment
/// <c>camera-module.json</c> and every capture-schedule revision in the raw-ingress journal, since the active revision's
/// processing steps replace the file's at runtime and an earlier revision's root can still hold an outbox. Each root is
/// mapped from its container path through the writable bind mounts. A root outside every persisted mount holds nothing
/// durable, so it has nothing to snapshot; a root that is not an absolute, normalized container path is refused before
/// anything changes.
/// </remarks>
internal static class CameraAgentStateInventory
{
    private const string OutboxStore = "outbox/artifact-outbox.db";
    private const int MaximumProfileBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<string> Stores(InstallationPaths paths, ContractReplayProfile replayProfile)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var stores = new List<string>(CameraAgentStateLayout.SqliteStores);
        var mounts = CameraAgentStateLayout.WritableBindSources(paths.StateRoot, replayProfile)
            .Where(static source => source.ContainerPath != "/run/hvo-replay")
            .Select(source => (Container: source.ContainerPath, Relative: Path.GetRelativePath(paths.StateRoot, source.HostPath).Replace('\\', '/')))
            .ToArray();
        var discovered = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in ConfiguredStorageRoots(paths))
        {
            if (!IsNormalizedAbsolute(root))
                throw new InstallerException($"The CameraAgent storage root '{root}' is not an absolute, normalized path, so its outbox cannot be found for a snapshot; correct the configuration before upgrading.");
            var trimmed = root.TrimEnd('/');
            var mount = mounts.FirstOrDefault(mount => trimmed == mount.Container || trimmed.StartsWith(mount.Container + "/", StringComparison.Ordinal));
            if (mount.Container is null) continue;
            var relative = trimmed.Length == mount.Container.Length
                ? $"{mount.Relative}/{OutboxStore}"
                : $"{mount.Relative}/{trimmed[(mount.Container.Length + 1)..]}/{OutboxStore}";
            if (!stores.Contains(relative, StringComparer.Ordinal)) discovered.Add(relative);
        }
        stores.AddRange(discovered);
        return stores;
    }

    /// <summary>Whether <paramref name="relative"/> is a store location a snapshot manifest may name.</summary>
    public static bool IsValidStoreLocation(string relative)
        => relative.Length is > 0 and < 512 && relative.EndsWith(".db", StringComparison.Ordinal) &&
           !relative.StartsWith('/') && !relative.Contains('\\', StringComparison.Ordinal) &&
           relative.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not "..");

    private static SortedSet<string> ConfiguredStorageRoots(InstallationPaths paths)
    {
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        var configuration = Path.Combine(paths.ConfigRoot, "camera-module.json");
        if (File.Exists(configuration))
        {
            using var stream = SafeFileSystem.OpenRegularFileRead(configuration);
            if (stream.Length > MaximumProfileBytes)
                throw new InstallerException("The CameraAgent configuration is too large to inventory its storage roots.");
            using var document = JsonDocument.Parse(stream);
            CollectStorageRoots(document.RootElement, roots);
        }
        var journal = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        if (File.Exists(journal))
        {
            using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(journal);
            using var exists = database.Connection.CreateCommand();
            exists.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'capture_schedule_revisions';";
            if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1)
            {
                using var command = database.Connection.CreateCommand();
                command.CommandText = "SELECT profile_json FROM capture_schedule_revisions ORDER BY revision_number;";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var profile = (byte[])reader[0];
                    if (profile.Length > MaximumProfileBytes)
                        throw new InstallerException("A CameraAgent capture-schedule revision is too large to inventory its storage roots.");
                    try
                    {
                        using var document = JsonDocument.Parse(profile);
                        CollectStorageRoots(document.RootElement, roots);
                    }
                    catch (JsonException exception)
                    {
                        throw new InstallerException("A CameraAgent capture-schedule revision is not valid JSON, so its storage roots cannot be inventoried.", exception);
                    }
                }
            }
        }
        return roots;
    }

    /// <summary>Every <c>storageRoot</c> string anywhere in a configuration or profile, whatever its casing.</summary>
    private static void CollectStorageRoots(JsonElement element, SortedSet<string> roots)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String &&
                        string.Equals(property.Name, "storageRoot", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.GetString() is { Length: > 0 } root)
                    {
                        roots.Add(root);
                    }
                    else
                    {
                        CollectStorageRoots(property.Value, roots);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectStorageRoots(item, roots);
                break;
        }
    }

    private static bool IsNormalizedAbsolute(string root)
        => root.StartsWith('/') && !root.Contains('\\', StringComparison.Ordinal) &&
           root.TrimEnd('/').Split('/').Skip(1).All(static segment => segment.Length > 0 && segment is not "." and not "..");
}
