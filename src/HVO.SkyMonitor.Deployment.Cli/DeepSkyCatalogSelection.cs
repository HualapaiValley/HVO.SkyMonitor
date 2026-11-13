using HVO.SkyMonitor.Deployment.Contracts;

namespace HVO.SkyMonitor.Deployment;

/// <summary>Independent deep-sky selection using the existing deployment locks, journal and drain protocol.</summary>
internal static class DeepSkyCatalogSelection
{
    internal const string DisabledVersion = "disabled";
    internal const string Mount = "      - ${HVO_DEEP_SKY_CATALOG_ROOT:?deep-sky catalog root required}:/app/deep-sky-catalog:ro\n";
    private static readonly string[] Settings = ["Enabled", "Root", "RequiredCatalogId", "RequiredPackageKind", "RequiredPackageVersion"];

    internal static async Task<LifecycleResult> SelectAsync(
        LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest, InstallationResult result,
        ComposeFiles compose, DockerClient docker,
        Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory, string lifecycleToken, string verificationToken,
        CancellationToken cancellationToken)
    {
        if (manifest.LifecycleCondition != InstanceLifecycleCondition.Installed)
            throw new InstallerException("Deep-sky selection requires an installed instance.");
        var retained = request.Resume
            ? await CameraAgentLifecycleManager.ReadOperationAsync(paths.LifecycleStatePath, cancellationToken).ConfigureAwait(false)
            : null;
        var rollback = request.Operation == LifecycleOperationKind.CatalogRollback;
        var disable = !rollback && request.CatalogFamily == "deep-sky" && request.CatalogVersion == DisabledVersion;
        if (rollback && !manifest.HasPreviousDeepSkySelection && retained is null)
            throw new InstallerException("No previous deep-sky selection is retained.");
        var candidate = retained is { DeepSkyTransition: true } ? retained.CandidateDeepSkyCatalog : rollback
            ? manifest.PreviousDeepSkyCatalog
            : disable ? null : await CatalogLifecycleManager.ReadInstalledIdentityAsync(paths, request.CatalogVersion!, cancellationToken).ConfigureAwait(false);
        if (candidate is not null)
        {
            if (ProductionCatalog.Get(candidate.CatalogId).ManifestVersion != 4)
                throw new InstallerException("The deep-sky family accepts only a standalone OpenNGC snapshot.");
            CatalogLifecycleManager.ValidateInstalled(paths, candidate);
            CameraAgentInstaller.EnsureImageSupportsCatalog(manifest.Image, candidate.CatalogId);
        }
        else if (manifest.Image.CatalogContract != CameraAgentImageContract.IndependentCatalogContract)
            throw new InstallerException("Independent deep-sky selection requires a compatible CameraAgent image.");
        if (retained is null || !retained.MutationStarted)
            await CameraAgentLifecycleManager.ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
        if (!request.Resume && candidate == manifest.DeepSkyCatalog)
            throw new InstallerException("The requested deep-sky selection is already active.");
        var operation = await CameraAgentLifecycleManager.BeginAsync(request, paths, request.Operation!.Value, manifest, cancellationToken)
            .ConfigureAwait(false);
        return await TransitionAsync(request, paths, manifest, result, compose, docker, lifecycleClientFactory,
            ownerClientFactory, lifecycleToken, verificationToken, candidate, operation, cancellationToken).ConfigureAwait(false);
    }

