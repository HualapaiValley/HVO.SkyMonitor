using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed record DestructiveTreeNode(
    string RelativePath,
    uint Uid,
    uint Gid,
    int Mode,
    uint Type,
    uint LinkCount,
    ulong Inode,
    uint DeviceMajor,
    uint DeviceMinor,
    ulong MountId);

internal sealed record PurgeDeletionEvidence(
    int SchemaVersion,
    Guid OperationId,
    string RequestSha256,
    string HostIdentitySha256,
    InstanceManifest Manifest,
    IReadOnlyList<DestructiveTreeNode> Tree);

internal sealed record CatalogGarbageCollectionEvidence(
    int SchemaVersion,
    Guid OperationId,
    string RequestSha256,
    string HostIdentitySha256,
    DockerDaemonIdentity DockerDaemon,
    string PackageVersion,
    CatalogInstallationIdentity Catalog,
    IReadOnlyList<DestructiveTreeNode> Tree,
    bool DeletionCommitted = false,
    DateTimeOffset? CompletedUtc = null);

internal sealed record CatalogInstallOperationEvidence(
    int SchemaVersion,
    Guid OperationId,
    string RequestSha256,
    string Phase,
    InstallationStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset UpdatedUtc,
    string? AcquiredBundleSha256 = null,
    CatalogInstallationIdentity? InstalledCatalog = null,
    string? FailureMessage = null);

internal static class LocalHostIdentity
{
    public static string ReadSha256()
    {
        var machineId = File.ReadAllText("/etc/machine-id").Trim();
        if (machineId.Length == 0) throw new InstallerException("The local machine identity is unavailable.");
        return ComposeDeployment.ComputeSha256(machineId);
    }
}
