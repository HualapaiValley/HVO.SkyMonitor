using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The cleanup policy for an instance's database snapshots (#1353). Each image lifecycle operation that migrates state,
/// and each backup and restore, writes a snapshot under <c>operations/lifecycle/&lt;operation&gt;/state-snapshots/</c>.
/// Once a backup or restore completes, every snapshot of this instance that nothing refers to is removed. A snapshot is
/// kept when the instance manifest names it for a rollback, when an unfinished operation in the instance's journal names
/// it or wrote it, or when that operation's saved manifest names it. A snapshot whose manifest cannot be read, or that
/// belongs to another instance, is never touched.
/// </summary>
internal static class CameraAgentStateSnapshotRetention
{
    private const string SnapshotsFolderName = "state-snapshots";
    private const string ManifestFileName = "manifest.json";

    /// <summary>The snapshots <see cref="PruneAsync"/> would remove now, for a plan.</summary>
    public static async Task<IReadOnlyList<string>> PlanAsync(
        InstallationPaths paths, InstanceManifest manifest, CancellationToken cancellationToken)
        => [.. (await UnreferencedAsync(paths, manifest, cancellationToken).ConfigureAwait(false)).Select(static snapshot => snapshot.Root)];

    /// <summary>Removes every snapshot of this instance nothing refers to, each one durably.</summary>
    public static async Task<IReadOnlyList<string>> PruneAsync(
        InstallationPaths paths, InstanceManifest manifest, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        foreach (var snapshot in await UnreferencedAsync(paths, manifest, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeFileSystem.MakeTreeOwnerWritable(snapshot.Root);
            Directory.Delete(snapshot.Root, recursive: true);
            var parent = Path.GetDirectoryName(snapshot.Root)!;
            if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            NativeLinux.FlushDirectory(Path.GetDirectoryName(parent)!);
            removed.Add(snapshot.Root);
        }
        return removed;
    }

    /// <summary>
    /// Every snapshot of this instance on disk that neither the manifest nor an unfinished operation refers to. The
    /// snapshot's own manifest names its instance; one that cannot be read is left alone.
    /// </summary>
    private static async Task<IReadOnlyList<(Guid OperationId, Guid SnapshotId, string Root)>> UnreferencedAsync(
        InstallationPaths paths, InstanceManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(manifest);
        var lifecycleRoot = Path.Combine(paths.OperationsRoot, "lifecycle");
        if (!Directory.Exists(lifecycleRoot)) return [];
        SafeFileSystem.EnsureSafeExistingAncestors(lifecycleRoot);
        var kept = await ReferencedAsync(paths, manifest, cancellationToken).ConfigureAwait(false);
        var unreferenced = new List<(Guid, Guid, string)>();
        foreach (var operationRoot in Directory.EnumerateDirectories(lifecycleRoot).Order(StringComparer.Ordinal))
        {
            if (!Guid.TryParseExact(Path.GetFileName(operationRoot), "D", out var operationId)) continue;
            var snapshotsRoot = Path.Combine(operationRoot, SnapshotsFolderName);
            if (!Directory.Exists(snapshotsRoot) || new DirectoryInfo(snapshotsRoot).LinkTarget is not null) continue;
            foreach (var root in Directory.EnumerateDirectories(snapshotsRoot).Order(StringComparer.Ordinal))
            {
                if (!Guid.TryParseExact(Path.GetFileName(root), "D", out var snapshotId) ||
                    new DirectoryInfo(root).LinkTarget is not null ||
                    kept.Snapshots.Contains(snapshotId) || kept.Operations.Contains(operationId) ||
                    await OwnerAsync(root, cancellationToken).ConfigureAwait(false) != manifest.InstanceId)
                {
                    continue;
                }
                unreferenced.Add((operationId, snapshotId, root));
            }
        }
        return unreferenced;
    }

    /// <summary>
    /// What refers to a snapshot: the manifest's rollback snapshot, and an unfinished operation's own snapshots, the ones
    /// its journal entry names, and the rollback snapshot of the manifest it saved before it changed anything.
    /// </summary>
    private static async Task<(HashSet<Guid> Snapshots, HashSet<Guid> Operations)> ReferencedAsync(
        InstallationPaths paths, InstanceManifest manifest, CancellationToken cancellationToken)
    {
        var snapshots = new HashSet<Guid>();
        var operations = new HashSet<Guid>();
        if (manifest.PreviousStateSnapshot is { } previous) snapshots.Add(previous.SnapshotId);
        var journal = await CameraAgentLifecycleManager.ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        if (journal is null || journal.Status == InstallationStatus.Completed || CameraAgentLifecycleManager.IsRefused(journal))
        {
            return (snapshots, operations);
        }
        operations.Add(journal.OperationId);
        foreach (var named in new[] { journal.StateSnapshot, journal.RetainedStateSnapshot }.OfType<LifecycleStateSnapshot>())
        {
            snapshots.Add(named.SnapshotId);
        }
        if (journal.StateRestorePlan is { } plan) snapshots.Add(plan.SnapshotId);
        var saved = Path.Combine(paths.OperationsRoot, "lifecycle", journal.OperationId.ToString("D"), "previous-instance-manifest.json");
        if (File.Exists(saved))
        {
            var previousManifest = await CameraAgentLifecycleManager.ReadManifestAsync(saved, cancellationToken).ConfigureAwait(false);
            if (previousManifest.PreviousStateSnapshot is { } savedSnapshot) snapshots.Add(savedSnapshot.SnapshotId);
        }
        return (snapshots, operations);
    }

    /// <summary>The instance a snapshot's manifest names, or <see langword="null"/> when it cannot be read.</summary>
    private static async Task<Guid?> OwnerAsync(string root, CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, ManifestFileName);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            var snapshot = await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.StateSnapshotManifest, cancellationToken)
                .ConfigureAwait(false);
            return snapshot?.InstanceId;
        }
        catch (Exception exception) when (exception is JsonException or InstallerException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
