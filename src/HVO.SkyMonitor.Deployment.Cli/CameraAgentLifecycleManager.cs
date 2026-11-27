using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HVO.SkyMonitor.AgentCore;
using HVO.SkyMonitor.Deployment.Contracts;
using Microsoft.Win32.SafeHandles;

namespace HVO.SkyMonitor.Deployment;

internal sealed partial class CameraAgentLifecycleManager
{
    private static readonly TimeSpan ImageChangeReadinessTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ImageRecoveryPreparationTimeout = TimeSpan.FromMinutes(4);
    private static readonly TimeSpan ImageRecoverySettlementTimeout = TimeSpan.FromMinutes(11);
    internal static readonly AsyncLocal<IHostEnvironment?> UpgradeStorageHostOverride = new();

    public static Task<LifecycleResult> ExecuteAsync(LifecycleRequest request, CancellationToken cancellationToken)
        => ExecuteAsync(request, new ProcessRunner(), null, null, NativeLinux.getuid(), NativeLinux.getgid(), cancellationToken);

    /// <summary>
    /// Whether this asynchronous flow may read a Silo-layout instance manifest (#1966). Only the operations the first Silo
    /// release supports for such an instance set it: uninstall, purge and status. Every other reader refuses the
    /// manifest before mutation, as every installer released before #1966 does through its component schema.
    /// </summary>
    private static readonly AsyncLocal<bool> SiloLayoutAdmitted = new();

    internal const string SiloLayoutUnsupported =
        "This instance uses the Silo storage layout; the first Silo release supports only status, first-resume, " +
        "silo-verify, uninstall and purge for it. Nothing was changed.";

    internal static bool AdmitsSiloLayout(LifecycleOperationKind? operation)
        => operation is null or LifecycleOperationKind.Uninstall or LifecycleOperationKind.Purge;

    internal static async Task<LifecycleResult> ExecuteAsync(
        LifecycleRequest request,
        IProcessRunner processRunner,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        uint uid,
        uint gid,
        CancellationToken cancellationToken,
        TextWriter? output = null,
        TextWriter? error = null,
        Func<string?, ReleaseImageSource?, string, CancellationToken, Task<AcquiredImage?>>? acquireReleaseImage = null)
    {
        SiloLayoutAdmitted.Value = AdmitsSiloLayout(request.Operation);
        var result = await ExecuteCoreAsync(request, processRunner, lifecycleClientFactory, ownerClientFactory,
            uid, gid, output, error, acquireReleaseImage ?? ReleaseAcquirer.AcquireImageAsync, cancellationToken).ConfigureAwait(false);
        if (result.Plan is null || request.InstanceId is not { } id ||
            request.Operation is not (LifecycleOperationKind.Upgrade or
                LifecycleOperationKind.Uninstall or LifecycleOperationKind.Reinstall or LifecycleOperationKind.Purge)) return result;
        var paths = InstallationPaths.Create(request.ProductRoot, id, ProductionCatalog.DefaultCatalogId);
        var manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var programs = await PathPrograms.PlanAsync(paths, request.ProductionProductRoot, request.Release, manifest.Image.ImageId,
            request.Operation, cancellationToken).ConfigureAwait(false);
        return programs.Count == 0 ? result : result with { Plan = DeploymentPlan.Of([.. result.Plan.Items, .. programs]) };
    }

