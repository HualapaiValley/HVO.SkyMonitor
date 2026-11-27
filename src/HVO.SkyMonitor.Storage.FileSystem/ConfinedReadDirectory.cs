using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Storage.FileSystem;

/// <summary>Opens single-link regular files relative to a pinned, physical Unix directory.</summary>
public sealed class ConfinedReadDirectory : IDisposable
{
    private const ushort FileTypeMask = 0xf000;
    private const ushort RegularFile = 0x8000;
    private readonly SafeFileHandle _directory;
    private readonly int _fileFlags;

    public ConfinedReadDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (System.IO.Path.TrimEndingDirectorySeparator(path) != canonicalPath || canonicalPath == "/")
            throw new ArgumentException("An absolute, canonical directory below the filesystem root is required.", nameof(path));

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
            if (!IsSingleLinkRegularFile(handle))
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

    private static bool IsSingleLinkRegularFile(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            const uint typeAndLinkCount = 0x5; // STATX_TYPE | STATX_NLINK
            return Statx(handle, "", 0x1000, typeAndLinkCount, out var value) == 0 &&
                (value.Mask & typeAndLinkCount) == typeAndLinkCount &&
                (value.Mode & FileTypeMask) == RegularFile && value.LinkCount == 1;
        }

        return FstatMac(handle, out var mac) == 0 && (mac.Mode & FileTypeMask) == RegularFile && mac.LinkCount == 1;
    }

    public void Dispose() => _directory.Dispose();

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxStatx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(16)] public uint LinkCount;
        [FieldOffset(28)] public ushort Mode;
    }

    // Darwin's 64-bit inode ABI places st_mode and st_nlink after the 32-bit device number.
    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
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
}
