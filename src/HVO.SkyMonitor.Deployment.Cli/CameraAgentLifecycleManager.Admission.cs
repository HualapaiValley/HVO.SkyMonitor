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
    /// and state-contract checks and the state preflight. A registry image is pulled last, once every check that only
    /// reads has passed, so no refusal here follows the pull; a refused or failed pull is removed again, so its refusal
    /// still leaves Docker as it was. The command and its plan both pass here first, and a plan pulls nothing. Returns
    /// the release, with what its registry gave, and the names the pull added, which a refusal under the locks removes
    /// again while nothing is written (#1315 PR-1355-R3 R3-1).
    /// </summary>
    private static async Task<(AcquiredImage? Release, IReadOnlyList<string> Imported)> AdmitBeforeWritingAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        ComposeFiles compose,
        DockerClient docker,
        AcquiredImage? signedImage,
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
        IReadOnlyList<string> imported = [];
        if (kind == LifecycleOperationKind.Upgrade && signedImage is { ArchivedImage: null } && !request.DryRun)
        {
            imported = await PullReleaseCandidateAsync(request, manifest, docker, signedImage, cancellationToken).ConfigureAwait(false);
        }
        return (signedImage, imported);
    }

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
