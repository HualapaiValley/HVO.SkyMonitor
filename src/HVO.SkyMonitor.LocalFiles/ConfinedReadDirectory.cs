using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.LocalFiles;

/// <summary>A Linux directory's inode and its location within the filesystem, independent of bind-mount aliases.</summary>
public readonly record struct LinuxDirectoryLocation(uint DeviceMajor, uint DeviceMinor, ulong Inode, ulong MountId, string FileSystemPath)
{
    public bool Contains(LinuxDirectoryLocation candidate) =>
        DeviceMajor == candidate.DeviceMajor && DeviceMinor == candidate.DeviceMinor &&
        (Inode == candidate.Inode || FileSystemPath == candidate.FileSystemPath ||
         candidate.FileSystemPath.StartsWith(FileSystemPath == "/" ? "/" : FileSystemPath + "/", StringComparison.Ordinal));
}

/// <summary>Opens single-link regular files relative to a pinned, physical Unix directory, within its mount on Linux.</summary>
public sealed class ConfinedReadDirectory : IDisposable
{
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFile = 0x8000;
    private const uint MountIdMask = 0x1000; // STATX_MNT_ID
    private const int EmptyPath = 0x1000; // AT_EMPTY_PATH
    private readonly SafeFileHandle _directory;
    private readonly (uint Major, uint Minor, ulong Id) _linuxMount;
    private readonly int _fileFlags;
    private readonly string _path;

    public ConfinedReadDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (System.IO.Path.TrimEndingDirectorySeparator(path) != canonicalPath || canonicalPath == "/")
            throw new ArgumentException("An absolute, canonical directory below the filesystem root is required.", nameof(path));
        _path = canonicalPath;
        if (OperatingSystem.IsMacOS())
            _ = UsesDarwinInode64Symbol(RuntimeInformation.ProcessArchitecture);

