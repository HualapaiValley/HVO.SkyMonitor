using System.Runtime.InteropServices;
using HVO.SkyMonitor.CameraAgent.Common.Storage;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

internal static class SiloFilesystemObservation
{
    internal static SiloDiskBudget CreateBudget(string stateRoot, string? dataRoot, long captureReserve, long captureHourBytes,
        Func<SiloExactLocator, long?>? exactAllocation = null, IStorageCapacityProvider? capacity = null)
    {
        capacity ??= new FileSystemStorageCapacityProvider();
        var capacityRoot = dataRoot ?? stateRoot;
        return new(() =>
        {
            if (!Directory.Exists(capacityRoot))
                throw new IOException("The Silo capacity root is unavailable.");
            var silo = capacity.GetCapacity(capacityRoot);
            StorageCapacity? state = dataRoot is null || string.Equals(stateRoot, dataRoot, StringComparison.Ordinal)
                ? null : capacity.GetCapacity(stateRoot);
            return new(silo.TotalBytes, silo.AvailableBytes, StateCapacity: state);
        }, captureReserve, SiloDiskBudget.GiB / 4, SiloDiskBudget.GiB / 4, SiloDiskBudget.GiB, exactAllocation,
            captureHourBytes, dataRoot is null ? null : () => ReadTrashBytes(dataRoot));
    }

    private static long? ReadTrashBytes(string dataRoot)
    {
        if (!OperatingSystem.IsLinux()) return null;
        const int maximumEntries = 65536;
        var trash = Path.Combine(dataRoot, ".minio.sys", "tmp", ".trash");
        try
        {
            // Count allocated sectors. Directory names change as the Silo trash worker drains them.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false
            };
            long total = 0;
            var examined = 0;
            foreach (var path in Directory.EnumerateFiles(trash, "*", options))
            {
                if (++examined > maximumEntries || !TryReadBlocks(path, out var blocks)) return null;
                total = checked(total + blocks * 512);
            }
            return total;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool TryReadBlocks(string path, out long blocks)
    {
        const int currentDirectory = -100;
        const int noFollow = 0x100;
        const uint blocksMask = 0x400;
        var status = Statx(currentDirectory, path, noFollow, blocksMask, out var stat);
        blocks = stat.Blocks;
        return status == 0 && (stat.Mask & blocksMask) != 0 && blocks >= 0;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxResult
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(48)] public long Blocks;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute",
        Justification = "This project does not compile unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("libc", EntryPoint = "statx", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Statx(int directory, string path, int flags, uint mask, out StatxResult result);
}
