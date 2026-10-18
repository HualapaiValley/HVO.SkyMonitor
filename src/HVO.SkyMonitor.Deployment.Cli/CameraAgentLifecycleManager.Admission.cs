using System.Text;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The refusals a lifecycle command decides before its first write (#1315): a refusal says that nothing changed, so it
/// is decided while nothing has. The command decides the same again under its locks; a refusal that still comes once
/// this run has written, a lock file or the journal, is a journaled failure that exits 1.
/// </summary>
internal sealed partial class CameraAgentLifecycleManager
{
    private const string AlreadyActive = "The candidate image is already active.";

    /// <summary>
    /// Every refusal a lifecycle command can decide without changing anything: the instance's condition, a rollback with
    /// no previous image, an older release, the journal gates, the purge gates, a release candidate as read from its
    /// archive or its registry, and for an image change whose candidate is known without importing it, the already-active
    /// and state-contract checks and the state preflight, and for an image change the host's container limits (#1357).
    /// A registry image is pulled last, once every check that only
    /// reads has passed, so no refusal here follows the pull; a refused or failed pull is removed again, so its refusal
    /// still leaves Docker as it was. The command and its plan both pass here first, and a plan pulls nothing. Returns
    /// the release, with what its registry gave, and the names the pull added, which a refusal under the locks removes
    /// again while nothing is written (#1315 PR-1355-R3 R3-1), and for an image change the host limits it was admitted
    /// with.
    /// </summary>
    private static async Task<(AcquiredImage? Release, IReadOnlyList<string> Imported, HostPrerequisiteReport? HostLimits)> AdmitBeforeWritingAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        ComposeFiles compose,
        DockerClient docker,
        DockerHostCapabilities? capabilities,
        AcquiredImage? signedImage,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var kind = request.Operation!.Value;
        var imageChange = kind is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback;
        if (imageChange)
        {
            if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
                throw InstallerException.Refused("An uninstalled instance must be reinstalled before image lifecycle operations.");
            if (kind == LifecycleOperationKind.Rollback && manifest.PreviousImage is null)
                throw InstallerException.Refused("No previous image is retained for rollback.");
            if (kind == LifecycleOperationKind.Upgrade && request.TargetRelease is { } targetRelease)
            {
                _ = UpgradeNotes.Plan(
                    await UpgradeNotes.InstalledVersionAsync(paths, manifest.Image, cancellationToken).ConfigureAwait(false),
                    targetRelease,
                    manifest.InstanceId);
            }
        }
        if (kind == LifecycleOperationKind.Reinstall && manifest.LifecycleCondition != InstanceLifecycleCondition.Uninstalled &&
            !request.Resume)
        {
            throw InstallerException.Refused("Reinstall requires a preserved uninstalled instance.");
        }
        if (kind == LifecycleOperationKind.Purge)
        {
            if (request.ConfirmRemovedOperationId is not null)
                throw InstallerException.Refused("The instance root exists, so it was not removed; purge refuses --confirm-removed-operation-id.");
            if (manifest.LifecycleCondition != InstanceLifecycleCondition.Uninstalled)
                throw InstallerException.Refused("Purge requires a completed preserve-by-default uninstall.");
            if ((await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false)).Exists)
                throw InstallerException.Refused("Purge refused because the instance container still exists.");
        }

        var resumed = await AdmitAsync(request, paths, kind, manifest, cancellationToken).ConfigureAwait(false);
        // A backup restarts an installed instance as it was, so its host limits are held as a rollback's are (#1357).
        var hostLimits = imageChange || kind == LifecycleOperationKind.Backup && manifest.LifecycleCondition == InstanceLifecycleCondition.Installed
            ? AdmitHostLimits(request, paths, manifest, capabilities!)
            : null;
        if (kind == LifecycleOperationKind.Backup && resumed is null)
        {
            await AdmitBackupAsync(request, paths, manifest, cancellationToken).ConfigureAwait(false);
        }
        if (kind == LifecycleOperationKind.Upgrade && signedImage is not null)
        {
            signedImage = await CheckReleaseCandidateAsync(manifest, docker, signedImage, cancellationToken).ConfigureAwait(false);
        }
        if (imageChange && resumed is null)
        {
            await AdmitCandidateAsync(request, paths, manifest, docker, signedImage, cancellationToken).ConfigureAwait(false);
        }
        // Every check that only reads has admitted the rollback, so it goes ahead: the warning comes now, before anything
        // changes, so a plan gives it too.
        await WarnOfUnenforcedLimitsAsync(request, hostLimits, error).ConfigureAwait(false);
        IReadOnlyList<string> imported = [];
        if (kind == LifecycleOperationKind.Upgrade && signedImage is { ArchivedImage: null } && !request.DryRun)
        {
            imported = await PullReleaseCandidateAsync(request, manifest, docker, signedImage, cancellationToken).ConfigureAwait(false);
        }
        return (signedImage, imported, hostLimits);
    }

    /// <summary>
    /// The host's container limits an image change is held to before it changes anything (#1357, install contract
    /// section 7, as HVO.RoofController#151). An upgrade, its plan and its <c>--resume</c> refuse a host whose Docker
    /// cannot enforce the CameraAgent limits, or has fewer CPUs than the CPU limit, with every finding and its remedy, as
    /// an install does; there is no override. A rollback and a restore-only recovery are the recovery path: they go
    /// ahead, with <see cref="WarnOfUnenforcedLimitsAsync"/>'s warning. A rollback is still refused for a CPU limit
    /// Docker enforces on a host with fewer CPUs, a container Docker refuses to create. A restore-only recovery is never
    /// refused here: it puts back the image the instance ran, from its exact retained Compose files. A backup of an
    /// installed instance is held as a rollback is, since it starts the instance it stopped again (#1353). Returns the
    /// report a recovery goes ahead with.
    /// </summary>
    private static HostPrerequisiteReport AdmitHostLimits(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        DockerHostCapabilities capabilities)
    {
        var upgrade = request.Operation == LifecycleOperationKind.Upgrade && !request.RestoreOnly;
        var report = HostPrerequisites.EvaluateContainerLimits(
            capabilities,
            paths.ProductRoot,
            LinuxHostEnvironment.Instance,
            upgrade
                ? CameraAgentContainerResources.UpgradeTarget(manifest.Resources)
                : manifest.Resources ?? CameraAgentContainerResources.Legacy,
            rollback: !upgrade);
        if (!report.Satisfied && !request.RestoreOnly)
        {
            throw new HostPrerequisiteException(report);
        }
        return report;
    }

    /// <summary>
    /// The loud warning of each limit a recovery goes ahead without (#1357), on standard error once the recovery is
    /// admitted and before it changes anything. A <c>--json</c> invocation reserves standard error for one-line JSON
    /// objects, so its result carries the warnings instead (<see cref="WithHostLimitWarnings"/>).
    /// </summary>
    private static async Task WarnOfUnenforcedLimitsAsync(LifecycleRequest request, HostPrerequisiteReport? hostLimits, TextWriter error)
    {
        if (!request.Json && hostLimits is not null && HostLimitsWarning(request, hostLimits) is { } warning)
        {
            await error.WriteLineAsync(warning).ConfigureAwait(false);
        }
    }

    private static string RecoveryName(LifecycleRequest request) => request.RestoreOnly ? "restore-only recovery" : "rollback";

    /// <summary>
    /// The warning of each limit <paramref name="request"/> goes ahead without (#1357). A backup is not a recovery, but it
    /// starts the instance it stopped again, on the image it ran, so it goes ahead as a rollback does (#1353).
    /// </summary>
    private static string? HostLimitsWarning(LifecycleRequest request, HostPrerequisiteReport hostLimits)
        => request.Operation == LifecycleOperationKind.Backup
            ? HostPrerequisites.RollbackWarning(
                hostLimits, "backup", "A backup starts the instance it stopped again, on the image it ran")
            : HostPrerequisites.RollbackWarning(hostLimits, RecoveryName(request));

    /// <summary>The result with the warnings of each limit the recovery went ahead without (#1357).</summary>
    private static LifecycleResult WithHostLimitWarnings(LifecycleResult result, HostPrerequisiteReport? hostLimits)
        => hostLimits is null || HostPrerequisites.RollbackWarnings(hostLimits) is not { Count: > 0 } warnings
            ? result
            : result with { Warnings = [.. result.Warnings ?? [], .. warnings] };

    /// <summary>
    /// Writes the warning of each limit a recovery goes ahead without (#1357) to the operation's install log,
    /// <c>operations/lifecycle/&lt;operation&gt;/install.log</c>, beside its other retained records, stamped with the
    /// time. Each run that goes ahead adds its entry. Nothing is written for a host that enforces every limit.
    /// </summary>
    private static async Task WriteHostLimitsToInstallLogAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        Guid operationId,
        HostPrerequisiteReport? hostLimits,
        CancellationToken cancellationToken)
    {
        if (hostLimits is null || HostLimitsWarning(request, hostLimits) is not { } warning)
        {
            return;
        }
        var lifecycleRoot = Path.Combine(paths.OperationsRoot, "lifecycle");
        var operationRoot = Path.Combine(lifecycleRoot, operationId.ToString("D"));
        SafeFileSystem.CreateOwnerDirectory(lifecycleRoot);
        SafeFileSystem.CreateOwnerDirectory(operationRoot);
        var path = Path.Combine(operationRoot, InstallLogName);
        var existing = string.Empty;
        if (File.Exists(path))
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            existing = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        SafeFileSystem.WriteTextAtomic(path, existing + FormattableString.Invariant($"{DateTimeOffset.UtcNow:O} {warning}\n"));
    }

    /// <summary>The operation's install log, which a recovery that goes ahead without some limits writes (#1357).</summary>
    internal const string InstallLogName = "install.log";

    /// <summary>
    /// The checks of an upgrade's or a rollback's candidate that only read: the already-active and state-contract checks
    /// and, for a candidate known without importing it, the state preflight.
    /// </summary>
    private static async Task AdmitCandidateAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        DockerClient docker,
        AcquiredImage? signedImage,
        CancellationToken cancellationToken)
    {
        if (request.Operation == LifecycleOperationKind.Rollback)
        {
            if (manifest.PreviousImage!.ImageId == manifest.Image.ImageId) throw InstallerException.Refused(AlreadyActive);
            if (!CameraAgentStateContract.IsKnown(manifest.Image.UpgradeCompatibility) &&
                !CameraAgentStateContract.IsKnown(manifest.UpgradeCompatibility))
            {
                throw InstallerException.Refused("Automatic rollback is forbidden because the active image did not declare a supported state contract.");
            }
            return;
        }
        // A release names every identity Docker can give its image, so a release whose image is the active one is
        // known to be active before anything is imported.
        if (signedImage?.Image.ImageIds is { } releaseIdentities && releaseIdentities.Contains(manifest.Image.ImageId, StringComparer.Ordinal))
        {
            throw InstallerException.Refused(AlreadyActive);
        }
        var candidate = await CandidateWithoutImportAsync(request, manifest, docker, signedImage, cancellationToken).ConfigureAwait(false);
        if (candidate is null)
        {
            // The candidate is known only once it is imported, after the journal entry; its checks run there.
            return;
        }
        if (candidate.ImageId == manifest.Image.ImageId) throw InstallerException.Refused(AlreadyActive);
        if (!CameraAgentStateContract.IsCurrent(candidate.UpgradeCompatibility))
        {
            throw InstallerException.Refused(
                $"The candidate image declares state compatibility '{CameraAgentStateContract.Describe(candidate.UpgradeCompatibility)}' instead of the supported '{CameraAgentStateContract.Current}' contract; an explicit state-disposition procedure is required.");
        }
        // The state preflight the upgrade makes before it changes anything, read-only: a bind source the command would
        // create or normalize is evaluated as the command leaves it.
        var preparedSources = new HashSet<string>(
            ComposeDeployment.WritableStateDirectories(paths.StateRoot)
                .Where(directory => SafeFileSystem.CheckRuntimeDirectory(directory, manifest.RuntimeUid, manifest.RuntimeGid)),
            StringComparer.Ordinal);
        await CameraAgentStatePreflight.EnsureCompatibleAsync(
                paths,
                manifest.InstanceId,
                candidate,
                manifest.Image.UpgradeCompatibility ?? manifest.UpgradeCompatibility,
                manifest.RuntimeUid,
                manifest.RuntimeGid,
                manifest.ReplayProfile,
                persist: false,
                renderToStandardError: !request.Json,
                cancellationToken,
                CameraAgentStateContractPolicy.RequireCurrent,
                signedImage?.Release.Tag,
                preparedSources: preparedSources)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The candidate of an upgrade as far as it is known without importing anything: a release's image as read from its
    /// archive, or the image Docker already holds. <see langword="null"/> when only the import would show it.
    /// </summary>
    private static async Task<ImageInstallationIdentity?> CandidateWithoutImportAsync(
        LifecycleRequest request,
        InstanceManifest manifest,
        DockerClient docker,
        AcquiredImage? signedImage,
        CancellationToken cancellationToken)
    {
        if (signedImage?.CheckedImage is { } checkedImage)
        {
            return checkedImage;
        }
        if (signedImage is null && request.ImageArchive is not null)
        {
            return null;
        }
        if (signedImage is not null && !await docker.HoldsReleaseImageAsync(signedImage.Image, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }
        try
        {
            var synthetic = ImageRequest(manifest, request.ImageReference!, null, null, noDownload: true);
            return (await docker.PrepareImageAsync(synthetic, allowMutation: false, signedImage?.Image, cancellationToken)
                .ConfigureAwait(false)).Image;
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            // Docker does not hold it: the upgrade imports it once it has begun, and checks it there.
            return null;
        }
    }

    /// <summary>
    /// What a lifecycle command has written so far that a refusal could not undo: the lifecycle journal, by its inode,
    /// length and write time, and whether each lock file exists.
    /// </summary>
    private sealed record WrittenState(string? Journal, bool ProductLock, bool InstanceLock);

    private static WrittenState ReadWrittenState(InstallationPaths paths)
    {
        string? journal = null;
        if (File.Exists(paths.LifecycleStatePath))
        {
            var node = NativeLinux.GetNodeIdentity(paths.LifecycleStatePath);
            var info = new FileInfo(paths.LifecycleStatePath);
            journal = FormattableString.Invariant($"{node.Inode}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        }
        return new WrittenState(
            journal,
            File.Exists(Path.Combine(paths.OperationsRoot, "deployment.lock")),
            File.Exists(Path.Combine(paths.InstanceRoot, ".deployment.lock")));
    }
}
