using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Notifications;

/// <summary>Raised when an existing protected state file exceeds the maximum allowed size immediately before replacement.</summary>
public sealed class NotificationStoreOversizedTargetException : IOException
{
    public NotificationStoreOversizedTargetException()
    {
    }

    public NotificationStoreOversizedTargetException(string message)
        : base(message)
    {
    }

    public NotificationStoreOversizedTargetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Result classification when inspecting a protected notification state file path.</summary>
public enum NotificationPathStatus
{
    ExistsRegularFile = 0,
    ConfirmedAbsent = 1,
    Unreadable = 2
}

/// <summary>Structured result of verifying a state directory's ancestor chain, the directory itself, and target file.</summary>
public readonly record struct NotificationPathInspection(
    NotificationPathStatus Status,
    string? TargetPath,
    string? ReasonTag,
    string? ExceptionTypeName,
    bool IsSymlink);

/// <summary>Result of a bounded state-file read enforcing an explicit maximum byte count.</summary>
public readonly record struct BoundedFileReadResult(
    bool ExceededLimit,
    ReadOnlyMemory<byte> Content);

/// <summary>
/// Restricted (0700/0600 on Unix), symlink-refusing, fsync'd atomic file publication helper backed by
/// <see cref="HVO.SkyMonitor.Storage.FileSystem"/> primitives for operator notification state and checkpoints.
/// </summary>
public static class NotificationStateFilePublisher
{
    private const UnixFileMode RestrictedDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode RestrictedFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const int MaximumVerifiedDirectoryCacheEntries = 4096;

    private static readonly StringComparer DirectoryPathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Tracks newly created directories whose parent directory entry has not yet completed DurableSync.Directory.
    // Value is true when the directory's parent pre-existed and may be a validated symlinked ancestor.
    private static readonly ConcurrentDictionary<string, bool> PendingParentSyncDirectories = new(DirectoryPathComparer);

    // Tracks directories whose parent directory entry has been durably verified/synced in this process.
    private static readonly ConcurrentDictionary<string, byte> VerifiedParentSyncedDirectories = new(DirectoryPathComparer);

    /// <summary>
    /// Walks and verifies the ancestor directory chain top-down, then inspects <paramref name="directoryPath"/> and
    /// <paramref name="fileName"/>. Returns <see cref="NotificationPathStatus.ConfirmedAbsent"/> ONLY when every
    /// existing ancestor is a valid traversable directory and the missing entry failed with genuine absence
    /// (<see cref="FileNotFoundException"/>). Any non-directory ancestor (regular file, FIFO, device, symlink to
    /// non-directory, dangling symlink), untraversable directory, or symlink/non-regular state entry returns
    /// <see cref="NotificationPathStatus.Unreadable"/> so callers fail closed.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Filesystem inspection errors must classify deterministically as Unreadable so callers fail closed without falling back.")]
    public static NotificationPathInspection InspectStateFilePath(string directoryPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (string.IsNullOrEmpty(fullDirectory))
        {
            fullDirectory = Path.GetPathRoot(Path.GetFullPath(directoryPath)) ?? directoryPath;
        }

        // 1. Walk proper ancestors top-down so ENOTDIR (regular file ancestor) or unreadable parents are detected
        //    before treating a missing child as confirmed absence.
        foreach (var ancestor in GetAncestorsTopDown(fullDirectory))
        {
            FileAttributes ancestorAttributes;
            try
            {
                ancestorAttributes = File.GetAttributes(ancestor);
            }
            catch (FileNotFoundException)
            {
                if (TryDetectLinkTarget(ancestor, out var linkError))
                {
                    return new NotificationPathInspection(
                        NotificationPathStatus.Unreadable, null, "state-dir-ancestor-symlink", nameof(IOException), IsSymlink: true);
                }
                if (linkError is not null)
                {
                    return new NotificationPathInspection(
                        NotificationPathStatus.Unreadable, null, $"state-dir-ancestor:{linkError}", linkError, IsSymlink: false);
                }

                // Every existing ancestor above this segment is a verified traversable directory; ENOENT here is genuine absence.
                return new NotificationPathInspection(
                    NotificationPathStatus.ConfirmedAbsent, null, null, null, IsSymlink: false);
            }
            catch (DirectoryNotFoundException)
            {
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, "state-dir-ancestor-not-directory", nameof(DirectoryNotFoundException), IsSymlink: false);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                var exceptionName = ex.GetType().Name;
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, $"state-dir-ancestor:{exceptionName}", exceptionName, IsSymlink: false);
            }