        // O_NOFOLLOW on every directory component prevents a concurrent path replacement from redirecting a read.
        var directoryFlag = OperatingSystem.IsLinux() ? LinuxOpenFlags.Directory
            : OperatingSystem.IsMacOS() ? 0x100000 : throw new PlatformNotSupportedException("Confined reads require Linux or macOS.");
        var commonFlags = OperatingSystem.IsLinux() ? LinuxOpenFlags.NoFollow | LinuxOpenFlags.CloseOnExec
            : 0x100 | 0x1000000; // Darwin O_NOFOLLOW | O_CLOEXEC
        _fileFlags = commonFlags | (OperatingSystem.IsLinux() ? 0x800 : 0x4); // O_NONBLOCK avoids blocking on a FIFO.
        var directoryFlags = commonFlags | directoryFlag;
        var current = CheckedHandle(Open("/", directoryFlags), "/");
        try
        {
            foreach (var segment in canonicalPath[1..].Split('/'))
            {
                var next = CheckedHandle(OpenAt(current, segment, directoryFlags), segment);
                current.Dispose();
                current = next;
            }
            if (OperatingSystem.IsLinux())
                _linuxMount = MountOf(ReadLinuxStat(current, MountIdMask));
            _directory = current;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The returned FileStream owns the handle.")]
    public FileStream OpenRead(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name is "." or ".." || name.AsSpan().ContainsAny('/', '\\', '\0'))
            throw new ArgumentException("A single filename is required.", nameof(name));

        var handle = CheckedHandle(OpenAt(_directory, name, _fileFlags), name);
        try
        {
            if (!IsSingleLinkRegularFile(handle, name))
                throw new IOException($"'{name}' must be a regular file with one hard link.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle CheckedHandle(int descriptor, string name) => descriptor >= 0
        ? new SafeFileHandle((nint)descriptor, ownsHandle: true)
        : throw new IOException($"Cannot open '{name}' without following links (errno {Marshal.GetLastPInvokeError()}).");

    private bool IsSingleLinkRegularFile(SafeFileHandle handle, string name)
    {
        if (OperatingSystem.IsLinux())
            return IsSingleLinkRegularLinuxFile(handle, _linuxMount, name);

        var result = UsesDarwinInode64Symbol(RuntimeInformation.ProcessArchitecture)
            ? FstatMacInode64(handle, out var mac)
            : FstatMac(handle, out mac);
        return result == 0 && (mac.Mode & FileTypeMask) == RegularFile && mac.LinkCount == 1;
    }

    private static LinuxStatx ReadLinuxStat(SafeFileHandle handle, uint mask)
    {
        if (Statx(handle, "", EmptyPath, mask, out var status) != 0 || (status.Mask & mask) != mask)
            throw new IOException("Cannot identify the pinned filesystem entry.");
        return status;
    }

    private static (uint Major, uint Minor, ulong Id) MountOf(LinuxStatx status) =>
        (status.DeviceMajor, status.DeviceMinor, status.MountId);

    internal static bool IsSingleLinkRegularLinuxFile(SafeFileHandle handle, (uint Major, uint Minor, ulong Id) directoryMount, string name)
    {
        const uint typeAndLinkCount = 0x5; // STATX_TYPE | STATX_NLINK
        var value = ReadLinuxStat(handle, typeAndLinkCount | MountIdMask);
        // Device numbers alone match across a bind mount of a file from the same filesystem.
        if (directoryMount != MountOf(value))
            throw new IOException($"'{name}' is on a different mount from the pinned directory.");
        return (value.Mode & FileTypeMask) == RegularFile && value.LinkCount == 1;
    }

    internal static bool UsesDarwinInode64Symbol(Architecture architecture) => architecture switch
    {
        Architecture.X64 => true,
        Architecture.Arm64 => false,
        _ => throw new PlatformNotSupportedException("Confined macOS reads require x64 or arm64.")
    };

    /// <summary>Identifies the pinned directory and its path within the Linux filesystem, through bind mounts.</summary>
    public LinuxDirectoryLocation ReadLinuxLocation()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Mount identity requires Linux.");

        const uint inodeMask = 0x100; // STATX_INO
        var status = ReadLinuxStat(_directory, inodeMask | MountIdMask);

        var mountId = status.MountId.ToString(CultureInfo.InvariantCulture);
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var fields = line.Split(' ', 6);
            if (fields.Length < 6 || fields[0] != mountId)
                continue;
            var fileSystemRoot = DecodeMountPath(fields[3]);
            var mountPoint = DecodeMountPath(fields[4]);
            var relative = Path.GetRelativePath(mountPoint, _path);
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
                break;
            return new(status.DeviceMajor, status.DeviceMinor, status.Inode, status.MountId,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(relative, fileSystemRoot)));
        }
        throw new IOException("Cannot resolve the pinned directory within its mount.");
    }

    private static string DecodeMountPath(string path) => path
        .Replace(@"\040", " ", StringComparison.Ordinal)
        .Replace(@"\011", "\t", StringComparison.Ordinal)
        .Replace(@"\012", "\n", StringComparison.Ordinal)
        .Replace(@"\134", "\\", StringComparison.Ordinal);

    /// <summary>Measures free bytes on the pinned directory's filesystem.</summary>
    public long ReadLinuxAvailableBytes()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Pinned filesystem capacity requires Linux.");
        var addedReference = false;
        try
        {
            _directory.DangerousAddRef(ref addedReference);
            var descriptor = _directory.DangerousGetHandle().ToString(CultureInfo.InvariantCulture);
            return new DriveInfo("/proc/self/fd/" + descriptor).AvailableFreeSpace;
        }
        finally
        {
            if (addedReference)
                _directory.DangerousRelease();
        }
    }

    public void Dispose() => _directory.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong MountId;
    }

    // Darwin's 64-bit inode ABI places st_mode and st_nlink after the 32-bit device number.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    internal struct DarwinStat
    {
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(6)] public ushort LinkCount;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "open", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int Open(string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "openat", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true, SetLastError = true)]
    private static extern int OpenAt(SafeFileHandle parent, string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Statx(SafeFileHandle handle, string path, int flags, uint mask, out LinuxStatx value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "fstat")]
    private static extern int FstatMac(SafeFileHandle handle, out DarwinStat value);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "fstat$INODE64")]
    private static extern int FstatMacInode64(SafeFileHandle handle, out DarwinStat value);
}
