using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed partial class CameraAgentLifecycleManager
{
    private const string SignedAutomaticRecoveryPending = "signed-upgrade-recovery-pending";
    private const string SignedAutomaticRestoreHeld = "signed-upgrade-checkpoint-restore-held";

    private static bool IsPendingSignedAutomaticRecovery(LifecycleOperationState operation)
        => operation is { Phase: LifecycleOperationPhase.Restoring, FailureCode: SignedAutomaticRecoveryPending };

    private static async Task<Guid?> ReadAutomaticRecoveryPauseAsync(
        InstallationPaths paths, Guid operationId, LifecycleContinuityBoundary boundary, CancellationToken cancellationToken)
    {
        if (boundary.CaptureState != "Paused" || !IsValidRecoveryBoundary(boundary)) return null;
        try
        {
            return await ReadSignedPauseOwnerAsync(CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot),
                operationId, boundary, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException)
        {
            // A compatible signed upgrade can run even if its initial pause was already in force. Its automatic
            // recovery cannot claim that pause or use it to reopen admission.
            return null;
        }
    }

    private static void VerifyAutomaticRecoveryRelease(LifecycleOperationState operation, AcquiredImage release)
    {
        if (release.VerifiedRelease.ChecksumsSha256 !=
            (operation.SignedReleaseChecksumsSha256 ?? PublishedRc2ChecksumsSha256))
            throw InstallerException.Refused("Automatic signed recovery requires the original verified release checksums.");
    }

    private static async Task<StateSnapshotManifest> VerifyAutomaticRecoveryCheckpointAsync(
        InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation, LifecycleStateSnapshot checkpoint,
        CancellationToken cancellationToken)
    {
        var verified = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, checkpoint, cancellationToken)
            .ConfigureAwait(false);
        if (checkpoint.OperationId != operation.OperationId || verified.Image != operation.OriginalImage ||
            verified.Image != manifest.Image || verified.ProtectedFiles is null)
            throw InstallerException.Refused("Automatic signed recovery requires the original image's paired checkpoint.");
        return verified;
    }

    private static async Task ProveStoppedSignedRestoreAsync(
        InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation, LifecycleStateSnapshot checkpoint,
        DockerClient docker, ComposeFiles compose, string operationRoot, CancellationToken cancellationToken)
    {
        try
        {
            var verified = await VerifyAutomaticRecoveryCheckpointAsync(paths, manifest, operation, checkpoint, cancellationToken)
                .ConfigureAwait(false);
            if (operation.StateRestorePlan?.SnapshotId == checkpoint.SnapshotId)
                throw InstallerException.Refused("The checkpoint was already selected for a restore whose completion cannot be proved.");

            var previous = compose with
            {
                ComposeFile = Path.Combine(operationRoot, "previous-compose.yml"),
                EnvironmentFile = Path.Combine(operationRoot, "previous.env")
            };
            await ValidateComposeAuthorityAsync(docker, previous, manifest, cancellationToken).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));

            var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
            var image = runtime.ImageId == manifest.Image.ImageId ? manifest.Image : operation.CandidateImage;
            if (!runtime.Exists || image is null || runtime.ImageId != image.ImageId)
                throw InstallerException.Refused("The exact candidate or predecessor container could not be identified before checkpoint restore.");
            await docker.VerifyContainerOwnershipAsync(previous, paths, image, manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken)
                .ConfigureAwait(false);
            await docker.VerifyRecoveryContainerAsync(previous, paths, manifest, cancellationToken).ConfigureAwait(false);
            await docker.ComposeAsync(previous.ComposeFile, previous.EnvironmentFile, previous.ProjectName,
                ["stop"], cancellationToken).ConfigureAwait(false);
            var stopped = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
            if (!stopped.Exists || stopped.Running || stopped.ImageId != image.ImageId ||
                compose.ReplayRunnerContainerName is { } runner &&
                    (await docker.InspectContainerAsync(runner, cancellationToken).ConfigureAwait(false)).Running)
                throw InstallerException.Refused("The exact image and its writers could not be proved stopped before checkpoint restore.");
            await docker.VerifyContainerOwnershipAsync(previous, paths, image, manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken)
                .ConfigureAwait(false);
            await docker.VerifyRecoveryContainerAsync(previous, paths, manifest, cancellationToken).ConfigureAwait(false);

            var archived = CameraAgentStateSnapshot.PreflightView(paths, checkpoint, verified).RawIngressDatabasePath;
            var retained = CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot);
            // Fleet reports consume their own allocations even when captures are paused. Only the raw
            // capture journal witnesses capture assignments and accepted owner commands for this restore.
            ProveRawWitnessNodes(retained);
            var before = await ReadDurableAdmissionAsync(archived, cancellationToken).ConfigureAwait(false);
            var after = await ReadDurableAdmissionAsync(retained, cancellationToken).ConfigureAwait(false);
            if (operation.PreMutationContinuity is not { CaptureState: "Paused" } boundary ||
                !IsValidRecoveryBoundary(boundary) || before.State != "paused" ||
                before.Version != boundary.CaptureVersion || before.CaptureSequence > boundary.CaptureSequence ||
                before != after)
                throw InstallerException.Refused("The stopped candidate accepted a later capture command or changed its raw capture boundary.");
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException or
                                            System.Text.Json.JsonException or InvalidOperationException or InvalidCastException or
                                            FormatException or KeyNotFoundException)
        {
            await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Restoring,
                Status = InstallationStatus.Failed,
                FailureCode = SignedAutomaticRestoreHeld,
                FailureMessage = Redaction.SafeDiagnostic(exception.Message)
            }, cancellationToken).ConfigureAwait(false);
            throw InstallerException.Refused($"Automatic signed recovery is held. Preserve the retained stores and checkpoints. {Redaction.SafeDiagnostic(exception.Message)}");
        }
    }

    private static void ProveRawWitnessNodes(string database)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(database);
        if (NativeLinux.TryGetNodeIdentity(database) is not { Type: 0x8000 })
            throw InstallerException.Refused("The stopped raw capture journal could not be proved.");
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
            if (NativeLinux.TryGetNodeIdentity(database + suffix) is { Type: not 0x8000 })
                throw InstallerException.Refused("The stopped raw capture journal sidecars could not be proved.");
    }

    private static async Task<LifecycleOperationState> SettleSignedAutomaticRecoveryAsync(
        InstallationPaths paths, LifecycleOperationState operation, ICameraAgentLifecycleClient lifecycle,
        string lifecycleControlToken, CancellationToken cancellationToken)
    {
        if (operation.PreMutationContinuity is not { CaptureState: "Paused", CaptureVersion: < long.MaxValue } boundary ||
            !IsValidRecoveryBoundary(boundary) || operation.SignedRecoveryPauseCommandId is not { } pauseCommandId)
            throw InstallerException.Refused("The verified predecessor has no proven operation-owned changed pause. Automatic recovery remains pending.");

        var before = ToBoundary(await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false));
        var paused = before.CaptureState == "Paused" && before.CaptureVersion == boundary.CaptureVersion;
        if (!IsValidRecoveryBoundary(before) || before.CaptureSequence < boundary.CaptureSequence ||
            !paused && (operation.RestoreResumeCommandId is null || !IsRecoveryAdmissionAfterResume(boundary, before)))
            throw InstallerException.Refused("The predecessor's current admission differs from the retained upgrade boundary. Automatic recovery remains pending.");

        await lifecycle.VerifyOwnedUpgradeResumeAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        if (operation.RestoreResumeCommandId is null)
            operation = await RecordAsync(paths, operation with { RestoreResumeCommandId = Guid.NewGuid() }, cancellationToken)
                .ConfigureAwait(false);
        var receipt = await lifecycle.ResumeOwnedUpgradeAsync(operation.OperationId, pauseCommandId,
            operation.RestoreResumeCommandId!.Value, boundary.CaptureVersion, lifecycleControlToken, cancellationToken)
            .ConfigureAwait(false);
        var after = ToBoundary(await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false));
        if (!IsValidRecoveryReceipt(boundary, receipt) || !paused && !receipt.Replayed ||
            !IsRecoveryAdmissionAfterResume(boundary, after) || receipt.CompletedUtc > after.RecordedUtc)
            throw InstallerException.Refused("The predecessor did not prove the owned recovery resume. Automatic recovery remains pending.");

        return await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Restored,
            Status = InstallationStatus.Failed,
            MutationStarted = false,
            PostMutationContinuity = after,
            RestoreResumeReceipt = receipt
        }, cancellationToken).ConfigureAwait(false);
    }
}
