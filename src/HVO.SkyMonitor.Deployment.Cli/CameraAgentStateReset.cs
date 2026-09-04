using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed record CameraAgentStateResetEvidence(
    int SchemaVersion,
    Guid OperationId,
    Guid InstanceId,
    string RequestSha256,
    string HostIdentitySha256,
    InstanceManifest Manifest,
    IReadOnlyList<string> DeletedRoots,
    IReadOnlyList<string> PreservedPaths,
    IReadOnlyList<DestructiveTreeNode> Tree,
    bool DeletionCommitted = false,
    DateTimeOffset? CompletedUtc = null);

internal sealed record CameraAgentStateResetResult(
    int SchemaVersion,
    string Outcome,
    Guid OperationId,
    Guid InstanceId,
    string InstanceRoot,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<string> PreservedPaths,
    string EvidencePath,
    string NextCommand,
    DateTimeOffset CompletedUtc);

/// <summary>
/// Deletes only CameraAgent-owned local runtime state so an instance whose persisted state predates the image's
/// minimum compatible revision can be redeployed. Deployment configuration, secrets, credentials, installation
/// identity, catalogs, and every other instance's state stay outside the reset boundary.
/// </summary>
internal static class CameraAgentStateResetManager
{
    public const int SchemaVersion = DeploymentSchemaVersions.StateResetEvidence;

    public static Task<CameraAgentStateResetResult> ExecuteAsync(
        CameraAgentStateResetRequest request,
        CancellationToken cancellationToken)
        => ExecuteAsync(request, new ProcessRunner(), NativeLinux.getuid(), NativeLinux.getgid(), cancellationToken);

