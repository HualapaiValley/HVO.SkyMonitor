using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Deployment;

internal static partial class NativeLinux
{
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymbolicLinkNoFollow = 0x100;
    private const int AtEmptyPath = 0x1000;
    private const uint StatxType = 0x00000001;
    private const uint StatxLinkCount = 0x00000004;
    private const uint StatxUid = 0x00000008;
    private const uint StatxGid = 0x00000010;
    private const uint StatxMode = 0x00000002;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint TypeMask = 0xF000;
    private const int OpenReadOnly = 0;
    private const int OpenReadWrite = 2;
    private const int OpenCreate = 0x40;
    private const int OpenDirectory = 0x10000;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const int TimeError = 5;
    private const int StatusUnsynchronized = 0x0040;

    [LibraryImport("libc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint getuid();

    [LibraryImport("libc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static partial uint getgid();

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int chown(string path, uint owner, uint group);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int StatX(
        int directoryFileDescriptor,
        string path,
        int flags,
        uint mask,
        out LinuxFileStatus status);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int Link(string existingPath, string newPath);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int OpenWithMode(string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int FSync(int fileDescriptor);

    [LibraryImport("libc", EntryPoint = "adjtimex", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AdjTime(ref LinuxTimeState state);

    public static void ChangeOwner(string path, uint uid, uint gid)
    {
        if (chown(path, uid, gid) != 0)
        {
            throw new InstallerException($"Could not assign runtime ownership to '{path}'.");
        }
    }

    public static uint GetLinkCount(string path)
    {
        if (StatX(
                AtFileDescriptorCurrentWorkingDirectory,
                path,
                AtSymbolicLinkNoFollow,
                StatxLinkCount,
                out var status) != 0 ||
            (status.Mask & StatxLinkCount) == 0)
        {
            throw new InstallerException($"Could not authenticate protected file '{path}'.");
        }
        return status.LinkCount;
    }

    public static UnixPathIdentity GetDirectoryIdentity(string path)
    {
        const uint mask = StatxType | StatxMode | StatxLinkCount | StatxUid | StatxGid;
        if (StatX(
                AtFileDescriptorCurrentWorkingDirectory,
                path,
                AtSymbolicLinkNoFollow,
                mask,
                out var status) != 0 ||
            (status.Mask & mask) != mask ||
            (status.Mode & TypeMask) != DirectoryType ||
            status.LinkCount < 2)
        {
            throw new InstallerException($"Directory '{path}' could not be authenticated.");
        }
        return new UnixPathIdentity(status.Uid, status.Gid, (UnixFileMode)(status.Mode & 0x0FFF));
    }

    public static SafeFileHandle OpenReadOnlyNoFollow(string path)
    {
        var descriptor = Open(path, OpenReadOnly | OpenNoFollow | OpenCloseOnExec);
        return descriptor >= 0
            ? new SafeFileHandle(descriptor, ownsHandle: true)
            : throw new InstallerException($"Protected file '{path}' could not be opened without following links.");
    }

    public static SafeFileHandle OpenLockFile(string path)
    {
        var descriptor = OpenWithMode(
            path,
            OpenReadWrite | OpenCreate | OpenNoFollow | OpenCloseOnExec,
            0x180);
        return descriptor >= 0
            ? new SafeFileHandle(descriptor, ownsHandle: true)
            : throw new InstallerException($"Operation lock '{path}' could not be opened safely.");
    }

    public static SafeFileHandle OpenReadWriteNoFollow(string path)
    {
        var descriptor = OpenWithMode(
            path,
            OpenReadWrite | OpenCreate | OpenNoFollow | OpenCloseOnExec,
            0x180);
        return descriptor >= 0
            ? new SafeFileHandle(descriptor, ownsHandle: true)
            : throw new InstallerException($"Protected file '{path}' could not be opened for writing without following links.");
    }

    public static UnixFileIdentity GetOpenFileIdentity(SafeFileHandle handle, string path)
    {
        const uint mask = StatxType | StatxMode | StatxLinkCount | StatxUid | StatxGid;
        if (StatX(handle.DangerousGetHandle().ToInt32(), string.Empty, AtEmptyPath, mask, out var status) != 0 ||
            (status.Mask & mask) != mask ||
            (status.Mode & TypeMask) != RegularFileType)
        {
            throw new InstallerException($"Protected file '{path}' could not be authenticated.");
        }
        return new UnixFileIdentity(
            status.Uid,
            status.Gid,
            (UnixFileMode)(status.Mode & 0x0FFF),
            status.LinkCount);
    }

    public static void FlushDirectory(string path)
    {
        using var descriptor = new SafeFileHandle(
            Open(path, OpenReadOnly | OpenDirectory | OpenNoFollow | OpenCloseOnExec),
            ownsHandle: true);
        if (descriptor.IsInvalid || FSync(descriptor.DangerousGetHandle().ToInt32()) != 0)
        {
            throw new InstallerException($"Directory '{path}' could not be flushed to disk.");
        }
    }

    public static bool IsClockSynchronized()
    {
        var state = new LinuxTimeState();
        var result = AdjTime(ref state);
        return result >= 0 && result != TimeError && (state.Status & StatusUnsynchronized) == 0 &&
               DateTimeOffset.UtcNow.Year >= 2025;
    }

    public static void CreateHardLink(string existingPath, string newPath)
    {
        if (Link(existingPath, newPath) != 0)
        {
            throw new InstallerException("Could not create the hard-link test fixture.");
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(16)] internal uint LinkCount;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(24)] internal uint Gid;
        [FieldOffset(28)] internal ushort Mode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 208)]
    private struct LinuxTimeState
    {
        [FieldOffset(40)] internal int Status;
    }
}

internal sealed record UnixPathIdentity(uint Uid, uint Gid, UnixFileMode Mode);
internal sealed record UnixFileIdentity(uint Uid, uint Gid, UnixFileMode Mode, uint LinkCount);