            var ancestorIsLink = (ancestorAttributes & FileAttributes.ReparsePoint) != 0;
            if (!ancestorIsLink)
            {
                ancestorIsLink = TryDetectLinkTarget(ancestor, out var linkErr);
                if (linkErr is not null)
                {
                    return new NotificationPathInspection(
                        NotificationPathStatus.Unreadable, null, $"state-dir-ancestor:{linkErr}", linkErr, IsSymlink: false);
                }
            }

            if (ancestorIsLink)
            {
                // File.GetAttributes(ancestor) follows symlinks using kernel path-resolution semantics
                // (handling relative '..' targets under symlinked parents accurately while leaving
                // dangling links, links to non-directories, and inaccessible targets without Directory).
                if ((ancestorAttributes & FileAttributes.Directory) == 0 ||
                    (ancestorAttributes & FileAttributes.Device) != 0 ||
                    (OperatingSystem.IsWindows() && !Directory.Exists(ancestor)))
                {
                    return new NotificationPathInspection(
                        NotificationPathStatus.Unreadable, null, "state-dir-ancestor-symlink-not-dir", nameof(IOException), IsSymlink: true);
                }
            }
            else if ((ancestorAttributes & FileAttributes.Directory) == 0 || (ancestorAttributes & FileAttributes.Device) != 0)
            {
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, "state-dir-ancestor-not-directory", nameof(IOException), IsSymlink: false);
            }
        }

        // 2. Inspect StateDirectory itself (symlinks are never permitted for the state directory itself).
        if (TryDetectLinkTarget(fullDirectory, out var dirLinkError))
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, "state-dir-symlink", nameof(IOException), IsSymlink: true);
        }
        if (dirLinkError is not null)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, $"state-dir:{dirLinkError}", dirLinkError, IsSymlink: false);
        }

        FileAttributes dirAttributes;
        try
        {
            dirAttributes = File.GetAttributes(fullDirectory);
        }
        catch (FileNotFoundException)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.ConfirmedAbsent, null, null, null, IsSymlink: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var exceptionName = ex.GetType().Name;
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, $"state-dir:{exceptionName}", exceptionName, IsSymlink: false);
        }

        if ((dirAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, "state-dir-symlink", nameof(IOException), IsSymlink: true);
        }

        if ((dirAttributes & FileAttributes.Directory) == 0 || (dirAttributes & FileAttributes.Device) != 0)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, "state-dir-not-directory", nameof(IOException), IsSymlink: false);
        }

        // 3. Inspect target file inside the verified StateDirectory.
        var targetPath = Path.Combine(fullDirectory, fileName);
        if (TryDetectLinkTarget(targetPath, out var targetLinkError))
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, "target-symlink", nameof(IOException), IsSymlink: true);
        }
        if (targetLinkError is not null)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, $"target-stat:{targetLinkError}", targetLinkError, IsSymlink: false);
        }

        FileAttributes targetAttributes;
        try
        {
            targetAttributes = File.GetAttributes(targetPath);
        }
        catch (FileNotFoundException)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.ConfirmedAbsent, null, null, null, IsSymlink: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var exceptionName = ex.GetType().Name;
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, $"target-stat:{exceptionName}", exceptionName, IsSymlink: false);
        }

        if ((targetAttributes & FileAttributes.ReparsePoint) != 0)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, "target-symlink", nameof(IOException), IsSymlink: true);
        }

        if ((targetAttributes & FileAttributes.Directory) != 0)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, "target-directory", nameof(IOException), IsSymlink: false);
        }

        if ((targetAttributes & FileAttributes.Device) != 0)
        {
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, "target-device", nameof(IOException), IsSymlink: false);
        }

        try
        {
            VerifyRegularFileNoSymlink(fullDirectory, fileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var exceptionName = ex.GetType().Name;
            var isSymlink = ex.Message.Contains("symbolic link", StringComparison.OrdinalIgnoreCase);
            return new NotificationPathInspection(
                NotificationPathStatus.Unreadable, null, $"target-verify:{exceptionName}", exceptionName, IsSymlink: isSymlink);
        }

        return new NotificationPathInspection(
            NotificationPathStatus.ExistsRegularFile, targetPath, null, null, IsSymlink: false);
    }

    private static List<string> GetAncestorsTopDown(string fullPath)
    {
        var ancestors = new List<string>();
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current))
        {
            ancestors.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }
            current = parent;
        }
        ancestors.Reverse();
        return ancestors;
    }

    private static bool TryDetectLinkTarget(string path, out string? errorExceptionTypeName)
    {
        errorExceptionTypeName = null;
        try
        {
            return new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            errorExceptionTypeName = ex.GetType().Name;
            return false;
        }
    }

    public static void EnsureRestrictedDirectory(string directoryPath)
        => EnsureRestrictedDirectory(directoryPath, onDirectorySynced: null, directorySyncAction: null);

    internal static void EnsureRestrictedDirectory(string directoryPath, Action<string>? onDirectorySynced)
        => EnsureRestrictedDirectory(directoryPath, onDirectorySynced, directorySyncAction: null);

    internal static void EnsureRestrictedDirectory(
        string directoryPath,
        Action<string>? onDirectorySynced,
        Action<string>? directorySyncAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (string.IsNullOrEmpty(fullDirectory))
        {
            fullDirectory = Path.GetPathRoot(Path.GetFullPath(directoryPath)) ?? directoryPath;
        }

        var missingDirectories = CollectMissingDirectoriesAndValidateAncestors(fullDirectory);
        var newlyCreatedDirectories = new HashSet<string>(DirectoryPathComparer);
        var chainBottomUp = GetSelfAndAncestorsBottomUp(fullDirectory);

        for (var i = 0; i < missingDirectories.Count; i++)
        {
            var missingDirectory = missingDirectories[i];
            var allowSymlinkedParent = i == 0;
            VerifyPathIsNotSymlink(missingDirectory);
            // Record pending parent sync BEFORE creating the directory so any retry or concurrent publisher
            // observing the newly created directory on disk still syncs its parent before acknowledging.
            VerifiedParentSyncedDirectories.TryRemove(missingDirectory, out _);
            PendingParentSyncDirectories[missingDirectory] = allowSymlinkedParent;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Pin the accepted pre-existing parent directory once and walk/create/harden every protected descendant
            // component strictly relative to its pinned parent descriptor (mkdirat + openat(O_NOFOLLOW|O_DIRECTORY)
            // + fstat/statx + fchmod(0700)) so an ancestor replacement race can never direct fchmod outside the tree.
            var topProtectedIndex = 0;
            for (var i = chainBottomUp.Count - 1; i >= 0; i--)
            {
                if (i == 0 || PendingParentSyncDirectories.ContainsKey(chainBottomUp[i]))
                {
                    topProtectedIndex = i;
                    break;
                }
            }

            var topProtectedPath = chainBottomUp[topProtectedIndex];
            var anchorParent = Path.GetDirectoryName(topProtectedPath);
            try
            {
                if (!string.IsNullOrEmpty(anchorParent))
                {
                    var protectedComponents = new List<string>(topProtectedIndex + 1);
                    for (var i = topProtectedIndex; i >= 0; i--)
                    {
                        var component = Path.GetFileName(chainBottomUp[i]);
                        if (!string.IsNullOrEmpty(component))
                        {
                            protectedComponents.Add(component);
                        }
                    }

                    var allowSymlinkedAnchor = TryDetectLinkTarget(anchorParent, out _);
                    DurableSync.EnsureAndHardenDirectoryChain(
                        anchorParent,
                        protectedComponents,
                        RestrictedDirectoryMode,
                        createMissing: missingDirectories.Count > 0,
                        allowSymlinkedAnchor: allowSymlinkedAnchor);
                }
                else
                {
                    RestrictDirectoryModeNoFollow(fullDirectory);
                }
            }
            catch (FileSystemFaultException ex)
            {
                CleanupNonExistentPendingDirectories(missingDirectories);
                throw TranslateFault(ex);
            }
            catch
            {
                CleanupNonExistentPendingDirectories(missingDirectories);
                throw;
            }

            foreach (var missingDirectory in missingDirectories)
            {
                newlyCreatedDirectories.Add(missingDirectory);
            }
        }
        else
        {
            for (var i = 0; i < missingDirectories.Count; i++)
            {
                var missingDirectory = missingDirectories[i];
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Directory.CreateDirectory(missingDirectory);
                    }
                    else
                    {
                        Directory.CreateDirectory(missingDirectory, RestrictedDirectoryMode);
                    }
                }
                catch
                {
                    CleanupNonExistentPendingDirectories(missingDirectories);
                    throw;
                }

                newlyCreatedDirectories.Add(missingDirectory);
                RestrictDirectoryModeNoFollow(missingDirectory);
            }

            for (var i = chainBottomUp.Count - 1; i >= 0; i--)
            {
                var candidate = chainBottomUp[i];
                if (i == 0 || PendingParentSyncDirectories.ContainsKey(candidate))
                {
                    RestrictDirectoryModeNoFollow(candidate);
                }
            }
        }

        try
        {
            _ = PhysicalRoot.Open(fullDirectory);
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }

        // Fsync the target directory itself (strict; never best-effort), then walk bottom-up and fsync the parent of
        // EVERY directory whose parent entry has not yet been durably verified/synced in this process. Walking the full
        // unverified ancestor chain on first publish per process guarantees cross-process crash recovery when a prior
        // process crashed after creating nested directories before syncing their parents; PermissionDenied (EACCES/EPERM)
        // is treated as best-effort strictly on pre-existing ancestors above newly created directories, and skipped
        // ancestors are NEVER marked verified so a later publish retries once permissions are restored.
        _ = SyncDirectory(
            fullDirectory,
            allowSymlinkedAncestor: false,
            allowBestEffortPermissionDenied: false,
            directorySyncAction,
            onDirectorySynced);
        for (var i = 0; i < chainBottomUp.Count; i++)
        {
            var current = chainBottomUp[i];
            var isPending = PendingParentSyncDirectories.TryGetValue(current, out var pendingAllowSymlinkedParent);
            var isUnverified = !VerifiedParentSyncedDirectories.ContainsKey(current);
            if (!isPending && !isUnverified)
            {
                continue;
            }

            var parent = Path.GetDirectoryName(current);
            var parentSynced = true;
            if (!string.IsNullOrEmpty(parent))
            {
                var parentIsNewlyCreatedOrPending =
                    newlyCreatedDirectories.Contains(parent) ||
                    PendingParentSyncDirectories.ContainsKey(parent);
                var allowSymlinkedParent = isPending ? pendingAllowSymlinkedParent : !parentIsNewlyCreatedOrPending;
                var allowBestEffortPermissionDenied = !parentIsNewlyCreatedOrPending;
                var outcome = SyncDirectory(
                    parent,
                    allowSymlinkedAncestor: allowSymlinkedParent,
                    allowBestEffortPermissionDenied: allowBestEffortPermissionDenied,
                    directorySyncAction,
                    onDirectorySynced);
                parentSynced = outcome == DirectorySyncOutcome.Synced;
            }

            if (parentSynced)
            {
                MarkDirectoryParentVerifiedSynced(current);
                PendingParentSyncDirectories.TryRemove(current, out _);
            }
        }
    }

    private static void CleanupNonExistentPendingDirectories(List<string> missingDirectories)
    {
        for (var i = 0; i < missingDirectories.Count; i++)
        {
            var candidate = missingDirectories[i];
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                PendingParentSyncDirectories.TryRemove(candidate, out _);
            }
        }
    }

    internal static void ResetVerifiedDirectorySyncCacheForPathPrefixForTesting(string pathPrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(pathPrefix));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefixWithSeparator = normalized + Path.DirectorySeparatorChar;

        foreach (var key in PendingParentSyncDirectories.Keys)
        {
            if (string.Equals(key, normalized, comparison) || key.StartsWith(prefixWithSeparator, comparison))
            {
                PendingParentSyncDirectories.TryRemove(key, out _);
            }
        }

        foreach (var key in VerifiedParentSyncedDirectories.Keys)
        {
            if (string.Equals(key, normalized, comparison) || key.StartsWith(prefixWithSeparator, comparison))
            {
                VerifiedParentSyncedDirectories.TryRemove(key, out _);
            }
        }
    }

    private static void RestrictDirectoryModeNoFollow(string directoryPath)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                DurableSync.SetDirectoryModeNoFollow(directoryPath, RestrictedDirectoryMode);
            }
            catch (FileSystemFaultException ex)
            {
                throw TranslateFault(ex);
            }
            return;
        }

        VerifyPathIsNotSymlink(directoryPath);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(directoryPath, RestrictedDirectoryMode);
        }
    }

    private static List<string> GetSelfAndAncestorsBottomUp(string fullPath)
    {
        var chain = new List<string> { fullPath };
        var current = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(current))
        {
            chain.Add(current);
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }
            current = parent;
        }
        return chain;
    }

    private static void MarkDirectoryParentVerifiedSynced(string directoryPath)
    {
        if (VerifiedParentSyncedDirectories.Count >= MaximumVerifiedDirectoryCacheEntries &&
            !VerifiedParentSyncedDirectories.ContainsKey(directoryPath))
        {
            VerifiedParentSyncedDirectories.Clear();
        }
        VerifiedParentSyncedDirectories[directoryPath] = 0;
    }

    /// <summary>
    /// Checks filesystem metadata (without opening the file) to determine whether <paramref name="filePath"/> is an
    /// existing non-symlink regular file whose length strictly exceeds <paramref name="maxBytes"/>.
    /// </summary>
    public static bool IsRegularFileExceedingSize(string filePath, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists ||
                info.LinkTarget is not null ||
                (info.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.Device)) != 0)
            {
                return false;
            }
            return info.Length > maxBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<string> CollectMissingDirectoriesAndValidateAncestors(string fullDirectory)
    {
        var ancestors = GetAncestorsTopDown(fullDirectory);
        var missingDirectories = new List<string>();

        for (var i = 0; i < ancestors.Count; i++)
        {
            var ancestor = ancestors[i];
            FileAttributes ancestorAttributes;
            try
            {
                ancestorAttributes = File.GetAttributes(ancestor);
            }
            catch (FileNotFoundException)
            {
                if (TryDetectLinkTarget(ancestor, out var linkError))
                {
                    throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
                }
                if (linkError is not null)
                {
                    ThrowAccessError(linkError, "Refusing to access an unreadable ancestor directory in the protected operator notification store.");
                }

                for (var j = i; j < ancestors.Count; j++)
                {
                    missingDirectories.Add(ancestors[j]);
                }
                missingDirectories.Add(fullDirectory);
                return missingDirectories;
            }
            catch (DirectoryNotFoundException ex)
            {
                throw new IOException("Ancestor path in the protected operator notification store is not a directory.", ex);
            }

            var ancestorIsLink = (ancestorAttributes & FileAttributes.ReparsePoint) != 0;
            if (!ancestorIsLink)
            {
                ancestorIsLink = TryDetectLinkTarget(ancestor, out var linkErr);
                if (linkErr is not null)
                {
                    ThrowAccessError(linkErr, "Refusing to access an unreadable ancestor directory in the protected operator notification store.");
                }
            }

            if (ancestorIsLink)
            {
                if ((ancestorAttributes & FileAttributes.Directory) == 0 ||
                    (ancestorAttributes & FileAttributes.Device) != 0 ||
                    (OperatingSystem.IsWindows() && !Directory.Exists(ancestor)))
                {
                    throw new IOException("Refusing to access a symbolic link to a non-directory ancestor in the protected operator notification store.");
                }
            }
            else if ((ancestorAttributes & FileAttributes.Directory) == 0 || (ancestorAttributes & FileAttributes.Device) != 0)
            {
                throw new IOException("Ancestor path in the protected operator notification store is not a directory.");
            }
        }

        VerifyPathIsNotSymlink(fullDirectory);
        try
        {
            var dirAttributes = File.GetAttributes(fullDirectory);
            if ((dirAttributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
            }
            if ((dirAttributes & FileAttributes.Directory) == 0 || (dirAttributes & FileAttributes.Device) != 0)
            {
                throw new IOException("Protected operator notification store directory path is not a directory.");
            }
        }
        catch (FileNotFoundException)
        {
            missingDirectories.Add(fullDirectory);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new IOException("Protected operator notification store directory path is not a directory.", ex);
        }

        return missingDirectories;
    }

    private enum DirectorySyncOutcome
    {
        Synced = 0,
        SkippedPermissionDenied = 1
    }

    private static DirectorySyncOutcome SyncDirectory(
        string directoryPath,
        bool allowSymlinkedAncestor,
        bool allowBestEffortPermissionDenied,
        Action<string>? directorySyncAction,
        Action<string>? onDirectorySynced)
    {
        var syncPath = directoryPath;
        if (allowSymlinkedAncestor && OperatingSystem.IsLinux())
        {
            // When a pre-existing ancestor directory is itself a validated symlink to a directory (for example
            // /var/run -> /run or a relative '..' symlink chain), append "/." so O_NOFOLLOW applies to '.' while
            // still failing closed if the resolved target is not a directory.
            if (TryDetectLinkTarget(directoryPath, out var linkError))
            {
                syncPath = Path.Combine(directoryPath, ".");
            }
            else if (linkError is not null)
            {
                if (allowBestEffortPermissionDenied &&
                    string.Equals(linkError, nameof(UnauthorizedAccessException), StringComparison.Ordinal))
                {
                    return DirectorySyncOutcome.SkippedPermissionDenied;
                }
                ThrowAccessError(linkError, "Unable to verify ancestor directory before synchronization.");
            }
        }

        try
        {
            if (directorySyncAction is not null)
            {
                directorySyncAction(syncPath);
            }
            else
            {
                DurableSync.Directory(syncPath);
            }
        }
        catch (FileSystemFaultException ex) when (allowBestEffortPermissionDenied && ex.Kind == FileSystemFaultKind.PermissionDenied)
        {
            // Pre-existing non-readable ancestor directories (e.g. 0100/0300 traverse-only parents) cannot be opened
            // O_RDONLY for fsync; treat strictly as best-effort without marking the ancestor verified so a later
            // publish retries if permissions are restored.
            return DirectorySyncOutcome.SkippedPermissionDenied;
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }

        onDirectorySynced?.Invoke(directoryPath);
        return DirectorySyncOutcome.Synced;
    }

    [DoesNotReturn]
    private static void ThrowAccessError(string exceptionTypeName, string message)
    {
        if (string.Equals(exceptionTypeName, nameof(UnauthorizedAccessException), StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(message);
        }
        throw new IOException(message);
    }

    public static ValueTask<BoundedFileReadResult> ReadBoundedBytesAsync(
        string filePath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return ReadBoundedBytesCoreAsync(filePath, maxBytes, cancellationToken);
    }

    private static async ValueTask<BoundedFileReadResult> ReadBoundedBytesCoreAsync(
        string filePath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = Math.Min(maxBytes + 1, 16 * 1024),
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        var stream = new FileStream(filePath, options);
        await using (stream.ConfigureAwait(false))
        {
            return await ReadBoundedStreamAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    public static BoundedFileReadResult ReadBoundedBytes(string filePath, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var options = new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = Math.Min(maxBytes + 1, 16 * 1024),
            Options = FileOptions.SequentialScan
        };

        using var stream = new FileStream(filePath, options);
        return ReadBoundedStream(stream, maxBytes);
    }

    internal static async ValueTask<BoundedFileReadResult> ReadBoundedStreamAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (stream.CanSeek && stream.Length > maxBytes)
        {
            return new BoundedFileReadResult(ExceededLimit: true, Content: ReadOnlyMemory<byte>.Empty);
        }

        var initialCapacity = stream.CanSeek
            ? (int)Math.Min(Math.Max(stream.Length, 0), maxBytes)
            : Math.Min(maxBytes, 4096);
        using var buffer = new MemoryStream(initialCapacity);
        var chunk = new byte[Math.Min(maxBytes + 1, 16 * 1024)];
        var totalRead = 0;

        while (true)
        {
            var remainingAllowed = maxBytes - totalRead + 1;
            var toRead = Math.Min(chunk.Length, remainingAllowed);
            var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }
            totalRead += bytesRead;
            if (totalRead > maxBytes)
            {
                return new BoundedFileReadResult(ExceededLimit: true, Content: ReadOnlyMemory<byte>.Empty);
            }
            await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
        }

        return new BoundedFileReadResult(ExceededLimit: false, Content: buffer.ToArray());
    }

    internal static BoundedFileReadResult ReadBoundedStream(Stream stream, int maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        if (stream.CanSeek && stream.Length > maxBytes)
        {
            return new BoundedFileReadResult(ExceededLimit: true, Content: ReadOnlyMemory<byte>.Empty);
        }

        var initialCapacity = stream.CanSeek
            ? (int)Math.Min(Math.Max(stream.Length, 0), maxBytes)
            : Math.Min(maxBytes, 4096);
        using var buffer = new MemoryStream(initialCapacity);
        var chunk = new byte[Math.Min(maxBytes + 1, 16 * 1024)];
        var totalRead = 0;

        while (true)
        {
            var remainingAllowed = maxBytes - totalRead + 1;
            var toRead = Math.Min(chunk.Length, remainingAllowed);
            var bytesRead = stream.Read(chunk, 0, toRead);
            if (bytesRead == 0)
            {
                break;
            }
            totalRead += bytesRead;
            if (totalRead > maxBytes)
            {
                return new BoundedFileReadResult(ExceededLimit: true, Content: ReadOnlyMemory<byte>.Empty);
            }
            buffer.Write(chunk, 0, bytesRead);
        }

        return new BoundedFileReadResult(ExceededLimit: false, Content: buffer.ToArray());
    }

    public static void VerifyRegularFileNoSymlink(string directoryPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        VerifyPathIsNotSymlink(fullDirectory);

        var targetPath = Path.Combine(fullDirectory, fileName);
        VerifyPathIsNotSymlink(targetPath);

        try
        {
            var root = PhysicalRoot.Open(fullDirectory);
            var resolved = root.Resolve(fileName);
            if (File.Exists(resolved))
            {
                DurableSync.RequireRegularFile(resolved);
            }
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }
    }

    public static ValueTask PublishRestrictedAtomicAsync(
        string directoryPath,
        string fileName,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken,
        Action<string>? beforeRename = null)
        => PublishRestrictedAtomicAsync(
            directoryPath,
            fileName,
            content,
            beforeRename,
            onDirectorySynced: null,
            maxExistingTargetBytes: null,
            cancellationToken);

    public static ValueTask PublishRestrictedAtomicAsync(
        string directoryPath,
        string fileName,
        ReadOnlyMemory<byte> content,
        Action<string>? beforeRename,
        Action<string>? onDirectorySynced,
        CancellationToken cancellationToken)
        => PublishRestrictedAtomicAsync(
            directoryPath,
            fileName,
            content,
            beforeRename,
            onDirectorySynced,
            maxExistingTargetBytes: null,
            cancellationToken);

    public static ValueTask PublishRestrictedAtomicAsync(
        string directoryPath,
        string fileName,
        ReadOnlyMemory<byte> content,
        Action<string>? beforeRename,
        Action<string>? onDirectorySynced,
        int? maxExistingTargetBytes,
        CancellationToken cancellationToken)
        => PublishRestrictedAtomicAsync(
            directoryPath,
            fileName,
            content,
            beforeRename,
            onDirectorySynced,
            directorySyncAction: null,
            maxExistingTargetBytes,
            cancellationToken);

    internal static async ValueTask PublishRestrictedAtomicAsync(
        string directoryPath,
        string fileName,
        ReadOnlyMemory<byte> content,
        Action<string>? beforeRename,
        Action<string>? onDirectorySynced,
        Action<string>? directorySyncAction,
        int? maxExistingTargetBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        EnsureRestrictedDirectory(fullDirectory, onDirectorySynced, directorySyncAction);

        PhysicalRoot root;
        string finalPath;
        try
        {
            root = PhysicalRoot.Open(fullDirectory);
            finalPath = root.Resolve(fileName);
            VerifyExistingTargetForReplace(finalPath, maxExistingTargetBytes);
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }

        var tempFileName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        var tempPath = Path.Combine(fullDirectory, tempFileName);

        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 16 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = RestrictedFileMode;
            }

            var stream = new FileStream(tempPath, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    {
                        DurableSync.SetFileMode(stream.SafeFileHandle, RestrictedFileMode, tempPath);
                    }
                    DurableSync.File(stream.SafeFileHandle, tempPath);
                }
                catch (FileSystemFaultException ex)
                {
                    throw TranslateFault(ex);
                }
            }

            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(tempPath, RestrictedFileMode);
            }

            beforeRename?.Invoke(tempPath);

            try
            {
                // Re-verify existing target immediately before atomic replace to block external growth or substitution.
                VerifyExistingTargetForReplace(finalPath, maxExistingTargetBytes);
                AtomicPublisher.PublishPrepared(root, tempPath, finalPath, PublishMode.Replace);
                root.Verify(finalPath, "publish-notification-state");
                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(finalPath, RestrictedFileMode);
                }
            }
            catch (FileSystemFaultException ex)
            {
                throw TranslateFault(ex);
            }

            SyncDirectory(
                fullDirectory,
                allowSymlinkedAncestor: false,
                allowBestEffortPermissionDenied: false,
                directorySyncAction,
                onDirectorySynced);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (IOException)
                {
                    // Best-effort temporary cleanup.
                }
            }
        }
    }

    private static void VerifyExistingTargetForReplace(string finalPath, int? maxExistingTargetBytes)
    {
        VerifyPathIsNotSymlink(finalPath);
        if (File.Exists(finalPath) || Directory.Exists(finalPath))
        {
            DurableSync.RequireRegularFile(finalPath);
            if (maxExistingTargetBytes.HasValue && IsRegularFileExceedingSize(finalPath, maxExistingTargetBytes.Value))
            {
                throw new NotificationStoreOversizedTargetException(
                    "Persisted operator notification store exceeds the maximum allowed size.");
            }
        }
    }

    public static void DeleteIfExistsNoSymlink(string directoryPath, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        var targetPath = Path.Combine(fullDirectory, fileName);
        if (!File.Exists(targetPath) && new FileInfo(targetPath).LinkTarget is null)
        {
            return;
        }

        VerifyRegularFileNoSymlink(fullDirectory, fileName);
        File.Delete(targetPath);
        try
        {
            DurableSync.Directory(fullDirectory);
        }
        catch (FileSystemFaultException ex)
        {
            throw TranslateFault(ex);
        }
    }

    private static void VerifyPathIsNotSymlink(string path)
    {
        var fileInfo = new FileInfo(path);
        var dirInfo = new DirectoryInfo(path);
        if (fileInfo.LinkTarget is not null || dirInfo.LinkTarget is not null)
        {
            throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
        }

        if (fileInfo.Exists && (fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
        }

        if (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
        }
    }

    private static Exception TranslateFault(FileSystemFaultException exception) =>
        exception.Kind switch
        {
            FileSystemFaultKind.Containment => new IOException(
                "Refusing to access a symbolic link or non-regular file in the protected operator notification store.",
                exception),
            FileSystemFaultKind.PermissionDenied => new UnauthorizedAccessException(
                "Refusing to access an unreadable directory or file in the protected operator notification store.",
                exception),
            _ => new IOException("Durable operator notification store file operation failed.", exception)
        };
}