    internal static async Task<CameraAgentStateResetResult> ExecuteAsync(
        CameraAgentStateResetRequest request,
        IProcessRunner processRunner,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (uid == 0)
        {
            throw new InstallerException("Run the CameraAgent state reset as the Docker-capable runtime user, not as root.");
        }
        var instanceId = request.InstanceId!.Value;
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.CatalogId);
        var manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (manifest.InstanceId != instanceId || manifest.Component != DeploymentComponent.CameraAgent ||
            manifest.StateRoot != paths.StateRoot)
        {
            throw new InstallerException("The retained instance manifest does not correlate with the selected instance.");
        }
        if (manifest.RuntimeUid != uid || manifest.RuntimeGid != gid)
        {
            throw new InstallerException(
                $"The reset must run as the installed runtime identity {manifest.RuntimeUid}:{manifest.RuntimeGid}.");
        }
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Uninstalled)
        {
            throw new InstallerException(
                "The CameraAgent state reset requires a preserve-by-default uninstall first; run cameraagent uninstall --instance-id <uuid>.");
        }

        SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
        using var productLock = OperationLock.Acquire(
            Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(
            Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);

        var docker = new DockerClient(processRunner);
        await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        var runtime = await docker.InspectContainerAsync(
            ComposeDeployment.ContainerNameFor(instanceId), cancellationToken).ConfigureAwait(false);
        if (runtime.Exists)
        {
            throw new InstallerException("The CameraAgent state reset refused because the instance container still exists.");
        }
        await docker.EnsureNoInstanceReferencesAsync(instanceId, paths.InstanceRoot, cancellationToken).ConfigureAwait(false);

        // Everything the reset must be able to complete is validated before the first irreversible deletion, so a
        // rejected precondition can never leave an instance with deleted state and a retained completed result.
        var retainedState = await ReadInstallationStateAsync(paths, instanceId, cancellationToken).ConfigureAwait(false);
        var operationId = Guid.NewGuid();
        var deletable = CameraAgentStateLayout.ResettableStateDirectories(paths.StateRoot)
            .Where(Directory.Exists)
            .ToArray();
        var inventory = new List<DestructiveTreeNode>();
        foreach (var directory in deletable)
        {
            var name = Path.GetFileName(directory);
            foreach (var node in SafeTreeDeletion.CaptureChildInventory(paths.StateRoot, name, uid, gid))
            {
                inventory.Add(node with { RelativePath = $"{name}/{node.RelativePath}" });
            }
        }
        var preserved = PreservedPaths(paths);
        var evidencePath = Path.Combine(paths.OperationsRoot, $"state-reset-{operationId:D}.evidence.json");
        var evidence = new CameraAgentStateResetEvidence(
            SchemaVersion,
            operationId,
            instanceId,
            request.ComputeRequestSha256(),
            LocalHostIdentity.ReadSha256(),
            manifest,
            deletable,
            preserved,
            inventory);
        await SafeFileSystem.WriteJsonAtomicAsync(
            evidencePath, evidence, DeploymentJsonContext.Default.CameraAgentStateResetEvidence, cancellationToken)
            .ConfigureAwait(false);

        if (request.DryRun)
        {
            return new CameraAgentStateResetResult(
                SchemaVersion, "planned", operationId, instanceId, paths.InstanceRoot, deletable, preserved,
                evidencePath, NextCommand(instanceId), DateTimeOffset.UtcNow);
        }

        // Owner bootstrap must run again against the preserved temporary password, so the retained completed result
        // is withdrawn before the deletion while the installation identity, configuration, and secrets stay
        // untouched. An interruption after this point leaves a pending installation the preflight still guards.
        await RewindInstallationStateAsync(paths, retainedState, cancellationToken).ConfigureAwait(false);
        foreach (var directory in deletable)
        {
            SafeTreeDeletion.DeleteChild(paths.StateRoot, Path.GetFileName(directory), uid, gid);
        }
        foreach (var directory in ComposeDeployment.WritableStateDirectories(paths.StateRoot))
        {
            SafeFileSystem.CreateRuntimeDirectory(directory, uid, gid);
        }

        var completedUtc = DateTimeOffset.UtcNow;
        await SafeFileSystem.WriteJsonAtomicAsync(
            evidencePath,
            evidence with { DeletionCommitted = true, CompletedUtc = completedUtc },
            DeploymentJsonContext.Default.CameraAgentStateResetEvidence,
            cancellationToken).ConfigureAwait(false);
        return new CameraAgentStateResetResult(
            SchemaVersion, "completed", operationId, instanceId, paths.InstanceRoot, deletable, preserved,
            evidencePath, NextCommand(instanceId), completedUtc);
    }

    private static string NextCommand(Guid instanceId) => string.Create(
        CultureInfo.InvariantCulture,
        $"hvo-skymonitor cameraagent install --instance-id {instanceId:D} <same immutable inputs>");

    private static IReadOnlyList<string> PreservedPaths(InstallationPaths paths) =>
    [
        paths.ManifestPath,
        paths.ApplicationIdentityPath,
        paths.ConfigRoot,
        Path.Combine(paths.ConfigRoot, "secrets"),
        Path.Combine(paths.ConfigRoot, "owner-bootstrap"),
        Path.Combine(paths.ConfigRoot, "lifecycle-control"),
        Path.Combine(paths.ConfigRoot, "installation-verification"),
        Path.Combine(paths.ConfigRoot, "compose"),
        paths.CatalogRoot,
        paths.OperationsRoot,
        paths.BackupsRoot
    ];

    private static async Task<InstallationState> ReadInstallationStateAsync(
        InstallationPaths paths,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.StatePath))
        {
            throw new InstallerException("The retained installation state is required to reset CameraAgent state.");
        }
        InstallationState state;
        await using (var stream = SafeFileSystem.OpenOwnerFileRead(paths.StatePath))
        {
            state = await System.Text.Json.JsonSerializer.DeserializeAsync(
                        stream, DeploymentJsonContext.Default.InstallationState, cancellationToken).ConfigureAwait(false)
                    ?? throw new InstallerException("The retained installation state is empty.");
        }
        if (state.InstanceId != instanceId)
        {
            throw new InstallerException("The retained installation state belongs to a different instance.");
        }
        if (File.Exists(paths.ResultPath))
        {
            SafeFileSystem.ValidateOwnerFile(paths.ResultPath);
        }
        return state;
    }

    private static async Task RewindInstallationStateAsync(
        InstallationPaths paths,
        InstallationState state,
        CancellationToken cancellationToken)
    {
        await SafeFileSystem.WriteJsonAtomicAsync(
            paths.StatePath,
            state with
            {
                Phase = InstallationPhase.Preflight,
                Status = InstallationStatus.Pending,
                UpdatedUtc = DateTimeOffset.UtcNow,
                FailureCode = null,
                FailureMessage = null
            },
            DeploymentJsonContext.Default.InstallationState,
            cancellationToken).ConfigureAwait(false);
        File.Delete(paths.ResultPath);
        NativeLinux.FlushDirectory(paths.DeploymentStateRoot);
    }

    private static async Task<InstanceManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new InstallerException("The selected instance has no retained manifest.");
        }
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        return await System.Text.Json.JsonSerializer.DeserializeAsync(
                   stream, DeploymentJsonContext.Default.InstanceManifest, cancellationToken).ConfigureAwait(false)
               ?? throw new InstallerException("The selected instance manifest is empty.");
    }
}
