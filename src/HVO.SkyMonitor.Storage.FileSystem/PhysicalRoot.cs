namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>
/// A physical directory that bounds every path a caller may touch. Resolution is canonical
/// (no <c>.</c>, <c>..</c>, or duplicate separators survive), rejects traversal through any
/// symbolic link or reparse point between the root and the target, and rejects a target that
/// is itself a link. The root must be physical: it may not be a link, and its own ancestors are
/// not inspected, because a caller who can point the root at a link can point it anywhere.
/// </summary>
/// <remarks>
/// Callers encode logical names (bucket, key) into a relative path before calling in; this
/// class knows nothing about what the path means. A valid logical name that happens to contain
/// characters a filesystem treats specially is the caller's responsibility to encode, and this
/// class will faithfully refuse the unencoded form when it escapes.
/// </remarks>
public sealed class PhysicalRoot
{
    private readonly StringComparison _comparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private PhysicalRoot(string path)
    {
        Path = path;
    }

    /// <summary>The canonical, separator-trimmed absolute path of the root.</summary>
    public string Path { get; }

    /// <summary>
    /// Bind to an existing directory. Fails with <see cref="FileSystemFaultKind.Containment"/> if
    /// the path is relative, is not a directory, or is a symbolic link; fails with
    /// <see cref="FileSystemFaultKind.NotFound"/> if it does not exist.
    /// </summary>
    public static PhysicalRoot Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "open-root", path);
        }
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        var info = new DirectoryInfo(full);
        if (!info.Exists)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "open-root", full);
        }
        if (info.LinkTarget is not null)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "open-root", full);
        }
        return new PhysicalRoot(full);
    }

    /// <summary>
    /// Resolve a relative path under the root to a canonical absolute path, proving that every
    /// existing component between the root and the target is a real directory (not a link) and
    /// that the result lies inside the root. Components that do not yet exist are permitted so
    /// that callers can resolve a path before creating it; they are re-checked by the primitive
    /// that creates them.
    /// </summary>
    public string Resolve(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (System.IO.Path.IsPathRooted(relativePath))
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "resolve", relativePath);
        }
        var candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        if (!IsInside(candidate))
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "resolve", relativePath);
        }
        EnsureNoLinksBetweenRootAnd(candidate, "resolve");
        return candidate;
    }

    /// <summary>
    /// Re-verify an already-resolved absolute path: inside the root, and no link anywhere from
    /// the root down to and including the target. Primitives call this after every step that
    /// could have raced with a concurrent link creation.
    /// </summary>
    public void Verify(string absolutePath, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!IsInside(absolutePath))
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, operation, absolutePath);
        }
        EnsureNoLinksBetweenRootAnd(absolutePath, operation);
    }

    /// <summary>True when <paramref name="absolutePath"/> is the root or lies beneath it, by canonical string prefix.</summary>
    public bool IsInside(string absolutePath)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(absolutePath));
        return string.Equals(full, Path, _comparison)
            || full.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, _comparison);
    }

    private void EnsureNoLinksBetweenRootAnd(string absolutePath, string operation)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(absolutePath));
        // The target itself, whether file or directory, may not be a link.
        if (new FileInfo(full).LinkTarget is not null || new DirectoryInfo(full).LinkTarget is not null)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, operation, absolutePath);
        }
        // Walk up from the target's directory to the root, refusing any link on the way.
        var current = new DirectoryInfo(Directory.Exists(full) ? full : System.IO.Path.GetDirectoryName(full)!);
        while (current is not null)
        {
            if (current.LinkTarget is not null)
            {
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, operation, absolutePath);
            }
            if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(current.FullName), Path, _comparison))
            {
                return;
            }
            current = current.Parent;
        }
        throw new FileSystemFaultException(FileSystemFaultKind.Containment, operation, absolutePath);
    }
}
