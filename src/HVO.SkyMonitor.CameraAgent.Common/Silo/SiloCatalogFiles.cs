using HVO.SkyMonitor.Storage.FileSystem;

namespace HVO.SkyMonitor.CameraAgent.Common.Silo;

/// <summary>
/// Staged replacement of the catalog file (#1965 4.6). A rebuild writes <c>.building</c>; when complete and synced it is
/// renamed to <c>.replacement</c>, so that name exists only for a complete catalog. The live catalog is retired by
/// rename, main file first and then its WAL, so its committed pages are never separated from it and nothing can open a
/// half-moved pair; then the replacement is installed and the retired files are deleted. <see cref="RecoverAsync"/>
/// runs before the catalog is opened or rebuilt: it proves every catalog it could keep, install or put back is this
/// instance's own supported one, and only then finishes or discards each intermediate state and removes the inspection
/// scratch a crash left. A foreign, newer or other-binding catalog is refused with every file left unchanged.
/// </summary>
internal static class SiloCatalogFiles
{
    public const string BuildingSuffix = ".building";
    public const string ReplacementSuffix = ".replacement";
    public const string RetiredSuffix = ".retired";
    public const string InspectionInfix = ".inspect-";

    private static readonly string[] Sidecars = ["-wal", "-shm", "-journal"];

    public static string Building(string target) => target + BuildingSuffix;

    public static string Replacement(string target) => target + ReplacementSuffix;

    public static string Retired(string target) => target + RetiredSuffix;

    /// <summary>Validate, then recover. Nothing is moved, deleted or cleaned for a catalog that is not this instance's own.</summary>
    public static async ValueTask RecoverAsync(
        string target, SiloSqliteFormat format, IReadOnlyDictionary<string, string> metadata, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(target)!;
        var scratch = InspectionScratch(target);
        var retired = Retired(target);
        if (!File.Exists(Building(target)) && !File.Exists(Replacement(target)) && !File.Exists(retired) &&
            !Sidecars.Any(suffix => File.Exists(retired + suffix)) && scratch.Count == 0)
        {
            return;
        }
        await SiloSqlite.InspectAsync(target, format, metadata, directory, cancellationToken).ConfigureAwait(false);
        await SiloSqlite.InspectAsync(Replacement(target), format, metadata, directory, cancellationToken).ConfigureAwait(false);
        // A retired catalog's WAL is beside it, or still beside the target name when its retirement was interrupted.
        await SiloSqlite.InspectAsync(retired, format, metadata, directory, cancellationToken,
            File.Exists(retired + "-wal") || File.Exists(target) ? retired + "-wal" : target + "-wal").ConfigureAwait(false);
        foreach (var file in scratch)
        {
            DeleteIfPresent(file);
        }
        Recover(target);
    }

    /// <summary>
    /// Inspection copies a crash left behind: named for one of this target's catalog files, and nothing else, so no
    /// unrelated file is ever removed.
    /// </summary>
    public static IReadOnlyList<string> InspectionScratch(string target)
    {
        var directory = Path.GetDirectoryName(target)!;
        var prefix = Path.GetFileName(target);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, prefix + "*" + InspectionInfix + "*")
                .Where(path => Path.GetFileName(path) is { } name && name.StartsWith(prefix, StringComparison.Ordinal) &&
                    name.IndexOf(InspectionInfix, prefix.Length, StringComparison.Ordinal) >= 0)
                .ToArray()
            : [];
    }

    /// <summary>Remove the inspection scratch of a database already proven to be this instance's own.</summary>
    public static void DeleteInspectionScratch(string target)
    {
        foreach (var file in InspectionScratch(target))
        {
            DeleteIfPresent(file);
        }
    }

    /// <summary>Finish or discard an interrupted replacement; only after <see cref="RecoverAsync"/> has validated it.</summary>
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
