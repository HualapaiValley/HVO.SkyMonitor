using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// What a backup of a CameraAgent instance holds (#1353). Under <c>instance/</c>, every folder and regular file of the
/// instance root, bulk captures included, except the SQLite stores, which come from the backup's verified snapshot
/// instead. Under <c>snapshots/&lt;operation&gt;/&lt;snapshot&gt;/</c>, that snapshot, and the snapshot the instance manifest
/// keeps for a rollback, so a restored instance can still roll back.
/// </summary>
internal static class BackupInventory
{
    /// <summary>The folder under the instance root in which a restore stages the archive and keeps what it replaces.</summary>
    public const string RestoreFolderName = ".hvo-restore";

    private const string InstanceLockName = ".deployment.lock";
    private const uint TypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint SocketType = 0xC000;

    /// <summary>
    /// The instance's files as a backup holds them, in a stable order, and what it leaves out on purpose: the instance
    /// lock, which belongs to the running command; a socket, which a stopped instance does not need; and a restore's
    /// staging folder. The stores <paramref name="stores"/> names, and their recovery files, are left to the snapshot. A
    /// link or a device refuses the backup before anything changes, because a restore could not put it back as it was.
    /// </summary>
    public static (List<BackupItem> Items, List<string> Omitted) CollectInstance(InstallationPaths paths, IReadOnlyList<string> stores)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(stores);
        var snapshotted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var store in stores)
        {
            var relative = $"state/{store}";
            snapshotted.Add(relative);
            foreach (var suffix in CameraAgentStateLayout.SqliteSidecarSuffixes) snapshotted.Add(relative + suffix);
        }
        var items = new List<BackupItem>();
        var omitted = new List<string>();
        SafeFileSystem.EnsureSafeExistingAncestors(paths.InstanceRoot);
        Walk(paths.InstanceRoot, string.Empty);
        return (items, omitted);

        void Walk(string directory, string relative)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                var entry = relative.Length == 0 ? name : $"{relative}/{name}";
                if (relative.Length == 0 && name is InstanceLockName or RestoreFolderName)
                {
                    omitted.Add(entry);
                    continue;
                }
                if (snapshotted.Contains(entry)) continue;
                var node = NativeLinux.GetNodeIdentity(path);
                var mode = node.Mode & (UnixFileMode)0x1FF;
                switch (node.Type & TypeMask)
                {
                    case DirectoryType:
                        items.Add(new BackupItem($"{BackupArchive.InstancePrefix}{entry}/", null, Directory: true, mode, 0));
                        Walk(path, entry);
                        break;
                    case RegularFileType:
                        items.Add(new BackupItem($"{BackupArchive.InstancePrefix}{entry}", path, Directory: false, mode, new FileInfo(path).Length));
                        break;
                    case SocketType:
                        omitted.Add(entry);
                        break;
                    default:
                        throw InstallerException.RefusedFormatted(
                            $"The instance holds '{CameraAgentStateDiagnostics.Reason(entry)}', which is neither a file nor a folder, so it cannot be backed up. Move it out of {CameraAgentStateDiagnostics.Reason(paths.InstanceRoot)}, then run the backup again.");
                }
            }
        }
    }

    /// <summary>
    /// Everything the archive holds: the instance's files, the backup's own snapshot, and the snapshot the instance
    /// manifest keeps for a rollback when it is intact. A rollback snapshot that is missing or damaged is left out, named
    /// among the omitted entries, because the backup can carry only what verifies.
    /// </summary>
    public static async Task<(List<BackupItem> Items, List<string> Omitted)> CollectAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        IReadOnlyList<string> stores,
        LifecycleStateSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var (items, omitted) = CollectInstance(paths, stores);
        items.AddRange(Snapshot(paths, snapshot));
        if (manifest.PreviousStateSnapshot is { } previous && previous.SnapshotId != snapshot.SnapshotId)
        {
            try
            {
                await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, previous, cancellationToken).ConfigureAwait(false);
                items.AddRange(Snapshot(paths, previous));
            }
            catch (InstallerException)
            {
                omitted.Add(SnapshotPrefix(previous));
            }
        }
        return (items, omitted);
    }

    /// <summary>The archive name of a snapshot's folder: <c>snapshots/&lt;operation&gt;/&lt;snapshot&gt;/</c>.</summary>
    public static string SnapshotPrefix(LifecycleStateSnapshot snapshot)
        => string.Create(CultureInfo.InvariantCulture, $"{BackupArchive.SnapshotPrefix}{snapshot.OperationId:D}/{snapshot.SnapshotId:D}/");

    /// <summary>
    /// The snapshot folder an archive entry under <c>snapshots/</c> belongs to and the file's name in it, or
    /// <see langword="null"/> when the entry is not exactly <c>snapshots/&lt;operation&gt;/&lt;snapshot&gt;/&lt;file&gt;</c>.
    /// </summary>
    public static (Guid OperationId, Guid SnapshotId, string File)? ParseSnapshotEntry(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!name.StartsWith(BackupArchive.SnapshotPrefix, StringComparison.Ordinal)) return null;
        var parts = name[BackupArchive.SnapshotPrefix.Length..].Split('/');
        return parts.Length == 3 && Guid.TryParseExact(parts[0], "D", out var operation) && Guid.TryParseExact(parts[1], "D", out var snapshot) &&
               parts[2].Length > 0 && parts[2] == Path.GetFileName(parts[2])
            ? (operation, snapshot, parts[2])
            : null;
    }

    private static IEnumerable<BackupItem> Snapshot(InstallationPaths paths, LifecycleStateSnapshot snapshot)
    {
        var root = CameraAgentStateSnapshot.SnapshotRoot(paths, snapshot.OperationId, snapshot.SnapshotId);
        var prefix = SnapshotPrefix(snapshot);
        foreach (var file in Directory.EnumerateFiles(root).Order(StringComparer.Ordinal))
        {
            var node = NativeLinux.GetNodeIdentity(file);
            if ((node.Type & TypeMask) != RegularFileType)
                throw InstallerException.Formatted($"The database snapshot holds '{CameraAgentStateDiagnostics.Reason(Path.GetFileName(file))}', which is not a file.");
            yield return new BackupItem(prefix + Path.GetFileName(file), file, Directory: false, node.Mode & (UnixFileMode)0x1FF, new FileInfo(file).Length);
        }
    }
}
