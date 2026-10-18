using System.Globalization;
using HVO.SkyMonitor.Deployment.Contracts;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;

namespace HVO.SkyMonitor.Deployment;

/// <summary>
/// The parts of an install that concern an instance already there (install contract sections 5 and 7): the plan of a
/// re-run, the check that a re-run describes the instance its records describe, and a change of its container limits.
/// </summary>
internal sealed partial class CameraAgentInstaller
{
    /// <summary>
    /// What an instance's own records hold before an install touches it, read without the instance lock and without
    /// changing anything. A run that goes on to change something reads them again under the lock.
    /// </summary>
    internal sealed record RetainedInstallation(
        InstallationState State,
        InstanceManifest? Manifest,
        InstallationResult? Result)
    {
        public bool Completed => State.Status == InstallationStatus.Completed;
    }

    private static async Task<RetainedInstallation?> ReadRetainedAsync(InstallationPaths paths, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(paths.StatePath, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }
        var manifest = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        var result = File.Exists(paths.ResultPath)
            ? await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false)
            : null;
        return new RetainedInstallation(state, manifest, result);
    }

    /// <summary>
    /// The limits the host must be able to enforce for this run: those a re-run of a completed installation moves it to,
    /// otherwise those the request asks for. An instance that records no limits and is given none keeps its original
    /// limits, and the host is checked against the default, as before limits were recorded.
    /// </summary>
    private static ContainerResourceLimits HostCheckLimits(InstallRequest request, RetainedInstallation? retained)
        => retained is { Completed: true, Manifest: { } manifest }
            ? CameraAgentContainerResources.ReRunTarget(manifest.Resources, request.Cpus, request.Memory) ?? request.ContainerResources()
            : request.ContainerResources();

    /// <summary>
    /// Why a re-run does not describe the completed instance its records describe, or <see langword="null"/> when it
    /// does. Only the container limits may differ: they change with a re-run. The image is compared once it is resolved.
    /// </summary>
    internal static string? InstalledDifference(InstallRequest request, InstallationPaths paths, InstanceManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        var differences = new List<string>();
        void Compare<T>(string option, T installed, T requested)
        {
            if (!EqualityComparer<T>.Default.Equals(installed, requested))
            {
                differences.Add(string.Create(CultureInfo.InvariantCulture, $"{option} ({installed}, not {requested})"));
            }
        }
        Compare("--friendly-name", manifest.FriendlyName, request.FriendlyName);
        Compare("--owner-email", manifest.OwnerEmail, request.OwnerEmail);
        Compare("--bind-address", manifest.BindAddress, request.BindAddress);
        Compare("--port", manifest.Port, request.Port);
        Compare("--product-root", manifest.ProductRoot, paths.ProductRoot);
        Compare("--latitude", manifest.LatitudeDegrees, request.LatitudeDegrees);
        Compare("--longitude", manifest.LongitudeDegrees, request.LongitudeDegrees);
        Compare("--elevation", manifest.ElevationMeters, request.ElevationMeters);
        Compare("--time-zone", manifest.TimeZoneId, request.TimeZoneId);
        Compare("--replay-profile", manifest.ReplayProfile, (ContractReplayProfile)request.ReplayProfile);
        return differences.Count == 0
            ? null
            : $"Instance {manifest.InstanceId:D} is installed with a different {string.Join(", ", differences)}. A re-run may change only --cpus and --memory; give the answers it was installed with.";
    }

    private static string DifferentImage(InstanceManifest manifest, ImageInstallationIdentity requested)
        => $"Instance {manifest.InstanceId:D} runs image {manifest.Image.ImageId}, not the requested {requested.ImageId}. A re-run keeps the image; change it with 'hvo-sky-install cameraagent upgrade --instance-id {manifest.InstanceId:D}', or re-run naming the image it runs.";

    /// <summary>
    /// A completed instance as a re-run finds it, read without changing anything: what the re-run and its plan both
    /// check before anything changes, and what the re-run would change (#1315).
    /// </summary>
    internal sealed record CompletedInstance(
        InstanceManifest Manifest,
        InstallationResult Result,
        InstallationPaths Paths,
        CatalogInstallationIdentity Catalog,
        ImageInstallationIdentity Image,
        DockerDaemonIdentity Daemon,
        Guid ApplicationIdentity,
        string VerificationToken,
        string LifecycleControlToken,
        ComposeFiles Compose,
        ContainerResourceLimits? Target,
        bool LimitsChange,
        bool FilesDiffer,
        bool ContainerDown,
        bool ResultModelStale,
        InstallRecordLocation RecordLocation,
        DeploymentPlanAction RecordAction)
    {
        /// <summary>The container must be started again from Compose files written for <see cref="Target"/>.</summary>
        public bool NeedsDeploy => LimitsChange || FilesDiffer || ContainerDown;

        public bool HasChanges => NeedsDeploy || ResultModelStale || RecordAction != DeploymentPlanAction.Unchanged;
    }

    /// <summary>
    /// The checks a re-run of a completed installation and its plan share, all of which only read: the instance its
    /// records describe must be the one the answers describe, its files and container must still be what its records
    /// describe (rendered in memory and compared, never rendered into a temporary directory), and its persisted state
    /// must suit its image. What differs from the records is reported as a change, so a re-run puts it back.
    /// </summary>
    private static async Task<CompletedInstance> AssessCompletedAsync(
        InstallRequest request,
        RetainedInstallation retained,
        InstallationPaths paths,
        Guid instanceId,
        DockerClient docker,
        AcquiredImage? release,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var manifest = retained.Manifest ?? throw new InstallerException("A completed installation requires its retained instance manifest.");
        var result = retained.Result ?? throw new InstallerException("A completed installation requires its retained installation result.");
        if (manifest.Product != "HVO.SkyMonitor" ||
            manifest.Component != DeploymentComponent.CameraAgent ||
            manifest.InstanceId != instanceId ||
            manifest.InstallationId != retained.State.InstallationId ||
            manifest.ProductRoot != paths.ProductRoot ||
            manifest.ConfigRoot != paths.ConfigRoot ||
            manifest.StateRoot != paths.StateRoot)
        {
            throw new InstallerException("The retained instance manifest does not correlate with this installation.");
        }
        if (manifest.LifecycleCondition == InstanceLifecycleCondition.Uninstalled)
        {
            throw InstallerException.Refused("A preserved uninstalled instance must be restored with cameraagent reinstall.");
        }
        if (InstalledDifference(request, paths, manifest) is { } difference)
        {
            throw InstallerException.Refused(difference);
        }
        paths = paths.WithCatalog(manifest.Catalog.CatalogId);
        var catalog = CatalogInstaller.ValidateExisting(request.CatalogBundle!, paths.CatalogRoot);
        if (catalog.CatalogId != manifest.Catalog.CatalogId || catalog.PackageVersion != manifest.Catalog.PackageVersion)
        {
            throw InstallerException.Refused(
                $"The instance uses catalog {manifest.Catalog.CatalogId} {manifest.Catalog.PackageVersion}, not the requested {catalog.CatalogId} {catalog.PackageVersion}; change it with catalog select.");
        }
        var effectiveRequest = await StageImageArchiveAsync(request, paths, allowMutation: false, cancellationToken).ConfigureAwait(false);
        var (daemon, image) = await docker.PrepareImageAsync(effectiveRequest, allowMutation: false, release?.Image, cancellationToken)
            .ConfigureAwait(false);
        if (image.ImageId != manifest.Image.ImageId)
        {
            throw InstallerException.Refused(DifferentImage(manifest, image));
        }
        if (!IsValid(image, request.ReplayProfile))
            throw new InstallerException("The CameraAgent image does not declare the required current configuration and catalog contracts.");
        EnsureImageSupportsCatalog(image, catalog.CatalogId);
        if (request.ReplayProfile == CameraAgentReplayProfile.InProcess &&
            manifest.Image.ReplayRunnerContract is null && result.Image.ReplayRunnerContract is null)
        {
            image = image with { ReplayRunnerContract = null };
        }
        var applicationIdentity = await GetOrCreateApplicationIdentityAsync(
            paths.ApplicationIdentityPath, allowCreation: false, cancellationToken).ConfigureAwait(false);
        if (result.InstallationId != manifest.InstallationId || result.InstanceId != instanceId ||
            result.ApplicationIdentity != applicationIdentity || manifest.ApplicationIdentity != applicationIdentity)
        {
            throw new InstallerException("The retained installation result does not correlate with this installation.");
        }
        var verificationToken = await ReadOwnerSecretAsync(
            Path.Combine(paths.ConfigRoot, "installation-verification", "token"), cancellationToken).ConfigureAwait(false);
        var lifecycleControlToken = await ReadOwnerSecretAsync(
            Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"), cancellationToken).ConfigureAwait(false);

        // The files the records describe, rendered in memory. When the instance's files are those, Docker renders the
        // model from them where they lie; when they are not, the records' model is the one a re-run puts back.
        var recorded = ComposeDeployment.Render(
            request, paths, instanceId, applicationIdentity, uid, gid, image.ImageId, catalog.PackageVersion, result.PasswordFile,
            verificationToken, lifecycleControlToken, passwordAuthorityEnabled: false, manifest.Resources);
        var filesMatch = ComposeDeployment.Matches(recorded, paths, instanceId, uid, gid);
        var model = filesMatch
            ? ComposeDeployment.ComputeSha256((await docker.ComposeAsync(
                    recorded.Files.ComposeFile, recorded.Files.EnvironmentFile, recorded.Files.ProjectName, ["config"], cancellationToken)
                .ConfigureAwait(false)).StandardOutput)
            : manifest.ComposeModelSha256;
        var candidate = CreateManifest(
            request, paths, instanceId, manifest.InstallationId, applicationIdentity, uid, gid, verificationToken,
            lifecycleControlToken, recorded.Files, model, catalog, image, daemon, manifest, manifest.Resources,
            keepPreviousStateSnapshot: true);
        if (candidate != manifest)
        {
            throw new InstallerException("The completed installation differs from its retained immutable manifest.");
        }
        var candidateResult = CreateInstalledResult(
            request, paths, instanceId, manifest.InstallationId, applicationIdentity, uid, gid, result.PasswordFile, recorded.Files,
            result.ComposeModelSha256, catalog, image, daemon, "owner-password-change-required", result.OwnerPasswordIsDefault,
            result.CompletedUtc);
        if (candidateResult != result)
        {
            throw new InstallerException("The completed installation differs from its retained result.");
        }

        // Persisted state is held to the image before anything changes, so its refusal changes nothing.
        await CameraAgentStatePreflight.EnsureCompatibleAsync(
                paths,
                instanceId,
                image,
                manifest.Image.UpgradeCompatibility ?? manifest.UpgradeCompatibility,
                uid,
                gid,
                (ContractReplayProfile)request.ReplayProfile,
                persist: false,
                renderToStandardError: !request.Json,
                cancellationToken,
                CameraAgentStateContractPolicy.AllowLegacy,
                release?.Release.Tag)
            .ConfigureAwait(false);

        var target = CameraAgentContainerResources.ReRunTarget(manifest.Resources, request.Cpus, request.Memory);
        var location = InstallRecordLocation.For(paths.ProductRoot, request.ProductionProductRoot);
        var record = await InstallRecordStore.ReadAsync(location, cancellationToken).ConfigureAwait(false);
        var existing = record?.Instances.FirstOrDefault(instance => instance.InstanceId == manifest.InstanceId);
        var entry = await InstallRecordStore.ProjectAsync(
                paths, manifest with { Resources = target }, InstallRecordEvent.Install, existing, cancellationToken)
            .ConfigureAwait(false);
        return new CompletedInstance(
            manifest,
            result,
            paths,
            catalog,
            image,
            daemon,
            applicationIdentity,
            verificationToken,
            lifecycleControlToken,
            recorded.Files,
            target,
            target != manifest.Resources,
            !filesMatch,
            !await RunsAsync(docker, recorded.Files, image, cancellationToken).ConfigureAwait(false),
            result.ComposeModelSha256 != manifest.ComposeModelSha256,
            location,
            existing is null ? DeploymentPlanAction.Create
            : existing.SameAs(entry) ? DeploymentPlanAction.Unchanged : DeploymentPlanAction.Change);
    }

    /// <summary>Whether every container of the instance is running, healthy, and on its image.</summary>
    private static async Task<bool> RunsAsync(
        DockerClient docker,
        ComposeFiles compose,
        ImageInstallationIdentity image,
        CancellationToken cancellationToken)
    {
        foreach (var container in new[] { compose.ContainerName, compose.ReplayRunnerContainerName }.OfType<string>())
        {
            var runtime = await docker.InspectContainerAsync(container, cancellationToken).ConfigureAwait(false);
            if (!runtime.Exists || !runtime.Running || !runtime.Healthy || runtime.ImageId != image.ImageId)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>The plan of a re-run of a completed installation: its shared checks, then what it would change.</summary>
    private static async Task<InstallationResult> PlanReRunAsync(
        InstallRequest request,
        RetainedInstallation retained,
        InstallationPaths paths,
        Guid instanceId,
        DockerClient docker,
        AcquiredImage? release,
        uint uid,
        uint gid,
        IReadOnlyList<InstallationWarning> warnings,
        CancellationToken cancellationToken)
    {
        var instance = await AssessCompletedAsync(request, retained, paths, instanceId, docker, release, uid, gid, cancellationToken)
            .ConfigureAwait(false);
        return instance.Result with { Outcome = InstallationOutcome.Planned, Warnings = warnings, Plan = ReRunPlan(instance) };
    }

    /// <summary>
    /// Re-runs a completed installation. After the shared checks it verifies the instance where it stands; with nothing
    /// to change it returns there, having written nothing and started nothing. Otherwise it changes only what differs
    /// from the answers: the container and its Compose files, then the manifest, the result and the install record.
    /// </summary>
    private static async Task<InstallationResult> ReRunAsync(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        DockerClient docker,
        AcquiredImage? release,
        DockerDaemonIdentity preflightDaemon,
        IReadOnlyList<InstallationWarning> warnings,
        uint uid,
        uint gid,
        Func<Uri, IOwnerBootstrapClient> ownerClientFactory,
        CancellationToken cancellationToken)
    {
        // A completed instance already has its folders and locks, so a re-run prepares nothing.
        using var productLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);
        var retained = await ReadRetainedAsync(paths, cancellationToken).ConfigureAwait(false);
        if (retained is not { Completed: true })
        {
            throw new InstallerException("The installation changed while this re-run was starting; run it again.");
        }
        EnsureInstanceRootCanBeOwned(paths, hasState: true);
        var instance = await AssessCompletedAsync(request, retained, paths, instanceId, docker, release, uid, gid, cancellationToken)
            .ConfigureAwait(false);
        if (instance.Daemon != preflightDaemon)
        {
            throw new InstallerException("The Docker daemon identity changed after preflight.");
        }
        paths = instance.Paths;
        var owner = ownerClientFactory(PublicUri(request));
        // An instance whose files and container are what its records describe is verified where it stands before
        // anything changes, a limits change included.
        if (!instance.FilesDiffer && !instance.ContainerDown)
        {
            await VerifyAsync(request, instance, instance.Compose, owner, docker, uid, gid, cancellationToken).ConfigureAwait(false);
        }
        if (!instance.HasChanges)
        {
            return instance.Result with { Warnings = warnings, Plan = ReRunPlan(instance) };
        }

        var manifest = instance.Manifest;
        if (instance.NeedsDeploy)
        {
            manifest = await ChangeContainerAsync(request, instance, owner, docker, uid, gid, cancellationToken).ConfigureAwait(false);
        }
        // The result follows the manifest's Compose model: after a limits change, or once the repair of a result an
        // interrupted change left behind its manifest.
        var result = instance.Result with { ComposeModelSha256 = manifest.ComposeModelSha256 };
        if (result != instance.Result)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath, result, DeploymentJsonContext.Default.InstallationResult, cancellationToken).ConfigureAwait(false);
        }
        StepCompleted("limits-result-written");
        // The install record is a view of the instance's own records, so an interruption here leaves those agreeing
        // with the container and the next run settles the view.
        cancellationToken.ThrowIfCancellationRequested();
        var recordAction = await InstallRecordStore.SettleInstanceAsync(
                instance.RecordLocation, paths, manifest, InstallRecordEvent.Install, cancellationToken)
            .ConfigureAwait(false);
        return result with { Warnings = warnings, Plan = ReRunPlan(instance, recordAction) };
    }

    /// <summary>
    /// Starts the instance again from Compose files written for its target limits, verifies it there, and records the
    /// new limits in the manifest, which is the change's commit point. Until the manifest is written, anything that
    /// stops the change, an interruption included, puts the recorded limits back on the container and the recorded
    /// manifest back on disk before the failure is reported; the restore has its own four-minute bound because the run's
    /// own cancellation may be what stopped the change. After the commit an interruption leaves the container and the
    /// manifest agreeing, and the next run settles the result and the install record. With no limits to change the
    /// container is put back to what its records describe, which must give the recorded model.
    /// </summary>
    private static async Task<InstanceManifest> ChangeContainerAsync(
        InstallRequest request,
        CompletedInstance instance,
        IOwnerBootstrapClient owner,
        DockerClient docker,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var recorded = instance.Manifest;
        var committed = false;
        try
        {
            var model = await DeployAsync(instance.Target, cancellationToken).ConfigureAwait(false);
            StepCompleted("limits-verified");
            cancellationToken.ThrowIfCancellationRequested();
            if (!instance.LimitsChange)
            {
                return model == recorded.ComposeModelSha256
                    ? recorded
                    : throw new InstallerException("The completed installation differs from its retained immutable manifest.");
            }
            var changed = recorded with
            {
                Resources = instance.Target,
                ComposeModelSha256 = model,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await SafeFileSystem.WriteJsonAtomicAsync(
                instance.Paths.ManifestPath, changed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken).ConfigureAwait(false);
            committed = true;
            StepCompleted("limits-manifest-written");
            return changed;
        }
        catch (Exception exception) when (instance.LimitsChange && !committed && exception is not OutOfMemoryException)
        {
            using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                _ = await DeployAsync(recorded.Resources, restore.Token).ConfigureAwait(false);
                // An interruption can land after the manifest was replaced and before the commit was seen.
                if (await ReadManifestAsync(instance.Paths.ManifestPath, restore.Token).ConfigureAwait(false) != recorded)
                {
                    await SafeFileSystem.WriteJsonAtomicAsync(
                        instance.Paths.ManifestPath, recorded, DeploymentJsonContext.Default.InstanceManifest, restore.Token).ConfigureAwait(false);
                }
            }
            catch (Exception restoreException) when (restoreException is not OutOfMemoryException)
            {
                throw new InstallerException(
                    $"Changing the container limits failed, and restoring the recorded limits also failed: {Redaction.SafeDiagnostic(restoreException.Message)} Re-run the same install to carry on.",
                    exception);
            }
            if (exception is OperationCanceledException)
            {
                throw;
            }
            throw new InstallerException(
                $"Changing the container limits to {CameraAgentContainerResources.Describe(instance.Target!)} failed, so the recorded limits were restored: {Redaction.SafeDiagnostic(exception.Message)}",
                exception);
        }

        async Task<string> DeployAsync(ContainerResourceLimits? limits, CancellationToken token)
        {
            var compose = ComposeDeployment.Write(
                request,
                instance.Paths,
                recorded.InstanceId,
                instance.ApplicationIdentity,
                uid,
                gid,
                instance.Image.ImageId,
                instance.Catalog.PackageVersion,
                instance.Result.PasswordFile,
                instance.VerificationToken,
                instance.LifecycleControlToken,
                passwordAuthorityEnabled: false,
                limits);
            var rendered = await docker.ComposeAsync(
                compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["config"], token).ConfigureAwait(false);
            await docker.ComposeAsync(
                    compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["up", "--detach", "--remove-orphans"], token)
                .ConfigureAwait(false);
            await VerifyAsync(request, instance, compose, owner, docker, uid, gid, token).ConfigureAwait(false);
            return ComposeDeployment.ComputeSha256(rendered.StandardOutput);
        }
    }

    /// <summary>Waits for the instance to be healthy and verifies its container and its installation identity.</summary>
    private static async Task VerifyAsync(
        InstallRequest request,
        CompletedInstance instance,
        ComposeFiles compose,
        IOwnerBootstrapClient owner,
        DockerClient docker,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        await owner.WaitForHealthAsync(ProductionCatalog.Get(instance.Catalog.CatalogId), cancellationToken).ConfigureAwait(false);
        await docker.VerifyContainerAsync(compose, instance.Paths, instance.Image, uid, gid, cancellationToken).ConfigureAwait(false);
        await owner.VerifyInstallationAsync(
                instance.VerificationToken,
                new InstallationVerificationExpectation(
                    instance.ApplicationIdentity.ToString("D"),
                    request.OwnerEmail,
                    "owner-password-change-required",
                    compose.ConfigurationSha256,
                    compose.RigProfileSha256,
                    compose.ScheduleSha256,
                    $"installer-{instance.Manifest.InstanceId:D}",
                    instance.Manifest.DeploymentLocationVersion,
                    instance.Manifest.DeploymentLocationSha256,
                    (ContractReplayProfile)request.ReplayProfile,
                    instance.Catalog,
                    AllowCompletedPasswordReplacement: true),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>What was already there when a fresh install, or the resumption of one, began.</summary>
    internal sealed record FreshInstallFacts(bool ProductRoot, bool CatalogRoot, bool InstanceRoot, bool Compose, bool Manifest)
    {
        public static FreshInstallFacts Observe(InstallationPaths paths)
        {
            ArgumentNullException.ThrowIfNull(paths);
            return new FreshInstallFacts(
                Directory.Exists(paths.ProductRoot),
                Directory.Exists(paths.CatalogRoot),
                Directory.Exists(paths.InstanceRoot),
                File.Exists(Path.Combine(paths.ConfigRoot, "compose", "compose.yml")),
                File.Exists(paths.ManifestPath));
        }
    }

    /// <summary>The install record entry a fresh install creates, or changes when the record already names the instance.</summary>
    private static async Task<DeploymentPlanAction> FreshRecordActionAsync(
        InstallRecordLocation location,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var record = await InstallRecordStore.ReadAsync(location, cancellationToken).ConfigureAwait(false);
        return record?.Instances.Any(instance => instance.InstanceId == instanceId) == true
            ? DeploymentPlanAction.Change
            : DeploymentPlanAction.Create;
    }

    /// <summary>
    /// What a fresh install creates, given what was already there when it began: an existing folder is left unchanged,
    /// and a Compose model, manifest or container a resumed install already made is changed rather than created.
    /// </summary>
    private static DeploymentPlan InstalledPlan(
        InstallationPaths paths,
        Guid instanceId,
        string image,
        ContainerResourceLimits? resources,
        FreshInstallFacts facts,
        DeploymentPlanAction recordAction,
        InstallRecordLocation record)
    {
        static DeploymentPlanAction Folder(bool present) => present ? DeploymentPlanAction.Unchanged : DeploymentPlanAction.Create;
        static DeploymentPlanAction Made(bool present) => present ? DeploymentPlanAction.Change : DeploymentPlanAction.Create;
        return DeploymentPlan.Of(
        [
            new(Folder(facts.ProductRoot), "folder", paths.ProductRoot, "product root"),
            new(Folder(facts.CatalogRoot), "folder", paths.CatalogRoot, "catalog"),
            new(Folder(facts.InstanceRoot), "folder", paths.InstanceRoot, "instance"),
            new(Made(facts.Compose), "file", Path.Combine(paths.ConfigRoot, "compose", "compose.yml"), "Compose model"),
            new(Made(facts.Manifest), "file", paths.ManifestPath, "instance manifest"),
            new(Made(facts.Compose), "container", $"hvo-skymonitor-{instanceId:N}", $"image {image}, {DescribeLimits(resources)}"),
            new(recordAction, "record", record.Path, $"instance {instanceId:D}")
        ]);
    }

    /// <summary>
    /// What a re-run of a completed installation changes: with the same answers nothing, otherwise the container's limits,
    /// which rewrite the Compose model, recreate the container and update the manifest and the install record; and
    /// whatever no longer matches the records, which the re-run puts back.
    /// </summary>
    private static DeploymentPlan ReRunPlan(CompletedInstance instance, DeploymentPlanAction? recordAction = null)
    {
        var paths = instance.Paths;
        var manifest = instance.Manifest;
        var limits = DescribeLimits(instance.Target);
        var filesChange = instance.LimitsChange || instance.FilesDiffer;
        var resultChanges = instance.LimitsChange || instance.ResultModelStale;
        var compose = Path.Combine(paths.ConfigRoot, "compose", "compose.yml");
        var container = $"hvo-skymonitor-{manifest.InstanceId:N}";
        return DeploymentPlan.Of(
            [
                new(DeploymentPlanAction.Unchanged, "folder", paths.InstanceRoot, "instance"),
                new(filesChange ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", compose,
                    filesChange ? $"Compose model, {limits}" : "Compose model"),
                new(instance.LimitsChange ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", paths.ManifestPath,
                    instance.LimitsChange ? $"instance manifest, {limits}" : "instance manifest"),
                new(resultChanges ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", paths.ResultPath,
                    resultChanges ? "installation result, its Compose model" : "installation result"),
                new(instance.NeedsDeploy ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "container", container,
                    instance.NeedsDeploy
                        ? filesChange ? $"recreated with {limits}" : $"started, {limits}"
                        : $"image {manifest.Image.ImageId}, {limits}"),
                new(recordAction ?? instance.RecordAction, "record", instance.RecordLocation.Path, $"instance {manifest.InstanceId:D}")
            ],
            "the instance is installed as the answers describe");
    }

    private static string DescribeLimits(ContainerResourceLimits? limits)
        => limits is null
            ? $"original limits of {CameraAgentContainerResources.Describe(CameraAgentContainerResources.Legacy)}"
            : CameraAgentContainerResources.Describe(limits);
}
