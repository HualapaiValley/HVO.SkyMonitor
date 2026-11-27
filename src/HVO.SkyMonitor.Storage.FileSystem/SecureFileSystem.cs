using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>Result of a single non-following (or following) filesystem metadata inspection.</summary>
public enum PathEntryKind
{
    NotFound = 0,
    Directory = 1,
    RegularFile = 2,
    SymbolicLink = 3,
    Other = 4,
    NotDirectory = 5,
    PermissionDenied = 6,
    IoError = 7
}

/// <summary>
/// Classification of attempting to open an existing regular file via
/// <see cref="SecureFileSystem.OpenRegularFileReadOnlyNoFollow(string, string, int)"/> (descriptor-relative on POSIX;
/// managed reparse-attribute check before open on non-POSIX platforms).
/// </summary>
public enum RegularFileOpenStatus
{
    Opened = 0,
    NotFound = 1,
    DirectorySymbolicLink = 2,
    DirectoryNotDirectory = 3,
    DirectoryPermissionDenied = 4,
    FileSymbolicLink = 5,
    FileIsDirectory = 6,
    FileNonRegular = 7,
    FilePermissionDenied = 8,
    FileOversized = 9,
    IoError = 10
}

/// <summary>
/// Result of opening an existing regular file via
/// <see cref="SecureFileSystem.OpenRegularFileReadOnlyNoFollow(string, string, int)"/> (relative to a pinned parent
/// directory descriptor on POSIX; managed reparse-attribute check before pathname open on non-POSIX platforms).
/// </summary>
public sealed class RegularFileOpenResult : IDisposable
{
    private SafeFileHandle? _handle;

    internal RegularFileOpenResult(RegularFileOpenStatus status, SafeFileHandle? handle, long sizeBytes, int errorCode)
    {
        Status = status;
        _handle = handle;
        SizeBytes = sizeBytes;
        ErrorCode = errorCode;
    }

    public RegularFileOpenStatus Status { get; }

    public long SizeBytes { get; }

    public int ErrorCode { get; }

    /// <summary>Transfers ownership of the verified regular-file handle to the caller.</summary>
    public SafeFileHandle DetachHandle()
    {
        var handle = Interlocked.Exchange(ref _handle, null);
        if (handle is null)
        {
            throw new InvalidOperationException("No open regular-file handle is available to detach.");
        }
        return handle;
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _handle, null)?.Dispose();
    }
}

/// <summary>
/// Filesystem inspection, permission hardening, and bounded file opens with symbolic-link guards.
/// </summary>
public static class SecureFileSystem
{
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const int MacOsAtSymbolicLinkNoFollow = 0x20;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;
    private const uint StatxSize = 0x00000200;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixDirectoryType = 0x4000;
    private const ushort UnixRegularFileType = 0x8000;
    private const ushort UnixSymbolicLinkType = 0xA000;
    private const int MacOsOpenReadOnly = 0;
    private const int MacOsOpenNonBlock = 0x0004;
    private const int MacOsOpenNoFollow = 0x0100;
    private const int MacOsOpenDirectory = 0x100000;
    private const int MacOsOpenCloseOnExec = 0x01000000;
    private const int MacOsOpenSearch = 0x40000000;
    private const int ErrnoPerm = 1;
    private const int ErrnoNoEnt = 2;
    private const int ErrnoIntr = 4;
    private const int ErrnoNxio = 6;
    private const int ErrnoAccess = 13;
    private const int ErrnoExist = 17;
    private const int ErrnoNotDir = 20;
    private const int ErrnoIsDir = 21;
    private const int LinuxErrnoLoop = 40;
    private const int MacOsErrnoLoop = 62;

    /// <summary>
    /// Classifies <paramref name="absolutePath"/> using a SINGLE non-following filesystem metadata
    /// observation (<c>statx(AT_SYMLINK_NOFOLLOW)</c> on Linux, <c>lstat(2)</c> on macOS, or a single
    /// <see cref="System.IO.File.GetAttributes(string)"/> call elsewhere) so callers never combine
    /// two pathname observations that could reclassify a normal directory as a permitted symlink.
    /// </summary>
    public static PathEntryKind InspectPathNoFollow(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (OperatingSystem.IsLinux())
        {
            return InspectPathLinux(absolutePath, followSymlinks: false);
        }
        if (OperatingSystem.IsMacOS())
        {
            return InspectPathMacOs(absolutePath, followSymlinks: false);
        }
        return InspectPathManaged(absolutePath, followSymlinks: false);
    }

