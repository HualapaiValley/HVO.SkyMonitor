using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>
/// The two durability boundaries a filesystem offers: flushing a file's data to its medium, and
/// flushing a directory's entries so that a rename or create is itself durable. Neither is a
/// policy about when to call them; a host's publication sequence decides that.
/// </summary>
/// <remarks>
/// Directory synchronization is a Linux mechanism (<c>fsync</c> on a descriptor opened with
/// <c>O_DIRECTORY</c>). On other platforms it is a no-op, reported through
/// <see cref="SupportsDirectorySync"/> so a caller can refuse to claim durability it did not get.
/// </remarks>
public static class DurableSync
{
    /// <summary>True where <see cref="Directory(string)"/> performs a real flush.</summary>
    public static bool SupportsDirectorySync => OperatingSystem.IsLinux();

    /// <summary>
    /// Flush a file's written data and metadata to its medium. The handle must be open for
    /// writing on Windows and may be read-only elsewhere.
    /// </summary>
    public static void File(SafeFileHandle handle, string pathForDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("sync-file", pathForDiagnostics, exception);
        }
    }

    /// <summary>
    /// Open a file by path without following a final symbolic link and flush it. On Linux the
    /// open uses <c>O_NOFOLLOW</c> so a link substituted between resolution and sync is refused
    /// rather than followed; elsewhere the caller's containment check is the only guard.
    /// </summary>
    public static void File(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (OperatingSystem.IsLinux())
        {
            using var handle = OpenLinux(absolutePath, LinuxOpenFlags.ReadOnly | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec, "sync-file");
            File(handle, absolutePath);
            return;
        }
        try
        {
            using var handle = System.IO.File.OpenHandle(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            File(handle, absolutePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("sync-file", absolutePath, exception);
        }
    }

    /// <summary>
    /// Flush a directory's entries to its medium so that entries created, renamed, or removed
    /// in it are durable. No-op where <see cref="SupportsDirectorySync"/> is false.
    /// </summary>
    public static void Directory(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!OperatingSystem.IsLinux())
        {
            return;
        }
        using var handle = OpenLinux(
            absolutePath,
            LinuxOpenFlags.ReadOnly | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec,
            "sync-directory");
        try
        {
            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("sync-directory", absolutePath, exception);
        }
    }

    /// <summary>
    /// Flush every directory from <paramref name="absolutePath"/> up to and including the root.
    /// Used after creating a directory chain so that each new entry is durable in its parent.
    /// Returns the number of directories flushed (0 where directory sync is unsupported).
    /// </summary>
    public static int DirectoryChain(PhysicalRoot root, string absolutePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!OperatingSystem.IsLinux())
        {
            return 0;
        }
        var count = 0;
        var current = new DirectoryInfo(System.IO.Path.GetFullPath(absolutePath));
        while (current is not null)
        {
            Directory(current.FullName);
            count++;
            if (string.Equals(System.IO.Path.TrimEndingDirectorySeparator(current.FullName), root.Path, StringComparison.Ordinal))
            {
                return count;
            }
            if (!root.IsInside(current.FullName))
            {
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, "sync-directory-chain", absolutePath);
            }
            current = current.Parent;
        }
        throw new FileSystemFaultException(FileSystemFaultKind.Containment, "sync-directory-chain", absolutePath);
    }

    private static SafeFileHandle OpenLinux(string path, int flags, string operation)
    {
        var descriptor = NativeOpen(path, flags);
        if (descriptor < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            var kind = errno switch
            {
                2 => FileSystemFaultKind.NotFound,
                13 or 1 => FileSystemFaultKind.PermissionDenied,
                40 or 20 => FileSystemFaultKind.Containment, // ELOOP (symlink under O_NOFOLLOW), ENOTDIR
                _ => FileSystemFaultKind.Io
            };
            throw new FileSystemFaultException(kind, operation, path, new Win32Exception(errno));
        }
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

#pragma warning disable SYSLIB1054 // A single narrow libc call; source-generated interop would require unsafe code for no benefit.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeOpen(string path, int flags);
#pragma warning restore SYSLIB1054
}
