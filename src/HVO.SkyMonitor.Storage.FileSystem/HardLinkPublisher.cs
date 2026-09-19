using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>
/// Publishes another directory entry for an immutable file on the same Linux filesystem.
/// Directory traversal, linking, opening and identity checks are descriptor-relative. The
/// primitive assumes the physical root has one trusted writer, as required by its supported
/// topology; it detects ordinary source movement and never follows links.
/// </summary>
public static class HardLinkPublisher
{
    private const int AtEmptyPath = 0x1000;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const uint StatxType = 0x00000001;
    private const uint StatxInode = 0x00000100;
    private const uint StatxSize = 0x00000200;
    private const ushort UnixFileTypeMask = 0xF000;
    private const ushort UnixRegularFileType = 0x8000;

    /// <summary>True when this process has the native hard-link implementation used below.</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Create and hash a hard-linked destination. Returns null for expected platform or policy
    /// refusals so the caller can retain a streaming-copy fallback.
    /// </summary>
    public static async Task<HardLinkPublication?> TryPublishAsync(
        PhysicalRoot root,
        string sourceRelativePath,
        string destinationRelativePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }
        var source = root.Resolve(sourceRelativePath);
        var destination = root.Resolve(destinationRelativePath);
        var destinationDirectory = Path.GetDirectoryName(destination)
            ?? throw new FileSystemFaultException(FileSystemFaultKind.Containment, "hard-link", destinationRelativePath);
        AtomicPublisher.EnsureDirectory(root, destinationDirectory);

        using var rootHandle = OpenDirectory(root.Path, "hard-link-root");
        using var sourceParent = OpenParent(rootHandle, sourceRelativePath, "hard-link-source");
        using var destinationParent = OpenParent(rootHandle, destinationRelativePath, "hard-link-destination");
        var sourceName = FileName(sourceRelativePath);
        var destinationName = FileName(destinationRelativePath);
        using var sourceHandle = OpenAt(sourceParent, sourceName, LinuxOpenFlags.ReadOnly | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec, "hard-link-source", sourceRelativePath);
        var sourceIdentity = ReadIdentity(sourceHandle, sourceRelativePath);
        if (NativeLinkAt(sourceParent, sourceName, destinationParent, destinationName, 0) != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno is 18 or 1 or 13 or 31 or 38 or 95) // EXDEV, EPERM/EACCES, EMLINK, ENOSYS, EOPNOTSUPP
            {
                return null;
            }
            throw NativeFault(errno, "hard-link", destinationRelativePath);
        }

        LinuxFileIdentity? linkedIdentity = null;
        try
        {
            using var destinationHandle = OpenAt(
                destinationParent,
                destinationName,
                LinuxOpenFlags.ReadOnly | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec,
                "hard-link-destination",
                destinationRelativePath);
            var destinationIdentity = ReadIdentity(destinationHandle, destinationRelativePath);
            linkedIdentity = destinationIdentity;
            if (sourceIdentity != destinationIdentity)
            {
                throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "hard-link-source-moved", sourceRelativePath);
            }
            DurableSync.Directory(destinationParent, destinationDirectory);
            var (length, sha256) = await HashAsync(destinationHandle, cancellationToken).ConfigureAwait(false);
            SafeFileHandle? publicationParent = null;
            SafeFileHandle? publicationFile = null;
            try
            {
                publicationParent = Duplicate(destinationParent, "hard-link-publication", destinationRelativePath);
                publicationFile = Duplicate(destinationHandle, "hard-link-publication", destinationRelativePath);
                var publication = new HardLinkPublication(
                    publicationParent,
                    publicationFile,
                    destinationName,
                    destinationRelativePath,
                    destinationDirectory,
                    destinationIdentity,
                    length,
                    sha256);
                publicationParent = null;
                publicationFile = null;
                return publication;
            }
            finally
            {
                publicationFile?.Dispose();
                publicationParent?.Dispose();
            }
        }
        catch
        {
            TryUnlinkOwned(destinationParent, destinationName, destinationDirectory, linkedIdentity ?? sourceIdentity);
            throw;
        }
    }

    private static async Task<(long Length, string Sha256)> HashAsync(SafeFileHandle handle, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        int read;
        while ((read = await RandomAccess.ReadAsync(handle, buffer, offset, cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            offset += read;
        }
        return (offset, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static SafeFileHandle OpenParent(SafeFileHandle root, string relativePath, string operation)
    {
        var segments = Segments(relativePath);
        var current = Duplicate(root, operation, relativePath);
        try
        {
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var next = OpenAt(
                    current,
                    segments[i],
                    LinuxOpenFlags.ReadOnly | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec,
                    operation,
                    relativePath);
                current.Dispose();
                current = next;
            }
            return current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path, string operation)
    {
        var descriptor = NativeOpen(path, LinuxOpenFlags.ReadOnly | LinuxOpenFlags.Directory | LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec);
        if (descriptor < 0)
        {
            throw NativeFault(Marshal.GetLastPInvokeError(), operation, path);
        }
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, int flags, string operation, string path)
    {
        var descriptor = NativeOpenAt(parent, name, flags);
        if (descriptor < 0)
        {
            throw NativeFault(Marshal.GetLastPInvokeError(), operation, path);
        }
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static SafeFileHandle Duplicate(SafeFileHandle handle, string operation, string path)
    {
        var descriptor = NativeDuplicate(handle);
        if (descriptor < 0)
        {
            throw NativeFault(Marshal.GetLastPInvokeError(), operation, path);
        }
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static LinuxFileIdentity ReadIdentity(SafeFileHandle handle, string path)
    {
        const uint mask = StatxType | StatxInode | StatxSize;
        if (NativeStatX(handle, string.Empty, AtEmptyPath | AtSymbolicLinkNoFollow, mask, out var status) != 0)
        {
            throw NativeFault(Marshal.GetLastPInvokeError(), "hard-link-stat", path);
        }
        if ((status.Mask & mask) != mask || (status.Mode & UnixFileTypeMask) != UnixRegularFileType)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "hard-link-stat", path);
        }
        return new(status.DeviceMajor, status.DeviceMinor, status.Inode, status.Size);
    }

    private static string[] Segments(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "hard-link", relativePath);
        }
        var segments = relativePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".." || segment.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "hard-link", relativePath);
        }
        return segments;
    }

    private static string FileName(string relativePath) => Segments(relativePath)[^1];

    private static FileSystemFaultException NativeFault(int errno, string operation, string path)
    {
        var kind = errno switch
        {
            2 => FileSystemFaultKind.NotFound,
            17 => FileSystemFaultKind.AlreadyExists,
            13 or 1 or 30 => FileSystemFaultKind.PermissionDenied,
            28 or 122 => FileSystemFaultKind.NoSpace,
            18 => FileSystemFaultKind.CrossDevice,
            40 or 20 => FileSystemFaultKind.Containment,
            _ => FileSystemFaultKind.Io
        };
        return new FileSystemFaultException(kind, operation, path, new Win32Exception(errno));
    }

    private static void TryUnlinkOwned(
        SafeFileHandle parent,
        string name,
        string directory,
        LinuxFileIdentity expectedIdentity)
    {
        if (!TryReadIdentity(parent, name, out var current) || current != expectedIdentity)
        {
            return;
        }
        if (NativeUnlinkAt(parent, name, 0) == 0)
        {
            DurableSync.Directory(parent, directory);
        }
    }

    private static bool TryReadIdentity(SafeFileHandle parent, string name, out LinuxFileIdentity identity)
    {
        const uint mask = StatxType | StatxInode | StatxSize;
        if (NativeStatX(parent, name, AtSymbolicLinkNoFollow, mask, out var status) != 0
            || (status.Mask & mask) != mask
            || (status.Mode & UnixFileTypeMask) != UnixRegularFileType)
        {
            identity = default;
            return false;
        }
        identity = new(status.DeviceMajor, status.DeviceMinor, status.Inode, status.Size);
        return true;
    }

