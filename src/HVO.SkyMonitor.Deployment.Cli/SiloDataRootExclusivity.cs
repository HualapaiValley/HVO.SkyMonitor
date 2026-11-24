using System.Globalization;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Where the mount table places a path: the device of the mount holding it, and its path within that filesystem.</summary>
internal sealed record SiloMountLocation(string Device, string Path)
{
    /// <summary>Whether the two share a device and one path is, or lies inside, the other.</summary>
    public bool Overlaps(SiloMountLocation other)
        => Device == other.Device &&
           (Path == other.Path || Path == "/" || other.Path == "/" ||
            Path.StartsWith(other.Path + "/", StringComparison.Ordinal) || other.Path.StartsWith(Path + "/", StringComparison.Ordinal));
}

/// <summary>
/// Whether a host path another container mounts is proved apart from a Silo data root (contract 7.2, #1966). These are the
/// rules the runbook's stock-image replacement applies, before its stop and after it. A path is apart only when it resolves
/// on this host and is neither the data root, inside it, nor holding it, compared by path as given and as resolved, by
/// device and inode, and by where the mount table places it and every mount below it.
/// </summary>
internal sealed class SiloDataRootExclusivity
{
    /// <summary>
    /// Reads the mount table <c>/proc/&lt;process&gt;/mountinfo</c> of <c>self</c> or of a process ID; throws when it cannot
    /// be read. A seam for tests, which script the host's and each container process's table as they script Docker.
    /// </summary>
    internal static Func<string, string> ReadMountTable { get; set; } = static process => File.ReadAllText($"/proc/{process}/mountinfo");

    private readonly string dataRoot;
    private readonly string resolvedDataRoot;
    private readonly UnixNodeIdentity dataNode;
    private readonly List<UnixNodeIdentity> holding = [];
    private readonly IReadOnlyList<(string Device, string Root, string MountPoint)> mounts;
    private readonly SiloMountLocation dataLocation;

    public SiloDataRootExclusivity(string dataRoot, string mountInfo)
    {
        this.dataRoot = dataRoot;
        resolvedDataRoot = NativeLinux.TryResolvePath(dataRoot)
            ?? throw InstallerException.Refused("The Silo data root does not resolve on this host, so its exclusivity is not proved.");
        dataNode = NativeLinux.TryGetTargetNodeIdentity(resolvedDataRoot)
            ?? throw InstallerException.Refused("The Silo data root cannot be examined, so its exclusivity is not proved.");
        // The data root and its existing ancestors as named and as resolved, and the root directory, by device and inode.
        foreach (var named in new[] { dataRoot, resolvedDataRoot })
        {
            for (var current = named; current is not null && current != "/"; current = System.IO.Path.GetDirectoryName(current))
            {
                if (NativeLinux.TryGetTargetNodeIdentity(current) is { } node) holding.Add(node);
            }
        }
        holding.Add(NativeLinux.TryGetTargetNodeIdentity("/")
            ?? throw InstallerException.Refused("The root directory cannot be examined, so the Silo data root's exclusivity is not proved."));
        mounts = Parse(mountInfo);
        dataLocation = Locate(resolvedDataRoot, mounts);
    }

