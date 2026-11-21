using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Data.Sqlite;

namespace HVO.SkyMonitor.Deployment;

internal sealed record LeavePausedReceipt(
    Guid OperationId, Guid InstanceId, string RequestSha256, string ReleaseChecksumsSha256, string ReleaseManifestSha256,
    string ImageId, LifecycleStateSnapshot OriginalCheckpoint, LifecycleStateSnapshot RetryCheckpoint,
    LifecycleAdmissionObservation Admission);

internal sealed partial class CameraAgentLifecycleManager
{
    // Published RC2 refuses a lifecycle journal of this version before dispatching any command.
    private const int LeavePausedJournalVersion = 2;
    private const string FleetStorePath = "raw/.fleet/fleet-status.db";

    private static bool ValidLifecycleJournalVersion(LifecycleOperationState state)
        => state.SchemaVersion == DeploymentSchemaVersions.LifecycleOperation &&
           state.LeavePausedOriginalSnapshot is null && state.LeavePausedOriginalRestorePlanId is null ||
           state.Kind == LifecycleOperationKind.Upgrade && state.RestoreResumeCommandId is null &&
           state.RestoreResumeReceipt is null && state.LeavePausedOriginalSnapshot is { } original &&
           original.OperationId == state.OperationId && original.SnapshotId == state.LeavePausedOriginalRestorePlanId &&
           state.SignedReleaseChecksumsSha256 == PublishedRc2ChecksumsSha256 &&
           (state is { SchemaVersion: LeavePausedJournalVersion, MutationStarted: true, Status: not InstallationStatus.Completed } ||
            state is { SchemaVersion: DeploymentSchemaVersions.LifecycleOperation, Phase: LifecycleOperationPhase.Completed });

    // This is the cut immediately after arming, before the phase that precedes candidate startup.
    private static bool IsArmedPausedRetry(LifecycleOperationState state)
        => state is
        {
            SchemaVersion: LeavePausedJournalVersion,
            Phase: LifecycleOperationPhase.Restoring or LifecycleOperationPhase.CandidateValidated or LifecycleOperationPhase.Prepared,
            Status: InstallationStatus.Running, StateSnapshot: not null, StateSnapshotRestoredUtc: not null
        } && state.StateSnapshot == state.LeavePausedOriginalSnapshot &&
        state.StateRestorePlan?.SnapshotId == state.LeavePausedOriginalRestorePlanId;

    private static bool IsQuietPausedRecovery(LifecycleOperationState state)
        => state is
        {
            SchemaVersion: LeavePausedJournalVersion, Phase: LifecycleOperationPhase.Restoring, Status: InstallationStatus.Failed,
            FailureCode: "leave-paused-predecessor-restored", StateSnapshot: not null, StateSnapshotRestoredUtc: not null
        } && state.StateSnapshot != state.LeavePausedOriginalSnapshot && state.StateRestorePlan?.SnapshotId == state.StateSnapshot.SnapshotId;

    private static string LeavePausedReceiptPath(InstallationPaths paths, Guid operationId)
        => Path.Combine(paths.OperationsRoot, "lifecycle", operationId.ToString("D"), "leave-paused-receipt.json");

