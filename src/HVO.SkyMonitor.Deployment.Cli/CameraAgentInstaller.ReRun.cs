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

    private static async Task<string?> ReadComposeIfPresentAsync(InstallationPaths paths, CancellationToken cancellationToken)
    {
        var path = Path.Combine(paths.ConfigRoot, "compose", "compose.yml");
        if (!File.Exists(path))
        {
            return null;
        }
        await using var stream = SafeFileSystem.OpenOwnerFileRead(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Moves a completed, just verified instance to <paramref name="target"/> limits: the Compose file is rewritten with
    /// them, Compose recreates the container, and the instance must come back healthy and verified. Anything that stops
    /// that, an interruption included, rewrites the Compose file with the recorded limits and recreates the container
    /// with them before the failure is reported, so a failed change leaves the instance as it was; the restore has its
    /// own four-minute bound because the run's own cancellation may be what stopped the change. The caller writes the
    /// manifest and the result only after this returns.
    /// </summary>
    private static async Task<(ComposeFiles Compose, string ComposeModelSha256)> ChangeLimitsAsync(
        InstallRequest request,
        InstallationPaths paths,
        Guid instanceId,
        Guid applicationIdentity,
        uint uid,
        uint gid,
        ImageInstallationIdentity image,
        CatalogInstallationIdentity catalog,
        string passwordPath,
        string verificationToken,
        string lifecycleControlToken,
        ContainerResourceLimits? recorded,
        ContainerResourceLimits target,
        InstanceManifest manifest,
        IOwnerBootstrapClient ownerClient,
        DockerClient docker,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DeployAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                _ = await DeployAsync(recorded, restore.Token).ConfigureAwait(false);
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
                $"Changing the container limits to {CameraAgentContainerResources.Describe(target)} failed, so the recorded limits were restored: {Redaction.SafeDiagnostic(exception.Message)}",
                exception);
        }

        async Task<(ComposeFiles, string)> DeployAsync(ContainerResourceLimits? limits, CancellationToken token)
        {
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
                limits);
            var rendered = await docker.ComposeAsync(
                compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["config"], token).ConfigureAwait(false);
            await docker.ComposeAsync(
                    compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["up", "--detach", "--remove-orphans"], token)
                .ConfigureAwait(false);
            await ownerClient.WaitForHealthAsync(ProductionCatalog.Get(catalog.CatalogId), token).ConfigureAwait(false);
            await docker.VerifyContainerAsync(compose, paths, image, uid, gid, token).ConfigureAwait(false);
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
                    AllowCompletedPasswordReplacement: true),
                token).ConfigureAwait(false);
            return (compose, ComposeDeployment.ComputeSha256(rendered.StandardOutput));
        }
    }

    /// <summary>
    /// The plan of a re-run of a completed installation, read without changing anything: the same checks a re-run makes
    /// before it changes anything, then what it would change. A re-run with the same answers changes nothing.
    /// </summary>
    private static async Task<InstallationResult> PlanReRunAsync(
        InstallRequest request,
        RetainedInstallation retained,
        InstallationPaths paths,
        Guid instanceId,
        DockerClient docker,
        ReleaseImageSelection? releaseImage,
        uint uid,
        uint gid,
        CancellationToken cancellationToken)
    {
        var manifest = retained.Manifest ?? throw new InstallerException("A completed installation requires its retained instance manifest.");
        var result = retained.Result ?? throw new InstallerException("A completed installation requires its retained installation result.");
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
        var (daemon, image) = await docker.PrepareImageAsync(effectiveRequest, allowMutation: false, releaseImage, cancellationToken)
            .ConfigureAwait(false);
        if (image.ImageId != manifest.Image.ImageId)
        {
            throw InstallerException.Refused(DifferentImage(manifest, image));
        }
        if (request.ReplayProfile == CameraAgentReplayProfile.InProcess &&
            manifest.Image.ReplayRunnerContract is null && result.Image.ReplayRunnerContract is null)
        {
            image = image with { ReplayRunnerContract = null };
        }
        var applicationIdentity = await GetOrCreateApplicationIdentityAsync(
            paths.ApplicationIdentityPath, allowCreation: false, cancellationToken).ConfigureAwait(false);
        var verificationToken = await ReadOwnerSecretAsync(
            Path.Combine(paths.ConfigRoot, "installation-verification", "token"), cancellationToken).ConfigureAwait(false);
        var lifecycleControlToken = await ReadOwnerSecretAsync(
            Path.Combine(paths.ConfigRoot, "lifecycle-control", "token"), cancellationToken).ConfigureAwait(false);
        var resultModelStale = await ValidateCompletedCandidateAsync(
                request, paths, instanceId, manifest.InstallationId, applicationIdentity, uid, gid, result.PasswordFile,
                verificationToken, lifecycleControlToken, catalog, image, daemon, manifest, result, manifest.Resources, docker,
                cancellationToken)
            .ConfigureAwait(false);
        var target = CameraAgentContainerResources.ReRunTarget(manifest.Resources, request.Cpus, request.Memory);
        var location = InstallRecordLocation.For(paths.ProductRoot, request.ProductionProductRoot);
        var record = await InstallRecordStore.ReadAsync(location, cancellationToken).ConfigureAwait(false);
        var existing = record?.Instances.FirstOrDefault(instance => instance.InstanceId == manifest.InstanceId);
        var limitsChange = target != manifest.Resources;
        var entry = await InstallRecordStore.ProjectAsync(
                paths, manifest with { Resources = target }, InstallRecordEvent.Install, existing, cancellationToken)
            .ConfigureAwait(false);
        var recordAction = existing is null ? DeploymentPlanAction.Create
            : existing.SameAs(entry) ? DeploymentPlanAction.Unchanged : DeploymentPlanAction.Change;
        return result with
        {
            Outcome = InstallationOutcome.Planned,
            Plan = ReRunPlan(
                paths,
                manifest,
                target,
                await ReadComposeIfPresentAsync(paths, cancellationToken).ConfigureAwait(false),
                limitsChange,
                limitsChange || resultModelStale,
                recordAction,
                location)
        };
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
    /// which rewrite the Compose model, recreate the container and update the manifest and the install record.
    /// </summary>
    private static DeploymentPlan ReRunPlan(
        InstallationPaths paths,
        InstanceManifest manifest,
        ContainerResourceLimits? target,
        string? composeBefore,
        bool manifestChanges,
        bool resultChanges,
        DeploymentPlanAction recordAction,
        InstallRecordLocation record)
    {
        var applied = composeBefore is null ? null : CameraAgentContainerResources.TryReadLimits(composeBefore);
        var targetLimits = target ?? CameraAgentContainerResources.Legacy;
        var containerChanges = applied is null || !CameraAgentContainerResources.SameLimits(applied, targetLimits);
        var limits = DescribeLimits(target);
        var compose = Path.Combine(paths.ConfigRoot, "compose", "compose.yml");
        var container = $"hvo-skymonitor-{manifest.InstanceId:N}";
        return DeploymentPlan.Of(
            [
                new(DeploymentPlanAction.Unchanged, "folder", paths.InstanceRoot, "instance"),
                new(containerChanges ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", compose,
                    containerChanges ? $"Compose model, {limits}" : "Compose model"),
                new(manifestChanges ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", paths.ManifestPath,
                    manifestChanges ? $"instance manifest, {limits}" : "instance manifest"),
                new(resultChanges ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", paths.ResultPath,
                    resultChanges ? "installation result, its Compose model" : "installation result"),
                new(containerChanges ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "container", container,
                    containerChanges ? $"recreated with {limits}" : $"image {manifest.Image.ImageId}, {limits}"),
                new(recordAction, "record", record.Path, $"instance {manifest.InstanceId:D}")
            ],
            "the instance is installed as the answers describe");
    }

    private static string DescribeLimits(ContainerResourceLimits? limits)
        => limits is null
            ? $"original limits of {CameraAgentContainerResources.Describe(CameraAgentContainerResources.Legacy)}"
            : CameraAgentContainerResources.Describe(limits);
}
