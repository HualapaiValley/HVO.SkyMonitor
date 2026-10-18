using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// Who a backup or restore runs as (#1353 PR-1369-R1 F2/F3, contract parity with RoofController's
/// <c>sudo hvo-roof-install backup</c>). A backup holds the instance's secrets, so under the production product root
/// both commands run as root, through <c>sudo</c>, and keep their archives in root's <c>/var/backups/hvo-sky</c>; run
/// otherwise, they are refused. As root they become the instance's runtime user at once
/// (<see cref="RootPrivilege.DropTo"/>), so every file they write belongs to that user as when it runs any lifecycle
/// command, and they are root again only for the backup folder, the machine install record and the preparation of an
/// absent instance root. Under a test product root they also run as the root's own user, for the tests.
/// </summary>
internal sealed partial class CameraAgentLifecycleManager
{
    private static async Task<(uint Uid, uint Gid)> EnterBackupPrivilegeAsync(
        LifecycleRequest request,
        IProcessRunner processRunner,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var production = string.Equals(request.ProductRoot, request.ProductionProductRoot, StringComparison.Ordinal);
        if (uid != 0)
        {
            if (production)
            {
                var command = request.Operation == LifecycleOperationKind.Backup ? "backup" : $"restore {request.Archive}";
                throw InstallerException.Refused(
                    $"A backup holds the instance's secrets, so backups are root's: run it with sudo, sudo hvo-sky-install cameraagent {command} --instance-id {request.InstanceId:D}.");
            }
            return (uid, gid);
        }
        var paths = InstallationPaths.Create(request.ProductRoot, request.InstanceId!.Value, ProductionCatalog.DefaultCatalogId);
        uint owner;
        uint group;
        if (Directory.Exists(paths.InstanceRoot))
        {
            var identity = NativeLinux.GetDirectoryIdentity(paths.InstanceRoot);
            (owner, group) = (identity.Uid, identity.Gid);
        }
        else if (request.Operation == LifecycleOperationKind.Restore)
        {
            // An absent instance belongs to the runtime user the backup's record names.
            var folder = BackupVault.RootFolder(request);
            var record = new BackupVaultFiles(folder, requireRootAncestry: true).ReadRecord(request.Archive!)
                ?? throw InstallerException.Refused(
                    $"'{request.Archive}' is not a committed backup in {folder}: a partial or unrecorded archive is never restored.");
            (owner, group) = (record.RuntimeUid, record.RuntimeGid);
        }
        else
        {
            throw InstallerException.Refused($"There is no instance {request.InstanceId:D} under {request.ProductRoot}.");
        }
        if (owner == 0)
        {
            throw InstallerException.Refused($"{paths.InstanceRoot} belongs to root; a backup or restore works as the instance's runtime user.");
        }
        var account = await processRunner.RunAsync("getent", ["passwd", owner.ToString(CultureInfo.InvariantCulture)], cancellationToken)
            .ConfigureAwait(false);
        var fields = account.ExitCode == 0 ? account.StandardOutput.Trim().Split(':') : [];
        if (fields.Length < 7 || fields[2] != owner.ToString(CultureInfo.InvariantCulture) || fields[0].Length == 0 || !Path.IsPathFullyQualified(fields[5]))
        {
            throw InstallerException.Refused(string.Create(
                CultureInfo.InvariantCulture, $"The instance's runtime user, uid {owner}, has no account on this host."));
        }
        LoadNativeLibraries();
        RootPrivilege.DropTo(owner, group, fields[0], fields[5]);
        return (owner, group);
    }

    /// <summary>
    /// Loads the native libraries the command uses while it is still root. A published installer extracts them into
    /// root's home when it starts, where the runtime user it becomes cannot read them.
    /// </summary>
    private static void LoadNativeLibraries()
    {
        using (var database = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:"))
        {
            database.Open();
        }
        using var compression = new System.IO.Compression.GZipStream(Stream.Null, System.IO.Compression.CompressionLevel.Fastest);
        compression.WriteByte(0);
    }
}
