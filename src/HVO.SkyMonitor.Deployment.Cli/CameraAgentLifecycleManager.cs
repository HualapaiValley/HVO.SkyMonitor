using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HVO.SkyMonitor.Deployment.Contracts;

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
        EnsureCorrelated(paths, instanceId, manifest, result,
            request.Resume && retainedOperation is { MutationStarted: true, Status: not InstallationStatus.Completed });
        if (manifest.ComposeTemplateVersion == "cameraagent-compose-v1" && request.OwnerPasswordFile is null &&
            request.Operation is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or
                LifecycleOperationKind.Reinstall or LifecycleOperationKind.Uninstall or
                LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback)
        {
            throw new InstallUsageException("A legacy v1 instance requires --owner-password-file for authenticated drain and resume.");
        }
        var docker = new DockerClient(processRunner);
        var compose = ComposeFrom(paths, manifest, result);
        var daemon = await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        EnsureDaemon(manifest.DockerDaemon, daemon);
        if (request.Operation is null)
        {
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
            var outcome = runtime.Exists && runtime.ImageId != manifest.Image.ImageId ? "drifted" : "status";
            return Result(null, outcome, null, paths, manifest, daemon, runtime.Running, runtime.Healthy);
        }
        SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
        using var productLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        result = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        EnsureCorrelated(paths, instanceId, manifest, result,
            request.Resume && retainedOperation is { MutationStarted: true, Status: not InstallationStatus.Completed });
        return request.Operation switch
        {
            LifecycleOperationKind.Upgrade => await ChangeImageAsync(
                request, paths, manifest, result, compose, docker, processRunner, lifecycleClientFactory, uid, gid,
                ownerClientFactory,
                rollback: false, cancellationToken).ConfigureAwait(false),
            LifecycleOperationKind.Rollback => await ChangeImageAsync(
                request, paths, manifest, result, compose, docker, processRunner, lifecycleClientFactory, uid, gid,
                ownerClientFactory,
                rollback: true, cancellationToken).ConfigureAwait(false),
            LifecycleOperationKind.Reinstall => await ReinstallAsync(
                request, paths, manifest, result, compose, docker, lifecycleClientFactory, ownerClientFactory, daemon, cancellationToken)
                .ConfigureAwait(false),
            LifecycleOperationKind.Uninstall => await UninstallAsync(
                request, paths, manifest, result, compose, docker, lifecycleClientFactory, daemon, cancellationToken)
                .ConfigureAwait(false),
            LifecycleOperationKind.Purge => await PurgeAsync(
                request, paths, manifest, compose, docker, daemon, cancellationToken).ConfigureAwait(false),
            LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback =>
                await CatalogLifecycleManager.SelectAsync(
                    request, paths, manifest, result, compose, docker, lifecycleClientFactory, daemon, cancellationToken)
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
        bool rollback,
        CancellationToken cancellationToken)
    {
        if (request.OwnerPasswordFile is null)
            throw new InstallUsageException("Image upgrade and rollback require --owner-password-file to verify owner login on the candidate.");
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw new InstallerException("An uninstalled instance must be reinstalled before image lifecycle operations.");
        if (rollback && manifest.PreviousImage is null) throw new InstallerException("No previous image is retained for rollback.");
        var operation = await BeginAsync(request, paths, rollback ? LifecycleOperationKind.Rollback : LifecycleOperationKind.Upgrade,
            manifest, cancellationToken).ConfigureAwait(false);
        if (!operation.MutationStarted)
        {
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            await docker.VerifyContainerAsync(
                compose, paths, manifest.Image, uid, gid, cancellationToken,
                manifest.ComposeTemplateVersion != "cameraagent-compose-v1").ConfigureAwait(false);
            EnsureUpgradeStorage(paths, request.ImageArchive);
        }
        var operationRoot = Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        var previousManifestPath = Path.Combine(operationRoot, "previous-instance-manifest.json");
        var previousResultPath = Path.Combine(operationRoot, "previous-installation-result.json");
        if (operation.MutationStarted && operation.Phase != LifecycleOperationPhase.Committed &&
            File.Exists(previousManifestPath) && File.Exists(previousResultPath))
        {
            SafeFileSystem.WriteTextAtomic(
                paths.ManifestPath,
                await File.ReadAllTextAsync(previousManifestPath, cancellationToken).ConfigureAwait(false));
            SafeFileSystem.WriteTextAtomic(
                paths.ResultPath,
                await File.ReadAllTextAsync(previousResultPath, cancellationToken).ConfigureAwait(false));
            manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            installationResult = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        }
        if (operation is { MutationStarted: true, Phase: LifecycleOperationPhase.Committed })
        {
            if (operation.CandidateImage is null || manifest.Image.ImageId != operation.CandidateImage.ImageId ||
                installationResult.Image.ImageId != operation.CandidateImage.ImageId ||
                manifest.LastLifecycleOperationId != operation.OperationId)
            {
                throw new InstallerException("The committed image lifecycle records do not match the retained operation.");
            }
            var committedVerificationToken = await ReadSecretAsync(
                Path.Combine(paths.ConfigRoot, "installation-verification", "token"), cancellationToken).ConfigureAwait(false);
            var committedLifecycleToken = await GetOrCreateLifecycleControlTokenAsync(paths, cancellationToken).ConfigureAwait(false);
            ValidateLifecycleControlToken(manifest, committedLifecycleToken);
            var committedOwner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
            await VerifyCandidateAsync(committedOwner, docker, compose, paths, manifest, installationResult, manifest.Image,
                committedVerificationToken, uid, gid, manifest.ComposeTemplateVersion != "cameraagent-compose-v1", cancellationToken)
                .ConfigureAwait(false);
            var committedLifecycle = CreateLifecycleClient(request, manifest, installationResult.Url, lifecycleClientFactory);
            await committedLifecycle.ResumeAsync(operation.OperationId, committedLifecycleToken, cancellationToken).ConfigureAwait(false);
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result(operation.Kind, "completed", operation.OperationId, paths, manifest, manifest.DockerDaemon, true, true);
        }
        ImageInstallationIdentity candidate;
        var candidateComposeTemplateVersion = rollback
            ? manifest.PreviousComposeTemplateVersion ??
              (manifest.PreviousImage?.Component is null ? "cameraagent-compose-v1" : ComposeDeployment.TemplateVersion)
            : ComposeDeployment.TemplateVersion;
        if (rollback)
        {
            var target = manifest.PreviousImage!;
            var synthetic = ImageRequest(manifest, target.ImmutableReference, null, null, noDownload: true);
            var prepared = await docker.PrepareImageAsync(synthetic, false, cancellationToken).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, prepared.Daemon);
            if (prepared.Image.ImageId != target.ImageId || prepared.Image.Architecture != target.Architecture)
            {
                throw new InstallerException("The retained rollback image no longer matches its immutable identity.");
            }
            candidate = target.Component is null
                ? prepared.Image with
                {
                    Source = target.Source,
                    ImmutableReference = target.ImmutableReference,
                    ArchiveSha256 = target.ArchiveSha256,
                    Distribution = target.Distribution,
                    UpgradeCompatibility = target.UpgradeCompatibility
                }
                : target;
        }
        else
        {
            var synthetic = ImageRequest(
                manifest, request.ImageReference!, request.ImageArchive, request.ImageArchiveSha256, request.NoDownload);
            var prepared = await docker.PrepareImageAsync(synthetic, !request.DryRun, cancellationToken).ConfigureAwait(false);
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
        if (candidateComposeTemplateVersion != "cameraagent-compose-v1" &&
            (candidate.Component != "CameraAgent" || candidate.ConfigurationContract != manifest.ComponentSchemaVersion ||
             candidate.CatalogContract != "hyg-v42-production-p3-s2" || !IsSourceRevision(candidate.SourceRevision)))
        {
            throw new InstallerException("The candidate image does not declare the required CameraAgent configuration and catalog contracts.");
        }
        if (candidateComposeTemplateVersion == "cameraagent-compose-v1" && request.OwnerPasswordFile is null)
            throw new InstallUsageException("Rolling back to a legacy v1 image requires --owner-password-file.");
        if (!rollback && (!request.MigrationBackwardCompatible || candidate.UpgradeCompatibility != "backward-compatible"))
        {
            throw new InstallerException("The candidate does not declare backward-compatible state migration; an explicit transactional restore path is required.");
        }
        if (rollback && manifest.Image.UpgradeCompatibility != "backward-compatible" &&
            manifest.UpgradeCompatibility != "backward-compatible")
        {
            throw new InstallerException("Automatic rollback is forbidden because the active image did not declare backward-compatible migration.");
        }
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
        operation = await RecordAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        EnsureCatalogSelectionSetting(paths, manifest.Catalog.PackageVersion);
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(paths.OperationsRoot, "lifecycle"));
        SafeFileSystem.CreateOwnerDirectory(operationRoot);
        var previousEnvironmentPath = Path.Combine(operationRoot, "previous.env");
        var previousComposePath = Path.Combine(operationRoot, "previous-compose.yml");
        var originalEnvironment = operation.MutationStarted && File.Exists(previousEnvironmentPath)
            ? await File.ReadAllTextAsync(previousEnvironmentPath, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllTextAsync(compose.EnvironmentFile, cancellationToken).ConfigureAwait(false);
        var originalCompose = operation.MutationStarted && File.Exists(previousComposePath)
            ? await File.ReadAllTextAsync(previousComposePath, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllTextAsync(compose.ComposeFile, cancellationToken).ConfigureAwait(false);
        var rollbackRoot = Path.Combine(paths.DeploymentStateRoot, "rollback");
        var retainedRollbackEnvironment = Path.Combine(rollbackRoot, "previous.env");
        var retainedRollbackCompose = Path.Combine(rollbackRoot, "previous-compose.yml");
        if (rollback && (!File.Exists(retainedRollbackEnvironment) || !File.Exists(retainedRollbackCompose)))
            throw new InstallerException("The retained rollback image is missing its exact Compose configuration.");
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
        if (!rollback && manifest.ComposeTemplateVersion == "cameraagent-compose-v1")
        {
            candidateEnvironment += $"HVO_INSTANCE_ID={manifest.InstanceId:D}\n";
        }
        var stagedEnvironment = Path.Combine(operationRoot, "candidate.env");
        var stagedCompose = Path.Combine(operationRoot, "candidate-compose.yml");
        SafeFileSystem.WriteTextAtomic(stagedEnvironment, candidateEnvironment);
        SafeFileSystem.WriteTextAtomic(
            stagedCompose,
            rollback
                ? await File.ReadAllTextAsync(retainedRollbackCompose, cancellationToken).ConfigureAwait(false)
                : UpgradeComposeTemplate(originalCompose, manifest.ComposeTemplateVersion));
        var candidateCompose = compose with { ComposeFile = stagedCompose, EnvironmentFile = stagedEnvironment };
        var renderedCandidate = await docker.ComposeAsync(
            candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName, ["config"], cancellationToken)
            .ConfigureAwait(false);
        var candidateComposeSha256 = ComposeDeployment.ComputeSha256(renderedCandidate.StandardOutput);
        var verificationToken = await ReadSecretAsync(
            Path.Combine(paths.ConfigRoot, "installation-verification", "token"), cancellationToken).ConfigureAwait(false);
        var lifecycleControlToken = await GetOrCreateLifecycleControlTokenAsync(paths, cancellationToken).ConfigureAwait(false);
        ValidateLifecycleControlToken(manifest, lifecycleControlToken);
        var baseAddress = installationResult.Url;
        var lifecycle = CreateLifecycleClient(request, manifest, baseAddress, lifecycleClientFactory);
        var candidateLifecycle = CreateLifecycleClient(
            request, manifest with { ComposeTemplateVersion = candidateComposeTemplateVersion }, baseAddress, lifecycleClientFactory);
        var owner = ownerClientFactory?.Invoke(baseAddress) ?? new OwnerBootstrapClient(baseAddress);
        if (operation.MutationStarted)
        {
            SafeFileSystem.WriteTextAtomic(compose.ComposeFile, originalCompose);
            SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, originalEnvironment);
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["up", "--detach", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                verificationToken, uid, gid, manifest.ComposeTemplateVersion != "cameraagent-compose-v1",
                cancellationToken).ConfigureAwait(false);
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
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["stop"], cancellationToken)
                .ConfigureAwait(false);
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
            await docker.ComposeAsync(candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName,
                ["up", "--detach", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            await VerifyCandidateAsync(owner, docker, candidateCompose, paths, manifest, installationResult, candidate, verificationToken, uid, gid,
                candidateComposeTemplateVersion != "cameraagent-compose-v1", cancellationToken)
                .ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.CandidateVerified }, cancellationToken)
                .ConfigureAwait(false);
            await docker.ComposeAsync(candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName, ["restart"], cancellationToken)
                .ConfigureAwait(false);
            await VerifyCandidateAsync(owner, docker, candidateCompose, paths, manifest, installationResult, candidate, verificationToken, uid, gid,
                candidateComposeTemplateVersion != "cameraagent-compose-v1", cancellationToken)
                .ConfigureAwait(false);
            var postMutation = await candidateLifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken)
                .ConfigureAwait(false);
            EnsureContinuity(operation.PreMutationContinuity, postMutation);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.CandidateVerified,
                PostMutationContinuity = ToBoundary(postMutation)
            }, cancellationToken).ConfigureAwait(false);
            SafeFileSystem.CreateOwnerDirectory(rollbackRoot);
            SafeFileSystem.WriteTextAtomic(retainedRollbackCompose, originalCompose);
            SafeFileSystem.WriteTextAtomic(retainedRollbackEnvironment, originalEnvironment);
            SafeFileSystem.WriteTextAtomic(compose.ComposeFile, await File.ReadAllTextAsync(stagedCompose, cancellationToken).ConfigureAwait(false));
            SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, candidateEnvironment);
            var committed = manifest with
            {
                Image = candidate,
                PreviousImage = manifest.Image,
                ComposeTemplateVersion = candidateComposeTemplateVersion,
                ComposeModelSha256 = candidateComposeSha256,
                BindAddress = installationResult.Url.Host,
                Port = installationResult.Url.Port,
                UpgradeCompatibility = candidate.UpgradeCompatibility ?? "requires-declared-compatible-migration",
                LifecycleControlTokenSha256 = ComposeDeployment.ComputeSha256(lifecycleControlToken),
                PreviousComposeTemplateVersion = manifest.ComposeTemplateVersion,
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
                    ComposeTemplateVersion = candidateComposeTemplateVersion,
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
                    operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Restoring }, recovery.Token)
                        .ConfigureAwait(false);
                    var logs = await docker.ReadContainerLogsAsync(compose.ContainerName, recovery.Token).ConfigureAwait(false);
                    SafeFileSystem.WriteTextAtomic(Path.Combine(operationRoot, "candidate-diagnostics.txt"), logs);
                    if (mutationStarted)
                    {
                        SafeFileSystem.WriteTextAtomic(compose.ComposeFile, originalCompose);
                        SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, originalEnvironment);
                        if (File.Exists(previousManifestPath))
                            SafeFileSystem.WriteTextAtomic(paths.ManifestPath, await File.ReadAllTextAsync(previousManifestPath, recovery.Token).ConfigureAwait(false));
                        if (File.Exists(previousResultPath))
                            SafeFileSystem.WriteTextAtomic(paths.ResultPath, await File.ReadAllTextAsync(previousResultPath, recovery.Token).ConfigureAwait(false));
                        if (hadRollbackCompose)
                            SafeFileSystem.WriteTextAtomic(retainedRollbackCompose, await File.ReadAllTextAsync(priorRollbackCompose, recovery.Token).ConfigureAwait(false));
                        else if (File.Exists(retainedRollbackCompose))
                            File.Delete(retainedRollbackCompose);
                        if (hadRollbackEnvironment)
                            SafeFileSystem.WriteTextAtomic(retainedRollbackEnvironment, await File.ReadAllTextAsync(priorRollbackEnvironment, recovery.Token).ConfigureAwait(false));
                        else if (File.Exists(retainedRollbackEnvironment))
                            File.Delete(retainedRollbackEnvironment);
                    }
                    await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                        ["up", "--detach", "--remove-orphans"], recovery.Token).ConfigureAwait(false);
                    await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                        verificationToken, uid, gid, manifest.ComposeTemplateVersion != "cameraagent-compose-v1",
                        recovery.Token).ConfigureAwait(false);
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
        DockerDaemonIdentity daemon,
        CancellationToken cancellationToken)
    {
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Uninstall, manifest, cancellationToken)
            .ConfigureAwait(false);
        if (request.DryRun) return Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, null, null);
        await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with { MutationStarted = true }, cancellationToken).ConfigureAwait(false);
        if (manifest.LifecycleCondition == InstanceLifecycleCondition.Installed)
        {
            var before = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
            if (before.Running)
            {
                var token = await GetOrCreateLifecycleControlTokenAsync(paths, cancellationToken)
                    .ConfigureAwait(false);
                ValidateLifecycleControlToken(manifest, token);
                var lifecycle = CreateLifecycleClient(request, manifest, installationResult.Url, lifecycleClientFactory);
                await lifecycle.PauseAndDrainAsync(operation.OperationId, token, cancellationToken).ConfigureAwait(false);
            }
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["down", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
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
        DockerDaemonIdentity daemon,
        CancellationToken cancellationToken)
    {
        if (request.OwnerPasswordFile is null)
            throw new InstallUsageException("Reinstall requires --owner-password-file to verify retained owner login.");
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Uninstalled)
        {
            var retained = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
            if (request.Resume && retained is { Kind: LifecycleOperationKind.Reinstall, MutationStarted: true } &&
                manifest.LastLifecycleOperationId == retained.OperationId)
            {
                var resumedVerificationToken = await ReadSecretAsync(
                    Path.Combine(paths.ConfigRoot, "installation-verification", "token"), cancellationToken).ConfigureAwait(false);
                var resumedLifecycleToken = await GetOrCreateLifecycleControlTokenAsync(paths, cancellationToken).ConfigureAwait(false);
                ValidateLifecycleControlToken(manifest, resumedLifecycleToken);
                var resumedOwner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
                await VerifyCandidateAsync(resumedOwner, docker, compose, paths, manifest, installationResult, manifest.Image,
                    resumedVerificationToken, manifest.RuntimeUid, manifest.RuntimeGid,
                    manifest.ComposeTemplateVersion != "cameraagent-compose-v1", cancellationToken).ConfigureAwait(false);
                var resumedLifecycle = CreateLifecycleClient(request, manifest, installationResult.Url, lifecycleClientFactory);
                if (retained.Phase != LifecycleOperationPhase.Committed)
                    retained = await RecordAsync(paths, retained with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                        .ConfigureAwait(false);
                await resumedLifecycle.ResumeAsync(retained.OperationId, resumedLifecycleToken, cancellationToken).ConfigureAwait(false);
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
        await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
            ["up", "--detach", "--force-recreate", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
        var verificationToken = await ReadSecretAsync(
            Path.Combine(paths.ConfigRoot, "installation-verification", "token"), cancellationToken).ConfigureAwait(false);
        var lifecycleToken = await GetOrCreateLifecycleControlTokenAsync(paths, cancellationToken).ConfigureAwait(false);
        ValidateLifecycleControlToken(manifest, lifecycleToken);
        var owner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
        await VerifyCandidateAsync(
            owner, docker, compose, paths, manifest, installationResult, manifest.Image, verificationToken,
            manifest.RuntimeUid, manifest.RuntimeGid, manifest.ComposeTemplateVersion != "cameraagent-compose-v1",
            cancellationToken).ConfigureAwait(false);
        var lifecycle = CreateLifecycleClient(request, manifest, installationResult.Url, lifecycleClientFactory);
        var postMutation = await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleToken, cancellationToken)
            .ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.CandidateVerified,
            PostMutationContinuity = ToBoundary(postMutation)
        }, cancellationToken).ConfigureAwait(false);
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
        await lifecycle.ResumeAsync(operation.OperationId, lifecycleToken, cancellationToken).ConfigureAwait(false);
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

    private static async Task VerifyCandidateAsync(
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
        bool requireOwnershipLabel,
        CancellationToken cancellationToken)
    {
        await owner.WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
        await owner.VerifyInstallationAsync(
            verificationToken,
            new InstallationVerificationExpectation(
                manifest.InstanceId.ToString("D"), manifest.OwnerEmail, result.OwnerBootstrapState,
                manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
                manifest.DeploymentLocationId, manifest.DeploymentLocationVersion, manifest.DeploymentLocationSha256,
                manifest.Catalog),
            cancellationToken).ConfigureAwait(false);
        await docker.VerifyContainerAsync(compose, paths, image, uid, gid, cancellationToken, requireOwnershipLabel).ConfigureAwait(false);
    }

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
        var value = await JsonSerializer.DeserializeAsync(
            stream, DeploymentJsonContext.Default.LifecycleOperationState, cancellationToken).ConfigureAwait(false)
            ?? throw new InstallerException("The retained lifecycle operation is empty.");
        if (value.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation || value.OperationId == Guid.Empty ||
            value.InstanceId is null || value.InstanceId == Guid.Empty || value.RequestSha256.Length != 64 ||
            value.RequestSha256.Any(static character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !Enum.IsDefined(value.Kind) || !Enum.IsDefined(value.Phase) || !Enum.IsDefined(value.Status) ||
            value.StartedUtc == default || value.UpdatedUtc < value.StartedUtc || value.OriginalImage is null ||
            value.OriginalCatalog is null || value.Status == InstallationStatus.Completed && value.Phase != LifecycleOperationPhase.Completed)
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
        var evidence = await JsonSerializer.DeserializeAsync(
            evidenceStream, DeploymentJsonContext.Default.PurgeDeletionEvidence, cancellationToken).ConfigureAwait(false)
            ?? throw new InstallerException("The purge deletion evidence is empty.");
        if (evidence.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation || evidence.OperationId != operation.OperationId ||
            evidence.RequestSha256 != operation.RequestSha256 || evidence.Manifest.InstanceId != operation.InstanceId || evidence.Tree.Count == 0)
            throw new InstallerException("The purge deletion evidence does not match its retained operation.");
        if (evidence.HostIdentitySha256 != LocalHostIdentity.ReadSha256())
            throw new InstallerException("The purge deletion evidence belongs to a different host.");
        var manifest = evidence.Manifest;
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
        return await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
            .ConfigureAwait(false) ?? throw new InstallerException("The instance manifest is empty.");
    }

    internal static async Task<InstallationResult> ReadResultAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        return await JsonSerializer.DeserializeAsync(stream, DeploymentJsonContext.Default.InstallationResult, cancellationToken)
            .ConfigureAwait(false) ?? throw new InstallerException("The installation result is empty.");
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

    internal static async Task<string> GetOrCreateLifecycleControlTokenAsync(
        InstallationPaths paths,
        CancellationToken cancellationToken)
    {
        var tokenPath = Path.Combine(paths.ConfigRoot, "lifecycle-control", "token");
        if (!File.Exists(tokenPath))
        {
            tokenPath = await CredentialFile.GetOrCreateAsync(null, tokenPath, cancellationToken).ConfigureAwait(false);
        }
        var token = await ReadSecretAsync(tokenPath, cancellationToken).ConfigureAwait(false);
        var mirrorPath = Path.Combine(paths.ConfigRoot, "secrets", "LifecycleControl__Token");
        if (File.Exists(mirrorPath))
        {
            var mirror = await ReadSecretAsync(mirrorPath, cancellationToken).ConfigureAwait(false);
            if (mirror != token) throw new InstallerException("The lifecycle control credential mirror does not match its authority file.");
        }
        else
        {
            SafeFileSystem.WriteTextAtomic(mirrorPath, token);
        }
        return token;
    }

    private static void EnsureCatalogSelectionSetting(InstallationPaths paths, string packageVersion)
    {
        var path = Path.Combine(paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion");
        if (!File.Exists(path)) SafeFileSystem.WriteTextAtomic(path, packageVersion);
    }

    internal static void ValidateLifecycleControlToken(InstanceManifest manifest, string token)
    {
        if (manifest.ComposeTemplateVersion != "cameraagent-compose-v1" &&
            manifest.LifecycleControlTokenSha256 != ComposeDeployment.ComputeSha256(token))
            throw new InstallerException("The lifecycle control credential does not match the retained instance manifest.");
    }

    private static void EnsureCorrelated(
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
            manifest.OwnerEmail != result.OwnerEmail || manifest.DeploymentLocationId != $"installer-{instanceId:D}" ||
            (!allowTransactionalDrift &&
             (manifest.ComposeTemplateVersion != result.ComposeTemplateVersion ||
              manifest.ComposeModelSha256 != result.ComposeModelSha256 ||
              manifest.ConfigurationSha256 != result.ConfigurationSha256 ||
              manifest.RigProfileSha256 != result.RigProfileSha256 || manifest.ScheduleSha256 != result.ScheduleSha256 ||
              manifest.Image.ImageId != result.Image.ImageId || manifest.Catalog.PackageVersion != result.Catalog.PackageVersion ||
              manifest.Catalog.DatabaseSha256 != result.Catalog.DatabaseSha256 || manifest.DockerDaemon != result.DockerDaemon)))
        {
            throw new InstallerException("The retained instance identities do not correlate.");
        }
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
            manifest.ScheduleState);
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

    private static bool IsSourceRevision(string? value)
        => value is { Length: 40 } && value.All(static character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static string UpgradeComposeTemplate(string compose, string version)
    {
        if (version == ComposeDeployment.TemplateVersion) return compose;
        if (version != "cameraagent-compose-v1") throw new InstallerException("The retained Compose template version is unsupported.");
        const string marker = "    pull_policy: never\n";
        const string labels = "    labels:\n      io.hvo.skymonitor.product: HVO.SkyMonitor\n      io.hvo.skymonitor.component: CameraAgent\n      io.hvo.skymonitor.instance-id: ${HVO_INSTANCE_ID:?instance id required}\n";
        var migrated = compose.Replace(marker, marker + labels, StringComparison.Ordinal);
        return migrated != compose ? migrated : throw new InstallerException("The retained v1 Compose template is not canonical.");
    }

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
        LifecycleRequest request,
        InstanceManifest manifest,
        Uri baseAddress,
        Func<Uri, ICameraAgentLifecycleClient>? factory)
        => factory?.Invoke(baseAddress) ?? (request.OwnerPasswordFile is null
            ? new CameraAgentLifecycleClient(baseAddress)
            : new OwnerAuthenticatedLifecycleClient(baseAddress, manifest.OwnerEmail, request.OwnerPasswordFile));

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
                ? $"hvo-skymonitor cameraagent reinstall --instance-id {manifest.InstanceId:D} --owner-password-file <owner-password-file>"
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
                await using var stream = SafeFileSystem.OpenOwnerFileRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync(
                    stream, DeploymentJsonContext.Default.InstanceManifest, cancellationToken).ConfigureAwait(false)
                    ?? throw new InstallerException("An instance manifest is empty.");
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