    internal static void EnsureFleetStoresAbsent(InstallationPaths paths)
    {
        var fleet = CameraAgentStateSnapshot.StorePath(paths.StateRoot, FleetStorePath);
        var directory = Path.GetDirectoryName(fleet)!;
        SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(directory)!);
        if (NativeLinux.TryGetNodeIdentity(directory) is { Type: not 0x4000 })
            throw InstallerException.Refused("Leave-paused could not authenticate the Fleet store directory.");
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (NativeLinux.TryGetNodeIdentity(fleet + suffix) is not null)
                throw InstallerException.Refused("Leave-paused requires an unconfigured Fleet with no Fleet SQLite store or sidecars.");
    }

    private static async Task VerifyRunningPausedFleetAsync(
        InstallationPaths paths, IOwnerBootstrapClient owner, CancellationToken cancellationToken)
    {
        await owner.VerifyFleetDisabledAsync(cancellationToken).ConfigureAwait(false);
        EnsureFleetStoresAbsent(paths);
    }

    internal static async Task<StateSnapshotManifest> VerifyPausedCheckpointAsync(
        InstallationPaths paths, Guid instanceId, LifecycleStateSnapshot checkpoint, ImageInstallationIdentity originalImage,
        StateRestorePlan? restorePlan, CancellationToken cancellationToken)
    {
        var verified = await CameraAgentStateSnapshot.VerifyAsync(paths, instanceId, checkpoint, cancellationToken)
            .ConfigureAwait(false);
        if (verified.Image != originalImage || verified.ProtectedFiles is null)
            throw InstallerException.Refused("Leave-paused requires the predecessor's paired checkpoint with Fleet absent.");
        EnsureFleetCheckpointAbsent(verified);
        if (restorePlan is not null) VerifyCompletedRestorePlan(checkpoint, verified, restorePlan);
        return verified;
    }

    private static void EnsureFleetCheckpointAbsent(StateSnapshotManifest checkpoint)
    {
        if (checkpoint.Databases.SingleOrDefault(static item => item.RelativePath == FleetStorePath) is not { Present: false })
            throw InstallerException.Refused("Leave-paused requires the predecessor's paired checkpoint with Fleet absent.");
    }

    internal static async Task VerifyPausedCommitEvidenceAsync(
        InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation, CancellationToken cancellationToken)
    {
        if (operation.LeavePausedOriginalSnapshot is not { } original || operation.StateSnapshot is not { } retry ||
            operation.LeavePausedOriginalRestorePlanId != original.SnapshotId ||
            original.OperationId != operation.OperationId || retry.OperationId != operation.OperationId ||
            original.SnapshotId == retry.SnapshotId || original.ManifestSha256 == retry.ManifestSha256 ||
            retry.RecordedUtc < original.RecordedUtc || operation.StateSnapshotRestoredUtc is not null ||
            operation.StateRestorePlan is not null ||
            manifest.Image == operation.CandidateImage && manifest.PreviousStateSnapshot != retry)
            throw InstallerException.Refused("Leave-paused requires separate proven original and retry checkpoints.");
        if (NativeLinux.TryGetNodeIdentity(CameraAgentStateSnapshot.SnapshotRoot(paths, original.OperationId, original.SnapshotId)) is null)
            throw InstallerException.Refused("The original working checkpoint is unavailable. Leave-paused replay cannot verify it.");
        await VerifyPausedCheckpointAsync(paths, manifest.InstanceId, original, operation.OriginalImage!, null, cancellationToken)
            .ConfigureAwait(false);
        await VerifyPausedCheckpointAsync(paths, manifest.InstanceId, retry, operation.OriginalImage!, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LifecycleResult> ObserveQuietPausedRecoveryAsync(
        InstallationPaths paths, InstanceManifest manifest, InstallationResult installation, LifecycleOperationState operation,
        LifecycleResult proof, Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory, string token,
        CancellationToken cancellationToken)
    {
        var original = operation.LeavePausedOriginalSnapshot!;
        var retry = operation.StateSnapshot!;
        if (original.SnapshotId == retry.SnapshotId || original.ManifestSha256 == retry.ManifestSha256)
            throw InstallerException.Refused("The held recovery does not contain distinct checkpoint evidence.");
        await VerifyPausedCheckpointAsync(paths, manifest.InstanceId, original, manifest.Image, null, cancellationToken)
            .ConfigureAwait(false);
        var retained = await VerifyPausedStoresAsync(paths, manifest, operation, retry, cancellationToken).ConfigureAwait(false);
        var lifecycle = CreateLifecycleClient(installation.Url, lifecycleClientFactory);
        var admission = await ObserveAdmissionAsync(paths, lifecycle, token, cancellationToken).ConfigureAwait(false);
        if (admission.State != "Paused" || admission.Version != operation.PreMutationContinuity!.CaptureVersion ||
            admission.CaptureSequence != operation.PreMutationContinuity.CaptureSequence ||
            admission.LatestCommandOrder != retained.Latest.Order ||
            admission.LatestCommandKeySha256 != ComposeDeployment.ComputeSha256(retained.Latest.IdempotencyKey))
            throw InstallerException.Refused("The verified predecessor is running but its admission changed. The recovery remains held.");
        return proof with { Outcome = "restored-predecessor-admission-preserved", PreservedAdmission = admission };
    }

    private static void EnsureUnchangedPausedBoundary(LifecycleContinuityBoundary original, LifecycleContinuity current)
    {
        if (!current.CaptureInitialized || current.CaptureState != "Paused" || original.CaptureState != "Paused" ||
            current.CaptureVersion != original.CaptureVersion || current.CaptureSequence != original.CaptureSequence)
            throw new InstallerException("The capture admission version or sequence changed during the leave-paused retry.");
    }

    private static async Task VerifyPausedRetrySnapshotAsync(
        InstallationPaths paths, Guid instanceId, LifecycleOperationState operation,
        LifecycleStateSnapshot snapshot, LifecycleContinuity continuity,
        CancellationToken cancellationToken)
    {
        var verified = await VerifyPausedCheckpointAsync(paths, instanceId, snapshot, operation.OriginalImage!, null, cancellationToken)
            .ConfigureAwait(false);
        var raw = CameraAgentStateSnapshot.PreflightView(paths, snapshot, verified).RawIngressDatabasePath;
        EnsureFleetStoresAbsent(paths);
        await ReadSignedPauseOwnerAsync(raw, operation.OperationId, operation.PreMutationContinuity!, cancellationToken)
            .ConfigureAwait(false);
        var boundary = await ReadDurableAdmissionAsync(raw, cancellationToken).ConfigureAwait(false);
        if (boundary.State != "paused" || boundary.Version != continuity.CaptureVersion ||
            boundary.CaptureSequence != continuity.CaptureSequence || boundary.Latest.Target != "paused")
            throw new InstallerException("The stopped predecessor's checkpoint does not match the verified paused admission.");
    }

    internal static async Task<LifecycleResult> SettlePausedUpgradeAsync(
        LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation,
        VerifiedRelease release, ICameraAgentLifecycleClient lifecycle, string lifecycleControlToken, CancellationToken cancellationToken)
    {
        var receiptPath = LeavePausedReceiptPath(paths, operation.OperationId);
        var retained = await ReadLeavePausedReceiptAsync(receiptPath, manifest, operation, release, cancellationToken).ConfigureAwait(false);
        // Keep the old-binary fence durable until both PATH and install-record settlement are complete.
        await SettleInstallRecordAsync(request, paths, manifest, operation.Kind, cancellationToken, release).ConfigureAwait(false);
        var admission = await ObserveAdmissionAsync(paths, lifecycle, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        if (retained is null)
        {
            var receipt = new LeavePausedReceipt(operation.OperationId, manifest.InstanceId, operation.RequestSha256,
                release.ChecksumsSha256, release.ManifestSha256, manifest.Image.ImageId, operation.LeavePausedOriginalSnapshot!,
                operation.StateSnapshot!, admission);
            await SafeFileSystem.WriteJsonAtomicAsync(receiptPath, receipt, DeploymentJsonContext.Default.LeavePausedReceipt, cancellationToken)
                .ConfigureAwait(false);
            SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(receiptPath)!, paths.ProductRoot);
        }
        await CompleteAsync(paths, operation with
        {
            SchemaVersion = DeploymentSchemaVersions.LifecycleOperation
        }, cancellationToken).ConfigureAwait(false);
        return PausedUpgradeResult(paths, manifest, operation, receiptPath, admission);
    }

    private static async Task<LifecycleResult> ObserveCompletedPausedUpgradeAsync(
        InstallationPaths paths, InstanceManifest manifest, InstallationResult installation, LifecycleOperationState operation,
        DockerClient docker, ComposeFiles compose, IOwnerBootstrapClient owner, Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        string lifecycleControlToken, string verificationToken, VerifiedRelease release, uint uid, uint gid, CancellationToken cancellationToken)
    {
        var receiptPath = LeavePausedReceiptPath(paths, operation.OperationId);
        _ = await ReadLeavePausedReceiptAsync(receiptPath, manifest, operation, release, cancellationToken).ConfigureAwait(false)
            ?? throw InstallerException.Refused("The completed operation has no leave-paused settlement receipt.");
        await VerifyPausedCommitEvidenceAsync(paths, manifest, operation, cancellationToken).ConfigureAwait(false);
        await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
        var expectedOwner = operation.ExpectedOwnerBootstrapState ?? installation.OwnerBootstrapState;
        await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installation, manifest.Image, verificationToken,
            uid, gid, cancellationToken, expectedOwner, allowCompletedPasswordReplacement: expectedOwner == "owner-password-change-required",
            readinessTimeout: ImageChangeReadinessTimeout)
            .ConfigureAwait(false);
        await VerifyRunningPausedFleetAsync(paths, owner, cancellationToken).ConfigureAwait(false);
        var lifecycle = CreateLifecycleClient(installation.Url, lifecycleClientFactory);
        var admission = await ObserveAdmissionAsync(paths, lifecycle, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        return PausedUpgradeResult(paths, manifest, operation, receiptPath, admission);
    }

    internal static void EnsurePausedUpgradeStorage(InstallationPaths paths, LifecycleRequest request, InstanceManifest manifest)
    {
        EnsureUpgradeStorage(paths, request.ImageArchive, manifest.ReplayProfile, rollback: false, snapshotRequired: false);
        EnsureUpgradeStorage(paths, request.ImageArchive, manifest.ReplayProfile, rollback: false, snapshotRequired: true);
    }

    private static async Task<LeavePausedReceipt?> ReadLeavePausedReceiptAsync(
        string path, InstanceManifest manifest, LifecycleOperationState operation, VerifiedRelease release,
        CancellationToken cancellationToken)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(path)!);
        if (NativeLinux.TryGetNodeIdentity(path) is null) return null;
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        var receipt = await DeserializeRetainedAsync(stream, DeploymentJsonContext.Default.LeavePausedReceipt,
            "leave-paused settlement receipt", cancellationToken).ConfigureAwait(false);
        if (receipt.OperationId != operation.OperationId || receipt.InstanceId != manifest.InstanceId ||
            receipt.RequestSha256 != operation.RequestSha256 || receipt.ReleaseChecksumsSha256 != release.ChecksumsSha256 ||
            receipt.ReleaseManifestSha256 != release.ManifestSha256 || receipt.ImageId != manifest.Image.ImageId ||
            operation.CandidateImage != manifest.Image || manifest.LastLifecycleOperationId != operation.OperationId ||
            receipt.RetryCheckpoint != operation.StateSnapshot || receipt.OriginalCheckpoint != operation.LeavePausedOriginalSnapshot ||
            receipt.Admission is not
            {
                State: "Paused" or "Running", Version: >= 0, CaptureSequence: >= 0,
                LatestCommandOrder: > 0, LatestCommandKeySha256: { } key
            } || !IsSha256(key) || receipt.Admission.LatestResultVersion != receipt.Admission.Version)
            throw InstallerException.Refused("The leave-paused settlement receipt does not match the released image and checkpoints.");
        return receipt;
    }

    private static LifecycleResult PausedUpgradeResult(
        InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation, string receiptPath,
        LifecycleAdmissionObservation admission)
    {
        var result = Result(operation.Kind, "completed-admission-preserved", operation.OperationId, paths, manifest,
            manifest.DockerDaemon, true, true);
        return result with
        {
            PreservedAdmission = admission,
            PreservedPaths = [.. result.PreservedPaths, receiptPath]
        };
    }

    private static async Task RecoverPausedUpgradeFailureAsync(
        LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest, InstallationResult installation,
        LifecycleOperationState operation, DockerClient docker, ComposeFiles compose, ComposeFiles candidateCompose,
        IOwnerBootstrapClient owner, string verificationToken, string expectedOwnerState, bool allowOwnerStateProgression,
        HostPrerequisiteReport? hostLimits, string operationRoot, TextWriter error, uint uid, uint gid, Exception failure)
    {
        using var preparation = new CancellationTokenSource(ImageRecoveryPreparationTimeout);
        var token = preparation.Token;
        var failedPhase = operation.Phase;
        operation = await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Restoring,
            Status = InstallationStatus.Failed,
            FailureCode = "leave-paused-recovery-held",
            FailureMessage = Redaction.SafeDiagnostic(failure.Message)
        }, token).ConfigureAwait(false);
        try
        {
            var retry = operation.StateSnapshot;
            if (failedPhase is not (LifecycleOperationPhase.Mutating or LifecycleOperationPhase.CandidateVerified) ||
                retry is null || retry == operation.LeavePausedOriginalSnapshot || operation.StateSnapshotRestoredUtc is not null)
                throw new InstallerException("A started candidate and its own unrestored retry checkpoint could not be proved.");
            await VerifyPausedCommitEvidenceAsync(paths, manifest, operation, token).ConfigureAwait(false);

            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(token).ConfigureAwait(false));
            await docker.VerifyContainerOwnershipAsync(candidateCompose, paths, operation.CandidateImage!, uid, gid, token).ConfigureAwait(false);
            await docker.VerifyRecoveryContainerAsync(candidateCompose, paths, manifest, token).ConfigureAwait(false);
            var running = await docker.InspectContainerAsync(candidateCompose.ContainerName, token).ConfigureAwait(false);
            if (!running.Exists || running.ImageId != operation.CandidateImage!.ImageId)
                throw new InstallerException("Recovery could not identify the exact candidate before stopping it.");
            await docker.ComposeAsync(candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName,
                ["stop"], token).ConfigureAwait(false);
            var stopped = await docker.InspectContainerAsync(candidateCompose.ContainerName, token).ConfigureAwait(false);
            if (!stopped.Exists || stopped.Running || stopped.ImageId != operation.CandidateImage!.ImageId ||
                candidateCompose.ReplayRunnerContainerName is { } runner &&
                    (await docker.InspectContainerAsync(runner, token).ConfigureAwait(false)).Running)
                throw new InstallerException("The exact candidate and its writers could not be proved stopped.");

            await VerifyPausedStoresAsync(paths, manifest, operation, retry, token).ConfigureAwait(false);

            // There is no application writer between this proof and replacement. If any earlier check fails, nothing is restored.
            var previousManifest = Path.Combine(operationRoot, "previous-instance-manifest.json");
            var previousResult = Path.Combine(operationRoot, "previous-installation-result.json");
            var original = await ReadRecoverySnapshotAsync(paths, operation, previousManifest, previousResult, token).ConfigureAwait(false);
            SafeFileSystem.WriteTextAtomic(compose.ComposeFile, await ReadRecoveryTextAsync(Path.Combine(operationRoot, "previous-compose.yml"), token).ConfigureAwait(false));
            SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, await ReadRecoveryTextAsync(Path.Combine(operationRoot, "previous.env"), token).ConfigureAwait(false));
            SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(compose.ComposeFile)!, paths.ProductRoot);
            await WriteRecoverySnapshotAsync(paths, original.Manifest, original.Result, token).ConfigureAwait(false);
            RestorePausedUpgradeRollbackHistory(paths, operationRoot);
            OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
            operation = await RestoreStateSnapshotAsync(paths, manifest, operation, retry, request.Json, error, token).ConfigureAwait(false);
            await StartOnThisHostAsync(docker, compose, hostLimits, operationRoot, token).ConfigureAwait(false);
            using var settlement = new CancellationTokenSource(ImageRecoverySettlementTimeout);
            await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installation, manifest.Image, verificationToken,
                uid, gid, settlement.Token, expectedOwnerState, allowOwnerStateProgression, readinessTimeout: ImageChangeReadinessTimeout)
                .ConfigureAwait(false);
            await VerifyRunningPausedFleetAsync(paths, owner, settlement.Token).ConfigureAwait(false);
            await RecordAsync(paths, operation with { FailureCode = "leave-paused-predecessor-restored" }, settlement.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InstallerException(
                $"Leave-paused recovery is held. Preserve the retained stores and both checkpoints. {Redaction.SafeDiagnostic(exception.Message)}",
                failure);
        }
    }

    private static void RestorePausedUpgradeRollbackHistory(InstallationPaths paths, string operationRoot)
    {
        var rollbackRoot = Path.Combine(paths.DeploymentStateRoot, "rollback");
        foreach (var name in new[] { "previous.env", "previous-compose.yml" })
        {
            var target = Path.Combine(rollbackRoot, name);
            var retained = Path.Combine(operationRoot, name == "previous.env" ? "prior-rollback.env" : "prior-rollback-compose.yml");
            if (File.Exists(retained))
                SafeFileSystem.WriteTextAtomic(target, File.ReadAllText(retained));
            else if (File.Exists(target))
                File.Delete(target);
        }
        if (Directory.Exists(rollbackRoot)) SafeFileSystem.FlushDirectoryChain(rollbackRoot, paths.ProductRoot);
    }

    private sealed record DurablePauseCommand(
        long Order, string IdempotencyKey, string Target, string Actor,
        string PayloadSha256, long ResultVersion, long CompletedUnixMs, bool Changed);

    private sealed record DurableAdmission(
        string State, long Version, long CaptureSequence, string AgentSequences,
        long AssignmentCount, long CaptureCount, DurablePauseCommand Latest);

    private static Task<DurableAdmission> ReadLiveAdmissionAsync(InstallationPaths paths, CancellationToken cancellationToken)
    {
        EnsureFleetStoresAbsent(paths);
        return ReadDurableAdmissionAsync(CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot), cancellationToken);
    }

    private static async Task<DurableAdmission> VerifyPausedStoresAsync(
        InstallationPaths paths, InstanceManifest manifest, LifecycleOperationState operation,
        LifecycleStateSnapshot checkpoint, CancellationToken cancellationToken)
    {
        var verified = await VerifyPausedCheckpointAsync(paths, manifest.InstanceId, checkpoint, operation.OriginalImage!,
            operation.StateSnapshotRestoredUtc is not null ? operation.StateRestorePlan : null, cancellationToken).ConfigureAwait(false);
        var original = CameraAgentStateSnapshot.PreflightView(paths, checkpoint, verified);
        var before = await ReadDurableAdmissionAsync(original.RawIngressDatabasePath, cancellationToken).ConfigureAwait(false);
        var after = await ReadLiveAdmissionAsync(paths, cancellationToken).ConfigureAwait(false);
        if (before.State != "paused" || before != after)
            throw new InstallerException("The retained candidate accepted a later capture command or recorded a different capture boundary.");
        var current = CameraAgentStateDatabaseView.Live(paths);
        if (await ReadIdentityFingerprintAsync(original.IdentityDatabasePath, cancellationToken).ConfigureAwait(false) !=
            await ReadIdentityFingerprintAsync(current.IdentityDatabasePath, cancellationToken).ConfigureAwait(false))
            throw new InstallerException("The retained Identity store changed after the checkpoint. Preserve both checkpoints and the retained stores.");
        EnsureFleetStoresAbsent(paths);
        return before;
    }

    internal static async Task<LifecycleAdmissionObservation> ObserveAdmissionAsync(
        InstallationPaths paths, ICameraAgentLifecycleClient lifecycle, string token, CancellationToken cancellationToken)
    {
        // A concurrent ordinary owner command may win. Report a consistent observation, without taking ownership.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var continuity = await lifecycle.ReadContinuityAsync(token, cancellationToken).ConfigureAwait(false);
            var current = await ReadLiveAdmissionAsync(paths, cancellationToken).ConfigureAwait(false);
            if (continuity.CaptureInitialized && current.Version == continuity.CaptureVersion &&
                current.CaptureSequence == continuity.CaptureSequence &&
                string.Equals(current.State, continuity.CaptureState, StringComparison.OrdinalIgnoreCase))
            {
                return new LifecycleAdmissionObservation(continuity.CaptureState, current.Version, current.CaptureSequence,
                    DateTimeOffset.UtcNow, current.Latest.Order, ComposeDeployment.ComputeSha256(current.Latest.IdempotencyKey),
                    current.Latest.Target, AdmissionActor(current.Latest.Actor), current.Latest.Changed, current.Latest.ResultVersion,
                    DateTimeOffset.FromUnixTimeMilliseconds(current.Latest.CompletedUnixMs));
            }
        }
        throw new InstallerException("Capture admission changed while the installer was reading the handoff. Retry the same leave-paused command.");
    }

    private static string AdmissionActor(string actor)
    {
        const string InstallerPrefix = "installer-lifecycle:";
        if (Guid.TryParseExact(actor, "D", out var owner)) return owner.ToString("D");
        if (actor.StartsWith(InstallerPrefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(actor.AsSpan(InstallerPrefix.Length), "D", out var installer))
            return InstallerPrefix + installer.ToString("D");
        return "sha256:" + ComposeDeployment.ComputeSha256(actor);
    }

    private static async Task<DurableAdmission> ReadDurableAdmissionAsync(string raw, CancellationToken cancellationToken)
    {
        try
        {
            SafeFileSystem.EnsureSafeExistingAncestors(raw);
            using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(raw);
            using var command = database.Connection.CreateCommand();
            command.CommandText = """
                SELECT s.state, s.version, c.rowid, c.idempotency_key, c.target_state, c.actor,
                    c.payload_sha256, c.result_version, c.completed_unix_ms,
                    (SELECT COALESCE(MAX(last_sequence), 0) FROM raw_capture_sequences),
                    (SELECT COALESCE(MAX(capture_sequence), 0) FROM raw_capture_assignments),
                    (SELECT COALESCE(MAX(capture_sequence), 0) FROM raw_captures),
                    (SELECT COUNT(*) FROM raw_capture_assignments), (SELECT COUNT(*) FROM raw_captures),
                    (SELECT json_group_array(json_array(agent_id, last_sequence) ORDER BY agent_id) FROM raw_capture_sequences),
                    c.changed
                FROM capture_control_state s CROSS JOIN capture_control_commands c
                WHERE s.state_key = 1 AND c.rowid = (SELECT MAX(rowid) FROM capture_control_commands)
                    AND c.status = 'completed' AND c.result_state = s.state AND c.result_version = s.version;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetString(0) is not ("paused" or "running"))
                throw new InstallerException("The current capture state and latest accepted command could not be correlated.");
            var last = new DurablePauseCommand(reader.GetInt64(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt64(7), reader.GetInt64(8), reader.GetBoolean(15));
            var rawSequence = Math.Max(reader.GetInt64(9), Math.Max(reader.GetInt64(10), reader.GetInt64(11)));
            return new DurableAdmission(reader.GetString(0), reader.GetInt64(1), rawSequence,
                reader.GetString(14), reader.GetInt64(12), reader.GetInt64(13), last);
        }
        catch (SqliteException exception)
        {
            throw new InstallerException("The durable capture command and sequence could not be proved.", exception);
        }
    }

    internal static async Task<string> ReadIdentityFingerprintAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(path)!);
            RequireIdentityFile(NativeLinux.TryGetNodeIdentity(path));
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                if (NativeLinux.TryGetNodeIdentity(path + suffix) is { } sidecar)
                    RequireIdentityFile(sidecar);
            using var database = CameraAgentStatePreflight.ReadOnlyDatabase.Open(path);
            using (var check = database.Connection.CreateCommand())
            {
                check.CommandText = "PRAGMA quick_check(1);";
                if (await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not "ok")
                    throw new InstallerException("The retained Identity store could not be authenticated.");
            }
            using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var tables = new List<string>();
            using (var schema = database.Connection.CreateCommand())
            {
                schema.CommandText = "SELECT name, sql FROM sqlite_schema WHERE type = 'table' ORDER BY name;";
                await using var reader = await schema.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    var name = reader.GetString(0);
                    tables.Add(name);
                    Add("table", name);
                    Add("schema", reader.GetString(1));
                }
            }
            if (!tables.Contains("AspNetUsers", StringComparer.Ordinal))
                throw new InstallerException("The checkpoint or retained Identity store is uninitialized.");
            foreach (var table in tables)
            {
                using var query = database.Connection.CreateCommand();
                // SQLite identifiers come from the verified database schema and are delimited with escaped quotes.
#pragma warning disable CA2100
                query.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" ORDER BY rowid;";
#pragma warning restore CA2100
                Add("read", table);
                await using var rows = await query.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
                    for (var column = 0; column < rows.FieldCount; column++)
                        switch (rows.GetValue(column))
                        {
                            case DBNull: Add("null", ""); break;
                            case string value: Add("text", value); break;
                            case long value: Add("integer", value.ToString(CultureInfo.InvariantCulture)); break;
                            case double value: Add("real", value.ToString("R", CultureInfo.InvariantCulture)); break;
                            case byte[] value: Add("blob", Convert.ToBase64String(value)); break;
                            default: throw new InstallerException("The retained Identity store could not be authenticated.");
                        }
            }
            return Convert.ToHexStringLower(fingerprint.GetHashAndReset());

            void Add(string type, string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                fingerprint.AppendData(Encoding.ASCII.GetBytes(type + ":" + bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
                fingerprint.AppendData(bytes);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            throw new InstallerException("The checkpoint or retained Identity store could not be authenticated. Recovery remains held.", exception);
        }

        static void RequireIdentityFile(UnixNodeIdentity? identity)
        {
            if (identity is not { Type: NativeLinux.RegularFileNodeType, LinkCount: 1 } node || node.Uid != NativeLinux.getuid())
                throw new InstallerException("The checkpoint or retained Identity file could not be authenticated. Recovery remains held.");
        }
    }
}
