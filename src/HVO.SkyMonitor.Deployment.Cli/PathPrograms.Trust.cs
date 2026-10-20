namespace HVO.SkyMonitor.Deployment;

internal static partial class PathPrograms
{
    internal static UnixAccountDatabase Accounts { get; set; } = UnixAccountDatabase.System;

    // Production checks reach /. An admitted isolated root maps every destination below itself and acts as that
    // test filesystem's root; its external host ancestors are never program destinations.
    internal static string? DestinationRoot(InstallationPaths paths, string productionRoot)
        => paths.ProductRoot == productionRoot ? null : paths.ProductRoot;

    internal static void EnsureTrustedDestination(string target, string? destinationRoot)
    {
        var boundary = destinationRoot ?? Path.GetPathRoot(target)!;
        if (!Path.GetFullPath(target).StartsWith((boundary == "/" ? "/" : Path.TrimEndingDirectorySeparator(boundary) + "/"), StringComparison.Ordinal))
            throw InstallerException.Refused("The PATH program destination is outside its admitted root.");
        SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(target)!);
        for (var folder = Path.GetDirectoryName(target); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder))
            {
                var owner = NativeLinux.GetDirectoryIdentity(folder);
                if (!IsTrustedDirectory(owner, NativeLinux.getuid(), NativeLinux.getgid(), Accounts))
                    throw InstallerException.Refused($"PATH program folder '{folder}' must be owned by the installing user or root and not writable by other users; it was preserved.");
            }
            else if (NativeLinux.TryGetNodeIdentity(folder) is not null)
                throw InstallerException.Refused($"PATH program folder '{folder}' is not a directory; it was preserved.");
            if (folder == boundary) break;
        }
    }

    internal static bool IsTrustedDirectory(UnixPathIdentity owner, uint uid, uint gid, UnixAccountDatabase accounts)
        => (owner.Uid == 0 || owner.Uid == uid) && (owner.Mode & UnixFileMode.OtherWrite) == 0 &&
           ((owner.Mode & UnixFileMode.GroupWrite) == 0 || accounts.IsPrivateGroup(uid, gid, owner.Gid));

    private static void RequirePublishedIdentity(string target, UnixNodeIdentity verified)
    {
        var published = NativeLinux.GetNodeIdentity(target);
        if (published.Type != NativeLinux.RegularFileNodeType || published.LinkCount != 1 ||
            published.DeviceMajor != verified.DeviceMajor || published.DeviceMinor != verified.DeviceMinor ||
            published.Inode != verified.Inode || published.MountId != verified.MountId)
            throw InstallerException.Refused($"Program '{target}' changed identity during publication.");
    }

    private static async Task ValidatePublicationAsync(
        string target, string desiredSha256, PathProgramRecord? publication, CancellationToken cancellationToken)
    {
        var current = await HashAsync(target, cancellationToken).ConfigureAwait(false);
        var previous = await HashAsync(target + ".previous", cancellationToken).ConfigureAwait(false);
        if (publication?.PendingSha256 is { } pending && pending != desiredSha256 ||
            current is not null && (publication is null || current != publication.Sha256 && current != publication.PendingSha256) ||
            previous is not null && (publication is null || previous != publication.PreviousSha256 &&
                (publication.PendingSha256 is null || previous != publication.Sha256)))
            throw InstallerException.Refused($"Program '{target}' or its .previous is unowned or differs from the publication record; both were preserved. Move the changed file aside before retrying.");
    }
}
