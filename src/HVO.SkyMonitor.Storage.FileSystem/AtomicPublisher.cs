namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>
/// Writes a file so that it is either fully present with its content durable, or absent, and
/// never observable half-written. The sequence is the one the CameraAgent raw-ingress store has
/// used in production: write to a same-directory temporary name, flush the file to its medium,
/// rename it onto the final name, then flush the directory so the rename is durable. The
/// temporary lives beside the target on purpose: a rename across filesystems is not atomic.
/// </summary>
/// <remarks>
/// This class holds no policy. It does not decide whether an existing target may be replaced
/// (the caller passes <see cref="PublishMode"/>), how content is produced (the caller writes
/// to the stream it is handed), or what happens on failure beyond removing its own temporary.
/// Every step re-verifies containment, because a link can be substituted between steps by a
/// concurrent actor with write access to the tree.
/// </remarks>
public static class AtomicPublisher
{
    private const string TemporarySuffix = ".tmp";

    /// <summary>
    /// True where <see cref="PublishMode.Replace"/> is atomic with respect to readers. On Linux the
    /// rename is <c>rename(2)</c>, which replaces the directory entry in one step. On Windows the
    /// runtime uses <c>MoveFileEx(MOVEFILE_REPLACE_EXISTING)</c>, which is not guaranteed atomic when
    /// the target exists, so a host that needs the guarantee must check this and refuse to claim it.
    /// <see cref="PublishMode.CreateNew"/> is safe everywhere: the target either appears complete or
    /// not at all.
    /// </summary>
    public static bool SupportsAtomicReplace => OperatingSystem.IsLinux();

    /// <summary>
    /// Publish <paramref name="relativePath"/> under <paramref name="root"/>. The
    /// <paramref name="write"/> callback receives a stream positioned at zero and must write
    /// the complete content; the stream is flushed to the medium after it returns.
    /// Returns the canonical absolute path of the published file.
    /// </summary>
    public static async Task<string> PublishAsync(
        PhysicalRoot root,
        string relativePath,
        PublishMode mode,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(write);
        var finalPath = root.Resolve(relativePath);
        var directory = Path.GetDirectoryName(finalPath)
            ?? throw new FileSystemFaultException(FileSystemFaultKind.Containment, "publish", relativePath);

        EnsureDirectory(root, directory);

        var temporaryPath = string.Concat(finalPath, ".", Guid.NewGuid().ToString("N"), TemporarySuffix);
        try
        {
            try
            {
                using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough);
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                DurableSync.File(stream.SafeFileHandle, temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                throw FileSystemFaultException.From("publish-write", relativePath, exception);
            }

            root.Verify(directory, "publish-rename");
            try
            {
                File.Move(temporaryPath, finalPath, overwrite: mode == PublishMode.Replace);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw FileSystemFaultException.From("publish-rename", relativePath, exception);
            }
            root.Verify(finalPath, "publish-rename");
            DurableSync.Directory(directory);
            return finalPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Create <paramref name="absoluteDirectory"/> and any missing ancestors inside the root,
    /// verifying containment, and flush the created chain so the entries are durable. Existing
    /// directories are left as found.
    /// </summary>
    public static void EnsureDirectory(PhysicalRoot root, string absoluteDirectory)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.Verify(absoluteDirectory, "ensure-directory");
        var existed = Directory.Exists(absoluteDirectory);
        try
        {
            Directory.CreateDirectory(absoluteDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("ensure-directory", absoluteDirectory, exception);
        }
        root.Verify(absoluteDirectory, "ensure-directory");
        if (!existed)
        {
            DurableSync.DirectoryChain(root, absoluteDirectory);
        }
    }

    /// <summary>
    /// Remove temporaries this publisher left behind under <paramref name="absoluteDirectory"/>
    /// (a crash between write and rename leaves one). Bounded: at most
    /// <paramref name="maximum"/> entries are examined, and only names carrying this
    /// publisher's suffix are candidates. Returns the number removed.
    /// </summary>
    public static int CleanupTemporaries(PhysicalRoot root, string absoluteDirectory, int maximum)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        root.Verify(absoluteDirectory, "cleanup");
        var removed = 0;
        var examined = 0;
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFiles(absoluteDirectory, "*" + TemporarySuffix, SearchOption.TopDirectoryOnly);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("cleanup", absoluteDirectory, exception);
        }
        foreach (var entry in entries)
        {
            if (++examined > maximum)
            {
                break;
            }
            if (new FileInfo(entry).LinkTarget is not null)
            {
                continue; // never delete through a link, even one carrying our suffix
            }
            if (TryDelete(entry))
            {
                removed++;
            }
        }
        return removed;
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>Whether a publication may replace an existing target.</summary>
public enum PublishMode
{
    /// <summary>The target must not exist; an existing target fails with <see cref="FileSystemFaultKind.AlreadyExists"/>.</summary>
    CreateNew,

    /// <summary>
    /// An existing target is replaced. Where <see cref="AtomicPublisher.SupportsAtomicReplace"/> is
    /// true, readers see the old content or the new and never a mix; elsewhere the replacement is
    /// best-effort and a host must not report it as atomic.
    /// </summary>
    Replace
}
