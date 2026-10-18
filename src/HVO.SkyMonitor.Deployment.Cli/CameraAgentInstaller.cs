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
    /// <summary>
    /// Runs at each step an interruption can fall between, named <c>configuration-written</c>, <c>limits-verified</c>,
    /// <c>limits-manifest-written</c> or <c>limits-result-written</c>. Interruption-injection tests cancel or fail the run
    /// there; production leaves it empty.
    /// </summary>
    internal static Action<string> StepCompleted { get; set; } = static _ => { };

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

        // Admission: every check a plan and a run make before anything changes, shared so that a plan refuses exactly
        // where the run would, and none of which writes anything, so a refusal here leaves nothing behind (#1315).
        if (uid == 0)
        {
            throw InstallerException.Refused("Run the installer as the Docker-capable runtime user, not as root or through sudo.");
        }
        var retained = await ReadRetainedAsync(paths, cancellationToken).ConfigureAwait(false);
        if (retained is null)
        {
            if (File.Exists(paths.ManifestPath))
                _ = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
            if (File.Exists(paths.ResultPath))
                _ = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);
        }
        // Read before anything is prepared, so the host is checked against the limits this run would apply. A run that
        // goes on to change something reads everything again under the instance lock.
        var hostCheckLimits = HostCheckLimits(request, retained);

        // The release is read and checked against its SHA256SUMS, its platform selected, and its archive verified before
        // the Docker daemon is contacted, so an unsupported architecture or tampered file fails with nothing yet mutated.
        var acquiredImage = await ReleaseAcquirer.AcquireImageAsync(request, cancellationToken).ConfigureAwait(false);
        request = ApplyReleaseImage(request, acquiredImage);

        // Every host prerequisite is evaluated, and every finding reported together, before the product root is
        // prepared or anything else is created.
        var (preflightDaemon, warnings) = await EnsureHostPrerequisitesAsync(
                request, docker, hostEnvironment, hostCheckLimits, cancellationToken)
            .ConfigureAwait(false);
        // The clock gate protects every production install, whichever root the host policy names; only an admitted
        // test root is exempt.
        if (request.IsProductionRoot && !NativeLinux.IsClockSynchronized())
        {
            throw new InstallerException("The host clock is not synchronized.");
        }
        if (retained is { Completed: false } &&
            !RequestIdentityMatches(retained.State.RequestSha256, identityRequest, instanceId, acquiredImage?.VerifiedRelease))
        {
            // An unfinished installation resumes only with exactly the inputs it began with. A completed one is held to
            // the instance its records describe instead, so a re-run may change its container limits (install contract
            // section 7) and nothing else.
            throw InstallerException.Refused("The retained installation state belongs to different immutable inputs.");
        }
        if (request.Resume && retained is null)
        {
            throw new InstallerException("No resumable installation state exists for --instance-id.");
        }
        // A run that installs the catalog first proves the bundle in a scratch root; a plan inspects it where it lies,
        // and a re-run validates the version the instance already has, so neither writes anything.
        if (!request.DryRun && retained is not { Completed: true })
        {
            ValidateCatalogInput(request.CatalogBundle!);
        }
        // The bundle's approved specification selects the side-by-side catalog root this instance installs into.
        var catalogId = ProductionCatalog.ReadBundleSpecification(request.CatalogBundle!).CatalogId;
        paths = paths.WithCatalog(catalogId);
        // A release's image is proved to be the release's, and an image this instance can run, and a new installation
        // imports it, before anything is claimed for it: no product root, state, application identity, catalog or staged
        // archive exists yet when it is refused or when Docker cannot import it. A plan imports nothing.
        var releaseImported = false;
        if (acquiredImage is not null)
        {
            (acquiredImage, releaseImported) = await EnsureReleaseImageAsync(
                    request, acquiredImage, docker, catalogId, import: retained is null && !request.DryRun, cancellationToken)
                .ConfigureAwait(false);
        }
        if (retained is { Completed: true })
        {
            return request.DryRun
                ? await PlanReRunAsync(request, retained, paths, instanceId, docker, acquiredImage, uid, gid, warnings, cancellationToken)
                    .ConfigureAwait(false)
                : await ReRunAsync(
                        request, paths, instanceId, docker, acquiredImage, preflightDaemon, warnings, uid, gid, ownerClientFactory,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        if (retained is not null)
        {
            await EnsureRetainedStateCompatibleAsync(request, paths, instanceId, retained, docker, acquiredImage, uid, gid, cancellationToken)
                .ConfigureAwait(false);
        }
        if (request.DryRun)
        {
            var plan = await PlanAsync(request, paths, instanceId, docker, acquiredImage, cancellationToken)
                .ConfigureAwait(false);
            var plannedPaths = paths.WithCatalog(Path.GetFileName(plan.Catalog.InstallRoot));
            var plannedRecord = InstallRecordLocation.For(plannedPaths.ProductRoot, request.ProductionProductRoot);
            return plan with
            {
                Warnings = warnings,
                Plan = InstalledPlan(
                    plannedPaths,
                    instanceId,
                    plan.Image.ImageId,
                    request.ContainerResources(),
                    FreshInstallFacts.Observe(plannedPaths),
                    await FreshRecordActionAsync(plannedRecord, instanceId, cancellationToken).ConfigureAwait(false),
                    plannedRecord)
            };
        }

        var freshFacts = FreshInstallFacts.Observe(paths);
        // The first write. Everything above changed nothing, so a refusal there exits 3 with nothing to undo; from here
        // on a gate that decides not to go ahead is a failure, journaled so that a re-run resumes it (#1315).
        await PrivilegedPreparation.PrepareAsync(paths, uid, gid, processRunner, cancellationToken).ConfigureAwait(false);
        using var productLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);
        if (!File.Exists(paths.StatePath))
        {
            portAvailabilityProbe(request.BindAddress, request.Port);
        }

        var requestSha256 = ComputeRequestSha256(identityRequest, instanceId, acquiredImage?.VerifiedRelease);
        var existingState = await ReadStateAsync(paths.StatePath, cancellationToken).ConfigureAwait(false);
        EnsureInstanceRootCanBeOwned(paths, existingState is not null);
        // Read again under the lock: another run may have moved the installation on since it was admitted.
        if (existingState?.Status == InstallationStatus.Completed)
        {
            throw new InstallerException("Another run completed this installation while this one was starting; run it again to check it.");
        }
        if (existingState is not null && !RequestIdentityMatches(
                existingState.RequestSha256,
                identityRequest,
                instanceId,
                acquiredImage?.VerifiedRelease))
        {
            throw new InstallerException("The retained installation state belongs to different immutable inputs.");
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
        state = await RecordPhaseAsync(paths, state, InstallationPhase.Preflight, cancellationToken).ConfigureAwait(false);
        try
        {
            var applicationIdentity = await GetOrCreateApplicationIdentityAsync(
                paths.ApplicationIdentityPath,
                allowCreation: true,
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
            state = await RecordPhaseAsync(paths, state, InstallationPhase.Prepare, cancellationToken).ConfigureAwait(false);

            state = await RecordPhaseAsync(paths, state, InstallationPhase.Catalog, cancellationToken).ConfigureAwait(false);
            var catalog = CatalogInstaller.Install(request.CatalogBundle!, paths.CatalogRoot, installationId);

            state = await RecordPhaseAsync(paths, state, InstallationPhase.Image, cancellationToken).ConfigureAwait(false);
            var effectiveRequest = await StageImageArchiveAsync(
                request,
                paths,
                allowMutation: true,
                cancellationToken).ConfigureAwait(false);
            // A release image imported before anything was claimed is found and checked again here, not imported twice.
            var (daemon, image) = await docker.PrepareImageAsync(
                effectiveRequest,
                allowMutation: !releaseImported,
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

            state = await RecordPhaseAsync(paths, state, InstallationPhase.Configuration, cancellationToken).ConfigureAwait(false);
            var generatedPasswordPath = Path.Combine(paths.ConfigRoot, "owner-bootstrap", "temporary-password");
            var passwordPath = await CreateOwnerPasswordAsync(
                    request,
                    generatedPasswordPath,
                    cancellationToken).ConfigureAwait(false);
            var verificationTokenPath = await CredentialFile.GetOrCreateAsync(
                null,
                Path.Combine(paths.ConfigRoot, "installation-verification", "token"),
                cancellationToken).ConfigureAwait(false);
            var verificationToken = await ReadOwnerSecretAsync(verificationTokenPath, cancellationToken).ConfigureAwait(false);
            var lifecycleControlTokenPath = await CredentialFile.GetOrCreateAsync(
                null,
                Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"),
                cancellationToken).ConfigureAwait(false);
            var lifecycleControlToken = await ReadOwnerSecretAsync(lifecycleControlTokenPath, cancellationToken).ConfigureAwait(false);
            var passwordAuthorityEnabled = existingState is null || existingState.Phase < InstallationPhase.OwnerSeeded;
            var resources = request.ContainerResources();
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
            StepCompleted("configuration-written");

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

            state = await RecordPhaseAsync(paths, state, InstallationPhase.Compose, cancellationToken).ConfigureAwait(false);
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
                resources);
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ManifestPath,
                manifest,
                DeploymentJsonContext.Default.InstanceManifest,
                cancellationToken).ConfigureAwait(false);

            state = await RecordPhaseAsync(paths, state, InstallationPhase.Startup, cancellationToken).ConfigureAwait(false);
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

            state = await RecordPhaseAsync(paths, state, InstallationPhase.OwnerBootstrap, cancellationToken).ConfigureAwait(false);
            // Whether the owner's first sign-in is the documented default comes from the credential actually seeded,
            // not from this invocation's flags, because a resumed installation keeps the credential its first run wrote.
            var password = await ReadOwnerPasswordAsync(passwordPath, cancellationToken).ConfigureAwait(false);
            var ownerPasswordIsDefault = string.Equals(password, OwnerCredentialDefaults.Password, StringComparison.Ordinal);
            var ownerState = await ownerClient.ReadStateAsync(request.OwnerEmail, password, cancellationToken)
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
                    AllowCompletedPasswordReplacement: false),
                cancellationToken).ConfigureAwait(false);

            manifest = manifest with { ComposeModelSha256 = composeModelSha256 };
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ManifestPath,
                manifest,
                DeploymentJsonContext.Default.InstanceManifest,
                cancellationToken).ConfigureAwait(false);

            state = await RecordPhaseAsync(paths, state, InstallationPhase.Verification, cancellationToken).ConfigureAwait(false);
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
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath,
                result,
                DeploymentJsonContext.Default.InstallationResult,
                cancellationToken).ConfigureAwait(false);
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
            _ = await RecordPhaseAsync(
                paths,
                state,
                InstallationPhase.Completed,
                cancellationToken,
                InstallationStatus.Completed).ConfigureAwait(false);
            // The warnings and the plan describe this invocation, so only the reported result carries them.
            return result with
            {
                Warnings = warnings,
                Plan = InstalledPlan(paths, instanceId, image.ImageId, resources, freshFacts, recordAction, recordLocation)
            };
        }
        catch (OperationCanceledException exception)
        {
            await RecordFailureAsync(paths, state, exception, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(paths, state, exception, CancellationToken.None).ConfigureAwait(false);
            // The installation has written by now, so even a gate that decides here not to go ahead cannot say nothing
            // changed: it is a failure, journaled above, that a re-run resumes (#1315).
            throw exception switch
            {
                InstallerException { IsRefusal: true } refusal => new InstallerException(refusal.Message, refusal),
                InstallerException installerException => installerException,
                _ => new InstallerException("Installation failed at a durable phase boundary.", exception)
            };
        }
    }

    /// <summary>
    /// Proves the persisted state an unfinished installation left behind can be used by this image, before the run
    /// changes anything and in its plan alike (#1315). The check only reads, and only an image Docker already holds can
    /// be checked: one this run has yet to import is checked by the run's own preflight once imported, where a refusal is
    /// a failure because the run has written by then.
    /// </summary>
    private static async Task EnsureRetainedStateCompatibleAsync(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        RetainedInstallation retained,
        DockerClient docker,
        AcquiredImage? release,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.StateRoot))
        {
            return;
        }
        ImageInstallationIdentity image;
        try
        {
            var effectiveRequest = await StageImageArchiveAsync(request, paths, allowMutation: false, cancellationToken).ConfigureAwait(false);
            (_, image) = await docker.PrepareImageAsync(effectiveRequest, allowMutation: false, release?.Image, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InstallerException exception) when (!exception.IsRefusal)
        {
            return;
        }
        await CameraAgentStatePreflight.EnsureCompatibleAsync(
                paths,
                instanceId,
                image,
                retained.Manifest is { } manifest
                    ? manifest.Image.UpgradeCompatibility ?? manifest.UpgradeCompatibility
                    : image.UpgradeCompatibility,
                uid,
                gid,
                (ContractReplayProfile)request.ReplayProfile,
                persist: false,
                renderToStandardError: !request.Json,
                cancellationToken,
                CameraAgentStateContractPolicy.AllowLegacy,
                release?.Release.Tag)
            .ConfigureAwait(false);
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
    /// removed. Only a new installation imports (<paramref name="import"/>): one that already has state (a resumed or
    /// repeated one) imports nothing here, and its image step does, as before, from the same verified inputs; a plan
    /// imports nothing at all. Returns the release, with the identities its registry gave, and whether the image is now
    /// imported.
    /// </summary>
    private static async Task<(AcquiredImage Release, bool Imported)> EnsureReleaseImageAsync(
        InstallRequest request,
        AcquiredImage release,
        DockerClient docker,
        string catalogId,
        bool import,
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
            release = release with { Image = selection, RegistryImage = registryImage };
        }
        if (!import)
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

    /// <summary>
    /// The plan of a fresh install, or of the resumption of one, computed without writing anything anywhere, not even a
    /// temporary file (#1315): the catalog is inspected where it lies, the image is inspected, and the Compose files are
    /// rendered in memory. Docker renders a Compose model only from files, so a plan reports no model hash. A release
    /// image Docker does not hold yet, which the run would import, is described as it was read and checked from the
    /// release's archive or registry.
    /// </summary>
    private static async Task<InstallationResult> PlanAsync(
        InstallRequest request,
        InstallationPaths finalPaths,
        Guid instanceId,
        DockerClient docker,
        AcquiredImage? release,
        CancellationToken cancellationToken)
    {
        var applicationIdentity = Guid.NewGuid();
        var catalog = CatalogInstaller.Inspect(request.CatalogBundle!, finalPaths.CatalogRoot);
        var (daemon, image) = release?.CheckedImage is { } checkedImage &&
                              !await docker.HoldsReleaseImageAsync(release.Image, cancellationToken).ConfigureAwait(false)
            ? (await docker.PreflightAsync(cancellationToken).ConfigureAwait(false), checkedImage)
            : await docker.PrepareImageAsync(request, allowMutation: false, release?.Image, cancellationToken)
                .ConfigureAwait(false);
        if (!IsValid(image, request.ReplayProfile))
            throw new InstallerException("The CameraAgent image does not declare the required current configuration and catalog contracts.");
        EnsureImageSupportsCatalog(image, catalog.CatalogId);
        var compose = ComposeDeployment.Render(
            request,
            finalPaths,
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
            request.ContainerResources()).Files;
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
            string.Empty,
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
