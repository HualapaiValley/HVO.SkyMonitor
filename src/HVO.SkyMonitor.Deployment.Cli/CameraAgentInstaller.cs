using HVO.SkyMonitor.CameraAgent.Configuration;
using HVO.SkyMonitor.Deployment.Contracts;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Deployment;

internal sealed partial class CameraAgentInstaller
{
    public static async Task<InstallationResult> InstallAsync(InstallRequest request, CancellationToken cancellationToken)
    {
        var processRunner = new ProcessRunner();
        return await InstallAsync(
            request,
            processRunner,
            static uri => new OwnerBootstrapClient(uri),
            NativeLinux.getuid(),
            NativeLinux.getgid(),
            cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every operational failure must be journaled and redacted before leaving the installer boundary.")]
    internal static async Task<InstallationResult> InstallAsync(
        InstallRequest request,
        IProcessRunner processRunner,
        Func<Uri, IOwnerBootstrapClient> ownerClientFactory,
        uint uid,
        uint gid,
        CancellationToken cancellationToken,
        Action<string, int>? portAvailabilityProbe = null,
        IHostEnvironment? hostEnvironment = null)
    {
        request.Validate();
        // The public entry point never supplies a host environment, so a production install always measures the real
        // filesystems and PATH; unit flows substitute one so shared-host disk and tool state cannot decide them.
        hostEnvironment ??= LinuxHostEnvironment.Instance;
        // The public entry point never supplies a probe, so a production install always admits the port through the
        // real socket bind below. Unit flows whose subject is identity, resume, or release acquisition can substitute
        // an explicit probe so shared-host socket state cannot decide their outcome.
        portAvailabilityProbe ??= EnsurePortAvailable;
        var identityRequest = request;
        var docker = new DockerClient(processRunner);
        var instanceId = request.InstanceId ?? Guid.NewGuid();
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.DefaultCatalogId);
        if (request.DryRun)
        {
            var dryRunImage = await ReleaseAcquirer.AcquireImageAsync(request, cancellationToken).ConfigureAwait(false);
            var plannedRequest = ApplyReleaseImage(request, dryRunImage);
            var retained = await ReadRetainedAsync(paths, cancellationToken).ConfigureAwait(false);
            // A dry run evaluates the same host prerequisites an install does, so a host that cannot run the instance
            // as deployed is refused by the plan rather than discovered after the product root exists.
            var (_, plannedWarnings) = await EnsureHostPrerequisitesAsync(
                    plannedRequest, docker, hostEnvironment, HostCheckLimits(plannedRequest, retained), cancellationToken)
                .ConfigureAwait(false);
            if (retained is { Completed: true })
            {
                var reRun = await PlanReRunAsync(
                        plannedRequest, retained, paths, instanceId, docker, dryRunImage?.Image, uid, gid, cancellationToken)
                    .ConfigureAwait(false);
                return reRun with { Warnings = plannedWarnings };
            }
            if (retained is not null && !RequestIdentityMatches(retained.State.RequestSha256, identityRequest, instanceId, dryRunImage?.VerifiedRelease))
            {
                throw InstallerException.Refused("The retained installation state belongs to different immutable inputs.");
            }
            var plan = await PlanAsync(
                    plannedRequest,
                    paths.WithCatalog(ProductionCatalog.ReadBundleSpecification(plannedRequest.CatalogBundle!).CatalogId),
                    instanceId,
                    docker,
                    dryRunImage?.Image,
                    cancellationToken)
                .ConfigureAwait(false);
            var plannedPaths = paths.WithCatalog(Path.GetFileName(plan.Catalog.InstallRoot));
            var plannedRecord = InstallRecordLocation.For(plannedPaths.ProductRoot, request.ProductionProductRoot);
            return plan with
            {
                Warnings = plannedWarnings,
                Plan = InstalledPlan(
                    plannedPaths,
                    instanceId,
                    plan.Image.ImageId,
                    plannedRequest.ContainerResources(),
                    FreshInstallFacts.Observe(plannedPaths),
                    await FreshRecordActionAsync(plannedRecord, instanceId, cancellationToken).ConfigureAwait(false),
                    plannedRecord)
            };
        }

        if (uid == 0)
        {
            throw InstallerException.Refused("Run the installer as the Docker-capable runtime user, not as root or through sudo.");
        }

        if (File.Exists(paths.ManifestPath))
            _ = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (File.Exists(paths.ResultPath))
            _ = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        // Read before anything is prepared, so the host is checked against the limits this run would apply. Everything
        // is read again under the instance lock before it decides anything else.
        var hostCheckLimits = HostCheckLimits(request, await ReadRetainedAsync(paths, cancellationToken).ConfigureAwait(false));

        // The release is read and checked against its SHA256SUMS, its platform selected, and its archive verified before
        // the Docker daemon is contacted, so an unsupported architecture or tampered file fails with nothing yet mutated.
        var acquiredImage = await ReleaseAcquirer.AcquireImageAsync(request, cancellationToken).ConfigureAwait(false);
        request = ApplyReleaseImage(request, acquiredImage);

        // Every host prerequisite is evaluated, and every finding reported together, before the product root is
        // prepared or anything else is created.
        var (preflightDaemon, warnings) = await EnsureHostPrerequisitesAsync(
                request, docker, hostEnvironment, hostCheckLimits, cancellationToken)
            .ConfigureAwait(false);
        ValidateCatalogInput(request.CatalogBundle!);
        // The bundle's approved specification selects the side-by-side catalog root this instance installs into.
        var catalogId = ProductionCatalog.ReadBundleSpecification(request.CatalogBundle!).CatalogId;
        paths = paths.WithCatalog(catalogId);
        // A release's image is proved to be the release's, and an image this instance can run, and a new installation
        // imports it, before anything is claimed for it: no product root, state, application identity, catalog or staged
        // archive exists yet when it is refused or when Docker cannot import it.
        var releaseImported = false;
        if (acquiredImage is not null)
        {
            (acquiredImage, releaseImported) = await EnsureReleaseImageAsync(
                    request, acquiredImage, docker, catalogId, File.Exists(paths.StatePath), cancellationToken)
                .ConfigureAwait(false);
        }

        var freshFacts = FreshInstallFacts.Observe(paths);
        await PrivilegedPreparation.PrepareAsync(paths, uid, gid, processRunner, cancellationToken).ConfigureAwait(false);
        using var productLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);
        if (!File.Exists(paths.StatePath))
        {
            portAvailabilityProbe(request.BindAddress, request.Port);
        }
        // The clock gate protects every production install, whichever root the host policy names; only an admitted
        // test root is exempt.
        if (request.IsProductionRoot && !NativeLinux.IsClockSynchronized())
        {
            throw new InstallerException("The host clock is not synchronized.");
        }

        var requestSha256 = ComputeRequestSha256(identityRequest, instanceId, acquiredImage?.VerifiedRelease);
        var existingState = await ReadStateAsync(paths.StatePath, cancellationToken).ConfigureAwait(false);
        EnsureInstanceRootCanBeOwned(paths, existingState is not null);
        // An unfinished installation resumes only with exactly the inputs it began with. A completed one is held to the
        // instance its records describe instead (below), so a re-run may change its container limits (install contract
        // section 7) and nothing else.
        if (existingState is { Status: not InstallationStatus.Completed } && !RequestIdentityMatches(
                existingState.RequestSha256,
                identityRequest,
                instanceId,
                acquiredImage?.VerifiedRelease))
        {
            throw InstallerException.Refused("The retained installation state belongs to different immutable inputs.");
        }
        InstallationResult? retainedCompletedResult = null;
        if (existingState?.Status == InstallationStatus.Completed)
        {
            retainedCompletedResult = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        }
        if (request.Resume && existingState is null)
        {
            throw new InstallerException("No resumable installation state exists for --instance-id.");
        }

        var installationId = existingState?.InstallationId ?? Guid.NewGuid();
        var state = existingState ?? new InstallationState(
            DeploymentSchemaVersions.InstallationState,
            installationId,
            instanceId,
            requestSha256,
            InstallationPhase.Preflight,
            InstallationStatus.Pending,
            DateTimeOffset.UtcNow);
        var completedCandidateValidated = false;
        var limitsChange = false;
        if (retainedCompletedResult is null)
        {
            state = await RecordPhaseAsync(paths, state, InstallationPhase.Preflight, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            var applicationIdentity = await GetOrCreateApplicationIdentityAsync(
                paths.ApplicationIdentityPath,
                allowCreation: retainedCompletedResult is null,
                cancellationToken).ConfigureAwait(false);
            var existingManifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (existingManifest is not null &&
                (existingManifest.Product != "HVO.SkyMonitor" ||
                 existingManifest.Component != DeploymentComponent.CameraAgent ||
                 existingManifest.InstanceId != instanceId ||
                 existingManifest.InstallationId != installationId ||
                 existingManifest.ApplicationIdentity != applicationIdentity ||
                 existingManifest.ProductRoot != paths.ProductRoot ||
                 existingManifest.ConfigRoot != paths.ConfigRoot ||
                 existingManifest.StateRoot != paths.StateRoot))
            {
                throw new InstallerException("The retained instance manifest does not correlate with this installation.");
            }
            if (retainedCompletedResult is not null && existingManifest is null)
            {
                throw new InstallerException("A completed installation requires its retained instance manifest.");
            }
            if (retainedCompletedResult is not null &&
                (retainedCompletedResult.InstallationId != installationId ||
                 retainedCompletedResult.InstanceId != instanceId ||
                 retainedCompletedResult.ApplicationIdentity != applicationIdentity))
            {
                throw new InstallerException("The retained installation result does not correlate with this installation.");
            }
            if (retainedCompletedResult is not null && existingManifest!.LifecycleCondition == InstanceLifecycleCondition.Uninstalled)
            {
                throw InstallerException.Refused("A preserved uninstalled instance must be restored with cameraagent reinstall.");
            }
            if (retainedCompletedResult is not null && InstalledDifference(request, paths, existingManifest!) is { } difference)
            {
                throw InstallerException.Refused(difference);
            }
            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Prepare, cancellationToken).ConfigureAwait(false);
            }

            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Catalog, cancellationToken).ConfigureAwait(false);
            }
            var catalog = retainedCompletedResult is null
                ? CatalogInstaller.Install(request.CatalogBundle!, paths.CatalogRoot, installationId)
                : CatalogInstaller.ValidateExisting(request.CatalogBundle!, paths.CatalogRoot);

            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Image, cancellationToken).ConfigureAwait(false);
            }
            var effectiveRequest = await StageImageArchiveAsync(
                request,
                paths,
                allowMutation: retainedCompletedResult is null,
                cancellationToken).ConfigureAwait(false);
            // A release image imported before anything was claimed is found and checked again here, not imported twice.
            var (daemon, image) = await docker.PrepareImageAsync(
                effectiveRequest,
                allowMutation: retainedCompletedResult is null && !releaseImported,
                acquiredImage?.Image,
                cancellationToken)
                .ConfigureAwait(false);
            if (!IsValid(image, request.ReplayProfile))
                throw new InstallerException("The CameraAgent image does not declare the required current configuration and catalog contracts.");
            EnsureImageSupportsCatalog(image, catalog.CatalogId);
            if (daemon != preflightDaemon)
            {
                throw new InstallerException("The Docker daemon identity changed after preflight.");
            }
            if (retainedCompletedResult is not null && image.ImageId != existingManifest!.Image.ImageId)
            {
                throw InstallerException.Refused(DifferentImage(existingManifest, image));
            }
            if (retainedCompletedResult is not null &&
                (catalog.CatalogId != existingManifest!.Catalog.CatalogId || catalog.PackageVersion != existingManifest.Catalog.PackageVersion))
            {
                throw InstallerException.Refused(
                    $"The instance uses catalog {existingManifest.Catalog.CatalogId} {existingManifest.Catalog.PackageVersion}, not the requested {catalog.CatalogId} {catalog.PackageVersion}; change it with catalog select.");
            }
            if (retainedCompletedResult is not null && request.ReplayProfile == CameraAgentReplayProfile.InProcess &&
                existingManifest!.Image.ReplayRunnerContract is null && retainedCompletedResult.Image.ReplayRunnerContract is null)
            {
                image = image with { ReplayRunnerContract = null };
            }

            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Configuration, cancellationToken).ConfigureAwait(false);
            }
            var generatedPasswordPath = Path.Combine(paths.ConfigRoot, "owner-bootstrap", "temporary-password");
            var passwordPath = retainedCompletedResult?.PasswordFile ?? await CreateOwnerPasswordAsync(
                    request,
                    generatedPasswordPath,
                    cancellationToken).ConfigureAwait(false);
            var verificationTokenPath = Path.Combine(paths.ConfigRoot, "installation-verification", "token");
            if (retainedCompletedResult is null)
            {
                verificationTokenPath = await CredentialFile.GetOrCreateAsync(
                    null,
                    verificationTokenPath,
                    cancellationToken).ConfigureAwait(false);
            }
            var verificationToken = await ReadOwnerSecretAsync(verificationTokenPath, cancellationToken).ConfigureAwait(false);
            var lifecycleControlTokenPath = Path.Combine(paths.ConfigRoot, "lifecycle-control", "token");
            if (retainedCompletedResult is null)
            {
                lifecycleControlTokenPath = await CredentialFile.GetOrCreateAsync(
                    null,
                    lifecycleControlTokenPath,
                    cancellationToken).ConfigureAwait(false);
            }
            var lifecycleControlToken = await ReadOwnerSecretAsync(lifecycleControlTokenPath, cancellationToken).ConfigureAwait(false);
            var passwordAuthorityEnabled = retainedCompletedResult is null &&
                                            (existingState is null || existingState.Phase < InstallationPhase.OwnerSeeded);
            // A re-run of a completed installation is first validated against the limits it recorded. It then applies the
            // limits this run asks for: a limit given replaces the recorded one and a limit left out is kept (install
            // contract section 7). An instance an earlier installer completed records none and keeps its original limits
            // until a re-run gives one or its next upgrade.
            var resources = retainedCompletedResult is null ? request.ContainerResources() : existingManifest!.Resources;
            var targetResources = retainedCompletedResult is null
                ? resources
                : CameraAgentContainerResources.ReRunTarget(existingManifest!.Resources, request.Cpus, request.Memory);
            limitsChange = retainedCompletedResult is not null && targetResources != existingManifest!.Resources;
            var resultModelStale = false;
            string? composeBefore = null;
            if (retainedCompletedResult is not null)
            {
                composeBefore = await ReadComposeIfPresentAsync(paths, cancellationToken).ConfigureAwait(false);
                resultModelStale = await ValidateCompletedCandidateAsync(
                    request,
                    paths,
                    instanceId,
                    installationId,
                    applicationIdentity,
                    uid,
                    gid,
                    passwordPath,
                    verificationToken,
                    lifecycleControlToken,
                    catalog,
                    image,
                    daemon,
                    existingManifest!,
                    retainedCompletedResult,
                    resources,
                    docker,
                    cancellationToken).ConfigureAwait(false);
                completedCandidateValidated = true;
            }
            var compose = ComposeDeployment.Write(
                request,
                paths,
                instanceId,
                applicationIdentity,
                uid,
                gid,
                image.ImageId,
                catalog.PackageVersion,
                passwordPath,
                verificationToken,
                lifecycleControlToken,
                passwordAuthorityEnabled,
                resources);

            // Compose writing created every writable bind source with the runtime identity. Prove the persisted
            // catalog, Identity, and raw-ingress boundaries match this image before Compose starts the container,
            // so an incompatible upgrade path fails once instead of through container restart loops.
            await CameraAgentStatePreflight.EnsureCompatibleAsync(
                paths,
                instanceId,
                image,
                existingManifest is not null
                    ? existingManifest.Image.UpgradeCompatibility ?? existingManifest.UpgradeCompatibility
                    : image.UpgradeCompatibility,
                uid,
                gid,
                (ContractReplayProfile)request.ReplayProfile,
                persist: true,
                renderToStandardError: !request.Json,
                cancellationToken,
                // Installing an image is not an in-place state migration, so an image that predates the label
                // correction is admitted as a known contract; the persisted boundaries it declares still decide.
                CameraAgentStateContractPolicy.AllowLegacy,
                acquiredImage?.Release.Tag).ConfigureAwait(false);

            // A re-run of a completed installation never moves its retained state back to an earlier phase: an
            // interruption leaves it completed, so the next run validates it as completed again.
            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Compose, cancellationToken).ConfigureAwait(false);
            }
            var rendered = await docker.ComposeAsync(
                compose.ComposeFile,
                compose.EnvironmentFile,
                compose.ProjectName,
                ["config"],
                cancellationToken).ConfigureAwait(false);
            var composeModelSha256 = ComposeDeployment.ComputeSha256(rendered.StandardOutput);
            var manifest = CreateManifest(
                request, paths, instanceId, installationId, applicationIdentity, uid, gid,
                verificationToken, lifecycleControlToken, compose, composeModelSha256, catalog, image, daemon, existingManifest,
                resources, keepPreviousStateSnapshot: retainedCompletedResult is not null);
            if (retainedCompletedResult is null)
            {
                await SafeFileSystem.WriteJsonAtomicAsync(
                    paths.ManifestPath,
                    manifest,
                    DeploymentJsonContext.Default.InstanceManifest,
                    cancellationToken).ConfigureAwait(false);
            }

            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Startup, cancellationToken).ConfigureAwait(false);
            }
            await docker.ComposeAsync(
                compose.ComposeFile,
                compose.EnvironmentFile,
                compose.ProjectName,
                ["up", "--detach", "--remove-orphans"],
                cancellationToken).ConfigureAwait(false);
            // Health and owner bootstrap reach the instance at the address it was bound to, the same public URL the
            // result reports and every lifecycle probe uses; a LAN-bound instance is not listening on loopback.
            var baseAddress = PublicUri(request);
            var ownerClient = ownerClientFactory(baseAddress);
            await ownerClient.WaitForHealthAsync(ProductionCatalog.Get(catalog.CatalogId), cancellationToken).ConfigureAwait(false);

            var ownerState = retainedCompletedResult?.OwnerBootstrapState ?? "owner-temporary-password";
            // Whether the owner's first sign-in is the documented default comes from the credential actually seeded,
            // not from this invocation's flags, because a resumed installation keeps the credential its first run wrote.
            var ownerPasswordIsDefault = retainedCompletedResult?.OwnerPasswordIsDefault ?? false;
            string? password = null;
            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.OwnerBootstrap, cancellationToken).ConfigureAwait(false);
                password = await ReadOwnerPasswordAsync(passwordPath, cancellationToken).ConfigureAwait(false);
                ownerPasswordIsDefault = string.Equals(password, OwnerCredentialDefaults.Password, StringComparison.Ordinal);
                ownerState = await ownerClient.ReadStateAsync(request.OwnerEmail, password, cancellationToken)
                    .ConfigureAwait(false);
                if (ownerState == "owner-temporary-password")
                {
                    state = await RecordPhaseAsync(paths, state, InstallationPhase.OwnerSeeded, cancellationToken)
                        .ConfigureAwait(false);
                    compose = ComposeDeployment.Write(
                        request,
                        paths,
                        instanceId,
                        applicationIdentity,
                        uid,
                        gid,
                        image.ImageId,
                        catalog.PackageVersion,
                        passwordPath,
                        verificationToken,
                        lifecycleControlToken,
                        passwordAuthorityEnabled: false,
                        resources);
                    rendered = await docker.ComposeAsync(
                        compose.ComposeFile,
                        compose.EnvironmentFile,
                        compose.ProjectName,
                        ["config"],
                        cancellationToken).ConfigureAwait(false);
                    composeModelSha256 = ComposeDeployment.ComputeSha256(rendered.StandardOutput);
                    await docker.ComposeAsync(
                        compose.ComposeFile,
                        compose.EnvironmentFile,
                        compose.ProjectName,
                        ["up", "--detach", "--remove-orphans"],
                        cancellationToken).ConfigureAwait(false);
                    await ownerClient.WaitForHealthAsync(ProductionCatalog.Get(catalog.CatalogId), cancellationToken).ConfigureAwait(false);
                    ownerState = await ownerClient.ReadStateAsync(request.OwnerEmail, password, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            if (ownerState != "owner-password-change-required")
            {
                throw new InstallerException($"Unexpected owner bootstrap state '{ownerState}'.");
            }
            await docker.VerifyContainerAsync(
                compose,
                paths,
                image,
                uid,
                gid,
                cancellationToken).ConfigureAwait(false);
            await ownerClient.VerifyInstallationAsync(
                verificationToken,
                new InstallationVerificationExpectation(
                    applicationIdentity.ToString("D"),
                    request.OwnerEmail,
                    "owner-password-change-required",
                    compose.ConfigurationSha256,
                    compose.RigProfileSha256,
                    compose.ScheduleSha256,
                    $"installer-{instanceId:D}",
                    manifest.DeploymentLocationVersion,
                    manifest.DeploymentLocationSha256,
                    (ContractReplayProfile)request.ReplayProfile,
                    catalog,
                    AllowCompletedPasswordReplacement: retainedCompletedResult is not null),
                cancellationToken).ConfigureAwait(false);

            manifest = manifest with { ComposeModelSha256 = composeModelSha256 };
            if (retainedCompletedResult is not null && manifest != existingManifest)
            {
                throw new InstallerException("The completed installation differs from its retained immutable manifest.");
            }
            if (limitsChange)
            {
                // The instance was just verified at the limits it records. It moves to the new limits, and only once it
                // is verified there are the manifest, then the result, rewritten; a failed change restores the recorded
                // limits first.
                (compose, composeModelSha256) = await ChangeLimitsAsync(
                        request, paths, instanceId, applicationIdentity, uid, gid, image, catalog, passwordPath,
                        verificationToken, lifecycleControlToken, resources, targetResources!, existingManifest!,
                        ownerClient, docker, cancellationToken)
                    .ConfigureAwait(false);
                manifest = manifest with
                {
                    Resources = targetResources,
                    ComposeModelSha256 = composeModelSha256,
                    UpdatedUtc = DateTimeOffset.UtcNow
                };
            }
            if (retainedCompletedResult is null || limitsChange)
            {
                await SafeFileSystem.WriteJsonAtomicAsync(
                    paths.ManifestPath,
                    manifest,
                    DeploymentJsonContext.Default.InstanceManifest,
                    cancellationToken).ConfigureAwait(false);
            }

            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Verification, cancellationToken).ConfigureAwait(false);
            }
            var result = CreateInstalledResult(
                request,
                paths,
                instanceId,
                installationId,
                applicationIdentity,
                uid,
                gid,
                passwordPath,
                compose,
                composeModelSha256,
                catalog,
                image,
                daemon,
                ownerState,
                ownerPasswordIsDefault,
                DateTimeOffset.UtcNow);
            if (retainedCompletedResult is not null)
            {
                // The Compose model follows the limits, so a limits change, or the repair of a result an interrupted one
                // left behind its manifest, is the only difference a re-run may leave in the retained result.
                var comparableResult = result with
                {
                    CompletedUtc = retainedCompletedResult.CompletedUtc,
                    ComposeModelSha256 = limitsChange || resultModelStale
                        ? retainedCompletedResult.ComposeModelSha256
                        : result.ComposeModelSha256
                };
                if (comparableResult != retainedCompletedResult)
                {
                    throw new InstallerException("The completed installation differs from its retained result.");
                }
                result = retainedCompletedResult with { ComposeModelSha256 = composeModelSha256 };
                if (limitsChange || resultModelStale)
                {
                    await SafeFileSystem.WriteJsonAtomicAsync(
                        paths.ResultPath,
                        result,
                        DeploymentJsonContext.Default.InstallationResult,
                        cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await SafeFileSystem.WriteJsonAtomicAsync(
                    paths.ResultPath,
                    result,
                    DeploymentJsonContext.Default.InstallationResult,
                    cancellationToken).ConfigureAwait(false);
            }
            // The release record is retained only once the installation is complete, so a failure at any earlier
            // stage never leaves evidence naming a release no container is running.
            if (acquiredImage is not null)
            {
                await ReleaseRecords.SettleAsync(paths, acquiredImage, result.Image, cancellationToken).ConfigureAwait(false);
            }
            // The install record follows the instance's own records, so it is settled once they are complete and before
            // the installation is recorded as completed: an interruption between the two is finished by a re-run.
            var recordLocation = InstallRecordLocation.For(paths.ProductRoot, request.ProductionProductRoot);
            var recordAction = await InstallRecordStore.SettleInstanceAsync(
                    recordLocation,
                    paths,
                    manifest,
                    InstallRecordEvent.Install,
                    cancellationToken)
                .ConfigureAwait(false);
            if (retainedCompletedResult is null)
            {
                _ = await RecordPhaseAsync(
                    paths,
                    state,
                    InstallationPhase.Completed,
                    cancellationToken,
                    InstallationStatus.Completed).ConfigureAwait(false);
            }
            // The warnings and the plan describe this invocation, so only the reported result carries them: the
            // retained result was written, and a completed re-run compared with it, without them.
            return result with
            {
                Warnings = warnings,
                Plan = retainedCompletedResult is null
                    ? InstalledPlan(paths, instanceId, image.ImageId, targetResources, freshFacts, recordAction, recordLocation)
                    : ReRunPlan(
                        paths, existingManifest!, targetResources, composeBefore, limitsChange, limitsChange || resultModelStale,
                        recordAction, recordLocation)
            };
        }
        catch (OperationCanceledException exception)
        {
            if (retainedCompletedResult is null || completedCandidateValidated && !limitsChange)
            {
                await RecordFailureAsync(paths, state, exception, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception)
        {
            // A failed limits change restored the recorded limits, or leaves a change the next re-run finishes, so the
            // installation stays completed and is validated as completed again.
            if (retainedCompletedResult is null || completedCandidateValidated && !limitsChange)
            {
                await RecordFailureAsync(paths, state, exception, CancellationToken.None).ConfigureAwait(false);
            }
            throw exception is InstallerException installerException
                ? installerException
                : new InstallerException("Installation failed at a durable phase boundary.", exception);
        }
    }

    /// <summary>
    /// Requires a release's image to be the release's and one this instance can run, and imports it for a new
    /// installation, before the install claims anything:
    /// <list type="bullet">
    /// <item>An offline archive's image, layers included, was read from the archive at acquisition. A new installation
    /// then loads it here, so a load Docker rejects, or one that is cancelled, leaves no state behind to recover.</item>
    /// <item>A registry image's metadata is read from the registry by the digest release.json names, and checked, before
    /// anything is pulled (<see cref="DockerClient.ReadRegistryReleaseImageAsync"/>). A new installation then pulls it
    /// here.</item>
    /// </list>
    /// What Docker holds after the import is checked again, and an import that is refused, fails or is cancelled is
    /// removed. An installation that already has state (a resumed or repeated one) imports nothing here; its image step
    /// does, as before, from the same verified inputs. Returns the release, with the identities its registry gave, and
    /// whether the image is now imported.
    /// </summary>
    private static async Task<(AcquiredImage Release, bool Imported)> EnsureReleaseImageAsync(
        InstallRequest request,
        AcquiredImage release,
        DockerClient docker,
        string catalogId,
        bool stateExists,
        CancellationToken cancellationToken)
    {
        // Whatever this refuses has changed nothing: the image is not imported yet, or the import that added it is
        // removed again, so it is a refusal (exit 3, #1315).
        void Validate(ImageInstallationIdentity image)
        {
            try
            {
                if (!IsValid(image, request.ReplayProfile))
                    throw new InstallerException("The CameraAgent image does not declare the required current configuration and catalog contracts.");
                EnsureImageSupportsCatalog(image, catalogId);
            }
            catch (InstallerException exception) when (!exception.IsRefusal)
            {
                throw InstallerException.Refused(exception.Message, exception);
            }
        }
        if (release.ArchivedImage is { } archived)
        {
            Validate(archived);
        }
        else
        {
            var (registryImage, selection) = await docker.ReadRegistryReleaseImageAsync(release.Image, cancellationToken)
                .ConfigureAwait(false);
            Validate(registryImage);
            release = release with { Image = selection };
        }
        if (stateExists)
        {
            return (release, false);
        }
        _ = await docker.ImportReleaseImageAsync(request, release.Image, Validate, cancellationToken).ConfigureAwait(false);
        return (release, true);
    }

    /// <summary>
    /// Rewrites the request to install exactly the immutable image the release selected for this host: its offline
    /// archive, loaded by the image ID it names, or its digest reference, pulled from the registry. A request that names
    /// an operator-supplied image is returned unchanged.
    /// </summary>
    private static InstallRequest ApplyReleaseImage(InstallRequest request, AcquiredImage? image)
        => image is null
            ? request
            : request with
            {
                ImageReference = image.ImageReference,
                ImageArchive = image.ArchivePath,
                ImageArchiveSha256 = image.ArchiveSha256
            };

    private static async Task<InstallationResult> PlanAsync(
        InstallRequest request,
        InstallationPaths finalPaths,
        Guid instanceId,
        DockerClient docker,
        ReleaseImageSelection? releaseImage,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hvo-installer-plan-{Guid.NewGuid():N}");
        try
        {
            var applicationIdentity = Guid.NewGuid();
            var temporaryPaths = InstallationPaths.Create(temporaryRoot, instanceId, Path.GetFileName(finalPaths.CatalogRoot));
            var catalog = CatalogInstaller.Install(request.CatalogBundle!, temporaryPaths.CatalogRoot, Guid.NewGuid()) with
            {
                InstallRoot = finalPaths.CatalogRoot
            };
            var (daemon, image) = await docker.PrepareImageAsync(request, allowMutation: false, releaseImage, cancellationToken)
                .ConfigureAwait(false);
            if (!IsValid(image, request.ReplayProfile))
                throw new InstallerException("The CameraAgent image does not declare the required current configuration and catalog contracts.");
            EnsureImageSupportsCatalog(image, catalog.CatalogId);
            var compose = ComposeDeployment.Write(
                request,
                temporaryPaths,
                instanceId,
                applicationIdentity,
                NativeLinux.getuid(),
                NativeLinux.getgid(),
                image.ImageId,
                catalog.PackageVersion,
                "/dev/null",
                "dry-run-installation-verification-token",
                "dry-run-lifecycle-control-token",
                passwordAuthorityEnabled: true,
                request.ContainerResources());
            var rendered = await docker.ComposeAsync(
                compose.ComposeFile,
                compose.EnvironmentFile,
                compose.ProjectName,
                ["config"],
                cancellationToken).ConfigureAwait(false);
            return new InstallationResult(
                DeploymentSchemaVersions.InstallationResult,
                InstallationOutcome.Planned,
                Guid.Empty,
                instanceId,
                applicationIdentity,
                request.FriendlyName,
                PublicUri(request),
                request.OwnerEmail,
                "<generated-during-install>",
                finalPaths.ProductRoot,
                finalPaths.InstanceRoot,
                finalPaths.ConfigRoot,
                finalPaths.StateRoot,
                NativeLinux.getuid(),
                NativeLinux.getgid(),
                ComposeDeployment.TemplateVersionFor(request.ReplayProfile),
                request.LatitudeDegrees,
                request.LongitudeDegrees,
                request.ElevationMeters,
                request.TimeZoneId,
                compose.ConfigurationSha256,
                compose.RigProfileSha256,
                compose.ScheduleSha256,
                compose.RigProfileName,
                compose.RigProfileVersion,
                compose.ScheduleSchemaVersion,
                compose.ScheduleState,
                ComposeDeployment.ComputeSha256(rendered.StandardOutput),
                catalog,
                image,
                daemon,
                Alive: false,
                Healthy: false,
                "planned",
                DateTimeOffset.UtcNow,
                (ContractReplayProfile)request.ReplayProfile,
                OwnerPasswordIsDefault: request.PasswordFile is null && !request.GeneratePassword);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                SafeFileSystem.MakeTreeOwnerWritable(temporaryRoot);
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static async Task<Guid> GetOrCreateApplicationIdentityAsync(
        string path,
        bool allowCreation,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            var existing = await CameraAgentLifecycleManager.DeserializeRetainedAsync(
                stream,
                DeploymentJsonContext.Default.ApplicationIdentityBinding,
                "application identity binding",
                cancellationToken).ConfigureAwait(false);
            if (existing.SchemaVersion != 1 || existing.State != "bound" || existing.BoundIdentity != existing.ConfiguredIdentity)
            {
                throw new InstallerException("The application identity binding is invalid.");
            }
            return existing.ConfiguredIdentity;
        }

        if (!allowCreation)
        {
            throw new InstallerException("The completed installation is missing its application identity binding.");
        }

        var identity = Guid.NewGuid();
        await SafeFileSystem.WriteJsonAtomicAsync(
            path,
            new ApplicationIdentityBinding(1, "bound", identity, identity),
            DeploymentJsonContext.Default.ApplicationIdentityBinding,
            cancellationToken).ConfigureAwait(false);
        return identity;
    }

    /// <summary>
    /// Proves a completed installation is exactly what its retained manifest and result say, by rendering it again from
    /// the request into a temporary root. Returns <see langword="true"/> when the result differs from the manifest only
    /// in its Compose model: a limits change interrupted after its manifest commit, which the caller repairs. The manifest
    /// was just proved against a fresh render, so the result follows it.
    /// </summary>
    private static async Task<bool> ValidateCompletedCandidateAsync(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        Guid installationId,
        Guid applicationIdentity,
        uint uid,
        uint gid,
        string passwordPath,
        string verificationToken,
        string lifecycleControlToken,
        CatalogInstallationIdentity catalog,
        ImageInstallationIdentity image,
        DockerDaemonIdentity daemon,
        InstanceManifest retainedManifest,
        InstallationResult retainedResult,
        ContainerResourceLimits? resources,
        DockerClient docker,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hvo-installer-rerun-{Guid.NewGuid():N}");
        try
        {
            var outputPaths = InstallationPaths.Create(temporaryRoot, instanceId, Path.GetFileName(paths.CatalogRoot));
            var compose = ComposeDeployment.Write(
                request,
                paths,
                instanceId,
                applicationIdentity,
                uid,
                gid,
                image.ImageId,
                catalog.PackageVersion,
                passwordPath,
                verificationToken,
                lifecycleControlToken,
                passwordAuthorityEnabled: false,
                resources,
                outputPaths: outputPaths);
            var rendered = await docker.ComposeAsync(
                compose.ComposeFile,
                compose.EnvironmentFile,
                compose.ProjectName,
                ["config"],
                cancellationToken).ConfigureAwait(false);
            var candidate = CreateManifest(
                request,
                paths,
                instanceId,
                installationId,
                applicationIdentity,
                uid,
                gid,
                verificationToken,
                lifecycleControlToken,
                compose,
                ComposeDeployment.ComputeSha256(rendered.StandardOutput),
                catalog,
                image,
                daemon,
                retainedManifest,
                resources,
                keepPreviousStateSnapshot: true);
            if (candidate != retainedManifest)
            {
                throw new InstallerException("The completed installation differs from its retained immutable manifest.");
            }
            var candidateResult = CreateInstalledResult(
                request,
                paths,
                instanceId,
                installationId,
                applicationIdentity,
                uid,
                gid,
                passwordPath,
                compose,
                ComposeDeployment.ComputeSha256(rendered.StandardOutput),
                catalog,
                image,
                daemon,
                "owner-password-change-required",
                retainedResult.OwnerPasswordIsDefault,
                retainedResult.CompletedUtc);
            if (candidateResult != retainedResult)
            {
                if (candidateResult with { ComposeModelSha256 = retainedResult.ComposeModelSha256 } == retainedResult)
                {
                    return true;
                }
                throw new InstallerException("The completed installation differs from its retained result.");
            }
            return false;
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                SafeFileSystem.MakeTreeOwnerWritable(temporaryRoot);
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static InstanceManifest CreateManifest(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        Guid installationId,
        Guid applicationIdentity,
        uint uid,
        uint gid,
        string verificationToken,
        string lifecycleControlToken,
        ComposeFiles compose,
        string composeModelSha256,
        CatalogInstallationIdentity catalog,
        ImageInstallationIdentity image,
        DockerDaemonIdentity daemon,
        InstanceManifest? previous,
        ContainerResourceLimits? resources,
        bool keepPreviousStateSnapshot = false)
    {
        var location = HVO.SkyMonitor.AgentCore.DeploymentLocationSnapshot.Create(
            $"installer-{instanceId:D}",
            1,
            "installer",
            null,
            DateTimeOffset.UnixEpoch,
            null,
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            request.TimeZoneId);
        return new(
            DeploymentSchemaVersions.InstanceManifest,
            "HVO.SkyMonitor",
            "cameraagent-install-v1",
            DeploymentComponent.CameraAgent,
            instanceId,
            request.FriendlyName,
            applicationIdentity,
            $"installer-{instanceId:D}",
            location.Version,
            Convert.ToHexStringLower(Convert.FromHexString(location.CanonicalSha256)),
            request.OwnerEmail,
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            request.TimeZoneId,
            installationId,
            uid,
            gid,
            paths.ProductRoot,
            paths.ConfigRoot,
            paths.StateRoot,
            ComposeDeployment.TemplateVersionFor(request.ReplayProfile),
            compose.ConfigurationSha256,
            compose.RigProfileSha256,
            compose.ScheduleSha256,
            compose.RigProfileName,
            compose.RigProfileVersion,
            compose.ScheduleSchemaVersion,
            compose.ScheduleState,
            ComposeDeployment.ComputeSha256(verificationToken),
            composeModelSha256,
            catalog,
            image,
            previous?.PreviousImage,
            daemon,
            image.UpgradeCompatibility ?? "requires-declared-compatible-migration",
            previous?.CreatedUtc ?? DateTimeOffset.UtcNow,
            previous?.PreviousCatalog,
            InstanceLifecycleCondition.Installed,
            request.BindAddress,
            request.Port,
            previous?.LastLifecycleOperationId,
            previous?.UpdatedUtc,
            ComposeDeployment.ComputeSha256(lifecycleControlToken),
            previous?.PreviousComposeModelSha256,
            (ContractReplayProfile)request.ReplayProfile,
            resources,
            // A completed installation's re-run describes the manifest as it is, retained database snapshot included. Any
            // other install (a fresh one, a resumed one, the reinstall after a state reset) drops it, so a later
            // rollback can never bring back databases from before the reset.
            keepPreviousStateSnapshot ? previous?.PreviousStateSnapshot : null);
    }

    private static InstallationResult CreateInstalledResult(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        Guid installationId,
        Guid applicationIdentity,
        uint uid,
        uint gid,
        string passwordPath,
        ComposeFiles compose,
        string composeModelSha256,
        CatalogInstallationIdentity catalog,
        ImageInstallationIdentity image,
        DockerDaemonIdentity daemon,
        string ownerState,
        bool ownerPasswordIsDefault,
        DateTimeOffset completedUtc)
        => new(
            DeploymentSchemaVersions.InstallationResult,
            InstallationOutcome.Installed,
            installationId,
            instanceId,
            applicationIdentity,
            request.FriendlyName,
            PublicUri(request),
            request.OwnerEmail,
            passwordPath,
            paths.ProductRoot,
            paths.InstanceRoot,
            paths.ConfigRoot,
            paths.StateRoot,
            uid,
            gid,
            ComposeDeployment.TemplateVersionFor(request.ReplayProfile),
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            request.TimeZoneId,
            compose.ConfigurationSha256,
            compose.RigProfileSha256,
            compose.ScheduleSha256,
            compose.RigProfileName,
            compose.RigProfileVersion,
            compose.ScheduleSchemaVersion,
            compose.ScheduleState,
            composeModelSha256,
            catalog,
            image,
            daemon,
            Alive: true,
            Healthy: true,
            ownerState,
            completedUtc,
            (ContractReplayProfile)request.ReplayProfile,
            ownerPasswordIsDefault);

    /// <summary>
    /// Publishes the owner's temporary password: the operator's <c>--password-file</c>, a random password for
    /// <c>--generate-password</c>, or otherwise the documented default. A credential an earlier run already
    /// published is kept unchanged.
    /// </summary>
    private static Task<string> CreateOwnerPasswordAsync(
        InstallRequest request,
        string generatedPasswordPath,
        CancellationToken cancellationToken)
        => request.PasswordFile is null && !request.GeneratePassword
            ? CredentialFile.GetOrCreateKnownAsync(generatedPasswordPath, OwnerCredentialDefaults.Password, cancellationToken)
            : CredentialFile.GetOrCreateAsync(request.PasswordFile, generatedPasswordPath, cancellationToken);

    /// <summary>
    /// Reads the owner's temporary password exactly as CameraAgent seeds it from the mounted password file, so the
    /// bootstrap check authenticates with, and classifies, the password the owner actually has.
    /// </summary>
    private static async Task<string> ReadOwnerPasswordAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        var content = new byte[stream.Length];
        try
        {
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
            return OwnerPasswordFileText.Decode(content);
        }
        catch (System.Text.DecoderFallbackException exception)
        {
            throw new InstallerException("The owner temporary password file is not valid UTF-8.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static async Task<string> ReadOwnerSecretAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        using var reader = new StreamReader(stream);
        return (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();
    }

    private static async Task<InstallRequest> StageImageArchiveAsync(
        InstallRequest request,
        InstallationPaths paths,
        bool allowMutation,
        CancellationToken cancellationToken)
    {
        if (request.ImageArchive is null)
        {
            return request;
        }
        var stagedPath = Path.Combine(paths.DeploymentStateRoot, "image-archive.tar");
        string sha256;
        if (File.Exists(stagedPath))
        {
            await using var stream = SafeFileSystem.OpenOwnerFileRead(stagedPath);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            sha256 = Convert.ToHexStringLower(hash);
        }
        else
        {
            if (!allowMutation)
            {
                throw new InstallerException("The retained offline image archive is missing.");
            }
            sha256 = await SafeFileSystem.CopyPrivateFileAsync(
                request.ImageArchive,
                stagedPath,
                cancellationToken).ConfigureAwait(false);
        }
        if (!string.Equals(sha256, request.ImageArchiveSha256, StringComparison.Ordinal))
        {
            throw new InstallerException("The privately staged image archive does not match its trusted SHA-256.");
        }
        return request with { ImageArchive = stagedPath };
    }

    private static async Task<InstallationState?> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        InstallationState value;
        try
        {
            value = await JsonSerializer.DeserializeAsync(
                stream,
                DeploymentJsonContext.Default.InstallationState,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerException("The installation state is empty.");
        }
        catch (JsonException exception)
        {
            throw new InstallerException("The retained installation state is invalid JSON.", exception);
        }
        if (value.SchemaVersion != DeploymentSchemaVersions.InstallationState ||
            value.InstallationId == Guid.Empty || value.InstanceId == Guid.Empty ||
            !IsSha256(value.RequestSha256) || !Enum.IsDefined(value.Phase) || !Enum.IsDefined(value.Status))
        {
            throw new InstallerException("The retained installation state is invalid or unsupported.");
        }
        return value;
    }

    private static async Task<InstanceManifest?> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        return File.Exists(path)
            ? await CameraAgentLifecycleManager.ReadManifestAsync(path, cancellationToken).ConfigureAwait(false)
            : null;
    }

    private static async Task<InstallationResult> ReadResultAsync(string path, CancellationToken cancellationToken)
    {
        return await CameraAgentLifecycleManager.ReadResultAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsValid(CatalogInstallationIdentity? value)
        => ProductionCatalog.IsPinned(value) &&
           HasValue(value.InstallRoot) && IsSha256(value.ManifestSha256) && value.Source == "local-offline";

    internal static bool IsValid(ImageInstallationIdentity? value, CameraAgentReplayProfile replayProfile)
        => value is not null && HasValue(value.Source) && HasValue(value.ImmutableReference) &&
           System.Text.RegularExpressions.Regex.IsMatch(
               value.ImmutableReference,
               "^(sha256:[a-f0-9]{64}|[^@\\s]+@sha256:[a-f0-9]{64})$",
               System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
           value.ImageId is not null && value.ImageId.StartsWith("sha256:", StringComparison.Ordinal) &&
           IsSha256(value.ImageId["sha256:".Length..]) && value.Architecture is "amd64" or "arm64" &&
            value.Component == "CameraAgent" && value.ConfigurationContract == "cameraagent-install-v1" &&
             CameraAgentImageContract.IsKnownCatalogContract(value.CatalogContract) && IsSourceRevision(value.SourceRevision) &&
             (replayProfile == CameraAgentReplayProfile.InProcess ||
              value.ReplayRunnerContract == "local-replay-runner-v1") &&
            (value.ArchiveSha256 is null || IsSha256(value.ArchiveSha256));

    private static bool IsValid(DockerDaemonIdentity? value)
        => value is not null && HasValue(value.Id) && HasValue(value.Name) && HasValue(value.Architecture) && HasValue(value.ServerVersion);

    private static bool IsSha256(string? value)
        => value is not null && value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsSourceRevision(string? value)
        => value is { Length: 40 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasValue(string? value) => !string.IsNullOrEmpty(value);

    private static async Task<InstallationState> RecordPhaseAsync(
        InstallationPaths paths,
        InstallationState basis,
        InstallationPhase phase,
        CancellationToken cancellationToken,
        InstallationStatus status = InstallationStatus.Running)
    {
        var state = basis with
        {
            Phase = phase,
            Status = status,
            UpdatedUtc = DateTimeOffset.UtcNow,
            FailureCode = null,
            FailureMessage = null
        };
        await SafeFileSystem.WriteJsonAtomicAsync(
            paths.StatePath,
            state,
            DeploymentJsonContext.Default.InstallationState,
            cancellationToken).ConfigureAwait(false);
        return state;
    }

    private static Task RecordFailureAsync(
        InstallationPaths paths,
        InstallationState basis,
        Exception exception,
        CancellationToken cancellationToken)
        => SafeFileSystem.WriteJsonAtomicAsync(
            paths.StatePath,
            basis with
            {
                Status = InstallationStatus.Failed,
                UpdatedUtc = DateTimeOffset.UtcNow,
                FailureCode = exception.GetType().Name,
                FailureMessage = Redaction.SafeDiagnostic(exception.Message)
            },
            DeploymentJsonContext.Default.InstallationState,
            cancellationToken);

    private static void EnsureInstanceRootCanBeOwned(InstallationPaths paths, bool hasState)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            ".deployment.lock", "config", "state", "application-identity.json", "instance-manifest.json"
        };
        var unexpected = Directory.EnumerateFileSystemEntries(paths.InstanceRoot)
            .Select(Path.GetFileName)
            .FirstOrDefault(name => name is not null && !allowed.Contains(name));
        if (unexpected is not null || !hasState && Directory.EnumerateFileSystemEntries(paths.InstanceRoot)
                .Any(path => Path.GetFileName(path) != ".deployment.lock"))
        {
            throw new InstallerException("The instance root contains unowned content and cannot be adopted.");
        }
    }

    private static void EnsurePortAvailable(string bindAddress, int port)
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Parse(bindAddress), port));
        }
        catch (SocketException exception)
        {
            throw new InstallerException($"TCP port {port} is unavailable on {bindAddress}.", exception);
        }
    }

    /// <summary>
    /// Evaluates the host prerequisites and returns the daemon identity they were read from, with the advisory findings
    /// as warnings for the structured result. Both storage requirements (see <see cref="StorageRequirements"/>) are
    /// measured on the filesystem that actually stores each root, and summed when they share one.
    /// </summary>
    private static async Task<(DockerDaemonIdentity Daemon, IReadOnlyList<InstallationWarning> Warnings)> EnsureHostPrerequisitesAsync(
        InstallRequest request,
        DockerClient docker,
        IHostEnvironment hostEnvironment,
        ContainerResourceLimits limits,
        CancellationToken cancellationToken)
    {
        var capabilities = await docker.ReadHostCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        var (productRootBytes, dockerRootBytes) = StorageRequirements(request.CatalogBundle!, request.ImageArchive);
        var report = HostPrerequisites.Evaluate(
            capabilities,
            request.ProductRoot,
            productRootBytes,
            dockerRootBytes,
            hostEnvironment,
            limits);
        if (!report.Satisfied)
        {
            throw new HostPrerequisiteException(report);
        }
        // An advisory finding does not refuse the installation, so it is rendered here or the operator would never see
        // it. A --json invocation reserves standard error for one error object, as the state preflight does, and
        // reports the finding in its structured result instead.
        if (report.Findings.Count > 0 && !request.Json)
        {
            await Console.Error.WriteLineAsync(HostPrerequisites.Render(report)).ConfigureAwait(false);
        }
        return (capabilities.Daemon, HostPrerequisites.Warnings(report));
    }

    /// <summary>
    /// The bytes an installation needs on each filesystem: <c>2 × (catalog bundle + image archive) + 1 GiB</c> on the one
    /// holding the product root, and <c>2 × image archive + 1 GiB</c> on the one holding Docker's data root.
    /// </summary>
    internal static (long ProductRootBytes, long DockerRootBytes) StorageRequirements(string catalogBundle, string? imageArchive)
    {
        var bundleBytes = Directory.EnumerateFiles(catalogBundle).Sum(path => new FileInfo(path).Length);
        var archiveBytes = imageArchive is null ? 0 : new FileInfo(imageArchive).Length;
        return (
            checked((2 * (bundleBytes + archiveBytes)) + HostPrerequisites.StorageHeadroomBytes),
            checked((2 * archiveBytes) + HostPrerequisites.StorageHeadroomBytes));
    }

    internal static void EnsureImageSupportsCatalog(ImageInstallationIdentity image, string catalogId)
    {
        if (!CameraAgentImageContract.SupportsCatalog(image.CatalogContract, catalogId))
            throw new InstallerException("The CameraAgent image cannot resolve the selected catalog.");
    }

    private static void ValidateCatalogInput(string bundlePath)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hvo-installer-catalog-preflight-{Guid.NewGuid():N}");
        try
        {
            _ = CatalogInstaller.Install(bundlePath, temporaryRoot, Guid.NewGuid());
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                SafeFileSystem.MakeTreeOwnerWritable(temporaryRoot);
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    /// <summary>
    /// The identity of an install request, which a re-run or a resume must match. A release install binds the release
    /// it read, by version and release.json SHA-256, and how its image reaches Docker, rather than the folder it was
    /// read from, so the same release moved to another folder is the same install and a different release in the same
    /// folder is not.
    /// </summary>
    internal static string ComputeRequestSha256(
        InstallRequest request,
        Guid instanceId,
        VerifiedRelease? release = null)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            instanceId,
            request.FriendlyName,
            request.OwnerEmail,
            request.BindAddress,
            request.Port,
            request.ProductRoot,
            request.CatalogBundle,
            // The retired signed catalog release keeps its null slot, so every install identity recorded before it was
            // retired (#1309) is unchanged.
            CatalogRelease = (object?)null,
            request.ImageReference,
            request.ImageArchive,
            request.ImageArchiveSha256,
            request.PasswordFile,
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            request.TimeZoneId,
            request.AcknowledgePlaintextHttp,
            request.ReplayProfile
        });
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(identity));
        if (release is not null)
        {
            sha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                request = sha256,
                release = new { release.Manifest.Version, release.ManifestSha256 },
                imageSource = request.ImageSource ?? ReleaseImageSource.Archive
            })));
        }
        if (request.Cpus is null && request.Memory is null)
        {
            return sha256;
        }
        // Container limits joined the request identity after installations had already begun without them. A request
        // that leaves both to the default keeps the identity it always had, so such an installation still resumes and
        // re-runs; one that sets either binds the normalized values it set.
        var resources = request.ContainerResources();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request = sha256,
            cpus = resources.CpusSetByOperator ? CameraAgentContainerResources.FormatCpus(resources.Cpus) : null,
            memoryBytes = resources.MemorySetByOperator ? resources.MemoryBytes : (long?)null
        })));
    }

    internal static bool RequestIdentityMatches(
        string retainedSha256,
        InstallRequest request,
        Guid instanceId,
        VerifiedRelease? release = null)
        => string.Equals(
               retainedSha256,
               ComputeRequestSha256(request, instanceId, release),
               StringComparison.Ordinal) ||
           // The legacy identity predates replay profiles, container limits and releases, so a request using any of them
           // never matches it.
           release is null && request.ReplayProfile == CameraAgentReplayProfile.InProcess && request.Cpus is null &&
           request.Memory is null &&
           string.Equals(
               retainedSha256,
               ComputeLegacyRequestSha256(request, instanceId),
               StringComparison.Ordinal);

    internal static string ComputeLegacyRequestSha256(
        InstallRequest request,
        Guid instanceId)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            instanceId,
            request.FriendlyName,
            request.OwnerEmail,
            request.BindAddress,
            request.Port,
            request.ProductRoot,
            request.CatalogBundle,
            CatalogRelease = (object?)null,
            request.ImageReference,
            request.ImageArchive,
            request.ImageArchiveSha256,
            request.PasswordFile,
            request.LatitudeDegrees,
            request.LongitudeDegrees,
            request.ElevationMeters,
            request.TimeZoneId,
            request.AcknowledgePlaintextHttp
        });
        return Convert.ToHexStringLower(SHA256.HashData(identity));
    }

    private static Uri PublicUri(InstallRequest request)
    {
        var host = request.BindAddress is "0.0.0.0" or "::" ? "localhost" : request.BindAddress;
        return new UriBuilder("http", host, request.Port).Uri;
    }
}