    // Candidate validation belongs to SelectAsync; the state transition is independently exercised with disposable roots.
    internal static async Task<LifecycleResult> TransitionAsync(
        LifecycleRequest request, InstallationPaths paths, InstanceManifest manifest, InstallationResult result,
        ComposeFiles compose, DockerClient docker, Func<Uri, ICameraAgentLifecycleClient>? lifecycleClientFactory,
        Func<Uri, IOwnerBootstrapClient>? ownerClientFactory, string lifecycleToken, string verificationToken,
        CatalogInstallationIdentity? candidate, LifecycleOperationState operation, CancellationToken cancellationToken)
    {
        if (request.DryRun) return Result("planned", paths, manifest, operation.OperationId, candidate, request.Operation);
        operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
        {
            DeepSkyTransition = true,
            OriginalDeepSkyCatalog = operation.OriginalDeepSkyCatalog,
            CandidateDeepSkyCatalog = candidate,
            CandidateCatalog = manifest.Catalog
        }, cancellationToken).ConfigureAwait(false);
        var lifecycle = CameraAgentLifecycleManager.CreateLifecycleClient(result.Url, lifecycleClientFactory);
        var recoveryRoot = Path.Combine(paths.OperationsRoot, "lifecycle", operation.OperationId.ToString("D"));
        SafeFileSystem.CreateOwnerDirectory(Path.GetDirectoryName(recoveryRoot)!);
        SafeFileSystem.CreateOwnerDirectory(recoveryRoot);
        if (operation is { MutationStarted: true, Phase: LifecycleOperationPhase.Committed })
        {
            if (manifest.DeepSkyCatalog != candidate || result.DeepSkyCatalog != candidate || manifest.LastLifecycleOperationId != operation.OperationId)
                throw new InstallerException("Committed deep-sky selection differs from its retained operation.");
            await VerifyAsync(manifest, result, compose, docker, ownerClientFactory, verificationToken, cancellationToken).ConfigureAwait(false);
            await lifecycle.ResumeAsync(operation.OperationId, lifecycleToken, cancellationToken).ConfigureAwait(false);
            await CameraAgentLifecycleManager.CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result("completed", paths, manifest, operation.OperationId, candidate, request.Operation);
        }
        if (operation.MutationStarted)
        {
            (manifest, result) = await RestoreAsync(paths, compose, recoveryRoot, operation, docker, ownerClientFactory,
                verificationToken, cancellationToken).ConfigureAwait(false);
            await lifecycle.ResumeAsync(operation.OperationId, lifecycleToken, cancellationToken).ConfigureAwait(false);
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            { MutationStarted = false, Phase = LifecycleOperationPhase.Prepared }, cancellationToken).ConfigureAwait(false);
        }
        CatalogLifecycleManager.EnsureReplayCatalogIndependent(
            await lifecycle.ReadContinuityAsync(lifecycleToken, cancellationToken).ConfigureAwait(false), request.Operation!.Value);
        Snapshot(paths.ManifestPath, Path.Combine(recoveryRoot, "manifest.json"));
        Snapshot(paths.ResultPath, Path.Combine(recoveryRoot, "result.json"));
        Snapshot(compose.ComposeFile, Path.Combine(recoveryRoot, "compose.yml"));
        Snapshot(compose.EnvironmentFile, Path.Combine(recoveryRoot, "instance.env"));
        foreach (var setting in Settings)
        {
            var source = SettingPath(paths, setting);
            SafeFileSystem.WriteTextAtomic(Path.Combine(recoveryRoot, setting), File.Exists(source) ? "1" + Read(source) : "0");
        }
        operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
        { MutationStarted = true, Phase = LifecycleOperationPhase.Prepared }, cancellationToken).ConfigureAwait(false);
        try
        {
            var drained = await lifecycle.PauseAndDrainAsync(operation.OperationId, lifecycleToken, cancellationToken).ConfigureAwait(false);
            if (drained.ProcessingPending > 0)
                throw new InstallerException("Deep-sky selection refused while capture processing is pending; retry after processing drains.");
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            { Phase = LifecycleOperationPhase.Drained, PreMutationContinuity = CameraAgentLifecycleManager.ToBoundary(drained) }, cancellationToken).ConfigureAwait(false);
            WriteSelection(paths, compose, candidate);
            var rendered = await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["config"], cancellationToken)
                .ConfigureAwait(false);
            var modelSha256 = ComposeDeployment.ComputeSha256(rendered.StandardOutput);
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            { Phase = LifecycleOperationPhase.Mutating }, cancellationToken).ConfigureAwait(false);
            CameraAgentLifecycleManager.EnsureDaemon(manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
                ["up", "--detach", "--force-recreate", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
            var committed = manifest with
            {
                DeepSkyCatalog = candidate,
                PreviousDeepSkyCatalog = manifest.DeepSkyCatalog,
                HasPreviousDeepSkySelection = true,
                ComposeModelSha256 = modelSha256,
                PreviousImage = null,
                PreviousComposeModelSha256 = null,
                LastLifecycleOperationId = operation.OperationId,
                UpdatedUtc = DateTimeOffset.UtcNow
            };
            var updatedResult = result with { DeepSkyCatalog = candidate, ComposeModelSha256 = modelSha256 };
            await VerifyAsync(committed, updatedResult, compose, docker, ownerClientFactory, verificationToken, cancellationToken).ConfigureAwait(false);
            await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName, ["restart"], cancellationToken).ConfigureAwait(false);
            await VerifyAsync(committed, updatedResult, compose, docker, ownerClientFactory, verificationToken, cancellationToken).ConfigureAwait(false);
            var after = await lifecycle.ConfirmDrainedAsync(lifecycleToken, cancellationToken).ConfigureAwait(false);
            if (after.CaptureSequence < drained.CaptureSequence)
                throw new InstallerException("Deep-sky selection did not preserve the durable capture-sequence boundary.");
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            { Phase = LifecycleOperationPhase.CandidateVerified, PostMutationContinuity = CameraAgentLifecycleManager.ToBoundary(after) }, cancellationToken).ConfigureAwait(false);
            if (manifest.DeepSkyCatalog is { } original)
                await CatalogReferenceStore.PinHistoricalAsync(paths.WithCatalog(original.CatalogId), original, operation.OperationId, cancellationToken)
                    .ConfigureAwait(false);
            await SafeFileSystem.WriteJsonAtomicAsync(paths.ManifestPath, committed, DeploymentJsonContext.Default.InstanceManifest, cancellationToken).ConfigureAwait(false);
            await SafeFileSystem.WriteJsonAtomicAsync(paths.ResultPath, updatedResult, DeploymentJsonContext.Default.InstallationResult, cancellationToken).ConfigureAwait(false);
            operation = await CameraAgentLifecycleManager.RecordAsync(paths, operation with
            { Phase = LifecycleOperationPhase.Committed }, cancellationToken).ConfigureAwait(false);
            await lifecycle.ResumeAsync(operation.OperationId, lifecycleToken, cancellationToken).ConfigureAwait(false);
            await CameraAgentLifecycleManager.CompleteAsync(paths, operation, cancellationToken).ConfigureAwait(false);
            return Result("completed", paths, committed, operation.OperationId, candidate, request.Operation);
        }
        catch (Exception exception)
        {
            if (operation.Phase == LifecycleOperationPhase.Committed)
                throw new InstallerException("Deep-sky selection committed; rerun the same command with --resume to acknowledge final resume.", exception);
            using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try
            {
                await RestoreAsync(paths, compose, recoveryRoot, operation, docker, ownerClientFactory, verificationToken, recovery.Token).ConfigureAwait(false);
                await lifecycle.ResumeAsync(operation.OperationId, lifecycleToken, recovery.Token).ConfigureAwait(false);
                operation = operation with { MutationStarted = false, Phase = LifecycleOperationPhase.Prepared };
            }
            catch (Exception recoveryException)
            {
                throw new InstallerException("Deep-sky selection recovery is incomplete; resume the same request.", recoveryException);
            }
            await CameraAgentLifecycleManager.FailAsync(paths, operation, exception, CancellationToken.None).ConfigureAwait(false);
            throw new InstallerException("Deep-sky selection failed; the original selections were restored.", exception);
        }
    }

    internal static void WriteSelection(InstallationPaths paths, ComposeFiles compose, CatalogInstallationIdentity? candidate)
    {
        const string starMount = "      - ${HVO_CATALOG_ROOT:?catalog root required}:/app/catalog:ro\n";
        var template = Read(compose.ComposeFile).Replace(Mount, string.Empty, StringComparison.Ordinal);
        if (!template.Contains(starMount, StringComparison.Ordinal))
            throw new InstallerException("The authenticated Compose template does not have the supported catalog mount.");
        if (candidate is not null) template = template.Replace(starMount, starMount + Mount, StringComparison.Ordinal);
        var environment = string.Join('\n', Read(compose.EnvironmentFile).Split('\n')
            .Where(static line => !line.StartsWith("HVO_DEEP_SKY_CATALOG_ROOT=", StringComparison.Ordinal)));
        if (candidate is not null) environment = environment.TrimEnd('\n') + $"\nHVO_DEEP_SKY_CATALOG_ROOT={candidate.InstallRoot}\n";
        SafeFileSystem.WriteTextAtomic(compose.ComposeFile, template);
        SafeFileSystem.WriteTextAtomic(compose.EnvironmentFile, environment);
        var values = new[] { candidate is null ? "false" : "true", "/app/deep-sky-catalog", candidate?.CatalogId ?? "", "Production", candidate?.PackageVersion ?? "" };
        for (var index = 0; index < Settings.Length; index++) SafeFileSystem.WriteTextAtomic(SettingPath(paths, Settings[index]), values[index]);
    }

    private static async Task<(InstanceManifest Manifest, InstallationResult Result)> RestoreAsync(
        InstallationPaths paths, ComposeFiles compose, string root, LifecycleOperationState operation, DockerClient docker,
        Func<Uri, IOwnerBootstrapClient>? ownerFactory, string verificationToken, CancellationToken cancellationToken)
    {
        var original = await CameraAgentLifecycleManager.ReadRecoverySnapshotAsync(paths, operation,
            Path.Combine(root, "manifest.json"), Path.Combine(root, "result.json"), cancellationToken).ConfigureAwait(false);
        var staged = compose with { ComposeFile = Path.Combine(root, "compose.yml"), EnvironmentFile = Path.Combine(root, "instance.env") };
        await CameraAgentLifecycleManager.ValidateComposeAuthorityAsync(docker, staged, original.Manifest, cancellationToken).ConfigureAwait(false);
        Snapshot(staged.ComposeFile, compose.ComposeFile);
        Snapshot(staged.EnvironmentFile, compose.EnvironmentFile);
        foreach (var setting in Settings)
        {
            var saved = Read(Path.Combine(root, setting));
            if (saved.StartsWith('1')) SafeFileSystem.WriteTextAtomic(SettingPath(paths, setting), saved[1..]);
            else if (saved == "0") File.Delete(SettingPath(paths, setting));
            else throw new InstallerException("Deep-sky recovery settings are unsupported.");
        }
        await CameraAgentLifecycleManager.WriteRecoverySnapshotAsync(paths, original.Manifest, original.Result, cancellationToken).ConfigureAwait(false);
        CameraAgentLifecycleManager.EnsureDaemon(original.Manifest.DockerDaemon, await docker.PreflightAsync(cancellationToken).ConfigureAwait(false));
        await docker.ComposeAsync(compose.ComposeFile, compose.EnvironmentFile, compose.ProjectName,
            ["up", "--detach", "--force-recreate", "--remove-orphans"], cancellationToken).ConfigureAwait(false);
        await VerifyAsync(original.Manifest, original.Result, compose, docker, ownerFactory, verificationToken, cancellationToken).ConfigureAwait(false);
        return original;
    }

    private static async Task VerifyAsync(InstanceManifest manifest, InstallationResult result, ComposeFiles compose,
        DockerClient docker, Func<Uri, IOwnerBootstrapClient>? ownerFactory, string verificationToken, CancellationToken cancellationToken)
    {
        await CameraAgentLifecycleManager.ValidateComposeAuthorityAsync(docker, compose, manifest, cancellationToken).ConfigureAwait(false);
        var owner = ownerFactory?.Invoke(result.Url) ?? new OwnerBootstrapClient(result.Url);
        await owner.WaitForHealthAsync(ProductionCatalog.Get(manifest.Catalog.CatalogId), cancellationToken).ConfigureAwait(false);
        await docker.VerifyContainerAsync(compose, InstallationPaths.Create(manifest.ProductRoot, manifest.InstanceId, manifest.Catalog.CatalogId),
            manifest.Image, manifest.RuntimeUid, manifest.RuntimeGid, cancellationToken, manifest.DeepSkyCatalog?.InstallRoot).ConfigureAwait(false);
        await owner.VerifyInstallationAsync(verificationToken, new(
            manifest.ApplicationIdentity.ToString("D"), manifest.OwnerEmail, result.OwnerBootstrapState,
            manifest.ConfigurationSha256, manifest.RigProfileSha256, manifest.ScheduleSha256,
            manifest.DeploymentLocationId, manifest.DeploymentLocationVersion, manifest.DeploymentLocationSha256,
            manifest.ReplayProfile, manifest.Catalog, AllowCompletedPasswordReplacement: true,
            DeepSkyCatalog: manifest.DeepSkyCatalog), cancellationToken).ConfigureAwait(false);
    }

    private static string SettingPath(InstallationPaths paths, string setting) => Path.Combine(paths.ConfigRoot, "secrets", "DeepSkyCatalog__" + setting);
    private static string Read(string path)
    {
        using var source = SafeFileSystem.OpenOwnerFileRead(path);
        using var reader = new StreamReader(source);
        return reader.ReadToEnd();
    }
    private static void Snapshot(string source, string destination) => SafeFileSystem.WriteTextAtomic(destination, Read(source));

    private static LifecycleResult Result(string outcome, InstallationPaths paths, InstanceManifest manifest, Guid operationId,
        CatalogInstallationIdentity? candidate, LifecycleOperationKind? kind) => new(
            DeploymentSchemaVersions.LifecycleOperation, kind, outcome, operationId, manifest.InstanceId,
            manifest.LifecycleCondition, paths.ProductRoot, paths.InstanceRoot, manifest.Image, manifest.PreviousImage,
            manifest.Catalog, manifest.PreviousCatalog, manifest.DockerDaemon, true, true, [paths.CatalogRoot, paths.OperationsRoot], null, DateTimeOffset.UtcNow)
        { DeepSkyCatalog = candidate };
}