    /// <summary>
    /// <see langword="null"/> when <paramref name="source"/> is proved apart from the data root; otherwise why it is not,
    /// as a clause that follows the path.
    /// </summary>
    public string? Overlap(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length == 0 || NativeLinux.TryResolvePath(source) is not { } resolved)
            return "which does not resolve here, so it is not proved apart from the data root";
        foreach (var path in new[] { source, resolved })
        {
            foreach (var root in new[] { dataRoot, resolvedDataRoot })
            {
                if (path == root || path.StartsWith(root + "/", StringComparison.Ordinal) ||
                    root.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal))
                    return "which is the data root, lies inside it or holds it";
            }
        }
        if (NativeLinux.TryGetTargetNodeIdentity(resolved) is not { } node)
            return "which does not resolve here, so it is not proved apart from the data root";
        if (holding.Any(candidate => SameNode(candidate, node)))
            return "the same directory as the data root, an ancestor of it or /";
        for (var current = resolved; current is not null && current != "/"; current = System.IO.Path.GetDirectoryName(current))
        {
            if (NativeLinux.TryGetTargetNodeIdentity(current) is not { } step)
                return "which does not resolve here, so it is not proved apart from the data root";
            if (SameNode(step, dataNode)) return "inside the data root under another name";
        }
        if (Locate(resolved, mounts).Overlaps(dataLocation))
            return "which the mount table places on the data root, inside it or holding it";
        if (mounts.Any(mount => (mount.MountPoint == resolved || mount.MountPoint.StartsWith(resolved + "/", StringComparison.Ordinal)) &&
                                new SiloMountLocation(mount.Device, mount.Root).Overlaps(dataLocation)))
            return "which holds a mount of the data root, of a path inside it or of one holding it";
        return null;
    }

    /// <summary>
    /// <see langword="null"/> when no mount in a running container process's own mount table overlaps the data root;
    /// otherwise why. This is what the container holds now, whatever its configured source names today: a link retargeted
    /// or a folder replaced after it started leaves the mount it started with (#1966 R6-01).
    /// </summary>
    public string? LiveOverlap(string processMountInfo)
        => Parse(processMountInfo).Any(mount => new SiloMountLocation(mount.Device, mount.Root).Overlaps(dataLocation))
            ? "which holds in its own mount table now the data root, a path inside it or one holding it"
            : null;

    /// <summary>
    /// <see langword="null"/> when no mount this host's table lists at or below <paramref name="mountPoint"/> overlaps the
    /// data root; otherwise why. The mount point is compared as Docker names it, without resolving it, so a folder only root
    /// may enter, such as Docker's own volume folders, is judged from the table alone.
    /// </summary>
    public string? MountedAtOrBelow(string mountPoint)
    {
        ArgumentNullException.ThrowIfNull(mountPoint);
        var below = mountPoint.TrimEnd('/') + "/";
        return mounts.Any(mount => (mount.MountPoint == mountPoint || mount.MountPoint.StartsWith(below, StringComparison.Ordinal)) &&
                                   new SiloMountLocation(mount.Device, mount.Root).Overlaps(dataLocation))
            ? "whose mount point holds a mount of the data root, of a path inside it or of one holding it now"
            : null;
    }

    /// <summary>The mount table of process <paramref name="pid"/>, or <see langword="null"/> when it cannot be read.</summary>
    public static string? TryReadProcessMountTable(int pid)
    {
        try
        {
            return ReadMountTable(pid.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where <paramref name="mountInfo"/> places existing resolved <paramref name="path"/>: the mount with the longest
    /// mount point that holds it, the later of equals, and the path's place within that mount's filesystem.
    /// </summary>
    public static SiloMountLocation Locate(string path, string mountInfo) => Locate(path, Parse(mountInfo));

    private static SiloMountLocation Locate(string path, IReadOnlyList<(string Device, string Root, string MountPoint)> mounts)
    {
        (string Device, string Root, string MountPoint)? best = null;
        foreach (var mount in mounts)
        {
            var holds = mount.MountPoint == "/" || mount.MountPoint == path || path.StartsWith(mount.MountPoint + "/", StringComparison.Ordinal);
            if (holds && (best is null || mount.MountPoint.Length >= best.Value.MountPoint.Length)) best = mount;
        }
        if (best is not { } found)
            throw InstallerException.Refused("The mount table does not hold a path beside the Silo data root, so its exclusivity is not proved.");
        var rest = found.MountPoint == "/" ? path : path[found.MountPoint.Length..];
        var location = found.Root.TrimEnd('/') + rest;
        return new SiloMountLocation(found.Device, location.Length == 0 ? "/" : location);
    }

    private static List<(string Device, string Root, string MountPoint)> Parse(string mountInfo)
    {
        ArgumentNullException.ThrowIfNull(mountInfo);
        var mounts = new List<(string Device, string Root, string MountPoint)>();
        foreach (var line in mountInfo.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(' ');
            if (fields.Length < 5) continue;
            mounts.Add((fields[2], HostProductRootPolicy.UnescapeMountPath(fields[3]), HostProductRootPolicy.UnescapeMountPath(fields[4])));
        }
        return mounts;
    }

    private static bool SameNode(UnixNodeIdentity left, UnixNodeIdentity right)
        => left.DeviceMajor == right.DeviceMajor && left.DeviceMinor == right.DeviceMinor && left.Inode == right.Inode;
}
