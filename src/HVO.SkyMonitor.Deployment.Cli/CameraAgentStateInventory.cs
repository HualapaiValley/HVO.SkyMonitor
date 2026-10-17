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
        => Stores(paths, replayProfile, includeJournalRevisions: true);

    /// <summary>
    /// The inventory from configuration alone, for a restore that cannot read the journal it is about to replace.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredStores(InstallationPaths paths, ContractReplayProfile replayProfile)
        => Stores(paths, replayProfile, includeJournalRevisions: false);

    /// <summary>
    /// The inventory a restore, or a rollback that restores, reads from stores the release it replaces wrote: the full
    /// inventory, or, when that release left its journal unreadable or its schedule revisions in a shape this installer
    /// does not read, the inventory from configuration alone and what could not be read. The configuration is the
    /// installer's own, mounted read-only, so the fallback never fails on what a candidate wrote.
    /// </summary>
    public static (IReadOnlyList<string> Stores, string? Unread) StoresForRestore(
        InstallationPaths paths, ContractReplayProfile replayProfile)
    {
        try
        {
            return (Stores(paths, replayProfile), null);
        }
        catch (Exception exception) when (IsUnreadable(exception))
        {
            return (ConfiguredStores(paths, replayProfile),
                $"the storage roots in the journal ({Redaction.SafeDiagnostic(exception.Message)})");
        }
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is how reading a store a candidate may have written fails: the store cannot
    /// be opened or read, or a value in it is not in the shape the installer reads, which the installer reports as an
    /// <see cref="InstallerException"/> rather than casting.
    /// </summary>
    public static bool IsUnreadable(Exception exception)
        => exception is InstallerException or SqliteException or IOException or UnauthorizedAccessException;

    private static List<string> Stores(InstallationPaths paths, ContractReplayProfile replayProfile, bool includeJournalRevisions)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var stores = new List<string>(CameraAgentStateLayout.SqliteStores);
        var mounts = CameraAgentStateLayout.WritableBindSources(paths.StateRoot, replayProfile)
            .Where(static source => source.ContainerPath != "/run/hvo-replay")
            .Select(source => (Container: source.ContainerPath, Relative: Path.GetRelativePath(paths.StateRoot, source.HostPath).Replace('\\', '/')))
            .ToArray();
        var discovered = new SortedSet<string>(StringComparer.Ordinal);
        var configured = ConfiguredStorageRoots(paths);
        foreach (var (root, fromJournal) in configured.Select(static root => (root, false))
                     .Concat((includeJournalRevisions ? JournalStorageRoots(paths) : Enumerable.Empty<string>()).Select(static root => (root, true))))
        {
            // A root from the journal is what a release wrote, so it is never echoed: it is only validated.
            if (!IsNormalizedAbsolute(root))
                throw new InstallerException(fromJournal
                    ? "A CameraAgent capture-schedule revision names a storage root that is not an absolute, normalized path, so its outbox cannot be found for a snapshot."
                    : $"The CameraAgent storage root '{root}' is not an absolute, normalized path, so its outbox cannot be found for a snapshot; correct the configuration before upgrading.");
            var trimmed = root.TrimEnd('/');
            var mount = mounts.FirstOrDefault(mount => trimmed == mount.Container || trimmed.StartsWith(mount.Container + "/", StringComparison.Ordinal));
            if (mount.Container is null) continue;
            var relative = trimmed.Length == mount.Container.Length
                ? $"{mount.Relative}/{OutboxStore}"
                : $"{mount.Relative}/{trimmed[(mount.Container.Length + 1)..]}/{OutboxStore}";
            // Every location the inventory returns is one a snapshot manifest and a restore plan accept, so a restore
            // never refuses a location after the inventory has been read.
            if (!IsValidStoreLocation(relative))
                throw new InstallerException(fromJournal
                    ? "A CameraAgent capture-schedule revision names a storage root whose outbox location a snapshot cannot record."
                    : "A configured CameraAgent storage root names an outbox location a snapshot cannot record; correct the configuration before upgrading.");
            if (!stores.Contains(relative, StringComparer.Ordinal)) discovered.Add(relative);
        }
        stores.AddRange(discovered);
        return stores;
    }

    /// <summary>Whether <paramref name="relative"/> is a store location a snapshot manifest may name.</summary>
    public static bool IsValidStoreLocation(string relative)
        => relative.Length is > 0 and < 512 && relative.EndsWith(".db", StringComparison.Ordinal) &&
           !relative.StartsWith('/') && !relative.Contains('\\', StringComparison.Ordinal) && !relative.Any(char.IsControl) &&
           relative.Split('/').All(static segment => segment.Length > 0 && segment is not "." and not "..");

    /// <summary>The storage roots the deployment configuration names; the installer writes it and mounts it read-only.</summary>
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
        return roots;
    }

    /// <summary>
    /// The storage roots every capture-schedule revision in the raw-ingress journal names. The journal is written by the
    /// running release, which may be a candidate, so each profile is checked for the shape the installer reads (a JSON
    /// blob of bounded size, valid UTF-8, well-formed JSON) before it is used, and any other shape is reported as an
    /// <see cref="InstallerException"/>. Nothing here casts a value or assumes it is present.
    /// </summary>
    private static SortedSet<string> JournalStorageRoots(InstallationPaths paths)
    {
        var roots = new SortedSet<string>(StringComparer.Ordinal);
        var journal = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
        if (!File.Exists(journal)) return roots;
        using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(journal);
        using var exists = database.Connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'capture_schedule_revisions';";
        if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 1) return roots;
        using var command = database.Connection.CreateCommand();
        // typeof() and length() are SQLite's own answers, so they are always text and an integer; the profile itself is
        // selected only once it is known to be a blob within the bound.
        command.CommandText = """
            SELECT typeof(profile_json), length(profile_json),
                   CASE WHEN typeof(profile_json) = 'blob' AND length(profile_json) <= $maximum THEN profile_json END
            FROM capture_schedule_revisions ORDER BY revision_number;
            """;
        command.Parameters.AddWithValue("$maximum", MaximumProfileBytes);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            if (type != "blob")
                throw new InstallerException($"A CameraAgent capture-schedule revision stores its profile as {type}, not as a JSON blob, so its storage roots cannot be inventoried.");
            if (reader.GetInt64(1) > MaximumProfileBytes)
                throw new InstallerException("A CameraAgent capture-schedule revision is too large to inventory its storage roots.");
            var profile = reader.GetFieldValue<byte[]>(2);
            if (!System.Text.Unicode.Utf8.IsValid(profile))
                throw new InstallerException("A CameraAgent capture-schedule revision's profile is not valid UTF-8, so its storage roots cannot be inventoried.");
            try
            {
                using var document = JsonDocument.Parse(profile);
                CollectStorageRoots(document.RootElement, roots);
            }
            // A string that does not transcode (an escaped lone surrogate, say) is reported by the reader as an invalid
            // operation rather than as malformed JSON.
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                throw new InstallerException("A CameraAgent capture-schedule revision is not valid JSON, so its storage roots cannot be inventoried.", exception);
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
