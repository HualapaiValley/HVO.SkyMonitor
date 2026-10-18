using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>What <c>cameraagent backup --plan</c> and <c>cameraagent restore --plan</c> report (#1353).</summary>
internal sealed partial class CameraAgentLifecycleManager
{
    private static DeploymentPlan BackupPlan(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        IBackupVault vault,
        string name,
        bool running,
        IReadOnlyList<BackupItem> items,
        IReadOnlyList<BackupPartial> partials,
        IReadOnlyList<string> prunable)
    {
        var bytes = items.Sum(static item => item.Size);
        var plan = new List<DeploymentPlanItem>
        {
            new(running ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "container", Container(manifest),
                running
                    ? "capture is paused and drained, and the container stopped while the snapshot and archive are written, then started, verified and resumed"
                    : "not running; left as it is"),
            new(DeploymentPlanAction.Create, "snapshot", Path.Combine(paths.OperationsRoot, "lifecycle"),
                "the small databases, verified while the instance is stopped"),
            new(DeploymentPlanAction.Create, "file", Path.Combine(vault.Root, name), string.Create(
                CultureInfo.InvariantCulture,
                $"root-only archive of {items.Count(static item => !item.Directory)} files ({FormatBytes(bytes)}) and the snapshot, read back and proved whole before it is committed with its record"))
        };
        plan.AddRange(partials.Select(partial => new DeploymentPlanItem(
            DeploymentPlanAction.Remove, "file", Path.Combine(vault.Root, partial.Name + BackupVaultFiles.PartialSuffix),
            "partial archive an interrupted backup left; it can never be restored")));
        plan.AddRange(prunable.Select(static root => new DeploymentPlanItem(
            DeploymentPlanAction.Remove, "snapshot", root, "database snapshot nothing refers to any more")));
        plan.Add(new(DeploymentPlanAction.Unchanged, "folder", paths.InstanceRoot, "the instance's files are read, never changed"));
        return DeploymentPlan.Of(plan);
    }

    private static DeploymentPlan RestorePlan(
        LifecycleRequest request,
        InstallationPaths paths,
        ArchivedInstance archived,
        string name,
        bool absent)
    {
        var relative = Path.Combine("state", CameraAgentStateLayout.IdentityDirectoryName, CameraAgentStateLayout.IdentityDatabaseFileName);
        var liveCredentials = !absent && File.Exists(Path.Combine(paths.InstanceRoot, relative));
        return DeploymentPlan.Of(
        [
            new(DeploymentPlanAction.Unchanged, "file", name, string.Create(
                CultureInfo.InvariantCulture,
                $"backup taken {archived.Archive.CreatedUtc:yyyy-MM-ddTHH:mm:ssZ}, read and proved whole")),
            new(absent ? DeploymentPlanAction.Create : DeploymentPlanAction.Change, "folder", paths.InstanceRoot,
                absent
                    ? "the instance's files, as the backup holds them"
                    : "the instance's files, replaced by the backup's; what they replace is kept until the restore completes"),
            new(DeploymentPlanAction.Change, "databases", paths.StateRoot, string.Create(
                CultureInfo.InvariantCulture,
                $"restored from the backup's snapshot {archived.Archive.StateSnapshot.SnapshotId:D}, carrying every counter the replaced instance handed out")),
            new(DeploymentPlanAction.Change, "credentials", Path.Combine(paths.InstanceRoot, relative),
                liveCredentials
                    ? "API keys and owner passwords the live instance revoked after the backup are revoked again"
                    : "WARNING: no live credential store to compare; credentials revoked after the backup become valid again"),
            new(DeploymentPlanAction.Change, "file", paths.ManifestPath,
                $"instance manifest: uninstalled; start it with 'hvo-sky-install cameraagent reinstall --instance-id {archived.Manifest.InstanceId:D}'"),
            RecordItem(request, paths, archived.Manifest, DeploymentPlanAction.Change)
        ]);
    }
}
