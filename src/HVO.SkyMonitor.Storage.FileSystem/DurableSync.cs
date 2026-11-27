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
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixDirectoryType = 0x4000;
    private const ushort UnixRegularFileType = 0x8000;
    private const int MacOsOpenReadOnly = 0;
    private const int MacOsOpenNoFollow = 0x0100;
    private const int MacOsOpenDirectory = 0x100000;
    private const int MacOsOpenCloseOnExec = 0x01000000;
    private const int MacOsOpenSearch = 0x40000000;
    private const int ErrnoPerm = 1;
    private const int ErrnoNoEnt = 2;
    private const int ErrnoIntr = 4;
    private const int ErrnoAccess = 13;
    private const int ErrnoExist = 17;
    private const int ErrnoNotDir = 20;
    private const int LinuxErrnoLoop = 40;
    private const int MacOsErrnoLoop = 62;
    /// <summary>True where <see cref="Directory(string)"/> performs a real flush.</summary>
    public static bool SupportsDirectorySync => OperatingSystem.IsLinux();

    /// <summary>Flush managed stream buffers and then the underlying file to its medium.</summary>
    public static void File(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
#pragma warning disable CA1849 // FlushAsync does not provide a flush-to-disk contract.
            if (OperatingSystem.IsLinux())
            {
                stream.Flush(flushToDisk: false);
                FlushLinux(stream.SafeFileHandle);
            }
            else
            {
                stream.Flush(flushToDisk: true);
            }
#pragma warning restore CA1849
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("sync-file", stream.Name, exception);
        }
    }

    /// <summary>
    /// Flush a file's written data and metadata to its medium. The handle must be open for
    /// writing on Windows and may be read-only elsewhere.
    /// </summary>
    public static void File(SafeFileHandle handle, string pathForDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(handle);
        try
        {
            if (OperatingSystem.IsLinux()) FlushLinux(handle);
            else RandomAccess.FlushToDisk(handle);
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
        => File(absolutePath, OperatingSystem.IsLinux());

    internal static void File(string absolutePath, bool isLinux)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (isLinux)
        {
            using var handle = OpenLinux(absolutePath, LinuxOpenFlags.ReadOnly | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec, "sync-file");
            File(handle, absolutePath);
            return;
        }
        try
        {
            using var handle = System.IO.File.OpenHandle(absolutePath, FileMode.Open,
                OperatingSystem.IsWindows() ? FileAccess.ReadWrite : FileAccess.Read, FileShare.Read);
            File(handle, absolutePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("sync-file", absolutePath, exception);
        }
    }

    /// <summary>
    /// Prove that <paramref name="absolutePath"/> is a regular Linux file without following
    /// links or blocking on a FIFO. Other platforms use the managed attributes available to
    /// them; callers that require the Linux guarantee must separately require Linux.
    /// </summary>
    public static void RequireRegularFile(string absolutePath)
        => RequireRegularFile(absolutePath, OperatingSystem.IsLinux());

    internal static void RequireRegularFile(string absolutePath, bool isLinux)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (isLinux)
        {
            if (StatX(
                    AtFileDescriptorCurrentWorkingDirectory,
                    absolutePath,
                    AtSymbolicLinkNoFollow,
                    StatxType,
                    out var status) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                throw new FileSystemFaultException(FileSystemFaultKind.Io, "inspect-file", absolutePath, new Win32Exception(errno));
            }
            if ((status.Mask & StatxType) != StatxType || (status.Mode & UnixFileTypeMask) != UnixRegularFileType)
            {
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, "inspect-file", absolutePath);
            }
            return;
        }
        var attributes = System.IO.File.GetAttributes(absolutePath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "inspect-file", absolutePath);
        }
    }

    /// <summary>
    /// Flush a directory's entries to its medium so that entries created, renamed, or removed
    /// in it are durable. No-op where <see cref="SupportsDirectorySync"/> is false.
    /// </summary>
    public static void Directory(string absolutePath)
        => Directory(absolutePath, OperatingSystem.IsLinux());

    internal static void Directory(string absolutePath, bool isLinux)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (!isLinux)
        {
            return;
        }
        using var handle = OpenLinux(
            absolutePath,
            LinuxOpenFlags.ReadOnly | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec,
            "sync-directory");
        try
        {
            FlushLinux(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("sync-directory", absolutePath, exception);
        }
    }

    /// <summary>Flush a directory through an already-authenticated Linux directory handle.</summary>
    public static void Directory(SafeFileHandle handle, string pathForDiagnostics)
        => Directory(handle, pathForDiagnostics, OperatingSystem.IsLinux());

    internal static void Directory(SafeFileHandle handle, string pathForDiagnostics, bool isLinux)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!isLinux)
        {
            return;
        }
        try
        {
            FlushLinux(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("sync-directory", pathForDiagnostics, exception);
        }
    }

    /// <summary>
    /// Flush every directory from <paramref name="absolutePath"/> up to and including the root.
    /// Used after creating a directory chain so that each new entry is durable in its parent.
    /// Returns the number of directories flushed (0 where directory sync is unsupported).
    /// </summary>
    public static int DirectoryChain(PhysicalRoot root, string absolutePath)
        => DirectoryChain(root, absolutePath, OperatingSystem.IsLinux());

    internal static int DirectoryChain(PhysicalRoot root, string absolutePath, bool isLinux)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!isLinux)
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

    /// <summary>
    /// Pins <paramref name="anchorParentDirectory"/> with an open directory descriptor and walks each
    /// protected descendant component in <paramref name="descendantComponents"/> relative to its pinned
    /// parent descriptor using <c>openat(2)</c> (<c>O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c>),
    /// <c>mkdirat(2)</c> when <paramref name="createMissing"/> is <c>true</c>, descriptor directory-type
    /// verification (<c>statx</c>/<c>fstat</c>), and <c>fchmod(2)</c>. Closing an ancestor replacement race
    /// ensures <c>fchmod</c> never alters permissions outside the verified directory chain.
    /// No-op on Windows (creates missing directories only when <paramref name="createMissing"/> is true).
    /// </summary>
    public static void EnsureAndHardenDirectoryChain(
        string anchorParentDirectory,
        IReadOnlyList<string> descendantComponents,
        UnixFileMode mode,
        bool createMissing = true,
        bool allowSymlinkedAnchor = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorParentDirectory);
        ArgumentNullException.ThrowIfNull(descendantComponents);
        if (!System.IO.Path.IsPathRooted(anchorParentDirectory))
        {
            throw new ArgumentException("Anchor parent directory must be an absolute path.", nameof(anchorParentDirectory));
        }
        if (descendantComponents.Count == 0)
        {
            throw new ArgumentException("At least one descendant component is required.", nameof(descendantComponents));
        }

        for (var i = 0; i < descendantComponents.Count; i++)
        {
            var component = descendantComponents[i];
            if (string.IsNullOrWhiteSpace(component) ||
                component is "." or ".." ||
                component.IndexOfAny(['/', '\\', '\0']) >= 0)
            {
                throw new ArgumentException(
                    $"Descendant component at index {i} must be a single non-empty directory name.",
                    nameof(descendantComponents));
            }
        }

        var normalizedAnchor = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(anchorParentDirectory));
        if (string.IsNullOrEmpty(normalizedAnchor))
        {
            normalizedAnchor = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(anchorParentDirectory)) ?? anchorParentDirectory;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var isMacOs = OperatingSystem.IsMacOS();
            var currentPath = normalizedAnchor;
            var parentHandle = OpenDirectoryAnchorUnix(normalizedAnchor, allowSymlinkedAnchor, isMacOs);
            try
            {
                VerifyDirectoryDescriptorUnix(parentHandle, currentPath, isMacOs);

                for (var i = 0; i < descendantComponents.Count; i++)
                {
                    var component = descendantComponents[i];
                    currentPath = System.IO.Path.Combine(currentPath, component);

                    if (!TryOpenDirectoryAtNoFollowUnix(parentHandle, component, currentPath, isMacOs, out var childHandle))
                    {
                        if (!createMissing)
                        {
                            throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "chmod-directory", currentPath);
                        }

                        CreateDirectoryAtUnix(parentHandle, component, mode, currentPath, isMacOs);
                        if (!TryOpenDirectoryAtNoFollowUnix(parentHandle, component, currentPath, isMacOs, out childHandle))
                        {
                            throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "chmod-directory", currentPath);
                        }
                    }

                    try
                    {
                        VerifyDirectoryDescriptorUnix(childHandle, currentPath, isMacOs);
                        FchmodUnix(childHandle, (uint)mode, "chmod-directory", currentPath, isMacOs);
                    }
                    catch
                    {
                        childHandle.Dispose();
                        throw;
                    }

                    parentHandle.Dispose();
                    parentHandle = childHandle;
                }
            }
            finally
            {
                parentHandle.Dispose();
            }
            return;
        }

        // Non-Linux/non-macOS fallback (Windows or other Unix platforms).
        try
        {
            var anchorInfo = new DirectoryInfo(normalizedAnchor);
            if (!anchorInfo.Exists)
            {
                throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "chmod-directory", normalizedAnchor);
            }
            if (!allowSymlinkedAnchor && (anchorInfo.LinkTarget is not null || (anchorInfo.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", normalizedAnchor);
            }

            var currentPath = normalizedAnchor;
            for (var i = 0; i < descendantComponents.Count; i++)
            {
                currentPath = System.IO.Path.Combine(currentPath, descendantComponents[i]);
                var dirInfo = new DirectoryInfo(currentPath);
                var fileInfo = new FileInfo(currentPath);
                if (dirInfo.LinkTarget is not null || fileInfo.LinkTarget is not null)
                {
                    throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", currentPath);
                }

                if (!dirInfo.Exists)
                {
                    if (fileInfo.Exists)
                    {
                        throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", currentPath);
                    }
                    if (!createMissing)
                    {
                        throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "chmod-directory", currentPath);
                    }
                    if (OperatingSystem.IsWindows())
                    {
                        System.IO.Directory.CreateDirectory(currentPath);
                    }
                    else
                    {
                        System.IO.Directory.CreateDirectory(currentPath, mode);
                    }
                    dirInfo.Refresh();
                }

                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    (dirInfo.Attributes & FileAttributes.Directory) == 0)
                {
                    throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", currentPath);
                }
                if (!OperatingSystem.IsWindows())
                {
                    System.IO.File.SetUnixFileMode(currentPath, mode);
                }
            }
        }
        catch (FileSystemFaultException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("chmod-directory", normalizedAnchor, exception);
        }
    }

    /// <summary>
    /// Restricts a directory's Unix permissions without following symbolic links by pinning its parent
    /// directory descriptor and opening the leaf component via <c>openat(2)</c> with
    /// <c>O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c>, verifying the descriptor is a directory,
    /// and applying <c>fchmod(2)</c> on the authenticated file descriptor (closing both symlink and
    /// ancestor-replacement TOCTOU windows). No-op on Windows.
    /// </summary>
    public static void SetDirectoryModeNoFollow(string absolutePath, UnixFileMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var trimmed = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(absolutePath));
            var parent = System.IO.Path.GetDirectoryName(trimmed);
            var leaf = System.IO.Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(leaf))
            {
                EnsureAndHardenDirectoryChain(parent, [leaf], mode, createMissing: false, allowSymlinkedAnchor: true);
                return;
            }

            var isMacOs = OperatingSystem.IsMacOS();
            var flags = GetOpenDirectoryNoFollowFlags(isMacOs);
            using var handle = OpenUnix(absolutePath, flags, "chmod-directory", isMacOs);
            VerifyDirectoryDescriptorUnix(handle, absolutePath, isMacOs);
            FchmodUnix(handle, (uint)mode, "chmod-directory", absolutePath, isMacOs);
            return;
        }

        try
        {
            var dirInfo = new DirectoryInfo(absolutePath);
            if (dirInfo.LinkTarget is not null || (dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", absolutePath);
            }
            if ((dirInfo.Attributes & FileAttributes.Directory) == 0)
            {
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", absolutePath);
            }
            System.IO.File.SetUnixFileMode(absolutePath, mode);
        }
        catch (FileSystemFaultException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw FileSystemFaultException.From("chmod-directory", absolutePath, exception);
        }
    }

    /// <summary>
    /// Applies Unix permissions directly to an already-open file descriptor via <c>fchmod(2)</c> on Linux
    /// and macOS (avoiding pathname <c>chmod(2)</c> symlink TOCTOU). No-op on Windows.
    /// </summary>
    public static void SetFileMode(SafeFileHandle handle, UnixFileMode mode, string pathForDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            FchmodUnix(handle, (uint)mode, "chmod-file", pathForDiagnostics, OperatingSystem.IsMacOS());
            return;
        }

        if (!string.IsNullOrWhiteSpace(pathForDiagnostics))
        {
            try
            {
                System.IO.File.SetUnixFileMode(pathForDiagnostics, mode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw FileSystemFaultException.From("chmod-file", pathForDiagnostics, exception);
            }
        }
    }

    // The pinned .NET 10 native FSync helper returns a positive boolean on failure.
    // Check libc directly so EIO/ENOSPC cannot become acknowledged durability. Keep the
    // SafeFileHandle pinned by the interop marshaller, including during EINTR retries.
    private static void FlushLinux(SafeFileHandle handle)
    {
        if (handle.IsInvalid) throw new ArgumentException("Invalid file handle.", nameof(handle));
        int result;
        int errno;
        do
        {
            result = NativeFsync(handle);
            errno = Marshal.GetLastPInvokeError();
        }
        while (result < 0 && errno == ErrnoIntr); // EINTR: no completed sync yet.
        if (result < 0)
        {
            throw new IOException(new Win32Exception(errno).Message, errno);
        }
    }

    private static int GetOpenDirectoryNoFollowFlags(bool isMacOs) =>
        isMacOs
            ? MacOsOpenReadOnly | MacOsOpenDirectory | MacOsOpenNoFollow | MacOsOpenCloseOnExec
            : LinuxOpenFlags.ReadOnly | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec;

    private static SafeFileHandle OpenDirectoryAnchorUnix(string anchorPath, bool allowSymlinkedAnchor, bool isMacOs)
    {
        var baseFlags = isMacOs
            ? MacOsOpenReadOnly | MacOsOpenDirectory | MacOsOpenCloseOnExec | (allowSymlinkedAnchor ? 0 : MacOsOpenNoFollow)
            : LinuxOpenFlags.ReadOnly | LinuxOpenFlags.Directory | LinuxOpenFlags.CloseOnExec | (allowSymlinkedAnchor ? 0 : LinuxOpenFlags.NoFollow);

        var descriptor = InvokeOpenUnix(anchorPath, baseFlags, isMacOs, out var errno);
        if (descriptor >= 0)
        {
            return new SafeFileHandle((nint)descriptor, ownsHandle: true);
        }

        // Pre-existing traverse-only parent directories (e.g. mode 0100 or 0300) cannot be opened O_RDONLY,
        // so fall back to an O_PATH (Linux) / O_SEARCH (macOS) directory descriptor that still pins the inode
        // for relative openat(2) and mkdirat(2) calls without re-resolving ancestor pathnames.
        if (errno is ErrnoAccess or ErrnoPerm)
        {
            var traverseFlags = isMacOs
                ? MacOsOpenSearch | MacOsOpenDirectory | MacOsOpenCloseOnExec | (allowSymlinkedAnchor ? 0 : MacOsOpenNoFollow)
                : LinuxOpenFlags.Path | LinuxOpenFlags.Directory | LinuxOpenFlags.CloseOnExec | (allowSymlinkedAnchor ? 0 : LinuxOpenFlags.NoFollow);
            descriptor = InvokeOpenUnix(anchorPath, traverseFlags, isMacOs, out var traverseErrno);
            if (descriptor >= 0)
            {
                return new SafeFileHandle((nint)descriptor, ownsHandle: true);
            }
            errno = traverseErrno;
        }

        throw CreateOpenFaultException(errno, "chmod-directory", anchorPath, isMacOs);
    }

    private static bool TryOpenDirectoryAtNoFollowUnix(
        SafeFileHandle parentHandle,
        string component,
        string pathForDiagnostics,
        bool isMacOs,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SafeFileHandle? childHandle)
    {
        var flags = GetOpenDirectoryNoFollowFlags(isMacOs);
        int descriptor;
        int errno;
        do
        {
            descriptor = isMacOs
                ? NativeMacOsOpenAt(parentHandle, component, flags)
                : NativeLinuxOpenAt(parentHandle, component, flags);
            errno = Marshal.GetLastPInvokeError();
        }
        while (descriptor < 0 && errno == ErrnoIntr);

        if (descriptor >= 0)
        {
            childHandle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            return true;
        }

        if (errno == ErrnoNoEnt)
        {
            childHandle = null;
            return false;
        }

        throw CreateOpenFaultException(errno, "chmod-directory", pathForDiagnostics, isMacOs);
    }

    private static void CreateDirectoryAtUnix(
        SafeFileHandle parentHandle,
        string component,
        UnixFileMode mode,
        string pathForDiagnostics,
        bool isMacOs)
    {
        int result;
        int errno;
        do
        {
            result = isMacOs
                ? NativeMacOsMkdirAt(parentHandle, component, (ushort)mode)
                : NativeLinuxMkdirAt(parentHandle, component, (uint)mode);
            errno = Marshal.GetLastPInvokeError();
        }
        while (result < 0 && errno == ErrnoIntr);

        if (result == 0 || errno == ErrnoExist)
        {
            return;
        }

        throw CreateOpenFaultException(errno, "create-directory", pathForDiagnostics, isMacOs);
    }

    private static void VerifyDirectoryDescriptorUnix(SafeFileHandle handle, string pathForDiagnostics, bool isMacOs)
    {
        if (handle.IsInvalid)
        {
            throw new ArgumentException("Invalid file handle.", nameof(handle));
        }

        int result;
        int errno;
        ushort fileType;
        if (isMacOs)
        {
            MacOsFileStatus macStatus;
            do
            {
                result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                    ? NativeMacOsFstat64(handle, out macStatus)
                    : NativeMacOsFstat(handle, out macStatus);
                errno = Marshal.GetLastPInvokeError();
            }
            while (result < 0 && errno == ErrnoIntr);

            if (result < 0)
            {
                throw CreateOpenFaultException(errno, "chmod-directory", pathForDiagnostics, isMacOs: true);
            }

            fileType = (ushort)(macStatus.Mode & UnixFileTypeMask);
        }
        else
        {
            LinuxFileStatus linuxStatus;
            do
            {
                result = StatXHandle(handle, string.Empty, AtEmptyPath, StatxType, out linuxStatus);
                errno = Marshal.GetLastPInvokeError();
            }
            while (result < 0 && errno == ErrnoIntr);

            if (result < 0)
            {
                throw CreateOpenFaultException(errno, "chmod-directory", pathForDiagnostics, isMacOs: false);
            }

            if ((linuxStatus.Mask & StatxType) != StatxType)
            {
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", pathForDiagnostics);
            }

            fileType = (ushort)(linuxStatus.Mode & UnixFileTypeMask);
        }

        if (fileType != UnixDirectoryType)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "chmod-directory", pathForDiagnostics);
        }
    }

    private static void FchmodUnix(SafeFileHandle handle, uint mode, string operation, string pathForDiagnostics, bool isMacOs)
    {
        if (handle.IsInvalid) throw new ArgumentException("Invalid file handle.", nameof(handle));
        int result;
        int errno;
        do
        {
            result = isMacOs
                ? NativeMacOsFchmod(handle, (ushort)mode)
                : NativeLinuxFchmod(handle, mode);
            errno = Marshal.GetLastPInvokeError();
        }
        while (result < 0 && errno == ErrnoIntr); // EINTR
        if (result < 0)
        {
            var kind = errno switch
            {
                ErrnoNoEnt => FileSystemFaultKind.NotFound,
                ErrnoAccess or ErrnoPerm => FileSystemFaultKind.PermissionDenied,
                _ => FileSystemFaultKind.Io
            };
            throw new FileSystemFaultException(kind, operation, pathForDiagnostics, new Win32Exception(errno));
        }
    }

    private static SafeFileHandle OpenLinux(string path, int flags, string operation)
        => OpenUnix(path, flags, operation, isMacOs: false);

    private static SafeFileHandle OpenUnix(string path, int flags, string operation, bool isMacOs)
    {
        var descriptor = InvokeOpenUnix(path, flags, isMacOs, out var errno);
        if (descriptor < 0)
        {
            throw CreateOpenFaultException(errno, operation, path, isMacOs);
        }
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static int InvokeOpenUnix(string path, int flags, bool isMacOs, out int errno)
    {
        int descriptor;
        do
        {
            descriptor = isMacOs
                ? NativeMacOsOpen(path, flags)
                : NativeLinuxOpen(path, flags);
            errno = Marshal.GetLastPInvokeError();
        }
        while (descriptor < 0 && errno == ErrnoIntr);
        return descriptor;
    }

    private static FileSystemFaultException CreateOpenFaultException(int errno, string operation, string path, bool isMacOs)
    {
        var loopErrno = isMacOs ? MacOsErrnoLoop : LinuxErrnoLoop;
        var kind = errno switch
        {
            ErrnoNoEnt => FileSystemFaultKind.NotFound,
            ErrnoAccess or ErrnoPerm => FileSystemFaultKind.PermissionDenied,
            ErrnoNotDir => FileSystemFaultKind.Containment, // ENOTDIR
            _ when errno == loopErrno => FileSystemFaultKind.Containment, // ELOOP (symlink under O_NOFOLLOW)
            _ => FileSystemFaultKind.Io
        };
        return new FileSystemFaultException(kind, operation, path, new Win32Exception(errno));
    }

#pragma warning disable SYSLIB1054 // Narrow POSIX libc/libSystem calls; source-generated interop adds unsafe boilerplate for no benefit.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeLinuxOpen(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeLinuxOpenAt(SafeFileHandle directoryFileDescriptor, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "mkdirat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeLinuxMkdirAt(SafeFileHandle directoryFileDescriptor, string path, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int NativeFsync(SafeFileHandle handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int NativeLinuxFchmod(SafeFileHandle handle, uint mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int StatX(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int StatXHandle(SafeFileHandle directoryFileDescriptor, string path, int flags, uint mask, out LinuxFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsOpen(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "openat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsOpenAt(SafeFileHandle directoryFileDescriptor, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "mkdirat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsMkdirAt(SafeFileHandle directoryFileDescriptor, string path, ushort mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int NativeMacOsFchmod(SafeFileHandle handle, ushort mode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "fstat", SetLastError = true)]
    private static extern int NativeMacOsFstat(SafeFileHandle handle, out MacOsFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int NativeMacOsFstat64(SafeFileHandle handle, out MacOsFileStatus status);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
    }

    // 64-bit inode Darwin struct stat layout (144 bytes; padded to 256 for margin):
    // st_dev (4 bytes @ offset 0), st_mode (uint16 @ offset 4) on both macOS arm64 (fstat) and x64 (fstat$INODE64).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MacOsFileStatus
    {
        [FieldOffset(4)] internal ushort Mode;
    }
}