#pragma warning disable SYSLIB1054 // Narrow Linux calls avoid unsafe source-generated interop.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeOpen(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeOpenAt(SafeFileHandle directory, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "linkat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeLinkAt(SafeFileHandle oldDirectory, string oldPath, SafeFileHandle newDirectory, string newPath, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "unlinkat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeUnlinkAt(SafeFileHandle directory, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int NativeDuplicate(SafeFileHandle handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeStatX(SafeFileHandle directory, string path, int flags, uint mask, out LinuxFileStatus status);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
}

/// <summary>A linked destination whose bytes and filesystem identity were verified through open handles.</summary>
public sealed class HardLinkPublication : IDisposable
{
    private SafeFileHandle? _parent;
    private SafeFileHandle? _file;
    private readonly string _name;
    private readonly string _relativePath;
    private readonly string _directory;
    private readonly LinuxFileIdentity _identity;

    internal HardLinkPublication(
        SafeFileHandle parent,
        SafeFileHandle file,
        string name,
        string relativePath,
        string directory,
        LinuxFileIdentity identity,
        long length,
        string sha256)
    {
        _parent = parent;
        _file = file;
        _name = name;
        _relativePath = relativePath;
        _directory = directory;
        _identity = identity;
        Length = length;
        Sha256 = sha256;
    }

    public long Length { get; }
    public string Sha256 { get; }

    /// <summary>Prove that the destination name still identifies the inode that was hashed.</summary>
    public void VerifyCurrent()
    {
        var parent = _parent ?? throw new ObjectDisposedException(nameof(HardLinkPublication));
        const uint mask = 0x00000001 | 0x00000100 | 0x00000200;
        if (NativeStatX(parent, _name, 0x100, mask, out var status) != 0)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.NotFound, "hard-link-verify", _relativePath, new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        if ((status.Mask & mask) != mask)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Io, "hard-link-verify", _relativePath);
        }
        var current = new LinuxFileIdentity(status.DeviceMajor, status.DeviceMinor, status.Inode, status.Size);
        if (current != _identity || (status.Mode & 0xF000) != 0x8000)
        {
            throw new FileSystemFaultException(FileSystemFaultKind.Containment, "hard-link-verify", _relativePath);
        }
    }

    /// <summary>Remove the uncommitted linked name and make that removal durable.</summary>
    public void DeleteUncommitted()
    {
        var parent = _parent ?? throw new ObjectDisposedException(nameof(HardLinkPublication));
        if (NativeStatX(parent, _name, 0x100, 0x00000001 | 0x00000100 | 0x00000200, out var status) != 0)
        {
            return;
        }
        var current = new LinuxFileIdentity(status.DeviceMajor, status.DeviceMinor, status.Inode, status.Size);
        if ((status.Mask & (0x00000001 | 0x00000100 | 0x00000200)) != (0x00000001 | 0x00000100 | 0x00000200)
            || (status.Mode & 0xF000) != 0x8000 || current != _identity)
        {
            return;
        }
        if (NativeUnlinkAt(parent, _name, 0) == 0)
        {
            DurableSync.Directory(parent, _directory);
        }
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        _parent?.Dispose();
        _parent = null;
    }

#pragma warning disable SYSLIB1054
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "unlinkat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeUnlinkAt(SafeFileHandle directory, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int NativeStatX(SafeFileHandle directory, string path, int flags, uint mask, out LinuxFileStatus status);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(40)] internal ulong Size;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
}

internal readonly record struct LinuxFileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode, ulong Size);
