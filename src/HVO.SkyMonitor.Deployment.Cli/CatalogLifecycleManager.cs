using System.Text.Json;
using HVO.SkyMonitor.Catalog.Sqlite;
using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

internal static class CatalogLifecycleManager
{
    public static async Task<LifecycleResult> ExecuteAsync(
        LifecycleRequest request,
        IProcessRunner processRunner,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var paths = InstallationPaths.Create(request.ProductRoot, Guid.Empty, ProductionCatalog.CatalogId);
        if (request.DryRun && request.Operation == LifecycleOperationKind.CatalogInstall)
        {
            using var acquired = await AcquireAsync(request, cancellationToken).ConfigureAwait(false);
            var temporary = Path.Combine(Path.GetTempPath(), $"hvo-catalog-plan-{Guid.NewGuid():N}");
            try
            {
                var plannedIdentity = CatalogInstaller.Install(acquired.BundlePath, temporary, Guid.NewGuid()) with
                {
                    InstallRoot = paths.CatalogRoot,
                    Distribution = acquired.Evidence
                };
                return Result(request.Operation, "planned", null, paths, plannedIdentity, null);
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    SafeFileSystem.MakeTreeOwnerWritable(temporary);
                    Directory.Delete(temporary, true);
                }
            }
        }
        if (request.DryRun && !Directory.Exists(paths.ProductRoot))
            throw new InstallerException("The product root does not exist for catalog garbage-collection planning.");
        if (!request.DryRun)
        {
            if (uid == 0) throw new InstallerException("Run catalog lifecycle operations as the deployment runtime user, not root.");
            SafeFileSystem.EnsureSafeExistingAncestors(paths.ProductRoot);
            SafeFileSystem.CreateOwnerDirectory(paths.ProductRoot);
            SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
        }
        using var productLock = request.DryRun
            ? null
            : OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        if (request.Operation == LifecycleOperationKind.CatalogInstall)
        {
            var statePath = Path.Combine(paths.OperationsRoot, "catalog-install.lifecycle.json");
            var retained = await ReadCatalogInstallOperationAsync(statePath, cancellationToken).ConfigureAwait(false);
            CatalogInstallOperationEvidence operation;
            if (retained is { Status: not InstallationStatus.Completed })
            {
                if (!request.Resume || retained.RequestSha256 != request.ComputeRequestSha256())
                    throw new InstallerException("An incomplete catalog install exists; resume the exact request.");
                operation = retained;
            }
            else
            {
                if (request.Resume) throw new InstallerException("No matching incomplete catalog install exists.");
                operation = new CatalogInstallOperationEvidence(
                    DeploymentSchemaVersions.LifecycleOperation, Guid.NewGuid(), request.ComputeRequestSha256(),
                    "planned", InstallationStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                await WriteCatalogInstallOperationAsync(statePath, operation, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                if (operation is { Phase: "installed", InstalledCatalog: not null })
                {
                    ValidateInstalled(paths, operation.InstalledCatalog);
                    await WriteInstalledIdentityAsync(paths, operation.InstalledCatalog, cancellationToken).ConfigureAwait(false);
                    operation = operation with
                    {
                        Phase = "completed",
                        Status = InstallationStatus.Completed,
                        UpdatedUtc = DateTimeOffset.UtcNow
                    };
                    await WriteCatalogInstallOperationAsync(statePath, operation, cancellationToken).ConfigureAwait(false);
                    return Result(request.Operation, "completed", operation.OperationId, paths, operation.InstalledCatalog, null);
                }
                using var acquired = await AcquireAsync(request, cancellationToken).ConfigureAwait(false);
                var acquiredBundleSha256 = await ComputeBundleSha256Async(acquired.BundlePath, cancellationToken).ConfigureAwait(false);
                if (operation.AcquiredBundleSha256 is not null && operation.AcquiredBundleSha256 != acquiredBundleSha256)
                    throw new InstallerException("The reacquired catalog bundle differs from the retained operation.");
                operation = operation with
                {
                    Phase = "acquired",
                    Status = InstallationStatus.Running,
                    AcquiredBundleSha256 = acquiredBundleSha256,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                await WriteCatalogInstallOperationAsync(statePath, operation, cancellationToken).ConfigureAwait(false);
                var installedIdentity = CatalogInstaller.Install(acquired.BundlePath, paths.CatalogRoot, operation.OperationId) with
                {
                    Distribution = acquired.Evidence
                };
                ValidateSignedIdentity(installedIdentity, acquired.SignedIdentity);
                operation = operation with
                {
                    Phase = "installed",
                    InstalledCatalog = installedIdentity,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                await WriteCatalogInstallOperationAsync(statePath, operation, cancellationToken).ConfigureAwait(false);
                await WriteInstalledIdentityAsync(paths, installedIdentity, cancellationToken).ConfigureAwait(false);
                operation = operation with
                {
                    Phase = "completed",
                    Status = InstallationStatus.Completed,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
                await WriteCatalogInstallOperationAsync(statePath, operation, cancellationToken).ConfigureAwait(false);
                return Result(request.Operation, "completed", operation.OperationId, paths, installedIdentity, null);
            }
            catch (Exception exception)
            {
                await WriteCatalogInstallOperationAsync(statePath, operation with
                {
                    Status = InstallationStatus.Failed,
                    FailureMessage = Redaction.SafeDiagnostic(exception.Message),
                    UpdatedUtc = DateTimeOffset.UtcNow
                }, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        return await GarbageCollectAsync(request, paths, new DockerClient(processRunner), cancellationToken).ConfigureAwait(false);
    }

    public static async Task<LifecycleResult> SelectAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        ComposeFiles compose,
        DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string lifecycleControlToken,
        DockerDaemonIdentity daemon,
        string verificationToken,
        CancellationToken cancellationToken)
    {
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw new InstallerException("Catalog selection requires an installed instance.");
        var rollback = request.Operation == LifecycleOperationKind.CatalogRollback;
        var retained = request.Resume
            ? await CameraAgentLifecycleManager.ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false)
            : null;
        var candidate = retained?.CandidateCatalog ?? (rollback
            ? manifest.PreviousCatalog ?? throw new InstallerException("No previous catalog selection is retained.")
            : await ReadInstalledIdentityAsync(paths, request.CatalogVersion!, cancellationToken).ConfigureAwait(false));
        ValidateInstalled(paths, candidate);
        await CameraAgentLifecycleManager.ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken)
            .ConfigureAwait(false);
        if (candidate.PackageVersion == manifest.Catalog.PackageVersion && !request.Resume)
            throw new InstallerException("The requested catalog package is already selected.");
        var operation = await CameraAgentLifecycleManager.BeginAsync(
            request, paths, request.Operation!.Value, manifest, cancellationToken).ConfigureAwait(false);
        if (operation is { MutationStarted: true, Phase: LifecycleOperationPhase.Committed, CandidateCatalog: not null })
        {
            candidate = operation.CandidateCatalog;
            ValidateInstalled(paths, candidate);
        }
        else if (operation.CandidateCatalog is not null && operation.CandidateCatalog != candidate)
            throw new InstallerException("The retained catalog operation has a different candidate identity.");
        if (operation.Phase != LifecycleOperationPhase.Committed)
        {
            operation = operation with
            {
                CandidateCatalog = candidate,
                Phase = LifecycleOperationPhase.CandidateValidated
            };
        }
        if (request.DryRun) return Result(request.Operation, "planned", operation.OperationId, paths, candidate, manifest);
        if (operation.Phase != LifecycleOperationPhase.Committed)
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        var lifecycle = CameraAgentLifecycleManager.CreateLifecycleClient(
            installationResult.Url, lifecycleClientFactory);
        var settingPath = Path.Combine(paths.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion");
        var operationRoot = Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        SafeFileSystem.CreateOwnerDirectory(Path.Combine(paths.OperationsRoot, "lifecycle"));
        SafeFileSystem.CreateOwnerDirectory(operationRoot);
        var previousSettingPath = Path.Combine(operationRoot, "previous-catalog-version");
        var previousManifestPath = Path.Combine(operationRoot, "previous-instance-manifest.json");
        var previousResultPath = Path.Combine(operationRoot, "previous-installation-result.json");
        if (operation.MutationStarted && operation.Phase != LifecycleOperationPhase.Committed)
        {
            if (!File.Exists(previousSettingPath) || !File.Exists(previousManifestPath) || !File.Exists(previousResultPath))
                throw new InstallerException("The retained catalog operation is missing its recovery identity records.");
            (manifest, installationResult) = await CameraAgentLifecycleManager.ReadRecoverySnapshotAsync(
                paths, operation, previousManifestPath, previousResultPath, cancellationToken).ConfigureAwait(false);
            await CameraAgentLifecycleManager.WriteRecoverySnapshotAsync(
                paths, manifest, installationResult, cancellationToken).ConfigureAwait(false);
        }
        var originalSetting = operation.MutationStarted && File.Exists(previousSettingPath)
            ? await File.ReadAllTextAsync(previousSettingPath, cancellationToken).ConfigureAwait(false)
            : await File.ReadAllTextAsync(settingPath, cancellationToken).ConfigureAwait(false);
        if (operation is { MutationStarted: true, Phase: LifecycleOperationPhase.Committed })
        {
            if (operation.CandidateCatalog is null || manifest.Catalog != operation.CandidateCatalog ||
                installationResult.Catalog != operation.CandidateCatalog ||
                manifest.LastLifecycleOperationId != operation.OperationId)
            {
                throw new InstallerException("The committed catalog lifecycle records do not match the retained operation.");
            }
            await VerifyAsync(manifest, installationResult, manifest.Catalog, compose, paths, docker, ownerClientFactory,
                    verificationToken, cancellationToken)
                .ConfigureAwait(false);
            await lifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            operation = await CameraAgentLifecycleManager.CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result(request.Operation, "completed", operation.OperationId, paths, manifest.Catalog, manifest);
        }
        if (candidate.PackageVersion == manifest.Catalog.PackageVersion)
            throw new InstallerException("The requested catalog package is already selected.");
        if (operation.MutationStarted)
        {
            SafeFileSystem.WriteTextAtomic(settingPath, originalSetting);
            CameraAgentLifecycleManager.EnsureDaemon(
                manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["up", "--detach", "--force-recreate", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            await VerifyAsync(manifest, installationResult, operation.OriginalCatalog!, compose, paths, docker,
                    ownerClientFactory, verificationToken, cancellationToken)
                .ConfigureAwait(false);
            await lifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            {
                MutationStarted = false,
                Phase = LifecycleOperationPhase.Prepared
            }, cancellationToken).ConfigureAwait(false);
        }
        SafeFileSystem.WriteTextAtomic(previousSettingPath, originalSetting);
        SafeFileSystem.WriteTextAtomic(previousManifestPath, await File.ReadAllTextAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false));
        SafeFileSystem.WriteTextAtomic(previousResultPath, await File.ReadAllTextAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false));
        operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Prepared,
            MutationStarted = true
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            var continuity = await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Drained,
                PreMutationContinuity = CameraAgentLifecycleManager.ToBoundary(continuity)
            }, cancellationToken).ConfigureAwait(false);
            SafeFileSystem.WriteTextAtomic(settingPath, candidate.PackageVersion);
            operation = await CameraAgentLifecycleManager.RecordAsync(
                paths, operation with { Phase = LifecycleOperationPhase.Mutating }, cancellationToken).ConfigureAwait(false);
            CameraAgentLifecycleManager.EnsureDaemon(
                manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["up", "--detach", "--force-recreate", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            await VerifyAsync(manifest, installationResult, candidate, compose, paths, docker, ownerClientFactory,
                    verificationToken, cancellationToken)
                .ConfigureAwait(false);
            CameraAgentLifecycleManager.EnsureDaemon(
                manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["restart"], cancellationToken)
                .ConfigureAwait(false);
            await VerifyAsync(manifest, installationResult, candidate, compose, paths, docker, ownerClientFactory,
                    verificationToken, cancellationToken)
                .ConfigureAwait(false);
            var committed = manifest with
            {
                Catalog = candidate,
                PreviousCatalog = manifest.Catalog,
                LastLifecycleOperationId = operation.OperationId,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            var postMutation = await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken)
                .ConfigureAwait(false);
            if (operation.PreMutationContinuity is null || postMutation.CaptureSequence < operation.PreMutationContinuity.CaptureSequence)
                throw new InstallerException("Catalog selection did not preserve the durable capture-sequence boundary.");
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.CandidateVerified,
                PostMutationContinuity = CameraAgentLifecycleManager.ToBoundary(postMutation)
            }, cancellationToken).ConfigureAwait(false);
            CameraAgentLifecycleManager.EnsureDaemon(
                manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await CatalogReferenceStore.PinHistoricalAsync(paths, manifest.Catalog, operation.OperationId, cancellationToken)
                .ConfigureAwait(false);
            await SafeFileSystem.WriteJsonAtomicAsync(paths.ManifestPath, committed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
                .ConfigureAwait(false);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath,
                installationResult with { Catalog = candidate },
                DeploymentJsonContext.Default.InstallationResult,
                cancellationToken).ConfigureAwait(false);
            operation = await CameraAgentLifecycleManager.RecordAsync(
                paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken).ConfigureAwait(false);
            await lifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            await CameraAgentLifecycleManager.CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result(request.Operation, "completed", operation.OperationId, paths, candidate, committed);
        }
        catch (Exception exception)
        {
            if (operation.Phase == LifecycleOperationPhase.Committed)
            {
                throw new InstallerException(
                    "The catalog selection commit succeeded but final resume was not acknowledged; rerun the same command with --resume.",
                    exception);
            }
            using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                SafeFileSystem.WriteTextAtomic(settingPath, originalSetting);
                var snapshot = await CameraAgentLifecycleManager.ReadRecoverySnapshotAsync(
                    paths, operation, previousManifestPath, previousResultPath, recovery.Token).ConfigureAwait(false);
                await CameraAgentLifecycleManager.WriteRecoverySnapshotAsync(
                    paths, snapshot.Manifest, snapshot.Result, recovery.Token).ConfigureAwait(false);
                CameraAgentLifecycleManager.EnsureDaemon(
                    manifest.DockerDaemon, await docker.PreflightAsync(recovery.Token).ConfigureAwait(false));
                await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                    ["up", "--detach", "--force-recreate", "--remove-orphans"], recovery.Token).ConfigureAwait(false);
                await VerifyAsync(manifest, installationResult, manifest.Catalog, compose, paths, docker,
                        ownerClientFactory, verificationToken, recovery.Token)
                    .ConfigureAwait(false);
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
                    $"Catalog selection failed and exact rollback also failed: {Redaction.SafeDiagnostic(recoveryException.Message)}", exception);
            }
            await CameraAgentLifecycleManager.FailAsync(paths, operation, exception, CancellationToken.None).ConfigureAwait(false);
            throw new InstallerException("Catalog selection failed and the prior exact selection was restored.", exception);
        }
    }

    private static async Task VerifyAsync(
        InstanceManifest manifest,
        InstallationResult result,
        CatalogInstallationIdentity catalog,
        ComposeFiles compose,
        InstallationPaths paths,
        DockerClient docker,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string token,
        CancellationToken cancellationToken)
    {
        var owner = ownerClientFactory?.Invoke(result.Url) ?? new OwnerBootstrapClient(result.Url);
        await owner.WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
        await owner.VerifyInstallationAsync(
            token,
            new InstallationVerificationExpectation(
                manifest.InstanceId.ToString("D"), manifest.OwnerEmail, result.OwnerBootstrapState,
                manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
                manifest.DeploymentLocationId, manifest.DeploymentLocationVersion, manifest.DeploymentLocationSha256,
                catalog), cancellationToken).ConfigureAwait(false);
        await docker.VerifyContainerAsync(compose, paths, manifest.Image, manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<LifecycleResult> GarbageCollectAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        DockerClient docker,
        CancellationToken cancellationToken)
    {
        var versionsRoot = Path.Combine(paths.CatalogRoot, "versions");
        if (!Directory.Exists(versionsRoot)) throw new InstallerException("The production catalog has no installed versions.");
        using var catalogLock = request.DryRun
            ? null
            : OperationLock.Acquire(Path.Combine(paths.CatalogRoot, ".catalog.lock"), cancellationToken: cancellationToken);
        var daemon = request.DryRun ? null : await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        Guid? completedOperationId = null;
        if (!request.DryRun)
            completedOperationId = await RecoverGarbageCollectionTombstonesAsync(
                request, paths, versionsRoot, docker, daemon!, cancellationToken).ConfigureAwait(false);
        var candidates = request.CatalogVersion is null
            ? Directory.EnumerateDirectories(versionsRoot).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()
            : [request.CatalogVersion];
        var deleted = new List<string>();
        if (request.Resume)
        {
            if (completedOperationId is null) throw new InstallerException("No matching interrupted catalog garbage-collection operation exists.");
            deleted.Add(request.CatalogVersion!);
        }
        foreach (var version in candidates)
        {
            if (version is null || await IsReferencedAsync(paths, version, cancellationToken).ConfigureAwait(false)) continue;
            var versionRoot = Path.Combine(versionsRoot, version);
            if (!Directory.Exists(versionRoot)) continue;
            ValidateInstalled(paths, await ReadInstalledIdentityAsync(paths, version, cancellationToken).ConfigureAwait(false));
            if (request.DryRun)
            {
                deleted.Add(version);
                continue;
            }
            var inventory = SafeTreeDeletion.CaptureChildInventory(
                versionsRoot, version, NativeLinux.getuid(), NativeLinux.getgid());
            await docker.EnsureNoPathReferencesAsync(versionRoot, cancellationToken, inventory).ConfigureAwait(false);
            var treeIdentity = SafeTreeDeletion.ValidateChild(versionsRoot, version, NativeLinux.getuid(), NativeLinux.getgid());
            var operationId = Guid.NewGuid();
            var evidencePath = Path.Combine(paths.OperationsRoot, $"catalog-gc-{operationId:N}.json");
            var evidence = new CatalogGarbageCollectionEvidence(
                DeploymentSchemaVersions.LifecycleOperation,
                operationId,
                request.ComputeRequestSha256(),
                LocalHostIdentity.ReadSha256(),
                daemon!,
                version,
                await ReadInstalledIdentityAsync(paths, version, cancellationToken).ConfigureAwait(false),
                inventory);
            await WriteGarbageCollectionEvidenceAsync(evidencePath, evidence, cancellationToken).ConfigureAwait(false);
            var tombstone = Path.Combine(versionsRoot, $".gc-{version}-{operationId:N}");
            SafeTreeDeletion.RenameChild(versionsRoot, version, Path.GetFileName(tombstone), treeIdentity);
            try
            {
                var currentDaemon = await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
                if (daemon is not null && (daemon.Id != currentDaemon.Id || daemon.Name != currentDaemon.Name ||
                                           daemon.Architecture != currentDaemon.Architecture))
                    throw new InstallerException("The Docker daemon changed during catalog garbage collection.");
                await docker.EnsureNoPathReferencesAsync(versionRoot, cancellationToken, inventory).ConfigureAwait(false);
                await docker.EnsureNoPathReferencesAsync(tombstone, cancellationToken, inventory).ConfigureAwait(false);
                if (await IsReferencedAsync(paths, version, cancellationToken).ConfigureAwait(false))
                    throw new InstallerException("Catalog garbage collection found a reference after tombstoning.");
                CameraAgentLifecycleManager.EnsureDaemon(
                    daemon!, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
                evidence = evidence with { DeletionCommitted = true };
                await WriteGarbageCollectionEvidenceAsync(evidencePath, evidence, cancellationToken).ConfigureAwait(false);
                SafeTreeDeletion.DeleteChild(
                    versionsRoot, Path.GetFileName(tombstone), NativeLinux.getuid(), NativeLinux.getgid(), treeIdentity);
                CameraAgentLifecycleManager.EnsureDaemon(
                    daemon!, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
                evidence = evidence with { CompletedUtc = DateTimeOffset.UtcNow };
                await WriteGarbageCollectionEvidenceAsync(evidencePath, evidence, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                throw new InstallerException("Catalog garbage collection stopped at a quarantined tombstone; rerun the exact version with --resume.", exception);
            }
            deleted.Add(version);
            completedOperationId = operationId;
        }
        return new LifecycleResult(
            DeploymentSchemaVersions.LifecycleOperation,
            request.Operation,
            request.DryRun ? $"planned:{string.Join(',', deleted)}" : $"completed:{string.Join(',', deleted)}",
            request.DryRun ? null : completedOperationId,
            null,
            null,
            paths.ProductRoot,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            [paths.CatalogRoot, paths.OperationsRoot],
            null,
            DateTimeOffset.UtcNow);
    }

    private static async Task<Guid?> RecoverGarbageCollectionTombstonesAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        string versionsRoot,
        DockerClient docker,
        DockerDaemonIdentity expectedDaemon,
        CancellationToken cancellationToken)
    {
        Guid? recoveredOperationId = null;
        foreach (var tombstone in Directory.EnumerateDirectories(versionsRoot, ".gc-*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(tombstone);
            if (name.Length <= 37 || name[^33] != '-' || !Guid.TryParseExact(name[^32..], "N", out _))
                throw new InstallerException("Catalog garbage collection found an unsupported tombstone.");
            var version = name[4..^33];
            if (!request.Resume || request.CatalogVersion != version)
                throw new InstallerException($"Catalog garbage collection has an interrupted '{version}' tombstone; resume that exact version.");
            var original = Path.Combine(versionsRoot, version);
            if (Directory.Exists(original)) throw new InstallerException("Catalog garbage collection found colliding original and tombstone versions.");
            var operationId = Guid.ParseExact(name[^32..], "N");
            var evidencePath = Path.Combine(paths.OperationsRoot, $"catalog-gc-{operationId:N}.json");
            var evidence = await ReadGarbageCollectionEvidenceAsync(
                evidencePath, cancellationToken).ConfigureAwait(false);
            if (evidence.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation || evidence.OperationId != operationId ||
                evidence.RequestSha256 != request.ComputeRequestSha256() || evidence.PackageVersion != version ||
                evidence.Catalog.PackageVersion != version || evidence.Tree.Count == 0)
                throw new InstallerException("Catalog garbage collection tombstone evidence does not match its version.");
            if (evidence.HostIdentitySha256 != LocalHostIdentity.ReadSha256())
                throw new InstallerException("Catalog garbage collection evidence belongs to a different host.");
            CameraAgentLifecycleManager.EnsureDaemon(evidence.DockerDaemon, expectedDaemon);
            CameraAgentLifecycleManager.EnsureDaemon(
                expectedDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.EnsureNoPathReferencesAsync(original, cancellationToken, evidence.Tree).ConfigureAwait(false);
            await docker.EnsureNoPathReferencesAsync(tombstone, cancellationToken, evidence.Tree).ConfigureAwait(false);
            if (await IsReferencedAsync(paths, version, cancellationToken).ConfigureAwait(false))
                throw new InstallerException("Catalog garbage collection recovery found a retained reference.");
            var treeIdentity = SafeTreeDeletion.ValidateRemainingChild(
                versionsRoot, name, NativeLinux.getuid(), NativeLinux.getgid(), evidence.Tree);
            CameraAgentLifecycleManager.EnsureDaemon(
                evidence.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.EnsureNoPathReferencesAsync(original, cancellationToken, evidence.Tree).ConfigureAwait(false);
            await docker.EnsureNoPathReferencesAsync(tombstone, cancellationToken, evidence.Tree).ConfigureAwait(false);
            evidence = evidence with { DeletionCommitted = true };
            await WriteGarbageCollectionEvidenceAsync(evidencePath, evidence, cancellationToken).ConfigureAwait(false);
            SafeTreeDeletion.DeleteChild(
                versionsRoot, name, NativeLinux.getuid(), NativeLinux.getgid(), treeIdentity);
            CameraAgentLifecycleManager.EnsureDaemon(
                evidence.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            evidence = evidence with { CompletedUtc = DateTimeOffset.UtcNow };
            await WriteGarbageCollectionEvidenceAsync(evidencePath, evidence, cancellationToken).ConfigureAwait(false);
            recoveredOperationId = operationId;
        }
        if (recoveredOperationId is null && request.Resume)
        {
            var pending = new List<(string Path, CatalogGarbageCollectionEvidence Evidence)>();
            foreach (var path in Directory.EnumerateFiles(paths.OperationsRoot, "catalog-gc-*.json", SearchOption.TopDirectoryOnly))
            {
                var evidence = await ReadGarbageCollectionEvidenceAsync(path, cancellationToken).ConfigureAwait(false);
                if (evidence.RequestSha256 == request.ComputeRequestSha256() && evidence.PackageVersion == request.CatalogVersion &&
                    evidence.CompletedUtc is null)
                    pending.Add((path, evidence));
            }
            if (pending.Count > 1) throw new InstallerException("Catalog garbage collection found ambiguous pending evidence.");
            if (pending.Count == 1)
            {
                var (path, evidence) = pending[0];
                if (evidence.HostIdentitySha256 != LocalHostIdentity.ReadSha256())
                    throw new InstallerException("Catalog garbage collection evidence belongs to a different host.");
                CameraAgentLifecycleManager.EnsureDaemon(
                    evidence.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
                if (!evidence.DeletionCommitted)
                {
                    var original = Path.Combine(versionsRoot, evidence.PackageVersion);
                    if (!Directory.Exists(original))
                        throw new InstallerException("The catalog root disappeared before deletion was committed.");
                    var currentInventory = SafeTreeDeletion.CaptureChildInventory(
                        versionsRoot, evidence.PackageVersion, NativeLinux.getuid(), NativeLinux.getgid());
                    if (currentInventory.Count != evidence.Tree.Count)
                        throw new InstallerException("The catalog root changed before tombstoning.");
                    var treeIdentity = SafeTreeDeletion.ValidateRemainingChild(
                        versionsRoot, evidence.PackageVersion, NativeLinux.getuid(), NativeLinux.getgid(), evidence.Tree);
                    await docker.EnsureNoPathReferencesAsync(original, cancellationToken, evidence.Tree).ConfigureAwait(false);
                    if (await IsReferencedAsync(paths, evidence.PackageVersion, cancellationToken).ConfigureAwait(false))
                        throw new InstallerException("Catalog garbage collection recovery found a retained reference.");
                    var tombstoneName = $".gc-{evidence.PackageVersion}-{evidence.OperationId:N}";
                    SafeTreeDeletion.RenameChild(versionsRoot, evidence.PackageVersion, tombstoneName, treeIdentity);
                    var tombstone = Path.Combine(versionsRoot, tombstoneName);
                    CameraAgentLifecycleManager.EnsureDaemon(
                        evidence.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
                    await docker.EnsureNoPathReferencesAsync(original, cancellationToken, evidence.Tree).ConfigureAwait(false);
                    await docker.EnsureNoPathReferencesAsync(tombstone, cancellationToken, evidence.Tree).ConfigureAwait(false);
                    evidence = evidence with { DeletionCommitted = true };
                    await WriteGarbageCollectionEvidenceAsync(path, evidence, cancellationToken).ConfigureAwait(false);
                    SafeTreeDeletion.DeleteChild(
                        versionsRoot, tombstoneName, NativeLinux.getuid(), NativeLinux.getgid(), treeIdentity);
                    CameraAgentLifecycleManager.EnsureDaemon(
                        evidence.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
                }
                await WriteGarbageCollectionEvidenceAsync(
                    path, evidence with { CompletedUtc = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
                recoveredOperationId = evidence.OperationId;
            }
        }
        return recoveredOperationId;
    }

    private static async Task<bool> IsReferencedAsync(
        InstallationPaths paths,
        string version,
        CancellationToken cancellationToken)
    {
        foreach (var pointerName in new[] { "current", "previous" })
        {
            var pointer = new DirectoryInfo(Path.Combine(paths.CatalogRoot, pointerName));
            var target = pointer.LinkTarget;
            if (target is null)
            {
                if (pointerName == "current")
                    throw new InstallerException("Catalog garbage collection requires one valid current selection pointer.");
                if (pointer.Exists || File.Exists(pointer.FullName))
                    throw new InstallerException("Catalog garbage collection found a non-symbolic selection pointer.");
                continue;
            }
            var targetVersion = target.StartsWith("versions/", StringComparison.Ordinal) ? target["versions/".Length..] : string.Empty;
            if (!System.Text.RegularExpressions.Regex.IsMatch(
                    targetVersion, "^hyg-v4\\.2-p3-s2-r[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InstallerException("Catalog garbage collection found a malformed selection pointer.");
            if (target == $"versions/{version}") return true;
        }
        var history = Path.Combine(paths.CatalogReferencesRoot, version, "historical");
        if (Directory.Exists(history) && Directory.EnumerateFiles(history, "*.json", SearchOption.AllDirectories).Any()) return true;
        var logicHostsRoot = Path.Combine(paths.ProductRoot, "logichosts");
        if (Directory.Exists(logicHostsRoot) && Directory.EnumerateFileSystemEntries(logicHostsRoot).Any())
        {
            throw new InstallerException("Catalog garbage collection cannot prove zero references while an unsupported LogicHost manifest exists.");
        }
        var agentsRoot = Path.Combine(paths.ProductRoot, "cameraagents");
        if (Directory.Exists(agentsRoot))
        {
            foreach (var instanceRoot in Directory.EnumerateDirectories(agentsRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (!Guid.TryParseExact(Path.GetFileName(instanceRoot), "D", out var directoryInstanceId))
                    throw new InstallerException("Catalog garbage collection found an unsupported CameraAgent root.");
                var manifestPath = Path.Combine(instanceRoot, "instance-manifest.json");
                if (!File.Exists(manifestPath))
                    throw new InstallerException("Catalog garbage collection cannot prove zero references for a CameraAgent root without a manifest.");
                try
                {
                    var manifest = await ReadInstanceManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                    if (manifest.InstanceId != directoryInstanceId || manifest.ProductRoot != paths.ProductRoot ||
                        manifest.ConfigRoot != Path.Combine(instanceRoot, "config") || manifest.StateRoot != Path.Combine(instanceRoot, "state"))
                        throw new InstallerException("Catalog garbage collection found a path-uncorrelated CameraAgent manifest.");
                    if (manifest.Catalog.PackageVersion == version || manifest.PreviousCatalog?.PackageVersion == version) return true;
                    var selectedPath = Path.Combine(manifest.ConfigRoot, "secrets", "Catalog__RequiredPackageVersion");
                    if (File.Exists(selectedPath))
                    {
                        using var selected = SafeFileSystem.OpenOwnerFileRead(selectedPath);
                        using var reader = new StreamReader(selected);
                        if ((await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n') == version) return true;
                    }
                }
                catch (Exception exception) when (exception is JsonException or InstallerException)
                {
                    throw new InstallerException("Catalog garbage collection cannot prove zero references because an instance manifest is unsupported.", exception);
                }
            }
        }
        var backupsRoot = Path.Combine(paths.OperationsRoot, "backups");
        if (Directory.Exists(backupsRoot))
        {
            foreach (var path in Directory.EnumerateFiles(backupsRoot, "backup-manifest.json", SearchOption.AllDirectories))
            {
                await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
                var backup = await CameraAgentLifecycleManager.DeserializeRetainedAsync(
                    stream, DeploymentJsonContext.Default.InstanceBackupManifest,
                    "backup manifest", cancellationToken).ConfigureAwait(false);
                if (backup.Catalog.PackageVersion == version) return true;
            }
        }
        foreach (var path in Directory.EnumerateFiles(paths.OperationsRoot, "cameraagent-*.lifecycle.json", SearchOption.TopDirectoryOnly))
        {
            var operation = await CameraAgentLifecycleManager.ReadOperationAsync(path, cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerException("A retained lifecycle operation is empty.");
            if (operation.Status != InstallationStatus.Completed &&
                (operation.OriginalCatalog?.PackageVersion == version || operation.CandidateCatalog?.PackageVersion == version))
            {
                return true;
            }
        }
        return false;
    }

    private static async Task<InstanceManifest> ReadInstanceManifestAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var manifest = await CameraAgentLifecycleManager.ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
        if (manifest.SchemaVersion != DeploymentSchemaVersions.InstanceManifest || manifest.Product != "HVO.SkyMonitor" ||
            manifest.ComponentSchemaVersion != "cameraagent-install-v1" || manifest.Component != DeploymentComponent.CameraAgent ||
            manifest.InstanceId == Guid.Empty || manifest.InstallationId == Guid.Empty || manifest.ApplicationIdentity == Guid.Empty)
            throw new InstallerException("An instance manifest is invalid or unsupported.");
        return manifest;
    }

    private static void ValidateInstalled(InstallationPaths paths, CatalogInstallationIdentity identity)
    {
        CameraAgentLifecycleManager.ValidateCanonicalCatalog(paths, identity);
        var result = CatalogSnapshotResolver.Resolve(ProductionCatalog.ResolverOptions(paths.CatalogRoot, identity.PackageVersion));
        var actual = ProductionCatalog.ToIdentity(result, paths.CatalogRoot);
        if (actual.CatalogId != identity.CatalogId || actual.PackageVersion != identity.PackageVersion ||
            actual.SchemaVersion != identity.SchemaVersion || actual.PreprocessingVersion != identity.PreprocessingVersion ||
            actual.ManifestSha256 != identity.ManifestSha256 || actual.DatabaseSha256 != identity.DatabaseSha256 ||
            actual.DatabaseLength != identity.DatabaseLength || actual.RowCount != identity.RowCount ||
            actual.InstallRoot != identity.InstallRoot || actual.Source != identity.Source)
        {
            throw new InstallerException("The installed catalog version differs from its retained identity.");
        }
    }

    private static async Task<AcquiredCatalog> AcquireAsync(LifecycleRequest request, CancellationToken cancellationToken)
    {
        var synthetic = new InstallRequest
        {
            FriendlyName = "catalog-lifecycle",
            OwnerEmail = "lifecycle@localhost.invalid",
            ProductRoot = request.ProductRoot,
            CatalogBundle = request.CatalogBundle,
            CatalogManifest = request.CatalogManifest,
            CatalogIndex = request.CatalogIndex,
            CatalogVersion = request.CatalogVersion,
            AssetBaseUrl = request.AssetBaseUrl,
            Channel = request.Channel,
            ImageReference = $"sha256:{new string('0', 64)}",
            NoDownload = request.NoDownload
        };
        synthetic.Validate();
        using var acquirer = new DistributionCatalogAcquirer();
        return await acquirer.AcquireAsync(synthetic, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteInstalledIdentityAsync(
        InstallationPaths paths,
        CatalogInstallationIdentity identity,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(paths.CatalogReferencesRoot, identity.PackageVersion);
        SafeFileSystem.CreateOwnerDirectory(paths.CatalogReferencesRoot);
        SafeFileSystem.CreateOwnerDirectory(root);
        await SafeFileSystem.WriteJsonAtomicAsync(
            Path.Combine(root, "installed.json"), identity, DeploymentJsonContext.Default.CatalogInstallationIdentity,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CatalogInstallOperationEvidence?> ReadCatalogInstallOperationAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        var value = await CameraAgentLifecycleManager.DeserializeRetainedAsync(
            stream, DeploymentJsonContext.Default.CatalogInstallOperationEvidence,
            "catalog install operation", cancellationToken).ConfigureAwait(false);
        if (value.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation || value.OperationId == Guid.Empty ||
            value.RequestSha256.Length != 64 || value.StartedUtc == default || value.UpdatedUtc < value.StartedUtc ||
            value.Phase is not ("planned" or "acquired" or "installed" or "completed") || !Enum.IsDefined(value.Status) ||
            value.Phase is "acquired" or "installed" or "completed" && value.AcquiredBundleSha256?.Length != 64 ||
            value.Phase is "installed" or "completed" && value.InstalledCatalog is null ||
            value.Status == InstallationStatus.Completed && value.Phase != "completed")
            throw new InstallerException("The catalog install operation is invalid or unsupported.");
        return value;
    }

    private static Task WriteCatalogInstallOperationAsync(
        string path,
        CatalogInstallOperationEvidence operation,
        CancellationToken cancellationToken)
        => SafeFileSystem.WriteJsonAtomicAsync(
            path, operation, DeploymentJsonContext.Default.CatalogInstallOperationEvidence, cancellationToken);

    private static async Task<string> ComputeBundleSha256Async(string bundlePath, CancellationToken cancellationToken)
    {
        using var aggregate = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var path in Directory.EnumerateFiles(bundlePath, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            await using var stream = SafeFileSystem.OpenRegularFileRead(path);
            var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            aggregate.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(path)));
            aggregate.AppendData([0]);
            aggregate.AppendData(BitConverter.GetBytes(stream.Length));
            aggregate.AppendData(hash);
        }
        return Convert.ToHexStringLower(aggregate.GetHashAndReset());
    }

    private static async Task<CatalogInstallationIdentity> ReadInstalledIdentityAsync(
        InstallationPaths paths,
        string version,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.CatalogReferencesRoot, version, "installed.json");
        if (!File.Exists(path))
        {
            var result = CatalogSnapshotResolver.Resolve(ProductionCatalog.ResolverOptions(paths.CatalogRoot, version));
            return ProductionCatalog.ToIdentity(result, paths.CatalogRoot);
        }
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        var identity = await CameraAgentLifecycleManager.DeserializeRetainedAsync(
            stream, DeploymentJsonContext.Default.CatalogInstallationIdentity,
            "installed catalog identity", cancellationToken).ConfigureAwait(false);
        CameraAgentLifecycleManager.ValidateCanonicalCatalog(paths, identity);
        return identity;
    }

    private static async Task<CatalogGarbageCollectionEvidence> ReadGarbageCollectionEvidenceAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        return await CameraAgentLifecycleManager.DeserializeRetainedAsync(
            stream, DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence,
            "catalog garbage-collection evidence", cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteGarbageCollectionEvidenceAsync(
        string path,
        CatalogGarbageCollectionEvidence evidence,
        CancellationToken cancellationToken)
        => SafeFileSystem.WriteJsonAtomicAsync(
            path, evidence, DeploymentJsonContext.Default.CatalogGarbageCollectionEvidence, cancellationToken);

    private static void ValidateSignedIdentity(CatalogInstallationIdentity actual, DistributionCatalogIdentity? signed)
    {
        if (signed is not null &&
            (actual.CatalogId != signed.CatalogId || actual.PackageVersion != signed.PackageVersion ||
             actual.SchemaVersion != signed.SchemaVersion || actual.PreprocessingVersion != signed.PreprocessingVersion ||
             actual.DatabaseSha256 != signed.DatabaseSha256 || actual.DatabaseLength != signed.DatabaseLength ||
             actual.RowCount != signed.RowCount || actual.ManifestSha256 != signed.BundleManifestSha256))
        {
            throw new InstallerException("The installed catalog differs from its signed release identity.");
        }
    }

    private static LifecycleResult Result(
        LifecycleOperationKind? operation,
        string outcome,
        Guid? operationId,
        InstallationPaths paths,
        CatalogInstallationIdentity catalog,
        InstanceManifest? manifest)
        => new(
            DeploymentSchemaVersions.LifecycleOperation,
            operation,
            outcome,
            operationId,
            manifest?.InstanceId,
            manifest?.LifecycleCondition,
            paths.ProductRoot,
            manifest is null ? null : paths.InstanceRoot,
            manifest?.Image,
            manifest?.PreviousImage,
            catalog,
            manifest?.PreviousCatalog,
            manifest?.DockerDaemon,
            manifest is null ? null : true,
            manifest is null ? null : true,
            [paths.CatalogRoot, paths.OperationsRoot],
            null,
            DateTimeOffset.UtcNow);
}
