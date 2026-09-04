namespace HVO.SkyMonitor.Deployment;

internal static class OwnerRecoverySocket
{
    internal const string FileName = "owner.sock";
    private const UnixFileMode OwnerDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerSocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    internal static string PathFor(InstallationPaths paths)
        => Path.Combine(paths.StateRoot, "identity", FileName);

    internal static string Validate(InstallationPaths paths, uint uid, uint gid)
        => Validate(paths, uid, gid, missingIsUnsupported: false);

    internal static string ValidateForRecovery(InstallationPaths paths, uint uid, uint gid)
        => Validate(paths, uid, gid, missingIsUnsupported: true);

    private static string Validate(InstallationPaths paths, uint uid, uint gid, bool missingIsUnsupported)
    {
        var parent = Path.GetDirectoryName(PathFor(paths))!;
        var parentIdentity = NativeLinux.GetDirectoryIdentity(parent);
        if (parentIdentity.Uid != uid || parentIdentity.Gid != gid || parentIdentity.Mode != OwnerDirectoryMode)
        {
            throw new InstallerException("The owner recovery socket directory is not owner-only.");
        }

        var socketPath = PathFor(paths);
        var socket = NativeLinux.TryGetNodeIdentity(socketPath);
        if (socket is null && missingIsUnsupported)
        {
            throw new OwnerRecoveryProtocolException(
                OwnerRecoveryFailureDisposition.Unsupported,
                "CameraAgent owner recovery runtime transport is unavailable.");
        }
        if (socket is null)
        {
            throw new InstallerException("The owner recovery socket is unavailable.");
        }
        if (socket.Type != NativeLinux.SocketNodeType || socket.Uid != uid || socket.Gid != gid ||
            socket.Mode != OwnerSocketMode || socket.LinkCount != 1)
        {
            throw new InstallerException("The owner recovery socket is not an owner-only runtime socket.");
        }
        return socketPath;
    }

    internal static void RemoveStoppedSocket(InstallationPaths paths, uint uid, uint gid)
    {
        var socketPath = PathFor(paths);
        if (!File.Exists(socketPath))
        {
            return;
        }
        _ = Validate(paths, uid, gid);
        using var parent = NativeLinux.OpenDirectoryNoFollow(Path.GetDirectoryName(socketPath)!);
        var name = Path.GetFileName(socketPath);
        var before = NativeLinux.GetNodeIdentityAt(parent, name);
        if (before.Type != NativeLinux.SocketNodeType || before.Uid != uid || before.Gid != gid ||
            before.Mode != OwnerSocketMode || before.LinkCount != 1)
        {
            throw new InstallerException("The stopped owner recovery socket changed before cleanup.");
        }
        NativeLinux.UnlinkNodeAt(parent, name, directory: false);
    }
}
