using HVO.SkyMonitor.Deployment.Contracts;
using ContractReplayProfile = HVO.SkyMonitor.Deployment.Contracts.CameraAgentReplayProfile;
using HVO.SkyMonitor.Deployment.Distribution;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;

namespace HVO.SkyMonitor.Deployment;

internal sealed class CameraAgentInstaller
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
        CancellationToken cancellationToken)
    {
        request.Validate();
        var identityRequest = request;
        var docker = new DockerClient(processRunner);
        var instanceId = request.InstanceId ?? Guid.NewGuid();
        var paths = InstallationPaths.Create(request.ProductRoot, instanceId, ProductionCatalog.CatalogId);
        if (request.DryRun)
        {
            using var dryRunAcquirer = new DistributionCatalogAcquirer();
            using var dryRunCatalog = await dryRunAcquirer.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
            return await PlanAsync(
                    request with { CatalogBundle = dryRunCatalog.BundlePath },
                    dryRunCatalog.Evidence,
                    paths,
                    instanceId,
                    docker,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (uid == 0)
        {
            throw new InstallerException("Run the installer as the Docker-capable runtime user, not as root or through sudo.");
        }

        if (File.Exists(paths.ManifestPath))
            _ = await ReadManifestAsync(paths.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (File.Exists(paths.ResultPath))
            _ = await ReadResultAsync(paths.ResultPath, cancellationToken).ConfigureAwait(false);

        using var catalogAcquirer = new DistributionCatalogAcquirer();
        using var acquiredCatalog = await catalogAcquirer.AcquireAsync(request, cancellationToken).ConfigureAwait(false);
        request = request with { CatalogBundle = acquiredCatalog.BundlePath };

        var preflightDaemon = await docker.PreflightAsync(cancellationToken).ConfigureAwait(false);
        ValidateCatalogInput(request.CatalogBundle!);

        await PrivilegedPreparation.PrepareAsync(paths, uid, gid, processRunner, cancellationToken).ConfigureAwait(false);
        using var productLock = OperationLock.Acquire(Path.Combine(paths.OperationsRoot, "deployment.lock"), cancellationToken: cancellationToken);
        using var instanceLock = OperationLock.Acquire(Path.Combine(paths.InstanceRoot, ".deployment.lock"), cancellationToken: cancellationToken);
        if (!File.Exists(paths.StatePath))
        {
            EnsurePortAvailable(request.BindAddress, request.Port);
        }
        EnsureStorageAvailable(request);
        if (string.Equals(request.ProductRoot, InstallRequest.DefaultProductRoot, StringComparison.Ordinal) &&
            !NativeLinux.IsClockSynchronized())
        {
            throw new InstallerException("The host clock is not synchronized.");
        }

        var requestSha256 = ComputeRequestSha256(identityRequest, instanceId, acquiredCatalog.Evidence);
        var existingState = await ReadStateAsync(paths.StatePath, cancellationToken).ConfigureAwait(false);
        EnsureInstanceRootCanBeOwned(paths, existingState is not null);
        if (existingState is not null && !RequestIdentityMatches(
                existingState.RequestSha256,
                identityRequest,
                instanceId,
                acquiredCatalog.Evidence))
        {
            throw new InstallerException("The retained installation state belongs to different immutable inputs.");
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
                throw new InstallerException("A preserved uninstalled instance must be restored with cameraagent reinstall.");
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
            if (acquiredCatalog.SignedIdentity is { } signedCatalog &&
                (catalog.CatalogId != signedCatalog.CatalogId || catalog.PackageVersion != signedCatalog.PackageVersion ||
                 catalog.SchemaVersion != signedCatalog.SchemaVersion || catalog.PreprocessingVersion != signedCatalog.PreprocessingVersion ||
                 catalog.DatabaseSha256 != signedCatalog.DatabaseSha256 || catalog.DatabaseLength != signedCatalog.DatabaseLength ||
                 catalog.RowCount != signedCatalog.RowCount || catalog.ManifestSha256 != signedCatalog.BundleManifestSha256))
            {
                throw new InstallerException("The installed catalog does not match its signed distribution identity.");
            }
            catalog = catalog with
            {
                Distribution = retainedCompletedResult?.Catalog.Distribution ?? acquiredCatalog.Evidence
            };

            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.Image, cancellationToken).ConfigureAwait(false);
            }
            var effectiveRequest = await StageImageArchiveAsync(
                request,
                paths,
                allowMutation: retainedCompletedResult is null,
                cancellationToken).ConfigureAwait(false);
            var (daemon, image) = await docker.PrepareImageAsync(
                effectiveRequest,
                allowMutation: retainedCompletedResult is null,
                cancellationToken)
                .ConfigureAwait(false);
            if (!IsValid(image, request.ReplayProfile))
                throw new InstallerException("The CameraAgent image does not declare the required current configuration and catalog contracts.");
            if (daemon != preflightDaemon)
            {
                throw new InstallerException("The Docker daemon identity changed after preflight.");
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
            var passwordPath = retainedCompletedResult?.PasswordFile ?? await CredentialFile.GetOrCreateAsync(
                    request.PasswordFile,
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
            if (retainedCompletedResult is not null)
            {
                await ValidateCompletedCandidateAsync(
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
                passwordAuthorityEnabled);

            // Compose writing created every writable bind source with the runtime identity. Prove the persisted
            // catalog, Identity, and raw-ingress boundaries match this image before Compose starts the container,
            // so an incompatible upgrade path fails once instead of through container restart loops.
            await CameraAgentStatePreflight.EnsureCompatibleAsync(
                paths,
                instanceId,
                image,
                existingManifest?.Image.UpgradeCompatibility ?? image.UpgradeCompatibility,
                uid,
                gid,
                (ContractReplayProfile)request.ReplayProfile,
                persist: true,
                cancellationToken,
                // Installing an image is not an in-place state migration, so an image that predates the label
                // correction is admitted as a known contract; the persisted boundaries it declares still decide.
                CameraAgentStateContractPolicy.AllowLegacy).ConfigureAwait(false);

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
                verificationToken, lifecycleControlToken, compose, composeModelSha256, catalog, image, daemon, existingManifest);
            if (retainedCompletedResult is null)
            {
                await SafeFileSystem.WriteJsonAtomicAsync(
                    paths.ManifestPath,
                    manifest,
                    DeploymentJsonContext.Default.InstanceManifest,
                    cancellationToken).ConfigureAwait(false);
            }

            state = await RecordPhaseAsync(paths, state, InstallationPhase.Startup, cancellationToken).ConfigureAwait(false);
            await docker.ComposeAsync(
                compose.ComposeFile,
                compose.EnvironmentFile,
                compose.ProjectName,
                ["up", "--detach", "--remove-orphans"],
                cancellationToken).ConfigureAwait(false);
            var baseAddress = new UriBuilder("http", "127.0.0.1", request.Port).Uri;
            var ownerClient = ownerClientFactory(baseAddress);
            await ownerClient.WaitForHealthAsync(cancellationToken).ConfigureAwait(false);

            var ownerState = retainedCompletedResult?.OwnerBootstrapState ?? "owner-temporary-password";
            string? password = null;
            if (retainedCompletedResult is null)
            {
                state = await RecordPhaseAsync(paths, state, InstallationPhase.OwnerBootstrap, cancellationToken).ConfigureAwait(false);
                password = await ReadOwnerSecretAsync(passwordPath, cancellationToken).ConfigureAwait(false);
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
                        passwordAuthorityEnabled: false);
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
                    await ownerClient.WaitForHealthAsync(cancellationToken).ConfigureAwait(false);
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
            if (retainedCompletedResult is null)
            {
                await SafeFileSystem.WriteJsonAtomicAsync(
                    paths.ManifestPath,
                    manifest,
                    DeploymentJsonContext.Default.InstanceManifest,
                    cancellationToken).ConfigureAwait(false);
            }

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
                DateTimeOffset.UtcNow);
            if (retainedCompletedResult is not null)
            {
                var comparableResult = result with { CompletedUtc = retainedCompletedResult.CompletedUtc };
                if (comparableResult != retainedCompletedResult)
                {
                    throw new InstallerException("The completed installation differs from its retained result.");
                }
                result = retainedCompletedResult;
            }
            else
            {
                await SafeFileSystem.WriteJsonAtomicAsync(
                    paths.ResultPath,
                    result,
                    DeploymentJsonContext.Default.InstallationResult,
                    cancellationToken).ConfigureAwait(false);
            }
            _ = await RecordPhaseAsync(
                paths,
                state,
                InstallationPhase.Completed,
                cancellationToken,
                InstallationStatus.Completed).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException exception)
        {
            if (retainedCompletedResult is null || completedCandidateValidated)
            {
                await RecordFailureAsync(paths, state, exception, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception exception)
        {
            if (retainedCompletedResult is null || completedCandidateValidated)
            {
                await RecordFailureAsync(paths, state, exception, CancellationToken.None).ConfigureAwait(false);
            }
            throw exception is InstallerException installerException
                ? installerException
                : new InstallerException("Installation failed at a durable phase boundary.", exception);
        }
    }

    private static async Task<InstallationResult> PlanAsync(
        InstallRequest request,
        DistributionVerificationEvidence? catalogDistribution,
        InstallationPaths finalPaths,
        Guid instanceId,
        DockerClient docker,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hvo-installer-plan-{Guid.NewGuid():N}");
        try
        {
            var applicationIdentity = Guid.NewGuid();
            var temporaryPaths = InstallationPaths.Create(temporaryRoot, instanceId, ProductionCatalog.CatalogId);
            var catalog = CatalogInstaller.Install(request.CatalogBundle!, temporaryPaths.CatalogRoot, Guid.NewGuid()) with
            {
                InstallRoot = finalPaths.CatalogRoot,
                Distribution = catalogDistribution
            };
            var (daemon, image) = await docker.PrepareImageAsync(request, allowMutation: false, cancellationToken)
                .ConfigureAwait(false);
            if (!IsValid(image, request.ReplayProfile))
                throw new InstallerException("The CameraAgent image does not declare the required current configuration and catalog contracts.");
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
                passwordAuthorityEnabled: true);
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
                (ContractReplayProfile)request.ReplayProfile);
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

    private static async Task ValidateCompletedCandidateAsync(
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
        DockerClient docker,
        CancellationToken cancellationToken)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hvo-installer-rerun-{Guid.NewGuid():N}");
        try
        {
            var outputPaths = InstallationPaths.Create(temporaryRoot, instanceId, ProductionCatalog.CatalogId);
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
                retainedManifest);
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
                retainedResult.CompletedUtc);
            if (candidateResult != retainedResult)
            {
                throw new InstallerException("The completed installation differs from its retained result.");
            }
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
        InstanceManifest? previous)
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
            (ContractReplayProfile)request.ReplayProfile);
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
            (ContractReplayProfile)request.ReplayProfile);

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
        => value is not null && value.CatalogId == ProductionCatalog.CatalogId && HasValue(value.PackageVersion) &&
           value.SchemaVersion == "2" && value.PreprocessingVersion == "3" &&
           value.DatabaseSha256 == ProductionCatalog.DatabaseSha256 &&
           value.DatabaseLength == ProductionCatalog.DatabaseLength && value.RowCount == ProductionCatalog.RowCount &&
           HasValue(value.InstallRoot) && IsSha256(value.ManifestSha256) && value.Source == "local-offline" &&
           (value.Distribution is null || IsValid(value.Distribution) &&
            value.Distribution.ManifestKind == DistributionManifestKind.CatalogRelease.ToString() &&
            value.Distribution.ReleaseTrain == "catalog" && value.Distribution.ReleaseVersion == value.PackageVersion &&
             value.Distribution.ReleaseTag == $"catalog-{value.PackageVersion}");

    internal static bool IsValid(ImageInstallationIdentity? value, CameraAgentReplayProfile replayProfile)
        => value is not null && HasValue(value.Source) && HasValue(value.ImmutableReference) &&
           System.Text.RegularExpressions.Regex.IsMatch(
               value.ImmutableReference,
               "^(sha256:[a-f0-9]{64}|[^@\\s]+@sha256:[a-f0-9]{64})$",
               System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
           value.ImageId is not null && value.ImageId.StartsWith("sha256:", StringComparison.Ordinal) &&
           IsSha256(value.ImageId["sha256:".Length..]) && value.Architecture is "amd64" or "arm64" &&
            value.Component == "CameraAgent" && value.ConfigurationContract == "cameraagent-install-v1" &&
             value.CatalogContract == "hyg-v42-production-p3-s2" && IsSourceRevision(value.SourceRevision) &&
             (replayProfile == CameraAgentReplayProfile.InProcess ||
              value.ReplayRunnerContract == "local-replay-runner-v1") &&
            (value.ArchiveSha256 is null || IsSha256(value.ArchiveSha256)) &&
            (value.Distribution is null || IsValid(value.Distribution) &&
             value.Distribution.ManifestKind == DistributionManifestKind.InstallerRelease.ToString() &&
             value.Distribution.ReleaseTrain == "installer" &&
             value.Distribution.ReleaseTag == $"installer-v{value.Distribution.ReleaseVersion}" &&
             value.Distribution.AssetName.EndsWith(
                 $"linux-{CameraAgentLifecycleManager.DistributionArchitecture(value.Architecture)}.tar.gz",
                 StringComparison.Ordinal) &&
             value.ArchiveSha256 is not null && value.Distribution.AssetSha256 == value.ArchiveSha256);

    private static bool IsValid(DistributionVerificationEvidence value)
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

    private static void EnsureStorageAvailable(InstallRequest request)
    {
        var bundleBytes = Directory.EnumerateFiles(request.CatalogBundle!).Sum(path => new FileInfo(path).Length);
        var archiveBytes = request.ImageArchive is null ? 0 : new FileInfo(request.ImageArchive).Length;
        var required = checked(bundleBytes * 2 + archiveBytes + 1024L * 1024 * 1024);
        var existing = new DirectoryInfo(request.ProductRoot);
        while (!existing.Exists)
        {
            existing = existing.Parent
                ?? throw new InstallerException("Could not identify storage for the product root.");
        }
        var drive = new DriveInfo(Path.GetPathRoot(existing.FullName)!);
        if (drive.AvailableFreeSpace < required)
        {
            throw new InstallerException(
                $"Insufficient storage: {required} bytes are required before installation mutation.");
        }
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

    internal static string ComputeRequestSha256(
        InstallRequest request,
        Guid instanceId,
        DistributionVerificationEvidence? catalogDistribution = null)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            instanceId,
            request.FriendlyName,
            request.OwnerEmail,
            request.BindAddress,
            request.Port,
            request.ProductRoot,
            CatalogBundle = catalogDistribution is null ? request.CatalogBundle : null,
            CatalogRelease = catalogDistribution is null ? null : new
            {
                catalogDistribution.ReleaseTrain,
                catalogDistribution.ReleaseVersion,
                catalogDistribution.ReleaseTag,
                catalogDistribution.ManifestSha256,
                catalogDistribution.AssetName,
                catalogDistribution.AssetSha256,
                catalogDistribution.AssetLength,
                catalogDistribution.SigningKeyId
            },
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
        return Convert.ToHexStringLower(SHA256.HashData(identity));
    }

    internal static bool RequestIdentityMatches(
        string retainedSha256,
        InstallRequest request,
        Guid instanceId,
        DistributionVerificationEvidence? catalogDistribution = null)
        => string.Equals(
               retainedSha256,
               ComputeRequestSha256(request, instanceId, catalogDistribution),
               StringComparison.Ordinal) ||
           request.ReplayProfile == CameraAgentReplayProfile.InProcess && string.Equals(
               retainedSha256,
               ComputeLegacyRequestSha256(request, instanceId, catalogDistribution),
               StringComparison.Ordinal);

    internal static string ComputeLegacyRequestSha256(
        InstallRequest request,
        Guid instanceId,
        DistributionVerificationEvidence? catalogDistribution = null)
    {
        var identity = JsonSerializer.SerializeToUtf8Bytes(new
        {
            instanceId,
            request.FriendlyName,
            request.OwnerEmail,
            request.BindAddress,
            request.Port,
            request.ProductRoot,
            CatalogBundle = catalogDistribution is null ? request.CatalogBundle : null,
            CatalogRelease = catalogDistribution is null ? null : new
            {
                catalogDistribution.ReleaseTrain,
                catalogDistribution.ReleaseVersion,
                catalogDistribution.ReleaseTag,
                catalogDistribution.ManifestSha256,
                catalogDistribution.AssetName,
                catalogDistribution.AssetSha256,
                catalogDistribution.AssetLength,
                catalogDistribution.SigningKeyId
            },
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

internal sealed class InstallerException : Exception
{
    public InstallerException()
    {
    }

    public InstallerException(string message)
        : base(message)
    {
    }

    public InstallerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
