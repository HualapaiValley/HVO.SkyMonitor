using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// Staged replacement of the catalog file (#1965 4.6). A rebuild writes <c>.building</c>; when complete and synced it is
/// renamed to <c>.replacement</c>, so that name exists only for a complete catalog. The live catalog is retired by
/// rename, main file first and then its WAL, so its committed pages are never separated from it and nothing can open a
/// half-moved pair; then the replacement is installed and the retired files are deleted. <see cref="Recover"/> finishes
/// or discards every intermediate state deterministically and is run before the catalog is opened or rebuilt.
/// </summary>
internal static class SiloCatalogFiles
{
    public const string BuildingSuffix = ".building";
    public const string ReplacementSuffix = ".replacement";
    public const string RetiredSuffix = ".retired";

    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];

    public static string Building(string target) => target + BuildingSuffix;

    public static string Replacement(string target) => target + ReplacementSuffix;

    public static string Retired(string target) => target + RetiredSuffix;

    public static void Recover(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        DeleteWithSidecars(Building(target));
        var replacement = Replacement(target);
        var retired = Retired(target);
        if (File.Exists(replacement))
        {
            if (File.Exists(target))
            {
                Retire(target);
            }
            else
            {
                // The main file was retired; finish moving its committed WAL beside it.
                MoveIfPresent(target + "-wal", retired + "-wal");
                MoveIfPresent(target + "-journal", retired + "-journal");
                DeleteIfPresent(target + "-shm");
            }
            File.Move(replacement, target);
            DurableSync.Directory(directory);
        }
        else if (!File.Exists(target) && File.Exists(retired))
        {
            // Retired with no replacement to install: put the previous catalog back, with its WAL.
            MoveIfPresent(retired + "-wal", target + "-wal");
            MoveIfPresent(retired + "-journal", target + "-journal");
            File.Move(retired, target);
            DurableSync.Directory(directory);
        }
        if (File.Exists(target) && (File.Exists(retired) || Sidecars.Any(suffix => File.Exists(retired + suffix))))
        {
            DeleteWithSidecars(retired);
            DurableSync.Directory(directory);
        }
    }

    /// <summary>Move the live catalog aside, main file first, then the WAL and rollback journal that belong to it.</summary>
    public static void Retire(string target)
    {
        var retired = Retired(target);
        DeleteWithSidecars(retired);
        File.Move(target, retired);
        MoveIfPresent(target + "-wal", retired + "-wal");
        MoveIfPresent(target + "-journal", retired + "-journal");
        DeleteIfPresent(target + "-shm");
        DurableSync.Directory(Path.GetDirectoryName(target)!);
    }

    public static void DeleteWithSidecars(string path)
    {
        DeleteIfPresent(path);
        foreach (var suffix in Sidecars)
        {
            DeleteIfPresent(path + suffix);
        }
    }

    private static void MoveIfPresent(string from, string to)
    {
        if (File.Exists(from))
        {
            File.Move(from, to, overwrite: true);
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
