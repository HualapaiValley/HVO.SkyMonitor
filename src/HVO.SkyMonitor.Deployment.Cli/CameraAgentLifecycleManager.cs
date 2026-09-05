using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HVO.SkyMonitor.Deployment.Contracts;
using HVO.SkyMonitor.Deployment.Distribution;

namespace HVO.SkyMonitor.Deployment;

internal sealed class CameraAgentLifecycleManager
{
    public static Task<LifecycleResult> ExecuteAsync(LifecycleRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(request, new ProcessRunner(), null, null, NativeLinux.getuid(), NativeLinux.getgid(), cancellationToken);

    internal static async Task<LifecycleResult> ExecuteAsync(
        LifecycleRequest request,
        IProcessRunner processRunner,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        request.Validate();
        if (request.Operation is LifecycleOperationKind.CatalogInstall or LifecycleOperationKind.CatalogGarbageCollect)
        {
            return await CatalogLifecycleManager.ExecuteAsync(request, processRunner, uid, gid, cancellationToken)
                .ConfigureAwait(false);
        }
        if (request.Operation is null && request.InstanceId is null)
        {
            return await ListAsync(request.ProductRoot, cancellationToken).ConfigureAwait(false);
        }
        // A signed image upgrade resolves and verifies its release before the instance is touched, so an unsupported
        // architecture, a missing platform, or a tampered archive fails while the running instance is untouched.
        using var imageAcquirer = request.Operation == LifecycleOperationKind.Upgrade &&
                                  (request.ImageManifest is not null || request.ImageIndex is not null)
            ? new DistributionAcquirer()
            : null;
        AcquiredImage? signedImage = null;
        if (imageAcquirer is not null)
        {
            signedImage = await imageAcquirer.AcquireImageAsync(ImageSelectionRequest(request), cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InstallerException("The signed CameraAgent image release did not resolve a candidate image.");
            request = request with
            {
                ImageReference = signedImage.Platform.OfflineArchiveImageId,
                ImageArchive = signedImage.ArchivePath,
                ImageArchiveSha256 = signedImage.ArchiveSha256
            };
        }
        var instanceId = request.InstanceId!.Value;
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.CatalogId);
        if (request.Operation is not null && uid == 0)
            throw new InstallerException("Run lifecycle operations as the Docker-capable runtime user, not as root.");
        if (request.Operation == LifecycleOperationKind.Purge && !Directory.Exists(paths.InstanceRoot))
        {
            if (!request.Resume) throw new InstallerException("The instance root is absent; use --resume only for a retained purge operation.");
            SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
            using var interruptedPurgeLock = OperationLock.Acquire(
                Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
            return await ResumeInterruptedPurgeAsync(request, paths, new DockerClient(processRunner), cancellationToken)
                .ConfigureAwait(false);
        }
        var manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var result = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        var retainedOperation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        var allowTransactionalDrift = AllowsTransactionalDrift(request, retainedOperation);
        EnsureCorrelated(paths, instanceId, manifest, result, allowTransactionalDrift);
        if (allowTransactionalDrift) EnsureTransactionalCorrelation(retainedOperation!, manifest, result);
        string? lifecycleControlToken = null;
        string? installationVerificationToken = null;
        if (RequiresLifecycleControl(request.Operation))
        {
            lifecycleControlToken = await ReadLifecycleControlTokenAsync(paths, cancellationToken).ConfigureAwait(false);
            ValidateLifecycleControlToken(manifest, lifecycleControlToken);
            await ValidateCatalogSelectionSettingAsync(
                    paths, manifest.Catalog.PackageVersion,
                    ResumableCatalogSelectionVersion(request, retainedOperation), cancellationToken)
                .ConfigureAwait(false);
        }
        if (RequiresInstallationVerification(request.Operation))
        {
            installationVerificationToken = await ReadInstallationVerificationTokenAsync(paths, manifest, cancellationToken)
                .ConfigureAwait(false);
        }
        var docker = new DockerClient(processRunner);
        var compose = ComposeFrom(paths, manifest, result);
        var daemon = await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        EnsureDaemon(manifest.DockerDaemon, daemon);
        if (request.Operation is null)
        {
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
            var runnerRuntime = compose.ReplayRunnerContainerName is null
                ? null
                : await docker.InspectContainerAsync(compose.ReplayRunnerContainerName, cancellationToken).ConfigureAwait(false);
            var outcome = runtime.Exists && runtime.ImageId != manifest.Image.ImageId ||
                          runnerRuntime is { Exists: true } && runnerRuntime.ImageId != manifest.Image.ImageId
                ? "drifted"
                : "status";
            return Result(
                null,
                outcome,
                null,
                paths,
                manifest,
                daemon,
                runtime.Running && (runnerRuntime?.Running ?? true),
                runtime.Healthy && (runnerRuntime?.Healthy ?? true));
        }
        SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
        using var productLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);
        if (signedImage is not null)
        {
            await signedImage.WriteEvidenceAsync(paths, cancellationToken).ConfigureAwait(false);
        }
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        result = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        retainedOperation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        allowTransactionalDrift = AllowsTransactionalDrift(request, retainedOperation);
        EnsureCorrelated(paths, instanceId, manifest, result, allowTransactionalDrift);
        if (allowTransactionalDrift) EnsureTransactionalCorrelation(retainedOperation!, manifest, result);
        if (RequiresLifecycleControl(request.Operation))
        {
            lifecycleControlToken = await ReadLifecycleControlTokenAsync(paths, cancellationToken).ConfigureAwait(false);
            ValidateLifecycleControlToken(manifest, lifecycleControlToken);
            await ValidateCatalogSelectionSettingAsync(
                    paths, manifest.Catalog.PackageVersion,
                    ResumableCatalogSelectionVersion(request, retainedOperation), cancellationToken)
                .ConfigureAwait(false);
        }
        if (RequiresInstallationVerification(request.Operation))
        {
            installationVerificationToken = await ReadInstallationVerificationTokenAsync(paths, manifest, cancellationToken)
                .ConfigureAwait(false);
        }
        return request.Operation switch
        {
            LifecycleOperationKind.Upgrade => await ChangeImageAsync(
                request, paths, manifest, result, compose, docker, processRunner, lifecycleClientFactory, uid, gid,
                ownerClientFactory, lifecycleControlToken!, installationVerificationToken!, signedImage?.Image,
                rollback: false, cancellationToken).ConfigureAwait(false),
            LifecycleOperationKind.Rollback => await ChangeImageAsync(
                request, paths, manifest, result, compose, docker, processRunner, lifecycleClientFactory, uid, gid,
                ownerClientFactory, lifecycleControlToken!, installationVerificationToken!, signedImage: null,
                rollback: true, cancellationToken).ConfigureAwait(false),
            LifecycleOperationKind.Reinstall => await ReinstallAsync(
                request, paths, manifest, result, compose, docker, lifecycleClientFactory, ownerClientFactory,
                lifecycleControlToken!, installationVerificationToken!, daemon, cancellationToken)
                .ConfigureAwait(false),
            LifecycleOperationKind.Uninstall => await UninstallAsync(
                request, paths, manifest, result, compose, docker, lifecycleClientFactory, lifecycleControlToken!, daemon,
                cancellationToken)
                .ConfigureAwait(false),
            LifecycleOperationKind.Purge => await PurgeAsync(
                request, paths, manifest, compose, docker, daemon, cancellationToken).ConfigureAwait(false),
            LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback =>
                await CatalogLifecycleManager.SelectAsync(
                    request, paths, manifest, result, compose, docker, lifecycleClientFactory, ownerClientFactory,
                    lifecycleControlToken!, daemon, installationVerificationToken!, cancellationToken)
                    .ConfigureAwait(false),
            _ => throw new InstallUsageException("The lifecycle operation is unsupported for a CameraAgent instance.")
        };
    }

    private static async Task<LifecycleResult> ChangeImageAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        ComposeFiles compose,
        DockerClient docker,
        IProcessRunner processRunner,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        uint uid,
        uint gid,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string lifecycleControlToken,
        string verificationToken,
        DistributionImageIdentity? signedImage,
        bool rollback,
        CancellationToken cancellationToken)
    {
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw new InstallerException("An uninstalled instance must be reinstalled before image lifecycle operations.");
        if (rollback && manifest.PreviousImage is null) throw new InstallerException("No previous image is retained for rollback.");
        var operation = await BeginAsync(request, paths, rollback ? LifecycleOperationKind.Rollback : LifecycleOperationKind.Upgrade,
            manifest, cancellationToken).ConfigureAwait(false);
        var owner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
        if (!operation.MutationStarted)
        {
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            await docker.VerifyContainerAsync(
                compose, paths, manifest.Image, uid, gid, cancellationToken).ConfigureAwait(false);
            EnsureUpgradeStorage(paths, request.ImageArchive);
        }
        var operationRoot = Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        var previousManifestPath = Path.Combine(operationRoot, "previous-instance-manifest.json");
        var previousResultPath = Path.Combine(operationRoot, "previous-installation-result.json");
        if (operation.MutationStarted && operation.Phase != LifecycleOperationPhase.Committed)
        {
            (manifest, installationResult) = await ReadRecoverySnapshotAsync(
                paths, operation, previousManifestPath, previousResultPath, cancellationToken).ConfigureAwait(false);
            await WriteRecoverySnapshotAsync(paths, manifest, installationResult, cancellationToken).ConfigureAwait(false);
        }
        if (operation is { MutationStarted: true, Phase: LifecycleOperationPhase.Committed })
        {
            if (operation.CandidateImage is null || manifest.Image != operation.CandidateImage ||
                installationResult.Image != operation.CandidateImage ||
                manifest.LastLifecycleOperationId != operation.OperationId)
            {
                throw new InstallerException("The committed image lifecycle records do not match the retained operation.");
            }
            var committedExpectedOwnerState = operation.ExpectedOwnerBootstrapState;
            var verifiedOwnerState = await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                verificationToken, uid, gid, cancellationToken,
                committedExpectedOwnerState ?? installationResult.OwnerBootstrapState,
                allowCompletedPasswordReplacement:
                    committedExpectedOwnerState is null or "owner-password-change-required")
                .ConfigureAwait(false);
            if (verifiedOwnerState != committedExpectedOwnerState)
            {
                operation = await RecordAsync(
                    paths,
                    operation with { ExpectedOwnerBootstrapState = verifiedOwnerState },
                    cancellationToken).ConfigureAwait(false);
            }
            var committedLifecycle = CreateLifecycleClient(installationResult.Url, lifecycleClientFactory);
            await committedLifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result(operation.Kind, "completed", operation.OperationId, paths, manifest, manifest.DockerDaemon, true, true);
        }
        ImageInstallationIdentity candidate;
        if (rollback)
        {
            var target = manifest.PreviousImage!;
            var synthetic = ImageRequest(manifest, target.ImmutableReference, null, null, noDownload: true);
            var prepared = await docker.PrepareImageAsync(synthetic, false, signedImage: null, cancellationToken)
                .ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, prepared.Daemon);
            if (prepared.Image.ImageId != target.ImageId || prepared.Image.Architecture != target.Architecture)
            {
                throw new InstallerException("The retained rollback image no longer matches its immutable identity.");
            }
            candidate = target;
        }
        else
        {
            var synthetic = ImageRequest(
                manifest, request.ImageReference!, request.ImageArchive, request.ImageArchiveSha256, request.NoDownload);
            var prepared = await docker.PrepareImageAsync(synthetic, !request.DryRun, signedImage, cancellationToken)
                .ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, prepared.Daemon);
            candidate = prepared.Image;
        }
        if (candidate.ImageId == manifest.Image.ImageId)
        {
            if (request.Resume && manifest.LastLifecycleOperationId == operation.OperationId)
            {
                await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
                return Result(operation.Kind, "completed", operation.OperationId, paths, manifest, manifest.DockerDaemon, true, true);
            }
            throw new InstallerException("The candidate image is already active.");
        }
        if (!IsCanonicalImage(
                candidate,
                manifest.ComponentSchemaVersion,
                manifest.DockerDaemon.Architecture,
                manifest.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner))
        {
            throw new InstallerException("The candidate image does not declare the required CameraAgent configuration and catalog contracts.");
        }
        if (!rollback && !request.MigrationBackwardCompatible)
        {
            throw new InstallerException("The candidate does not declare backward-compatible state migration; an explicit transactional restore path is required.");
        }
        if (!rollback && !CameraAgentStateContract.IsCurrent(candidate.UpgradeCompatibility))
        {
            throw new InstallerException(
                $"The candidate image declares state compatibility '{CameraAgentStateContract.Describe(candidate.UpgradeCompatibility)}' instead of the supported '{CameraAgentStateContract.Current}' contract; an explicit state-disposition procedure is required.");
        }
        if (rollback && !CameraAgentStateContract.IsKnown(manifest.Image.UpgradeCompatibility) &&
            !CameraAgentStateContract.IsKnown(manifest.UpgradeCompatibility))
        {
            throw new InstallerException("Automatic rollback is forbidden because the active image did not declare a supported state contract.");
        }
        // Every boundary is proved before the backup, drain, stop, and Compose mutation begin, so an incompatible
        // upgrade or rollback never reaches a container restart loop.
        // This creates a missing bind source and normalizes an existing one to 0700, exactly as the committed
        // mutation does, so a dry run evaluates the same sources. A linked or foreign-owned source still fails
        // closed here with its own path rather than reaching the consolidated report.
        foreach (var directory in ComposeDeployment.WritableStateDirectories(paths.StateRoot))
        {
            SafeFileSystem.CreateRuntimeDirectory(directory, manifest.RuntimeUid, manifest.RuntimeGid);
        }
        await CameraAgentStatePreflight.EnsureCompatibleAsync(
            paths,
            manifest.InstanceId,
            candidate,
            manifest.Image.UpgradeCompatibility ?? manifest.UpgradeCompatibility,
            manifest.RuntimeUid,
            manifest.RuntimeGid,
            manifest.ReplayProfile,
            persist: !request.DryRun,
            renderToStandardError: !request.Json,
            cancellationToken,
            rollback ? CameraAgentStateContractPolicy.AllowLegacy : CameraAgentStateContractPolicy.RequireCurrent)
            .ConfigureAwait(false);
        if (operation.CandidateImage is not null && operation.CandidateImage != candidate)
            throw new InstallerException("The retained image operation has a different candidate identity.");
        operation = operation with
        {
            CandidateImage = candidate,
            Phase = LifecycleOperationPhase.CandidateValidated,
            Status = InstallationStatus.Running
        };
        if (request.DryRun)
        {
            return Result(operation.Kind, "planned", operation.OperationId, paths, manifest with { Image = candidate }, manifest.DockerDaemon, null, null);
        }
        var expectedOwnerState = operation.ExpectedOwnerBootstrapState;
        if (!operation.MutationStarted)
        {
            var currentOwnerState = await owner.ReadInstallationStateAsync(verificationToken, cancellationToken)
                .ConfigureAwait(false);
            if (expectedOwnerState is null)
            {
                if (!OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
                        installationResult.OwnerBootstrapState,
                        currentOwnerState,
                        allowCompletedPasswordReplacement: true))
                {
                    throw new InstallerException("CameraAgent reported an invalid owner bootstrap state before image mutation.");
                }
                expectedOwnerState = currentOwnerState;
                operation = operation with { ExpectedOwnerBootstrapState = expectedOwnerState };
            }
            else if (!OwnerBootstrapClient.IsAllowedOwnerBootstrapState(
                         expectedOwnerState,
                         currentOwnerState,
                         allowCompletedPasswordReplacement: true))
            {
                throw new InstallerException("CameraAgent owner bootstrap state changed after image lifecycle preparation.");
            }
            else if (currentOwnerState != expectedOwnerState)
            {
                expectedOwnerState = currentOwnerState;
                operation = operation with { ExpectedOwnerBootstrapState = expectedOwnerState };
            }
        }
        var allowLegacyOwnerStateProgression = expectedOwnerState is null;
        expectedOwnerState ??= installationResult.OwnerBootstrapState;
        var allowOwnerStateProgression = allowLegacyOwnerStateProgression ||
                                         expectedOwnerState == "owner-password-change-required";
        operation = await RecordAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(paths.OperationsRoot, "lifecycle"));
        SafeFileSystem.CreateOwnerDirectory(operationRoot);
        var previousEnvironmentPath = Path.Combine(operationRoot, "previous.env");
        var previousComposePath = Path.Combine(operationRoot, "previous-compose.yml");
        if (operation.MutationStarted && (!File.Exists(previousEnvironmentPath) || !File.Exists(previousComposePath)))
            throw new InstallerException("The retained image operation is missing its exact Compose recovery records.");
        var originalEnvironment = operation.MutationStarted
            ? await File.ReadAllTextAsync(previousEnvironmentPath, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllTextAsync(compose.EnvironmentFile, cancellationToken).ConfigureAwait(false);
        var originalCompose = operation.MutationStarted
            ? await File.ReadAllTextAsync(previousComposePath, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllTextAsync(compose.ComposeFile, cancellationToken).ConfigureAwait(false);
        var rollbackRoot = Path.Combine(paths.DeploymentStateRoot, "rollback");
        var retainedRollbackEnvironment = Path.Combine(rollbackRoot, "previous.env");
        var retainedRollbackCompose = Path.Combine(rollbackRoot, "previous-compose.yml");
        if (rollback && (!File.Exists(retainedRollbackEnvironment) || !File.Exists(retainedRollbackCompose)))
            throw new InstallerException("The retained rollback image is missing its exact Compose configuration.");
        if (rollback && !IsSha256(manifest.PreviousComposeModelSha256))
            throw new InstallerException("The retained rollback image is missing its authenticated Compose model identity.");
        var priorRollbackEnvironment = Path.Combine(operationRoot, "prior-rollback.env");
        var priorRollbackCompose = Path.Combine(operationRoot, "prior-rollback-compose.yml");
        var absentRollbackEnvironment = Path.Combine(operationRoot, "prior-rollback.env.absent");
        var absentRollbackCompose = Path.Combine(operationRoot, "prior-rollback-compose.yml.absent");
        if (!File.Exists(priorRollbackEnvironment) && !File.Exists(absentRollbackEnvironment))
        {
            if (File.Exists(retainedRollbackEnvironment))
                SafeFileSystem.WriteTextAtomic(priorRollbackEnvironment, await File.ReadAllTextAsync(retainedRollbackEnvironment, cancellationToken).ConfigureAwait(false));
            else
                SafeFileSystem.WriteTextAtomic(absentRollbackEnvironment, string.Empty);
        }
        if (!File.Exists(priorRollbackCompose) && !File.Exists(absentRollbackCompose))
        {
            if (File.Exists(retainedRollbackCompose))
                SafeFileSystem.WriteTextAtomic(priorRollbackCompose, await File.ReadAllTextAsync(retainedRollbackCompose, cancellationToken).ConfigureAwait(false));
            else
                SafeFileSystem.WriteTextAtomic(absentRollbackCompose, string.Empty);
        }
        var hadRollbackEnvironment = File.Exists(priorRollbackEnvironment);
        var hadRollbackCompose = File.Exists(priorRollbackCompose);
        var candidateEnvironment = rollback
            ? ReplaceEnvironmentValue(await File.ReadAllTextAsync(retainedRollbackEnvironment, cancellationToken).ConfigureAwait(false), "CAMERAAGENT_IMAGE", candidate.ImageId)
            : ReplaceEnvironmentValue(originalEnvironment, "CAMERAAGENT_IMAGE", candidate.ImageId);
        var stagedEnvironment = Path.Combine(operationRoot, "candidate.env");
        var stagedCompose = Path.Combine(operationRoot, "candidate-compose.yml");
        SafeFileSystem.WriteTextAtomic(stagedEnvironment, candidateEnvironment);
        SafeFileSystem.WriteTextAtomic(
            stagedCompose,
            rollback
                ? await File.ReadAllTextAsync(retainedRollbackCompose, cancellationToken).ConfigureAwait(false)
                : originalCompose);
        var candidateCompose = compose with { ComposeFile = stagedCompose, EnvironmentFile = stagedEnvironment };
        var renderedCandidate = await docker.ComposeAsync(
            candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName, ["config"], cancellationToken)
            .ConfigureAwait(false);
        var candidateComposeSha256 = ComposeDeployment.ComputeSha256(renderedCandidate.StandardOutput);
        if (rollback && candidateComposeSha256 != manifest.PreviousComposeModelSha256)
            throw new InstallerException("The retained rollback Compose model differs from its authenticated identity.");
        var baseAddress = installationResult.Url;
        var lifecycle = CreateLifecycleClient(baseAddress, lifecycleClientFactory);
        var candidateLifecycle = lifecycle;
        if (operation.MutationStarted)
        {
            SafeFileSystem.WriteTextAtomic(compose.ComposeFile, originalCompose);
            SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, originalEnvironment);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["up", "--detach", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            var restoredOwnerState = await VerifyCandidateAsync(
                owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                verificationToken, uid, gid, cancellationToken, expectedOwnerState, allowOwnerStateProgression)
                .ConfigureAwait(false);
            if (restoredOwnerState != expectedOwnerState)
            {
                expectedOwnerState = restoredOwnerState;
                allowOwnerStateProgression = allowLegacyOwnerStateProgression ||
                                             expectedOwnerState == "owner-password-change-required";
                operation = await RecordAsync(
                    paths,
                    operation with { ExpectedOwnerBootstrapState = expectedOwnerState },
                    cancellationToken).ConfigureAwait(false);
            }
            await lifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Prepared,
                MutationStarted = false
            }, cancellationToken).ConfigureAwait(false);
        }
        SafeFileSystem.WriteTextAtomic(previousEnvironmentPath, originalEnvironment);
        SafeFileSystem.WriteTextAtomic(previousComposePath, originalCompose);
        SafeFileSystem.WriteTextAtomic(
            previousManifestPath,
            await File.ReadAllTextAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false));
        SafeFileSystem.WriteTextAtomic(
            previousResultPath,
            await File.ReadAllTextAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false));
        operation = await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Prepared,
            MutationStarted = true
        }, cancellationToken).ConfigureAwait(false);
        var mutationStarted = true;
        var drainAttempted = false;
        try
        {
            drainAttempted = true;
            var continuity = await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Drained,
                PreMutationContinuity = ToBoundary(continuity)
            }, cancellationToken)
                .ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["stop"], cancellationToken)
                .ConfigureAwait(false);
            OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
            var backup = await InstanceBackupManager.CreateAsync(paths, manifest, operation.OperationId, processRunner, cancellationToken)
                .ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.BackupRecorded,
                BackupManifestSha256 = backup.ManifestSha256
            }, cancellationToken).ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Mutating,
                MutationStarted = true
            }, cancellationToken).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName,
                ["up", "--detach", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            var verifiedOwnerState = await VerifyCandidateAsync(
                owner, docker, candidateCompose, paths, manifest, installationResult, candidate, verificationToken, uid, gid,
                cancellationToken, expectedOwnerState, allowOwnerStateProgression).ConfigureAwait(false);
            expectedOwnerState = verifiedOwnerState;
            allowOwnerStateProgression = allowLegacyOwnerStateProgression ||
                                         expectedOwnerState == "owner-password-change-required";
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.CandidateVerified,
                ExpectedOwnerBootstrapState = expectedOwnerState
            }, cancellationToken)
                .ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName, ["restart"], cancellationToken)
                .ConfigureAwait(false);
            verifiedOwnerState = await VerifyCandidateAsync(
                owner, docker, candidateCompose, paths, manifest, installationResult, candidate, verificationToken, uid, gid,
                cancellationToken, expectedOwnerState, allowOwnerStateProgression).ConfigureAwait(false);
            if (verifiedOwnerState != expectedOwnerState)
            {
                expectedOwnerState = verifiedOwnerState;
                allowOwnerStateProgression = allowLegacyOwnerStateProgression ||
                                             expectedOwnerState == "owner-password-change-required";
                operation = await RecordAsync(
                    paths,
                    operation with { ExpectedOwnerBootstrapState = expectedOwnerState },
                    cancellationToken).ConfigureAwait(false);
            }
            var postMutation = await candidateLifecycle.ConfirmDrainedAsync(lifecycleControlToken, cancellationToken)
                .ConfigureAwait(false);
            EnsureContinuity(operation.PreMutationContinuity, postMutation);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.CandidateVerified,
                PostMutationContinuity = ToBoundary(postMutation),
                ExpectedOwnerBootstrapState = expectedOwnerState
            }, cancellationToken).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            SafeFileSystem.CreateOwnerDirectory(rollbackRoot);
            SafeFileSystem.WriteTextAtomic(retainedRollbackCompose, originalCompose);
            SafeFileSystem.WriteTextAtomic(retainedRollbackEnvironment, originalEnvironment);
            SafeFileSystem.WriteTextAtomic(compose.ComposeFile, await File.ReadAllTextAsync(stagedCompose, cancellationToken).ConfigureAwait(false));
            SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, candidateEnvironment);
            var committed = manifest with
            {
                Image = candidate,
                PreviousImage = manifest.Image,
                ComposeModelSha256 = candidateComposeSha256,
                UpgradeCompatibility = candidate.UpgradeCompatibility ?? "requires-declared-compatible-migration",
                LifecycleControlTokenSha256 = ComposeDeployment.ComputeSha256(lifecycleControlToken),
                PreviousComposeModelSha256 = manifest.ComposeModelSha256,
                LastLifecycleOperationId = operation.OperationId,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await SafeFileSystem.WriteJsonAtomicAsync(paths.ManifestPath, committed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
                .ConfigureAwait(false);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath,
                installationResult with
                {
                    ComposeModelSha256 = candidateComposeSha256,
                    Image = candidate
                },
                DeploymentJsonContext.Default.InstallationResult,
                cancellationToken).ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                .ConfigureAwait(false);
            await candidateLifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result(operation.Kind, "completed", operation.OperationId, paths, committed, manifest.DockerDaemon, true, true);
        }
        catch (Exception exception)
        {
            if (operation.Phase == LifecycleOperationPhase.Committed)
            {
                throw new InstallerException(
                    "The image lifecycle commit succeeded but final resume was not acknowledged; rerun the same command with --resume.",
                    exception);
            }
            if (mutationStarted || drainAttempted)
            {
                using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(4));
                try
                {
                    var failedPhase = operation.Phase;
                    operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Restoring }, recovery.Token)
                        .ConfigureAwait(false);
                    var diagnostics = new StringBuilder();
                    foreach (var containerName in new[]
                             {
                                 candidateCompose.ContainerName,
                                 candidateCompose.ReplayRunnerContainerName
                             }.OfType<string>())
                    {
                        diagnostics.Append("container=").AppendLine(containerName);
                        diagnostics.AppendLine(
                            await docker.ReadContainerLogsAsync(containerName, recovery.Token).ConfigureAwait(false));
                    }
                    SafeFileSystem.WriteTextAtomic(
                        Path.Combine(operationRoot, "candidate-diagnostics.txt"),
                        diagnostics.ToString());
                    if (mutationStarted)
                    {
                        SafeFileSystem.WriteTextAtomic(compose.ComposeFile, originalCompose);
                        SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, originalEnvironment);
                        var snapshot = await ReadRecoverySnapshotAsync(
                            paths, operation, previousManifestPath, previousResultPath, recovery.Token).ConfigureAwait(false);
                        await WriteRecoverySnapshotAsync(paths, snapshot.Manifest, snapshot.Result, recovery.Token).ConfigureAwait(false);
                        if (hadRollbackCompose)
                            SafeFileSystem.WriteTextAtomic(retainedRollbackCompose, await File.ReadAllTextAsync(priorRollbackCompose, recovery.Token).ConfigureAwait(false));
                        else if (File.Exists(retainedRollbackCompose))
                            File.Delete(retainedRollbackCompose);
                        if (hadRollbackEnvironment)
                            SafeFileSystem.WriteTextAtomic(retainedRollbackEnvironment, await File.ReadAllTextAsync(priorRollbackEnvironment, recovery.Token).ConfigureAwait(false));
                        else if (File.Exists(retainedRollbackEnvironment))
                            File.Delete(retainedRollbackEnvironment);
                    }
                    EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(recovery.Token).ConfigureAwait(false));
                    if (failedPhase is LifecycleOperationPhase.Mutating or LifecycleOperationPhase.CandidateVerified)
                    {
                        await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                            ["stop"], recovery.Token).ConfigureAwait(false);
                        OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
                    }
                    await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                        ["up", "--detach", "--remove-orphans"], recovery.Token).ConfigureAwait(false);
                    var restoredOwnerState = await VerifyCandidateAsync(
                        owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                        verificationToken, uid, gid, recovery.Token, expectedOwnerState, allowOwnerStateProgression)
                        .ConfigureAwait(false);
                    if (restoredOwnerState != expectedOwnerState)
                    {
                        expectedOwnerState = restoredOwnerState;
                        allowOwnerStateProgression = allowLegacyOwnerStateProgression ||
                                                     expectedOwnerState == "owner-password-change-required";
                        operation = await RecordAsync(
                            paths,
                            operation with { ExpectedOwnerBootstrapState = expectedOwnerState },
                            recovery.Token).ConfigureAwait(false);
                    }
                    await lifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, recovery.Token).ConfigureAwait(false);
                    operation = operation with
                    {
                        Phase = LifecycleOperationPhase.Prepared,
                        MutationStarted = false
                    };
                }
                catch (Exception recoveryException)
                {
                    throw new InstallerException(
                        $"Candidate failed and exact rollback also failed: {Redaction.SafeDiagnostic(recoveryException.Message)}", exception);
                }
            }
            await FailAsync(paths, operation, exception, CancellationToken.None).ConfigureAwait(false);
            if (exception is OperationCanceledException) throw;
            throw new InstallerException("The image lifecycle operation failed; the retained journal and diagnostics identify the recovery boundary.", exception);
        }
    }

    private static async Task<LifecycleResult> UninstallAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        ComposeFiles compose,
        DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        string lifecycleControlToken,
        DockerDaemonIdentity daemon,
        CancellationToken cancellationToken)
    {
        DockerClient.ContainerRuntimeIdentity? before = null;
        if (!request.DryRun)
        {
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            before = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
            if (before.Exists)
            {
                await docker.VerifyContainerOwnershipAsync(
                    compose, paths, manifest.Image, manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Uninstall, manifest, cancellationToken)
            .ConfigureAwait(false);
        if (request.DryRun) return Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, null, null);
        operation = await RecordAsync(paths, operation with { MutationStarted = true }, cancellationToken).ConfigureAwait(false);
        if (manifest.LifecycleCondition == InstanceLifecycleCondition.Installed)
        {
            if (before!.Running)
            {
                var lifecycle = CreateLifecycleClient(installationResult.Url, lifecycleClientFactory);
                await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            }
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["down"], cancellationToken).ConfigureAwait(false);
            OwnerRecoverySocket.RemoveStoppedSocket(paths, manifest.RuntimeUid, manifest.RuntimeGid);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken)
                .ConfigureAwait(false);
        }
        var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        if (runtime.Exists) throw new InstallerException("The selected instance container still exists after uninstall.");
        var committed = manifest with
        {
            LifecycleCondition = InstanceLifecycleCondition.Uninstalled,
            LastLifecycleOperationId = operation.OperationId,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await CatalogReferenceStore.PinHistoricalAsync(paths, manifest.Catalog, operation.OperationId, cancellationToken)
            .ConfigureAwait(false);
        await SafeFileSystem.WriteJsonAtomicAsync(paths.ManifestPath, committed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
            .ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
            .ConfigureAwait(false);
        await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        return Result(operation.Kind, "completed", operation.OperationId, paths, committed, daemon, false, false);
    }

    private static async Task<LifecycleResult> ReinstallAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        ComposeFiles compose,
        DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string lifecycleControlToken,
        string verificationToken,
        DockerDaemonIdentity daemon,
        CancellationToken cancellationToken)
    {
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Uninstalled)
        {
            var retained = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
            if (request.Resume && retained is
                {
                    Kind: LifecycleOperationKind.Reinstall,
                    Status: not InstallationStatus.Completed,
                    Phase: LifecycleOperationPhase.CandidateVerified or LifecycleOperationPhase.Committed,
                    MutationStarted: true
                } &&
                retained.InstanceId == request.InstanceId && retained.RequestSha256 == request.ComputeRequestSha256() &&
                retained.OriginalImage == manifest.Image && retained.OriginalCatalog == manifest.Catalog &&
                manifest.LastLifecycleOperationId == retained.OperationId)
            {
                var resumedOwner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
                await VerifyCandidateAsync(resumedOwner, docker, compose, paths, manifest, installationResult, manifest.Image,
                    verificationToken, manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken).ConfigureAwait(false);
                var resumedLifecycle = CreateLifecycleClient(installationResult.Url, lifecycleClientFactory);
                if (retained.Phase != LifecycleOperationPhase.Committed)
                    retained = await RecordAsync(paths, retained with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                        .ConfigureAwait(false);
                await resumedLifecycle.ResumeAsync(retained.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
                await CompleteAsync(paths, retained, cancellationToken).ConfigureAwait(false);
                return Result(retained.Kind, "completed", retained.OperationId, paths, manifest, daemon, true, true);
            }
            throw new InstallerException("Reinstall requires a preserved uninstalled instance.");
        }
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Reinstall, manifest, cancellationToken)
            .ConfigureAwait(false);
        if (request.DryRun) return Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, false, false);
        await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with { MutationStarted = true }, cancellationToken).ConfigureAwait(false);
        OwnerRecoverySocket.RemoveStoppedSocket(paths, manifest.RuntimeUid, manifest.RuntimeGid);
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
            ["up", "--detach", "--force-recreate", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
        var owner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
        await VerifyCandidateAsync(
            owner, docker, compose, paths, manifest, installationResult, manifest.Image, verificationToken,
            manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken).ConfigureAwait(false);
        var lifecycle = CreateLifecycleClient(installationResult.Url, lifecycleClientFactory);
        var postMutation = await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken)
            .ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.CandidateVerified,
            PostMutationContinuity = ToBoundary(postMutation)
        }, cancellationToken).ConfigureAwait(false);
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        var committed = manifest with
        {
            LifecycleCondition = InstanceLifecycleCondition.Installed,
            LastLifecycleOperationId = operation.OperationId,
            UpdatedUtc = DateTimeOffset.UtcNow
        };
        await SafeFileSystem.WriteJsonAtomicAsync(
            paths.ManifestPath, committed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
            .ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
            .ConfigureAwait(false);
        await lifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        return Result(operation.Kind, "completed", operation.OperationId, paths, committed, daemon, true, true);
    }

    private static async Task<LifecycleResult> PurgeAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        ComposeFiles compose,
        DockerClient docker,
        DockerDaemonIdentity daemon,
        CancellationToken cancellationToken)
    {
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Uninstalled)
            throw new InstallerException("Purge requires a completed preserve-by-default uninstall.");
        var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        if (runtime.Exists) throw new InstallerException("Purge refused because the instance container still exists.");
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken)
            .ConfigureAwait(false);
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Purge, manifest, cancellationToken)
            .ConfigureAwait(false);
        if (request.DryRun) return Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, false, false);
        await CatalogReferenceStore.PinHistoricalAsync(paths, manifest.Catalog, operation.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var parent = Directory.GetParent(paths.InstanceRoot)?.FullName
            ?? throw new InstallerException("The instance root has no authenticated parent.");
        var expectedParent = Path.Combine(paths.ProductRoot, "cameraagents");
        if (parent != expectedParent || new DirectoryInfo(paths.InstanceRoot).LinkTarget is not null)
            throw new InstallerException("The instance root path is not canonical.");
        var identity = NativeLinux.GetDirectoryIdentity(paths.InstanceRoot);
        if (identity.Uid != manifest.RuntimeUid || identity.Gid != manifest.RuntimeGid)
            throw new InstallerException("The instance root ownership changed before purge.");
        var childName = Path.GetFileName(paths.InstanceRoot);
        var inventory = SafeTreeDeletion.CaptureChildInventory(parent, childName, identity.Uid, identity.Gid);
        var treeIdentity = SafeTreeDeletion.ValidateChild(parent, childName, identity.Uid, identity.Gid);
        var tombstone = Path.Combine(parent, $".purge-{manifest.InstanceId:D}-{operation.OperationId:D}");
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(paths.OperationsRoot, $"purge-{operation.OperationId:D}.evidence.json"),
            new PurgeDeletionEvidence(
                DeploymentSchemaVersions.LifecycleOperation, operation.OperationId, operation.RequestSha256,
                LocalHostIdentity.ReadSha256(), manifest, inventory),
            DeploymentJsonContext.Default.PurgeDeletionEvidence, cancellationToken).ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Mutating,
            MutationStarted = true
        }, cancellationToken).ConfigureAwait(false);
        SafeTreeDeletion.RenameChild(parent, Path.GetFileName(paths.InstanceRoot), Path.GetFileName(tombstone), treeIdentity);
        try
        {
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken)
                .ConfigureAwait(false);
            await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, tombstone, cancellationToken, inventory)
                .ConfigureAwait(false);
            var tombstoneManifest = await ReadManifestAsync(Path.Combine(tombstone, "instance-manifest.json"), cancellationToken)
                .ConfigureAwait(false);
            if (tombstoneManifest.InstanceId != manifest.InstanceId || tombstoneManifest.InstallationId != manifest.InstallationId ||
                tombstoneManifest.ApplicationIdentity != manifest.ApplicationIdentity)
                throw new InstallerException("The purge tombstone ownership marker does not match the selected instance.");
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                .ConfigureAwait(false);
            SafeTreeDeletion.DeleteChild(parent, Path.GetFileName(tombstone), identity.Uid, identity.Gid, treeIdentity);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            throw new InstallerException("Purge stopped at its quarantined tombstone; rerun the same command with --resume.", exception);
        }
        await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        return new LifecycleResult(
            DeploymentSchemaVersions.LifecycleOperation,
            operation.Kind,
            "completed",
            operation.OperationId,
            manifest.InstanceId,
            null,
            paths.ProductRoot,
            null,
            null,
            null,
            manifest.Catalog,
            manifest.PreviousCatalog,
            daemon,
            false,
            false,
            [paths.CatalogRoot, paths.OperationsRoot],
            null,
            DateTimeOffset.UtcNow);
    }

    private static async Task<string> VerifyCandidateAsync(
        IOwnerBootstrapClient owner,
        DockerClient docker,
        ComposeFiles compose,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult result,
        ImageInstallationIdentity image,
        string verificationToken,
        uint uid,
        uint gid,
        CancellationToken cancellationToken,
        string? ownerBootstrapState = null,
        bool allowCompletedPasswordReplacement = true)
    {
        await owner.WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
        var verifiedOwnerState = await owner.VerifyInstallationAsync(
            verificationToken,
            new InstallationVerificationExpectation(
                manifest.ApplicationIdentity.ToString("D"), manifest.OwnerEmail, ownerBootstrapState ?? result.OwnerBootstrapState,
                manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
                manifest.DeploymentLocationId, manifest.DeploymentLocationVersion, manifest.DeploymentLocationSha256,
                manifest.ReplayProfile,
                manifest.Catalog,
                allowCompletedPasswordReplacement),
            cancellationToken).ConfigureAwait(false);
        await docker.VerifyContainerAsync(compose, paths, image, uid, gid, cancellationToken).ConfigureAwait(false);
        return verifiedOwnerState;
    }

    /// <summary>The image-train selectors an upgrade uses to resolve its signed release, with no instance state.</summary>
    private static InstallRequest ImageSelectionRequest(LifecycleRequest request) => new()
    {
        FriendlyName = "lifecycle",
        OwnerEmail = "lifecycle@localhost.invalid",
        ImageManifest = request.ImageManifest,
        ImageIndex = request.ImageIndex,
        ImageVersion = request.ImageVersion,
        AssetBaseUrl = request.AssetBaseUrl,
        Channel = request.Channel,
        NoDownload = request.NoDownload
    };

    private static InstallRequest ImageRequest(
        InstanceManifest manifest,
        string imageReference,
        string? archive,
        string? archiveSha256,
        bool noDownload)
        => new()
        {
            FriendlyName = manifest.FriendlyName,
            OwnerEmail = manifest.OwnerEmail,
            ProductRoot = manifest.ProductRoot,
            CatalogBundle = "/dev/null",
            ImageReference = imageReference,
            ImageArchive = archive,
            ImageArchiveSha256 = archiveSha256,
            NoDownload = noDownload
        };

    internal static async Task<LifecycleOperationState> BeginAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        LifecycleOperationKind kind,
        InstanceManifest manifest,
        CancellationToken cancellationToken)
    {
        if (request.DryRun)
        {
            var now = DateTimeOffset.UtcNow;
            return new LifecycleOperationState(
                DeploymentSchemaVersions.LifecycleOperation,
                Guid.NewGuid(),
                kind,
                manifest.InstanceId,
                request.ComputeRequestSha256(),
                LifecycleOperationPhase.Planned,
                InstallationStatus.Pending,
                now,
                now,
                manifest.Image,
                OriginalCatalog: manifest.Catalog);
        }
        var existing = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        var hash = request.ComputeRequestSha256();
        if (existing is { Status: not InstallationStatus.Completed })
        {
            if (!request.Resume) throw new InstallerException("An incomplete lifecycle operation exists; rerun the same command with --resume.");
            if (existing.Kind != kind || existing.InstanceId != manifest.InstanceId || existing.RequestSha256 != hash)
                throw new InstallerException("The retained lifecycle operation belongs to different immutable inputs.");
            var transitionAtCommitBoundary = existing.Phase is
                                                 LifecycleOperationPhase.CandidateVerified or LifecycleOperationPhase.Committed &&
                                             kind is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or
                                                 LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback;
            if (!transitionAtCommitBoundary &&
                (existing.OriginalImage != manifest.Image || existing.OriginalCatalog != manifest.Catalog))
            {
                throw new InstallerException("The retained lifecycle operation does not own the selected instance identities.");
            }
            return existing;
        }
        if (request.Resume) throw new InstallerException("No incomplete matching lifecycle operation exists.");
        var state = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation,
            Guid.NewGuid(),
            kind,
            manifest.InstanceId,
            hash,
            LifecycleOperationPhase.Prepared,
            InstallationStatus.Running,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            manifest.Image,
            OriginalCatalog: manifest.Catalog);
        return await RecordAsync(paths, state, cancellationToken).ConfigureAwait(false);
    }

    internal static Task<LifecycleOperationState> CompleteAsync(
        InstallationPaths paths,
        LifecycleOperationState state,
        CancellationToken cancellationToken)
        => RecordAsync(paths, state with
        {
            Phase = LifecycleOperationPhase.Completed,
            Status = InstallationStatus.Completed,
            UpdatedUtc = DateTimeOffset.UtcNow,
            FailureCode = null,
            FailureMessage = null,
            MutationStarted = false
        }, cancellationToken);

    internal static async Task FailAsync(
        InstallationPaths paths,
        LifecycleOperationState state,
        Exception exception,
        CancellationToken cancellationToken)
        => await RecordAsync(paths, state with
        {
            Status = InstallationStatus.Failed,
            UpdatedUtc = DateTimeOffset.UtcNow,
            FailureCode = "lifecycle-failed",
            FailureMessage = Redaction.SafeDiagnostic(exception.Message)
        }, cancellationToken).ConfigureAwait(false);

    internal static async Task<LifecycleOperationState> RecordAsync(
        InstallationPaths paths,
        LifecycleOperationState state,
        CancellationToken cancellationToken)
    {
        state = state with { UpdatedUtc = DateTimeOffset.UtcNow };
        await SafeFileSystem.WriteJsonAtomicAsync(
            paths.LifecycleStatePath, state, DeploymentJsonContext.Default.LifecycleOperationState, cancellationToken)
            .ConfigureAwait(false);
        return state;
    }

    internal static async Task<LifecycleOperationState?> ReadOperationAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        LifecycleOperationState value;
        try
        {
            value = await JsonSerializer.DeserializeAsync(
                stream, DeploymentJsonContext.Default.LifecycleOperationState, cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerException("The retained lifecycle operation is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException("The retained lifecycle operation is invalid JSON.", exception);
        }
        if (value.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation || value.OperationId == Guid.Empty ||
            value.InstanceId is null || value.InstanceId == Guid.Empty || value.RequestSha256.Length != 64 ||
            value.RequestSha256.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.Phase) || !Enum.IsDefined(value.Status) ||
            value.StartedUtc == default || value.UpdatedUtc < value.StartedUtc || value.OriginalImage is null ||
            value.OriginalCatalog is null ||
            !IsCanonicalImage(value.OriginalImage, "cameraagent-install-v1", value.OriginalImage.Architecture) ||
            !IsValidCatalog(value.OriginalCatalog) ||
            value.CandidateImage is not null &&
            !IsCanonicalImage(value.CandidateImage, "cameraagent-install-v1", value.CandidateImage.Architecture) ||
            value.CandidateCatalog is not null && !IsValidCatalog(value.CandidateCatalog) ||
            value.Phase == LifecycleOperationPhase.Planned ||
            value.Status == InstallationStatus.Completed && value.Phase != LifecycleOperationPhase.Completed ||
            value.Phase == LifecycleOperationPhase.Completed && value.Status != InstallationStatus.Completed ||
            value.Status == InstallationStatus.Completed && value.MutationStarted)
        {
            throw new InstallerException("The retained lifecycle operation is invalid or unsupported.");
        }
        var imageTransition = value.Kind is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback;
        var catalogTransition = value.Kind is LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback;
        if (value.Status != InstallationStatus.Completed && value.Phase >= LifecycleOperationPhase.Drained && !value.MutationStarted ||
            imageTransition && value.Phase >= LifecycleOperationPhase.CandidateValidated && value.CandidateImage is null ||
            catalogTransition && value.Phase >= LifecycleOperationPhase.CandidateValidated && value.CandidateCatalog is null ||
            value.Phase == LifecycleOperationPhase.Committed &&
            (value.Kind is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or
                LifecycleOperationKind.Reinstall or LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback) &&
            value.PostMutationContinuity is null)
        {
            throw new InstallerException("The retained lifecycle operation has an impossible phase state.");
        }
        return value;
    }

    private static async Task<LifecycleResult> ResumeInterruptedPurgeAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        DockerClient docker,
        CancellationToken cancellationToken)
    {
        var operation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        if (operation is not { Kind: LifecycleOperationKind.Purge, Status: not InstallationStatus.Completed, MutationStarted: true } ||
            operation.InstanceId != request.InstanceId || operation.RequestSha256 != request.ComputeRequestSha256())
            throw new InstallerException("No matching interrupted purge operation exists.");
        var parent = Path.GetDirectoryName(paths.InstanceRoot)!;
        var tombstone = Path.Combine(parent, $".purge-{operation.InstanceId!.Value:D}-{operation.OperationId:D}");
        var evidencePath = Path.Combine(paths.OperationsRoot, $"purge-{operation.OperationId:D}.evidence.json");
        await using var evidenceStream = SafeFileSystem.OpenOwnerFileRead(evidencePath);
        var evidence = await DeserializeRetainedAsync(
            evidenceStream, DeploymentJsonContext.Default.PurgeDeletionEvidence,
            "purge deletion evidence", cancellationToken).ConfigureAwait(false);
        if (evidence.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation || evidence.OperationId != operation.OperationId ||
            evidence.RequestSha256 != operation.RequestSha256 || evidence.Manifest.InstanceId != operation.InstanceId || evidence.Tree.Count == 0)
            throw new InstallerException("The purge deletion evidence does not match its retained operation.");
        if (evidence.HostIdentitySha256 != LocalHostIdentity.ReadSha256())
            throw new InstallerException("The purge deletion evidence belongs to a different host.");
        var manifest = evidence.Manifest;
        ValidateCanonicalManifest(manifest);
        if (operation.OriginalImage != manifest.Image || operation.OriginalCatalog != manifest.Catalog ||
            manifest.ProductRoot != paths.ProductRoot || manifest.ConfigRoot != paths.ConfigRoot ||
            manifest.StateRoot != paths.StateRoot)
        {
            throw new InstallerException("The purge deletion evidence does not own the retained operation paths and identities.");
        }
        var daemon = await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        EnsureDaemon(manifest.DockerDaemon, daemon);
        await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken, evidence.Tree).ConfigureAwait(false);
        await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, tombstone, cancellationToken, evidence.Tree).ConfigureAwait(false);
        if (Directory.Exists(tombstone))
        {
            var treeIdentity = SafeTreeDeletion.ValidateRemainingChild(
                parent, Path.GetFileName(tombstone), manifest.RuntimeUid, manifest.RuntimeGid, evidence.Tree);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken, evidence.Tree).ConfigureAwait(false);
            await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, tombstone, cancellationToken, evidence.Tree).ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                .ConfigureAwait(false);
            SafeTreeDeletion.DeleteChild(
                parent, Path.GetFileName(tombstone), manifest.RuntimeUid, manifest.RuntimeGid, treeIdentity);
        }
        else if (operation.Phase != LifecycleOperationPhase.Committed)
        {
            throw new InstallerException("The interrupted purge tombstone is missing before deletion was committed.");
        }
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        return new LifecycleResult(
            DeploymentSchemaVersions.LifecycleOperation, operation.Kind, "completed", operation.OperationId,
            manifest.InstanceId, null, paths.ProductRoot, null, null, null, manifest.Catalog, manifest.PreviousCatalog,
            daemon, false, false, [paths.CatalogRoot, paths.OperationsRoot], null, DateTimeOffset.UtcNow);
    }

    internal static async Task<InstanceManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        InstanceManifest manifest;
        try
        {
            using (var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                if (document.RootElement.TryGetProperty("previousComposeTemplateVersion", out var previousVersion) &&
                    (previousVersion.ValueKind != JsonValueKind.String ||
                     previousVersion.GetString() is not (
                         ComposeDeployment.TemplateVersion or ComposeDeployment.LocalRunnerTemplateVersion)))
                {
                    throw new InstallerException("The retained instance manifest declares an unsupported previous Compose contract.");
                }
            }
            stream.Position = 0;
            manifest = await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
                .ConfigureAwait(false) ?? throw new InstallerException("The instance manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException("The retained instance manifest is invalid JSON.", exception);
        }
        ValidateCanonicalManifest(manifest);
        return manifest;
    }

    internal static async Task<InstallationResult> ReadResultAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        InstallationResult result;
        try
        {
            result = await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.InstallationResult, cancellationToken)
                .ConfigureAwait(false) ?? throw new InstallerException("The installation result is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException("The retained installation result is invalid JSON.", exception);
        }
        if (result.SchemaVersion != DeploymentSchemaVersions.InstallationResult ||
            result.Outcome != InstallationOutcome.Installed || result.InstallationId == Guid.Empty ||
            result.InstanceId == Guid.Empty || result.ApplicationIdentity == Guid.Empty ||
            !HasValue(result.FriendlyName) || result.Url is not { IsAbsoluteUri: true } || !HasValue(result.OwnerEmail) ||
            !HasValue(result.PasswordFile) || !HasValue(result.ProductRoot) || !HasValue(result.InstanceRoot) ||
            !HasValue(result.ConfigRoot) || !HasValue(result.StateRoot) || result.RuntimeUid == 0 ||
            !Enum.IsDefined(result.ReplayProfile) ||
            result.ComposeTemplateVersion != ComposeDeployment.TemplateVersionFor((CameraAgentReplayProfile)result.ReplayProfile) ||
            !double.IsFinite(result.LatitudeDegrees) || !double.IsFinite(result.LongitudeDegrees) ||
            !double.IsFinite(result.ElevationMeters) || !HasValue(result.TimeZoneId) ||
            !IsSha256(result.ConfigurationSha256) || !IsSha256(result.RigProfileSha256) ||
            !IsSha256(result.ScheduleSha256) || !HasValue(result.RigProfileName) ||
            !HasValue(result.RigProfileVersion) || !HasValue(result.ScheduleSchemaVersion) ||
            !HasValue(result.ScheduleState) || !IsSha256(result.ComposeModelSha256) ||
            !IsValidCatalog(result.Catalog) ||
            result.Catalog.InstallRoot != Path.Combine(result.ProductRoot, "catalogs", result.Catalog.CatalogId) ||
            !IsCanonicalImage(
                result.Image,
                "cameraagent-install-v1",
                result.DockerDaemon?.Architecture,
                result.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner) ||
            !IsValidDaemon(result.DockerDaemon) || !result.Alive || !result.Healthy ||
            result.OwnerBootstrapState != "owner-password-change-required" || result.CompletedUtc == default)
        {
            throw new InstallerException("The retained installation result is invalid or unsupported.");
        }
        return result;
    }

    internal static async Task<(InstanceManifest Manifest, InstallationResult Result)> ReadRecoverySnapshotAsync(
        InstallationPaths paths,
        LifecycleOperationState operation,
        string manifestPath,
        string resultPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath) || !File.Exists(resultPath))
            throw new InstallerException("The retained lifecycle operation is missing its recovery identity records.");
        var manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var result = await ReadResultAsync(resultPath, cancellationToken).ConfigureAwait(false);
        EnsureCorrelated(paths, operation.InstanceId!.Value, manifest, result, allowTransactionalDrift: false);
        if (manifest.Image != operation.OriginalImage || result.Image != operation.OriginalImage ||
            manifest.Catalog != operation.OriginalCatalog || result.Catalog != operation.OriginalCatalog)
        {
            throw new InstallerException("The retained lifecycle recovery records do not match the original operation identities.");
        }
        return (manifest, result);
    }

    internal static async Task WriteRecoverySnapshotAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult result,
        CancellationToken cancellationToken)
    {
        await SafeFileSystem.WriteJsonAtomicAsync(
            paths.ManifestPath, manifest, DeploymentJsonContext.Default.InstanceManifest, cancellationToken).ConfigureAwait(false);
        await SafeFileSystem.WriteJsonAtomicAsync(
            paths.ResultPath, result, DeploymentJsonContext.Default.InstallationResult, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadSecretAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: false);
        var value = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n');
        return value.Length > 0 && !value.Any(char.IsControl)
            ? value
            : throw new InstallerException("A retained lifecycle credential is invalid.");
    }

    internal static async Task<string> ReadLifecycleControlTokenAsync(
        InstallationPaths paths,
        CancellationToken cancellationToken)
    {
        var tokenPath = Path.Combine(paths.ConfigRoot, "lifecycle-control", "token");
        var mirrorPath = Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token");
        if (!File.Exists(tokenPath) || !File.Exists(mirrorPath))
            throw new InstallerException("The canonical lifecycle control credential is incomplete.");
        var token = await ReadSecretAsync(tokenPath, cancellationToken).ConfigureAwait(false);
        var mirror = await ReadSecretAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
        if (mirror != token) throw new InstallerException("The lifecycle control credential mirror does not match its authority file.");
        return token;
    }

    private static async Task<string> ReadInstallationVerificationTokenAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.ConfigRoot, "installation-verification", "token");
        if (!File.Exists(path))
            throw new InstallerException("The canonical installation verification credential is incomplete.");
        var token = await ReadSecretAsync(path, cancellationToken).ConfigureAwait(false);
        if (manifest.InstallationVerificationTokenSha256 != ComposeDeployment.ComputeSha256(token))
            throw new InstallerException("The installation verification credential does not match the retained instance manifest.");
        return token;
    }

    private static async Task ValidateCatalogSelectionSettingAsync(
        InstallationPaths paths,
        string packageVersion,
        string? resumablePackageVersion,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion");
        if (!File.Exists(path))
            throw new InstallerException("The catalog selection credential does not match the retained instance manifest.");
        var value = await ReadSecretAsync(path, cancellationToken).ConfigureAwait(false);
        if (value != packageVersion && value != resumablePackageVersion)
            throw new InstallerException("The catalog selection credential does not match the retained lifecycle state.");
    }

    internal static void ValidateLifecycleControlToken(InstanceManifest manifest, string token)
    {
        if (manifest.LifecycleControlTokenSha256 != ComposeDeployment.ComputeSha256(token))
            throw new InstallerException("The lifecycle control credential does not match the retained instance manifest.");
    }

    internal static void ValidateCanonicalManifest(InstanceManifest manifest)
    {
        if (manifest.SchemaVersion != DeploymentSchemaVersions.InstanceManifest ||
            manifest.Product != "HVO.SkyMonitor" || manifest.ComponentSchemaVersion != "cameraagent-install-v1" ||
            manifest.Component != DeploymentComponent.CameraAgent || manifest.InstanceId == Guid.Empty ||
            manifest.InstallationId == Guid.Empty || manifest.ApplicationIdentity == Guid.Empty ||
            !HasValue(manifest.FriendlyName) || !HasValue(manifest.DeploymentLocationId) ||
            manifest.DeploymentLocationVersion < 1 || !IsSha256(manifest.DeploymentLocationSha256) ||
            !HasValue(manifest.OwnerEmail) || !HasValue(manifest.TimeZoneId) ||
            !double.IsFinite(manifest.LatitudeDegrees) || !double.IsFinite(manifest.LongitudeDegrees) ||
            !double.IsFinite(manifest.ElevationMeters) || manifest.RuntimeUid == 0 ||
            !HasValue(manifest.ProductRoot) || !HasValue(manifest.ConfigRoot) || !HasValue(manifest.StateRoot) ||
            !Enum.IsDefined(manifest.ReplayProfile) ||
            manifest.ComposeTemplateVersion != ComposeDeployment.TemplateVersionFor(
                (CameraAgentReplayProfile)manifest.ReplayProfile) ||
            !IsSha256(manifest.ConfigurationSha256) || !IsSha256(manifest.RigProfileSha256) ||
            !IsSha256(manifest.ScheduleSha256) || !HasValue(manifest.RigProfileName) ||
            !HasValue(manifest.RigProfileVersion) || !HasValue(manifest.ScheduleSchemaVersion) ||
            !HasValue(manifest.ScheduleState) || !IsSha256(manifest.InstallationVerificationTokenSha256) ||
            !IsSha256(manifest.ComposeModelSha256) || !IsValidCatalog(manifest.Catalog) ||
            manifest.Catalog.InstallRoot != Path.Combine(manifest.ProductRoot, "catalogs", manifest.Catalog.CatalogId) ||
            !IsSha256(manifest.LifecycleControlTokenSha256) ||
            !IsCanonicalImage(
                manifest.Image,
                manifest.ComponentSchemaVersion,
                manifest.DockerDaemon?.Architecture,
                manifest.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner) ||
            manifest.PreviousImage is not null &&
            !IsCanonicalImage(
                manifest.PreviousImage,
                manifest.ComponentSchemaVersion,
                manifest.DockerDaemon?.Architecture,
                manifest.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner) ||
            (manifest.PreviousImage is null) != (manifest.PreviousComposeModelSha256 is null) ||
            manifest.PreviousComposeModelSha256 is not null && !IsSha256(manifest.PreviousComposeModelSha256) ||
            manifest.PreviousCatalog is not null && !IsValidCatalog(manifest.PreviousCatalog) ||
            manifest.PreviousCatalog is not null &&
            manifest.PreviousCatalog.InstallRoot != Path.Combine(
                manifest.ProductRoot, "catalogs", manifest.PreviousCatalog.CatalogId) ||
            !IsValidDaemon(manifest.DockerDaemon) || !HasValue(manifest.UpgradeCompatibility) ||
            !Enum.IsDefined(manifest.LifecycleCondition) || manifest.CreatedUtc == default)
        {
            throw new InstallerException("The retained instance manifest is invalid or unsupported.");
        }
    }

    internal static void EnsureCorrelated(
        InstallationPaths paths,
        Guid instanceId,
        InstanceManifest manifest,
        InstallationResult result,
        bool allowTransactionalDrift)
    {
        if (manifest.SchemaVersion != DeploymentSchemaVersions.InstanceManifest || manifest.Product != "HVO.SkyMonitor" ||
            manifest.Component != DeploymentComponent.CameraAgent || manifest.InstanceId != instanceId ||
            result.InstanceId != instanceId || manifest.ProductRoot != paths.ProductRoot ||
            result.ProductRoot != paths.ProductRoot || result.InstanceRoot != paths.InstanceRoot ||
            manifest.ConfigRoot != paths.ConfigRoot || manifest.StateRoot != paths.StateRoot ||
            result.ConfigRoot != paths.ConfigRoot || result.StateRoot != paths.StateRoot ||
            manifest.InstallationId != result.InstallationId || manifest.ApplicationIdentity != result.ApplicationIdentity ||
            manifest.RuntimeUid != result.RuntimeUid || manifest.RuntimeGid != result.RuntimeGid ||
            manifest.FriendlyName != result.FriendlyName || manifest.OwnerEmail != result.OwnerEmail ||
            manifest.LatitudeDegrees != result.LatitudeDegrees || manifest.LongitudeDegrees != result.LongitudeDegrees ||
            manifest.ElevationMeters != result.ElevationMeters || manifest.TimeZoneId != result.TimeZoneId ||
            PublicHost(manifest.BindAddress) != result.Url.Host || manifest.Port != result.Url.Port ||
            manifest.RigProfileName != result.RigProfileName || manifest.RigProfileVersion != result.RigProfileVersion ||
            manifest.ScheduleSchemaVersion != result.ScheduleSchemaVersion || manifest.ScheduleState != result.ScheduleState ||
            manifest.ComposeTemplateVersion != result.ComposeTemplateVersion ||
            manifest.ReplayProfile != result.ReplayProfile ||
            manifest.ConfigurationSha256 != result.ConfigurationSha256 ||
            manifest.RigProfileSha256 != result.RigProfileSha256 || manifest.ScheduleSha256 != result.ScheduleSha256 ||
            manifest.DeploymentLocationId != $"installer-{instanceId:D}" || manifest.DockerDaemon != result.DockerDaemon ||
            (!allowTransactionalDrift &&
             (manifest.ComposeModelSha256 != result.ComposeModelSha256 ||
              manifest.Image != result.Image || manifest.Catalog != result.Catalog)))
        {
            throw new InstallerException("The retained instance identities do not correlate.");
        }
    }

    private static bool AllowsTransactionalDrift(LifecycleRequest request, LifecycleOperationState? operation)
        => request.Resume && operation is
        {
            MutationStarted: true,
            Status: not InstallationStatus.Completed,
            Phase: LifecycleOperationPhase.CandidateVerified,
            Kind: LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or
                LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback
        };

    private static string PublicHost(string bindAddress)
        => bindAddress is "0.0.0.0" or "::" ? "localhost" : bindAddress;

    private static void EnsureTransactionalCorrelation(
        LifecycleOperationState operation,
        InstanceManifest manifest,
        InstallationResult result)
    {
        var imageTransition = operation.Kind is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback;
        var catalogTransition = operation.Kind is LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback;
        var imagesCorrelate = imageTransition
            ? operation.CandidateImage is not null &&
              (manifest.Image == operation.OriginalImage || manifest.Image == operation.CandidateImage) &&
              (result.Image == operation.OriginalImage || result.Image == operation.CandidateImage)
            : manifest.Image == operation.OriginalImage && result.Image == operation.OriginalImage;
        var catalogsCorrelate = catalogTransition
            ? operation.CandidateCatalog is not null &&
              (manifest.Catalog == operation.OriginalCatalog || manifest.Catalog == operation.CandidateCatalog) &&
              (result.Catalog == operation.OriginalCatalog || result.Catalog == operation.CandidateCatalog)
            : manifest.Catalog == operation.OriginalCatalog && result.Catalog == operation.OriginalCatalog;
        if (!imagesCorrelate || !catalogsCorrelate)
            throw new InstallerException("The retained transactional identities do not match the lifecycle operation.");
    }

    private static ComposeFiles ComposeFrom(InstallationPaths paths, InstanceManifest manifest, InstallationResult result)
    {
        var compact = manifest.InstanceId.ToString("N");
        return new ComposeFiles(
            Path.Combine(paths.ConfigRoot, "compose", "compose.yml"),
            Path.Combine(paths.ConfigRoot, "compose", "instance.env"),
            $"hvo-skymonitor-{compact}",
            $"hvo-skymonitor-{compact}",
            result.PasswordFile,
            string.Empty,
            manifest.ConfigurationSha256,
            manifest.RigProfileSha256,
            manifest.ScheduleSha256,
            manifest.RigProfileName,
            manifest.RigProfileVersion,
            manifest.ScheduleSchemaVersion,
            manifest.ScheduleState,
            manifest.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner
                ? $"hvo-skymonitor-{compact}-replay"
                : null);
    }

    private static string ReplaceEnvironmentValue(string environment, string key, string value)
    {
        var prefix = key + "=";
        var lines = environment.Split('\n');
        var matches = 0;
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].StartsWith(prefix, StringComparison.Ordinal)) continue;
            lines[index] = prefix + value;
            matches++;
        }
        return matches == 1 ? string.Join('\n', lines) : throw new InstallerException($"Compose environment omitted unique {key}.");
    }

    private static bool IsCanonicalImage(
        ImageInstallationIdentity? image,
        string configurationContract,
        string? daemonArchitecture,
        bool requireReplayRunner = false)
        => image is not null && HasValue(image.Source) && HasValue(image.ImmutableReference) &&
           System.Text.RegularExpressions.Regex.IsMatch(
               image.ImmutableReference,
               "^(sha256:[a-f0-9]{64}|[^@\\s]+@sha256:[a-f0-9]{64})$",
               System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
           image.ImageId is not null && image.ImageId.StartsWith("sha256:", StringComparison.Ordinal) &&
           IsSha256(image.ImageId["sha256:".Length..]) && image.Architecture is "amd64" or "arm64" &&
           image.Architecture == daemonArchitecture &&
           (image.ArchiveSha256 is null || IsSha256(image.ArchiveSha256)) &&
           image.Component == "CameraAgent" && image.ConfigurationContract == configurationContract &&
            image.CatalogContract == "hyg-v42-production-p3-s2" && IsSourceRevision(image.SourceRevision) &&
            (!requireReplayRunner || image.ReplayRunnerContract == "local-replay-runner-v1") &&
           (image.Distribution is null || IsValidDistribution(image.Distribution) &&
            image.Distribution.ManifestKind == DistributionManifestKind.InstallerRelease.ToString() &&
            image.Distribution.ReleaseTrain == "installer" &&
            image.Distribution.ReleaseTag == $"installer-v{image.Distribution.ReleaseVersion}" &&
            image.Distribution.AssetName.EndsWith(
                $"linux-{DistributionArchitecture(image.Architecture)}.tar.gz", StringComparison.Ordinal) &&
            image.ArchiveSha256 is not null && image.Distribution.AssetSha256 == image.ArchiveSha256);

    private static bool IsValidCatalog(CatalogInstallationIdentity? value)
        => value is not null && value.CatalogId == ProductionCatalog.CatalogId && HasValue(value.PackageVersion) &&
           value.SchemaVersion == "2" && value.PreprocessingVersion == "3" &&
           value.DatabaseSha256 == ProductionCatalog.DatabaseSha256 &&
           value.DatabaseLength == ProductionCatalog.DatabaseLength && value.RowCount == ProductionCatalog.RowCount &&
           HasValue(value.InstallRoot) && IsSha256(value.ManifestSha256) && value.Source == "local-offline" &&
           (value.Distribution is null || IsValidDistribution(value.Distribution) &&
            value.Distribution.ManifestKind == DistributionManifestKind.CatalogRelease.ToString() &&
             value.Distribution.ReleaseTrain == "catalog" && value.Distribution.ReleaseVersion == value.PackageVersion &&
             value.Distribution.ReleaseTag == $"catalog-{value.PackageVersion}");

    internal static void ValidateCanonicalCatalog(InstallationPaths paths, CatalogInstallationIdentity value)
    {
        if (!IsValidCatalog(value) || value.InstallRoot != paths.CatalogRoot)
            throw new InstallerException("The retained catalog identity is invalid or unsupported.");
    }

    private static bool IsValidDistribution(DistributionVerificationEvidence value)
        => HasValue(value.ManifestKind) && HasValue(value.ReleaseTrain) && HasValue(value.ReleaseVersion) &&
           HasValue(value.ReleaseTag) && IsSha256(value.ManifestSha256) &&
           value.ManifestLength is > 0 and <= DistributionVerifier.MaximumManifestBytes &&
           DistributionTrustRoot.IsCanonicalKeyId(value.SigningKeyId) && HasValue(value.AssetName) &&
           IsSha256(value.AssetSha256) && value.AssetLength is > 0 and <= DistributionVerifier.MaximumImageArchiveBytes &&
           IsSafeEvidenceUri(value.SourceBaseUri) && IsSafeEvidenceUri(value.ResolvedPublicUri) &&
           value.VerificationResult == "verified" && value.VerifiedUtc != default && value.VerifiedUtc.Offset == TimeSpan.Zero &&
           (value.ProvenanceAssetName is null && value.ProvenanceSha256 is null ||
            HasValue(value.ProvenanceAssetName) && IsSha256(value.ProvenanceSha256));

    private static bool IsSafeEvidenceUri(Uri? value)
        => value is { IsAbsoluteUri: true } && value.Scheme is "https" or "file" &&
           string.IsNullOrEmpty(value.UserInfo) && string.IsNullOrEmpty(value.Fragment);

    internal static string DistributionArchitecture(string dockerArchitecture)
        => dockerArchitecture == "amd64" ? "x64" : dockerArchitecture;

    internal static async Task<T> DeserializeRetainedAsync<T>(
        Stream stream,
        JsonTypeInfo<T> typeInfo,
        string description,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false)
                   ?? throw new InstallerException($"The retained {description} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException($"The retained {description} is invalid JSON.", exception);
        }
    }

    private static bool IsValidDaemon(DockerDaemonIdentity? value)
        => value is not null && HasValue(value.Id) && HasValue(value.Name) &&
           HasValue(value.Architecture) && HasValue(value.ServerVersion);

    private static bool HasValue(string? value) => !string.IsNullOrEmpty(value);

    private static bool IsSourceRevision(string? value)
        => value is { Length: 40 } && value.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool RequiresLifecycleControl(LifecycleOperationKind? operation)
        => operation is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or LifecycleOperationKind.Reinstall or
            LifecycleOperationKind.Uninstall or LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback;

    private static bool RequiresInstallationVerification(LifecycleOperationKind? operation)
        => operation is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or LifecycleOperationKind.Reinstall or
            LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback;

    internal static string? ResumableCatalogSelectionVersion(
        LifecycleRequest request,
        LifecycleOperationState? operation)
        => request.Resume && operation is
        {
            Kind: LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback,
            MutationStarted: true,
            CandidateCatalog: not null,
            Status: not InstallationStatus.Completed
        }
            ? operation.CandidateCatalog.PackageVersion
            : null;

    internal static async Task ValidateComposeAuthorityAsync(
        DockerClient docker,
        ComposeFiles compose,
        InstanceManifest manifest,
        CancellationToken cancellationToken)
    {
        var rendered = await docker.ComposeAsync(
            compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["config"], cancellationToken).ConfigureAwait(false);
        if (ComposeDeployment.ComputeSha256(rendered.StandardOutput) != manifest.ComposeModelSha256)
        {
            throw new InstallerException("The retained Compose model differs from the authenticated instance manifest.");
        }
    }

    internal static void EnsureDaemon(DockerDaemonIdentity expected, DockerDaemonIdentity actual)
    {
        if (expected.Id != actual.Id || expected.Name != actual.Name || expected.Architecture != actual.Architecture)
        {
            throw new InstallerException("The Docker daemon identity differs from the instance manifest.");
        }
    }

    private static void EnsureUpgradeStorage(InstallationPaths paths, string? imageArchive)
    {
        long instanceBytes = 0;
        foreach (var file in Directory.EnumerateFiles(paths.InstanceRoot, "*", SearchOption.AllDirectories))
        {
            instanceBytes = checked(instanceBytes + new FileInfo(file).Length);
        }
        var archiveBytes = imageArchive is null ? 0 : new FileInfo(imageArchive).Length;
        var required = checked(instanceBytes + archiveBytes + 256L * 1024 * 1024);
        var available = new DriveInfo(Path.GetPathRoot(paths.ProductRoot)!).AvailableFreeSpace;
        if (available < required)
        {
            throw new InstallerException("Insufficient free space for the candidate image and consistent rollback backup.");
        }
    }

    internal static LifecycleContinuityBoundary ToBoundary(LifecycleContinuity value)
        => new(
            value.CaptureState,
            value.CaptureVersion,
            value.CaptureSequence,
            value.RawPending,
            value.RawLeased,
            value.LanePending,
            value.LaneLeased,
            value.ProcessingPending,
            value.ProcessingLeased,
            value.OutboxPending,
            value.OutboxLeased,
            DateTimeOffset.UtcNow);

    private static void EnsureContinuity(LifecycleContinuityBoundary? before, LifecycleContinuity after)
    {
        if (before is null || after.CaptureSequence < before.CaptureSequence)
            throw new InstallerException("The candidate did not preserve the durable capture-sequence boundary.");
    }

    internal static ICameraAgentLifecycleClient CreateLifecycleClient(
        Uri baseAddress,
        Func<Uri, ICameraAgentLifecycleClient>? factory)
        => factory?.Invoke(baseAddress) ?? new CameraAgentLifecycleClient(baseAddress);

    private static LifecycleResult Result(
        LifecycleOperationKind? kind,
        string outcome,
        Guid? operationId,
        InstallationPaths paths,
        InstanceManifest manifest,
        DockerDaemonIdentity daemon,
        bool? running,
        bool? healthy)
        => new(
            DeploymentSchemaVersions.LifecycleOperation,
            kind,
            outcome,
            operationId,
            manifest.InstanceId,
            manifest.LifecycleCondition,
            paths.ProductRoot,
            paths.InstanceRoot,
            manifest.Image,
            manifest.PreviousImage,
            manifest.Catalog,
            manifest.PreviousCatalog,
            daemon,
            running,
            healthy,
            [paths.ConfigRoot, paths.StateRoot, paths.CatalogRoot],
            manifest.LifecycleCondition == InstanceLifecycleCondition.Uninstalled
                ? $"hvo-skymonitor cameraagent reinstall --instance-id {manifest.InstanceId:D}"
                : null,
            DateTimeOffset.UtcNow);

    private static async Task<LifecycleResult> ListAsync(string productRoot, CancellationToken cancellationToken)
    {
        var agentsRoot = Path.Combine(productRoot, "cameraagents");
        var instances = new List<string>();
        if (Directory.Exists(agentsRoot))
        {
            foreach (var manifestPath in Directory.EnumerateFiles(agentsRoot, "instance-manifest.json", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                var manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                instances.Add($"{manifest.InstanceId:D}:{manifest.LifecycleCondition}:{manifest.Image.ImageId}:{manifest.Catalog.PackageVersion}");
            }
        }
        return new LifecycleResult(
            DeploymentSchemaVersions.LifecycleOperation,
            null,
            "inventory",
            null,
            null,
            null,
            productRoot,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            instances,
            null,
            DateTimeOffset.UtcNow);
    }
}
