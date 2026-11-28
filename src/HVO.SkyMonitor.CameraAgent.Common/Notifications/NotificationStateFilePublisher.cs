using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.LocalFiles;

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

/// <summary>Outcome of a descriptor-verified protected state-file read.</summary>
public enum ProtectedStateFileReadStatus
{
    Valid = 0,
    ConfirmedAbsent = 1,
    ExceededLimit = 2,
    Unreadable = 3
}

/// <summary>
/// Result of walking ancestors and reading a protected state file directly from a pinned parent directory
/// descriptor via <c>openat(O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)</c> + <c>fstat(fd)</c> without
/// a second pathname open.
/// </summary>
public readonly record struct ProtectedStateFileReadResult(
    ProtectedStateFileReadStatus Status,
    ReadOnlyMemory<byte> Content,
    string? TargetPath,
    string? ReasonTag,
    string? ExceptionTypeName,
    bool CanReplace,
    bool IsSymlink,
    long SizeBytes = 0);

/// <summary>
/// Restricted (0700/0600 on Unix), symlink-refusing, fsync'd atomic file publication helper backed by
/// <see cref="HVO.SkyMonitor.LocalFiles"/> primitives for operator notification state and checkpoints.
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
    // Never caches allowSymlinkedParent across retries; each attempt classifies ancestors fresh via a single
    // non-following observation.
    private static readonly ConcurrentDictionary<string, byte> PendingParentSyncDirectories = new(DirectoryPathComparer);

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

        // 1. Walk proper ancestors top-down using a SINGLE non-following metadata observation per segment
        //    so a normal directory is never reclassified as a symlink by a second pathname probe.
        if (TryInspectAncestorsTopDownFailure(fullDirectory, out var ancestorFailure))
        {
            return ancestorFailure;
        }

        // 2. Inspect StateDirectory itself using a single non-following observation (symlinks are never permitted).
        switch (SecureFileSystem.InspectPathNoFollow(fullDirectory))
        {
            case PathEntryKind.NotFound:
                return new NotificationPathInspection(
                    NotificationPathStatus.ConfirmedAbsent, null, null, null, IsSymlink: false);
            case PathEntryKind.Directory:
                break;
            case PathEntryKind.SymbolicLink:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, "state-dir-symlink", nameof(IOException), IsSymlink: true);
            case PathEntryKind.PermissionDenied:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable,
                    null,
                    $"state-dir:{nameof(UnauthorizedAccessException)}",
                    nameof(UnauthorizedAccessException),
                    IsSymlink: false);
            case PathEntryKind.NotDirectory:
            case PathEntryKind.RegularFile:
            case PathEntryKind.Other:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, "state-dir-not-directory", nameof(IOException), IsSymlink: false);
            default:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, $"state-dir:{nameof(IOException)}", nameof(IOException), IsSymlink: false);
        }

        // 3. Inspect target file inside the verified StateDirectory using a single non-following observation.
        var targetPath = Path.Combine(fullDirectory, fileName);
        switch (SecureFileSystem.InspectPathNoFollow(targetPath))
        {
            case PathEntryKind.NotFound:
                return new NotificationPathInspection(
                    NotificationPathStatus.ConfirmedAbsent, null, null, null, IsSymlink: false);
            case PathEntryKind.RegularFile:
                break;
            case PathEntryKind.SymbolicLink:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, "target-symlink", nameof(IOException), IsSymlink: true);
            case PathEntryKind.Directory:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, "target-directory", nameof(IOException), IsSymlink: false);
            case PathEntryKind.PermissionDenied:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable,
                    null,
                    $"target-stat:{nameof(UnauthorizedAccessException)}",
                    nameof(UnauthorizedAccessException),
                    IsSymlink: false);
            case PathEntryKind.NotDirectory:
            case PathEntryKind.Other:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, "target-device", nameof(IOException), IsSymlink: false);
            default:
                return new NotificationPathInspection(
                    NotificationPathStatus.Unreadable, null, $"target-stat:{nameof(IOException)}", nameof(IOException), IsSymlink: false);
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

    private static bool TryInspectAncestorsTopDownFailure(string fullDirectory, out NotificationPathInspection failure)
    {
        foreach (var ancestor in GetAncestorsTopDown(fullDirectory))
        {
            var entry = SecureFileSystem.InspectPathNoFollow(ancestor);
            switch (entry)
            {
                case PathEntryKind.NotFound:
                    // Every existing ancestor above this segment is a verified traversable directory;
                    // ENOENT from non-following inspection is genuine absence (dangling symlinks report SymbolicLink).
                    failure = new NotificationPathInspection(
                        NotificationPathStatus.ConfirmedAbsent, null, null, null, IsSymlink: false);
                    return true;
                case PathEntryKind.Directory:
                    continue;
                case PathEntryKind.SymbolicLink:
                    {
                        var resolved = SecureFileSystem.InspectPathFollow(ancestor);
                        if (resolved == PathEntryKind.Directory)
                        {
                            continue;
                        }
                        if (resolved == PathEntryKind.PermissionDenied)
                        {
                            failure = new NotificationPathInspection(
                                NotificationPathStatus.Unreadable,
                                null,
                                $"state-dir-ancestor:{nameof(UnauthorizedAccessException)}",
                                nameof(UnauthorizedAccessException),
                                IsSymlink: true);
                            return true;
                        }
                        failure = new NotificationPathInspection(
                            NotificationPathStatus.Unreadable,
                            null,
                            "state-dir-ancestor-symlink-not-dir",
                            nameof(IOException),
                            IsSymlink: true);
                        return true;
                    }
                case PathEntryKind.PermissionDenied:
                    failure = new NotificationPathInspection(
                        NotificationPathStatus.Unreadable,
                        null,
                        $"state-dir-ancestor:{nameof(UnauthorizedAccessException)}",
                        nameof(UnauthorizedAccessException),
                        IsSymlink: false);
                    return true;
                case PathEntryKind.NotDirectory:
                case PathEntryKind.RegularFile:
                case PathEntryKind.Other:
                    failure = new NotificationPathInspection(
                        NotificationPathStatus.Unreadable,
                        null,
                        "state-dir-ancestor-not-directory",
                        nameof(IOException),
                        IsSymlink: false);
                    return true;
                default:
                    failure = new NotificationPathInspection(
                        NotificationPathStatus.Unreadable,
                        null,
                        $"state-dir-ancestor:{nameof(IOException)}",
                        nameof(IOException),
                        IsSymlink: false);
                    return true;
            }
        }

        failure = default;
        return false;
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

    public static void EnsureRestrictedDirectory(string directoryPath)
        => EnsureRestrictedDirectory(directoryPath, onDirectorySynced: null, directorySyncAction: null, beforeAnchorOpen: null, afterAncestorClassified: null, beforeDirectorySync: null);

    internal static void EnsureRestrictedDirectory(string directoryPath, Action<string>? onDirectorySynced)
        => EnsureRestrictedDirectory(directoryPath, onDirectorySynced, directorySyncAction: null, beforeAnchorOpen: null, afterAncestorClassified: null, beforeDirectorySync: null);

    internal static void EnsureRestrictedDirectory(
        string directoryPath,
        Action<string>? onDirectorySynced,
        Action<string>? directorySyncAction,
        Action<string>? beforeAnchorOpen = null,
        Action<string>? afterAncestorClassified = null,
        Action<string, bool>? beforeDirectorySync = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (string.IsNullOrEmpty(fullDirectory))
        {
            fullDirectory = Path.GetPathRoot(Path.GetFullPath(directoryPath)) ?? directoryPath;
        }

        var (missingDirectories, validatedSymlinkAncestors) = CollectMissingDirectoriesAndValidateAncestors(
            fullDirectory,
            afterAncestorClassified);
        var newlyCreatedDirectories = new HashSet<string>(DirectoryPathComparer);
        var chainBottomUp = GetSelfAndAncestorsBottomUp(fullDirectory);

        for (var i = 0; i < missingDirectories.Count; i++)
        {
            var missingDirectory = missingDirectories[i];
            VerifyPathIsNotSymlink(missingDirectory);
            // Record pending parent sync BEFORE creating the directory so any retry or concurrent publisher
            // observing the newly created directory on disk still syncs its parent before acknowledging.
            VerifiedParentSyncedDirectories.TryRemove(missingDirectory, out _);
            PendingParentSyncDirectories[missingDirectory] = 0;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            // Pin the pre-existing parent directory once (enforcing O_DIRECTORY|O_NOFOLLOW unless the anchor
            // itself was already validated as a pre-existing symlinked ancestor such as /var/run or /tmp) and
            // walk/create/harden every protected descendant component strictly relative to its pinned parent
            // descriptor (mkdirat + openat(O_NOFOLLOW|O_DIRECTORY) + fstat/statx + fchmod(0700)). Once the
            // trusted anchor descriptor is pinned, post-pin ancestor replacement or descendant symlink
            // substitution cannot direct mkdirat or fchmod outside the pinned subtree; acquiring the initial
            // anchor descriptor by pathname still relies on non-attacker-controlled ancestors above the anchor.
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

                    // Do NOT re-probe anchorParent by pathname here: doing so would allow a symlink substituted
                    // after validation to disable O_NOFOLLOW. Only pre-existing symlinked ancestors validated
                    // during the initial ancestor walk may bypass O_NOFOLLOW on anchor acquisition.
                    var allowSymlinkedAnchor = validatedSymlinkAncestors.Contains(anchorParent);
                    beforeAnchorOpen?.Invoke(anchorParent);
                    SecureFileSystem.EnsureAndHardenDirectoryChain(
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
            var isPending = PendingParentSyncDirectories.ContainsKey(current);
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
                // Always derive allowSymlinkedParent strictly from the CURRENT operation's single-observation
                // ancestor walk (never a stale cached boolean from an earlier failed attempt).
                var allowSymlinkedParent = !parentIsNewlyCreatedOrPending &&
                    validatedSymlinkAncestors.Contains(parent);
                var allowBestEffortPermissionDenied = !parentIsNewlyCreatedOrPending;
                beforeDirectorySync?.Invoke(parent, allowSymlinkedParent);
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
                SecureFileSystem.SetDirectoryModeNoFollow(directoryPath, RestrictedDirectoryMode);
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

    private static (List<string> MissingDirectories, HashSet<string> ValidatedSymlinkAncestors)
        CollectMissingDirectoriesAndValidateAncestors(
            string fullDirectory,
            Action<string>? afterAncestorClassified = null)
    {
        var ancestors = GetAncestorsTopDown(fullDirectory);
        var missingDirectories = new List<string>();
        var validatedSymlinkAncestors = new HashSet<string>(DirectoryPathComparer);

        for (var i = 0; i < ancestors.Count; i++)
        {
            var ancestor = ancestors[i];
            // Single non-following metadata observation per ancestor: a normal directory is classified
            // immediately and NEVER re-probed by pathname (preventing a race from reclassifying it as
            // a permitted symlink and disabling O_NOFOLLOW).
            var entry = SecureFileSystem.InspectPathNoFollow(ancestor);
            switch (entry)
            {
                case PathEntryKind.NotFound:
                    for (var j = i; j < ancestors.Count; j++)
                    {
                        missingDirectories.Add(ancestors[j]);
                    }
                    missingDirectories.Add(fullDirectory);
                    return (missingDirectories, validatedSymlinkAncestors);
                case PathEntryKind.Directory:
                    afterAncestorClassified?.Invoke(ancestor);
                    break;
                case PathEntryKind.SymbolicLink:
                    {
                        var resolved = SecureFileSystem.InspectPathFollow(ancestor);
                        if (resolved == PathEntryKind.PermissionDenied)
                        {
                            throw new UnauthorizedAccessException(
                                "Refusing to access an unreadable ancestor directory in the protected operator notification store.");
                        }
                        if (resolved != PathEntryKind.Directory)
                        {
                            throw new IOException(
                                "Refusing to access a symbolic link to a non-directory ancestor in the protected operator notification store.");
                        }
                        validatedSymlinkAncestors.Add(ancestor);
                        break;
                    }
                case PathEntryKind.PermissionDenied:
                    throw new UnauthorizedAccessException(
                        "Refusing to access an unreadable ancestor directory in the protected operator notification store.");
                case PathEntryKind.NotDirectory:
                case PathEntryKind.RegularFile:
                case PathEntryKind.Other:
                    throw new IOException(
                        "Ancestor path in the protected operator notification store is not a directory.");
                default:
                    throw new IOException(
                        "Refusing to access an unreadable ancestor directory in the protected operator notification store.");
            }
        }

        switch (SecureFileSystem.InspectPathNoFollow(fullDirectory))
        {
            case PathEntryKind.NotFound:
                missingDirectories.Add(fullDirectory);
                break;
            case PathEntryKind.Directory:
                break;
            case PathEntryKind.SymbolicLink:
                throw new IOException("Refusing to access a symbolic link in the protected operator notification store.");
            case PathEntryKind.PermissionDenied:
                throw new UnauthorizedAccessException(
                    "Refusing to access an unreadable directory in the protected operator notification store.");
            case PathEntryKind.NotDirectory:
            case PathEntryKind.RegularFile:
            case PathEntryKind.Other:
                throw new IOException("Protected operator notification store directory path is not a directory.");
            default:
                throw new IOException("Refusing to access an unreadable directory in the protected operator notification store.");
        }

        return (missingDirectories, validatedSymlinkAncestors);
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
        // Only pre-existing symlinked ancestors already validated during the single-observation ancestor walk
        // pass allowSymlinkedAncestor=true; append "/." on Linux without re-probing the pathname so O_NOFOLLOW
        // applies to '.' while still failing closed if the resolved target is not a directory.
        var syncPath = allowSymlinkedAncestor && OperatingSystem.IsLinux()
            ? Path.Combine(directoryPath, ".")
            : directoryPath;

        try
        {
            if (directorySyncAction is not null)
            {
                directorySyncAction(syncPath);
            }
            else
            {
                if (!allowSymlinkedAncestor && OperatingSystem.IsMacOS())
                {
                    SecureFileSystem.VerifyDirectoryNoFollow(directoryPath);
                }
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

    /// <summary>
    /// Walks ancestors top-down via a single non-following observation per segment, pins
    /// <paramref name="directoryPath"/> with <c>O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c> on POSIX, opens
    /// <paramref name="fileName"/> relative to that directory descriptor via
    /// <c>openat(O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)</c>, verifies <c>S_ISREG</c> and
    /// <c>st_size &lt;= maxBytes</c> on the opened descriptor via <c>fstat</c>/<c>statx</c>, and reads at most
    /// <paramref name="maxBytes"/> bytes directly from that verified descriptor.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Protected state reads must classify filesystem errors deterministically so callers fail closed without falling back.")]
    public static ProtectedStateFileReadResult ReadProtectedStateFile(
        string directoryPath,
        string fileName,
        int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var fullDirectory = NormalizeDirectoryPath(directoryPath);
        if (TryInspectAncestorsTopDownFailure(fullDirectory, out var ancestorFailure))
        {
            return FromAncestorFailure(ancestorFailure);
        }

        var targetPath = Path.Combine(fullDirectory, fileName);
        using var openResult = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(fullDirectory, fileName, maxBytes);
        if (openResult.Status != RegularFileOpenStatus.Opened)
        {
            return MapFailedOpen(openResult, targetPath);
        }

        try
        {
            using var stream = CreateReadStreamFromVerifiedHandle(openResult, maxBytes);
            var bounded = ReadBoundedStream(stream, maxBytes);
            if (bounded.ExceededLimit)
            {
                return new ProtectedStateFileReadResult(
                    ProtectedStateFileReadStatus.ExceededLimit,
                    ReadOnlyMemory<byte>.Empty,
                    targetPath,
                    "oversized",
                    nameof(InvalidDataException),
                    CanReplace: false,
                    IsSymlink: false,
                    SizeBytes: openResult.SizeBytes);
            }

            return new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Valid,
                bounded.Content,
                targetPath,
                null,
                null,
                CanReplace: true,
                IsSymlink: false,
                SizeBytes: bounded.Content.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var exceptionName = ex.GetType().Name;
            return new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                targetPath,
                $"read:{exceptionName}",
                exceptionName,
                CanReplace: true,
                IsSymlink: false,
                SizeBytes: openResult.SizeBytes);
        }
    }

    /// <summary>
    /// Asynchronous descriptor-verified protected state-file read using a single pinned parent directory descriptor,
    /// <c>openat(O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)</c>, <c>fstat</c>/<c>statx</c> (<c>S_ISREG</c> and
    /// <c>st_size &lt;= maxBytes</c>), and bounded streaming directly from the opened descriptor.
    /// </summary>
    public static ValueTask<ProtectedStateFileReadResult> ReadProtectedStateFileAsync(
        string directoryPath,
        string fileName,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        return ReadProtectedStateFileCoreAsync(directoryPath, fileName, maxBytes, cancellationToken);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Protected state reads must classify filesystem errors deterministically so callers fail closed without falling back.")]
    private static async ValueTask<ProtectedStateFileReadResult> ReadProtectedStateFileCoreAsync(
        string directoryPath,
        string fileName,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fullDirectory = NormalizeDirectoryPath(directoryPath);
        if (TryInspectAncestorsTopDownFailure(fullDirectory, out var ancestorFailure))
        {
            return FromAncestorFailure(ancestorFailure);
        }

        var targetPath = Path.Combine(fullDirectory, fileName);
        using var openResult = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(fullDirectory, fileName, maxBytes);
        if (openResult.Status != RegularFileOpenStatus.Opened)
        {
            return MapFailedOpen(openResult, targetPath);
        }

        try
        {
            var stream = CreateReadStreamFromVerifiedHandle(openResult, maxBytes);
            await using (stream.ConfigureAwait(false))
            {
                var bounded = await ReadBoundedStreamAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
                if (bounded.ExceededLimit)
                {
                    return new ProtectedStateFileReadResult(
                        ProtectedStateFileReadStatus.ExceededLimit,
                        ReadOnlyMemory<byte>.Empty,
                        targetPath,
                        "oversized",
                        nameof(InvalidDataException),
                        CanReplace: false,
                        IsSymlink: false,
                        SizeBytes: openResult.SizeBytes);
                }

                return new ProtectedStateFileReadResult(
                    ProtectedStateFileReadStatus.Valid,
                    bounded.Content,
                    targetPath,
                    null,
                    null,
                    CanReplace: true,
                    IsSymlink: false,
                    SizeBytes: bounded.Content.Length);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var exceptionName = ex.GetType().Name;
            return new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                targetPath,
                $"read:{exceptionName}",
                exceptionName,
                CanReplace: true,
                IsSymlink: false,
                SizeBytes: openResult.SizeBytes);
        }
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
        cancellationToken.ThrowIfCancellationRequested();
        var (directoryPath, fileName, fullPath) = SplitAbsoluteFilePath(filePath);
        using var openResult = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(directoryPath, fileName, maxBytes);
        if (openResult.Status == RegularFileOpenStatus.FileOversized)
        {
            return new BoundedFileReadResult(ExceededLimit: true, Content: ReadOnlyMemory<byte>.Empty);
        }
        if (openResult.Status != RegularFileOpenStatus.Opened)
        {
            throw CreateReadOpenException(openResult.Status, fullPath);
        }

        var stream = CreateReadStreamFromVerifiedHandle(openResult, maxBytes);
        await using (stream.ConfigureAwait(false))
        {
            return await ReadBoundedStreamAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    public static BoundedFileReadResult ReadBoundedBytes(string filePath, int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var (directoryPath, fileName, fullPath) = SplitAbsoluteFilePath(filePath);
        using var openResult = SecureFileSystem.OpenRegularFileReadOnlyNoFollow(directoryPath, fileName, maxBytes);
        if (openResult.Status == RegularFileOpenStatus.FileOversized)
        {
            return new BoundedFileReadResult(ExceededLimit: true, Content: ReadOnlyMemory<byte>.Empty);
        }
        if (openResult.Status != RegularFileOpenStatus.Opened)
        {
            throw CreateReadOpenException(openResult.Status, fullPath);
        }

        using var stream = CreateReadStreamFromVerifiedHandle(openResult, maxBytes);
        return ReadBoundedStream(stream, maxBytes);
    }

    private static string NormalizeDirectoryPath(string directoryPath)
    {
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (string.IsNullOrEmpty(fullDirectory))
        {
            fullDirectory = Path.GetPathRoot(Path.GetFullPath(directoryPath)) ?? directoryPath;
        }
        return fullDirectory;
    }

    private static (string DirectoryPath, string FileName, string FullPath) SplitAbsoluteFilePath(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        var directoryPath = Path.GetDirectoryName(fullPath);
        var fileName = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(directoryPath) || string.IsNullOrWhiteSpace(fileName))
        {
            throw new IOException("Protected operator notification state file path is invalid.");
        }
        return (directoryPath, fileName, fullPath);
    }

    private static FileStream CreateReadStreamFromVerifiedHandle(RegularFileOpenResult openResult, int maxBytes)
    {
        var handle = openResult.DetachHandle();
        try
        {
            return new FileStream(
                handle,
                FileAccess.Read,
                bufferSize: Math.Min(maxBytes + 1, 16 * 1024),
                isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static ProtectedStateFileReadResult FromAncestorFailure(NotificationPathInspection ancestorFailure)
    {
        if (ancestorFailure.Status == NotificationPathStatus.ConfirmedAbsent)
        {
            return new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.ConfirmedAbsent,
                ReadOnlyMemory<byte>.Empty,
                null,
                null,
                null,
                CanReplace: true,
                IsSymlink: false);
        }

        return new ProtectedStateFileReadResult(
            ProtectedStateFileReadStatus.Unreadable,
            ReadOnlyMemory<byte>.Empty,
            null,
            ancestorFailure.ReasonTag ?? "state-dir-ancestor-unreadable",
            ancestorFailure.ExceptionTypeName ?? nameof(IOException),
            CanReplace: false,
            IsSymlink: ancestorFailure.IsSymlink);
    }

    private static ProtectedStateFileReadResult MapFailedOpen(RegularFileOpenResult openResult, string targetPath) =>
        openResult.Status switch
        {
            RegularFileOpenStatus.NotFound => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.ConfirmedAbsent,
                ReadOnlyMemory<byte>.Empty,
                null,
                null,
                null,
                CanReplace: true,
                IsSymlink: false),
            RegularFileOpenStatus.FileOversized => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.ExceededLimit,
                ReadOnlyMemory<byte>.Empty,
                targetPath,
                "oversized",
                nameof(InvalidDataException),
                CanReplace: false,
                IsSymlink: false,
                SizeBytes: openResult.SizeBytes),
            RegularFileOpenStatus.DirectorySymbolicLink => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                null,
                "state-dir-symlink",
                nameof(IOException),
                CanReplace: false,
                IsSymlink: true),
            RegularFileOpenStatus.DirectoryNotDirectory => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                null,
                "state-dir-not-directory",
                nameof(IOException),
                CanReplace: false,
                IsSymlink: false),
            RegularFileOpenStatus.DirectoryPermissionDenied => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                null,
                $"state-dir:{nameof(UnauthorizedAccessException)}",
                nameof(UnauthorizedAccessException),
                CanReplace: false,
                IsSymlink: false),
            RegularFileOpenStatus.FileSymbolicLink => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                null,
                "target-symlink",
                nameof(IOException),
                CanReplace: false,
                IsSymlink: true),
            RegularFileOpenStatus.FileIsDirectory => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                null,
                "target-directory",
                nameof(IOException),
                CanReplace: false,
                IsSymlink: false),
            RegularFileOpenStatus.FileNonRegular => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                null,
                "target-device",
                nameof(IOException),
                CanReplace: false,
                IsSymlink: false),
            RegularFileOpenStatus.FilePermissionDenied => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                targetPath,
                $"read:{nameof(UnauthorizedAccessException)}",
                nameof(UnauthorizedAccessException),
                CanReplace: true,
                IsSymlink: false,
                SizeBytes: openResult.SizeBytes),
            _ => new ProtectedStateFileReadResult(
                ProtectedStateFileReadStatus.Unreadable,
                ReadOnlyMemory<byte>.Empty,
                targetPath,
                $"target-open:{nameof(IOException)}",
                nameof(IOException),
                CanReplace: false,
                IsSymlink: false)
        };

    private static Exception CreateReadOpenException(RegularFileOpenStatus status, string fullPath) =>
        status switch
        {
            RegularFileOpenStatus.NotFound => new FileNotFoundException(
                "Protected operator notification state file was not found.", fullPath),
            RegularFileOpenStatus.DirectoryPermissionDenied or RegularFileOpenStatus.FilePermissionDenied =>
                new UnauthorizedAccessException(
                    "Refusing to access an unreadable directory or file in the protected operator notification store."),
            RegularFileOpenStatus.DirectorySymbolicLink or RegularFileOpenStatus.FileSymbolicLink =>
                new IOException(
                    "Refusing to access a symbolic link in the protected operator notification store."),
            RegularFileOpenStatus.DirectoryNotDirectory or RegularFileOpenStatus.FileIsDirectory or RegularFileOpenStatus.FileNonRegular =>
                new IOException(
                    "Refusing to access a non-regular file or directory in the protected operator notification store."),
            _ => new IOException("Protected operator notification state file could not be opened.")
        };

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
                        SecureFileSystem.SetFileMode(stream.SafeFileHandle, RestrictedFileMode, tempPath);
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
        var entry = SecureFileSystem.InspectPathNoFollow(path);
        if (entry == PathEntryKind.SymbolicLink)
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
        var readResult = await NotificationStateFilePublisher.ReadProtectedStateFileAsync(
            _stateDirectory,
            HeartbeatFileName,
            MaximumCheckpointBytes,
            cancellationToken).ConfigureAwait(false);
        if (readResult.Status == ProtectedStateFileReadStatus.ConfirmedAbsent)
        {
            return null;
        }
        if (readResult.Status == ProtectedStateFileReadStatus.ExceededLimit)
        {
            throw new InvalidDataException("Operator notification heartbeat checkpoint exceeds maximum allowed size.");
        }
        if (readResult.Status != ProtectedStateFileReadStatus.Valid)
        {
            if (string.Equals(readResult.ExceptionTypeName, nameof(UnauthorizedAccessException), StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Operator notification heartbeat checkpoint path cannot be accessed.");
            }
            throw new IOException(readResult.IsSymlink
                ? "Refusing to access a symbolic link in the operator notification heartbeat checkpoint."
                : "Operator notification heartbeat checkpoint path is unreadable or not a regular file.");
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