    /// <summary>
    /// Classifies the resolved target of <paramref name="absolutePath"/> by following symbolic links
    /// using kernel path-resolution semantics (<c>statx(0)</c> on Linux, <c>stat(2)</c> on macOS).
    /// Intended strictly for verifying the target of an ancestor already observed to be a pre-existing
    /// symbolic link via <see cref="InspectPathNoFollow(string)"/>.
    /// </summary>
    public static PathEntryKind InspectPathFollow(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (OperatingSystem.IsLinux())
        {
            return InspectPathLinux(absolutePath, followSymlinks: true);
        }
        if (OperatingSystem.IsMacOS())
        {
            return InspectPathMacOs(absolutePath, followSymlinks: true);
        }
        return InspectPathManaged(absolutePath, followSymlinks: true);
    }

    private static PathEntryKind InspectPathLinux(string absolutePath, bool followSymlinks)
    {
        var flags = followSymlinks ? 0 : AtSymbolicLinkNoFollow;
        int result;
        int errno;
        LinuxFileStatus status;
        do
        {
            result = StatX(AtFileDescriptorCurrentWorkingDirectory, absolutePath, flags, StatxType, out status);
            errno = Marshal.GetLastPInvokeError();
        }
        while (result < 0 && errno == ErrnoIntr);

        if (result < 0)
        {
            return MapStatErrno(errno);
        }

        if ((status.Mask & StatxType) != StatxType)
        {
            return PathEntryKind.IoError;
        }

        return MapUnixMode(status.Mode);
    }

    private static PathEntryKind InspectPathMacOs(string absolutePath, bool followSymlinks)
    {
        int result;
        int errno;
        MacOsFileStatus status;
        var isX64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
        do
        {
            result = followSymlinks
                ? (isX64 ? NativeMacOsStat64(absolutePath, out status) : NativeMacOsStat(absolutePath, out status))
                : (isX64 ? NativeMacOsLstat64(absolutePath, out status) : NativeMacOsLstat(absolutePath, out status));
            errno = Marshal.GetLastPInvokeError();
        }
        while (result < 0 && errno == ErrnoIntr);

        if (result < 0)
        {
            return MapStatErrno(errno);
        }

        return MapUnixMode(status.Mode);
    }

