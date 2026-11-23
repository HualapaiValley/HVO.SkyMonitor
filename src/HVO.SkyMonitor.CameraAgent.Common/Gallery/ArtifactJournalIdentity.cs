using System.Runtime.InteropServices;

namespace HVO.SkyMonitor.CameraAgent.Common.Gallery;

// A read-only SQLite handle can still address an unlinked database or an old WAL mapping.
// Use the filesystem's identity for all three files. Without it, use a fresh handle as before.
internal readonly record struct ArtifactJournalIdentity(
    ArtifactJournalIdentity.FileIdentity Database,
    ArtifactJournalIdentity.FileIdentity Wal,
    ArtifactJournalIdentity.FileIdentity SharedMemory)
{
    internal static ArtifactJournalIdentity? Read(string database, string wal, string sharedMemory)
    {
        if (!OperatingSystem.IsLinux()) return null;

        try
        {
            return ReadFile(database) is { } databaseIdentity &&
                ReadFile(wal) is { } walIdentity && ReadFile(sharedMemory) is { } sharedMemoryIdentity
                    ? new(databaseIdentity, walIdentity, sharedMemoryIdentity)
                    : null;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static FileIdentity? ReadFile(string path)
    {
        const int currentDirectory = -100; // AT_FDCWD
        const int noFollow = 0x100; // AT_SYMLINK_NOFOLLOW
        const uint typeAndInode = 0x101; // STATX_TYPE | STATX_INO
        return StatX(currentDirectory, path, noFollow, typeAndInode, out var status) == 0 &&
            (status.Mask & typeAndInode) == typeAndInode &&
            (status.Mode & 0xF000) == 0x8000 // S_IFMT and S_IFREG
            ? new(status.DeviceMajor, status.DeviceMinor, status.Inode)
            : null;
    }

    internal readonly record struct FileIdentity(uint DeviceMajor, uint DeviceMinor, ulong Inode);

#pragma warning disable SYSLIB1054 // A narrow libc call avoids enabling unsafe code.
    // Keep this binding at the retrieval boundary. The staging, catalog and recovery bindings
    // belong to their own path or handle validation contracts.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int StatX(int directoryFileDescriptor, string path, int flags, uint mask, out LinuxFileStatus status);
#pragma warning restore SYSLIB1054

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct LinuxFileStatus
    {
        [FieldOffset(0)] internal uint Mask;
        [FieldOffset(28)] internal ushort Mode;
        [FieldOffset(32)] internal ulong Inode;
        [FieldOffset(136)] internal uint DeviceMajor;
        [FieldOffset(140)] internal uint DeviceMinor;
    }
}
