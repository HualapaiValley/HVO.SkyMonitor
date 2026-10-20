using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HVO.SkyMonitor.CameraAgent.Cli.Client;

/// <summary>What the file system says of one path (following a final symbolic link): its owner, mode and type, or the error.</summary>
internal readonly record struct SkyPathStatus(int Error, uint? Owner, UnixFileMode Mode, bool IsDirectory, bool IsRegularFile)
{
    /// <summary><c>ENOENT</c>: the path, or a folder on it, does not exist. Only this means "absent".</summary>
    public bool Missing => Error == SkyFileSystem.NoSuchFileOrDirectory;

    public bool Exists => Error == 0;

    /// <summary>The system's description of <see cref="Error"/>, such as "Permission denied".</summary>
    public string Reason => Marshal.GetPInvokeErrorMessage(Error);
}

/// <summary>
/// The file-system facts the credentials file is checked with. On Linux, where <c>hvo-sky</c> ships, <c>statx</c> gives
/// the exact error and the owner, which .NET does not expose; elsewhere the managed API gives the error and mode only.
/// </summary>
internal static partial class SkyFileSystem
{
    internal const int NoSuchFileOrDirectory = 2;
    private const int PermissionDenied = 13;
    private const int NotADirectory = 20;
    private const int InputOutputError = 5;
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const uint StatxType = 0x00000001;
    private const uint StatxMode = 0x00000002;
    private const uint StatxUid = 0x00000008;
    private const uint TypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;

    /// <summary>The effective user ID of this process; on another system than Linux, none.</summary>
    public static uint CurrentUserId { get; } = OperatingSystem.IsLinux() ? GetEffectiveUserId() : uint.MaxValue;

    public static SkyPathStatus Inspect(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return OperatingSystem.IsLinux() ? InspectLinux(path) : InspectManaged(path);
    }

    [SupportedOSPlatform("linux")]
    private static SkyPathStatus InspectLinux(string path)
    {
        const uint mask = StatxType | StatxMode | StatxUid;
        if (StatX(AtFileDescriptorCurrentWorkingDirectory, path, 0, mask, out var status) != 0)
        {
            return new SkyPathStatus(Marshal.GetLastPInvokeError(), null, default, false, false);
        }

        if ((status.Mask & mask) != mask)
        {
            return new SkyPathStatus(InputOutputError, null, default, false, false);
        }

        var type = status.Mode & TypeMask;
        return new SkyPathStatus(0, status.Uid, (UnixFileMode)(status.Mode & 0x0FFF), type == DirectoryType, type == RegularFileType);
    }

    [UnsupportedOSPlatform("linux")]
    private static SkyPathStatus InspectManaged(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            var mode = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);
            var directory = attributes.HasFlag(FileAttributes.Directory);
            return new SkyPathStatus(0, null, mode, directory, !directory);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SkyPathStatus(NoSuchFileOrDirectory, null, default, false, false);
        }
        catch (UnauthorizedAccessException)
        {
            return new SkyPathStatus(PermissionDenied, null, default, false, false);
        }
        catch (IOException)
        {
            return new SkyPathStatus(NotADirectory, null, default, false, false);
        }
    }

    [SupportedOSPlatform("linux")]
    private static uint GetEffectiveUserId() => geteuid();

    [LibraryImport("libc")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint geteuid();

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int StatX(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxFileStatus status);

    // struct statx, as far as the fields read here (linux/stat.h); it is 256 bytes on every architecture.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(20)] internal uint Uid;
        [FieldOffset(28)] internal ushort Mode;
    }
}
