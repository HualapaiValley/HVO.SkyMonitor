using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Who a backup or restore runs as (#1353; owner decision 2026-10-18, PR-1369-R2 F12 and F13). Backups are owner-only:
/// the instance's service account owns every live store and secret, and it is in the <c>docker</c> group, which is root
/// on the host in all but name, so an archive kept from it by root would protect almost nothing. Both commands therefore
/// run as that account, with no <c>sudo</c> and nothing done as root, and keep their archives in its own folder
/// (<see cref="BackupVault"/>). Run by anyone else, root included, they are refused before anything is read or written,
/// naming the account to run them as. RoofController's run as root; the install contract records the difference.
/// </summary>
internal sealed partial class CameraAgentLifecycleManager
{
    /// <summary>The file type statx reports for a directory.</summary>
    private const uint DirectoryNodeType = 0x4000;

    /// <summary>
    /// Refuses (exit 3), before anything changes, a backup or restore run by anyone but the instance's service account:
    /// the owner of the instance root, which the installer creates as the account the instance's manifest records. A
    /// restore of an absent instance runs as the owner of the backup folder, and its admission then requires the account
    /// the backup's record names (<see cref="EnsureRecordedOwner"/>). Only file metadata is read, so root or another user
    /// is refused with the right account named, not with an error about a file it cannot read.
    /// </summary>
    private static void EnsureServiceAccount(LifecycleRequest request, uint uid, uint gid)
    {
        var paths = InstallationPaths.Create(request.ProductRoot, request.InstanceId!.Value, ProductionCatalog.DefaultCatalogId);
        if (NativeLinux.TryGetNodeIdentity(paths.InstanceRoot) is { Type: DirectoryNodeType } instance)
        {
            if (instance.Uid != uid || instance.Gid != gid)
            {
                throw NotTheServiceAccount(request, instance.Uid, instance.Gid, $"Instance {request.InstanceId:D} belongs to");
            }
            return;
        }
        var folder = Path.Combine(request.ProductRoot, BackupVault.FolderName);
        if (request.Operation == LifecycleOperationKind.Restore &&
            NativeLinux.TryGetNodeIdentity(folder) is { Type: DirectoryNodeType } backups && backups.Uid != uid)
        {
            throw NotTheServiceAccount(request, backups.Uid, backups.Gid, $"The backup folder {folder} belongs to");
        }
    }

    /// <summary>
    /// Refuses (exit 3) a backup or restore whose recorded owner, <paramref name="ownerUid"/>:<paramref name="ownerGid"/>,
    /// is not the verified account running it, <paramref name="uid"/>:<paramref name="gid"/>: the instance manifest's for a
    /// backup, and the backup's record and archive for a restore. <paramref name="whose"/> says what records it.
    /// </summary>
    private static void EnsureRecordedOwner(LifecycleRequest request, uint ownerUid, uint ownerGid, uint uid, uint gid, string whose)
    {
        if (ownerUid != uid || ownerGid != gid)
        {
            throw NotTheServiceAccount(request, ownerUid, ownerGid, whose);
        }
    }

    /// <summary>The refusal of a backup or restore not run as the account <paramref name="ownerUid"/>, naming that account.</summary>
    private static InstallerException NotTheServiceAccount(LifecycleRequest request, uint ownerUid, uint ownerGid, string whose)
    {
        var name = NativeLinux.UserName(ownerUid);
        var account = name ?? string.Create(CultureInfo.InvariantCulture, $"uid {ownerUid}");
        var command = request.Operation == LifecycleOperationKind.Backup ? "backup" : $"restore {request.Archive}";
        return InstallerException.Refused(string.Create(
            CultureInfo.InvariantCulture,
            $"{whose} its service account {account} (uid {ownerUid}, gid {ownerGid}), and a backup or restore runs as that account alone: run it as {account}, not with sudo or as root: hvo-sky-install cameraagent {command} --instance-id {request.InstanceId:D}. Nothing was changed."));
    }
}
