using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal sealed partial class CameraAgentLifecycleManager
{
    // RC2 does not understand the stopped-writer authorization or terminal preserved disposition.
    private const int SignedRecoveryJournalVersion = 3;
    private const string SignedAutomaticRecoveryPreparing = "signed-upgrade-recovery-preparing";
    private const string SignedAutomaticRecoveryPending = "signed-upgrade-recovery-pending";
    private const string SignedAutomaticRestoreHeld = "signed-upgrade-checkpoint-restore-held";

    private static bool IsPendingSignedAutomaticRecovery(LifecycleOperationState operation)
        => operation is
        {
            Phase: LifecycleOperationPhase.Restoring,
            FailureCode: SignedAutomaticRecoveryPreparing or SignedAutomaticRecoveryPending
        };

    private static bool IsAdmissionPreserved(LifecycleOperationState operation)
        => operation is
        {
            Kind: LifecycleOperationKind.Upgrade, Phase: LifecycleOperationPhase.RecoveryAdmissionPreserved,
            Status: InstallationStatus.Failed, MutationStarted: false,
            SchemaVersion: SignedRecoveryJournalVersion or LeavePausedJournalVersion
        };

    private static bool IsValidSignedRecoveryJournal(LifecycleOperationState operation)
    {
        if (operation is not
            {
                SchemaVersion: SignedRecoveryJournalVersion, Kind: LifecycleOperationKind.Upgrade,
                SignedReleaseChecksumsSha256: not null, LeavePausedOriginalSnapshot: null,
                LeavePausedOriginalRestorePlanId: null
            }) return false;
        return operation.Phase switch
        {
            LifecycleOperationPhase.RecoveryAdmissionPreserved => IsAdmissionPreserved(operation),
            LifecycleOperationPhase.Restoring => operation.MutationStarted &&
                operation.FailureCode is SignedAutomaticRecoveryPreparing or SignedAutomaticRecoveryPending or SignedAutomaticRestoreHeld,
            LifecycleOperationPhase.Restored => IsRestored(operation),
            _ => false
        };
    }

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

    private static async Task<bool> OwnsRetainedAutomaticPauseAsync(
        InstallationPaths paths, LifecycleOperationState operation, LifecycleContinuity continuity,
        CancellationToken cancellationToken)
    {
        return operation.PreMutationContinuity is { CaptureState: "Paused" } original &&
            IsValidRecoveryBoundary(original) && continuity.CaptureInitialized && continuity.CaptureState == "Paused" &&
            continuity.CaptureVersion == original.CaptureVersion && continuity.CaptureSequence >= original.CaptureSequence &&
            operation.SignedRecoveryPauseCommandId is { } expected &&
            await ReadAutomaticRecoveryPauseAsync(paths, operation.OperationId, original, cancellationToken).ConfigureAwait(false) == expected;
    }

    private static async Task RestoreRecoveryRecordsAsync(InstallationPaths paths, ComposeFiles compose, ComposeFiles previous,
        (InstanceManifest Manifest, InstallationResult Result) original, string operationRoot, CancellationToken cancellationToken)
    {
        SafeFileSystem.WriteTextAtomic(compose.ComposeFile, await ReadRecoveryTextAsync(previous.ComposeFile, cancellationToken).ConfigureAwait(false));
        SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, await ReadRecoveryTextAsync(previous.EnvironmentFile, cancellationToken).ConfigureAwait(false));
        SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(compose.ComposeFile)!, paths.ProductRoot);
        await WriteRecoverySnapshotAsync(paths, original.Manifest, original.Result, cancellationToken).ConfigureAwait(false);
        RestorePausedUpgradeRollbackHistory(paths, operationRoot);
    }

    private static async Task StartPendingSignedPredecessorAsync(
        InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation,
        DockerClient docker, ComposeFiles compose, HostPrerequisiteReport? hostLimits, string operationRoot,
        CancellationToken cancellationToken)
    {
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        if (runtime.Exists && runtime.ImageId != manifest.Image.ImageId &&
            (runtime.Running || runtime.ImageId != operation.CandidateImage?.ImageId))
            throw InstallerException.Refused("The pending signed recovery could not identify an exact predecessor or stopped candidate.");
        if (!runtime.Running)
        {
            if (runtime.Exists)
            {
                var image = runtime.ImageId == manifest.Image.ImageId ? manifest.Image : operation.CandidateImage!;
                await docker.VerifyContainerOwnershipAsync(compose, paths, image, manifest.RuntimeUid,
                    manifest.RuntimeGid, cancellationToken).ConfigureAwait(false);
                await docker.VerifyRecoveryContainerAsync(compose, paths, manifest, cancellationToken).ConfigureAwait(false);
            }
            await StartOnThisHostAsync(docker, compose, hostLimits, operationRoot, cancellationToken).ConfigureAwait(false);
        }
        await docker.VerifyRecoveryContainerAsync(compose, paths, manifest, cancellationToken).ConfigureAwait(false);
    }

    private static async Task StopSignedCandidateWithoutCheckpointAsync(
        InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation,
        DockerClient docker, ComposeFiles previous, CancellationToken cancellationToken)
    {
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        var runtime = await docker.InspectContainerAsync(previous.ContainerName, cancellationToken).ConfigureAwait(false);
        var runnerName = previous.ReplayRunnerContainerName;
        var runner = runnerName is null ? null : await docker.InspectContainerAsync(runnerName, cancellationToken).ConfigureAwait(false);
        var stopRequired = runtime.Running && runtime.ImageId != manifest.Image.ImageId ||
            runner is { Running: true } && (!runtime.Running || runner.ImageId != manifest.Image.ImageId);
        if (runtime.Running && !stopRequired) return;

        if (runtime.Exists)
        {
            var image = RequireRecoveryImage(runtime);
            await docker.VerifyContainerOwnershipAsync(previous, paths, image, manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken)
                .ConfigureAwait(false);
            await docker.VerifyRecoveryContainerAsync(previous, paths, manifest, cancellationToken).ConfigureAwait(false);
        }
        if (runner is { Running: true })
            await docker.VerifyReplayRunnerAsync(previous, paths, RequireRecoveryImage(runner), manifest.RuntimeUid, manifest.RuntimeGid,
                requireReadiness: false, cancellationToken).ConfigureAwait(false);
        if (stopRequired)
        {
            await docker.ComposeAsync(previous.ComposeFile, previous.EnvironmentFile, previous.ProjectName, ["stop"], cancellationToken)
                .ConfigureAwait(false);
            var stopped = await docker.InspectContainerAsync(previous.ContainerName, cancellationToken).ConfigureAwait(false);
            if (stopped.Running || stopped.Exists != runtime.Exists || stopped.ImageId != runtime.ImageId ||
                runnerName is not null && (await docker.InspectContainerAsync(runnerName, cancellationToken).ConfigureAwait(false)).Running)
                throw InstallerException.Refused("The exact signed candidate and its writers could not be proved stopped.");
        }
        OwnerRecoverySocket.RemoveStoppedSocket(paths, manifest.RuntimeUid, manifest.RuntimeGid);

        ImageInstallationIdentity RequireRecoveryImage(DockerClient.ContainerRuntimeIdentity observed)
        {
            if (observed.ImageId == manifest.Image.ImageId) return manifest.Image;
            if (operation.CandidateImage is { } candidate && observed.ImageId == candidate.ImageId) return candidate;
            throw InstallerException.Refused("Signed recovery could not identify the exact candidate or predecessor runtime.");
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
            var authorized = operation.AuthorizedSignedRestoreSnapshotId == checkpoint.SnapshotId;
            if (operation.StateRestorePlan?.SnapshotId == checkpoint.SnapshotId)
            {
                if (!authorized)
                    throw InstallerException.Refused("The interrupted restore lacks a recorded stopped-writer proof.");
                VerifyCompletedRestorePlan(checkpoint, verified, operation.StateRestorePlan);
            }

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
            var runnerRunning = compose.ReplayRunnerContainerName is { } replay &&
                (await docker.InspectContainerAsync(replay, cancellationToken).ConfigureAwait(false)).Running;
            if (authorized && (runtime.Running || runnerRunning))
                throw InstallerException.Refused("A writer restarted after the signed checkpoint replacement was authorized.");
            if (runtime.Running || runnerRunning)
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

            if (authorized) return;
            await ProveFleetRestoreAsync(paths, checkpoint, verified, cancellationToken).ConfigureAwait(false);
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
                SchemaVersion = SignedRecoveryJournalVersion,
                Phase = LifecycleOperationPhase.Restoring,
                Status = InstallationStatus.Failed,
                FailureCode = SignedAutomaticRestoreHeld,
                FailureMessage = Redaction.SafeDiagnostic(exception.Message)
            }, cancellationToken).ConfigureAwait(false);
            throw InstallerException.SignedRecoveryHeld(exception);
        }
    }

    private static async Task ProveFleetRestoreAsync(InstallationPaths paths, LifecycleStateSnapshot checkpoint,
        StateSnapshotManifest verified, CancellationToken cancellationToken)
    {
        var archived = CameraAgentStateSnapshot.StoreFile(paths, checkpoint, verified, FleetStorePath);
        if (archived is null)
        {
            EnsureFleetStoresAbsent(paths);
            return;
        }

        var retained = CameraAgentStateSnapshot.StorePath(paths.StateRoot, FleetStorePath);
        if (await ReadStoreFingerprintAsync(archived, "Fleet", "fleet_status_records", cancellationToken).ConfigureAwait(false) !=
            await ReadStoreFingerprintAsync(retained, "Fleet", "fleet_status_records", cancellationToken).ConfigureAwait(false))
            throw InstallerException.Refused("The stopped candidate changed Fleet records or metadata after the checkpoint.");
    }

    private static Task<LifecycleOperationState> PreserveRecoveryAdmissionAsync(
        InstallationPaths paths, LifecycleOperationState operation, LifecycleContinuityBoundary admission,
        string reason, CancellationToken cancellationToken)
    {
        if (!IsValidRecoveryBoundary(admission) || admission.CaptureState == "PauseRequested")
            throw InstallerException.Refused("The predecessor did not provide an initialized recovery boundary.");
        return RecordAsync(paths, operation with
        {
            SchemaVersion = operation.SchemaVersion == LeavePausedJournalVersion ? LeavePausedJournalVersion : SignedRecoveryJournalVersion,
            Phase = LifecycleOperationPhase.RecoveryAdmissionPreserved,
            Status = InstallationStatus.Failed,
            MutationStarted = false,
            FailureCode = reason,
            FailureMessage = operation.FailureMessage ?? "The interrupted upgrade was settled without changing capture admission.",
            PostMutationContinuity = admission,
            RestoreResumeCommandId = null,
            AuthorizedSignedRestoreSnapshotId = null
        }, cancellationToken);
    }

    private static string AutomaticRecoveryOutcome(LifecycleOperationState operation)
        => IsAdmissionPreserved(operation)
            ? operation.FailureCode!
            : RecoveryOutcome(operation.PreMutationContinuity!, operation.PostMutationContinuity!.CaptureVersion);

    private const string RecoveryAdmissionPreserved = "restored-predecessor-admission-preserved";
    private const string RecoveryAdmissionUnsupported = "restored-predecessor-admission-preserved-unsupported-atomic-resume";

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
        var before = ToBoundary(await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false));
        if (before.CaptureState == "PauseRequested")
            before = ToBoundary(await lifecycle.ConfirmDrainedAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false));
        if (!IsValidRecoveryBoundary(before) || before.CaptureState == "PauseRequested")
            throw InstallerException.Refused("The verified predecessor has no settled admission observation. Automatic recovery remains pending.");

        // A cancellation can occur after the durable Pause but before the drain observation is journaled.
        if (operation.PreMutationContinuity is null && before.CaptureState == "Paused")
        {
            before = ToBoundary(await lifecycle.ConfirmDrainedAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false));
            if (await ReadAutomaticRecoveryPauseAsync(paths, operation.OperationId, before, cancellationToken).ConfigureAwait(false) is { } recoveredPause)
                operation = await RecordAsync(paths, operation with
                {
                    PreMutationContinuity = before,
                    SignedRecoveryPauseCommandId = recoveredPause
                }, cancellationToken).ConfigureAwait(false);
        }
        if (operation.PreMutationContinuity is not { CaptureState: "Paused", CaptureVersion: < long.MaxValue } boundary ||
            !IsValidRecoveryBoundary(boundary) || operation.SignedRecoveryPauseCommandId is not { } pauseCommandId)
            return await PreserveRecoveryAdmissionAsync(paths, operation, before, RecoveryAdmissionPreserved, cancellationToken).ConfigureAwait(false);

        var paused = before.CaptureState == "Paused" && before.CaptureVersion == boundary.CaptureVersion;
        if (before.CaptureVersion < boundary.CaptureVersion || before.CaptureSequence < boundary.CaptureSequence)
            throw InstallerException.Refused("The predecessor's current admission regressed from the retained upgrade boundary.");
        if (!paused && operation.RestoreResumeCommandId is null)
            return await PreserveRecoveryAdmissionAsync(paths, operation, before, RecoveryAdmissionPreserved, cancellationToken).ConfigureAwait(false);

        if (operation.RestoreResumeCommandId is null)
            operation = await RecordAsync(paths, operation with { RestoreResumeCommandId = Guid.NewGuid() }, cancellationToken)
                .ConfigureAwait(false);
        var disposition = await lifecycle.TryResumeOwnedUpgradeAsync(operation.OperationId, pauseCommandId,
            operation.RestoreResumeCommandId!.Value, boundary.CaptureVersion, lifecycleControlToken, cancellationToken)
            .ConfigureAwait(false);
        if (disposition.Receipt is not { } receipt)
        {
            var current = ToBoundary(await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false));
            return await PreserveRecoveryAdmissionAsync(paths, operation, current,
                disposition.Supported ? RecoveryAdmissionPreserved : RecoveryAdmissionUnsupported, cancellationToken).ConfigureAwait(false);
        }
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

    private const string SignedRetryAdmissionPreserved = "completed-admission-preserved";
    private const string SignedRetryAdmissionUnsupported = "completed-admission-preserved-unsupported-atomic-resume";

    private static bool IsCommittedSignedRetry(LifecycleOperationState state)
        => state is
        {
            Phase: LifecycleOperationPhase.Committed, Kind: LifecycleOperationKind.Upgrade, MutationStarted: true,
            SignedReleaseChecksumsSha256: not null, SignedRecoveryPauseCommandId: not null,
            PreMutationContinuity.CaptureState: "Paused", RestoreResumeCommandId: not null,
            LeavePausedOriginalSnapshot: null
        };

    private static async Task<LifecycleResult> SettleCommittedSignedRetryAsync(
        LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation,
        VerifiedRelease release, ICameraAgentLifecycleClient lifecycle, string lifecycleControlToken,
        CancellationToken cancellationToken)
    {
        var boundary = operation.PreMutationContinuity!;
        if (!IsValidRecoveryBoundary(boundary) || boundary.CaptureVersion == long.MaxValue)
            throw InstallerException.Refused("The committed signed retry has no valid retained pause boundary.");
        var outcome = operation.SignedRetryAdmissionOutcome;
        if (outcome is null)
        {
            var disposition = await lifecycle.TryResumeOwnedUpgradeAsync(operation.OperationId, operation.SignedRecoveryPauseCommandId!.Value,
                operation.RestoreResumeCommandId!.Value, boundary.CaptureVersion, lifecycleControlToken, cancellationToken)
                .ConfigureAwait(false);
            outcome = disposition.Supported ? SignedRetryAdmissionPreserved : SignedRetryAdmissionUnsupported;
            if (disposition.Receipt is { } receipt)
            {
                var current = ToBoundary(await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false));
                if (!IsValidRecoveryReceipt(boundary, receipt) || !IsRecoveryAdmissionAfterResume(boundary, current) ||
                    receipt.CompletedUtc > current.RecordedUtc)
                    throw new InstallerException("The committed signed retry did not prove the owned resume receipt.");
                if (current.CaptureVersion == receipt.Version && current.CaptureState == receipt.State) outcome = "completed";
            }
            operation = await RecordAsync(paths, operation with { SignedRetryAdmissionOutcome = outcome }, cancellationToken)
                .ConfigureAwait(false);
        }
        await SettleInstallRecordAsync(request, paths, manifest, operation.Kind, cancellationToken, release).ConfigureAwait(false);
        // Older installers reject a resume command ID in Committed. Keep it as a fence until the release programs
        // have settled and the terminal, legacy-readable journal can no longer dispatch any resume.
        await CompleteAsync(paths, operation with { RestoreResumeCommandId = null }, cancellationToken).ConfigureAwait(false);
        return Result(operation.Kind, outcome, operation.OperationId, paths, manifest, manifest.DockerDaemon, true, true);
    }
}
