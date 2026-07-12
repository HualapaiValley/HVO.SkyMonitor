using System.Collections.Concurrent;

namespace HVO.SkyMonitor.CameraAgent.Common.Storage;

public readonly record struct StorageCapacity(long TotalBytes, long AvailableBytes)
{
    public double AvailablePercent => 100d * AvailableBytes / TotalBytes;
}

public interface IStorageCapacityProvider
{
    StorageCapacity GetCapacity(string storageRoot);
}

public sealed class FileSystemStorageCapacityProvider : IStorageCapacityProvider
{
    public StorageCapacity GetCapacity(string storageRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageRoot);
        var path = Path.GetFullPath(storageRoot);
        while (!Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path)
                ?? throw new IOException($"No existing parent exists for storage root '{storageRoot}'.");
        }
        path = ResolvePhysicalPath(path);

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var drive = DriveInfo.GetDrives()
            .Where(candidate => candidate.IsReady && IsWithinRoot(path, candidate.RootDirectory.FullName, comparison))
            .OrderByDescending(candidate => candidate.RootDirectory.FullName.Length)
            .FirstOrDefault()
            ?? throw new IOException($"No mounted filesystem exists for '{storageRoot}'.");
        if (!drive.IsReady || drive.TotalSize <= 0)
        {
            throw new IOException($"Filesystem capacity is unavailable for '{storageRoot}'.");
        }
        return new StorageCapacity(drive.TotalSize, drive.AvailableFreeSpace);
    }

    private static bool IsWithinRoot(string path, string root, StringComparison comparison)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        root = Path.TrimEndingDirectorySeparator(root);
        if (path.Equals(root, comparison))
        {
            return true;
        }
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, comparison);
    }

    private static string ResolvePhysicalPath(string path)
    {
        var root = Path.GetPathRoot(path) ?? throw new IOException($"No filesystem root exists for '{path}'.");
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            current = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? current;
        }
        return Path.GetFullPath(current);
    }
}

public sealed record StoragePressureSnapshot(
    string StorageRoot,
    StorageCapacity Capacity,
    bool IsUnderPressure,
    int EffectiveRetentionDays,
    DateTimeOffset EvaluatedUtc,
    string? ProbeFailure = null);

public sealed class StoragePressureState
{
    private readonly ConcurrentDictionary<string, StoragePressureSnapshot> _snapshots = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public IReadOnlyCollection<StoragePressureSnapshot> Snapshots => _snapshots.Values.ToArray();

    public StoragePressureSnapshot? Get(string storageRoot)
        => _snapshots.TryGetValue(Path.GetFullPath(storageRoot), out var value) ? value : null;

    internal void Set(StoragePressureSnapshot snapshot) => _snapshots[snapshot.StorageRoot] = snapshot;
}

internal static class StorageLifecycleLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static SemaphoreSlim ForRoot(string storageRoot)
        => Gates.GetOrAdd(Path.GetFullPath(storageRoot), static _ => new SemaphoreSlim(1, 1));
}