    private static async Task<LifecycleResult> ExecuteCoreAsync(
        LifecycleRequest request, IProcessRunner processRunner,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory, Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        uint uid, uint gid, TextWriter? output, TextWriter? error,
        Func<string?, ReleaseImageSource?, string, CancellationToken, Task<AcquiredImage?>> acquireReleaseImage,
        CancellationToken cancellationToken)
    {
        request.Validate();
        output ??= Console.Out;
        error ??= Console.Error;
        // A backup or restore is the instance's service account's alone, and runs as nothing else (#1353 PR-1369-R2).
        if (request.Operation is LifecycleOperationKind.Backup or LifecycleOperationKind.Restore)
        {
            EnsureServiceAccount(request, uid, gid);
        }
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
        // Instance paths start from the default catalog and are re-bound to the instance's selected catalog once its
        // retained manifest has been read; only the catalog root and reference root depend on the selection.
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.DefaultCatalogId);
        // A release upgrade reads and verifies its release before the instance is touched, so an unsupported
        // architecture, a missing platform, or a tampered file fails while the running instance is untouched. The
        // platform is selected for the architecture of the Docker daemon the instance was installed against, which the
        // manifest records and which the candidate is later required to match, not for the architecture of the process
        // running this CLI: a remote or cross-architecture daemon is given the image it can run, and a release that
        // does not publish that architecture is refused before anything is loaded or pulled. The same verified
        // release.json supplies the upgrade notes and the older-target refusal, so they always describe the candidate.
        // The candidate keeps the name the upgrade orchestration gives it.
        AcquiredImage? signedImage = null;
        if (request.Operation == LifecycleOperationKind.Upgrade && request.Release is not null)
        {
            var daemonArchitecture = (await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false))
                .DockerDaemon.Architecture;
            signedImage = await acquireReleaseImage(
                    request.Release, request.ImageSource, daemonArchitecture, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InstallerException("The release did not select a CameraAgent image.");
            request = request with
            {
                ImageReference = signedImage.ImageReference,
                ImageArchive = signedImage.ArchivePath,
                ImageArchiveSha256 = signedImage.ArchiveSha256,
                TargetRelease = signedImage.Release
            };
        }
        if (request.Operation is not null && uid == 0)
            throw InstallerException.Refused("Run lifecycle operations as the Docker-capable runtime user, not as root.");
        if (request.Operation == LifecycleOperationKind.Rollback && !request.Resume)
        {
            var usesSiloLayout = await DeclaresSiloLayoutAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            throw InstallerException.Refused(usesSiloLayout ? SiloLayoutUnsupported : ManualRollbackGuidance);
        }
        // A restore's target may be absent or half replaced, so it starts from the archive, not the instance manifest.
        if (request.Operation == LifecycleOperationKind.Restore)
        {
            return await RestoreAsync(request, processRunner, uid, gid, error, cancellationToken).ConfigureAwait(false);
        }
        if (request.Operation == LifecycleOperationKind.Purge && !Directory.Exists(paths.InstanceRoot))
        {
            if (!request.Resume) throw InstallerException.Refused("The instance root is absent; use --resume only for a retained purge operation.");
            // Resume only ever continues retained state: it never creates the operations root or any other path.
            SafeFileSystem.EnsureSafeExistingAncestors(paths.OperationsRoot);
            if (!Directory.Exists(paths.OperationsRoot)) throw new InstallerException("No matching interrupted purge operation exists.");
            using var interruptedPurgeLock = OperationLock.Acquire(
                Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
            await EnsureControlSettledAsync(paths, cancellationToken).ConfigureAwait(false);
            return await ResumeInterruptedPurgeAsync(
                    request, paths, new DockerClient(processRunner), processRunner, cancellationToken)
                .ConfigureAwait(false);
        }
        var manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (request.Operation == LifecycleOperationKind.Backup)
        {
            EnsureRecordedOwner(request, manifest.RuntimeUid, manifest.RuntimeGid, uid, gid, $"Instance {instanceId:D} records");
        }
        paths = paths.WithCatalog(manifest.Catalog.CatalogId);
        if (request.Operation == LifecycleOperationKind.Upgrade && request.Release is not null && !request.RestoreOnly)
            _ = await PathPrograms.PlanAsync(paths, request.ProductionProductRoot, request.Release, string.Empty,
                request.Operation, cancellationToken, request.LeavePaused ? signedImage?.VerifiedRelease : null).ConfigureAwait(false);
        if (request.Operation is LifecycleOperationKind.Uninstall or LifecycleOperationKind.Purge)
            _ = await PathPrograms.PlanAsync(paths, request.ProductionProductRoot, null, manifest.Image.ImageId,
                request.Operation, cancellationToken).ConfigureAwait(false);
        var result = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        var retainedOperation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        EnsureLegacyRollbackSettlement(request, manifest, retainedOperation);
        if (request.Operation == LifecycleOperationKind.Rollback)
            await PathPrograms.VerifyRollbackAsync(paths, request.ProductionProductRoot, manifest.Image.ImageId, cancellationToken).ConfigureAwait(false);
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
        await ValidateDeepSkySelectionSettingAsync(paths, manifest.DeepSkyCatalog, retainedOperation, cancellationToken).ConfigureAwait(false);
        if (RequiresInstallationVerification(request.Operation))
        {
            installationVerificationToken = await ReadInstallationVerificationTokenAsync(paths, manifest, cancellationToken)
                .ConfigureAwait(false);
        }
        var docker = new DockerClient(processRunner);
        var compose = ComposeFrom(paths, manifest, result);
        // An image change, and a backup that restarts the instance, read which limits Docker enforces from the same
        // docker info read (#1357).
        var capabilities = request.Operation is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or LifecycleOperationKind.Backup
            ? await docker.ReadHostCapabilitiesAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var daemon = capabilities?.Daemon ?? await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        EnsureDaemon(manifest.DockerDaemon, daemon);
        if (request.Operation is null)
        {
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            return await ObserveRuntimeAsync(Result(null, "status", null, paths, manifest, daemon, null, null),
                docker, compose, cancellationToken).ConfigureAwait(false);
        }
        // Every refusal that can be decided before the first write is decided here, for the command and its plan alike,
        // so a refusal says truthfully that nothing changed (#1315).
        IReadOnlyList<string> imported = [];
        HostPrerequisiteReport? hostLimits;
        if (!request.RestoreOnly)
        {
            (signedImage, imported, hostLimits) = await AdmitBeforeWritingAsync(
                    request, paths, manifest, compose, docker, capabilities, signedImage, processRunner, uid, gid, error, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            hostLimits = AdmitHostLimits(request, paths, manifest, capabilities!);
        }
        var written = ReadWrittenState(paths);
        try
        {
            var settled = await LockedAsync().ConfigureAwait(false);
            if (!request.DryRun && settled is { Running: true, Healthy: true })
                settled = await ObserveSettledRuntimeAsync(settled, docker, compose, cancellationToken).ConfigureAwait(false);
            return WithHostLimitWarnings(settled, hostLimits);
        }
        catch (InstallerException exception) when (exception.IsRefusal && !request.DryRun)
        {
            // Once this run has written, a lock file or the journal, a gate that decides not to go ahead can no longer say
            // nothing changed: it is a failure, journaled where an operation began (#1315).
            if (ReadWrittenState(paths) != written) throw new InstallerException(exception.Message, exception);
            // Nothing is written yet, so the refusal still says nothing changed once an image this run pulled is removed
            // again, as its pull's own refusal would remove it (#1315 PR-1355-R3 R3-1). An image Docker keeps makes it a
            // failure that names it (R4-1).
            await docker.UndoImportAsync(imported, exception).ConfigureAwait(false);
            throw;
        }

        async Task<LifecycleResult> LockedAsync()
        {
            // A plan creates nothing, not even the operations directory or a lock file: it takes the locks that exist, so it
            // still reads what a command would, and none where there is none to take (#1315).
            var signedRecovery = request.LeavePaused || request.RestoreOnly && signedImage is not null;
            if (!request.DryRun && !signedRecovery) SafeFileSystem.CreateOwnerDirectory(paths.OperationsRoot);
            using var productLock = AcquireLifecycleLock(Path.Combine(paths.OperationsRoot, "deployment.lock"));
            using var instanceLock = AcquireLifecycleLock(Path.Combine(paths.InstanceRoot, ".deployment.lock"));
            if (request.Operation is not null)
                await EnsureControlSettledAsync(paths, cancellationToken, request.Operation, docker).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            paths = paths.WithCatalog(manifest.Catalog.CatalogId);
            result = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
            retainedOperation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
            EnsureLegacyRollbackSettlement(request, manifest, retainedOperation);
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
            await ValidateDeepSkySelectionSettingAsync(paths, manifest.DeepSkyCatalog, retainedOperation, cancellationToken).ConfigureAwait(false);
            if (RequiresInstallationVerification(request.Operation))
            {
                installationVerificationToken = await ReadInstallationVerificationTokenAsync(paths, manifest, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (request.RestoreOnly)
            {
                try
                {
                    return (await RestoreOnlyAsync(request, paths, manifest, result, retainedOperation, docker,
                        lifecycleClientFactory, ownerClientFactory, lifecycleControlToken!, installationVerificationToken!,
                        signedImage, uid, gid, error, hostLimits, cancellationToken).ConfigureAwait(false)).Result;
                }
                catch (Exception exception) when (exception is IOException or HttpRequestException or JsonException or
                                                    UnauthorizedAccessException or KeyNotFoundException or InvalidOperationException)
                {
                    throw new InstallerException("Restore-only could not verify its retained records or authenticated runtime response; recovery remains incomplete.");
                }
            }
            return request.Operation switch
            {
                LifecycleOperationKind.Upgrade => await ChangeImageAsync(
                    request, paths, manifest, result, compose, docker, lifecycleClientFactory, uid, gid,
                    ownerClientFactory, lifecycleControlToken!, installationVerificationToken!, signedImage,
                    rollback: false, hostLimits, output, error, cancellationToken).ConfigureAwait(false),
                LifecycleOperationKind.Rollback => await ChangeImageAsync(
                    request, paths, manifest, result, compose, docker, lifecycleClientFactory, uid, gid,
                    ownerClientFactory, lifecycleControlToken!, installationVerificationToken!, signedImage: null,
                    rollback: true, hostLimits, output, error, cancellationToken).ConfigureAwait(false),
                LifecycleOperationKind.Reinstall => await ReinstallAsync(
                    request, paths, manifest, result, compose, docker, lifecycleClientFactory, ownerClientFactory,
                    lifecycleControlToken!, installationVerificationToken!, daemon, cancellationToken)
                    .ConfigureAwait(false),
                LifecycleOperationKind.Uninstall => await UninstallAsync(
                    request, paths, manifest, result, compose, docker, lifecycleClientFactory, lifecycleControlToken!, daemon,
                    cancellationToken)
                    .ConfigureAwait(false),
                LifecycleOperationKind.Backup => await BackupAsync(
                    request, paths, manifest, result, compose, docker, lifecycleClientFactory, ownerClientFactory,
                    lifecycleControlToken!, installationVerificationToken!, daemon, hostLimits, uid, gid, error, cancellationToken)
                    .ConfigureAwait(false),
                LifecycleOperationKind.Purge => await PurgeAsync(
                    request, paths, manifest, compose, docker, daemon, processRunner, cancellationToken).ConfigureAwait(false),
                LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback =>
                    await CatalogLifecycleManager.SelectAsync(
                        request, paths, manifest, result, compose, docker, lifecycleClientFactory, ownerClientFactory,
                        lifecycleControlToken!, daemon, installationVerificationToken!, cancellationToken)
                        .ConfigureAwait(false),
                _ => throw new InstallUsageException("The lifecycle operation is unsupported for a CameraAgent instance.")
            };

            OperationLock? AcquireLifecycleLock(string path)
            {
                if (signedRecovery)
                    return OperationLock.AcquireExisting(path, cancellationToken: cancellationToken)
                        ?? throw InstallerException.Refused("Signed restore-only requires the original deployment locks.");
                return request.DryRun
                    ? OperationLock.AcquireExisting(path, cancellationToken: cancellationToken)
                    : OperationLock.Acquire(path, cancellationToken: cancellationToken);
            }
        }
    }

    internal const string ManualRollbackGuidance =
        "CameraAgent checkpoint recovery ends at upgrade commit. Preserve the current captures and records. " +
        "For a committed upgrade, use a supported forward repair or the explicit backup and restore procedure. " +
        "For a pending upgrade, see 'Restore Only After an Interrupted Upgrade' in the deployment installer runbook.";

    private static async Task<bool> DeclaresSiloLayoutAsync(string manifestPath, CancellationToken cancellationToken)
    {
        // A fresh rollback always refuses. Read only the layout declaration to select guidance even when other
        // retained records are unavailable.
        try
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(manifestPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("componentSchemaVersion", out var schema) &&
                   schema.ValueKind == JsonValueKind.String && schema.ValueEquals(SiloRuntimeProfile.ComponentSchemaVersion);
        }
        catch (Exception exception) when (exception is InstallerException or IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void EnsureLegacyRollbackSettlement(
        LifecycleRequest request, InstanceManifest manifest, LifecycleOperationState? operation)
    {
        if (request.Operation != LifecycleOperationKind.Rollback) return;
        if (request.Resume && operation is
            {
                Kind: LifecycleOperationKind.Rollback, Phase: LifecycleOperationPhase.Committed,
                Status: not InstallationStatus.Completed, MutationStarted: true, CandidateImage: not null
            } && operation.InstanceId == manifest.InstanceId && operation.OperationId == manifest.LastLifecycleOperationId &&
            operation.CandidateImage == manifest.Image && operation.RequestSha256 == request.ComputeRequestSha256()) return;

        var guidance = operation is { Kind: LifecycleOperationKind.Rollback, MutationStarted: true }
            ? "The legacy rollback requires transaction-aware recovery. Keep the journals and stores intact and contact support."
            : ManualRollbackGuidance;
        throw InstallerException.Refused(guidance);
    }

    private static async Task<(LifecycleResult Result, string VerifiedOwner)> RestoreOnlyAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        LifecycleOperationState? operation,
        DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string lifecycleControlToken,
        string verificationToken,
        AcquiredImage? signedImage,
        uint uid,
        uint gid,
        TextWriter error,
        HostPrerequisiteReport? hostLimits,
        CancellationToken cancellationToken)
    {
        if (operation is null || operation.Kind != LifecycleOperationKind.Upgrade ||
            operation.OperationId != request.RecoveryOperationId || operation.InstanceId != manifest.InstanceId ||
            operation.RequestSha256 != request.ComputeRequestSha256())
            throw new InstallerException("Restore-only requires the exact retained upgrade operation and original request.");
        if (operation.SchemaVersion == LeavePausedJournalVersion && !request.LeavePaused)
            throw InstallerException.Refused("The retained upgrade requires the original --resume --leave-paused request.");
        if (operation.Status == InstallationStatus.Completed ||
            operation.Phase is LifecycleOperationPhase.Committed or LifecycleOperationPhase.Completed ||
            manifest.LastLifecycleOperationId == operation.OperationId ||
            manifest.Image != operation.OriginalImage || installationResult.Image != operation.OriginalImage)
            throw new InstallerException("Restore-only cannot undo a committed or partially committed candidate.");
        if ((!operation.MutationStarted && !IsRestored(operation)) || operation.CandidateImage is null ||
            operation.PreMutationContinuity is not { CaptureState: "Paused", CaptureVersion: >= 0 } boundary ||
            manifest.LifecycleCondition != InstanceLifecycleCondition.Installed ||
            manifest.RuntimeUid != uid || manifest.RuntimeGid != gid)
            throw new InstallerException("Restore-only requires an owned interrupted upgrade with a recorded durable pause.");
        var candidate = operation.CandidateImage;
        if (signedImage is null && (candidate.ImmutableReference != request.ImageReference ||
            request.ImageReference!.StartsWith("sha256:", StringComparison.Ordinal) && candidate.ImageId != request.ImageReference ||
            candidate.ArchiveSha256 != request.ImageArchiveSha256 ||
            candidate.Source != (request.ImageArchive is null ? "registry" : "archive") ||
            candidate.Architecture != manifest.Image.Architecture || candidate.Architecture != manifest.DockerDaemon.Architecture))
            throw new InstallerException("Restore-only candidate identity does not match the original request and instance platform.");
        if (!IsValidRecoveryBoundary(boundary) || boundary.CaptureVersion == long.MaxValue)
            throw new InstallerException("Restore-only requires a valid retained pause boundary.");
        // An upgrade that recorded a database snapshot is recovered onto it. When the snapshot is not yet restored, the
        // original image's boundaries are checked against the authenticated snapshot, not against what the
        // interrupted candidate left in the live stores, and the snapshot is put back before the original starts.
        var pendingSnapshot = UnrestoredSnapshot(operation);
        CameraAgentStateDatabaseView? recoveryDatabases = null;
        Guid? signedPauseCommandId = null;
        RawCaptureProgress? signedCheckpointCapture = null;
        if (signedImage is not null)
        {
            var signedProof = await VerifySignedRestoreOnlyAsync(paths, manifest, operation, candidate, signedImage, boundary, cancellationToken)
                .ConfigureAwait(false);
            signedPauseCommandId = signedProof.PauseCommandId;
            signedCheckpointCapture = signedProof.Capture;
            if (request.LeavePaused)
            {
                EnsureFleetCheckpointAbsent(signedProof.Checkpoint);
                EnsureFleetStoresAbsent(paths);
            }
        }
        else if (pendingSnapshot is not null)
        {
            var pendingManifest = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, pendingSnapshot, cancellationToken)
                .ConfigureAwait(false);
            recoveryDatabases = CameraAgentStateSnapshot.PreflightView(paths, pendingSnapshot, pendingManifest);
        }

        var operationRoot = Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        var roots = new[] { paths.InstanceRoot, paths.ConfigRoot, paths.StateRoot, paths.DeploymentStateRoot, operationRoot };
        var rootIdentities = roots.Select(path =>
        {
            SafeFileSystem.EnsureSafeExistingAncestors(path);
            var identity = NativeLinux.GetNodeIdentity(path);
            if (identity.Type != 0x4000 || identity.Uid != uid || identity.Gid != gid ||
                identity.Mode != SafeFileSystem.OwnerDirectoryMode)
                throw new InstallerException("Restore-only recovery roots must retain runtime ownership and owner-only access.");
            return identity;
        }).ToArray();
        var previousManifest = Path.Combine(operationRoot, "previous-instance-manifest.json");
        var previousResult = Path.Combine(operationRoot, "previous-installation-result.json");
        // These are retained operation snapshots, never a partial backup archive. Validate before publishing anything.
        _ = await ReadRecoveryTextAsync(previousManifest, cancellationToken).ConfigureAwait(false);
        _ = await ReadRecoveryTextAsync(previousResult, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadRecoverySnapshotAsync(paths, operation, previousManifest, previousResult, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot.Manifest != manifest || snapshot.Result != installationResult)
            throw new InstallerException("Restore-only requires the unchanged original manifest and installation result.");
        var binding = JsonSerializer.Deserialize(
            await ReadRecoveryTextAsync(paths.ApplicationIdentityPath, cancellationToken).ConfigureAwait(false),
            DeploymentJsonContext.Default.ApplicationIdentityBinding);
        if (binding is not { SchemaVersion: 1, State: "bound" } ||
            binding.ConfiguredIdentity != manifest.ApplicationIdentity || binding.BoundIdentity != installationResult.ApplicationIdentity)
            throw new InstallerException("Restore-only application identity binding does not match the original installation.");

        var compose = ComposeFrom(paths, manifest, installationResult);
        var previousCompose = compose with
        {
            ComposeFile = Path.Combine(operationRoot, "previous-compose.yml"),
            EnvironmentFile = Path.Combine(operationRoot, "previous.env")
        };
        foreach (var (retained, current) in new[]
                 {
                     (previousCompose.ComposeFile, compose.ComposeFile),
                     (previousCompose.EnvironmentFile, compose.EnvironmentFile)
                 })
        {
            if (await ReadRecoveryTextAsync(retained, cancellationToken).ConfigureAwait(false) !=
                await ReadRecoveryTextAsync(current, cancellationToken).ConfigureAwait(false))
                throw new InstallerException("Restore-only requires the exact original Compose recovery files.");
        }
        await ValidateComposeAuthorityAsync(docker, previousCompose, manifest, cancellationToken).ConfigureAwait(false);
        await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
        // Repository digests and Docker image IDs need not be equal. The staged request must bind the resolved ID too.
        var originalEnvironment = await ReadRecoveryTextAsync(previousCompose.EnvironmentFile, cancellationToken).ConfigureAwait(false);
        if (await ReadRecoveryTextAsync(Path.Combine(operationRoot, "candidate.env"), cancellationToken).ConfigureAwait(false) !=
            ReplaceEnvironmentValue(originalEnvironment, "CAMERAAGENT_IMAGE", candidate.ImageId))
            throw new InstallerException("Restore-only staged candidate identity does not match the retained request.");
        if (signedImage is not null)
        {
            var originalCompose = await ReadRecoveryTextAsync(previousCompose.ComposeFile, cancellationToken).ConfigureAwait(false);
            var expectedCandidateCompose = CameraAgentContainerResources.WithLimits(
                originalCompose,
                CameraAgentContainerResources.UpgradeTarget(manifest.Resources),
                manifest.Resources ?? CameraAgentContainerResources.Legacy);
            if (await ReadRecoveryTextAsync(Path.Combine(operationRoot, "candidate-compose.yml"), cancellationToken).ConfigureAwait(false) !=
                expectedCandidateCompose)
                throw new InstallerException("Signed restore-only staged Compose differs from the original upgrade model.");
        }
        var configuration = await ReadRecoveryTextAsync(Path.Combine(paths.ConfigRoot, "camera-module.json"), cancellationToken)
            .ConfigureAwait(false);
        using var configurationDocument = JsonDocument.Parse(configuration);
        if (!CaptureContractJson.ComputeCanonicalJsonSha256(configurationDocument.RootElement)
                .Equals(manifest.ConfigurationSha256, StringComparison.OrdinalIgnoreCase))
            throw new InstallerException("Restore-only configuration differs from the original manifest.");
        foreach (var name in new[] { "previous.env", "previous-compose.yml" })
        {
            var current = Path.Combine(paths.DeploymentStateRoot, "rollback", name);
            var retained = Path.Combine(operationRoot, name == "previous.env" ? "prior-rollback.env" : "prior-rollback-compose.yml");
            if (File.Exists(retained))
            {
                if (File.Exists(retained + ".absent") ||
                    await ReadRecoveryTextAsync(retained, cancellationToken).ConfigureAwait(false) !=
                    await ReadRecoveryTextAsync(current, cancellationToken).ConfigureAwait(false))
                    throw new InstallerException("Restore-only rollback history differs from its retained snapshot.");
            }
            else if ((await ReadRecoveryTextAsync(retained + ".absent", cancellationToken).ConfigureAwait(false)).Length != 0 || File.Exists(current))
                throw new InstallerException("Restore-only rollback history differs from its retained absence record.");
        }
        var requirements = CameraAgentStateRequirements.From(manifest.Image);
        if (requirements.IdentityMigration is null || requirements.RawIngressSchema is null || requirements.CatalogManifestVersion is null)
            throw new InstallerException("Restore-only requires declared original image state boundaries.");
        var databases = recoveryDatabases ?? CameraAgentStateDatabaseView.Live(paths);
        var preflight = CameraAgentStatePreflight.Evaluate(paths, manifest.InstanceId, manifest.Image.ImageId, null,
            requirements, manifest.Image.UpgradeCompatibility, uid, gid, manifest.ReplayProfile,
            CameraAgentStateContractPolicy.AllowLegacy, databases: databases);
        if (!preflight.Compatible)
            throw new InstallerException("Restore-only refused incompatible or unreadable original image state; no recovery mutation was performed.");
        if (!File.Exists(databases.IdentityDatabasePath) || !File.Exists(databases.RawIngressDatabasePath) ||
            CameraAgentStatePreflight.ReadRawIngressVersion(databases.RawIngressDatabasePath) != requirements.RawIngressSchema)
            throw new InstallerException("Restore-only requires retained initialized original image state.");
        // Every restore-only guard has passed, so the recovery goes ahead: a limit Docker cannot enforce is warned of and
        // logged before anything changes (#1357).
        await WarnOfUnenforcedLimitsAsync(request, hostLimits, error).ConfigureAwait(false);
        if (signedImage is null)
            await WriteHostLimitsToInstallLogAsync(request, paths, operation.OperationId, hostLimits, cancellationToken).ConfigureAwait(false);

        // Without a database snapshot there is no Compose up, restart or stop: a recovered runtime may carry an
        // operator-approved memory override. An upgrade that recorded a snapshot must not run the original image on
        // the candidate's stores, so restore-only stops whatever runs, puts the snapshot back, and starts the original
        // when it is not already running on the restored stores.
        if (signedImage is null && pendingSnapshot is not null)
        {
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["stop"], cancellationToken).ConfigureAwait(false);
            OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
            operation = await RestoreStateSnapshotAsync(
                paths, manifest, operation, pendingSnapshot, request.Json, error, cancellationToken).ConfigureAwait(false);
        }
        var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        if (signedImage is null && operation.StateSnapshot is not null && (!runtime.Running || runtime.ImageId != manifest.Image.ImageId))
        {
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await StartOnThisHostAsync(docker, compose, hostLimits, operationRoot, cancellationToken).ConfigureAwait(false);
            // The freshly started original must be running and healthy before restore-only inspects it.
            await docker.VerifyContainerAsync(compose, paths, manifest.Image, uid, gid, cancellationToken).ConfigureAwait(false);
            runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        }
        if (!runtime.Running || !runtime.Healthy || runtime.ImageId != manifest.Image.ImageId)
            throw new InstallerException("Restore-only requires the original image running and healthy.");
        await docker.VerifyContainerOwnershipAsync(compose, paths, manifest.Image, uid, gid, cancellationToken).ConfigureAwait(false);
        await docker.VerifyRecoveryContainerAsync(compose, paths, manifest, cancellationToken).ConfigureAwait(false);
        var owner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
        var expectedOwner = operation.ExpectedOwnerBootstrapState ?? installationResult.OwnerBootstrapState;
        var verifiedOwner = await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installationResult,
            manifest.Image, verificationToken, uid, gid, cancellationToken, expectedOwner,
            allowCompletedPasswordReplacement: expectedOwner == "owner-password-change-required",
            allowQuarantinedReadiness: request.AllowQuarantinedReadiness,
            readinessTimeout: ImageChangeReadinessTimeout).ConfigureAwait(false);
        if (request.LeavePaused)
            EnsureFleetStoresAbsent(paths);
        var lifecycle = CreateLifecycleClient(installationResult.Url, lifecycleClientFactory);
        var continuity = await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        var paused = continuity.CaptureState == "Paused" && continuity.CaptureVersion == boundary.CaptureVersion;
        if (signedCheckpointCapture is not null)
        {
            var liveCapture = (await ReadDurableAdmissionAsync(databases.RawIngressDatabasePath, cancellationToken)
                .ConfigureAwait(false)).Progress;
            if (liveCapture.Sequence < signedCheckpointCapture.Sequence || paused && liveCapture != signedCheckpointCapture)
                throw new InstallerException("Signed restore-only raw capture progress no longer matches the retained operation boundary.");
        }
        var resumedOrSuperseded = IsRecoveryAdmissionAfterResume(boundary, ToBoundary(continuity));
        if (!continuity.CaptureInitialized || !IsValidRecoveryBoundary(ToBoundary(continuity)) ||
            signedCheckpointCapture is null && continuity.CaptureSequence < boundary.CaptureSequence ||
            (!paused && !(operation.RestoreResumeCommandId is not null && resumedOrSuperseded)) ||
            IsRestored(operation) && !resumedOrSuperseded)
            throw new InstallerException("Restore-only capture admission no longer matches the retained operation boundary.");
        if (IsRestored(operation))
        {
            var recorded = operation.PostMutationContinuity!;
            if (continuity.CaptureVersion < recorded.CaptureVersion || continuity.CaptureSequence < recorded.CaptureSequence ||
                continuity.CaptureVersion == recorded.CaptureVersion && continuity.CaptureState != recorded.CaptureState)
                throw new InstallerException("Restore-only current admission regressed from the terminal recovery evidence.");
            return (Result(operation.Kind, RecoveryOutcome(boundary, continuity.CaptureVersion), operation.OperationId,
                paths, manifest, manifest.DockerDaemon, true, true), verifiedOwner);
        }

        if (signedPauseCommandId is { } pauseCommand)
        {
            if (paused && pauseCommand != await ReadSignedPauseOwnerAsync(
                    databases.RawIngressDatabasePath, operation.OperationId, boundary, cancellationToken).ConfigureAwait(false))
                throw new InstallerException("Signed restore-only capture admission is no longer owned by the interrupted upgrade.");
            // A local read can identify an already superseded pause. Only the predecessor can close the race
            // between that read and the resume transaction, including later no-op pauses at the same version.
            if (!request.LeavePaused)
                await lifecycle.VerifyOwnedUpgradeResumeAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        }

        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        for (var index = 0; index < roots.Length; index++)
        {
            if (NativeLinux.GetNodeIdentity(roots[index]) != rootIdentities[index])
                throw new InstallerException("Restore-only recovery root identity changed during verification.");
        }
        if (request.LeavePaused)
            return (Result(operation.Kind, "paused-retry-admitted", operation.OperationId, paths, manifest,
                manifest.DockerDaemon, true, true), verifiedOwner);
        if (signedPauseCommandId is not null)
            await WriteHostLimitsToInstallLogAsync(request, paths, operation.OperationId, hostLimits, cancellationToken)
                .ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Restoring,
            ExpectedOwnerBootstrapState = verifiedOwner,
            RestoreResumeCommandId = operation.RestoreResumeCommandId ?? Guid.NewGuid(),
            FailureCode = operation.FailureCode ?? "upgrade-interrupted",
            FailureMessage = operation.FailureMessage ?? "The original upgrade was interrupted; its failure detail was not retained. Restore-only does not complete that upgrade."
        }, cancellationToken).ConfigureAwait(false);
        // Replaying this same command after a lost acknowledgement proves ownership; never mint a second command.
        var commandId = operation.RestoreResumeCommandId!.Value;
        var receipt = signedPauseCommandId is { } ownedPause
            ? await lifecycle.ResumeOwnedUpgradeAsync(operation.OperationId, ownedPause, commandId,
                boundary.CaptureVersion, lifecycleControlToken, cancellationToken).ConfigureAwait(false)
            : await lifecycle.ResumeRecoveryAsync(operation.OperationId, commandId,
                boundary.CaptureVersion, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        if (!IsValidRecoveryReceipt(boundary, receipt) || !paused && !receipt.Replayed)
            throw new InstallerException("Restore-only command receipt does not prove the retained resume.");
        continuity = await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
        var postBoundary = ToBoundary(continuity);
        await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Restored,
            Status = InstallationStatus.Failed,
            MutationStarted = false,
            PostMutationContinuity = postBoundary,
            RestoreResumeReceipt = receipt
        }, cancellationToken).ConfigureAwait(false);
        return (Result(operation.Kind, RecoveryOutcome(boundary, continuity.CaptureVersion), operation.OperationId,
            paths, manifest, manifest.DockerDaemon, true, true), verifiedOwner);
    }

    /// <summary>The operation's own database snapshot when it was recorded and has not been put back yet.</summary>
    private static LifecycleStateSnapshot? UnrestoredSnapshot(LifecycleOperationState operation)
        => operation.StateSnapshotRestoredUtc is null ? operation.StateSnapshot : null;

    private static string RecoveryOutcome(LifecycleContinuityBoundary before, long currentVersion)
        => currentVersion == before.CaptureVersion + 1
            ? "restored-previous-healthy-admission-resumed"
            : "restored-previous-healthy-resume-superseded-current-admission-preserved";

    private static bool IsValidRecoveryBoundary(LifecycleContinuityBoundary? boundary)
        => boundary is
        {
            CaptureState: "Paused" or "Running" or "PauseRequested",
            CaptureInitialized: true,
            CaptureVersion: >= 0, CaptureSequence: >= 0,
            RawPending: >= 0, RawLeased: >= 0, LanePending: >= 0, LaneLeased: >= 0,
            ProcessingPending: >= 0, ProcessingLeased: >= 0, OutboxPending: >= 0, OutboxLeased: >= 0,
            ReplayPending: >= 0, ReplayLeased: >= 0
        } && boundary.RecordedUtc != default;

    private static bool IsValidRecoveryReceipt(LifecycleContinuityBoundary before, LifecycleResumeReceipt? receipt)
        => receipt is { State: "Running", Changed: true } && before.CaptureVersion < long.MaxValue &&
           receipt.Version == before.CaptureVersion + 1 && receipt.RequestedUtc >= before.RecordedUtc && receipt.CompletedUtc >= receipt.RequestedUtc;

    private static bool IsRecoveryAdmissionAfterResume(LifecycleContinuityBoundary before, LifecycleContinuityBoundary after)
        => IsValidRecoveryBoundary(after) && after.CaptureSequence >= before.CaptureSequence &&
           after.RecordedUtc >= before.RecordedUtc &&
           (after.CaptureVersion == before.CaptureVersion + 1 && after.CaptureState == "Running" ||
            after.CaptureVersion > before.CaptureVersion + 1);

    private static async Task<string> ReadRecoveryTextAsync(string path, CancellationToken cancellationToken)
    {
        SafeFileSystem.EnsureSafeExistingAncestors(Path.GetDirectoryName(path)!);
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        if (stream.Length > 1024 * 1024)
            throw new InstallerException("A restore-only recovery record exceeds the bounded input size.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<LifecycleResult> ChangeImageAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        ComposeFiles compose,
        DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        uint uid,
        uint gid,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string lifecycleControlToken,
        string verificationToken,
        AcquiredImage? signedImage,
        bool rollback,
        HostPrerequisiteReport? hostLimits,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw InstallerException.Refused("An uninstalled instance must be reinstalled before image lifecycle operations.");
        if (rollback && manifest.PreviousImage is null) throw InstallerException.Refused("No previous image is retained for rollback.");
        // The target release's notes are shown, and an older target refused, against the manifest read under the
        // instance lock and before the journal entry, so a refusal leaves nothing to settle, a dry run shows them too,
        // and a failure anywhere after has already shown them. With --json they are an event on standard error.
        var upgradeNotes = !rollback && request.TargetRelease is { } targetRelease
            ? UpgradeNotes.Plan(
                await UpgradeNotes.InstalledVersionAsync(paths, manifest.Image, cancellationToken).ConfigureAwait(false),
                targetRelease)
            : null;
        if (upgradeNotes is not null)
        {
            await UpgradeNotes.ShowAsync(upgradeNotes, request.Json, output, error).ConfigureAwait(false);
        }
        // A release candidate was checked, and a registry image pulled, before the locks (AdmitBeforeWritingAsync).
        var operation = await BeginAsync(request, paths, rollback ? LifecycleOperationKind.Rollback : LifecycleOperationKind.Upgrade,
            manifest, cancellationToken, docker).ConfigureAwait(false);
        try
        {
            // A rollback going ahead without a limit Docker cannot enforce logs it with the operation before anything
            // changes; a plan writes nothing (#1357).
            if (!request.DryRun && !request.LeavePaused)
            {
                await WriteHostLimitsToInstallLogAsync(request, paths, operation.OperationId, hostLimits, cancellationToken)
                    .ConfigureAwait(false);
            }
            var changed = await ChangeImageAsync(
                    request, paths, manifest, installationResult, compose, docker, lifecycleClientFactory,
                    uid, gid, ownerClientFactory, lifecycleControlToken, verificationToken, signedImage, rollback, operation,
                    hostLimits, error, cancellationToken)
                .ConfigureAwait(false);
            return upgradeNotes is null ? changed : changed with { UpgradeNotes = upgradeNotes };
        }
        catch (Exception exception) when (!request.DryRun && exception is not OperationCanceledException)
        {
            // Every gate between the journal entry above and the mutation record (release label agreement, the
            // already-active and contract checks, the state preflight, the migration assertion, the owner-state read,
            // Compose staging) runs before the instance is touched. A refusal there is settled as terminal so the
            // journal never reports a running operation that never started, which would otherwise block rollback,
            // uninstall, and any other upgrade behind a --resume the same gate would refuse again. A cancellation is
            // not a refusal: its unmutated journal stays resumable and, like any unmutated record, never blocks
            // another operation.
            await SettleRefusedOperationAsync(paths, operation, exception).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<LifecycleResult> ChangeImageAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        InstallationResult installationResult,
        ComposeFiles compose,
        DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        uint uid,
        uint gid,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory,
        string lifecycleControlToken,
        string verificationToken,
        AcquiredImage? signedImage,
        bool rollback,
        LifecycleOperationState operation,
        HostPrerequisiteReport? hostLimits,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var owner = ownerClientFactory?.Invoke(installationResult.Url) ?? new OwnerBootstrapClient(installationResult.Url);
        var automaticSignedRecovery = signedImage is not null && !request.LeavePaused;
        var reuseSignedPause = automaticSignedRecovery && operation.MutationStarted;
        if (request.LeavePaused)
        {
            if (signedImage?.VerifiedRelease.ChecksumsSha256 != PublishedRc2ChecksumsSha256)
                throw InstallerException.Refused("Leave-paused requires the original published RC2 archive and checksums.");
            if (operation.Phase == LifecycleOperationPhase.Completed)
            {
                return await ObserveCompletedPausedUpgradeAsync(paths, manifest, installationResult, operation, docker,
                    compose, owner, lifecycleClientFactory, lifecycleControlToken, verificationToken, signedImage.VerifiedRelease,
                    uid, gid, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (IsQuietPausedRecovery(operation) || IsAwaitingPausedRecovery(operation) || IsLeavePausedAdmissionPreserved(operation))
                return await SettlePausedPredecessorAsync(paths, manifest, installationResult, operation, docker, compose,
                    owner, lifecycleClientFactory, lifecycleControlToken, verificationToken, hostLimits, uid, gid, cancellationToken)
                    .ConfigureAwait(false);
            if (operation.SchemaVersion == DeploymentSchemaVersions.LifecycleOperation || IsArmedPausedRetry(operation))
            {
                var original = await RestoreOnlyAsync(request, paths, manifest, installationResult,
                    operation with { Phase = LifecycleOperationPhase.Restoring }, docker,
                    lifecycleClientFactory, ownerClientFactory, lifecycleControlToken, verificationToken, signedImage,
                    uid, gid, error, hostLimits, cancellationToken).ConfigureAwait(false);
                EnsurePausedUpgradeStorage(paths, request, manifest);
                operation = operation with { ExpectedOwnerBootstrapState = original.VerifiedOwner };
                if (operation.SchemaVersion == DeploymentSchemaVersions.LifecycleOperation)
                {
                    operation = await RecordAsync(paths, operation with
                    {
                        SchemaVersion = LeavePausedJournalVersion,
                        LeavePausedOriginalSnapshot = operation.StateSnapshot,
                        LeavePausedOriginalRestorePlanId = operation.StateRestorePlan!.SnapshotId,
                        SignedReleaseChecksumsSha256 = signedImage.VerifiedRelease.ChecksumsSha256
                    }, cancellationToken).ConfigureAwait(false);
                }
                await WriteHostLimitsToInstallLogAsync(request, paths, operation.OperationId, hostLimits, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (operation.Phase != LifecycleOperationPhase.Committed)
            {
                throw InstallerException.Refused("Leave-paused has a held precommit recovery. Retain the candidate stores and both checkpoints.");
            }
        }
        if (!operation.MutationStarted)
        {
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            await docker.VerifyContainerAsync(
                compose, paths, manifest.Image, uid, gid, cancellationToken).ConfigureAwait(false);
            EnsureUpgradeStorage(paths, request.ImageArchive, manifest.ReplayProfile, rollback, snapshotRequired: false);
        }
        var operationRoot = Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        var previousManifestPath = Path.Combine(operationRoot, "previous-instance-manifest.json");
        var previousResultPath = Path.Combine(operationRoot, "previous-installation-result.json");
        if (IsPendingSignedAutomaticRecovery(operation))
        {
            if (!automaticSignedRecovery)
                throw InstallerException.Refused("The pending automatic recovery requires the exact ordinary signed upgrade request.");
            VerifyAutomaticRecoveryRelease(operation, signedImage!);
            var original = await ReadRecoverySnapshotAsync(
                paths, operation, previousManifestPath, previousResultPath, cancellationToken).ConfigureAwait(false);
            if (operation.FailureCode == SignedAutomaticRecoveryPreparing)
            {
                manifest = original.Manifest;
                installationResult = original.Result;
                var previous = compose with
                {
                    ComposeFile = Path.Combine(operationRoot, "previous-compose.yml"),
                    EnvironmentFile = Path.Combine(operationRoot, "previous.env")
                };
                await ValidateComposeAuthorityAsync(docker, previous, manifest, cancellationToken).ConfigureAwait(false);
                if (UnrestoredSnapshot(operation) is { } interrupted)
                {
                    await ProveStoppedSignedRestoreAsync(paths, manifest, operation, interrupted, docker, compose,
                        operationRoot, cancellationToken).ConfigureAwait(false);
                    OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
                    operation = await RestoreStateSnapshotAsync(paths, manifest, operation, interrupted, request.Json, error,
                        cancellationToken, signedStoppedProof: true).ConfigureAwait(false);
                }
                else
                {
                    await StopSignedCandidateWithoutCheckpointAsync(paths, manifest, operation, docker, previous, cancellationToken)
                        .ConfigureAwait(false);
                }
                await RestoreRecoveryRecordsAsync(paths, compose, previous, original, operationRoot, cancellationToken)
                    .ConfigureAwait(false);
                operation = await RecordAsync(paths, operation with { FailureCode = SignedAutomaticRecoveryPending },
                    cancellationToken).ConfigureAwait(false);
            }
            else if (original.Manifest != manifest || original.Result != installationResult)
                throw InstallerException.Refused("The pending automatic recovery requires the unchanged predecessor records.");
            if (operation.StateSnapshot is { } checkpoint)
            {
                var verified = await VerifyAutomaticRecoveryCheckpointAsync(paths, manifest, operation, checkpoint, cancellationToken)
                    .ConfigureAwait(false);
                if (UnrestoredSnapshot(operation) is not null)
                    throw InstallerException.Refused("The pending automatic recovery has no completed checkpoint restore.");
                VerifyCompletedRestorePlan(checkpoint, verified, operation.StateRestorePlan);
            }
            await ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
            await StartPendingSignedPredecessorAsync(paths, manifest, operation, docker, compose, hostLimits,
                operationRoot, cancellationToken).ConfigureAwait(false);
            var ownerState = operation.ExpectedOwnerBootstrapState ?? installationResult.OwnerBootstrapState;
            _ = await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                verificationToken, uid, gid, cancellationToken, ownerState, ownerState == "owner-password-change-required",
                allowQuarantinedReadiness: request.AllowQuarantinedReadiness, readinessTimeout: ImageChangeReadinessTimeout)
                .ConfigureAwait(false);
            var restoredLifecycle = CreateLifecycleClient(installationResult.Url, lifecycleClientFactory);
            operation = await SettleSignedAutomaticRecoveryAsync(paths, operation, restoredLifecycle, lifecycleControlToken,
                cancellationToken).ConfigureAwait(false);
            return Result(operation.Kind, AutomaticRecoveryOutcome(operation),
                operation.OperationId, paths, manifest, manifest.DockerDaemon, true, true);
        }
        if (operation.MutationStarted && operation.Phase != LifecycleOperationPhase.Committed && !request.LeavePaused)
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
            if (IsCommittedSignedRetry(operation))
            {
                if (!automaticSignedRecovery)
                    throw InstallerException.Refused("The committed retry requires the original ordinary signed upgrade request.");
                VerifyAutomaticRecoveryRelease(operation, signedImage!);
            }
            var committedExpectedOwnerState = operation.ExpectedOwnerBootstrapState;
            var verifiedOwnerState = await VerifyCandidateAsync(owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                verificationToken, uid, gid, cancellationToken,
                committedExpectedOwnerState ?? installationResult.OwnerBootstrapState,
                allowCompletedPasswordReplacement:
                    committedExpectedOwnerState is null or "owner-password-change-required",
                allowQuarantinedReadiness: request.AllowQuarantinedReadiness,
                readinessTimeout: ImageChangeReadinessTimeout)
                .ConfigureAwait(false);
            if (verifiedOwnerState != committedExpectedOwnerState)
            {
                operation = await RecordAsync(
                    paths,
                    operation with { ExpectedOwnerBootstrapState = verifiedOwnerState },
                    cancellationToken).ConfigureAwait(false);
            }
            if (request.LeavePaused)
            {
                await VerifyPausedCommitEvidenceAsync(paths, manifest, operation, cancellationToken).ConfigureAwait(false);
                EnsureFleetStoresAbsent(paths);
            }
            // The commit is durable, so the retained release record is settled again here: a run that lost its final
            // resume acknowledgement, or failed between the commit and the record write, must not leave the record
            // naming a release the instance no longer runs.
            await SettleReleaseRecordAsync(paths, signedImage, manifest.Image, cancellationToken).ConfigureAwait(false);
            var committedLifecycle = CreateLifecycleClient(installationResult.Url, lifecycleClientFactory);
            if (request.LeavePaused)
                return await SettlePausedUpgradeAsync(request, paths, manifest, operation, signedImage!.VerifiedRelease, committedLifecycle,
                    lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            if (IsCommittedSignedRetry(operation))
                return await SettleCommittedSignedRetryAsync(request, paths, manifest, operation, signedImage!.VerifiedRelease,
                    committedLifecycle, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            await committedLifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            await SettleInstallRecordAsync(request, paths, manifest, operation.Kind, cancellationToken).ConfigureAwait(false);
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result(operation.Kind, "completed", operation.OperationId, paths, manifest, manifest.DockerDaemon, true, true);
        }
        ImageInstallationIdentity candidate;
        if (rollback)
        {
            var target = manifest.PreviousImage!;
            var synthetic = ImageRequest(manifest, target.ImmutableReference, null, null, noDownload: true);
            var prepared = await docker.PrepareImageAsync(synthetic, false, release: null, cancellationToken)
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
            if (request.DryRun && signedImage?.CheckedImage is { } checkedImage &&
                !await docker.HoldsReleaseImageAsync(signedImage.Image, cancellationToken).ConfigureAwait(false))
            {
                // A plan imports nothing: a release image Docker does not hold yet is described as it was read and
                // checked from the release's archive or registry, under the ID the upgrade will report (#1384).
                candidate = await docker.PlannedReleaseImageAsync(signedImage.Image, checkedImage, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var prepared = await docker.PrepareImageAsync(synthetic, !request.DryRun, signedImage?.Image, cancellationToken)
                    .ConfigureAwait(false);
                EnsureDaemon(manifest.DockerDaemon, prepared.Daemon);
                candidate = prepared.Image;
            }
        }
        if (candidate.ImageId == manifest.Image.ImageId)
        {
            if (request.Resume && !request.LeavePaused && manifest.LastLifecycleOperationId == operation.OperationId)
            {
                await SettleInstallRecordAsync(request, paths, manifest, operation.Kind, cancellationToken).ConfigureAwait(false);
                await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
                return Result(operation.Kind, "completed", operation.OperationId, paths, manifest, manifest.DockerDaemon, true, true);
            }
            throw InstallerException.Refused("The candidate image is already active.");
        }
        if (!IsCanonicalImage(
                candidate,
                manifest.ComponentSchemaVersion,
                manifest.DockerDaemon.Architecture,
                manifest.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner))
        {
            throw new InstallerException("The candidate image does not declare the required CameraAgent configuration and catalog contracts.");
        }
        CameraAgentInstaller.EnsureImageSupportsCatalog(candidate, manifest.Catalog.CatalogId);
        if (manifest.DeepSkyCatalog is { } deepSky) CameraAgentInstaller.EnsureImageSupportsCatalog(candidate, deepSky.CatalogId);
        if (!rollback && !CameraAgentStateContract.IsCurrent(candidate.UpgradeCompatibility))
        {
            throw InstallerException.Refused(
                $"The candidate image declares state compatibility '{CameraAgentStateContract.Describe(candidate.UpgradeCompatibility)}' instead of the supported '{CameraAgentStateContract.Current}' contract; an explicit state-disposition procedure is required.");
        }
        if (rollback && !CameraAgentStateContract.IsKnown(manifest.Image.UpgradeCompatibility) &&
            !CameraAgentStateContract.IsKnown(manifest.UpgradeCompatibility))
        {
            throw InstallerException.Refused("Automatic rollback is forbidden because the active image did not declare a supported state contract.");
        }
        // A rollback that undoes a migrating upgrade puts back the database snapshot that upgrade took, because the
        // rollback image is not assumed to read the migrated stores. The snapshot must still be intact and must have
        // been taken under the image the rollback returns to.
        var (retainedSnapshot, preflightDatabases) = rollback && manifest.PreviousStateSnapshot is { } previousState
            ? await ResolveRetainedSnapshotAsync(paths, manifest, operation, previousState, candidate, cancellationToken)
                .ConfigureAwait(false)
            : (null, null);
        // An interrupted operation whose own snapshot is not yet restored runs on that snapshot once --resume restores
        // it, so the snapshot is authenticated here, before any container or file changes, whichever snapshot supplies
        // the preflight view: a rollback previews its retained snapshot but still restores this one first. An upgrade's
        // preflight and migration check then read it rather than whatever the interruption left in the live stores, so a
        // damaged candidate state can never block its own recovery.
        if (operation is { MutationStarted: true, StateSnapshot: { } pendingSnapshot, StateSnapshotRestoredUtc: null })
        {
            var pendingManifest = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, pendingSnapshot, cancellationToken)
                .ConfigureAwait(false);
            preflightDatabases ??= CameraAgentStateSnapshot.PreflightView(paths, pendingSnapshot, pendingManifest);
        }
        // Every boundary is proved before the snapshot, drain, stop, and Compose mutation begin, so an incompatible
        // upgrade or rollback never reaches a container restart loop.
        // This creates a missing bind source and normalizes an existing one to 0700, exactly as the committed
        // mutation does. A plan creates nothing: it checks each source the same way and evaluates the ones the command
        // would create or normalize as the command leaves them (#1315). A linked or foreign-owned source still fails
        // closed here with its own path rather than reaching the consolidated report.
        HashSet<string>? preparedSources = null;
        if (request.DryRun)
        {
            preparedSources = new HashSet<string>(
                ComposeDeployment.WritableStateDirectories(paths.StateRoot)
                    .Where(directory => SafeFileSystem.CheckRuntimeDirectory(directory, manifest.RuntimeUid, manifest.RuntimeGid)),
                StringComparer.Ordinal);
        }
        else
        {
            foreach (var directory in ComposeDeployment.WritableStateDirectories(paths.StateRoot))
            {
                SafeFileSystem.CreateRuntimeDirectory(directory, manifest.RuntimeUid, manifest.RuntimeGid);
            }
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
            rollback ? CameraAgentStateContractPolicy.AllowLegacy : CameraAgentStateContractPolicy.RequireCurrent,
            signedImage?.Release.Tag,
            preflightDatabases,
            preparedSources)
            .ConfigureAwait(false);
        // The preflight has proved the persisted state readable and within the candidate's boundaries, so what remains
        // is whether the candidate changes that state. An unchanged contract is a pure swap. A declared migration has
        // its small databases snapshotted before the candidate starts, and a failed candidate is restored onto that
        // snapshot. The decision is sticky across --resume: a candidate that already migrated the stores before an
        // interruption no longer looks like a migration, but its snapshot must still be retaken on a retry.
        var declaredChanges = rollback
            ? []
            : CameraAgentStateMigration.DeclaredChanges(
                paths, manifest.Image, manifest.Image.UpgradeCompatibility ?? manifest.UpgradeCompatibility, candidate,
                preflightDatabases?.RawIngressDatabasePath);
        var stateSnapshotRequired = operation.StateSnapshotRequired || retainedSnapshot is not null || declaredChanges.Count > 0;
        // Archive/headroom admission ran before image preparation. Only a snapshotting operation needs the
        // database copies and carry journal, and that decision is known only after candidate state preflight.
        if (!operation.MutationStarted && stateSnapshotRequired)
            EnsureUpgradeStorage(paths, request.ImageArchive, manifest.ReplayProfile, rollback, snapshotRequired: true);
        if (operation.CandidateImage is not null && operation.CandidateImage != candidate)
            throw new InstallerException("The retained image operation has a different candidate identity.");
        if (operation.SignedReleaseChecksumsSha256 is { } retainedChecksums &&
            retainedChecksums != signedImage?.VerifiedRelease.ChecksumsSha256)
            throw new InstallerException("The retained image operation has different release checksums.");
        operation = operation with
        {
            CandidateImage = candidate,
            Phase = reuseSignedPause ? operation.Phase : LifecycleOperationPhase.CandidateValidated,
            Status = InstallationStatus.Running,
            StateSnapshotRequired = stateSnapshotRequired,
            // What the candidate changes, kept for the operator; a retry that no longer sees it keeps the first answer.
            StateMigration = declaredChanges.Count > 0 ? string.Join("; ", declaredChanges) : operation.StateMigration,
            RetainedStateSnapshot = retainedSnapshot,
            SignedReleaseChecksumsSha256 = signedImage?.VerifiedRelease.ChecksumsSha256
        };
        if (retainedSnapshot is not null)
        {
            // Said before anything changes, so a dry run says it too.
            await CameraAgentStateSnapshot.ShowWarningAsync(retainedSnapshot, request.Json, error).ConfigureAwait(false);
        }
        if (request.DryRun)
        {
            var planned = Result(operation.Kind, "planned", operation.OperationId, paths, manifest with { Image = candidate }, manifest.DockerDaemon, null, null);
            return planned with
            {
                Plan = ImagePlan(request, paths, manifest, candidate, rollback, stateSnapshotRequired, retainedSnapshot, hostLimits)
            };
        }
        var expectedOwnerState = operation.ExpectedOwnerBootstrapState;
        if (!operation.MutationStarted)
        {
            var currentOwnerState = await owner.ReadInstallationStateAsync(
                verificationToken, CurrentInstallationExpectation(manifest, installationResult), cancellationToken)
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
        if (!reuseSignedPause)
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
        var sourceCompose = rollback
            ? await File.ReadAllTextAsync(retainedRollbackCompose, cancellationToken).ConfigureAwait(false)
            : originalCompose;
        // Upgrade replay authenticates this durable file while the predecessor still runs. Stage the final upgrade limits
        // in one write. Rollback must first authenticate its retained Compose model before applying the current limits.
        var candidateResources = rollback ? manifest.Resources : CameraAgentContainerResources.UpgradeTarget(manifest.Resources);
        var preparedCompose = rollback ? sourceCompose : CameraAgentContainerResources.WithLimits(
            sourceCompose, candidateResources!, manifest.Resources ?? CameraAgentContainerResources.Legacy);
        SafeFileSystem.WriteTextAtomic(stagedEnvironment, candidateEnvironment);
        SafeFileSystem.WriteTextAtomic(stagedCompose, preparedCompose);
        var candidateCompose = compose with { ComposeFile = stagedCompose, EnvironmentFile = stagedEnvironment };
        var renderedCandidate = await docker.ComposeAsync(
            candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName, ["config"], cancellationToken)
            .ConfigureAwait(false);
        var candidateComposeSha256 = ComposeDeployment.ComputeSha256(renderedCandidate.StandardOutput);
        if (rollback)
        {
            if (candidateComposeSha256 != manifest.PreviousComposeModelSha256)
                throw new InstallerException("The retained rollback Compose model differs from its authenticated identity.");
            var limitedCompose = CameraAgentContainerResources.WithLimits(
                sourceCompose, candidateResources ?? CameraAgentContainerResources.Legacy, recorded: null);
            if (!string.Equals(limitedCompose, sourceCompose, StringComparison.Ordinal))
            {
                SafeFileSystem.WriteTextAtomic(stagedCompose, limitedCompose);
                renderedCandidate = await docker.ComposeAsync(
                    candidateCompose.ComposeFile, candidateCompose.EnvironmentFile, candidateCompose.ProjectName, ["config"], cancellationToken)
                    .ConfigureAwait(false);
                candidateComposeSha256 = ComposeDeployment.ComputeSha256(renderedCandidate.StandardOutput);
            }
        }
        var baseAddress = installationResult.Url;
        var lifecycle = CreateLifecycleClient(baseAddress, lifecycleClientFactory);
        var candidateLifecycle = lifecycle;
        LifecycleContinuity? retriedDrain = null;
        if (operation.MutationStarted && !request.LeavePaused)
        {
            SafeFileSystem.WriteTextAtomic(compose.ComposeFile, originalCompose);
            SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, originalEnvironment);
            SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(compose.ComposeFile)!, paths.ProductRoot);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            if (operation.StateSnapshot is { } interrupted && operation.StateSnapshotRestoredUtc is null)
            {
                if (automaticSignedRecovery)
                {
                    await ProveStoppedSignedRestoreAsync(paths, manifest, operation, interrupted, docker, compose,
                        operationRoot, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                        ["stop"], cancellationToken).ConfigureAwait(false);
                }
                OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
                operation = await RestoreStateSnapshotAsync(
                    paths, manifest, operation, interrupted, request.Json, error, cancellationToken,
                    signedStoppedProof: automaticSignedRecovery).ConfigureAwait(false);
            }
            await StartOnThisHostAsync(docker, compose, hostLimits, operationRoot, cancellationToken).ConfigureAwait(false);
            var restoredOwnerState = await VerifyCandidateAsync(
                owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                verificationToken, uid, gid, cancellationToken, expectedOwnerState, allowOwnerStateProgression,
                allowQuarantinedReadiness: request.AllowQuarantinedReadiness, readinessTimeout: ImageChangeReadinessTimeout)
                .ConfigureAwait(false);
            if (restoredOwnerState != expectedOwnerState)
            {
                expectedOwnerState = restoredOwnerState;
                allowOwnerStateProgression = allowLegacyOwnerStateProgression ||
                                             expectedOwnerState == "owner-password-change-required";
                operation = operation with { ExpectedOwnerBootstrapState = expectedOwnerState };
                if (!reuseSignedPause)
                    operation = await RecordAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            }
            if (reuseSignedPause)
            {
                var current = await lifecycle.ReadContinuityAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
                if (current.CaptureState == "PauseRequested")
                    current = await lifecycle.ConfirmDrainedAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
                if (!IsValidRecoveryBoundary(ToBoundary(current)) || current.CaptureState == "PauseRequested")
                    throw InstallerException.Refused("The signed retry could not verify settled predecessor admission.");
                if (current.CaptureState == "Paused")
                {
                    current = await lifecycle.ConfirmDrainedAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false);
                    var observed = ToBoundary(current);
                    if (operation.PreMutationContinuity is null &&
                        await ReadAutomaticRecoveryPauseAsync(paths, operation.OperationId, observed, cancellationToken)
                            .ConfigureAwait(false) is { } accepted)
                        operation = operation with { PreMutationContinuity = observed, SignedRecoveryPauseCommandId = accepted };
                    if (await OwnsRetainedAutomaticPauseAsync(paths, operation, current, cancellationToken).ConfigureAwait(false))
                        retriedDrain = current;
                }
                if (retriedDrain is null)
                {
                    operation = await PreserveRecoveryAdmissionAsync(paths, operation, ToBoundary(current),
                        RecoveryAdmissionPreserved, cancellationToken).ConfigureAwait(false);
                    return Result(operation.Kind, AutomaticRecoveryOutcome(operation), operation.OperationId, paths, manifest,
                        manifest.DockerDaemon, true, true);
                }
            }
            // The original stays paused through the retry. A signed retry retains its changed pause rather than
            // superseding it with a no-op. The restored checkpoint is history; the retry takes its own.
            operation = await RecordAsync(paths, operation with
            {
                SchemaVersion = DeploymentSchemaVersions.LifecycleOperation,
                Phase = LifecycleOperationPhase.Prepared,
                MutationStarted = true,
                StateSnapshot = null,
                StateSnapshotRestoredUtc = null,
                StateRestorePlan = null,
                AuthorizedSignedRestoreSnapshotId = null
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
        // The recovery records become authoritative with the next journal entry, so the operation directory and every
        // directory above it to the product root are made durable first.
        SafeFileSystem.FlushDirectoryChain(operationRoot, paths.ProductRoot);
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
            var continuity = retriedDrain ?? (request.LeavePaused
                ? await lifecycle.ConfirmDrainedAsync(lifecycleControlToken, cancellationToken).ConfigureAwait(false)
                : await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false));
            if (request.LeavePaused)
            {
                EnsureUnchangedPausedBoundary(operation.PreMutationContinuity!, continuity);
                EnsureFleetStoresAbsent(paths);
                await ReadSignedPauseOwnerAsync(CameraAgentStateLayout.RawIngressDatabasePath(paths.StateRoot),
                    operation.OperationId, operation.PreMutationContinuity!, cancellationToken).ConfigureAwait(false);
                EnsureUpgradeStorage(paths, request.ImageArchive, manifest.ReplayProfile, rollback: false, snapshotRequired: true);
            }
            var drainedBoundary = ToBoundary(continuity);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Drained,
                PreMutationContinuity = drainedBoundary,
                SignedRecoveryPauseCommandId = automaticSignedRecovery && !reuseSignedPause
                    ? await ReadAutomaticRecoveryPauseAsync(paths, operation.OperationId, drainedBoundary, cancellationToken)
                        .ConfigureAwait(false)
                    : operation.SignedRecoveryPauseCommandId
            }, cancellationToken)
                .ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["stop"], cancellationToken)
                .ConfigureAwait(false);
            if (retriedDrain is not null)
            {
                var stopped = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
                if (!stopped.Exists || stopped.Running || stopped.ImageId != manifest.Image.ImageId ||
                    compose.ReplayRunnerContainerName is { } runner &&
                        (await docker.InspectContainerAsync(runner, cancellationToken).ConfigureAwait(false)).Running)
                    throw InstallerException.Refused("The signed retry could not prove the predecessor and its writers stopped.");
                if (!await OwnsRetainedAutomaticPauseAsync(paths, operation, retriedDrain, cancellationToken).ConfigureAwait(false))
                    throw InstallerException.Refused("The interrupted signed upgrade no longer owns its recorded changed pause. Capture admission was preserved.");
            }
            OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
            // Nothing bulk is copied: frames, archives and derived products stay where they are, and the candidate
            // holds their retention and cleanup while capture admission is paused. Only a declared migration (or a
            // rollback that puts a retained snapshot back) snapshots the small databases, while the instance is
            // stopped, and the record that the candidate may now start is written only after that snapshot is durable.
            if (operation.StateSnapshotRequired)
            {
                IReadOnlyList<string> stores = SnapshotInventory(paths, manifest.ReplayProfile, rollback);
                if (operation.RetainedStateSnapshot is { } replaced)
                {
                    // A rollback first snapshots every store it is about to replace, so rolling forward again brings each back.
                    var replacedManifest = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, replaced, cancellationToken)
                        .ConfigureAwait(false);
                    stores = [.. stores.Union(replacedManifest.Databases.Select(static database => database.RelativePath), StringComparer.Ordinal)];
                }
                var snapshot = await CameraAgentStateSnapshot.CreateAsync(
                    paths, manifest.InstanceId, operation.OperationId, manifest.Image, stores, cancellationToken).ConfigureAwait(false);
                if (request.LeavePaused)
                    await VerifyPausedRetrySnapshotAsync(paths, manifest.InstanceId, operation, snapshot, continuity, cancellationToken)
                        .ConfigureAwait(false);
                operation = await RecordAsync(paths, operation with
                {
                    Phase = LifecycleOperationPhase.StateSnapshotRecorded,
                    StateSnapshot = snapshot,
                    StateSnapshotRestoredUtc = null,
                    StateRestorePlan = request.LeavePaused || automaticSignedRecovery ? null : operation.StateRestorePlan
                }, cancellationToken).ConfigureAwait(false);
                if (operation.RetainedStateSnapshot is { } retainedState)
                {
                    operation = await RestoreStoresAsync(paths, manifest, operation, retainedState, request.Json, error, cancellationToken)
                        .ConfigureAwait(false);
                    operation = await RecordAsync(
                        paths, operation with { Phase = LifecycleOperationPhase.RetainedStateApplied }, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Mutating,
                MutationStarted = true
            }, cancellationToken).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await StartOnThisHostAsync(docker, candidateCompose, hostLimits, operationRoot, cancellationToken).ConfigureAwait(false);
            var verifiedOwnerState = await VerifyCandidateAsync(
                owner, docker, candidateCompose, paths, manifest, installationResult, candidate, verificationToken, uid, gid,
                cancellationToken, expectedOwnerState, allowOwnerStateProgression,
                allowQuarantinedReadiness: request.AllowQuarantinedReadiness, readinessTimeout: ImageChangeReadinessTimeout)
                .ConfigureAwait(false);
            if (request.LeavePaused)
                EnsureFleetStoresAbsent(paths);
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
            await StartOnThisHostAsync(docker, candidateCompose, hostLimits, operationRoot, cancellationToken, restart: true)
                .ConfigureAwait(false);
            verifiedOwnerState = await VerifyCandidateAsync(
                owner, docker, candidateCompose, paths, manifest, installationResult, candidate, verificationToken, uid, gid,
                cancellationToken, expectedOwnerState, allowOwnerStateProgression,
                allowQuarantinedReadiness: request.AllowQuarantinedReadiness, readinessTimeout: ImageChangeReadinessTimeout)
                .ConfigureAwait(false);
            if (request.LeavePaused)
                EnsureFleetStoresAbsent(paths);
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
            if (request.LeavePaused) EnsureUnchangedPausedBoundary(operation.PreMutationContinuity!, postMutation);
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.CandidateVerified,
                PostMutationContinuity = ToBoundary(postMutation),
                ExpectedOwnerBootstrapState = expectedOwnerState
            }, cancellationToken).ConfigureAwait(false);
            if (request.LeavePaused)
                await VerifyPausedCommitEvidenceAsync(paths, manifest, operation, cancellationToken).ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            SafeFileSystem.CreateOwnerDirectory(rollbackRoot);
            SafeFileSystem.WriteTextAtomic(retainedRollbackCompose, originalCompose);
            SafeFileSystem.WriteTextAtomic(retainedRollbackEnvironment, originalEnvironment);
            SafeFileSystem.FlushDirectoryChain(rollbackRoot, paths.ProductRoot);
            SafeFileSystem.WriteTextAtomic(compose.ComposeFile, await File.ReadAllTextAsync(stagedCompose, cancellationToken).ConfigureAwait(false));
            SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, candidateEnvironment);
            SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(compose.ComposeFile)!, paths.ProductRoot);
            var committed = manifest with
            {
                Image = candidate,
                PreviousImage = manifest.Image,
                ComposeModelSha256 = candidateComposeSha256,
                UpgradeCompatibility = candidate.UpgradeCompatibility ?? "requires-declared-compatible-migration",
                LifecycleControlTokenSha256 = ComposeDeployment.ComputeSha256(lifecycleControlToken),
                PreviousComposeModelSha256 = manifest.ComposeModelSha256,
                Resources = candidateResources,
                LastLifecycleOperationId = operation.OperationId,
                UpdatedUtc = DateTimeOffset.UtcNow,
                // The databases as the now-previous image left them, which a rollback to that image puts back. A pure
                // swap has none, and its rollback is a swap too.
                PreviousStateSnapshot = operation.StateSnapshot
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
            operation = await RecordAsync(paths, operation with
            {
                Phase = LifecycleOperationPhase.Committed,
                RestoreResumeCommandId = retriedDrain is not null ? Guid.NewGuid() : operation.RestoreResumeCommandId
            }, cancellationToken)
                .ConfigureAwait(false);
            // The retained release record follows the image the instance actually runs. It is settled as soon as the
            // commit is durable and before the final resume, so a lost resume acknowledgement cannot separate the
            // two; the committed-resume branch above settles it again on --resume.
            await SettleReleaseRecordAsync(paths, signedImage, candidate, cancellationToken).ConfigureAwait(false);
            if (request.LeavePaused)
                return await SettlePausedUpgradeAsync(request, paths, committed, operation, signedImage!.VerifiedRelease, candidateLifecycle,
                    lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            if (IsCommittedSignedRetry(operation))
                return await SettleCommittedSignedRetryAsync(request, paths, committed, operation, signedImage!.VerifiedRelease,
                    candidateLifecycle, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            await candidateLifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, cancellationToken).ConfigureAwait(false);
            // The install record follows the committed records; settled after capture resumes, so a record that cannot be
            // written never holds capture paused, and before completion, so --resume finishes it.
            await SettleInstallRecordAsync(request, paths, committed, operation.Kind, cancellationToken).ConfigureAwait(false);
            operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result(operation.Kind, "completed", operation.OperationId, paths, committed, manifest.DockerDaemon, true, true);
        }
        catch (Exception exception)
        {
            if (operation.Phase == LifecycleOperationPhase.Committed)
            {
                if (IsCommittedSignedRetry(operation))
                    throw new InstallerException("The signed retry committed and settlement remains pending. Rerun the same command with --resume.", exception);
                throw new InstallerException(
                    request.LeavePaused
                        ? "The leave-paused image committed but settlement is incomplete; retry with the same --resume --leave-paused request and operation ID."
                        : "The image lifecycle commit succeeded but final resume was not acknowledged; rerun the same command with --resume.",
                    exception);
            }
            if (request.LeavePaused)
            {
                if (IsArmedPausedRetry(operation))
                {
                    if (exception is OperationCanceledException) throw;
                    throw new InstallerException(
                        "Leave-paused preparation stopped before the predecessor stop. The exact retry must verify the predecessor and original pause again.",
                        exception);
                }
                await RecoverPausedUpgradeFailureAsync(
                    request, paths, manifest, installationResult, operation, docker, compose, candidateCompose,
                    owner, lifecycleClientFactory, lifecycleControlToken, verificationToken,
                    hostLimits, operationRoot, error, uid, gid, exception).ConfigureAwait(false);
                if (exception is OperationCanceledException) throw;
                throw new InstallerException("The leave-paused retry failed. The verified predecessor is healthy with capture admission preserved.", exception);
            }
            if (mutationStarted || drainAttempted)
            {
                using var recovery = new CancellationTokenSource(ImageRecoveryPreparationTimeout);
                try
                {
                    var failedPhase = operation.Phase;
                    operation = await RecordAsync(paths, operation with
                    {
                        SchemaVersion = automaticSignedRecovery ? SignedRecoveryJournalVersion : operation.SchemaVersion,
                        Phase = LifecycleOperationPhase.Restoring,
                        FailureCode = automaticSignedRecovery ? SignedAutomaticRecoveryPreparing : "lifecycle-failed",
                        FailureMessage = Redaction.SafeDiagnostic(exception.Message)
                    }, recovery.Token)
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
                    var signedCheckpoint = automaticSignedRecovery ? UnrestoredSnapshot(operation) : null;
                    if (signedCheckpoint is not null)
                        await ProveStoppedSignedRestoreAsync(paths, manifest, operation, signedCheckpoint, docker, compose,
                            operationRoot, recovery.Token).ConfigureAwait(false);
                    else if (automaticSignedRecovery)
                    {
                        var previous = compose with { ComposeFile = previousComposePath, EnvironmentFile = previousEnvironmentPath };
                        await ValidateComposeAuthorityAsync(docker, previous, manifest, recovery.Token).ConfigureAwait(false);
                        await StopSignedCandidateWithoutCheckpointAsync(paths, manifest, operation, docker, previous, recovery.Token)
                            .ConfigureAwait(false);
                    }
                    if (mutationStarted)
                    {
                        SafeFileSystem.WriteTextAtomic(compose.ComposeFile, originalCompose);
                        SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, originalEnvironment);
                        SafeFileSystem.FlushDirectoryChain(Path.GetDirectoryName(compose.ComposeFile)!, paths.ProductRoot);
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
                        // The restored rollback records, deletions included, are durable before the journal settles.
                        if (Directory.Exists(rollbackRoot)) SafeFileSystem.FlushDirectoryChain(rollbackRoot, paths.ProductRoot);
                    }
                    // The snapshot this recovery restores is authenticated before any container is stopped, so a
                    // damaged snapshot leaves whatever runs in place rather than an instance with nothing running.
                    if (signedCheckpoint is null && UnrestoredSnapshot(operation) is { } pending)
                    {
                        await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, pending, recovery.Token).ConfigureAwait(false);
                    }
                    EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(recovery.Token).ConfigureAwait(false));
                    if (!automaticSignedRecovery && failedPhase is (LifecycleOperationPhase.Mutating or LifecycleOperationPhase.CandidateVerified))
                    {
                        await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                            ["stop"], recovery.Token).ConfigureAwait(false);
                        OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
                    }
                    // Every phase that recorded a snapshot ran with the original stopped, and the candidate, if it ran,
                    // was stopped above, so the stores are put back before the original image starts on them.
                    if (operation.StateSnapshot is { } taken && operation.StateSnapshotRestoredUtc is null)
                    {
                        if (signedCheckpoint is not null) OwnerRecoverySocket.RemoveStoppedSocket(paths, uid, gid);
                        operation = await RestoreStateSnapshotAsync(
                            paths, manifest, operation, taken, request.Json, error, recovery.Token,
                            signedStoppedProof: automaticSignedRecovery).ConfigureAwait(false);
                    }
                    // The predecessor can accept owner commands as soon as it starts. Record the disposition before
                    // the start so a crash can only replay this recovery, even if another owner command wins.
                    if (automaticSignedRecovery)
                        operation = await RecordAsync(paths, operation with { FailureCode = SignedAutomaticRecoveryPending },
                            recovery.Token).ConfigureAwait(false);
                    await StartOnThisHostAsync(docker, compose, hostLimits, operationRoot, recovery.Token).ConfigureAwait(false);
                    // Restoring the checkpoint and starting the predecessor can spend most of the preparation budget.
                    // Allow full semantic startup, protected proof, Docker verification and admission settlement after it.
                    using var settlement = new CancellationTokenSource(ImageRecoverySettlementTimeout);
                    var restoredOwnerState = await VerifyCandidateAsync(
                        owner, docker, compose, paths, manifest, installationResult, manifest.Image,
                        verificationToken, uid, gid, settlement.Token, expectedOwnerState, allowOwnerStateProgression,
                        allowQuarantinedReadiness: request.AllowQuarantinedReadiness, readinessTimeout: ImageChangeReadinessTimeout)
                        .ConfigureAwait(false);
                    if (restoredOwnerState != expectedOwnerState)
                    {
                        expectedOwnerState = restoredOwnerState;
                        allowOwnerStateProgression = allowLegacyOwnerStateProgression ||
                                                     expectedOwnerState == "owner-password-change-required";
                        operation = await RecordAsync(
                            paths,
                            operation with { ExpectedOwnerBootstrapState = expectedOwnerState },
                            settlement.Token).ConfigureAwait(false);
                    }
                    if (automaticSignedRecovery)
                    {
                        operation = await SettleSignedAutomaticRecoveryAsync(paths, operation, lifecycle, lifecycleControlToken,
                            settlement.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await lifecycle.ResumeAsync(operation.OperationId, lifecycleControlToken, settlement.Token).ConfigureAwait(false);
                        operation = operation with { Phase = LifecycleOperationPhase.Prepared, MutationStarted = false };
                    }
                }
                catch (Exception recoveryException)
                {
                    if (recoveryException is InstallerException { IsSignedRecoveryHeld: true })
                        throw new InstallerException($"Candidate failed. {Redaction.SafeDiagnostic(recoveryException.Message)}", exception);
                    if (IsPendingSignedAutomaticRecovery(operation))
                        throw new InstallerException(
                            $"Candidate failed. Automatic signed predecessor recovery remains pending: {Redaction.SafeDiagnostic(recoveryException.Message)}",
                            exception);
                    throw new InstallerException(
                        $"Candidate failed and exact rollback also failed: {Redaction.SafeDiagnostic(recoveryException.Message)}", exception);
                }
            }
            if (!IsRestored(operation) && !IsAdmissionPreserved(operation))
                await FailAsync(paths, operation, exception, CancellationToken.None).ConfigureAwait(false);
            if (exception is OperationCanceledException) throw;
            if (IsAdmissionPreserved(operation))
            {
                var state = operation.PostMutationContinuity!.CaptureState;
                var reason = operation.FailureCode == RecoveryAdmissionUnsupported
                    ? " The predecessor lacks authenticated atomic resume."
                    : string.Empty;
                throw new InstallerException(
                    $"The signed upgrade failed. The verified predecessor is healthy with capture admission in the {state} state.{reason} Use its ordinary owner controls for any admission change.",
                    exception);
            }
            throw new InstallerException("The image lifecycle operation failed; the retained journal and diagnostics identify the recovery boundary.", exception);
        }
    }

    private static async Task<(LifecycleStateSnapshot? Snapshot, CameraAgentStateDatabaseView? Databases)> ResolveRetainedSnapshotAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        LifecycleOperationState operation,
        LifecycleStateSnapshot previousState,
        ImageInstallationIdentity rollbackImage,
        CancellationToken cancellationToken)
    {
        var retained = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, previousState, cancellationToken)
            .ConfigureAwait(false);
        if (retained.Image != rollbackImage)
            throw new InstallerException("The retained database snapshot was not taken under the rollback image.");
        if (operation.RetainedStateSnapshot is not null && operation.RetainedStateSnapshot != previousState)
            throw new InstallerException("The retained image operation names a different database snapshot.");
        return (previousState, CameraAgentStateSnapshot.PreflightView(paths, previousState, retained));
    }

    /// <summary>
    /// Puts an operation's own database snapshot back, after saying so, and records that it did before anything
    /// starts on the restored stores. A repeat after a crash before the record restores the same bytes again; a
    /// repeat after it never restores over state the original image has since written.
    /// </summary>
    private static async Task<LifecycleOperationState> RestoreStateSnapshotAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        LifecycleOperationState operation,
        LifecycleStateSnapshot snapshot,
        bool json,
        TextWriter error,
        CancellationToken cancellationToken,
        bool signedStoppedProof = false)
    {
        await CameraAgentStateSnapshot.RecoverLegacyBoundaryAuthorityAsync(paths, manifest.InstanceId,
            operation, snapshot, cancellationToken).ConfigureAwait(false);
        await CameraAgentStateSnapshot.ShowWarningAsync(snapshot, json, error).ConfigureAwait(false);
        operation = await RestoreStoresAsync(paths, manifest, operation, snapshot, json, error, cancellationToken, signedStoppedProof)
            .ConfigureAwait(false);
        return await RecordAsync(paths, operation with
        {
            StateSnapshotRestoredUtc = DateTimeOffset.UtcNow,
            AuthorizedSignedRestoreSnapshotId = null
        }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Records the restore plan, carries what can be carried, and puts every store of <paramref name="snapshot"/> back.
    /// Each carry is planned and proved before any store is replaced, and one that still fails leaves its store as the
    /// snapshot recorded it; either way standard error names what was not carried and why, and the restore completes.
    /// </summary>
    private static async Task<LifecycleOperationState> RestoreStoresAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        LifecycleOperationState operation,
        LifecycleStateSnapshot snapshot,
        bool json,
        TextWriter error,
        CancellationToken cancellationToken,
        bool signedStoppedProof = false)
    {
        (operation, var carry) = await RecordRestorePlanAsync(paths, manifest, operation, snapshot, json, error, cancellationToken, signedStoppedProof)
            .ConfigureAwait(false);
        var uncarried = await CameraAgentStateSnapshot.RestoreAsync(
                paths, manifest.InstanceId, snapshot, manifest.RuntimeUid, manifest.RuntimeGid,
                carry.Floors, operation.StateRestorePlan!, cancellationToken)
            .ConfigureAwait(false);
        if (uncarried.Count > 0)
            await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(snapshot, uncarried, json, error).ConfigureAwait(false);
        return operation;
    }

    /// <summary>
    /// Records, before any store is replaced, what the restore of <paramref name="snapshot"/> must preserve and cover:
    /// <list type="bullet">
    /// <item>the highest value each monotonic counter has reached, the higher of what the journal already holds and what
    /// the live stores hold now, so a restore repeated after a crash, when the live stores may already be the rewound
    /// snapshot, still raises the counters to the true marks;</item>
    /// <item>the plan: every store location the configured inventory lists now, together with those any earlier attempt
    /// recorded (the inventory reads schedule revisions from the journal the restore replaces) and, when the target is
    /// another snapshot, those the operation's own snapshot recorded; and the target snapshot's own locations.</item>
    /// </list>
    /// A candidate that damaged a store beyond reading must not block its own recovery, so the inventory then falls back
    /// to the configured roots, the damaged store's counters are carried only as far as the journal recorded them, and
    /// the restore says so on standard error. The journal keeps the marks themselves; what is carried is planned from
    /// them against the target snapshot on every attempt, so a mark a retry can no longer carry never fails the retry.
    /// </summary>
    private static async Task<(LifecycleOperationState Operation, CameraAgentStateCounters.CarryPlan Carry)> RecordRestorePlanAsync(
        InstallationPaths paths,
        InstanceManifest manifest,
        LifecycleOperationState operation,
        LifecycleStateSnapshot snapshot,
        bool json,
        TextWriter error,
        CancellationToken cancellationToken,
        bool signedStoppedProof = false)
    {
        var target = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, snapshot, cancellationToken)
            .ConfigureAwait(false);
        if (signedStoppedProof && operation.AuthorizedSignedRestoreSnapshotId == snapshot.SnapshotId)
        {
            VerifyCompletedRestorePlan(snapshot, target, operation.StateRestorePlan);
            return (operation, await PlanCarryAsync(operation.StateCounterFloors ?? []).ConfigureAwait(false));
        }
        var incomplete = new List<string>();
        var (inventory, unreadInventory) = CameraAgentStateInventory.StoresForRestore(paths, manifest.ReplayProfile);
        if (unreadInventory is not null) incomplete.Add(unreadInventory);
        IEnumerable<string> current = (operation.StateRestorePlan?.CurrentLocations ?? []).Concat(inventory);
        if (operation.StateSnapshot is { } own && own != snapshot)
        {
            var ownManifest = await CameraAgentStateSnapshot.VerifyAsync(paths, manifest.InstanceId, own, cancellationToken)
                .ConfigureAwait(false);
            current = current.Concat(ownManifest.Databases.Select(static database => database.RelativePath));
        }
        var plan = await CameraAgentStateSnapshot.PlanRestoreAsync(paths, manifest.InstanceId, snapshot, current, cancellationToken)
            .ConfigureAwait(false);
        var (observed, unreadable) = CameraAgentStateCounters.ReadReadable(paths.StateRoot);
        incomplete.AddRange(unreadable.Select(static entry => $"the counters of '{entry.Store}' ({CameraAgentStateDiagnostics.Reason(entry.Reason)})"));
        if (incomplete.Count > 0)
        {
            await CameraAgentStateSnapshot.ShowRestoreIncompleteAsync(snapshot, string.Join("; ", incomplete), json, error)
                .ConfigureAwait(false);
        }
        var floors = CameraAgentStateCounters.TrimCarries(
            CameraAgentStateCounters.Merge(operation.StateCounterFloors, observed),
            CameraAgentStateSnapshot.StoreFile(paths, snapshot, target, CameraAgentStateLayout.AutomationStore));
        if (signedStoppedProof)
            operation = operation with
            {
                SchemaVersion = SignedRecoveryJournalVersion,
                Phase = LifecycleOperationPhase.Restoring,
                FailureCode = SignedAutomaticRecoveryPreparing,
                AuthorizedSignedRestoreSnapshotId = snapshot.SnapshotId
            };
        operation = await RecordAsync(paths, operation with
        {
            StateCounterFloors = floors,
            StateRestorePlan = plan
        }, cancellationToken).ConfigureAwait(false);
        return (operation, await PlanCarryAsync(floors).ConfigureAwait(false));

        async Task<CameraAgentStateCounters.CarryPlan> PlanCarryAsync(IReadOnlyList<StateCounterFloor> marks)
        {
            var carry = CameraAgentStateCounters.PlanCarry(
                marks,
                relative => CameraAgentStateSnapshot.StoreFile(paths, snapshot, target, relative),
                Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"), "carry-trial"),
                target.Databases.Sum(static database => database.Length));
            if (carry.Uncarried.Count > 0)
                await CameraAgentStateSnapshot.ShowRestoreUncarriedAsync(snapshot, carry.Uncarried, json, error).ConfigureAwait(false);
            return carry;
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
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Uninstall, manifest, cancellationToken, docker)
            .ConfigureAwait(false);
        if (request.DryRun)
        {
            var planned = Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, null, null);
            return planned with { Plan = UninstallPlan(request, paths, manifest) };
        }
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
        await PinCatalogsAsync(paths, manifest, operation.OperationId, cancellationToken)
            .ConfigureAwait(false);
        await SafeFileSystem.WriteJsonAtomicAsync(paths.ManifestPath, committed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken)
            .ConfigureAwait(false);
        operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
            .ConfigureAwait(false);
        await SettleInstallRecordAsync(request, paths, committed, operation.Kind, cancellationToken).ConfigureAwait(false);
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
                await SettleInstallRecordAsync(request, paths, manifest, retained.Kind, cancellationToken).ConfigureAwait(false);
                await CompleteAsync(paths, retained, cancellationToken).ConfigureAwait(false);
                return Result(retained.Kind, "completed", retained.OperationId, paths, manifest, daemon, true, true);
            }
            throw InstallerException.Refused("Reinstall requires a preserved uninstalled instance.");
        }
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Reinstall, manifest, cancellationToken, docker)
            .ConfigureAwait(false);
        if (request.DryRun)
        {
            var planned = Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, false, false);
            return planned with { Plan = ReinstallPlan(request, paths, manifest) };
        }
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
        await SettleInstallRecordAsync(request, paths, committed, operation.Kind, cancellationToken).ConfigureAwait(false);
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
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        if (request.ConfirmRemovedOperationId is not null)
            throw InstallerException.Refused("The instance root exists, so it was not removed; purge refuses --confirm-removed-operation-id.");
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Uninstalled)
            throw InstallerException.Refused("Purge requires a completed preserve-by-default uninstall.");
        var runtime = await docker.InspectContainerAsync(compose.ContainerName, cancellationToken).ConfigureAwait(false);
        if (runtime.Exists) throw InstallerException.Refused("Purge refused because the instance container still exists.");
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken)
            .ConfigureAwait(false);
        var operation = await BeginAsync(request, paths, LifecycleOperationKind.Purge, manifest, cancellationToken)
            .ConfigureAwait(false);
        if (request.DryRun)
        {
            var planned = Result(operation.Kind, "planned", operation.OperationId, paths, manifest, daemon, false, false);
            return planned with { Plan = PurgePlan(request, paths, manifest) };
        }
        // Deletion is committed only after the tree has been quarantined, so an instance root present beside a
        // committed purge is a different tree that this operation never authenticated.
        if (operation.Phase == LifecycleOperationPhase.Committed)
            throw new InstallerException("The retained purge already committed its deletion; it cannot adopt an instance root that reappeared.");
        await PinCatalogsAsync(paths, manifest, operation.OperationId, cancellationToken)
            .ConfigureAwait(false);
        var parent = Directory.GetParent(paths.InstanceRoot)?.FullName
            ?? throw new InstallerException("The instance root has no authenticated parent.");
        await PathPrograms.RetainForPurgeAsync(paths, request.ProductionProductRoot, operation.OperationId,
            cancellationToken).ConfigureAwait(false);
        var expectedParent = PrivilegedPurgeNamespace.NamespaceRoot(paths.ProductRoot);
        if (parent != expectedParent || new DirectoryInfo(paths.InstanceRoot).LinkTarget is not null)
            throw new InstallerException("The instance root path is not canonical.");
        var identity = NativeLinux.GetDirectoryIdentity(paths.InstanceRoot);
        if (identity.Uid != manifest.RuntimeUid || identity.Gid != manifest.RuntimeGid)
            throw new InstallerException("The instance root ownership changed before purge.");
        var childName = Path.GetFileName(paths.InstanceRoot);
        var inventory = SafeTreeDeletion.CaptureChildInventory(parent, childName, identity.Uid, identity.Gid);
        var treeIdentity = SafeTreeDeletion.ValidateChild(parent, childName, identity.Uid, identity.Gid);
        var tombstoneName = PrivilegedPurgeNamespace.TombstoneName(manifest.InstanceId, operation.OperationId);
        var tombstone = Path.Combine(parent, tombstoneName);
        var evidencePath = Path.Combine(paths.OperationsRoot, $"purge-{operation.OperationId:D}.evidence.json");
        UnixNodeIdentity namespaceIdentity;
        using (var namespaceRoot = NativeLinux.OpenDirectoryNoFollow(parent))
            namespaceIdentity = NativeLinux.GetOpenNodeIdentity(namespaceRoot, parent);
        if (operation.MutationStarted)
        {
            // A retry before quarantine continues under the evidence its first attempt recorded and never replaces it.
            await EnsureRetainedPurgeTreeAsync(
                    evidencePath, operation, paths, manifest, inventory, namespaceIdentity, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                evidencePath,
                new PurgeDeletionEvidence(
                    DeploymentSchemaVersions.LifecycleOperation, operation.OperationId, operation.RequestSha256,
                    LocalHostIdentity.ReadSha256(), manifest, inventory,
                    Namespace: PurgeNamespaceIdentity.From(namespaceIdentity)),
                DeploymentJsonContext.Default.PurgeDeletionEvidence, cancellationToken).ConfigureAwait(false);
        }
        operation = await RecordAsync(paths, operation with
        {
            Phase = LifecycleOperationPhase.Mutating,
            MutationStarted = true
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            // The runtime user cannot write the root-owned namespace, so the rename may be handed to the narrow,
            // identity-bound privileged helper; the journal already records the mutation, and --resume continues
            // from whichever side of the atomic rename an interruption leaves.
            await PrivilegedPurgeNamespace.QuarantineAsync(
                    paths.ProductRoot, manifest.InstanceId, operation.OperationId, treeIdentity, processRunner, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InstallerException exception)
        {
            throw new InstallerException(
                $"Purge stopped before its quarantine was confirmed; rerun the same command with --resume. {exception.Message}",
                exception);
        }
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
            var emptied = SafeTreeDeletion.EmptyChild(parent, tombstoneName, identity.Uid, identity.Gid, treeIdentity);
            await PrivilegedPurgeNamespace.RemoveTombstoneAsync(
                    paths.ProductRoot, manifest.InstanceId, operation.OperationId, emptied, processRunner, cancellationToken)
                .ConfigureAwait(false);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            throw new InstallerException(
                $"Purge stopped at its quarantined tombstone; rerun the same command with --resume. {SafeMessage(exception)}",
                exception);
        }
        await RemoveFromInstallRecordAsync(request, paths, manifest.InstanceId, cancellationToken).ConfigureAwait(false);
        await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        // Removal is last: after the last installer is gone, a crash must not leave an unfinished data deletion.
        await PathPrograms.RemoveAsync(paths, request.ProductionProductRoot, purge: true, cancellationToken,
            PathPrograms.PurgeRecord(paths, operation.OperationId)).ConfigureAwait(false);
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

    private static InstallationVerificationExpectation CurrentInstallationExpectation(
        InstanceManifest manifest, InstallationResult result)
        => new(
            manifest.ApplicationIdentity.ToString("D"), manifest.OwnerEmail, result.OwnerBootstrapState,
            manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
            manifest.DeploymentLocationId, manifest.DeploymentLocationVersion, manifest.DeploymentLocationSha256,
            manifest.ReplayProfile, manifest.Catalog, AllowCompletedPasswordReplacement: true,
            DeepSkyCatalog: manifest.DeepSkyCatalog);

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
        bool allowCompletedPasswordReplacement = true,
        bool allowQuarantinedReadiness = false,
        TimeSpan? readinessTimeout = null)
    {
        await owner.WaitForHealthAsync(
            ProductionCatalog.Get(manifest.Catalog.CatalogId), cancellationToken, timeout: readinessTimeout,
            allowQuarantinedReadiness: allowQuarantinedReadiness).ConfigureAwait(false);
        var verifiedOwnerState = await owner.VerifyInstallationAsync(
            verificationToken,
            new InstallationVerificationExpectation(
                manifest.ApplicationIdentity.ToString("D"), manifest.OwnerEmail, ownerBootstrapState ?? result.OwnerBootstrapState,
                manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
                manifest.DeploymentLocationId, manifest.DeploymentLocationVersion, manifest.DeploymentLocationSha256,
                manifest.ReplayProfile,
                manifest.Catalog,
                allowCompletedPasswordReplacement, manifest.DeepSkyCatalog),
            cancellationToken).ConfigureAwait(false);
        await docker.VerifyContainerAsync(compose, paths, image, uid, gid, cancellationToken, manifest.DeepSkyCatalog?.InstallRoot).ConfigureAwait(false);
        return verifiedOwnerState;
    }

    /// <summary>
    /// Makes the retained release record follow the image the instance now runs. An upgrade from a release writes the
    /// release's record for <paramref name="running"/>; any other image the instance was moved to gets the record kept
    /// for it (a rollback target restores its release's record), or none, and the outgoing image's record is kept beside
    /// it (<see cref="ReleaseRecords"/>).
    /// The step is idempotent and runs both when a commit first becomes durable and on the committed-resume branch, so
    /// an interruption between the commit and the record is repaired by <c>--resume</c> instead of leaving evidence
    /// that contradicts the container.
    /// </summary>
    private static Task SettleReleaseRecordAsync(
        InstallationPaths paths,
        AcquiredImage? releaseImage,
        ImageInstallationIdentity running,
        CancellationToken cancellationToken)
        => ReleaseRecords.SettleAsync(paths, releaseImage, running, cancellationToken);

    /// <summary>
    /// Requires a release's image to be the release's and one this instance can run before the upgrade's journal entry,
    /// so a refused release leaves the instance, its journal and Docker as they were. An offline archive's image, layers
    /// included, was read from the archive at acquisition; a load Docker still rejects after the journal entry is settled
    /// as a terminal refusal, which blocks no later upgrade. A registry image's metadata is read from the registry by the
    /// digest release.json names, and checked, and nothing is pulled (<see cref="DockerClient.ReadRegistryReleaseImageAsync"/>);
    /// a plan reads and checks it the same way. The pull comes once every other check that only reads has passed
    /// (<see cref="PullReleaseCandidateAsync"/>), and the same checks run again on the image Docker resolves once the
    /// upgrade has begun. Returns the release, with the identities its registry gave and the image it read there.
    /// </summary>
    private static async Task<AcquiredImage> CheckReleaseCandidateAsync(
        InstanceManifest manifest,
        DockerClient docker,
        AcquiredImage release,
        CancellationToken cancellationToken)
    {
        if (release.ArchivedImage is { } archived)
        {
            ValidateReleaseCandidate(manifest, archived);
            return release;
        }
        // Reading the registry pulls nothing, so a plan reads and checks it too, as the command does (#1315).
        var (registryImage, selection) = await docker.ReadRegistryReleaseImageAsync(release.Image, cancellationToken)
            .ConfigureAwait(false);
        ValidateReleaseCandidate(manifest, registryImage);
        return release with { Image = selection, RegistryImage = registryImage };
    }

    /// <summary>
    /// Pulls a registry release's image, checked by <see cref="CheckReleaseCandidateAsync"/>, and checks what Docker then
    /// holds again. The pull is removed again if it is refused, if it fails or if the upgrade is cancelled. Returns the
    /// names it added, so a later refusal that still changes nothing can remove them too.
    /// </summary>
    private static async Task<IReadOnlyList<string>> PullReleaseCandidateAsync(
        LifecycleRequest request,
        InstanceManifest manifest,
        DockerClient docker,
        AcquiredImage release,
        CancellationToken cancellationToken)
        => (await docker.ImportReleaseImageAsync(
                    ImageRequest(manifest, release.ImageReference, null, null, request.NoDownload), release.Image,
                    candidate => ValidateReleaseCandidate(manifest, candidate), cancellationToken)
                .ConfigureAwait(false)).Added;

    /// <summary>
    /// Whatever this refuses has changed nothing: the candidate is not imported yet, or the import that added it is
    /// removed again, so it is a refusal (exit 3, #1315).
    /// </summary>
    private static void ValidateReleaseCandidate(InstanceManifest manifest, ImageInstallationIdentity candidate)
    {
        try
        {
            if (!IsCanonicalImage(
                    candidate,
                    manifest.ComponentSchemaVersion,
                    manifest.DockerDaemon.Architecture,
                    manifest.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner))
            {
                throw new InstallerException("The candidate image does not declare the required CameraAgent configuration and catalog contracts.");
            }
            CameraAgentInstaller.EnsureImageSupportsCatalog(candidate, manifest.Catalog.CatalogId);
            if (manifest.DeepSkyCatalog is { } deepSky) CameraAgentInstaller.EnsureImageSupportsCatalog(candidate, deepSky.CatalogId);
            if (!CameraAgentStateContract.IsCurrent(candidate.UpgradeCompatibility))
            {
                throw new InstallerException(
                    $"The candidate image declares state compatibility '{CameraAgentStateContract.Describe(candidate.UpgradeCompatibility)}' instead of the supported '{CameraAgentStateContract.Current}' contract; an explicit state-disposition procedure is required.");
            }
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            throw InstallerException.Refused(exception.Message, exception);
        }
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

    /// <summary>
    /// The failure code of a lifecycle operation refused before it mutated anything. Such a record is terminal: it
    /// never blocks a later operation and cannot be resumed, because there is nothing to recover.
    /// </summary>
    internal const string RefusedFailureCode = "lifecycle-refused";

    internal static async Task<LifecycleOperationState> BeginAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        LifecycleOperationKind kind,
        InstanceManifest manifest,
        CancellationToken cancellationToken,
        DockerClient? docker = null)
    {
        await EnsureControlSettledAsync(paths, cancellationToken, kind, docker).ConfigureAwait(false);
        // A plan meets the same journal gates as the command, and writes nothing (#1315).
        var resumed = await AdmitAsync(request, paths, kind, manifest, cancellationToken).ConfigureAwait(false);
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
                OriginalCatalog: manifest.Catalog, OriginalDeepSkyCatalog: manifest.DeepSkyCatalog);
        }
        if (resumed is not null) return resumed;
        var state = new LifecycleOperationState(
            DeploymentSchemaVersions.LifecycleOperation,
            Guid.NewGuid(),
            kind,
            manifest.InstanceId,
            request.ComputeRequestSha256(),
            LifecycleOperationPhase.Prepared,
            InstallationStatus.Running,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            manifest.Image,
            OriginalCatalog: manifest.Catalog, OriginalDeepSkyCatalog: manifest.DeepSkyCatalog);
        return await RecordAsync(paths, state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The journal gates every lifecycle command and its plan pass before anything changes, reading the journal only.
    /// Returns the retained operation a <c>--resume</c> continues or <c>--abort</c> settles, or <see langword="null"/>
    /// for a fresh operation.
    /// </summary>
    private static async Task<LifecycleOperationState?> AdmitAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        LifecycleOperationKind kind,
        InstanceManifest manifest,
        CancellationToken cancellationToken)
    {
        var existing = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        if (existing?.FailureCode == SignedAutomaticRestoreHeld)
            throw InstallerException.Refused("Automatic signed recovery is held. Preserve the retained stores and checkpoints.");
        if (existing?.SchemaVersion == LeavePausedJournalVersion && !request.LeavePaused && !IsAdmissionPreserved(existing))
            throw InstallerException.Refused("The retained upgrade requires the original --resume --leave-paused request.");
        if (request.LeavePaused)
        {
            if (existing is not null && IsPendingSignedAutomaticRecovery(existing))
                throw InstallerException.Refused("The retained automatic recovery requires the exact ordinary signed upgrade request.");
            if (existing is null || existing.Kind != LifecycleOperationKind.Upgrade || existing.InstanceId != manifest.InstanceId ||
                existing.OperationId != request.RecoveryOperationId || existing.RequestSha256 != request.ComputeRequestSha256())
                throw InstallerException.Refused("Leave-paused requires the exact retained upgrade operation and original request.");
            if (existing.Phase == LifecycleOperationPhase.Completed || IsArmedPausedRetry(existing) || IsQuietPausedRecovery(existing) ||
                IsAwaitingPausedRecovery(existing) || IsLeavePausedAdmissionPreserved(existing) ||
                existing.SchemaVersion == LeavePausedJournalVersion && existing.Phase == LifecycleOperationPhase.Committed ||
                existing.SchemaVersion == DeploymentSchemaVersions.LifecycleOperation && existing is
                {
                    Phase: LifecycleOperationPhase.Restoring, Status: InstallationStatus.Running,
                    MutationStarted: true, StateSnapshotRestoredUtc: not null,
                    StateSnapshot: not null, RestoreResumeCommandId: null
                })
                return existing;
            if (existing.SchemaVersion == LeavePausedJournalVersion)
                throw InstallerException.Refused("Leave-paused has a held precommit recovery. Retain the stores and both checkpoints.");
            throw InstallerException.Refused("Leave-paused requires an already-restored interrupted upgrade or its own committed retry.");
        }
        if (request is { Resume: true, Release: not null } && kind == LifecycleOperationKind.Upgrade &&
            existing is
            {
                Kind: LifecycleOperationKind.Upgrade, SchemaVersion: DeploymentSchemaVersions.LifecycleOperation,
                Phase: LifecycleOperationPhase.Restoring, MutationStarted: true, StateSnapshotRestoredUtc: not null,
                SignedReleaseChecksumsSha256: null, SignedRecoveryPauseCommandId: null
            })
            throw InstallerException.Refused("The legacy signed journal has no installer-owned retry pause. Use the exact signed restore-only or leave-paused recovery request.");
        // Abort settles the retained journal itself, but admission still needs its recorded restart boundary.
        if (request.Abort)
        {
            if (kind != LifecycleOperationKind.Backup)
            {
                throw new InstallUsageException("--abort applies only to cameraagent backup.");
            }

            return BackupToAbort(request, manifest, existing);
        }
        var hash = request.ComputeRequestSha256();
        // A refused operation is terminal: it mutated nothing, so it neither blocks a new operation nor offers
        // anything to resume. Any other incomplete record whose mutation flag is clear is unmutated by construction
        // (the flag is recorded before the first mutation and cleared only after a completed restore), including a
        // pre-mutation refusal journalled by an earlier release or an interrupted, cancelled, or crashed
        // preparation: a fresh operation of any kind may supersede it, while --resume of the matching request still
        // continues it. Only an operation that started mutating demands --resume.
        if (existing?.RestoreResumeCommandId is not null && !IsRestored(existing) &&
            !IsPendingSignedAutomaticRecovery(existing) && !IsCommittedSignedRetry(existing))
            throw InstallerException.Refused("Restore-only recovery has started; resume it with --restore-only and the same --operation-id.");
        if (existing is { Status: not InstallationStatus.Completed } && !IsRefused(existing) && !IsRestored(existing) &&
            !IsAdmissionPreserved(existing) &&
            !IsAbandonedBackup(existing) &&
            (existing.MutationStarted || request.Resume))
        {
            if (!request.Resume) throw InstallerException.Refused("An incomplete lifecycle operation exists; rerun the same command with --resume.");
            if (existing.Kind != kind || existing.InstanceId != manifest.InstanceId || existing.RequestSha256 != hash)
                throw InstallerException.Refused("The retained lifecycle operation belongs to different immutable inputs.");
            var transitionAtCommitBoundary = IsPendingSignedAutomaticRecovery(existing) || existing.Phase is
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
        if (request.Resume) throw InstallerException.Refused("No incomplete matching lifecycle operation exists.");
        return null;
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

    internal static bool IsRefused(LifecycleOperationState state)
        => state is { Status: InstallationStatus.Failed, FailureCode: RefusedFailureCode, MutationStarted: false };

    /// <summary>
    /// A backup the operator aborted, or a resume that found the recovery had already discarded the partial. It no
    /// longer blocks another command and there is nothing for <c>--resume</c> to finish.
    /// </summary>
    private static bool IsAbandonedBackup(LifecycleOperationState state)
        => state is
        {
            Kind: LifecycleOperationKind.Backup,
            Status: InstallationStatus.Failed,
            MutationStarted: false,
            FailureCode: AbandonedBackupCode or MissingPartialBackupCode
        };

    internal const string AbandonedBackupCode = "backup-aborted";
    internal const string MissingPartialBackupCode = "backup-partial-missing";

    private static bool IsRestored(LifecycleOperationState state)
        => state is { Status: InstallationStatus.Failed, Phase: LifecycleOperationPhase.Restored, MutationStarted: false };

    /// <summary>
    /// Records an operation that failed before its mutation record as refused. Both the operation as
    /// <see cref="BeginAsync"/> returned it and the retained journal must be unmutated: a resumed operation that had
    /// mutated, was restored, and then failed again keeps its recovery journal, because the journal's current flag
    /// is not a history of mutation. The retained journal decides the rest: only the same operation, still running,
    /// is settled, so an already-failed one is left alone. Settlement is best-effort: its own failure leaves the
    /// journal as it was and never replaces the refusal the caller is about to rethrow.
    /// </summary>
    internal static async Task SettleRefusedOperationAsync(
        InstallationPaths paths,
        LifecycleOperationState begun,
        Exception exception)
    {
        if (begun.MutationStarted) return;
        try
        {
            var retained = await ReadOperationAsync(paths.LifecycleStatePath, CancellationToken.None).ConfigureAwait(false);
            if (retained is null || retained.OperationId != begun.OperationId || retained.MutationStarted ||
                retained.Status != InstallationStatus.Running)
            {
                return;
            }
            await RecordAsync(paths, retained with
            {
                Status = InstallationStatus.Failed,
                FailureCode = RefusedFailureCode,
                FailureMessage = Redaction.SafeDiagnostic(exception.Message)
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception settlement) when (settlement is not OutOfMemoryException)
        {
            // The unmutated journal stays as it was; the original refusal remains the diagnostic the operator sees.
        }
    }

    /// <summary>
    /// Runs after every lifecycle journal record is durable. Crash-injection tests capture what a power loss at that
    /// moment would leave behind; production leaves it empty.
    /// </summary>
    internal static Action<LifecycleOperationState> JournalRecorded { get; set; } = static _ => { };

    internal static async Task<LifecycleOperationState> RecordAsync(
        InstallationPaths paths,
        LifecycleOperationState state,
        CancellationToken cancellationToken)
    {
        state = state with { UpdatedUtc = DateTimeOffset.UtcNow };
        // Never replace resumable intent with a terminal record the reader would reject.
        if (state.Phase == LifecycleOperationPhase.Restored || state.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation)
            ValidateOperationState(state);
        await SafeFileSystem.WriteJsonAtomicAsync(
            paths.LifecycleStatePath, state, DeploymentJsonContext.Default.LifecycleOperationState, cancellationToken)
            .ConfigureAwait(false);
        JournalRecorded(state);
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
        ValidateOperationState(value);
        return value;
    }

    private static void ValidateOperationState(LifecycleOperationState value)
    {
        if (!(ValidLifecycleJournalVersion(value) || IsValidSignedRecoveryJournal(value)) || value.OperationId == Guid.Empty ||
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
            value.OriginalDeepSkyCatalog is not null && !IsValidCatalog(value.OriginalDeepSkyCatalog) ||
            value.CandidateDeepSkyCatalog is not null && !IsValidCatalog(value.CandidateDeepSkyCatalog) ||
            value.DeepSkyTransition && value.Kind is not (LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback) ||
            value.Phase == LifecycleOperationPhase.Planned ||
            value.Status == InstallationStatus.Completed && value.Phase != LifecycleOperationPhase.Completed ||
            value.Phase == LifecycleOperationPhase.Completed && value.Status != InstallationStatus.Completed ||
            value.Status == InstallationStatus.Completed && value.MutationStarted ||
            value.SignedReleaseChecksumsSha256 is { } checksums && (value.Kind != LifecycleOperationKind.Upgrade || !IsSha256(checksums)) ||
            value.SignedRecoveryPauseCommandId is { } pause &&
            (pause == Guid.Empty || value.SignedReleaseChecksumsSha256 is null ||
             value.PreMutationContinuity is not { CaptureState: "Paused" } || pause == value.RestoreResumeCommandId) ||
            value.RestoreResumeCommandId == Guid.Empty ||
            value.RestoreResumeCommandId is not null &&
            !IsCommittedSignedRetry(value) &&
            (value.Kind != LifecycleOperationKind.Upgrade || value.Phase is not (LifecycleOperationPhase.Restoring or LifecycleOperationPhase.Restored)) ||
            value.SignedRetryAdmissionOutcome is { } admission &&
            (admission is not ("completed" or SignedRetryAdmissionPreserved or SignedRetryAdmissionUnsupported) ||
             !IsCommittedSignedRetry(value) && value is not
             {
                 Kind: LifecycleOperationKind.Upgrade, Phase: LifecycleOperationPhase.Completed,
                 SignedReleaseChecksumsSha256: not null, SignedRecoveryPauseCommandId: not null, LeavePausedOriginalSnapshot: null
             }) ||
            value.Phase == LifecycleOperationPhase.Restored &&
            (!IsRestored(value) || value.RestoreResumeCommandId is null || value.PostMutationContinuity is null ||
             value.FailureCode is null || value.FailureMessage is null) ||
            value.RestoreResumeReceipt is not null && value.Phase != LifecycleOperationPhase.Restored ||
            value.AuthorizedSignedRestoreSnapshotId is { } authorized &&
            (value.SchemaVersion != SignedRecoveryJournalVersion || value.Phase != LifecycleOperationPhase.Restoring ||
             value.StateSnapshot?.SnapshotId != authorized || value.StateRestorePlan?.SnapshotId != authorized ||
             value.StateSnapshotRestoredUtc is not null || value.FailureCode is not (SignedAutomaticRecoveryPreparing or SignedAutomaticRestoreHeld)) ||
            value.Phase == LifecycleOperationPhase.RecoveryAdmissionPreserved &&
            (!IsAdmissionPreserved(value) || !IsValidRecoveryBoundary(value.PostMutationContinuity) ||
             value.PostMutationContinuity?.CaptureState == "PauseRequested" ||
             value.FailureCode is not (RecoveryAdmissionPreserved or RecoveryAdmissionUnsupported) || value.FailureMessage is null ||
             value.RestoreResumeCommandId is not null) ||
            value.FailureCode == RefusedFailureCode && (value.Status != InstallationStatus.Failed || value.MutationStarted))
        {
            throw new InstallerException("The retained lifecycle operation is invalid or unsupported.");
        }
        var imageTransition = value.Kind is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback;
        var catalogTransition = value.Kind is LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback;
        if (value.Status != InstallationStatus.Completed && !IsRestored(value) && !IsAdmissionPreserved(value) &&
            value.Phase >= LifecycleOperationPhase.Drained && !value.MutationStarted ||
            imageTransition && value.Phase >= LifecycleOperationPhase.CandidateValidated && value.CandidateImage is null ||
            catalogTransition && value.Phase >= LifecycleOperationPhase.CandidateValidated && value.CandidateCatalog is null ||
            value.Phase == LifecycleOperationPhase.Committed &&
            (value.Kind is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or
                LifecycleOperationKind.Reinstall or LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback) &&
            value.PostMutationContinuity is null)
        {
            throw new InstallerException("The retained lifecycle operation has an impossible phase state.");
        }
        if (value.Phase == LifecycleOperationPhase.Restored &&
            (value.PreMutationContinuity is not { CaptureState: "Paused" } before || !IsValidRecoveryBoundary(before) ||
             !IsValidRecoveryReceipt(before, value.RestoreResumeReceipt) ||
             !IsRecoveryAdmissionAfterResume(before, value.PostMutationContinuity!) ||
             value.RestoreResumeReceipt!.CompletedUtc > value.PostMutationContinuity!.RecordedUtc))
            throw new InstallerException("The restored lifecycle operation has inconsistent recovery evidence.");
    }

    /// <summary>The evidence settlement of an interrupted purge whose instance tree was already removed outside the CLI.</summary>
    internal const string PurgeAlreadyAbsentSettlement = "already-absent";

    private static async Task<LifecycleResult> ResumeInterruptedPurgeAsync(
        LifecycleRequest request,
        InstallationPaths paths,
        DockerClient docker,
        IProcessRunner processRunner,
        CancellationToken cancellationToken)
    {
        var operation = await ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false);
        var cleanupOnly = operation is
        {
            Kind: LifecycleOperationKind.Purge, Status: InstallationStatus.Completed,
            Phase: LifecycleOperationPhase.Completed, MutationStarted: false
        } &&
            File.Exists(PathPrograms.PurgeRecord(paths, operation.OperationId));
        if (operation is null || operation.Kind != LifecycleOperationKind.Purge ||
            !cleanupOnly && operation is not { Status: not InstallationStatus.Completed, MutationStarted: true } ||
            operation.InstanceId != request.InstanceId || operation.RequestSha256 != request.ComputeRequestSha256())
            throw new InstallerException("No matching interrupted purge operation exists.");
        if (request.ConfirmRemovedOperationId is { } confirmedOperationId && confirmedOperationId != operation.OperationId)
            throw new InstallerException("--confirm-removed-operation-id does not name the interrupted purge.");
        var parent = PrivilegedPurgeNamespace.NamespaceRoot(paths.ProductRoot);
        var instanceName = operation.InstanceId!.Value.ToString("D");
        var tombstoneName = PrivilegedPurgeNamespace.TombstoneName(operation.InstanceId.Value, operation.OperationId);
        var tombstone = Path.Combine(parent, tombstoneName);
        var evidencePath = Path.Combine(paths.OperationsRoot, $"purge-{operation.OperationId:D}.evidence.json");
        var evidence = await ReadPurgeEvidenceAsync(evidencePath, cancellationToken).ConfigureAwait(false);
        ValidateRetainedPurgeEvidence(evidence, operation, paths);
        var manifest = evidence.Manifest;
        paths = paths.WithCatalog(manifest.Catalog.CatalogId);
        var daemon = await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        EnsureDaemon(manifest.DockerDaemon, daemon);
        await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken, evidence.Tree).ConfigureAwait(false);
        await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, tombstone, cancellationToken, evidence.Tree).ConfigureAwait(false);
        if (cleanupOnly)
        {
            if (request.ConfirmRemovedOperationId is not null && evidence.Settlement != PurgeAlreadyAbsentSettlement ||
                NativeLinux.TryGetNodeIdentity(tombstone) is not null)
                throw InstallerException.Refused("A completed purge resumes only its retained program cleanup, without adopting another tree.");
            var cleanup = new LifecycleResult(
                DeploymentSchemaVersions.LifecycleOperation, operation.Kind, "completed", operation.OperationId,
                manifest.InstanceId, null, paths.ProductRoot, null, null, null, manifest.Catalog, manifest.PreviousCatalog,
                daemon, false, false, [paths.CatalogRoot, paths.OperationsRoot], null, DateTimeOffset.UtcNow);
            await PathPrograms.RemoveAsync(paths, request.ProductionProductRoot, purge: true, cancellationToken,
                PathPrograms.PurgeRecord(paths, operation.OperationId)).ConfigureAwait(false);
            return cleanup;
        }
        SafeFileSystem.EnsureSafeExistingAncestors(parent);
        bool tombstonePresent;
        using (var namespaceRoot = NativeLinux.OpenDirectoryNoFollow(parent))
            tombstonePresent = NativeLinux.TryGetNodeIdentityAt(namespaceRoot, tombstoneName) is not null;
        if (evidence.Settlement is not null && (tombstonePresent || operation.Phase != LifecycleOperationPhase.Mutating))
            throw new InstallerException("The purge deletion evidence records a settlement that contradicts the retained tree.");
        if (request.ConfirmRemovedOperationId is not null && (tombstonePresent || operation.Phase != LifecycleOperationPhase.Mutating))
            throw new InstallerException("The interrupted purge retained its tombstone or committed its deletion; it refuses --confirm-removed-operation-id.");
        var outcome = "completed";
        if (tombstonePresent)
        {
            var treeIdentity = SafeTreeDeletion.ValidateRemainingChild(
                parent, tombstoneName, manifest.RuntimeUid, manifest.RuntimeGid, evidence.Tree);
            EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, paths.InstanceRoot, cancellationToken, evidence.Tree).ConfigureAwait(false);
            await docker.EnsureNoInstanceReferencesAsync(manifest.InstanceId, tombstone, cancellationToken, evidence.Tree).ConfigureAwait(false);
            operation = await RecordAsync(paths, operation with { Phase = LifecycleOperationPhase.Committed }, cancellationToken)
                .ConfigureAwait(false);
            var emptied = SafeTreeDeletion.EmptyChild(
                parent, tombstoneName, manifest.RuntimeUid, manifest.RuntimeGid, treeIdentity);
            await PrivilegedPurgeNamespace.RemoveTombstoneAsync(
                    paths.ProductRoot, manifest.InstanceId, operation.OperationId, emptied, processRunner, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (operation.Phase != LifecycleOperationPhase.Committed)
        {
            await SettleAlreadyAbsentAsync(request, operation, evidence, evidencePath, parent, instanceName, cancellationToken)
                .ConfigureAwait(false);
            outcome = PurgeAlreadyAbsentSettlement;
        }
        else
        {
            // The committed tombstone is gone, but its removal is durable only once the namespace is flushed; a failed
            // flush leaves the journal resumable instead of completing over an entry a crash could restore.
            using var namespaceRoot = NativeLinux.OpenDirectoryNoFollow(parent);
            FlushNamespace(namespaceRoot);
        }
        EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        await RemoveFromInstallRecordAsync(request, paths, manifest.InstanceId, cancellationToken).ConfigureAwait(false);
        operation = await CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
        await PathPrograms.RemoveAsync(paths, request.ProductionProductRoot, purge: true, cancellationToken,
            PathPrograms.PurgeRecord(paths, operation.OperationId)).ConfigureAwait(false);
        return new LifecycleResult(
            DeploymentSchemaVersions.LifecycleOperation, operation.Kind, outcome, operation.OperationId,
            manifest.InstanceId, null, paths.ProductRoot, null, null, null, manifest.Catalog, manifest.PreviousCatalog,
            daemon, false, false, [paths.CatalogRoot, paths.OperationsRoot], null, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Settles an interrupted purge whose instance tree and tombstone were both removed outside the CLI after the
    /// purge recorded its deletion evidence. The CLI deleted nothing, so it records that truth rather than a deletion:
    /// the evidence gains an <see cref="PurgeAlreadyAbsentSettlement"/> settlement, and the result reports that
    /// outcome instead of <c>completed</c>. A missing name never proves removal, because the tree may have been moved
    /// rather than deleted, so settlement requires the operator's explicit disposition naming this operation. It also
    /// refuses whatever the CLI can prove is not a removal: a namespace that is not the evidenced directory or is on
    /// another device, any entry at the instance name, any other tombstone of the instance, and the evidenced tree
    /// under any other name in the namespace. The namespace is flushed before the settlement is recorded, so the
    /// recorded absence is durable. It creates and changes no path outside the operation's own evidence and journal.
    /// </summary>
    private static async Task SettleAlreadyAbsentAsync(
        LifecycleRequest request,
        LifecycleOperationState operation,
        PurgeDeletionEvidence evidence,
        string evidencePath,
        string parent,
        string instanceName,
        CancellationToken cancellationToken)
    {
        if (operation.Phase != LifecycleOperationPhase.Mutating)
            throw new InstallerException("The interrupted purge has an impossible phase for an already-absent tree.");
        var evidencedRoot = evidence.Tree.SingleOrDefault(static node => node.RelativePath == ".")
            ?? throw new InstallerException("The purge deletion evidence does not record its tree root.");
        using (var namespaceRoot = NativeLinux.OpenDirectoryNoFollow(parent))
        {
            var namespaceIdentity = NativeLinux.GetOpenNodeIdentity(namespaceRoot, parent);
            if (evidence.Namespace is { } evidencedNamespace && !evidencedNamespace.Matches(namespaceIdentity))
                throw new InstallerException("The instance namespace is not the directory that held the evidenced tree; purge resume will not settle against it.");
            if (namespaceIdentity.DeviceMajor != evidencedRoot.DeviceMajor || namespaceIdentity.DeviceMinor != evidencedRoot.DeviceMinor)
                throw new InstallerException("The instance namespace is not on the filesystem that held the evidenced tree; mount it before resuming.");
            if (NativeLinux.TryGetNodeIdentityAt(namespaceRoot, instanceName) is not null)
                throw new InstallerException("An entry occupies the purged instance name; purge resume will neither settle nor delete it.");
            var tombstonePrefix = $".purge-{instanceName}-";
            foreach (var name in Directory.EnumerateFileSystemEntries($"/proc/self/fd/{namespaceRoot.DangerousGetHandle().ToInt64()}")
                         .Select(Path.GetFileName)
                         .Where(static name => !string.IsNullOrEmpty(name)))
            {
                if (name!.StartsWith(tombstonePrefix, StringComparison.Ordinal))
                    throw new InstallerException("Another purge tombstone of this instance exists; purge resume will not settle around it.");
                if (NativeLinux.TryGetNodeIdentityAt(namespaceRoot, name) is { } entry && entry.Inode == evidencedRoot.Inode &&
                    entry.DeviceMajor == evidencedRoot.DeviceMajor && entry.DeviceMinor == evidencedRoot.DeviceMinor)
                {
                    throw new InstallerException("The evidenced instance tree still exists in its namespace under another name; purge resume will neither settle nor delete it.");
                }
            }
            if (evidence.Settlement is null && request.ConfirmRemovedOperationId != operation.OperationId)
            {
                throw new InstallerException(
                    "The evidenced instance tree is absent from its namespace, but its absence does not prove removal. " +
                    "If the tree was removed outside the CLI, rerun the same command with " +
                    $"--confirm-removed-operation-id {operation.OperationId:D}.");
            }
            FlushNamespace(namespaceRoot);
        }
        if (evidence.Settlement is null)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                evidencePath,
                evidence with { Settlement = PurgeAlreadyAbsentSettlement, SettledUtc = DateTimeOffset.UtcNow },
                DeploymentJsonContext.Default.PurgeDeletionEvidence, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Binds a resumed purge whose quarantine was never confirmed to the evidence its first attempt recorded, before
    /// anything is renamed or deleted: the retained evidence must belong to this operation, request, and host, carry
    /// no settlement, record exactly the current manifest and, when it recorded one, the current namespace, and the
    /// current tree must be exactly the evidenced inventory. An added, missing, or replaced entry, or a root that
    /// reappeared after the evidenced tree was removed, is never adopted for deletion.
    /// </summary>
    private static async Task EnsureRetainedPurgeTreeAsync(
        string evidencePath,
        LifecycleOperationState operation,
        InstallationPaths paths,
        InstanceManifest manifest,
        IReadOnlyList<DestructiveTreeNode> inventory,
        UnixNodeIdentity namespaceIdentity,
        CancellationToken cancellationToken)
    {
        var evidence = await ReadPurgeEvidenceAsync(evidencePath, cancellationToken).ConfigureAwait(false);
        ValidateRetainedPurgeEvidence(evidence, operation, paths);
        if (evidence.Settlement is not null ||
            JsonSerializer.Serialize(evidence.Manifest, DeploymentJsonContext.Default.InstanceManifest) !=
            JsonSerializer.Serialize(manifest, DeploymentJsonContext.Default.InstanceManifest) ||
            evidence.Namespace is { } evidencedNamespace && !evidencedNamespace.Matches(namespaceIdentity) ||
            !SafeTreeDeletion.IsUnchangedInventory(inventory, evidence.Tree))
        {
            throw new InstallerException("The instance root is not the tree the retained purge evidenced; purge will not adopt it.");
        }
    }

    private static async Task<PurgeDeletionEvidence> ReadPurgeEvidenceAsync(string evidencePath, CancellationToken cancellationToken)
    {
        await using var evidenceStream = SafeFileSystem.OpenOwnerFileRead(evidencePath);
        return await DeserializeRetainedAsync(
            evidenceStream, DeploymentJsonContext.Default.PurgeDeletionEvidence,
            "purge deletion evidence", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The binding every retained purge evidence must satisfy before a resumed purge acts on it: this operation and
    /// request, a well-formed settlement, this host, and a canonical manifest that owns the operation's paths and
    /// identities.
    /// </summary>
    private static void ValidateRetainedPurgeEvidence(
        PurgeDeletionEvidence evidence,
        LifecycleOperationState operation,
        InstallationPaths paths)
    {
        if (evidence.SchemaVersion != DeploymentSchemaVersions.LifecycleOperation || evidence.OperationId != operation.OperationId ||
            evidence.RequestSha256 != operation.RequestSha256 || evidence.Manifest.InstanceId != operation.InstanceId || evidence.Tree.Count == 0 ||
            evidence.Settlement is not (null or PurgeAlreadyAbsentSettlement) || (evidence.Settlement is null) != (evidence.SettledUtc is null))
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
    }

    /// <summary>
    /// Replaces the namespace flush for the current asynchronous flow only, so a contract can prove that a failed flush
    /// leaves the purge resumable. Production never sets it.
    /// </summary>
    internal static readonly AsyncLocal<Action<SafeFileHandle>?> NamespaceFlushOverride = new();

    private static void FlushNamespace(SafeFileHandle namespaceRoot)
    {
        if (NamespaceFlushOverride.Value is { } flush)
            flush(namespaceRoot);
        else
            NativeLinux.FlushOpenDirectory(namespaceRoot);
    }

    private static string SafeMessage(Exception exception) => exception is InstallerException ? exception.Message : string.Empty;

    internal static async Task<InstanceManifest> ReadManifestAsync(
        string path, CancellationToken cancellationToken, bool allowSiloLayout = false)
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
        ValidateCanonicalManifest(manifest, allowSiloLayout);
        return manifest;
    }

    internal static async Task<InstallationResult> ReadResultAsync(
        string path, CancellationToken cancellationToken, bool allowSiloLayout = false)
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
            result.ComposeTemplateVersion != ComposeDeployment.TemplateVersionFor((CameraAgentReplayProfile)result.ReplayProfile) &&
            !((allowSiloLayout || SiloLayoutAdmitted.Value) &&
              result.ComposeTemplateVersion == SiloRuntimeProfile.ComposeTemplateVersion &&
              result.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess) ||
            !double.IsFinite(result.LatitudeDegrees) || !double.IsFinite(result.LongitudeDegrees) ||
            !double.IsFinite(result.ElevationMeters) || !HasValue(result.TimeZoneId) ||
            !IsSha256(result.ConfigurationSha256) || !IsSha256(result.RigProfileSha256) ||
            !IsSha256(result.ScheduleSha256) || !HasValue(result.RigProfileName) ||
            !HasValue(result.RigProfileVersion) || !HasValue(result.ScheduleSchemaVersion) ||
            !HasValue(result.ScheduleState) || !IsSha256(result.ComposeModelSha256) ||
            !IsValidCatalog(result.Catalog) || !ValidDeepSky(result.DeepSkyCatalog, result.ProductRoot) ||
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
            manifest.Catalog != operation.OriginalCatalog || result.Catalog != operation.OriginalCatalog ||
            manifest.DeepSkyCatalog != operation.OriginalDeepSkyCatalog || result.DeepSkyCatalog != operation.OriginalDeepSkyCatalog)
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
        // The required catalog ID names the lineage the host enforces; it must name the same approved catalog as the
        // selected package version, which an interrupted cross-lineage selection may have advanced to its candidate.
        var catalogIdPath = Path.Combine(paths.ConfigRoot, "secrets", "Catalog__RequiredCatalogId");
        if (!File.Exists(catalogIdPath))
            throw new InstallerException("The catalog selection credential does not match the retained instance manifest.");
        var catalogId = await ReadSecretAsync(catalogIdPath, cancellationToken).ConfigureAwait(false);
        if (catalogId != ProductionCatalog.ForPackageVersion(packageVersion).CatalogId &&
            (resumablePackageVersion is null || catalogId != ProductionCatalog.ForPackageVersion(resumablePackageVersion).CatalogId))
            throw new InstallerException("The catalog selection credential does not match the retained lifecycle state.");
    }

    internal static void ValidateLifecycleControlToken(InstanceManifest manifest, string token)
    {
        if (manifest.LifecycleControlTokenSha256 != ComposeDeployment.ComputeSha256(token))
            throw new InstallerException("The lifecycle control credential does not match the retained instance manifest.");
    }

    internal static void ValidateCanonicalManifest(InstanceManifest manifest, bool allowSiloLayout = false)
    {
        var siloLayout = string.Equals(manifest.ComponentSchemaVersion, SiloRuntimeProfile.ComponentSchemaVersion, StringComparison.Ordinal);
        if (siloLayout && !(allowSiloLayout || SiloLayoutAdmitted.Value))
            throw InstallerException.Refused(SiloLayoutUnsupported);
        if (siloLayout ? !IsCanonicalSilo(manifest) : manifest.Silo is not null)
            throw new InstallerException("The retained instance manifest is invalid or unsupported.");
        // A Silo-layout instance runs the same CameraAgent image contract; only its Compose model differs.
        var componentSchema = siloLayout ? "cameraagent-install-v1" : manifest.ComponentSchemaVersion;
        var composeTemplate = siloLayout
            ? SiloRuntimeProfile.ComposeTemplateVersion
            : ComposeDeployment.TemplateVersionFor((CameraAgentReplayProfile)manifest.ReplayProfile);
        if (manifest.SchemaVersion != DeploymentSchemaVersions.InstanceManifest ||
            manifest.Product != "HVO.SkyMonitor" || componentSchema != "cameraagent-install-v1" ||
            manifest.Component != DeploymentComponent.CameraAgent || manifest.InstanceId == Guid.Empty ||
            manifest.InstallationId == Guid.Empty || manifest.ApplicationIdentity == Guid.Empty ||
            !HasValue(manifest.FriendlyName) || !HasValue(manifest.DeploymentLocationId) ||
            manifest.DeploymentLocationVersion < 1 || !IsSha256(manifest.DeploymentLocationSha256) ||
            !HasValue(manifest.OwnerEmail) || !HasValue(manifest.TimeZoneId) ||
            !double.IsFinite(manifest.LatitudeDegrees) || !double.IsFinite(manifest.LongitudeDegrees) ||
            !double.IsFinite(manifest.ElevationMeters) || manifest.RuntimeUid == 0 ||
            !HasValue(manifest.ProductRoot) || !HasValue(manifest.ConfigRoot) || !HasValue(manifest.StateRoot) ||
            !Enum.IsDefined(manifest.ReplayProfile) ||
            manifest.ComposeTemplateVersion != composeTemplate ||
            !IsSha256(manifest.ConfigurationSha256) || !IsSha256(manifest.RigProfileSha256) ||
            !IsSha256(manifest.ScheduleSha256) || !HasValue(manifest.RigProfileName) ||
            !HasValue(manifest.RigProfileVersion) || !HasValue(manifest.ScheduleSchemaVersion) ||
            !HasValue(manifest.ScheduleState) || !IsSha256(manifest.InstallationVerificationTokenSha256) ||
            !IsSha256(manifest.ComposeModelSha256) || !IsValidCatalog(manifest.Catalog) ||
            !ValidDeepSky(manifest.DeepSkyCatalog, manifest.ProductRoot) || !ValidDeepSky(manifest.PreviousDeepSkyCatalog, manifest.ProductRoot) ||
            manifest.DeepSkyCatalog is not null && !CameraAgentImageContract.SupportsCatalog(manifest.Image?.CatalogContract, manifest.DeepSkyCatalog.CatalogId) ||
            manifest.Resources is not null && !CameraAgentContainerResources.IsValid(manifest.Resources) ||
            !CameraAgentImageContract.SupportsCatalog(manifest.Image?.CatalogContract, manifest.Catalog.CatalogId) ||
            manifest.Catalog.InstallRoot != Path.Combine(manifest.ProductRoot, "catalogs", manifest.Catalog.CatalogId) ||
            !IsSha256(manifest.LifecycleControlTokenSha256) ||
            !IsCanonicalImage(
                manifest.Image,
                componentSchema,
                manifest.DockerDaemon?.Architecture,
                manifest.ReplayProfile == HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.LocalRunner) ||
            manifest.PreviousImage is not null &&
            !IsCanonicalImage(
                manifest.PreviousImage,
                componentSchema,
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

    /// <summary>The Silo block of a Silo-layout manifest names exactly this instance's pinned store and data root.</summary>
    private static bool IsCanonicalSilo(InstanceManifest manifest)
    {
        if (manifest.Silo is not { } silo ||
            manifest.ReplayProfile != HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile.InProcess ||
            manifest.PreviousImage is not null) return false;
        var instanceRoot = Directory.GetParent(manifest.StateRoot)?.FullName;
        return instanceRoot is not null &&
               silo.StateContract == SiloRuntimeProfile.StateContract &&
               silo.ImageReference == SiloRuntimeProfile.ImageReference &&
               silo.ImageRelease == SiloRuntimeProfile.Release &&
               silo.ImageIndexDigest == SiloRuntimeProfile.IndexDigest &&
               silo.ImageRevision == SiloRuntimeProfile.SourceRevision &&
               silo.ImagePlatformDigest == SiloRuntimeProfile.PlatformDigestFor(manifest.DockerDaemon?.Architecture) &&
               SiloImagePin.ForArchitecture(manifest.DockerDaemon?.Architecture) is { } platform &&
               SiloImagePin.AcceptedImageIds(platform).Contains(silo.ImageId, StringComparer.Ordinal) &&
               silo.DataRoot == Path.Combine(instanceRoot, SiloRuntimeProfile.DataDirectoryName) &&
               silo.DataRootFileSystem == SiloRuntimeProfile.RequiredFileSystem && HasValue(silo.DataRootDevice) &&
               silo.Bucket == SiloRuntimeProfile.BucketFor(manifest.InstanceId) &&
               silo.StoreId == SiloRuntimeProfile.StoreIdFor(manifest.InstanceId) &&
               silo.StorageBindingId == SiloRuntimeProfile.StorageBindingIdFor(manifest.InstanceId, silo.StoreId, silo.Bucket) &&
               silo.WriterGeneration == SiloRuntimeProfile.WriterGeneration &&
               SiloRuntimeProfile.AccessKeyPattern().IsMatch(silo.WriterAccessKeyId) &&
               SiloRuntimeProfile.VersionIdPattern().IsMatch(silo.MarkerVersionId) && IsSha256(silo.MarkerSha256) &&
               SiloRuntimeProfile.VersionIdPattern().IsMatch(silo.FenceVersionId) && IsSha256(silo.FenceSha256) &&
               silo.FirstBootPauseId != Guid.Empty && silo.DiskSyncOnWrite &&
               (silo.Archive is null || IsCanonicalSiloArchive(silo.Archive));
    }

    private static bool IsCanonicalSiloArchive(SiloArchiveIdentity archive)
    {
        try
        {
            var endpoint = SiloArchiveProbe.ValidateEndpoint(archive.Endpoint + "/", archive.Bucket);
            return endpoint.GetLeftPart(UriPartial.Authority) == archive.Endpoint &&
                   IsSha256(archive.AuthoritySha256) && IsSha256(archive.ServerCertificateSha256) &&
                   !string.IsNullOrEmpty(archive.AccessKeyId) && archive.VerifiedUtc != default;
        }
        catch (InstallUsageException)
        {
            return false;
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
              manifest.Image != result.Image || manifest.Catalog != result.Catalog || manifest.DeepSkyCatalog != result.DeepSkyCatalog)))
        {
            throw new InstallerException("The retained instance identities do not correlate.");
        }
    }

    private static bool AllowsTransactionalDrift(LifecycleRequest request, LifecycleOperationState? operation)
        => request.Resume && (operation is not null && IsPendingSignedAutomaticRecovery(operation) || operation is
        {
            MutationStarted: true,
            Status: not InstallationStatus.Completed,
            Phase: LifecycleOperationPhase.CandidateVerified,
            Kind: LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or
                LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback
        });

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
        var deepSkyCorrelates = operation.DeepSkyTransition
            ? (manifest.DeepSkyCatalog == operation.OriginalDeepSkyCatalog || manifest.DeepSkyCatalog == operation.CandidateDeepSkyCatalog) &&
              (result.DeepSkyCatalog == operation.OriginalDeepSkyCatalog || result.DeepSkyCatalog == operation.CandidateDeepSkyCatalog)
            : manifest.DeepSkyCatalog == operation.OriginalDeepSkyCatalog && result.DeepSkyCatalog == operation.OriginalDeepSkyCatalog;
        if (!imagesCorrelate || !catalogsCorrelate || !deepSkyCorrelates)
            throw new InstallerException("The retained transactional identities do not match the lifecycle operation.");
    }

    internal static ComposeFiles ComposeFrom(InstallationPaths paths, InstanceManifest manifest, InstallationResult result)
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

    internal static string ReplaceEnvironmentValue(string environment, string key, string value)
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
           image.Component == CameraAgentImageContract.Component && image.ConfigurationContract == configurationContract &&
            CameraAgentImageContract.IsKnownCatalogContract(image.CatalogContract) && IsSourceRevision(image.SourceRevision) &&
            (!requireReplayRunner || image.ReplayRunnerContract == CameraAgentImageContract.ReplayRunnerContract);

    private static bool IsValidCatalog(CatalogInstallationIdentity? value)
        => ProductionCatalog.IsPinned(value) &&
           HasValue(value.InstallRoot) && IsSha256(value.ManifestSha256) && value.Source == "local-offline";

    internal static async Task ValidateDeepSkySelectionSettingAsync(InstallationPaths paths, CatalogInstallationIdentity? selected,
        LifecycleOperationState? retained, CancellationToken cancellationToken)
    {
        if (retained is { DeepSkyTransition: true, MutationStarted: true }) return;
        var root = Path.Combine(paths.ConfigRoot, "secrets");
        var enabledPath = Path.Combine(root, "DeepSkyCatalog__Enabled");
        if (selected is null)
        {
            if (!File.Exists(enabledPath)) return;
            using var source = SafeFileSystem.OpenOwnerFileRead(enabledPath);
            using var reader = new StreamReader(source);
            if ((await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n') != "false")
                throw new InstallerException("The deep-sky configuration differs from the retained independent selection.");
            return;
        }
        foreach (var (key, expected) in new[] { ("Enabled", "true"), ("Root", "/app/deep-sky-catalog"),
                     ("RequiredCatalogId", selected.CatalogId), ("RequiredPackageKind", "Production"), ("RequiredPackageVersion", selected.PackageVersion) })
        {
            var path = Path.Combine(root, "DeepSkyCatalog__" + key);
            if (!File.Exists(path)) throw new InstallerException("The deep-sky selection configuration is incomplete.");
            using var source = SafeFileSystem.OpenOwnerFileRead(path);
            using var reader = new StreamReader(source);
            if ((await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).TrimEnd('\r', '\n') != expected)
                throw new InstallerException("The deep-sky configuration differs from the retained independent selection.");
        }
    }

    internal static async Task PinCatalogsAsync(InstallationPaths paths, InstanceManifest manifest, Guid operationId, CancellationToken cancellationToken)
    {
        await CatalogReferenceStore.PinHistoricalAsync(paths, manifest.Catalog, operationId, cancellationToken).ConfigureAwait(false);
        if (manifest.DeepSkyCatalog is { } deepSky)
            await CatalogReferenceStore.PinHistoricalAsync(paths.WithCatalog(deepSky.CatalogId), deepSky, operationId, cancellationToken).ConfigureAwait(false);
    }

    private static bool ValidDeepSky(CatalogInstallationIdentity? catalog, string productRoot) => catalog is null ||
        IsValidCatalog(catalog) && ProductionCatalog.Get(catalog.CatalogId).ManifestVersion == 4 &&
        catalog.InstallRoot == Path.Combine(productRoot, "catalogs", catalog.CatalogId);

    internal static void ValidateCanonicalCatalog(InstallationPaths paths, CatalogInstallationIdentity value)
    {
        if (!IsValidCatalog(value) || value.InstallRoot != paths.CatalogRoot)
            throw new InstallerException("The retained catalog identity is invalid or unsupported.");
    }

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
            LifecycleOperationKind.Uninstall or LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback or
            LifecycleOperationKind.Backup;

    private static bool RequiresInstallationVerification(LifecycleOperationKind? operation)
        => operation is LifecycleOperationKind.Upgrade or LifecycleOperationKind.Rollback or LifecycleOperationKind.Reinstall or
            LifecycleOperationKind.CatalogSelect or LifecycleOperationKind.CatalogRollback or LifecycleOperationKind.Backup;

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

    /// <summary>
    /// Estimated room for the image archive and headroom, plus the operation's snapshot, scratch database,
    /// concurrent rollback journal and separate restore-copy allowance only when those phases are required.
    /// Triggers may grow the trial beyond its current-size estimate; every trial checks capacity again while
    /// keeping space for the restore. Bulk data is never copied or counted.
    /// </summary>
    private static void EnsureUpgradeStorage(
        InstallationPaths paths, string? imageArchive, HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile replayProfile,
        bool rollback, bool snapshotRequired)
    {
        var databaseBytes = snapshotRequired
            ? CameraAgentStateSnapshot.StoreBytes(paths.StateRoot, SnapshotInventory(paths, replayProfile, rollback))
            : 0;
        var snapshotBytes = snapshotRequired
            ? checked(4 * databaseBytes + CameraAgentStateCounters.JournalRecordBytes(databaseBytes))
            : 0;
        var archiveBytes = imageArchive is null ? 0 : new FileInfo(imageArchive).Length;
        var required = checked(snapshotBytes + archiveBytes + 256L * 1024 * 1024);
        // The snapshot and staged archive land on the filesystem that holds the product root, which need not be '/'.
        var available = UpgradeStorageHostOverride.Value is { } host
            ? host.MeasureFileSystem(paths.ProductRoot).AvailableBytes
            : LinuxHostEnvironment.AvailableBytes(paths.ProductRoot);
        if (available < required)
        {
            throw new InstallerException(snapshotRequired
                ? "Insufficient free space for the candidate image and the database snapshot."
                : "Insufficient free space for the candidate image and headroom.");
        }
    }

    /// <summary>
    /// The inventory an operation snapshots before it changes the image. An upgrade snapshots the stores the installed
    /// release wrote, and a schedule revision it cannot inventory refuses the upgrade before anything changes. A rollback
    /// snapshots the stores the release it undoes wrote, so it reads them as a restore does: a journal that release left
    /// in a shape this installer does not read narrows the inventory to the configured roots rather than blocking the
    /// rollback (the rollback adds every store the retained snapshot records), and the restore that follows says so on
    /// standard error and leaves a store under a root only that journal names in place.
    /// </summary>
    private static IReadOnlyList<string> SnapshotInventory(
        InstallationPaths paths, HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile replayProfile, bool rollback)
        => rollback
            ? CameraAgentStateInventory.StoresForRestore(paths, replayProfile).Stores
            : CameraAgentStateInventory.Stores(paths, replayProfile);

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
            0,
            0,
            DateTimeOffset.UtcNow,
            value.CaptureInitialized,
            value.ReplayPending,
            value.ReplayLeased);

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
                ? $"{InstallerProgram.Current} cameraagent reinstall --instance-id {manifest.InstanceId:D}"
                : null,
            DateTimeOffset.UtcNow)
        { DeepSkyCatalog = manifest.DeepSkyCatalog };

    private static async Task<LifecycleResult> ListAsync(string productRoot, CancellationToken cancellationToken)
    {
        var agentsRoot = Path.Combine(productRoot, "cameraagents");
        var instances = new List<string>();
        if (Directory.Exists(agentsRoot))
        {
            foreach (var manifestPath in Directory.EnumerateFiles(agentsRoot, "instance-manifest.json", SearchOption.AllDirectories)
                         .Order(StringComparer.Ordinal))
            {
                var manifest = await ReadManifestAsync(manifestPath, cancellationToken, allowSiloLayout: true).ConfigureAwait(false);
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
