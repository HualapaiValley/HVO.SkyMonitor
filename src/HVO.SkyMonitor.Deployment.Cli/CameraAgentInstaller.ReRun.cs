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
    /// The lighting is compared when the instance's configuration was <paramref name="recognized"/>, which is where the
    /// installed lighting is recorded (#1779, 1706-B1): omitted and explicit <c>LegacyScalarSolar</c> are different
    /// answers, as at install.
    /// </summary>
    internal static string? InstalledDifference(
        InstallRequest request,
        InstallationPaths paths,
        InstanceManifest manifest,
        RecognizedConfiguration? recognized = null)
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
        if (recognized is not null)
        {
            Compare("--illumination-mode", Lighting(recognized.IlluminationMode), Lighting(request.IlluminationMode));
        }
        return differences.Count == 0
            ? null
            : $"Instance {manifest.InstanceId:D} is installed with a different {string.Join(", ", differences)}. A re-run may change only --cpus and --memory; give the answers it was installed with.";
    }

    private static string Lighting(string? mode) => mode ?? "omitted";

    /// <summary>
    /// The installed answers a configuration is generated from: the request's, with every answer the manifest records
    /// taken from the manifest. A re-run whose answers differ is refused before they are used.
    /// </summary>
    private static InstallRequest InstalledAnswers(InstallRequest request, InstanceManifest manifest)
        => request with
        {
            FriendlyName = manifest.FriendlyName,
            OwnerEmail = manifest.OwnerEmail,
            BindAddress = manifest.BindAddress,
            Port = manifest.Port,
            LatitudeDegrees = manifest.LatitudeDegrees,
            LongitudeDegrees = manifest.LongitudeDegrees,
            ElevationMeters = manifest.ElevationMeters,
            TimeZoneId = manifest.TimeZoneId,
            ReplayProfile = (CameraAgentReplayProfile)manifest.ReplayProfile
        };

    /// <summary>
    /// The canonical identity of the instance's <c>camera-module.json</c> as it lies, with its text, or
    /// <see langword="null"/> when there is no such file. A file that cannot be read or parsed has neither.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A configuration that cannot be read is refused as unexplained, whatever stopped the read.")]
    private static ConfigurationFile? ReadConfigurationFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists && info.LinkTarget is null && !Directory.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = SafeFileSystem.OpenOwnerFileRead(path);
            using var reader = new StreamReader(stream, new System.Text.UTF8Encoding(false, true));
            var text = reader.ReadToEnd();
            using var document = System.Text.Json.JsonDocument.Parse(text);
            return new ConfigurationFile(text, Convert.ToHexStringLower(Convert.FromHexString(
                HVO.SkyMonitor.AgentCore.CaptureContractJson.ComputeCanonicalJsonSha256(document.RootElement))));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new ConfigurationFile(null, null);
        }
    }

    private sealed record ConfigurationFile(string? Text, string? Sha256);

    private static string DifferentImage(InstanceManifest manifest, ImageInstallationIdentity requested)
        => $"Instance {manifest.InstanceId:D} runs image {manifest.Image.ImageId}, not the requested {requested.ImageId}. A re-run keeps the image; change it with '{InstallerProgram.Current} cameraagent upgrade --instance-id {manifest.InstanceId:D}', or re-run naming the image it runs.";

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
        bool ContainerDiffers,
        bool ResultModelStale,
        bool ProgramsChange,
        InstallRecordLocation RecordLocation,
        DeploymentPlanAction RecordAction,
        CameraConfigurationGeneration RetainedGeneration,
        IReadOnlyList<CameraConfigurationGeneration> Adopted,
        string ConfigurationSha256,
        bool ConfigurationFileDiffers,
        bool ResultConfigurationStale)
    {
        /// <summary>
        /// The configuration-default generation the instance is left on: the installer's current one when the re-run
        /// adopts newer defaults (#1779), otherwise the one that wrote its configuration.
        /// </summary>
        public CameraConfigurationGeneration Generation => Adopted.Count == 0 ? RetainedGeneration : Adopted[^1];

        /// <summary>The re-run adopts the installer's newer configuration defaults (#1779).</summary>
        public bool Adopts => Adopted.Count > 0;

        /// <summary>
        /// The container must be started again from Compose files written for <see cref="Target"/>: the limits change,
        /// the files differ from what the records describe, the configuration adopts new defaults, or the container is
        /// not running healthy on the recorded image and limits (<see cref="ContainerDiffers"/>).
        /// </summary>
        public bool NeedsDeploy => LimitsChange || FilesDiffer || ContainerDiffers || Adopts;

        /// <summary>The manifest is committed with new limits or a new configuration identity.</summary>
        public bool Commits => LimitsChange || Adopts;

        /// <summary>
        /// The deploy rewrites camera-module.json, which the container mounts as a single file: a rewrite replaces the file,
        /// so only a recreated container reads the new one.
        /// </summary>
        public bool RecreatesContainer => Adopts || ConfigurationFileDiffers;

        public bool HasChanges => NeedsDeploy || ResultModelStale || ResultConfigurationStale || ProgramsChange ||
                                  RecordAction != DeploymentPlanAction.Unchanged;
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
        // The configuration is held to the installed answers under each configuration-default generation this installer
        // knows, which also says what lighting was installed (#1779). Every difference is classified before anything is
        // read from Docker or written: answers that differ are refused and named; defaults that moved are adopted below;
        // anything else the installer cannot explain is refused, naming the file or record.
        var installed = InstalledAnswers(request, manifest);
        var recognized = CameraConfigurationDefaults.Recognize(
            installed, manifest.ApplicationIdentity, manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256);
        if (InstalledDifference(request, paths, manifest, recognized) is { } difference)
        {
            throw InstallerException.Refused(difference);
        }
        var configurationPath = Path.Combine(paths.ConfigRoot, "camera-module.json");
        if (recognized is null)
        {
            throw CameraConfigurationRefusals.UnknownIdentity(
                manifest.InstanceId, paths.ManifestPath, configurationPath, manifest.ConfigurationSha256,
                string.Join(", ", CameraConfigurationDefaults.Known.Select(static generation => generation.Id)));
        }
        var adopted = CameraConfigurationDefaults.AdoptedAfter(recognized.Generation);
        var configuration = adopted.Count == 0
            ? recognized.Configuration
            : adopted[^1].Generate(installed with { IlluminationMode = recognized.IlluminationMode }, manifest.ApplicationIdentity);
        if (configuration.RigProfileSha256 != manifest.RigProfileSha256 || configuration.ScheduleSha256 != manifest.ScheduleSha256 ||
            configuration.RigProfileName != manifest.RigProfileName || configuration.RigProfileVersion != manifest.RigProfileVersion ||
            configuration.ScheduleSchemaVersion != manifest.ScheduleSchemaVersion || configuration.ScheduleState != manifest.ScheduleState)
        {
            // A configuration-default generation never moves the rig profile or schedule: their identities bind calibration
            // and capture history, so a change to them is not adopted by a re-run.
            throw CameraConfigurationRefusals.MovesRigOrSchedule(manifest.InstanceId, adopted[^1].Id);
        }
        // The result follows the manifest, which is the commit point: a re-run interrupted after committing a new
        // configuration identity leaves the result on the identity before it, which this re-run settles. Any other
        // disagreement is not one a re-run leaves behind.
        var resultConfigurationStale = false;
        if (result.ConfigurationSha256 != manifest.ConfigurationSha256)
        {
            var resultRecognized = CameraConfigurationDefaults.Recognize(
                installed, manifest.ApplicationIdentity, result.ConfigurationSha256, result.RigProfileSha256, result.ScheduleSha256);
            if (resultRecognized is null || resultRecognized.IlluminationMode != recognized.IlluminationMode ||
                resultRecognized.Generation.Number >= recognized.Generation.Number)
            {
                throw CameraConfigurationRefusals.RecordsDisagree(
                    manifest.InstanceId, configurationPath, paths.ManifestPath, manifest.ConfigurationSha256, paths.ResultPath,
                    result.ConfigurationSha256);
            }
            resultConfigurationStale = true;
        }
        // The file is what the container reads. It may hold the recorded configuration, possibly reformatted, or the one an
        // interrupted adoption wrote before its commit; a missing file is written as the records describe. Anything else
        // was edited, and an edit is never overwritten.
        var file = ReadConfigurationFile(configurationPath);
        if (file is not null &&
            (file.Sha256 is null ||
             (file.Sha256 != manifest.ConfigurationSha256 && file.Sha256 != configuration.Sha256 &&
              !(resultConfigurationStale && file.Sha256 == result.ConfigurationSha256))))
        {
            throw file.Sha256 is null
                ? CameraConfigurationRefusals.UnreadableConfiguration(manifest.InstanceId, configurationPath)
                : CameraConfigurationRefusals.EditedConfiguration(manifest.InstanceId, configurationPath, file.Sha256, manifest.ConfigurationSha256);
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
        // A re-run keeps the image, so newer defaults are adopted only by an instance whose running image already runs
        // them; otherwise nothing changes, and the operator upgrades the image first (#1779, #1777).
        var requiredImageGeneration = CameraConfigurationDefaults.RequiredImageGeneration(adopted);
        if (requiredImageGeneration > 0)
        {
            var declaredLabel = await docker.ReadImageLabelAsync(image.ImmutableReference, CameraConfigurationDefaults.ImageLabel, cancellationToken)
                .ConfigureAwait(false);
            if (CameraConfigurationDefaults.DeclaredByImage(declaredLabel) < requiredImageGeneration)
            {
                throw CameraConfigurationRefusals.ImageCannotAdopt(
                    manifest.InstanceId,
                    CameraConfigurationDefaults.Describe(adopted),
                    CameraConfigurationDefaults.IdPrefix + requiredImageGeneration.ToString(CultureInfo.InvariantCulture),
                    image.ImageId,
                    declaredLabel ?? "none",
                    InstallerProgram.Current);
            }
        }
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
            verificationToken, lifecycleControlToken, passwordAuthorityEnabled: false, manifest.Resources,
            defaults: recognized.Generation);
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
        if ((resultConfigurationStale ? candidateResult with { ConfigurationSha256 = result.ConfigurationSha256 } : candidateResult) != result)
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
            !await RunsAsync(docker, recorded.Files, image, manifest.Resources, cancellationToken).ConfigureAwait(false),
            result.ComposeModelSha256 != manifest.ComposeModelSha256,
            (await PathPrograms.PlanAsync(paths, request.ProductionProductRoot, request.Release, image.ImageId,
                operation: null, cancellationToken).ConfigureAwait(false)).Any(item => item.Action != DeploymentPlanAction.Unchanged) ||
                await InstallRecordStore.ProgramsDifferAsync(location, paths, cancellationToken).ConfigureAwait(false),
            location,
            existing is null ? DeploymentPlanAction.Create
            : existing.SameAs(entry) ? DeploymentPlanAction.Unchanged : DeploymentPlanAction.Change,
            recognized.Generation,
            adopted,
            configuration.Sha256,
            file?.Text != configuration.Json,
            resultConfigurationStale);
    }

    /// <summary>
    /// Whether every container of the instance is running, healthy and on its image, and CameraAgent's container is held
    /// to the limits its records describe. A container left on other limits, such as one a failed restore of a limits
    /// change could not recreate, is redeployed from the records (#1315).
    /// </summary>
    private static async Task<bool> RunsAsync(
        DockerClient docker,
        ComposeFiles compose,
        ImageInstallationIdentity image,
        ContainerResourceLimits? recordedLimits,
        CancellationToken cancellationToken)
    {
        foreach (var container in new[] { compose.ContainerName, compose.ReplayRunnerContainerName }.OfType<string>())
        {
            var runtime = await docker.InspectContainerAsync(container, cancellationToken).ConfigureAwait(false);
            if (!runtime.Exists || !runtime.Running || !runtime.Healthy || runtime.ImageId != image.ImageId)
            {
                return false;
            }
            if (container == compose.ContainerName && runtime.Limits is { } applied &&
                !CameraAgentContainerResources.SameLimits(applied, recordedLimits ?? CameraAgentContainerResources.Legacy))
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
        // anything changes, a limits change included. An adoption of newer defaults is verified on the configuration it
        // adopts instead: its container may still run one a failed restore could not take back, which Docker cannot show
        // and only the recreate settles (#1779).
        if (!instance.FilesDiffer && !instance.ContainerDiffers && !instance.Adopts)
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
        // The result follows the manifest's Compose model and configuration identity: after a limits change or an adoption
        // of new configuration defaults, or once the repair of a result an interrupted change left behind its manifest.
        var result = instance.Result with
        {
            ComposeModelSha256 = manifest.ComposeModelSha256,
            ConfigurationSha256 = manifest.ConfigurationSha256
        };
        if (result != instance.Result)
        {
            await SafeFileSystem.WriteJsonAtomicAsync(
                paths.ResultPath, result, DeploymentJsonContext.Default.InstallationResult, cancellationToken).ConfigureAwait(false);
        }
        StepCompleted("limits-result-written");
        // The install record is a view of the instance's own records, so an interruption here leaves those agreeing
        // with the container and the next run settles the view.
        cancellationToken.ThrowIfCancellationRequested();
        await PathPrograms.SettleAsync(paths, request.ProductionProductRoot, request.Release, manifest.Image.ImageId,
            cancellationToken).ConfigureAwait(false);
        var recordAction = await InstallRecordStore.SettleInstanceAsync(
                instance.RecordLocation, paths, manifest, InstallRecordEvent.Install, cancellationToken)
            .ConfigureAwait(false);
        return result with { Warnings = warnings, Plan = ReRunPlan(instance, recordAction) };
    }

    /// <summary>
    /// Starts the instance again from Compose files written for its target limits and configuration, verifies it there,
    /// and records the new limits and configuration identity in the manifest, which is the change's commit point. Until the
    /// manifest is written, anything that stops the change, an interruption included, puts the recorded limits and the
    /// recorded configuration, of the configuration-default generation that wrote it, back on the container and the
    /// recorded manifest back on disk before the failure is reported; the restore has its own four-minute bound because
    /// the run's own cancellation may be what stopped the change. After the commit an interruption leaves the container
    /// and the manifest agreeing, and the next run settles the result and the install record. With nothing to commit the
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
            var (model, configurationSha256) = await DeployAsync(instance.Target, instance.Generation, forward: true, cancellationToken)
                .ConfigureAwait(false);
            StepCompleted("limits-verified");
            cancellationToken.ThrowIfCancellationRequested();
            if (!instance.Commits)
            {
                return model == recorded.ComposeModelSha256
                    ? recorded
                    : throw new InstallerException("The completed installation differs from its retained immutable manifest.");
            }
            if (configurationSha256 != instance.ConfigurationSha256)
            {
                throw new InstallerException("The written camera configuration is not the one the re-run planned.");
            }
            var changed = recorded with
            {
                Resources = instance.Target,
                ComposeModelSha256 = model,
                ConfigurationSha256 = configurationSha256,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            await SafeFileSystem.WriteJsonAtomicAsync(
                instance.Paths.ManifestPath, changed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken).ConfigureAwait(false);
            committed = true;
            StepCompleted("limits-manifest-written");
            return changed;
        }
        catch (Exception exception) when (instance.Commits && !committed && exception is not OutOfMemoryException)
        {
            using var restore = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                _ = await DeployAsync(recorded.Resources, instance.RetainedGeneration, forward: false, restore.Token).ConfigureAwait(false);
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
                    $"{ChangeDescription(instance, namingTarget: false)} failed, and restoring the recorded {RestoredDescription(instance)} also failed: {Redaction.SafeDiagnostic(restoreException.Message)} Re-run the same install to carry on.",
                    exception);
            }
            if (exception is OperationCanceledException)
            {
                throw;
            }
            throw new InstallerException(
                $"{ChangeDescription(instance, namingTarget: true)} failed, so the recorded {RestoredDescription(instance)} {(instance.Adopts && !instance.LimitsChange ? "was" : "were")} restored: {Redaction.SafeDiagnostic(exception.Message)}",
                exception);
        }

        // Writes the files for limits and a configuration generation, starts the instance from them and verifies it there.
        // A rewrite of the configuration recreates the container (CompletedInstance.RecreatesContainer); the restore of
        // one does too, since it rewrites the file again.
        async Task<(string Model, string ConfigurationSha256)> DeployAsync(
            ContainerResourceLimits? limits,
            CameraConfigurationGeneration generation,
            bool forward,
            CancellationToken token)
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
                limits,
                defaults: generation);
            if (forward)
            {
                StepCompleted("rerun-files-written");
            }
            var rendered = await docker.ComposeAsync(
                compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["config"], token).ConfigureAwait(false);
            await docker.ComposeAsync(
                    compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                    instance.RecreatesContainer ? ["up", "--detach", "--force-recreate", "--remove-orphans"] : ["up", "--detach", "--remove-orphans"],
                    token)
                .ConfigureAwait(false);
            await VerifyAsync(request, instance, compose, owner, docker, uid, gid, token).ConfigureAwait(false);
            return (ComposeDeployment.ComputeSha256(rendered.StandardOutput), compose.ConfigurationSha256);
        }
    }

    /// <summary>What a committing re-run changes, as its failure reports it.</summary>
    private static string ChangeDescription(CompletedInstance instance, bool namingTarget)
    {
        var limits = namingTarget ? $"Changing the container limits to {CameraAgentContainerResources.Describe(instance.Target!)}" : "Changing the container limits";
        var adoption = $"adopting the installer's new configuration defaults ({CameraConfigurationDefaults.Describe(instance.Adopted)})";
        return (instance.LimitsChange, instance.Adopts) switch
        {
            (true, true) => $"{limits} and {adoption}",
            (false, true) => char.ToUpperInvariant(adoption[0]) + adoption[1..],
            _ => limits
        };
    }

    /// <summary>What a failed committing re-run puts back.</summary>
    private static string RestoredDescription(CompletedInstance instance)
        => (instance.LimitsChange, instance.Adopts) switch
        {
            (true, true) => "limits and configuration",
            (false, true) => "configuration",
            _ => "limits"
        };

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
    /// which rewrite the Compose model, recreate the container and update the manifest and the install record; the
    /// installer's newer configuration defaults, which rewrite camera-module.json, recreate the container and update the
    /// manifest and the result, each named (#1779); and whatever no longer matches the records, which the re-run puts back.
    /// </summary>
    private static DeploymentPlan ReRunPlan(CompletedInstance instance, DeploymentPlanAction? recordAction = null)
    {
        var paths = instance.Paths;
        var manifest = instance.Manifest;
        var limits = DescribeLimits(instance.Target);
        var filesChange = instance.LimitsChange || instance.FilesDiffer;
        var resultModelChanges = instance.LimitsChange || instance.ResultModelStale;
        var resultConfigurationChanges = instance.Adopts || instance.ResultConfigurationStale;
        var compose = Path.Combine(paths.ConfigRoot, "compose", "compose.yml");
        var container = $"hvo-skymonitor-{manifest.InstanceId:N}";
        var configurationChanges = instance.Adopts || instance.ConfigurationFileDiffers;
        return DeploymentPlan.Of(
            [
                new(DeploymentPlanAction.Unchanged, "folder", paths.InstanceRoot, "instance"),
                new(configurationChanges ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file",
                    Path.Combine(paths.ConfigRoot, "camera-module.json"),
                    instance.Adopts
                        ? $"camera configuration, adopts the installer's new defaults: {CameraConfigurationDefaults.Describe(instance.Adopted)}"
                        : instance.ConfigurationFileDiffers ? "camera configuration, written as its records describe" : "camera configuration"),
                new(filesChange ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", compose,
                    filesChange ? $"Compose model, {limits}" : "Compose model"),
                new(instance.Commits ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file", paths.ManifestPath,
                    (instance.LimitsChange, instance.Adopts) switch
                    {
                        (true, true) => $"instance manifest, {limits} and configuration {instance.ConfigurationSha256}",
                        (true, false) => $"instance manifest, {limits}",
                        (false, true) => $"instance manifest, configuration {instance.ConfigurationSha256}",
                        _ => "instance manifest"
                    }),
                new(resultModelChanges || resultConfigurationChanges ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "file",
                    paths.ResultPath,
                    (resultModelChanges, resultConfigurationChanges) switch
                    {
                        (true, true) => "installation result, its Compose model and configuration identity",
                        (true, false) => "installation result, its Compose model",
                        (false, true) => "installation result, its configuration identity",
                        _ => "installation result"
                    }),
                new(instance.NeedsDeploy ? DeploymentPlanAction.Change : DeploymentPlanAction.Unchanged, "container", container,
                    instance.NeedsDeploy
                        ? instance.Adopts ? $"recreated with {limits} and the adopted configuration" : $"recreated with {limits}"
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