    private static PathEntryKind InspectPathManaged(string absolutePath, bool followSymlinks)
    {
        try
        {
            if (followSymlinks)
            {
                if (System.IO.Directory.Exists(absolutePath))
                {
                    return PathEntryKind.Directory;
                }
                if (System.IO.File.Exists(absolutePath))
                {
                    var targetAttrs = System.IO.File.GetAttributes(absolutePath);
                    return (targetAttrs & FileAttributes.Device) != 0
                        ? PathEntryKind.Other
                        : PathEntryKind.RegularFile;
                }
                return PathEntryKind.NotFound;
            }

            var attrs = System.IO.File.GetAttributes(absolutePath);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                return PathEntryKind.SymbolicLink;
            }
            if ((attrs & FileAttributes.Device) != 0)
            {
                return PathEntryKind.Other;
            }
            if ((attrs & FileAttributes.Directory) != 0)
            {
                return PathEntryKind.Directory;
            }
            return PathEntryKind.RegularFile;
        }
        catch (FileNotFoundException)
        {
            return PathEntryKind.NotFound;
        }
        catch (DirectoryNotFoundException)
        {
            return PathEntryKind.NotDirectory;
        }
        catch (UnauthorizedAccessException)
        {
            return PathEntryKind.PermissionDenied;
        }
        catch (IOException)
        {
            return PathEntryKind.IoError;
        }
    }

    private static PathEntryKind MapUnixMode(ushort mode) => (ushort)(mode & UnixFileTypeMask) switch
    {
        UnixDirectoryType => PathEntryKind.Directory,
        UnixRegularFileType => PathEntryKind.RegularFile,
        UnixSymbolicLinkType => PathEntryKind.SymbolicLink,
        _ => PathEntryKind.Other
    };

    private static PathEntryKind MapStatErrno(int errno) => errno switch
    {
        ErrnoNoEnt => PathEntryKind.NotFound,
        ErrnoNotDir => PathEntryKind.NotDirectory,
        ErrnoAccess or ErrnoPerm => PathEntryKind.PermissionDenied,
        _ => PathEntryKind.IoError
    };

    /// <summary>
    /// Pins <paramref name="anchorParentDirectory"/> with an open directory descriptor (enforcing
    /// <c>O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c> when <paramref name="allowSymlinkedAnchor"/> is <c>false</c>,
    /// with an <c>O_PATH</c>/<c>O_SEARCH</c> fallback for traverse-only anchors) and walks each protected
    /// descendant component in <paramref name="descendantComponents"/> strictly relative to its pinned parent
    /// descriptor using <c>openat(2)</c> (<c>O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c>),
    /// <c>mkdirat(2)</c> when <paramref name="createMissing"/> is <c>true</c>, descriptor directory-type
    /// verification (<c>statx</c>/<c>fstat</c>), and <c>fchmod(2)</c>. Once the trusted anchor directory
    /// descriptor is pinned, neither descendant symlink substitution nor post-pin ancestor replacement can direct
    /// <c>mkdirat</c> or <c>fchmod</c> outside the pinned subtree; acquiring the initial anchor by pathname still
    /// relies on non-attacker-controlled ancestors above <paramref name="anchorParentDirectory"/>.
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
    /// Restricts a directory's Unix permissions without following a leaf symbolic link by pinning its parent
    /// directory descriptor (allowing pre-existing symlinked system parents such as <c>/tmp</c> or <c>/var/run</c>),
    /// opening the leaf component via <c>openat(2)</c> with <c>O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c>,
    /// verifying the descriptor is a directory, and applying <c>fchmod(2)</c> on the authenticated file descriptor.
    /// This eliminates leaf-symlink TOCTOU and post-pin ancestor-replacement races once the parent descriptor is
    /// pinned; acquiring the parent descriptor by pathname still relies on non-attacker-controlled ancestors above
    /// the parent. No-op on Windows.
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

    /// <summary>
    /// Opens <paramref name="absolutePath"/> with <c>O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c> (falling back to
    /// <c>O_PATH</c>/<c>O_SEARCH</c> for traverse-only directories on Unix) and verifies via descriptor metadata that
    /// the opened target is a real directory, failing closed with <see cref="FileSystemFaultKind.Containment"/> on
    /// symbolic links or non-directories.
    /// </summary>
    public static void VerifyDirectoryNoFollow(string absolutePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absolutePath);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var isMacOs = OperatingSystem.IsMacOS();
            using var handle = OpenDirectoryAnchorUnix(absolutePath, allowSymlinkedAnchor: false, isMacOs);
            VerifyDirectoryDescriptorUnix(handle, absolutePath, isMacOs);
            return;
        }

        var kind = InspectPathNoFollow(absolutePath);
        switch (kind)
        {
            case PathEntryKind.Directory:
                return;
            case PathEntryKind.NotFound:
                throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "verify-directory", absolutePath);
            case PathEntryKind.PermissionDenied:
                throw new FileSystemFaultException(FileSystemFaultKind.PermissionDenied, "verify-directory", absolutePath);
            case PathEntryKind.SymbolicLink:
            case PathEntryKind.NotDirectory:
            case PathEntryKind.RegularFile:
            case PathEntryKind.Other:
                throw new FileSystemFaultException(FileSystemFaultKind.Containment, "verify-directory", absolutePath);
            default:
                throw new FileSystemFaultException(FileSystemFaultKind.Io, "verify-directory", absolutePath);
        }
    }

    /// <summary>
    /// On Linux and macOS (POSIX), pins <paramref name="directoryPath"/> with
    /// <c>O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC</c>, opens <paramref name="fileName"/> strictly relative to that
    /// directory descriptor via <c>openat(dirFd, fileName, O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC)</c>, and
    /// verifies <c>S_ISREG</c> and open-time file length (<c>st_size &lt;= maxBytes</c>) via <c>fstat</c>/<c>statx</c>
    /// on the opened descriptor before returning the handle (preventing symlink TOCTOU, FIFO open hangs, and oversized
    /// open-time handles; callers must still enforce read-time byte bounds when reading from the returned handle in
    /// case the file grows after open).
    /// On Windows and other non-POSIX platforms, performs managed reparse-attribute, file-type, and size checks
    /// before opening the pathname and re-checking the opened handle length at open time (the descriptor-relative
    /// <c>O_NOFOLLOW</c> TOCTOU guarantee applies only to POSIX; callers must likewise enforce read-time byte bounds).
    /// </summary>
    public static RegularFileOpenResult OpenRegularFileReadOnlyNoFollow(
        string directoryPath,
        string fileName,
        int maxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (!System.IO.Path.IsPathRooted(directoryPath))
        {
            throw new ArgumentException("Directory path must be an absolute path.", nameof(directoryPath));
        }
        if (fileName is "." or ".." || fileName.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException("File name must be a single path segment.", nameof(fileName));
        }

        var normalizedDir = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directoryPath));
        if (string.IsNullOrEmpty(normalizedDir))
        {
            normalizedDir = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(directoryPath)) ?? directoryPath;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            return OpenRegularFileReadOnlyNoFollowUnix(normalizedDir, fileName, maxBytes, OperatingSystem.IsMacOS());
        }

        return OpenRegularFileReadOnlyNoFollowManaged(normalizedDir, fileName, maxBytes);
    }

    private static RegularFileOpenResult OpenRegularFileReadOnlyNoFollowUnix(
        string normalizedDir,
        string fileName,
        int maxBytes,
        bool isMacOs)
    {
        var loopErrno = isMacOs ? MacOsErrnoLoop : LinuxErrnoLoop;
        var dirBaseFlags = GetOpenDirectoryNoFollowFlags(isMacOs);
        var dirDescriptor = InvokeOpenUnix(normalizedDir, dirBaseFlags, isMacOs, out var dirErrno);
        if (dirDescriptor < 0 && dirErrno is ErrnoAccess or ErrnoPerm)
        {
            var traverseFlags = isMacOs
                ? MacOsOpenSearch | MacOsOpenDirectory | MacOsOpenNoFollow | MacOsOpenCloseOnExec
                : LinuxOpenFlags.Path | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec;
            dirDescriptor = InvokeOpenUnix(normalizedDir, traverseFlags, isMacOs, out var traverseErrno);
            if (dirDescriptor < 0)
            {
                dirErrno = traverseErrno;
            }
        }

        if (dirDescriptor < 0)
        {
            var status = dirErrno switch
            {
                ErrnoNoEnt => RegularFileOpenStatus.NotFound,
                ErrnoAccess or ErrnoPerm => RegularFileOpenStatus.DirectoryPermissionDenied,
                ErrnoNotDir => InspectPathNoFollow(normalizedDir) == PathEntryKind.SymbolicLink
                    ? RegularFileOpenStatus.DirectorySymbolicLink
                    : RegularFileOpenStatus.DirectoryNotDirectory,
                _ when dirErrno == loopErrno => RegularFileOpenStatus.DirectorySymbolicLink,
                _ => RegularFileOpenStatus.IoError
            };
            return new RegularFileOpenResult(status, null, 0, dirErrno);
        }

        using var dirHandle = new SafeFileHandle((nint)dirDescriptor, ownsHandle: true);
        if (!TryGetDescriptorTypeAndSizeUnix(dirHandle, isMacOs, out var dirType, out _, out var dirStatErrno))
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.IoError, null, 0, dirStatErrno);
        }
        if (dirType != UnixDirectoryType)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.DirectoryNotDirectory, null, 0, ErrnoNotDir);
        }

        var openFileFlags = isMacOs
            ? MacOsOpenReadOnly | MacOsOpenNoFollow | MacOsOpenNonBlock | MacOsOpenCloseOnExec
            : LinuxOpenFlags.ReadOnly | LinuxOpenFlags.NoFollow | LinuxOpenFlags.NonBlock | LinuxOpenFlags.CloseOnExec;

        int fileDescriptor;
        int fileErrno;
        do
        {
            fileDescriptor = isMacOs
                ? NativeMacOsOpenAt(dirHandle, fileName, openFileFlags)
                : NativeLinuxOpenAt(dirHandle, fileName, openFileFlags);
            fileErrno = Marshal.GetLastPInvokeError();
        }
        while (fileDescriptor < 0 && fileErrno == ErrnoIntr);

        if (fileDescriptor >= 0)
        {
            var fileHandle = new SafeFileHandle((nint)fileDescriptor, ownsHandle: true);
            if (!TryGetDescriptorTypeAndSizeUnix(fileHandle, isMacOs, out var fileType, out var fileSize, out var fstatErrno))
            {
                fileHandle.Dispose();
                return new RegularFileOpenResult(RegularFileOpenStatus.IoError, null, 0, fstatErrno);
            }

            if (fileType == UnixDirectoryType)
            {
                fileHandle.Dispose();
                return new RegularFileOpenResult(RegularFileOpenStatus.FileIsDirectory, null, 0, ErrnoIsDir);
            }

            if (fileType == UnixSymbolicLinkType)
            {
                fileHandle.Dispose();
                return new RegularFileOpenResult(RegularFileOpenStatus.FileSymbolicLink, null, 0, loopErrno);
            }

            if (fileType != UnixRegularFileType)
            {
                fileHandle.Dispose();
                return new RegularFileOpenResult(RegularFileOpenStatus.FileNonRegular, null, 0, 0);
            }

            if (fileSize < 0 || fileSize > maxBytes)
            {
                fileHandle.Dispose();
                return new RegularFileOpenResult(RegularFileOpenStatus.FileOversized, null, fileSize, 0);
            }

            return new RegularFileOpenResult(RegularFileOpenStatus.Opened, fileHandle, fileSize, 0);
        }

        if (fileErrno == ErrnoNoEnt)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.NotFound, null, 0, fileErrno);
        }
        if (fileErrno == loopErrno)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.FileSymbolicLink, null, 0, fileErrno);
        }
        if (fileErrno == ErrnoIsDir)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.FileIsDirectory, null, 0, fileErrno);
        }
        if (fileErrno is ErrnoNotDir or ErrnoNxio)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.FileNonRegular, null, 0, fileErrno);
        }
        if (fileErrno is ErrnoAccess or ErrnoPerm)
        {
            // Inspect entry metadata relative to the SAME pinned directory descriptor so unreadable
            // write-only (0200) oversized files, symlinks, directories, and FIFOs still classify
            // fail-closed without re-resolving pathnames.
            if (TryGetRelativeEntryTypeAndSizeNoFollowUnix(dirHandle, fileName, isMacOs, out var entryType, out var entrySize))
            {
                if (entryType == UnixSymbolicLinkType)
                {
                    return new RegularFileOpenResult(RegularFileOpenStatus.FileSymbolicLink, null, 0, loopErrno);
                }
                if (entryType == UnixDirectoryType)
                {
                    return new RegularFileOpenResult(RegularFileOpenStatus.FileIsDirectory, null, 0, ErrnoIsDir);
                }
                if (entryType != UnixRegularFileType)
                {
                    return new RegularFileOpenResult(RegularFileOpenStatus.FileNonRegular, null, 0, 0);
                }
                if (entrySize < 0 || entrySize > maxBytes)
                {
                    return new RegularFileOpenResult(RegularFileOpenStatus.FileOversized, null, entrySize, fileErrno);
                }
                return new RegularFileOpenResult(RegularFileOpenStatus.FilePermissionDenied, null, entrySize, fileErrno);
            }
            // If fstatat/statx relative to the pinned directory also failed with EACCES/EPERM (e.g. mode 0000
            // directory opened via O_PATH on Linux), classify as DirectoryPermissionDenied (CanReplace=false).
            return new RegularFileOpenResult(RegularFileOpenStatus.DirectoryPermissionDenied, null, 0, fileErrno);
        }

        return new RegularFileOpenResult(RegularFileOpenStatus.IoError, null, 0, fileErrno);
    }

    private static RegularFileOpenResult OpenRegularFileReadOnlyNoFollowManaged(
        string normalizedDir,
        string fileName,
        int maxBytes)
    {
        switch (InspectPathManaged(normalizedDir, followSymlinks: false))
        {
            case PathEntryKind.NotFound:
                return new RegularFileOpenResult(RegularFileOpenStatus.NotFound, null, 0, 0);
            case PathEntryKind.Directory:
                break;
            case PathEntryKind.SymbolicLink:
                return new RegularFileOpenResult(RegularFileOpenStatus.DirectorySymbolicLink, null, 0, 0);
            case PathEntryKind.PermissionDenied:
                return new RegularFileOpenResult(RegularFileOpenStatus.DirectoryPermissionDenied, null, 0, 0);
            case PathEntryKind.NotDirectory:
            case PathEntryKind.RegularFile:
            case PathEntryKind.Other:
                return new RegularFileOpenResult(RegularFileOpenStatus.DirectoryNotDirectory, null, 0, 0);
            default:
                return new RegularFileOpenResult(RegularFileOpenStatus.IoError, null, 0, 0);
        }

        var fullPath = System.IO.Path.Combine(normalizedDir, fileName);
        switch (InspectPathManaged(fullPath, followSymlinks: false))
        {
            case PathEntryKind.NotFound:
                return new RegularFileOpenResult(RegularFileOpenStatus.NotFound, null, 0, 0);
            case PathEntryKind.RegularFile:
                break;
            case PathEntryKind.SymbolicLink:
                return new RegularFileOpenResult(RegularFileOpenStatus.FileSymbolicLink, null, 0, 0);
            case PathEntryKind.Directory:
                return new RegularFileOpenResult(RegularFileOpenStatus.FileIsDirectory, null, 0, 0);
            case PathEntryKind.PermissionDenied:
                return new RegularFileOpenResult(RegularFileOpenStatus.FilePermissionDenied, null, 0, 0);
            case PathEntryKind.NotDirectory:
            case PathEntryKind.Other:
                return new RegularFileOpenResult(RegularFileOpenStatus.FileNonRegular, null, 0, 0);
            default:
                return new RegularFileOpenResult(RegularFileOpenStatus.IoError, null, 0, 0);
        }

        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists)
            {
                return new RegularFileOpenResult(RegularFileOpenStatus.NotFound, null, 0, 0);
            }
            if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return new RegularFileOpenResult(RegularFileOpenStatus.FileSymbolicLink, null, 0, 0);
            }
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                return new RegularFileOpenResult(RegularFileOpenStatus.FileIsDirectory, null, 0, 0);
            }
            if ((info.Attributes & FileAttributes.Device) != 0)
            {
                return new RegularFileOpenResult(RegularFileOpenStatus.FileNonRegular, null, 0, 0);
            }
            if (info.Length > maxBytes)
            {
                return new RegularFileOpenResult(RegularFileOpenStatus.FileOversized, null, info.Length, 0);
            }

            var handle = System.IO.File.OpenHandle(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                var length = RandomAccess.GetLength(handle);
                if (length < 0 || length > maxBytes)
                {
                    handle.Dispose();
                    return new RegularFileOpenResult(RegularFileOpenStatus.FileOversized, null, length, 0);
                }
                return new RegularFileOpenResult(RegularFileOpenStatus.Opened, handle, length, 0);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        catch (FileNotFoundException)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.NotFound, null, 0, 0);
        }
        catch (DirectoryNotFoundException)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.NotFound, null, 0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.FilePermissionDenied, null, 0, 0);
        }
        catch (IOException)
        {
            return new RegularFileOpenResult(RegularFileOpenStatus.IoError, null, 0, 0);
        }
    }

    private static bool TryGetDescriptorTypeAndSizeUnix(
        SafeFileHandle handle,
        bool isMacOs,
        out ushort fileType,
        out long sizeBytes,
        out int errno)
    {
        fileType = 0;
        sizeBytes = 0;
        errno = 0;
        if (handle.IsInvalid)
        {
            return false;
        }

        int result;
        if (isMacOs)
        {
            MacOsFileStatus macStatus;
            var isX64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            do
            {
                result = isX64
                    ? NativeMacOsFstat64(handle, out macStatus)
                    : NativeMacOsFstat(handle, out macStatus);
                errno = Marshal.GetLastPInvokeError();
            }
            while (result < 0 && errno == ErrnoIntr);

            if (result < 0)
            {
                return false;
            }

            fileType = (ushort)(macStatus.Mode & UnixFileTypeMask);
            sizeBytes = macStatus.Size;
            return true;
        }

        LinuxFileStatus linuxStatus;
        do
        {
            result = StatXHandle(handle, string.Empty, AtEmptyPath, StatxType | StatxSize, out linuxStatus);
            errno = Marshal.GetLastPInvokeError();
        }
        while (result < 0 && errno == ErrnoIntr);

        if (result < 0 || (linuxStatus.Mask & StatxType) != StatxType)
        {
            return false;
        }

        fileType = (ushort)(linuxStatus.Mode & UnixFileTypeMask);
        if (fileType == UnixRegularFileType && (linuxStatus.Mask & StatxSize) != StatxSize)
        {
            return false;
        }

        sizeBytes = linuxStatus.Size > long.MaxValue ? long.MaxValue : (long)linuxStatus.Size;
        return true;
    }

    private static bool TryGetRelativeEntryTypeAndSizeNoFollowUnix(
        SafeFileHandle dirHandle,
        string fileName,
        bool isMacOs,
        out ushort fileType,
        out long sizeBytes)
    {
        fileType = 0;
        sizeBytes = 0;
        if (dirHandle.IsInvalid)
        {
            return false;
        }

        int result;
        int errno;
        if (isMacOs)
        {
            MacOsFileStatus macStatus;
            var isX64 = RuntimeInformation.ProcessArchitecture == Architecture.X64;
            do
            {
                result = isX64
                    ? NativeMacOsFstatAt64(dirHandle, fileName, out macStatus, MacOsAtSymbolicLinkNoFollow)
                    : NativeMacOsFstatAt(dirHandle, fileName, out macStatus, MacOsAtSymbolicLinkNoFollow);
                errno = Marshal.GetLastPInvokeError();
            }
            while (result < 0 && errno == ErrnoIntr);

            if (result < 0)
            {
                return false;
            }

            fileType = (ushort)(macStatus.Mode & UnixFileTypeMask);
            sizeBytes = macStatus.Size;
            return true;
        }

        LinuxFileStatus linuxStatus;
        do
        {
            result = StatXHandle(dirHandle, fileName, AtSymbolicLinkNoFollow, StatxType | StatxSize, out linuxStatus);
            errno = Marshal.GetLastPInvokeError();
        }
        while (result < 0 && errno == ErrnoIntr);

        if (result < 0 || (linuxStatus.Mask & StatxType) != StatxType)
        {
            return false;
        }

        fileType = (ushort)(linuxStatus.Mode & UnixFileTypeMask);
        if (fileType == UnixRegularFileType && (linuxStatus.Mask & StatxSize) != StatxSize)
        {
            return false;
        }

        sizeBytes = linuxStatus.Size > long.MaxValue ? long.MaxValue : (long)linuxStatus.Size;
        return true;
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

    internal static int OpenLinuxDescriptor(string path, int flags, out int errno)
        => InvokeOpenUnix(path, flags, isMacOs: false, out errno);

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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "fstatat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsFstatAt(SafeFileHandle directoryFileDescriptor, string path, out MacOsFileStatus status, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "fstatat$INODE64", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsFstatAt64(SafeFileHandle directoryFileDescriptor, string path, out MacOsFileStatus status, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "lstat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsLstat(string path, out MacOsFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "lstat$INODE64", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsLstat64(string path, out MacOsFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "stat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsStat(string path, out MacOsFileStatus status);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libSystem", EntryPoint = "stat$INODE64", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeMacOsStat64(string path, out MacOsFileStatus status);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(40)] internal ulong Size;
    }

    // 64-bit inode Darwin struct stat layout (144 bytes; padded to 256 for margin):
    // st_dev (4 bytes @ offset 0), st_mode (uint16 @ offset 4), st_size (int64 @ offset 96)
    // on both macOS arm64 (fstat/fstatat) and x64 (fstat$INODE64/fstatat$INODE64).
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct MacOsFileStatus
    {
        [FieldOffset(4)] internal ushort Mode;
        [FieldOffset(96)] internal long Size;
    }
}
