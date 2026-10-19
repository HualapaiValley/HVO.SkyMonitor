using System.Globalization;
using HVO.SkyMonitor.Deployment;

namespace HVO.SkyMonitor.Deployment.Cli.Tests;

/// <summary>
/// Proves a command changed nothing on disk (#1315 PR-1355-R1 F1, F3): every entry under a root with its type, size,
/// mode, owner, link count, inode and write time, so a file rewritten with the same content, a lock or temporary file
/// created, or a directory recreated is seen, not only content that is kept. A private temporary directory stands in
/// for <c>TMPDIR</c> while the command runs, so temporary files it creates and removes are seen too.
/// </summary>
internal static class FileSystemActivity
{
    /// <summary>Every entry under <paramref name="root"/>, or none when it does not exist.</summary>
    public static string[] Snapshot(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }
        return
        [
            .. Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Prepend(root)
                .Order(StringComparer.Ordinal)
                .Select(path => Describe(root, path))
        ];
    }

    /// <summary>Fails naming every entry that appeared, disappeared or changed between two snapshots.</summary>
    public static void AssertUnchanged(string[] before, string[] after, string what)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var removed = before.Except(after, StringComparer.Ordinal).ToArray();
        var added = after.Except(before, StringComparer.Ordinal).ToArray();
        if (removed.Length > 0 || added.Length > 0)
        {
            Assert.Fail($"{what}: before [{string.Join("; ", removed)}], after [{string.Join("; ", added)}]");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> with <c>TMPDIR</c> pointing at a new empty directory and returns what was left in
    /// it, and whether anything was created there at all while it ran.
    /// </summary>
    public static async Task<(T Result, string[] Created)> WithPrivateTemporaryDirectoryAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var original = Environment.GetEnvironmentVariable("TMPDIR");
        var temporary = Path.Combine(Path.GetTempPath(), $"hvo-activity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        using var watcher = new FileSystemWatcher(temporary) { IncludeSubdirectories = true, EnableRaisingEvents = true };
        var created = new List<string>();
        watcher.Created += (_, change) =>
        {
            lock (created)
            {
                created.Add(change.Name ?? change.FullPath);
            }
        };
        try
        {
            Environment.SetEnvironmentVariable("TMPDIR", temporary + "/");
            var result = await action();
            string[] seen;
            lock (created)
            {
                seen = [.. created.Concat(Directory.EnumerateFileSystemEntries(temporary).Select(static path => Path.GetFileName(path) ?? path)).Distinct(StringComparer.Ordinal)];
            }
            return (result, seen);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TMPDIR", original);
            SafeFileSystem.MakeTreeOwnerWritable(temporary);
            Directory.Delete(temporary, recursive: true);
        }
    }

    private static string Describe(string root, string path)
    {
        var node = NativeLinux.GetNodeIdentity(path);
        var relative = Path.GetRelativePath(root, path);
        var size = Directory.Exists(path) ? 0 : new FileInfo(path).Length;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{relative} type={node.Type} size={size} mode={node.Mode} owner={node.Uid}:{node.Gid} links={node.LinkCount} inode={node.Inode} written={File.GetLastWriteTimeUtc(path).Ticks}");
    }
}
