using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The plans the instance lifecycle commands report with <c>--plan</c> (install contract section 5), and the install
/// record each completed operation settles.
/// </summary>
internal sealed partial class CameraAgentLifecycleManager
{
    /// <summary>
    /// Settles the instance's install record entry once <paramref name="kind"/> has committed <paramref name="manifest"/>.
    /// Each caller does so before it records completion, so an interruption or a record that cannot be written leaves the
    /// operation resumable and <c>--resume</c> finishes it.
    /// </summary>
    private static Task<DeploymentPlanAction> SettleInstallRecordAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        LifecycleOperationKind kind,
        CancellationToken cancellationToken)
        => InstallRecordStore.SettleInstanceAsync(
            InstallRecordLocation.For(paths.ProductRoot, request.ProductionProductRoot),
            paths,
            manifest,
            RecordEvent(kind),
            cancellationToken);

    /// <summary>Removes a purged instance from the install record before the purge records completion.</summary>
    private static Task<DeploymentPlanAction> RemoveFromInstallRecordAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        Guid instanceId,
        CancellationToken cancellationToken)
        => InstallRecordStore.RemoveInstanceAsync(
            InstallRecordLocation.For(paths.ProductRoot, request.ProductionProductRoot),
            instanceId,
            cancellationToken);

    private static InstallRecordEvent RecordEvent(LifecycleOperationKind kind) => kind switch
    {
        LifecycleOperationKind.Upgrade => InstallRecordEvent.Upgrade,
        LifecycleOperationKind.Rollback => InstallRecordEvent.Rollback,
        _ => InstallRecordEvent.Settle
    };

    /// <summary>
    /// What an upgrade or a rollback changes: the container's image and everything recorded with it, and for a rollback
    /// the limits Docker here cannot enforce, which it goes ahead without (#1357).
    /// </summary>
    private static DeploymentPlan ImagePlan(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        ImageInstallationIdentity candidate,
        bool rollback,
        bool stateSnapshotRequired,
        LifecycleStateSnapshot? retainedSnapshot,
        HostPrerequisiteReport? hostLimits)
    {
        var resources = rollback ? manifest.Resources : CameraAgentContainerResources.UpgradeTarget(manifest.Resources);
        var limits = resources is null
            ? CameraAgentContainerResources.Describe(CameraAgentContainerResources.Legacy)
            : CameraAgentContainerResources.Describe(resources);
        var unenforced = hostLimits is null ? [] : HostPrerequisites.UnenforcedLimitNames(hostLimits);
        if (unenforced.Count > 0)
        {
            limits += unenforced.Count == 1
                ? $", without the {unenforced[0]} limit Docker here cannot enforce"
                : $", without the {string.Join(", ", unenforced.Take(unenforced.Count - 1))} and {unenforced[^1]} limits Docker here cannot enforce";
        }
        var items = new List<DeploymentPlanItem>
        {
            new(DeploymentPlanAction.Change, "container", Container(manifest),
                $"image {manifest.Image.ImageId} to {candidate.ImageId}, {limits}; capture is paused and drained first"),
            new(DeploymentPlanAction.Change, "file", Path.Combine(paths.ConfigRoot, "compose", "compose.yml"), "Compose model"),
            new(DeploymentPlanAction.Change, "file", paths.ManifestPath, "instance manifest")
        };
        if (retainedSnapshot is not null)
        {
            items.Add(new(DeploymentPlanAction.Change, "databases", paths.StateRoot,
                $"restored from snapshot {retainedSnapshot.SnapshotId:D}; database changes made since the upgrade are lost"));
        }
        if (stateSnapshotRequired)
        {
            items.Add(new(DeploymentPlanAction.Create, "snapshot", Path.Combine(paths.OperationsRoot, "lifecycle"),
                "the small databases, before the candidate starts"));
        }
        items.Add(new(DeploymentPlanAction.Unchanged, "folder", paths.StateRoot,
            "frames, archives and derived products are never copied"));
        items.Add(RecordItem(request, paths, manifest, DeploymentPlanAction.Change));
        return DeploymentPlan.Of(items);
    }

    private static DeploymentPlan UninstallPlan(LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest)
        => DeploymentPlan.Of(
        [
            new(manifest.LifecycleCondition == InstanceLifecycleCondition.Installed ? DeploymentPlanAction.Remove : DeploymentPlanAction.Unchanged,
                "container", Container(manifest), "capture is paused and drained first"),
            new(DeploymentPlanAction.Change, "file", paths.ManifestPath, "instance manifest: uninstalled"),
            new(DeploymentPlanAction.Unchanged, "folder", paths.ConfigRoot, "configuration and secrets are preserved"),
            new(DeploymentPlanAction.Unchanged, "folder", paths.StateRoot, "state is preserved"),
            RecordItem(request, paths, manifest, DeploymentPlanAction.Change)
        ]);

    private static DeploymentPlan ReinstallPlan(LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest)
        => DeploymentPlan.Of(
        [
            new(DeploymentPlanAction.Create, "container", Container(manifest), $"image {manifest.Image.ImageId}"),
            new(DeploymentPlanAction.Change, "file", paths.ManifestPath, "instance manifest: installed"),
            RecordItem(request, paths, manifest, DeploymentPlanAction.Change)
        ]);

    private static DeploymentPlan PurgePlan(LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest)
        => DeploymentPlan.Of(
        [
            new(DeploymentPlanAction.Remove, "folder", paths.InstanceRoot, "configuration, secrets and state"),
            new(DeploymentPlanAction.Unchanged, "folder", paths.CatalogRoot, "shared catalogs are kept"),
            RecordItem(request, paths, manifest, DeploymentPlanAction.Remove)
        ]);

    private static DeploymentPlanItem RecordItem(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        DeploymentPlanAction action)
        => new(action, "record", InstallRecordLocation.For(paths.ProductRoot, request.ProductionProductRoot).Path,
            $"instance {manifest.InstanceId:D}");

    private static string Container(InstanceManifest manifest) => $"hvo-skymonitor-{manifest.InstanceId:N}";
}