/// <summary>
/// Persists the last emitted site-local calendar date for the daily operator notification heartbeat as a tiny
/// fsync'd, 0600 atomic JSON checkpoint under the agent state directory so restarts do not re-emit the same day's
/// heartbeat. Writes zero files while notifications are disabled or before a heartbeat is emitted.
/// </summary>
public sealed class FileOperatorNotificationHeartbeatStore(string stateDirectory) : IOperatorNotificationHeartbeatStore
{
    public const string HeartbeatFileName = "operator-notifications-heartbeat.json";
    public const int MaximumCheckpointBytes = 4 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _stateDirectory = !string.IsNullOrWhiteSpace(stateDirectory)
        ? stateDirectory
        : throw new ArgumentException("State directory is required.", nameof(stateDirectory));

    internal string FilePath => Path.Combine(_stateDirectory, HeartbeatFileName);

    public async ValueTask<DateOnly?> ReadLastHeartbeatSiteDateAsync(CancellationToken cancellationToken)
    {
        var inspection = NotificationStateFilePublisher.InspectStateFilePath(_stateDirectory, HeartbeatFileName);
        if (inspection.Status == NotificationPathStatus.ConfirmedAbsent)
        {
            return null;
        }
        if (inspection.Status == NotificationPathStatus.Unreadable || inspection.TargetPath is null)
        {
            if (string.Equals(inspection.ExceptionTypeName, nameof(UnauthorizedAccessException), StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Operator notification heartbeat checkpoint path cannot be accessed.");
            }
            throw new IOException(inspection.IsSymlink
                ? "Refusing to access a symbolic link in the operator notification heartbeat checkpoint."
                : "Operator notification heartbeat checkpoint path is unreadable or not a regular file.");
        }

        var readResult = await NotificationStateFilePublisher.ReadBoundedBytesAsync(
            inspection.TargetPath,
            MaximumCheckpointBytes,
            cancellationToken).ConfigureAwait(false);
        if (readResult.ExceededLimit)
        {
            throw new InvalidDataException("Operator notification heartbeat checkpoint exceeds maximum allowed size.");
        }

        var span = readResult.Content.Span;
        var utf8Bom = Encoding.UTF8.Preamble;
        if (span.StartsWith(utf8Bom))
        {
            span = span[utf8Bom.Length..];
        }

        if (IsAsciiWhitespaceOnly(span))
        {
            throw new InvalidDataException("Operator notification heartbeat checkpoint is empty.");
        }

        var payload = JsonSerializer.Deserialize<HeartbeatCheckpoint>(span, JsonOptions);
        if (payload is null ||
            string.IsNullOrWhiteSpace(payload.LastSiteDate) ||
            !DateOnly.TryParseExact(payload.LastSiteDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            throw new InvalidDataException("Operator notification heartbeat checkpoint is malformed.");
        }
        return parsed;
    }

    private static bool IsAsciiWhitespaceOnly(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }
        return true;
    }

    public async ValueTask WriteLastHeartbeatSiteDateAsync(DateOnly siteDate, CancellationToken cancellationToken)
    {
        var payload = new HeartbeatCheckpoint(siteDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions));
        await NotificationStateFilePublisher.PublishRestrictedAtomicAsync(
            _stateDirectory,
            HeartbeatFileName,
            bytes,
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record HeartbeatCheckpoint(string? LastSiteDate);
}
